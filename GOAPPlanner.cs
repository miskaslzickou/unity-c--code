using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Goal-Oriented Action Planning (GOAP) - the AI technique from F.E.A.R. / The Sims.
/// The agent declares GOALS (desired world states) and ACTIONS (preconditions + effects + cost).
/// The planner runs A* over world-state space to synthesize a new action sequence each time
/// the world changes, so behavior emerges instead of being hand-scripted.
/// Works in any genre (2D/3D): survival NPCs, RPG enemies, quest planners, robot crafters.
/// </summary>
public class GOAPWorldState
{
    readonly Dictionary<string, object> values = new Dictionary<string, object>();

    public object Get(string key) => values.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => values.ContainsKey(key);

    public void Set(string key, object v)
    {
        if (v == null) values.Remove(key);
        else values[key] = v;
    }

    /// <summary>True if every pair in 'conditions' matches this state.</summary>
    public bool Matches(Dictionary<string, object> conditions)
    {
        foreach (var kv in conditions)
            if (!TryMatch(Get(kv.Key), kv.Value)) return false;
        return true;
    }

    static bool TryMatch(object a, object b)
    {
        if (a == null) return b == null || Equals(b, false); // missing key ~= false
        if (a is bool ab && b is bool bb) return ab == bb;
        if (a is float af && b is float bf) return Mathf.Abs(af - bf) < 0.0001f;
        if (a is int ai && b is int bi) return ai == bi;
        return Equals(a, b);
    }

    public GOAPWorldState Clone()
    {
        var c = new GOAPWorldState();
        foreach (var kv in values) c.values[kv.Key] = kv.Value;
        return c;
    }

    public Dictionary<string, object> AllPairs()
    {
        var d = new Dictionary<string, object>();
        foreach (var kv in values) d[kv.Key] = kv.Value;
        return d;
    }

    public override string ToString() => string.Join(", ", values);
}

[Serializable]
public abstract class GOAPGoal
{
    public string goalName;
    public readonly Dictionary<string, object> desired = new Dictionary<string, object>();
    public abstract float Priority(GOAPAgent agent); // dynamic priority
}

[Serializable]
public abstract class GOAPAction
{
    public string actionName;
    public float cost = 1f;

    public readonly Dictionary<string, object> preconditions = new Dictionary<string, object>();
    public readonly Dictionary<string, object> effects = new Dictionary<string, object>();

    [NonSerialized] public GameObject target;

    /// <summary>Runtime validity check (is there food in range? etc.).</summary>
    public virtual bool CheckProceduralPrecondition(GOAPAgent agent) => true;

    /// <summary>Execute one tick. Return Running / Success / Failure.</summary>
    public abstract GOAPAgent.ActionStatus Perform(GOAPAgent agent);

    public virtual void ResetAction()
    {
        target = null;
        elapsed = 0f;
    }

    [NonSerialized] protected float elapsed;
    /// <summary>Helper: run for 'seconds' then report Success.</summary>
    protected bool RunFor(float seconds)
    {
        elapsed += Time.deltaTime;
        return elapsed >= seconds;
    }
}

/// <summary>Planner + agent. Attach, assign goals & actions, it plans and executes.</summary>
public class GOAPAgent : MonoBehaviour
{
    public enum ActionStatus { Running, Success, Failure }

    [Header("Knowledge")]
    public GOAPWorldState world = new GOAPWorldState();

    [Header("Goals & Actions (assigned in code or subclasses)")]
    public List<GOAPGoal> goals = new List<GOAPGoal>();
    public List<GOAPAction> actions = new List<GOAPAction>();

    [Header("Planning")]
    public float replanInterval = 0.5f;
    public int maxPlanDepth = 8;
    public int maxNodesExplored = 300;

    [Header("Events")]
    public event Action<List<GOAPAction>> OnPlanCreated;
    public event Action<GOAPAction> OnActionStarted;
    public event Action<GOAPAction> OnActionFailed;

    public GOAPAction CurrentAction { get; private set; }
    public IReadOnlyList<GOAPAction> Plan => currentPlan;
    public int PlanIndex { get; private set; }

    readonly List<GOAPAction> currentPlan = new List<GOAPAction>();
    float nextReplan;
    bool planDirty;

    public void MarkWorldChanged() => planDirty = true;

    protected virtual void Update()
    {
        if (Time.time >= nextReplan)
        {
            nextReplan = Time.time + replanInterval;
            if (currentPlan.Count == 0 || CurrentAction == null || planDirty) Replan();
        }

        if (CurrentAction == null) return;

        var status = CurrentAction.Perform(this);
        if (status == ActionStatus.Success)
        {
            foreach (var e in CurrentAction.effects) world.Set(e.Key, e.Value);
            CurrentAction.ResetAction();
            CurrentAction = null;
            PlanIndex++;
            if (PlanIndex < currentPlan.Count)
            {
                CurrentAction = currentPlan[PlanIndex];
                OnActionStarted?.Invoke(CurrentAction);
            }
            else currentPlan.Clear();
        }
        else if (status == ActionStatus.Failure)
        {
            OnActionFailed?.Invoke(CurrentAction);
            CurrentAction.ResetAction();
            CurrentAction = null;
            currentPlan.Clear();
            Replan();
        }
    }

    public void Replan()
    {
        planDirty = false;
        currentPlan.Clear();
        PlanIndex = 0;
        CurrentAction = null;

        var sorted = new List<GOAPGoal>(goals);
        sorted.Sort((a, b) => b.Priority(this).CompareTo(a.Priority(this)));

        foreach (var g in sorted)
        {
            if (world.Matches(g.desired)) continue; // already satisfied
            var plan = PlanActions(g);
            if (plan == null) continue;
            currentPlan.AddRange(plan);
            OnPlanCreated?.Invoke(plan);
            PlanIndex = 0;
            CurrentAction = currentPlan[0];
            OnActionStarted?.Invoke(CurrentAction);
            return;
        }
    }

    // ---------------- A* PLANNER ----------------

    class Node : IComparable<Node>
    {
        public GOAPWorldState state;
        public float g, h;
        public Node parent;
        public GOAPAction causedBy;
        public int depth;
        public float F => g + h;
        public int CompareTo(Node o) => F.CompareTo(o.F);
    }

    List<GOAPAction> PlanActions(GOAPGoal goal)
    {
        int explored = 0;
        var open = new List<Node>();
        var closed = new List<GOAPWorldState>();

        var start = new Node { state = world.Clone(), g = 0, depth = 0 };
        start.h = CountUnsat(start.state, goal);
        open.Add(start);

        while (open.Count > 0 && explored < maxNodesExplored)
        {
            open.Sort((a, b) => a.F.CompareTo(b.F));
            var cur = open[0];
            open.RemoveAt(0);
            explored++;

            if (cur.state.Matches(goal.desired)) return Reconstruct(cur);

            foreach (var action in actions)
            {
                if (!action.CheckProceduralPrecondition(this)) continue;
                if (!cur.state.Matches(action.preconditions)) continue;
                if (cur.depth + 1 > maxPlanDepth) continue;

                var next = cur.state.Clone();
                foreach (var e in action.effects) next.Set(e.Key, e.Value);

                bool dup = false;
                for (int i = 0; i < closed.Count; i++)
                    if (closed[i].Matches(next.AllPairs())) { dup = true; break; }
                if (dup) continue;
                closed.Add(cur.state);

                open.Add(new Node
                {
                    state = next,
                    parent = cur,
                    causedBy = action,
                    g = cur.g + action.cost,
                    h = CountUnsat(next, goal),
                    depth = cur.depth + 1
                });
            }
        }
        return null;
    }

    static List<GOAPAction> Reconstruct(Node end)
    {
        var list = new List<GOAPAction>();
        var n = end;
        while (n.causedBy != null) { list.Insert(0, n.causedBy); n = n.parent; }
        return list;
    }

    static float CountUnsat(GOAPWorldState s, GOAPGoal g)
    {
        float n = 0;
        foreach (var kv in g.desired)
        {
            var v = s.Get(kv.Key);
            bool ok = (v is bool b1 && kv.Value is bool b2 && b1 == b2) || (v != null && Equals(v, kv.Value));
            if (!ok) n++;
        }
        return n;
    }
}

/// <summary>
/// EXAMPLE AGENT - a survival NPC that gets hungry and tired, then plans to eat / sleep.
/// Copy as a template for your own goals/actions.
/// </summary>
public class GOAPSurvivalExample : GOAPAgent
{
    public Transform foodSource;
    public Transform bed;
    [Range(0f, 100f)] public float hunger = 100f;
    [Range(0f, 100f)] public float energy = 100f;

    class Goal : GOAPGoal
    {
        readonly Func<float> prio;
        public Goal(string name, Func<float> priority, string key, object value)
        {
            goalName = name; prio = priority; desired[key] = value;
        }
        public override float Priority(GOAPAgent a) => prio();
    }

    class EatAction : GOAPAction
    {
        public Transform food;
        public EatAction() { actionName = "Eat"; cost = 2f; preconditions["hungry"] = true; effects["hungry"] = false; }
        public override bool CheckProceduralPrecondition(GOAPAgent a) => food != null;
        public override ActionStatus Perform(GOAPAgent a) => RunFor(2f) ? ActionStatus.Success : ActionStatus.Running;
    }

    class SleepAction : GOAPAction
    {
        public Transform bed;
        public SleepAction() { actionName = "Sleep"; cost = 3f; preconditions["rested"] = false; effects["rested"] = true; }
        public override bool CheckProceduralPrecondition(GOAPAgent a) => bed != null;
        public override ActionStatus Perform(GOAPAgent a) => RunFor(5f) ? ActionStatus.Success : ActionStatus.Running;
    }

    void Start()
    {
        world.Set("hungry", false);
        world.Set("rested", true);

        goals.Add(new Goal("BeFull", () => (bool)world.Get("hungry") ? 10f : 0f, "hungry", false));
        goals.Add(new Goal("BeRested", () => (bool)world.Get("rested") ? 0f : 5f, "rested", true));

        actions.Add(new EatAction { food = foodSource });
        actions.Add(new SleepAction { bed = bed });
    }

    protected override void Update()
    {
        base.Update();
        hunger += 1f * Time.deltaTime;
        energy -= 1.5f * Time.deltaTime;

        if (hunger >= 100f && !(bool)world.Get("hungry")) { world.Set("hungry", true); MarkWorldChanged(); }
        if (energy <= 0f && (bool)world.Get("rested")) { world.Set("rested", false); MarkWorldChanged(); }

        if (CurrentAction != null && CurrentAction.actionName == "Eat") hunger = 0f;
        if (CurrentAction != null && CurrentAction.actionName == "Sleep") energy = 100f;
    }
}
