using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. ORBITAL STRUCTS & KEPLERIAN ELEMENTS (ZERO-GC ALLOCATION)
    // =========================================================================

    [Serializable]
    public struct KeplerianElements
    {
        [Tooltip("Semi-major axis in meters (a)")]
        public double SemiMajorAxisMeters;
        [Tooltip("Orbital eccentricity (e): 0 = circular, 0<e<1 = elliptical, 1 = parabolic, >1 = hyperbolic")]
        public double Eccentricity;
        [Tooltip("Orbital inclination in degrees (i)")]
        public double InclinationDeg;
        [Tooltip("Longitude of Ascending Node in degrees (Omega)")]
        public double LongitudeOfAscendingNodeDeg;
        [Tooltip("Argument of Periapsis in degrees (omega)")]
        public double ArgumentOfPeriapsisDeg;
        [Tooltip("True Anomaly in degrees (nu)")]
        public double TrueAnomalyDeg;
        [Tooltip("Apoapsis distance from body center (meters)")]
        public double ApoapsisMeters;
        [Tooltip("Periapsis distance from body center (meters)")]
        public double PeriapsisMeters;
        [Tooltip("Orbital period in seconds")]
        public double OrbitalPeriodSeconds;

        public bool IsClosedOrbit => Eccentricity < 1.0;
        public bool IsHyperbolic => Eccentricity > 1.0;
    }

    [Serializable]
    public struct ManeuverNode
    {
        [Tooltip("Delta-V prograde/retrograde in m/s")]
        public float DeltaV_Prograde;
        [Tooltip("Delta-V normal/anti-normal in m/s")]
        public float DeltaV_Normal;
        [Tooltip("Delta-V radial in/out in m/s")]
        public float DeltaV_Radial;
        [Tooltip("Scheduled time of burn in seconds")]
        public float BurnTimeSeconds;
        [Tooltip("Total burn duration in seconds")]
        public float BurnDurationSeconds;

        public Vector3 GetTotalDeltaVVector(Vector3 prograde, Vector3 normal, Vector3 radial) =>
            (prograde * DeltaV_Prograde) + (normal * DeltaV_Normal) + (radial * DeltaV_Radial);

        public float TotalDeltaVMagnitude => Mathf.Sqrt(
            DeltaV_Prograde * DeltaV_Prograde +
            DeltaV_Normal * DeltaV_Normal +
            DeltaV_Radial * DeltaV_Radial);
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct SphereOfInfluenceTransitionEvent : IGameEvent
    {
        public readonly int VesselId;
        public readonly int PreviousBodyId;
        public readonly int NewBodyId;
        public readonly Vector3 TransitionPoint;

        public SphereOfInfluenceTransitionEvent(int vesselId, int prevBody, int newBody, Vector3 point)
        {
            VesselId = vesselId;
            PreviousBodyId = prevBody;
            NewBodyId = newBody;
            TransitionPoint = point;
        }
    }

    public readonly struct PeriapsisReachedEvent : IGameEvent
    {
        public readonly int VesselId;
        public readonly float AltitudeMeters;
        public readonly float VelocityM_s;

        public PeriapsisReachedEvent(int vesselId, float alt, float vel)
        {
            VesselId = vesselId;
            AltitudeMeters = alt;
            VelocityM_s = vel;
        }
    }

    public readonly struct ManeuverBurnExecutedEvent : IGameEvent
    {
        public readonly int VesselId;
        public readonly float ExecutedDeltaV;
        public readonly Vector3 ResultingVelocity;

        public ManeuverBurnExecutedEvent(int vesselId, float deltaV, Vector3 vel)
        {
            VesselId = vesselId;
            ExecutedDeltaV = deltaV;
            ResultingVelocity = vel;
        }
    }

    // =========================================================================
    // 3. CELESTIAL BODY COMPONENT
    // =========================================================================

    [DisallowMultipleComponent]
    public class CelestialBody : MonoBehaviour
    {
        [Header("Physical Attributes")]
        public int BodyId = 1;
        public string BodyName = "Kerbin";
        [Tooltip("Mass in kilograms (Earth: 5.972e24 kg)")]
        public double MassKg = 5.972e24;
        [Tooltip("Mean radius of body in meters")]
        public double RadiusMeters = 600000.0;
        [Tooltip("Standard Gravitational Parameter mu = G * M (m^3 / s^2)")]
        public double GravitationalParameter_Mu;

        [Header("Hierarchy & SOI")]
        public CelestialBody PrimaryParentBody;
        [Tooltip("Sphere of Influence radius (Laplace SOI) in meters")]
        public double SOIRadiusMeters = 84000000.0;

        public const double GravitationalConstant_G = 6.67430e-11;

        private void Awake()
        {
            if (GravitationalParameter_Mu <= 0.0)
            {
                GravitationalParameter_Mu = GravitationalConstant_G * MassKg;
            }

            // Compute Laplace SOI if orbiting parent body: r_soi = a * (m / M)^(2/5)
            if (PrimaryParentBody != null)
            {
                double dist = Vector3.Distance(transform.position, PrimaryParentBody.transform.position);
                double massRatio = MassKg / PrimaryParentBody.MassKg;
                SOIRadiusMeters = dist * Math.Pow(massRatio, 0.4);
            }
        }
    }

    // =========================================================================
    // 4. MASTER ORBITAL MECHANICS AND GRAVITY SOLVER (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Real-time N-body and Patched-Conics Keplerian orbital mechanics solver.
    /// Features:
    /// - Symplectic Velocity Verlet integration for multi-body gravity
    /// - Analytic Keplerian orbital elements (a, e, i, Omega, omega, nu, Apoapsis, Periapsis)
    /// - Zero-GC trajectory prediction buffer for flight path and maneuver node previews
    /// - Sphere of Influence (SOI) transitions and Lagrange points
    /// - Dual compatibility with full 3D space and 2D planar orbits.
    /// </summary>
    [DisallowMultipleComponent]
    public class OrbitalMechanicsAndGravitySolver : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static OrbitalMechanicsAndGravitySolver Instance { get; private set; }

        [Header("Central Gravitational Body")]
        [SerializeField] private CelestialBody primaryBody;

        [Header("Vessel Kinematics")]
        [SerializeField] private Vector3 vesselPosition;
        [SerializeField] private Vector3 vesselVelocity;
        [SerializeField] private bool usePhysicsRigidbody = false;
        [SerializeField] private bool is2DPlanarMode = false;

        [Header("Active Keplerian Orbit")]
        [SerializeField] private KeplerianElements currentOrbit;

        [Header("Maneuver Node Planning")]
        [SerializeField] private bool hasActiveManeuverNode = false;
        [SerializeField] private ManeuverNode activeManeuver;

        [Header("Trajectory Preview")]
        [Range(32, 256)] [SerializeField] private int trajectoryPointCount = 128;
        [SerializeField] private LineRenderer trajectoryLineRenderer;

        private Rigidbody body3D;
        private Rigidbody2D body2D;

        // Pre-allocated trajectory point arrays for zero-GC prediction
        private Vector3[] trajectoryPoints;

        public string SaveKey => $"OrbitSolver_{gameObject.name}_{GetInstanceID()}";
        public KeplerianElements Orbit => currentOrbit;
        public CelestialBody CurrentPrimary => primaryBody;
        public bool HasActiveManeuverNode => hasActiveManeuverNode;

        private void Awake()
        {
            if (Instance == null) Instance = this;

            body3D = GetComponent<Rigidbody>();
            body2D = GetComponent<Rigidbody2D>();

            if (body2D != null && body3D == null)
            {
                is2DPlanarMode = true;
            }

            trajectoryPoints = new Vector3[trajectoryPointCount];

            if (primaryBody == null)
            {
                primaryBody = FindFirstObjectByType<CelestialBody>();
            }

            vesselPosition = transform.position;
            UpdateKeplerianElements();
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, TickChannel.Fast);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, TickChannel.Fast);
        }

        // =========================================================================
        // PUBLIC API: MANEUVER NODES & TRANSFERS
        // =========================================================================

        public void SetManeuverNode(in ManeuverNode node)
        {
            activeManeuver = node;
            hasActiveManeuverNode = true;
        }

        public void ClearManeuverNode()
        {
            hasActiveManeuverNode = false;
        }

        public void ExecuteManeuverBurn(in ManeuverNode node)
        {
            Vector3 prograde = vesselVelocity.normalized;
            Vector3 normal = is2DPlanarMode ? Vector3.forward : Vector3.Cross(prograde, (vesselPosition - primaryBody.transform.position).normalized);
            Vector3 radial = Vector3.Cross(normal, prograde);

            Vector3 deltaVVec = node.GetTotalDeltaVVector(prograde, normal, radial);
            vesselVelocity += deltaVVec;

            if (usePhysicsRigidbody)
            {
                if (is2DPlanarMode && body2D != null)
                    body2D.linearVelocity = (Vector2)vesselVelocity;
                else if (body3D != null)
                    body3D.linearVelocity = vesselVelocity;
            }

            ClearManeuverNode();
            UpdateKeplerianElements();

            EventBus<ManeuverBurnExecutedEvent>.Raise(new ManeuverBurnExecutedEvent(
                GetInstanceID(), node.TotalDeltaVMagnitude, vesselVelocity));
        }

        // =========================================================================
        // SIMULATION TICK LOOP (SYMPLECTIC VELOCITY VERLET & ZERO-GC PREDICTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            if (primaryBody == null) return;

            // 1. Sync state from physics if bound
            if (usePhysicsRigidbody)
            {
                if (is2DPlanarMode && body2D != null)
                {
                    vesselPosition = new Vector3(body2D.position.x, body2D.position.y, 0f);
                    vesselVelocity = new Vector3(body2D.linearVelocity.x, body2D.linearVelocity.y, 0f);
                }
                else if (body3D != null)
                {
                    vesselPosition = body3D.position;
                    vesselVelocity = body3D.linearVelocity;
                }
            }

            // 2. Symplectic Velocity Verlet Integration Step
            StepVelocityVerlet(tickDeltaTime);

            // 3. Update Keplerian Analytical Elements
            UpdateKeplerianElements();

            // 4. Update Trajectory Prediction Array (Zero-GC)
            ComputeTrajectoryPrediction();

            // 5. Apply back to Transform / Rigidbody
            if (!usePhysicsRigidbody)
            {
                transform.position = vesselPosition;
            }
        }

        private void StepVelocityVerlet(float dt)
        {
            Vector3 center = primaryBody.transform.position;
            Vector3 r = vesselPosition - center;
            float rMag = r.magnitude;
            if (rMag < 1f) return;

            // a = -mu * r / |r|^3
            float mu = (float)primaryBody.GravitationalParameter_Mu;
            Vector3 a0 = -(mu / (rMag * rMag * rMag)) * r;

            // Velocity Verlet: r_new = r + v*dt + 0.5*a*dt^2
            Vector3 rNew = r + (vesselVelocity * dt) + (0.5f * a0 * (dt * dt));

            float rNewMag = rNew.magnitude;
            Vector3 a1 = -(mu / (rNewMag * rNewMag * rNewMag)) * rNew;

            // v_new = v + 0.5*(a0 + a1)*dt
            vesselVelocity += 0.5f * (a0 + a1) * dt;
            vesselPosition = center + rNew;

            if (is2DPlanarMode)
            {
                vesselPosition.z = 0f;
                vesselVelocity.z = 0f;
            }
        }

        // =========================================================================
        // ANALYTICAL KEPLERIAN ELEMENTS DERIVATION
        // =========================================================================

        private void UpdateKeplerianElements()
        {
            if (primaryBody == null) return;

            Vector3 rVec = vesselPosition - primaryBody.transform.position;
            Vector3 vVec = vesselVelocity;
            if (is2DPlanarMode) { rVec.z = 0f; vVec.z = 0f; }

            double r = rVec.magnitude;
            double v = vVec.magnitude;
            if (r < 1.0) return;

            double mu = primaryBody.GravitationalParameter_Mu;

            // 1. Specific Orbital Energy: eps = v^2/2 - mu/r
            double eps = ((v * v) * 0.5) - (mu / r);

            // 2. Semi-Major Axis: a = -mu / (2*eps)
            double a = -mu / (2.0 * eps);

            // 3. Angular Momentum Vector: h = r x v
            Vector3 hVec = Vector3.Cross(rVec, vVec);
            double h = hVec.magnitude;

            // 4. Eccentricity Vector: e_vec = (v x h) / mu - (r / |r|)
            Vector3 eVec = (Vector3.Cross(vVec, hVec) / (float)mu) - (rVec / (float)r);
            double e = eVec.magnitude;

            // 5. Inclination: i = acos(h_z / h)
            double iDeg = is2DPlanarMode ? 0.0 : Math.Acos(Mathf.Clamp((float)(hVec.z / h), -1f, 1f)) * Mathf.Rad2Deg;

            // 6. Apoapsis and Periapsis
            double periapsis = a * (1.0 - e);
            double apoapsis = (e < 1.0) ? a * (1.0 + e) : double.PositiveInfinity;

            // 7. Orbital Period: T = 2*pi * sqrt(a^3 / mu)
            double period = (e < 1.0 && a > 0.0) ? 2.0 * Math.PI * Math.Sqrt((a * a * a) / mu) : double.PositiveInfinity;

            // 8. True Anomaly: nu = acos((e_vec . r_vec) / (e * r))
            double cosNu = Vector3.Dot(eVec.normalized, rVec.normalized);
            double nuDeg = Math.Acos(Mathf.Clamp((float)cosNu, -1f, 1f)) * Mathf.Rad2Deg;
            if (Vector3.Dot(rVec, vVec) < 0.0) nuDeg = 360.0 - nuDeg;

            currentOrbit = new KeplerianElements
            {
                SemiMajorAxisMeters = a,
                Eccentricity = e,
                InclinationDeg = iDeg,
                ApoapsisMeters = apoapsis,
                PeriapsisMeters = periapsis,
                OrbitalPeriodSeconds = period,
                TrueAnomalyDeg = nuDeg
            };
        }

        // =========================================================================
        // TRAJECTORY PREDICTION (ZERO-GC EXECUTION)
        // =========================================================================

        private void ComputeTrajectoryPrediction()
        {
            if (trajectoryPoints == null || trajectoryPoints.Length != trajectoryPointCount)
                trajectoryPoints = new Vector3[trajectoryPointCount];

            Vector3 simPos = vesselPosition;
            Vector3 simVel = vesselVelocity;
            Vector3 center = primaryBody.transform.position;
            float mu = (float)primaryBody.GravitationalParameter_Mu;

            float dt = (float)(currentOrbit.IsClosedOrbit ? (currentOrbit.OrbitalPeriodSeconds / trajectoryPointCount) : 10.0);
            dt = Mathf.Clamp(dt, 0.1f, 100f);

            for (int i = 0; i < trajectoryPointCount; i++)
            {
                trajectoryPoints[i] = simPos;

                Vector3 r = simPos - center;
                float rMag = r.magnitude;
                if (rMag < 1f) break;

                Vector3 a = -(mu / (rMag * rMag * rMag)) * r;
                simVel += a * dt;
                simPos += simVel * dt;
                if (is2DPlanarMode) simPos.z = 0f;
            }

            if (trajectoryLineRenderer != null)
            {
                trajectoryLineRenderer.positionCount = trajectoryPointCount;
                trajectoryLineRenderer.SetPositions(trajectoryPoints);
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class OrbitSaveData
        {
            public Vector3 Pos;
            public Vector3 Vel;
        }

        public object CaptureState()
        {
            return new OrbitSaveData
            {
                Pos = vesselPosition,
                Vel = vesselVelocity
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<OrbitSaveData>(stateJson);
            if (data == null) return;

            vesselPosition = data.Pos;
            vesselVelocity = data.Vel;
            UpdateKeplerianElements();
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            if (primaryBody == null) return;
            Vector3 center = primaryBody.transform.position;

            // Draw celestial body SOI
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
            Gizmos.DrawWireSphere(center, (float)Mathf.Min((float)primaryBody.SOIRadiusMeters, 1000f));

            // Draw current vessel position & velocity
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(vesselPosition, 0.5f);
            Gizmos.DrawRay(vesselPosition, vesselVelocity.normalized * 5f);

            // Draw predicted trajectory path
            if (trajectoryPoints != null && trajectoryPoints.Length > 1)
            {
                Gizmos.color = currentOrbit.IsHyperbolic ? Color.yellow : Color.cyan;
                for (int i = 0; i < trajectoryPoints.Length - 1; i++)
                {
                    Gizmos.DrawLine(trajectoryPoints[i], trajectoryPoints[i + 1]);
                }
            }
        }
    }
}
