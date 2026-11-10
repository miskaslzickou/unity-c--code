using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight Behavior Tree framework. Works for any genre (2D/3D).
/// Build trees in code with the fluent BT helpers:
///   var tree = BT.Sel(
///       BT.Seq(BT.Cond("EnemyVisible", () => enemyVisible),
///              BT.Act("Attack", AttackTick)),
///       BT.Act("Patrol", PatrolTick));
///   gameObject.RunTree(tree);   // ticks every Update automatically
/// </summary>
public abstract class BTNode
{
    public enum Status { Success, Failure, Running }

    public string Name { get; protected set; }
    public Status LastStatus { get; protected set; }

    public abstract Status Tick();
    public virtual void Reset() { }
}

public class BTAction : BTNode
{
    readonly Func<Status> fn;
    public BTAction(string name, Func<Status> fn) { Name = name; this.fn = fn; }
    public override Status Tick() => LastStatus = fn();
}

public class BTCondition : BTNode
{
    readonly Func<bool> fn;
    public BTCondition(string name, Func<bool> fn) { Name = name; this.fn = fn; }
    public override Status Tick() => LastStatus = fn() ? Status.Success : Status.Failure;
}

public abstract class BTComposite : BTNode
{
    public readonly List<BTNode> Children = new List<BTNode>();
    protected int runningIndex;

    public override void Reset()
    {
        runningIndex = 0;
        for (int i = 0; i < Children.Count; i++) Children[i].Reset();
    }
}

/// <summary>Runs children in order; fails fast. Succeeds when all children succeed.</summary>
public class BTSequence : BTComposite
{
    public BTSequence(string name, params BTNode[] children) { Name = name; Children.AddRange(children); }

    public override Status Tick()
    {
        for (int i = runningIndex; i < Children.Count; i++)
        {
            var s = Children[i].Tick();
            LastStatus = s;
            if (s == Status.Failure) { Reset(); return Status.Failure; }
            if (s == Status.Running) { runningIndex = i; return Status.Running; }
        }
        Reset();
        return Status.Success;
    }
}

/// <summary>Runs children in order; succeeds fast. Fails when all children fail.</summary>
public class BTSelector : BTComposite
{
    public BTSelector(string name, params BTNode[] children) { Name = name; Children.AddRange(children); }

    public override Status Tick()
    {
        for (int i = runningIndex; i < Children.Count; i++)
        {
            var s = Children[i].Tick();
            LastStatus = s;
            if (s == Status.Success) { Reset(); return Status.Success; }
            if (s == Status.Running) { runningIndex = i; return Status.Running; }
        }
        Reset();
        return Status.Failure;
    }
}

/// <summary>Inverts its single child.</summary>
public class BTInverter : BTNode
{
    readonly BTNode child;
    public BTInverter(BTNode child) { Name = "Inverter(" + child.Name + ")"; this.child = child; }
    public override Status Tick()
    {
        var s = child.Tick();
        LastStatus = s == Status.Running ? Status.Running : (s == Status.Success ? Status.Failure : Status.Success);
        return LastStatus;
    }
}

/// <summary>Child cannot run again until cooldown elapses after its last completion.</summary>
public class BTCooldown : BTNode
{
    readonly BTNode child;
    readonly float cooldown;
    float readyAt;

    public BTCooldown(float seconds, BTNode child) { Name = "Cooldown(" + child.Name + ")"; this.child = child; cooldown = seconds; }

    public override Status Tick()
    {
        if (Time.time < readyAt) return LastStatus = Status.Failure;
        var s = child.Tick();
        if (s != Status.Running) readyAt = Time.time + cooldown;
        return LastStatus = s;
    }
}

/// <summary>Component wrapper: ticks the tree every frame (or on an interval).</summary>
public class BehaviorTreeRunner : MonoBehaviour
{
    [HideInInspector] public BTNode root;
    [Tooltip("How often (seconds) to re-evaluate. 0 = every frame.")]
    public float tickInterval = 0f;

    float nextTick;

    void Update()
    {
        if (root == null || Time.time < nextTick) return;
        nextTick = Time.time + tickInterval;
        root.Tick();
    }
}

/// <summary>Compact static helpers for building trees.</summary>
public static class BT
{
    public static BTAction Act(string name, Func<BTNode.Status> fn) => new BTAction(name, fn);
    public static BTCondition Cond(string name, Func<bool> fn) => new BTCondition(name, fn);
    public static BTSequence Seq(params BTNode[] c) => new BTSequence("Seq", c);
    public static BTSelector Sel(params BTNode[] c) => new BTSelector("Sel", c);
    public static BTInverter Not(BTNode c) => new BTInverter(c);
    public static BTCooldown Every(float sec, BTNode c) => new BTCooldown(sec, c);

    public static BTNode.Status S() => BTNode.Status.Success;
    public static BTNode.Status F() => BTNode.Status.Failure;
    public static BTNode.Status R() => BTNode.Status.Running;

    public static BehaviorTreeRunner RunTree(this GameObject host, BTNode root)
    {
        var runner = host.AddComponent<BehaviorTreeRunner>();
        runner.root = root;
        return runner;
    }
}
