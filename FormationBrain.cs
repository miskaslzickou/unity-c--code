using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Squad formation system. Leader holds this component; call AddMember() and slots are
/// auto-assigned (a FormationFollower is attached to each member). 2D and 3D.
/// Genres: RTS squads, escort missions, shmup option drones, RPG parties, creep columns.
/// </summary>
public class FormationBrain : MonoBehaviour
{
    public enum Pattern { Line, Column, Wedge, Circle, Grid, ArrowHead }

    [Header("Formation")]
    public Pattern pattern = Pattern.Wedge;
    public float spacing = 2f;
    [Tooltip("Follow lag: lower = tighter formation.")]
    public float followerSmoothTime = 0.35f;

    [Header("Rotation")]
    public bool faceMovementDirection = true;

    [Header("Debug")]
    public bool drawGizmos = true;

    readonly List<GameObject> members = new List<GameObject>();
    readonly Dictionary<GameObject, Vector3> slotOffsets = new Dictionary<GameObject, Vector3>();
    Vector3 lastLeaderPos;
    public Vector3 LeaderVelocity { get; private set; }

    public Quaternion FormationRotation => faceMovementDirection
        ? Quaternion.LookRotation(GetPlanarDirection(LeaderVelocity))
        : transform.rotation;

    void Update()
    {
        LeaderVelocity = (transform.position - lastLeaderPos) / Mathf.Max(Time.deltaTime, 0.0001f);
        lastLeaderPos = transform.position;
    }

    public void AddMember(GameObject member)
    {
        if (member == null || members.Contains(member)) return;
        members.Add(member);
        Redistribute();
    }

    public void RemoveMember(GameObject member)
    {
        if (members.Remove(member)) Redistribute();
    }

    public IReadOnlyList<GameObject> Members => members;

    /// <summary>Recompute slot offsets and push them to followers.</summary>
    public void Redistribute()
    {
        slotOffsets.Clear();
        int n = members.Count;
        for (int i = 0; i < n; i++) slotOffsets[members[i]] = ComputeOffset(i, n);

        for (int i = 0; i < n; i++)
        {
            var f = members[i].GetComponent<FormationFollower>();
            if (f == null) f = members[i].AddComponent<FormationFollower>();
            f.Join(this, slotOffsets[members[i]]);
        }
    }

    Vector3 ComputeOffset(int index, int count)
    {
        switch (pattern)
        {
            case Pattern.Line:
                return new Vector3((index - (count - 1) * 0.5f) * spacing, 0f, 0f);

            case Pattern.Column:
                return new Vector3(0f, 0f, -index * spacing);

            case Pattern.Wedge:
            {
                if (index == 0) return Vector3.zero;
                int side = (index % 2 == 1) ? -1 : 1;
                int rank = (index + 1) / 2;
                return new Vector3(side * rank * spacing, 0f, -rank * spacing * 0.75f);
            }

            case Pattern.Circle:
            {
                float a = index * Mathf.PI * 2f / Mathf.Max(count, 1);
                return new Vector3(Mathf.Cos(a) * spacing, 0f, Mathf.Sin(a) * spacing);
            }

            case Pattern.Grid:
            {
                int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
                int r = index / cols, c = index % cols;
                return new Vector3((c - (cols - 1) * 0.5f) * spacing, 0f, -r * spacing);
            }

            case Pattern.ArrowHead:
            default:
            {
                if (index == 0) return Vector3.zero;
                int rank = Mathf.CeilToInt((Mathf.Sqrt(8 * index + 1) - 1) * 0.5f);
                int inRankStart = rank * (rank - 1) / 2 + 1;
                int posInRank = index - inRankStart;
                return new Vector3((posInRank - (rank - 1) * 0.5f) * spacing, 0f, -rank * spacing * 0.7f);
            }
        }
    }

    /// <summary>World-space target for a member slot (leader frame, rotated toward movement).</summary>
    public Vector3 GetSlotWorldPosition(GameObject member)
    {
        return slotOffsets.TryGetValue(member, out var off)
            ? transform.position + FormationRotation * off
            : transform.position;
    }

    static Vector3 GetPlanarDirection(Vector3 v)
    {
        var p = new Vector3(v.x, 0f, v.z);
        return p.sqrMagnitude > 0.01f ? p.normalized : Vector3.forward;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        Gizmos.color = Color.green;
        int n = Mathf.Max(members.Count, 3);
        for (int i = 0; i < n; i++)
            Gizmos.DrawWireSphere(transform.position + FormationRotation * ComputeOffset(i, n), 0.3f);
    }
}

/// <summary>Attached automatically by FormationBrain, or add manually and call Join().</summary>
public class FormationFollower : MonoBehaviour
{
    public FormationBrain formation;
    public Vector3 slotOffset;

    Rigidbody rb3d;
    Rigidbody2D rb2d;
    Vector3 vel3d;
    Vector2 vel2d;

    public void Join(FormationBrain brain, Vector3 offset)
    {
        formation = brain;
        slotOffset = offset;
        rb3d = GetComponent<Rigidbody>();
        rb2d = GetComponent<Rigidbody2D>();
    }

    void FixedUpdate()
    {
        if (formation == null) return;
        Vector3 target = formation.GetSlotWorldPosition(gameObject);

        if (rb2d != null && rb3d == null)
        {
            Vector2 newPos = Vector2.SmoothDamp(transform.position, (Vector2)target, ref vel2d, formation.followerSmoothTime);
            rb2d.linearVelocity = (newPos - (Vector2)transform.position) / Time.fixedDeltaTime;
        }
        else if (rb3d != null)
        {
            Vector3 newPos = Vector3.SmoothDamp(transform.position, target, ref vel3d, formation.followerSmoothTime);
            rb3d.linearVelocity = (newPos - transform.position) / Time.fixedDeltaTime;
        }
        else
        {
            transform.position = Vector3.SmoothDamp(transform.position, target, ref vel3d, formation.followerSmoothTime);
        }
    }
}
