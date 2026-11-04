using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Utility AI brain: scores candidate actions each interval and picks the best.
/// Genre-agnostic: combat AI, Sims-like needs, companions, shopkeepers, tower-defense targeting.
/// Hook behavior via OnActionChosen / OnActionAborted, or poll CurrentAction.
/// </summary>
public class UtilityBrain : MonoBehaviour
{
    [Serializable]
    public class Consideration
    {
        public enum InputSource
        {
            HealthFraction,      // 0..1 (HP component or supplied getter)
            DistanceToTarget,    // meters, normalized between minInput/maxInput
            LineOfSightToTarget, // 1 if visible, 0 if not
            CustomFloat,         // blackboard value by floatParameterName
            CustomGetter         // UtilityBrain.customInput delegate
        }

        public string name;
        public AnimationCurve response = AnimationCurve.Linear(0, 0, 1, 1);
        public InputSource source;
        public Transform target;
        public string floatParameterName;
        public float minInput = 0f, maxInput = 10f;
        [Range(0f, 1f)] public float weight = 1f;

        public float Score(UtilityBrain brain)
        {
            float raw;
            switch (source)
            {
                case InputSource.HealthFraction: raw = brain.GetHealthFraction(); break;
                case InputSource.DistanceToTarget:
                    if (target == null) return 0f;
                    raw = Vector3.Distance(brain.transform.position, target.position); break;
                case InputSource.LineOfSightToTarget:
                    if (target == null) return 0f;
                    raw = brain.HasLineOfSight(target) ? 1f : 0f; break;
                case InputSource.CustomFloat: raw = brain.GetFloat(floatParameterName); break;
                case InputSource.CustomGetter: raw = brain.customInput != null ? brain.customInput(name) : 0f; break;
                default: raw = 0f; break;
            }
            float t = Mathf.InverseLerp(minInput, maxInput, raw);
            return Mathf.Clamp01(response.Evaluate(t)) * weight;
        }
    }

    [Serializable]
    public class UtilityAction
    {
        public string actionName;
        public List<Consideration> considerations = new List<Consideration>();
        [Tooltip("Flat bonus added after scoring (intrinsic preference).")]
        public float bias;
        [Tooltip("While active, this action wins ties unless beaten by this margin.")]
        public float commitmentBonus = 0.05f;
        [NonSerialized] public float lastScore;
    }

    [Header("Actions")]
    public List<UtilityAction> actions = new List<UtilityAction>();

    [Header("Timing")]
    public float decisionInterval = 0.25f;

    [Header("Scoring")]
    [Tooltip("Geometric mean (many low scores kill an action) vs average.")]
    public bool useGeometricMean = true;

    [Header("Events")]
    public event Action<UtilityAction> OnActionChosen;
    public event Action<UtilityAction> OnActionAborted;

    public Func<string, float> customInput;

    readonly Dictionary<string, float> blackboard = new Dictionary<string, float>();

    public string CurrentAction { get; private set; }
    UtilityAction current;
    float nextDecision;

    void Update()
    {
        if (Time.time < nextDecision) return;
        nextDecision = Time.time + decisionInterval;
        Decide();
    }

    void Decide()
    {
        UtilityAction best = null;
        float bestScore = float.MinValue;

        foreach (var a in actions)
        {
            float score = Score(a);
            if (a == current) score += a.commitmentBonus;
            a.lastScore = score;
            if (score > bestScore) { bestScore = score; best = a; }
        }

        if (best == null || best == current) return;
        if (current != null) OnActionAborted?.Invoke(current);
        current = best;
        CurrentAction = best.actionName;
        OnActionChosen?.Invoke(best);
    }

    public float Score(UtilityAction a)
    {
        if (a.considerations.Count == 0) return a.bias;

        float result;
        if (useGeometricMean)
        {
            result = 1f;
            foreach (var c in a.considerations) result *= Mathf.Max(0.0001f, c.Score(this));
            result = Mathf.Pow(result, 1f / a.considerations.Count);
        }
        else
        {
            result = 0f;
            foreach (var c in a.considerations) result += c.Score(this);
            result /= a.considerations.Count;
        }
        return result + a.bias;
    }

    float GetHealthFraction()
    {
        var hp = GetComponent<HP>();
        if (hp != null)
        {
            var maxF = hp.GetType().GetField("maxHealth");
            var maxP = hp.GetType().GetProperty("maxHealth");
            float max = 100f;
            if (maxF != null) max = Convert.ToSingle(maxF.GetValue(hp));
            else if (maxP != null) max = Convert.ToSingle(maxP.GetValue(hp));
            return Mathf.Clamp01(hp.health / Mathf.Max(1f, max));
        }
        return 1f;
    }

    bool HasLineOfSight(Transform t)
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        Vector3 dir = t.position - origin;
        if (Physics.Raycast(origin, dir.normalized, out RaycastHit hit, dir.magnitude))
            return hit.transform == t || t.IsChildOf(hit.transform);
        return true;
    }

    public void SetFloat(string key, float v) => blackboard[key] = v;
    public float GetFloat(string key) => blackboard.TryGetValue(key, out var v) ? v : 0f;

    public void ForceReconsider() => nextDecision = 0f;
}
