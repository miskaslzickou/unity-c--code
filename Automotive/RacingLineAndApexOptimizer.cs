using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Roads;
using GameSystems.Persistence;

/// <summary>
/// Precomputed node along the optimized racing line.
/// </summary>
[System.Serializable]
public struct RacingLineNode
{
    public Vector3 Position;
    public Vector3 Tangent;
    public Vector3 Normal;
    public float Curvature;           // kappa (1 / radius)
    public float TargetVelocityM_s;   // Speed profile
    public float LateralGForce;       // Predicted cornering G
    public bool IsBrakingPoint;
    public bool IsApex;
    public float TrackWidth;
    public float LateralOffset;       // -1 (left boundary) to +1 (right boundary)
}

/// <summary>
/// Automated racing line solver computing the optimal minimum-curvature path,
/// geometric apexes, braking points, and velocity profile along SplineRoadBuilder tracks.
/// Dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class RacingLineAndApexOptimizer : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Track Connection")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private SplineRoadBuilder roadBuilder;
    [SerializeField] private float sampleIntervalDistanceM = 3.0f;
    [SerializeField] private float trackMarginM = 0.5f;

    [Header("Vehicle Performance Limits")]
    [SerializeField] private float vehicleMassKg = 1200f;
    [SerializeField] private float maxLateralG = 1.6f;          // 1.6 G cornering limit
    [SerializeField] private float maxBrakingDecelG = 1.4f;     // 1.4 G braking limit
    [SerializeField] private float maxDriveAccelerationG = 0.8f;// 0.8 G acceleration limit
    [SerializeField] private float maxTopSpeedM_s = 85f;        // ~306 km/h
    [SerializeField] private float aeroDownforceCoeff = 0.45f;  // Downforce multiplier

    [Header("Optimization Settings")]
    [SerializeField, Range(10, 150)] private int relaxationIterations = 60;
    [SerializeField, Range(0.01f, 0.5f)] private float relaxationStepSize = 0.15f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Generated racing line nodes
    private readonly List<RacingLineNode> optimizedNodes = new List<RacingLineNode>(512);

    public string SaveKey => "RacingLine_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public IReadOnlyList<RacingLineNode> Nodes => optimizedNodes;
    public int NodeCount => optimizedNodes.Count;

    private void Awake()
    {
        if (roadBuilder == null)
        {
            roadBuilder = FindFirstObjectByType<SplineRoadBuilder>();
        }

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }
    }

    private void OnDestroy()
    {
        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }
    }

    private void Start()
    {
        if (roadBuilder != null && roadBuilder.Segments.Count > 0)
        {
            GenerateAndOptimizeRacingLine();
        }
    }

    // =========================================================================
    // OPTIMIZATION & VELOCITY PROFILING PIPELINE
    // =========================================================================

    [ContextMenu("Generate & Optimize Racing Line")]
    public void GenerateAndOptimizeRacingLine()
    {
        if (roadBuilder == null || roadBuilder.Segments.Count == 0) return;

        // 1. Discretize track centerline and sample normals/widths
        SampleTrackCenterline();

        if (optimizedNodes.Count < 5) return;

        // 2. Iteratively relax lateral offsets to minimize path curvature (Outside -> Apex -> Outside)
        RelaxRacingLineCurvature();

        // 3. Compute local curvatures and identify corner Apexes
        EvaluateCurvaturesAndApexes();

        // 4. Run forward-backward velocity profiler and precompute exact Braking Points
        ComputeVelocityProfile();
    }

    private void SampleTrackCenterline()
    {
        optimizedNodes.Clear();

        var segments = roadBuilder.Segments;
        for (int s = 0; s < segments.Count; s++)
        {
            RoadSegment seg = segments[s];
            float segLength = seg.TotalLength;
            int samples = Mathf.Max(2, Mathf.RoundToInt(segLength / sampleIntervalDistanceM));

            for (int i = 0; i < samples; i++)
            {
                float dist = (float)i / samples * segLength;
                seg.SampleAtDistance(dist, out Vector3 pos, out Vector3 tangent);

                if (defaultTo2D)
                {
                    pos.z = 0f;
                    tangent.z = 0f;
                    tangent.Normalize();
                }

                // Compute perpendicular normal vector
                Vector3 upVector = defaultTo2D ? Vector3.forward : Vector3.up;
                Vector3 normal = Vector3.Cross(tangent, upVector).normalized;

                optimizedNodes.Add(new RacingLineNode
                {
                    Position = pos,
                    Tangent = tangent,
                    Normal = normal,
                    TrackWidth = Mathf.Max(2f, seg.Width - (trackMarginM * 2f)),
                    LateralOffset = 0f, // Centerline baseline
                    Curvature = 0f,
                    TargetVelocityM_s = maxTopSpeedM_s,
                    IsBrakingPoint = false,
                    IsApex = false
                });
            }
        }
    }

    private void RelaxRacingLineCurvature()
    {
        int count = optimizedNodes.Count;
        float[] offsets = new float[count];

        for (int iter = 0; iter < relaxationIterations; iter++)
        {
            for (int i = 0; i < count; i++)
            {
                int prevIdx = (i - 1 + count) % count;
                int nextIdx = (i + 1) % count;

                RacingLineNode node = optimizedNodes[i];
                RacingLineNode prev = optimizedNodes[prevIdx];
                RacingLineNode next = optimizedNodes[nextIdx];

                Vector3 currentPos = node.Position + (node.Normal * (offsets[i] * node.TrackWidth * 0.5f));
                Vector3 prevPos = prev.Position + (prev.Normal * (offsets[prevIdx] * prev.TrackWidth * 0.5f));
                Vector3 nextPos = next.Position + (next.Normal * (offsets[nextIdx] * next.TrackWidth * 0.5f));

                // Desired position is midpoint of neighbors (Laplacian smoothing reduces curvature)
                Vector3 targetPos = (prevPos + nextPos) * 0.5f;
                Vector3 displacement = targetPos - node.Position;

                // Project displacement onto lateral normal axis
                float lateralProj = Vector3.Dot(displacement, node.Normal);
                float desiredOffset = lateralProj / (node.TrackWidth * 0.5f);

                offsets[i] = Mathf.Clamp(Mathf.Lerp(offsets[i], desiredOffset, relaxationStepSize), -1f, 1f);
            }
        }

        // Apply relaxed offsets to nodes
        for (int i = 0; i < count; i++)
        {
            RacingLineNode n = optimizedNodes[i];
            n.LateralOffset = offsets[i];
            n.Position += n.Normal * (offsets[i] * n.TrackWidth * 0.5f);
            optimizedNodes[i] = n;
        }
    }

    private void EvaluateCurvaturesAndApexes()
    {
        int count = optimizedNodes.Count;

        for (int i = 0; i < count; i++)
        {
            int prevIdx = (i - 1 + count) % count;
            int nextIdx = (i + 1) % count;

            Vector3 a = optimizedNodes[prevIdx].Position;
            Vector3 b = optimizedNodes[i].Position;
            Vector3 c = optimizedNodes[nextIdx].Position;

            // Menger curvature computation: kappa = 4 * Area / (|a-b| * |b-c| * |c-a|)
            float ab = Vector3.Distance(a, b);
            float bc = Vector3.Distance(b, c);
            float ca = Vector3.Distance(c, a);

            float s = (ab + bc + ca) * 0.5f;
            float areaSq = s * (s - ab) * (s - bc) * (s - ca);
            float area = areaSq > 0f ? Mathf.Sqrt(areaSq) : 0f;

            float denom = ab * bc * ca;
            float kappa = denom > 0.0001f ? (4f * area / denom) : 0f;

            RacingLineNode node = optimizedNodes[i];
            node.Curvature = kappa;

            // Update tangent based on optimized neighbor chord
            Vector3 chord = (c - a).normalized;
            if (chord.sqrMagnitude > 0.001f) node.Tangent = chord;

            optimizedNodes[i] = node;
        }

        // Identify corner Apexes (local curvature maxima)
        for (int i = 0; i < count; i++)
        {
            int prevIdx = (i - 1 + count) % count;
            int nextIdx = (i + 1) % count;

            float k = optimizedNodes[i].Curvature;
            if (k > 0.015f && k > optimizedNodes[prevIdx].Curvature && k > optimizedNodes[nextIdx].Curvature)
            {
                RacingLineNode node = optimizedNodes[i];
                node.IsApex = true;
                optimizedNodes[i] = node;
            }
        }
    }

    private void ComputeVelocityProfile()
    {
        int count = optimizedNodes.Count;
        float g = 9.81f;

        // Pass 1: Maximum cornering speed based on lateral G-force and aero downforce
        for (int i = 0; i < count; i++)
        {
            RacingLineNode node = optimizedNodes[i];
            float k = Mathf.Max(0.0005f, node.Curvature);

            // Speed limit from tire grip: v_corner = sqrt((maxLateralG * g) / k)
            float vMax = Mathf.Sqrt((maxLateralG * g) / k);

            // Aerodynamic downforce benefit: v_aero = v * sqrt(1 + (F_down / M*g))
            vMax *= Mathf.Sqrt(1.0f + (aeroDownforceCoeff * (vMax / 50f)));
            node.TargetVelocityM_s = Mathf.Min(maxTopSpeedM_s, vMax);
            node.LateralGForce = (node.TargetVelocityM_s * node.TargetVelocityM_s * k) / g;

            optimizedNodes[i] = node;
        }

        // Pass 2: Backward Pass (Braking Profile - precompute Braking Points)
        float maxBrakeDecel = maxBrakingDecelG * g;
        for (int step = 0; step < count * 2; step++)
        {
            int i = (count - (step % count) - 1);
            int nextIdx = (i + 1) % count;

            float dist = Vector3.Distance(optimizedNodes[i].Position, optimizedNodes[nextIdx].Position);
            float vNext = optimizedNodes[nextIdx].TargetVelocityM_s;

            // v_i = min(v_i, sqrt(v_{i+1}^2 + 2 * a_brake * d))
            float maxSafeArrival = Mathf.Sqrt((vNext * vNext) + (2f * maxBrakeDecel * dist));

            RacingLineNode node = optimizedNodes[i];
            if (maxSafeArrival < node.TargetVelocityM_s)
            {
                node.TargetVelocityM_s = maxSafeArrival;
                // If velocity dropped significantly, this node is within the braking zone
                if (step % count > 0 && maxSafeArrival < maxTopSpeedM_s * 0.95f)
                {
                    node.IsBrakingPoint = true;
                }
            }
            optimizedNodes[i] = node;
        }

        // Pass 3: Forward Pass (Acceleration Profile out of corners)
        float maxDriveAcc = maxDriveAccelerationG * g;
        for (int step = 0; step < count * 2; step++)
        {
            int i = step % count;
            int nextIdx = (i + 1) % count;

            float dist = Vector3.Distance(optimizedNodes[i].Position, optimizedNodes[nextIdx].Position);
            float vCurr = optimizedNodes[i].TargetVelocityM_s;

            float maxPossibleNext = Mathf.Sqrt((vCurr * vCurr) + (2f * maxDriveAcc * dist));

            RacingLineNode nextNode = optimizedNodes[nextIdx];
            if (maxPossibleNext < nextNode.TargetVelocityM_s)
            {
                nextNode.TargetVelocityM_s = maxPossibleNext;
                optimizedNodes[nextIdx] = nextNode;
            }
        }
    }

    // =========================================================================
    // QUERY APIS FOR AI DRIVERS
    // =========================================================================

    /// <summary>
    /// Zero-GC spatial query finding the nearest racing line node for an AI racer.
    /// </summary>
    public RacingLineNode QueryNearestNode(Vector3 currentPos, out int nearestIndex)
    {
        nearestIndex = 0;
        if (optimizedNodes.Count == 0) return default;

        float minSqrDist = float.MaxValue;
        for (int i = 0; i < optimizedNodes.Count; i++)
        {
            Vector3 nodePos = optimizedNodes[i].Position;
            float sqrDist = defaultTo2D
                ? (new Vector2(nodePos.x - currentPos.x, nodePos.y - currentPos.y)).sqrMagnitude
                : (nodePos - currentPos).sqrMagnitude;

            if (sqrDist < minSqrDist)
            {
                minSqrDist = sqrDist;
                nearestIndex = i;
            }
        }

        return optimizedNodes[nearestIndex];
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<RacingLineNode> Nodes = new List<RacingLineNode>();
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer();
        container.Nodes.AddRange(optimizedNodes);
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null || container.Nodes == null) return;

        optimizedNodes.Clear();
        optimizedNodes.AddRange(container.Nodes);
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || optimizedNodes.Count < 2) return;

        for (int i = 0; i < optimizedNodes.Count; i++)
        {
            RacingLineNode node = optimizedNodes[i];
            int nextIdx = (i + 1) % optimizedNodes.Count;
            RacingLineNode next = optimizedNodes[nextIdx];

            // Color racing line by target speed: Red = heavy braking, Yellow = cornering, Green = full throttle
            float speedFraction = Mathf.Clamp01(node.TargetVelocityM_s / maxTopSpeedM_s);
            Color lineColor = Color.Lerp(Color.red, Color.green, speedFraction);
            Gizmos.color = lineColor;
            Gizmos.DrawLine(node.Position, next.Position);

            // Mark Apexes with magenta spheres
            if (node.IsApex)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireSphere(node.Position, 0.6f);
            }

            // Mark Braking Points with yellow cones/spheres
            if (node.IsBrakingPoint)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(node.Position, 0.35f);
            }
        }
    }
}
