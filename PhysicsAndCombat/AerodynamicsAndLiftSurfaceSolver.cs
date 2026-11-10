using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. AIRFOIL TYPES & DATA STRUCTURES
    // =========================================================================

    public enum AirfoilRole
    {
        MainWingLeft,
        MainWingRight,
        HorizontalStabilizerElevator,
        VerticalStabilizerRudder,
        Canard,
        FuselageBody
    }

    public enum ControlSurfaceType
    {
        None,
        Aileron,
        Elevator,
        Rudder,
        Flap,
        Elevon
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct WingStructuralDamageEvent : IGameEvent
    {
        public readonly int VesselInstanceId;
        public readonly string SurfaceName;
        public readonly float RemainingIntegrity;
        public readonly bool IsSevered;

        public WingStructuralDamageEvent(int vesselId, string surfaceName, float integrity, bool isSevered)
        {
            VesselInstanceId = vesselId;
            SurfaceName = surfaceName;
            RemainingIntegrity = integrity;
            IsSevered = isSevered;
        }
    }

    public readonly struct AirfoilStallStateEvent : IGameEvent
    {
        public readonly int VesselInstanceId;
        public readonly string SurfaceName;
        public readonly bool IsStalled;
        public readonly float AngleOfAttackDeg;

        public AirfoilStallStateEvent(int vesselId, string surfaceName, bool isStalled, float aoa)
        {
            VesselInstanceId = vesselId;
            SurfaceName = surfaceName;
            IsStalled = isStalled;
            AngleOfAttackDeg = aoa;
        }
    }

    // =========================================================================
    // 3. INDIVIDUAL AERODYNAMIC SURFACE COMPONENT
    // =========================================================================

    [Serializable]
    public class AerodynamicSurface
    {
        [Header("Identity & Geometry")]
        public string SurfaceName = "Left Wing";
        public AirfoilRole Role = AirfoilRole.MainWingLeft;
        [Tooltip("Local position of the Aerodynamic Center (quarter-chord) relative to vessel root")]
        public Vector3 LocalPosition = new Vector3(-3f, 0f, 0f);
        [Tooltip("Surface area in square meters (S)")]
        [Min(0.01f)] public float SurfaceAreaM2 = 8.0f;
        [Tooltip("Wing Aspect Ratio (b^2 / S)")]
        [Min(0.5f)] public float AspectRatio = 6.0f;
        [Tooltip("Oswald efficiency factor (typically 0.7 to 0.9)")]
        [Range(0.5f, 1.0f)] public float OswaldEfficiency = 0.85f;

        [Header("Lift & Drag Characteristics")]
        [Tooltip("Linear lift curve slope (dCl/dAlpha), typically ~2*pi (6.28 / rad = 0.11 / deg)")]
        public float LiftSlopePerDeg = 0.11f;
        [Tooltip("Zero-lift angle of attack in degrees")]
        public float ZeroLiftAngleDeg = 0f;
        [Tooltip("Critical stall angle in degrees")]
        [Range(5f, 30f)] public float StallAngleDeg = 16f;
        [Tooltip("Baseline parasite drag coefficient (Cd0)")]
        [Min(0.001f)] public float ParasiteDragCoeff = 0.02f;

        [Header("Control Surface Deflection")]
        public ControlSurfaceType ControlType = ControlSurfaceType.Aileron;
        [Tooltip("Maximum deflection angle in degrees")]
        [Range(0f, 45f)] public float MaxDeflectionDeg = 25f;
        [Tooltip("Control surface chord fraction (flapped area ratio)")]
        [Range(0.1f, 0.5f)] public float FlapFraction = 0.25f;

        [Header("Structural Integrity & Damage")]
        [Range(0f, 100f)] public float IntegrityPercent = 100f;
        public bool IsSevered = false;
        public float MaxLoadG = 12f;

        // Telemetry values updated each physics step
        [NonSerialized] public float CurrentAngleOfAttackDeg;
        [NonSerialized] public bool IsCurrentlyStalled;
        [NonSerialized] public Vector3 ComputedLiftForce;
        [NonSerialized] public Vector3 ComputedDragForce;
        [NonSerialized] public float CurrentDeflectionNormalized; // -1 to +1
    }

    // =========================================================================
    // 4. MASTER AERODYNAMICS & LIFT SURFACE SOLVER (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Component-based aerodynamic airfoil solver calculating real-time Lift, Drag,
    /// Angle of Attack (AoA), induced drag, post-stall separation, and control surface deflections.
    /// Supports both full 6-DOF 3D flight (Rigidbody) and 2D flight (Rigidbody2D),
    /// plus asymmetric structural damage causing realistic aerodynamic spin/stalls.
    /// </summary>
    [DisallowMultipleComponent]
    public class AerodynamicsAndLiftSurfaceSolver : MonoBehaviour, ISaveableSystem
    {
        [Header("Atmospheric Conditions")]
        [Tooltip("Sea-level air density in kg/m^3")]
        [SerializeField] private float seaLevelAirDensity = 1.225f;
        [Tooltip("Scale height for barometric formula in meters")]
        [SerializeField] private float atmosphericScaleHeight = 8500f;
        [Tooltip("Ambient wind velocity vector")]
        [SerializeField] private Vector3 ambientWindVelocity = Vector3.zero;

        [Header("Physics Mode")]
        [Tooltip("Enables 2D aerodynamic physics (Rigidbody2D) instead of 3D (Rigidbody)")]
        [SerializeField] private bool is2DMode = false;

        [Header("Lifting Surfaces")]
        [SerializeField] private List<AerodynamicSurface> surfaces = new List<AerodynamicSurface>();

        [Header("Control Surface Inputs")]
        [Range(-1f, 1f)] public float PitchInput = 0f;
        [Range(-1f, 1f)] public float RollInput = 0f;
        [Range(-1f, 1f)] public float YawInput = 0f;
        [Range(0f, 1f)] public float FlapsInput = 0f;

        private Rigidbody body3D;
        private Rigidbody2D body2D;

        public string SaveKey => $"Aero_{gameObject.name}_{GetInstanceID()}";
        public int SurfaceCount => surfaces.Count;

        private void Awake()
        {
            body3D = GetComponent<Rigidbody>();
            body2D = GetComponent<Rigidbody2D>();

            if (body2D != null && body3D == null)
            {
                is2DMode = true;
            }
        }

        public void ApplyDamageToSurface(int surfaceIndex, float damagePercent)
        {
            if (surfaceIndex < 0 || surfaceIndex >= surfaces.Count) return;
            var s = surfaces[surfaceIndex];
            s.IntegrityPercent = Mathf.Max(0f, s.IntegrityPercent - damagePercent);

            if (s.IntegrityPercent <= 0f && !s.IsSevered)
            {
                s.IsSevered = true;
                EventBus<WingStructuralDamageEvent>.Raise(new WingStructuralDamageEvent(
                    GetInstanceID(), s.SurfaceName, 0f, true));
            }
            else
            {
                EventBus<WingStructuralDamageEvent>.Raise(new WingStructuralDamageEvent(
                    GetInstanceID(), s.SurfaceName, s.IntegrityPercent, false));
            }
        }

        private void FixedUpdate()
        {
            float altitude = transform.position.y;
            // Barometric formula: rho = rho0 * exp(-h / H)
            float airDensity = seaLevelAirDensity * Mathf.Exp(-Mathf.Max(0f, altitude) / atmosphericScaleHeight);

            Vector3 linearVel = is2DMode ? (body2D != null ? (Vector3)body2D.linearVelocity : Vector3.zero) :
                                          (body3D != null ? body3D.linearVelocity : Vector3.zero);
            Vector3 angularVel = is2DMode ? (body2D != null ? new Vector3(0f, 0f, body2D.angularVelocity * Mathf.Deg2Rad) : Vector3.zero) :
                                            (body3D != null ? body3D.angularVelocity : Vector3.zero);

            for (int i = 0; i < surfaces.Count; i++)
            {
                AerodynamicSurface surface = surfaces[i];
                if (surface.IsSevered)
                {
                    surface.ComputedLiftForce = Vector3.zero;
                    surface.ComputedDragForce = Vector3.zero;
                    continue;
                }

                EvaluateSurfaceForces(surface, airDensity, linearVel, angularVel);
            }
        }

        private void EvaluateSurfaceForces(AerodynamicSurface s, float density, Vector3 bodyLinearVel, Vector3 bodyAngularVel)
        {
            Vector3 worldCenter = transform.TransformPoint(s.LocalPosition);
            Vector3 r = worldCenter - transform.position;

            // Local relative airflow velocity: v_local = v_body + (omega x r) - v_wind
            Vector3 tangentialVel = Vector3.Cross(bodyAngularVel, r);
            Vector3 relativeAirflow = bodyLinearVel + tangentialVel - ambientWindVelocity;

            float speed = relativeAirflow.magnitude;
            if (speed < 0.1f)
            {
                s.ComputedLiftForce = Vector3.zero;
                s.ComputedDragForce = Vector3.zero;
                s.CurrentAngleOfAttackDeg = 0f;
                return;
            }

            Vector3 flowDir = relativeAirflow / speed;

            // Surface reference vectors
            Vector3 forwardDir = transform.forward;
            Vector3 upDir = transform.up;
            Vector3 rightDir = transform.right;

            if (is2DMode)
            {
                forwardDir = transform.right; // 2D facing direction
                upDir = transform.up;
                rightDir = Vector3.forward;
            }

            // Determine effective deflection based on control inputs
            float deflectionInput = 0f;
            switch (s.ControlType)
            {
                case ControlSurfaceType.Aileron:
                    deflectionInput = (s.Role == AirfoilRole.MainWingLeft) ? RollInput : -RollInput;
                    break;
                case ControlSurfaceType.Elevator:
                    deflectionInput = PitchInput;
                    break;
                case ControlSurfaceType.Rudder:
                    deflectionInput = YawInput;
                    break;
                case ControlSurfaceType.Flap:
                    deflectionInput = FlapsInput;
                    break;
                case ControlSurfaceType.Elevon:
                    float rollSign = (s.Role == AirfoilRole.MainWingLeft) ? 1f : -1f;
                    deflectionInput = Mathf.Clamp(PitchInput + (RollInput * rollSign), -1f, 1f);
                    break;
            }
            s.CurrentDeflectionNormalized = deflectionInput;
            float deflectionAngleDeg = deflectionInput * s.MaxDeflectionDeg;

            // Angle of Attack: angle between surface chord and airflow
            // Project airflow onto surface plane
            float chordVel = Vector3.Dot(relativeAirflow, forwardDir);
            float normalVel = Vector3.Dot(relativeAirflow, upDir);

            float aoaRad = -Mathf.Atan2(normalVel, chordVel);
            float aoaDeg = aoaRad * Mathf.Rad2Deg + s.ZeroLiftAngleDeg + (deflectionAngleDeg * s.FlapFraction);
            s.CurrentAngleOfAttackDeg = aoaDeg;

            // Lift and Drag Coefficients with smooth post-stall behavior
            float absAoA = Mathf.Abs(aoaDeg);
            bool isStalled = absAoA > s.StallAngleDeg;

            if (isStalled != s.IsCurrentlyStalled)
            {
                s.IsCurrentlyStalled = isStalled;
                EventBus<AirfoilStallStateEvent>.Raise(new AirfoilStallStateEvent(
                    GetInstanceID(), s.SurfaceName, isStalled, aoaDeg));
            }

            float cl;
            if (!isStalled)
            {
                // Linear regime
                cl = s.LiftSlopePerDeg * aoaDeg;
            }
            else
            {
                // Post-stall lift decay: sinusoidal transition into flat plate
                float stallCl = s.LiftSlopePerDeg * (s.StallAngleDeg * Mathf.Sign(aoaDeg));
                cl = stallCl * Mathf.Cos((absAoA - s.StallAngleDeg) * Mathf.Deg2Rad);
            }

            // Induced drag: Cdi = Cl^2 / (pi * AR * e)
            float inducedDrag = (cl * cl) / Mathf.Max(0.1f, Mathf.PI * s.AspectRatio * s.OswaldEfficiency);
            // Form & friction drag increases dramatically past stall
            float formDrag = isStalled ? 1.2f * Mathf.Sin(absAoA * Mathf.Deg2Rad) : 0f;
            float cd = s.ParasiteDragCoeff + inducedDrag + formDrag;

            // Scale by surface integrity (damage reduces wing area)
            float effectiveArea = s.SurfaceAreaM2 * (s.IntegrityPercent / 100f);

            // Dynamic pressure: q = 0.5 * rho * v^2
            float q = 0.5f * density * speed * speed;

            float liftMagnitude = q * effectiveArea * cl;
            float dragMagnitude = q * effectiveArea * cd;

            // Lift is perpendicular to relative airflow in the surface pitch plane
            Vector3 liftDir = Vector3.Cross(Vector3.Cross(flowDir, upDir), flowDir).normalized;
            if (liftDir.sqrMagnitude < 0.001f) liftDir = upDir;

            Vector3 dragDir = -flowDir;

            Vector3 totalLift = liftDir * liftMagnitude;
            Vector3 totalDrag = dragDir * dragMagnitude;

            s.ComputedLiftForce = totalLift;
            s.ComputedDragForce = totalDrag;

            Vector3 totalForce = totalLift + totalDrag;

            // Apply force at Aerodynamic Center
            if (is2DMode && body2D != null)
            {
                body2D.AddForceAtPosition(new Vector2(totalForce.x, totalForce.y), new Vector2(worldCenter.x, worldCenter.y));
            }
            else if (body3D != null)
            {
                body3D.AddForceAtPosition(totalForce, worldCenter);
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class AeroSaveData
        {
            public List<SurfaceSaveRecord> Records = new List<SurfaceSaveRecord>();
        }

        [Serializable]
        private class SurfaceSaveRecord
        {
            public string Name;
            public float Integrity;
            public bool IsSevered;
        }

        public object CaptureState()
        {
            var data = new AeroSaveData();
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                data.Records.Add(new SurfaceSaveRecord
                {
                    Name = s.SurfaceName,
                    Integrity = s.IntegrityPercent,
                    IsSevered = s.IsSevered
                });
            }
            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<AeroSaveData>(stateJson);
            if (data == null || data.Records == null) return;

            for (int i = 0; i < data.Records.Count; i++)
            {
                var r = data.Records[i];
                for (int s = 0; s < surfaces.Count; s++)
                {
                    if (surfaces[s].SurfaceName == r.Name)
                    {
                        surfaces[s].IntegrityPercent = r.Integrity;
                        surfaces[s].IsSevered = r.IsSevered;
                        break;
                    }
                }
            }
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                Vector3 worldCenter = transform.TransformPoint(s.LocalPosition);

                // Surface frame
                Gizmos.color = s.IsSevered ? Color.gray : (s.IsCurrentlyStalled ? Color.red : Color.cyan);
                Gizmos.DrawWireCube(worldCenter, new Vector3(Mathf.Sqrt(s.SurfaceAreaM2), 0.1f, Mathf.Sqrt(s.SurfaceAreaM2) / s.AspectRatio));

                // Lift Vector (Green)
                if (s.ComputedLiftForce.sqrMagnitude > 0.01f)
                {
                    Gizmos.color = Color.green;
                    Gizmos.DrawRay(worldCenter, s.ComputedLiftForce.normalized * Mathf.Clamp(s.ComputedLiftForce.magnitude * 0.01f, 0.5f, 5f));
                }

                // Drag Vector (Red)
                if (s.ComputedDragForce.sqrMagnitude > 0.01f)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawRay(worldCenter, s.ComputedDragForce.normalized * Mathf.Clamp(s.ComputedDragForce.magnitude * 0.01f, 0.5f, 5f));
                }
            }
        }
    }
}
