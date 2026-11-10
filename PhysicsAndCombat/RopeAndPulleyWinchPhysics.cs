using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. ENUMS & DATA STRUCTURES
    // =========================================================================

    public enum WinchState
    {
        IdleBraked,
        WindingIn,
        PayingOut,
        FreeSpooling,
        CableSnapped
    }

    [Serializable]
    public class PulleySheave
    {
        public string SheaveName = "Pulley 1";
        public Transform SheaveTransform;
        public Vector3 LocalOffset;
        [Range(0.05f, 2f)] public float SheaveRadius = 0.15f;
        [Range(0.8f, 1f)] public float MechanicalEfficiency = 0.98f;

        public Vector3 WorldPosition => SheaveTransform != null ?
            SheaveTransform.TransformPoint(LocalOffset) : LocalOffset;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct CableSnappedEvent : IGameEvent
    {
        public readonly int WinchInstanceId;
        public readonly float TensionAtBreakNewtons;
        public readonly Vector3 BreakPosition;

        public CableSnappedEvent(int winchId, float tension, Vector3 pos)
        {
            WinchInstanceId = winchId;
            TensionAtBreakNewtons = tension;
            BreakPosition = pos;
        }
    }

    public readonly struct CableTensionOverloadEvent : IGameEvent
    {
        public readonly int WinchInstanceId;
        public readonly float CurrentTensionNewtons;
        public readonly float BreakingStrengthNewtons;

        public CableTensionOverloadEvent(int winchId, float tension, float strength)
        {
            WinchInstanceId = winchId;
            CurrentTensionNewtons = tension;
            BreakingStrengthNewtons = strength;
        }
    }

    // =========================================================================
    // 3. MASTER ROPE, PULLEY & WINCH PHYSICS COMPONENT (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Mechanical force-transmitting cables, motorized winches, and pulley systems.
    /// Supports mechanical advantage (block & tackle), elastic tension dynamics,
    /// bidirectional physics forces (Rigidbody 3D & Rigidbody2D), and cable snapping.
    /// Extends or synchronizes with VerletRope for high-fidelity visual simulation.
    /// </summary>
    [DisallowMultipleComponent]
    public class RopeAndPulleyWinchPhysics : MonoBehaviour, ISaveableSystem
    {
        [Header("Anchors & Connected Entities")]
        [Tooltip("Winch base anchor (optional Rigidbody or Rigidbody2D)")]
        [SerializeField] private Transform winchAnchor;
        [Tooltip("Suspended payload attachment point (with Rigidbody or Rigidbody2D)")]
        [SerializeField] private Transform payloadAttachment;
        [Tooltip("Optional visual VerletRope to synchronize length with")]
        [SerializeField] private VerletRope optionalVerletRope;

        [Header("Intermediate Pulleys")]
        [SerializeField] private List<PulleySheave> intermediatePulleys = new List<PulleySheave>();

        [Header("Winch Motor & Drum")]
        [SerializeField] private WinchState currentState = WinchState.IdleBraked;
        [Tooltip("Current unspooled cable length in meters")]
        [SerializeField, Min(0.1f)] private float currentCableLength = 10f;
        [Tooltip("Minimum allowable cable length")]
        [SerializeField, Min(0.1f)] private float minCableLength = 1.0f;
        [Tooltip("Maximum spool capacity in meters")]
        [SerializeField, Min(1f)] private float maxCableLength = 100f;
        [Tooltip("Motor spooling speed in meters/second")]
        [SerializeField, Range(0.1f, 25f)] private float spoolSpeedMetersPerSec = 2.5f;
        [Tooltip("Maximum motor pull force (Newtons)")]
        [SerializeField, Min(100f)] private float maxMotorTensionNewtons = 25000f;
        [Tooltip("Mechanical Advantage multiplier (Block & Tackle: 1 = direct, 2 = 2-part purchase, etc.)")]
        [SerializeField, Range(1, 8)] private int mechanicalAdvantage = 1;

        [Header("Cable Material & Tension Dynamics")]
        [Tooltip("Young's elastic modulus / spring stiffness in N/m")]
        [SerializeField, Min(1000f)] private float cableStiffness = 50000f;
        [Tooltip("Internal damping coefficient")]
        [SerializeField, Range(10f, 2000f)] private float cableDamping = 250f;
        [Tooltip("Breaking strength in Newtons. Exceeding this snaps the cable!")]
        [SerializeField, Min(500f)] private float breakingStrengthNewtons = 45000f;

        [Header("Physics Mode")]
        [Tooltip("Operate with 2D physics (Rigidbody2D) instead of 3D")]
        [SerializeField] private bool is2DMode = false;

        [Header("Visual Line Rendering")]
        [SerializeField] private LineRenderer cableLineRenderer;

        // Telemetry
        [SerializeField] private float currentTensionNewtons = 0f;
        [SerializeField] private float totalGeometricSpan = 0f;

        private Rigidbody winchRb3D;
        private Rigidbody payloadRb3D;
        private Rigidbody2D winchRb2D;
        private Rigidbody2D payloadRb2D;
        private float lastGeometricSpan = 0f;

        // Pre-allocated line points buffer
        private readonly Vector3[] linePointsBuffer = new Vector3[32];

        public string SaveKey => $"Winch_{gameObject.name}_{GetInstanceID()}";
        public WinchState State => currentState;
        public float CurrentTension => currentTensionNewtons;
        public float CableLength => currentCableLength;

        private void Awake()
        {
            if (winchAnchor == null) winchAnchor = transform;

            if (winchAnchor != null)
            {
                winchRb3D = winchAnchor.GetComponent<Rigidbody>();
                winchRb2D = winchAnchor.GetComponent<Rigidbody2D>();
            }

            if (payloadAttachment != null)
            {
                payloadRb3D = payloadAttachment.GetComponent<Rigidbody>();
                payloadRb2D = payloadAttachment.GetComponent<Rigidbody2D>();
            }

            if (winchRb2D != null || payloadRb2D != null)
            {
                is2DMode = true;
            }

            if (cableLineRenderer == null)
            {
                cableLineRenderer = GetComponent<LineRenderer>();
            }

            CalculateGeometricSpan(out totalGeometricSpan);
            lastGeometricSpan = totalGeometricSpan;
        }

        // =========================================================================
        // PUBLIC CONTROL API
        // =========================================================================

        public void WindIn()
        {
            if (currentState != WinchState.CableSnapped)
                currentState = WinchState.WindingIn;
        }

        public void PayOut()
        {
            if (currentState != WinchState.CableSnapped)
                currentState = WinchState.PayingOut;
        }

        public void Brake()
        {
            if (currentState != WinchState.CableSnapped)
                currentState = WinchState.IdleBraked;
        }

        public void FreeSpool()
        {
            if (currentState != WinchState.CableSnapped)
                currentState = WinchState.FreeSpooling;
        }

        public void SnapCable()
        {
            if (currentState == WinchState.CableSnapped) return;

            currentState = WinchState.CableSnapped;
            currentTensionNewtons = 0f;

            Vector3 breakPos = payloadAttachment != null ? payloadAttachment.position : transform.position;
            EventBus<CableSnappedEvent>.Raise(new CableSnappedEvent(GetInstanceID(), currentTensionNewtons, breakPos));

            if (cableLineRenderer != null)
                cableLineRenderer.enabled = false;
        }

        // =========================================================================
        // FIXEDUPDATE: MECHANICAL & TENSION PHYSICS LOOP
        // =========================================================================

        private void FixedUpdate()
        {
            if (currentState == WinchState.CableSnapped || payloadAttachment == null)
                return;

            float dt = Time.fixedDeltaTime;

            // 1. Spool drum kinematics
            UpdateSpoolMotor(dt);

            // 2. Measure actual geometric path through all pulleys
            int pointCount = CalculateGeometricSpan(out totalGeometricSpan);

            // 3. Hooke's Elasticity + Damping Tension Solver
            float stretch = totalGeometricSpan - currentCableLength;
            float stretchVelocity = (totalGeometricSpan - lastGeometricSpan) / dt;
            lastGeometricSpan = totalGeometricSpan;

            if (stretch > 0f)
            {
                // Mechanical advantage multiplies lifting pull and effective stiffness
                float effectiveStiffness = cableStiffness * mechanicalAdvantage;
                currentTensionNewtons = (effectiveStiffness * (stretch / currentCableLength)) + (cableDamping * stretchVelocity);
                currentTensionNewtons = Mathf.Clamp(currentTensionNewtons, 0f, maxMotorTensionNewtons * mechanicalAdvantage);
            }
            else
            {
                currentTensionNewtons = 0f; // Cable is slack
            }

            // 4. Overload and Snapping Checks
            if (currentTensionNewtons >= breakingStrengthNewtons)
            {
                SnapCable();
                return;
            }
            else if (currentTensionNewtons >= breakingStrengthNewtons * 0.85f)
            {
                EventBus<CableTensionOverloadEvent>.Raise(new CableTensionOverloadEvent(
                    GetInstanceID(), currentTensionNewtons, breakingStrengthNewtons));
            }

            // 5. Apply Bidirectional Forces
            ApplyTensionForces(pointCount);

            // 6. Update Visuals
            UpdateLineRenderer(pointCount);

            // Sync with optional VerletRope
            if (optionalVerletRope != null)
            {
                optionalVerletRope.segmentLength = currentCableLength / Mathf.Max(1, optionalVerletRope.segments);
            }
        }

        private void UpdateSpoolMotor(float dt)
        {
            float speed = (spoolSpeedMetersPerSec / mechanicalAdvantage) * dt;

            switch (currentState)
            {
                case WinchState.WindingIn:
                    currentCableLength = Mathf.Max(minCableLength, currentCableLength - speed);
                    break;
                case WinchState.PayingOut:
                    currentCableLength = Mathf.Min(maxCableLength, currentCableLength + speed);
                    break;
                case WinchState.FreeSpooling:
                    if (currentTensionNewtons > 50f)
                    {
                        currentCableLength = Mathf.Min(maxCableLength, currentCableLength + (spoolSpeedMetersPerSec * 2f * dt));
                    }
                    break;
            }
        }

        private int CalculateGeometricSpan(out float totalLength)
        {
            totalLength = 0f;
            Vector3 startPos = winchAnchor != null ? winchAnchor.position : transform.position;
            linePointsBuffer[0] = startPos;
            int count = 1;

            Vector3 prev = startPos;

            for (int i = 0; i < intermediatePulleys.Count; i++)
            {
                Vector3 pPos = intermediatePulleys[i].WorldPosition;
                linePointsBuffer[count++] = pPos;
                totalLength += Vector3.Distance(prev, pPos);
                prev = pPos;
            }

            if (payloadAttachment != null)
            {
                Vector3 endPos = payloadAttachment.position;
                linePointsBuffer[count++] = endPos;
                totalLength += Vector3.Distance(prev, endPos);
            }

            return count;
        }

        private void ApplyTensionForces(int pointCount)
        {
            if (currentTensionNewtons <= 0.001f || pointCount < 2) return;

            // Vector pulling payload towards last pulley/winch
            Vector3 lastSegment = (linePointsBuffer[pointCount - 2] - linePointsBuffer[pointCount - 1]).normalized;
            Vector3 payloadForce = lastSegment * currentTensionNewtons;

            // Vector pulling winch towards first pulley/payload
            Vector3 firstSegment = (linePointsBuffer[1] - linePointsBuffer[0]).normalized;
            Vector3 winchForce = firstSegment * currentTensionNewtons;

            // Apply to Payload
            if (is2DMode && payloadRb2D != null)
            {
                payloadRb2D.AddForceAtPosition(new Vector2(payloadForce.x, payloadForce.y), (Vector2)payloadAttachment.position);
            }
            else if (payloadRb3D != null)
            {
                payloadRb3D.AddForceAtPosition(payloadForce, payloadAttachment.position);
            }

            // Apply to Winch Anchor
            if (is2DMode && winchRb2D != null)
            {
                winchRb2D.AddForceAtPosition(new Vector2(winchForce.x, winchForce.y), (Vector2)winchAnchor.position);
            }
            else if (winchRb3D != null)
            {
                winchRb3D.AddForceAtPosition(winchForce, winchAnchor.position);
            }

            // Intermediate Pulleys experience net resultant force vector
            for (int i = 1; i < pointCount - 1; i++)
            {
                int pulleyIndex = i - 1;
                var pulley = intermediatePulleys[pulleyIndex];
                if (pulley.SheaveTransform != null)
                {
                    Vector3 inVec = (linePointsBuffer[i - 1] - linePointsBuffer[i]).normalized;
                    Vector3 outVec = (linePointsBuffer[i + 1] - linePointsBuffer[i]).normalized;
                    Vector3 resultant = (inVec + outVec) * (currentTensionNewtons * pulley.MechanicalEfficiency);

                    var pRb3D = pulley.SheaveTransform.GetComponent<Rigidbody>();
                    var pRb2D = pulley.SheaveTransform.GetComponent<Rigidbody2D>();

                    if (is2DMode && pRb2D != null)
                        pRb2D.AddForce(new Vector2(resultant.x, resultant.y));
                    else if (pRb3D != null)
                        pRb3D.AddForce(resultant);
                }
            }
        }

        private void UpdateLineRenderer(int count)
        {
            if (cableLineRenderer == null) return;
            cableLineRenderer.positionCount = count;
            for (int i = 0; i < count; i++)
            {
                cableLineRenderer.SetPosition(i, linePointsBuffer[i]);
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class WinchSaveData
        {
            public int State;
            public float CableLen;
            public float Tension;
        }

        public object CaptureState()
        {
            return new WinchSaveData
            {
                State = (int)currentState,
                CableLen = currentCableLength,
                Tension = currentTensionNewtons
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<WinchSaveData>(stateJson);
            if (data == null) return;

            currentState = (WinchState)data.State;
            currentCableLength = data.CableLen;
            currentTensionNewtons = data.Tension;
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            Vector3 start = winchAnchor != null ? winchAnchor.position : transform.position;
            Gizmos.color = currentState == WinchState.CableSnapped ? Color.red : Color.cyan;
            Gizmos.DrawWireSphere(start, 0.25f);

            Vector3 prev = start;
            for (int i = 0; i < intermediatePulleys.Count; i++)
            {
                Vector3 p = intermediatePulleys[i].WorldPosition;
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(p, intermediatePulleys[i].SheaveRadius);
                Gizmos.color = Color.white;
                Gizmos.DrawLine(prev, p);
                prev = p;
            }

            if (payloadAttachment != null)
            {
                Gizmos.color = Color.white;
                Gizmos.DrawLine(prev, payloadAttachment.position);
                Gizmos.color = Color.magenta;
                Gizmos.DrawWireCube(payloadAttachment.position, Vector3.one * 0.3f);
            }
        }
    }
}
