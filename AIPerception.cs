using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// AI perception & memory: sight cone (3D raycast / 2D linecast), hearing, and short-term
/// memory of last-known positions per target. Genre-agnostic: stealth guards, horror stalkers,
/// hunting animals, tower-defense scanners, sports opponents.
/// </summary>
public class AIPerception : MonoBehaviour
{
    public class Memory
    {
        public Transform target;
        public Vector3 lastKnownPosition;
        public float lastSeenTime;
        public float awareness;      // 0..1 builds while visible, decays when not
        public bool IsAlert => awareness >= 1f;
    }

    [Header("Sight")]
    public bool useSight = true;
    public float viewDistance = 12f;
    [Range(0f, 360f)] public float viewAngle = 90f;
    public float eyeHeight = 1f;
    public LayerMask targetMask = ~0;
    public LayerMask obstructionMask = ~0;

    [Header("Awareness Build-up (stealth style)")]
    public float awarenessRisePerSecond = 2.5f;
    public float awarenessDecayPerSecond = 0.5f;
    public bool instantAlert = false;

    [Header("Hearing")]
    public bool useHearing = true;
    public float hearingRadius = 6f;

    [Header("Memory")]
    public float memoryDuration = 5f;

    [Header("Events")]
    public event System.Action<Transform> OnTargetSpotted;
    public event System.Action<Vector3> OnHeardNoise;
    public event System.Action<Transform> OnTargetLost;

    [Header("Scanning")]
    public float scanInterval = 0.2f;

    public bool IsAlert { get; private set; }
    public Transform PrimaryTarget { get; private set; }

    readonly Dictionary<Transform, Memory> memories = new Dictionary<Transform, Memory>();
    readonly Collider[] hits3d = new Collider[16];
    readonly Collider2D[] hits2d = new Collider2D[16];
    float nextScan;
    bool is2D;

    void Awake()
    {
        is2D = GetComponent<Collider2D>() != null || GetComponent<Rigidbody2D>() != null;
    }

    void Update()
    {
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + scanInterval;
            Scan();
        }
        DecayAwareness();
    }

    void Scan()
    {
        bool anyAlert = false;

        if (useSight)
        {
            if (is2D)
            {
                int n = Physics2D.OverlapCircleNonAlloc(transform.position, viewDistance, hits2d, targetMask);
                for (int i = 0; i < n; i++) Evaluate(hits2d[i].transform);
            }
            else
            {
                int n = Physics.OverlapSphereNonAlloc(transform.position, viewDistance, hits3d, targetMask);
                for (int i = 0; i < n; i++)
                {
                    var t = hits3d[i].attachedRigidbody != null ? hits3d[i].attachedRigidbody.transform : hits3d[i].transform;
                    Evaluate(t);
                }
            }
        }

        foreach (var m in memories)
            if (m.Value.IsAlert) { anyAlert = true; if (PrimaryTarget == null) PrimaryTarget = m.Key; }

        if (!anyAlert && PrimaryTarget != null)
        {
            OnTargetLost?.Invoke(PrimaryTarget);
            PrimaryTarget = null;
        }
        IsAlert = anyAlert;
    }

    void Evaluate(Transform t)
    {
        if (t == transform || t.IsChildOf(transform)) return;

        Vector3 eye = transform.position + (is2D ? Vector3.zero : Vector3.up * eyeHeight);
        Vector3 toT = t.position - transform.position;

        Vector2 selfFwd = is2D ? (Vector2)transform.up : new Vector2(transform.forward.x, transform.forward.z);
        Vector2 flat = is2D ? new Vector2(toT.x, toT.y) : new Vector2(toT.x, toT.z);
        if (Vector2.Angle(selfFwd, flat) > viewAngle * 0.5f) return;
        if (toT.magnitude > viewDistance) return;

        if (is2D)
        {
            var hit = Physics2D.Linecast(eye, t.position, obstructionMask & ~targetMask);
            if (hit.collider != null && !hit.collider.transform.IsChildOf(t)) return;
        }
        else
        {
            if (Physics.Linecast(eye, t.position, obstructionMask & ~targetMask, out RaycastHit block)
                && !block.transform.IsChildOf(t)) return;
        }

        if (!memories.TryGetValue(t, out var mem))
        {
            mem = new Memory { target = t };
            memories.Add(t, mem);
        }
        mem.lastKnownPosition = t.position;
        mem.lastSeenTime = Time.time;

        float before = mem.awareness;
        mem.awareness = instantAlert ? 1f : Mathf.Min(1f, mem.awareness + awarenessRisePerSecond * scanInterval);
        if (before < 1f && mem.awareness >= 1f) OnTargetSpotted?.Invoke(t);
    }

    void DecayAwareness()
    {
        float decay = awarenessDecayPerSecond * Time.deltaTime;
        List<Transform> forget = null;
        foreach (var kv in memories)
        {
            kv.Value.awareness = Mathf.Max(0f, kv.Value.awareness - decay);
            if (Time.time - kv.Value.lastSeenTime > memoryDuration && kv.Value.awareness <= 0f)
                (forget ??= new List<Transform>()).Add(kv.Key);
        }
        if (forget != null)
            foreach (var t in forget) memories.Remove(t);
    }

    /// <summary>Call from footsteps, gunshots, explosions, etc.</summary>
    public void HearNoise(Vector3 position, float noiseRadius)
    {
        if (!useHearing) return;
        if (Vector3.Distance(transform.position, position) <= Mathf.Max(hearingRadius, noiseRadius))
            OnHeardNoise?.Invoke(position);
    }

    /// <summary>Nearest remembered last-known position (search point after losing a target).</summary>
    public bool TryGetSearchPoint(out Vector3 point)
    {
        float best = float.MaxValue;
        point = default;
        bool found = false;
        foreach (var m in memories.Values)
        {
            float d = (m.lastKnownPosition - transform.position).sqrMagnitude;
            if (d < best) { best = d; point = m.lastKnownPosition; found = true; }
        }
        return found;
    }

    public Memory GetMemory(Transform t) => memories.TryGetValue(t, out var m) ? m : null;

    void OnDrawGizmosSelected()
    {
        Gizmos.color = IsAlert ? Color.red : Color.yellow;
        Vector3 fwd = transform.forward.sqrMagnitude < 0.001f ? Vector3.forward : transform.forward;
        if (is2D) fwd = transform.up;
        Vector3 planar = new Vector3(fwd.x, 0f, fwd.z);
        if (planar.sqrMagnitude < 0.001f) planar = Vector3.forward;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, Quaternion.LookRotation(planar.normalized), Vector3.one);
        Gizmos.DrawFrustum(Vector3.zero, viewAngle, viewDistance, 0.1f, 1f);
        Gizmos.matrix = Matrix4x4.identity;

        if (useHearing)
        {
            Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, hearingRadius);
        }
    }
}
