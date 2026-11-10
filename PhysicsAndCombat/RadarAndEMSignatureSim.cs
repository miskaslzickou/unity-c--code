using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.Vision;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. RADAR & EM SIGNATURE ENUMS AND STRUCTS
    // =========================================================================

    public enum RadarSensorMode
    {
        StandbyPassive,
        ActiveSearchSweep,
        TrackWhileScan,
        SingleTargetTrackLocked
    }

    public enum JammingTechnique
    {
        None,
        BarrageNoise,
        RangeGatePullOffDeception,
        ChaffCloud
    }

    /// <summary>
    /// Aspect-dependent Radar Cross Section (RCS in square meters) and IR Thermal signature.
    /// </summary>
    [Serializable]
    public class TargetEMSignature : MonoBehaviour
    {
        [Header("Radar Cross Section (m^2)")]
        [Tooltip("Frontal aspect RCS (e.g. stealth fighter: 0.001 m^2, airliner: 50 m^2)")]
        public float NoseRCS_m2 = 1.0f;
        [Tooltip("Broadside aspect RCS (beam)")]
        public float BeamRCS_m2 = 10.0f;
        [Tooltip("Rear aspect RCS (tail)")]
        public float TailRCS_m2 = 3.0f;

        [Header("Infrared (IR) Thermal Signature")]
        [Tooltip("Thermal radiance in kW/sr (afterburners: 500+, gliders: 0.1)")]
        public float InfraredRadianceKW = 25f;
        public bool IsAfterburnerActive = false;

        [Header("Active EM Radiation")]
        public bool IsEmittingRadar = false;
        public float EmissionFrequencyMHz = 9500f;
        public float EmissionPowerWatts = 5000f;

        public float GetAspectRCS(Vector3 lookDirectionToTarget)
        {
            // Vector from target towards the radar
            Vector3 targetForward = transform.forward;
            float dot = Vector3.Dot(targetForward, lookDirectionToTarget); // 1 = looking at tail, -1 = looking at nose

            if (dot < -0.5f)
            {
                // Nose aspect
                return NoseRCS_m2;
            }
            else if (dot > 0.5f)
            {
                // Tail aspect
                return TailRCS_m2;
            }
            else
            {
                // Beam / side aspect
                return BeamRCS_m2;
            }
        }
    }

    [Serializable]
    public struct RadarContact
    {
        public int TargetInstanceId;
        public Vector3 EstimatedPosition;
        public Vector3 EstimatedVelocity;
        public float SignalToNoiseRatio_dB;
        public float RangeMeters;
        public float BearingDeg;
        public float RadialVelocityM_s;
        public bool IsGhostDecoy;
        public float LastDetectedTime;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct RadarContactDetectedEvent : IGameEvent
    {
        public readonly int RadarId;
        public readonly int TargetId;
        public readonly Vector3 Position;
        public readonly float Range;
        public readonly float SNR_dB;

        public RadarContactDetectedEvent(int radarId, int targetId, Vector3 pos, float range, float snr)
        {
            RadarId = radarId;
            TargetId = targetId;
            Position = pos;
            Range = range;
            SNR_dB = snr;
        }
    }

    public readonly struct RadarLockAcquiredEvent : IGameEvent
    {
        public readonly int RadarId;
        public readonly int TargetId;
        public readonly Vector3 TargetPosition;

        public RadarLockAcquiredEvent(int radarId, int targetId, Vector3 pos)
        {
            RadarId = radarId;
            TargetId = targetId;
            TargetPosition = pos;
        }
    }

    public readonly struct RadarLockLostEvent : IGameEvent
    {
        public readonly int RadarId;
        public readonly int TargetId;

        public RadarLockLostEvent(int radarId, int targetId)
        {
            RadarId = radarId;
            TargetId = targetId;
        }
    }

    // =========================================================================
    // 3. MASTER RADAR & EM SIGNATURE SIMULATOR (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Electromagnetic spectrum, Radar Cross-Section (RCS), Infrared (IR), and Electronic Warfare simulator.
    /// Computes physical radar detection using the Radar Range Equation, accounting for aspect RCS,
    /// terrain line-of-sight occlusion, Doppler clutter, noise jamming, and deception ghost targets.
    /// Implements IVisionEmitter to automatically reveal Fog of War in FogOfWarManager.
    /// Feeds locked target coordinates to HomingProjectile seekers.
    /// Compatible with both 3D and 2D spatial pipelines.
    /// </summary>
    [DisallowMultipleComponent]
    public class RadarAndEMSignatureSim : MonoBehaviour, ITickable, ISaveableSystem, IVisionEmitter
    {
        public static RadarAndEMSignatureSim Instance { get; private set; }

        [Header("Radar Transmitter Hardware")]
        [SerializeField] private RadarSensorMode currentMode = RadarSensorMode.ActiveSearchSweep;
        [Tooltip("Operating frequency in MHz (e.g. X-band: 9500 MHz, L-band: 1250 MHz)")]
        [SerializeField] private float radarFrequencyMHz = 9500f;
        [Tooltip("Peak transmitter pulse power in Watts")]
        [SerializeField, Min(100f)] private float transmitterPowerWatts = 50000f;
        [Tooltip("Antenna gain in dBi (directional main-lobe gain)")]
        [SerializeField, Range(10f, 45f)] private float antennaGain_dBi = 34f;
        [Tooltip("Minimum detectable signal threshold (dBm)")]
        [SerializeField] private float receiverSensitivity_dBm = -105f;

        [Header("Scan & Beam Geometry")]
        [Tooltip("Horizontal azimuth scan sector (+/- degrees from boresight)")]
        [Range(10f, 180f)] [SerializeField] private float azimuthScanAngleDeg = 60f;
        [Tooltip("Vertical elevation scan angle (+/- degrees from boresight)")]
        [Range(5f, 90f)] [SerializeField] private float elevationScanAngleDeg = 30f;
        [Tooltip("Antenna mechanical/electronic scan sweep speed in deg/second")]
        [SerializeField] private float scanSpeedDegPerSec = 120f;
        [Tooltip("Maximum instrumented display range in meters")]
        [SerializeField] private float maxInstrumentedRangeMeters = 25000f;

        [Header("Terrain Masking & Clutter")]
        [Tooltip("Layer mask for terrain, hills, mountains, and obstacles blocking radar beams")]
        [SerializeField] private LayerMask terrainOcclusionMask = ~0;
        [Tooltip("Minimum Doppler speed in m/s required to separate target from ground clutter (MTI filter)")]
        [SerializeField] private float groundClutterFilterSpeedM_s = 15f;

        [Header("Electronic Warfare (Jamming)")]
        [SerializeField] private JammingTechnique activeJamming = JammingTechnique.None;
        [Tooltip("Jamming noise floor increase in dB")]
        [SerializeField] private float receivedJammingNoise_dB = 0f;

        [Header("Spatial Mode")]
        [SerializeField] private bool is2DMode = false;

        [Header("Tracked Contacts & Locked Target")]
        [SerializeField] private Transform lockedTargetTransform;
        [SerializeField] private List<RadarContact> activeContacts = new List<RadarContact>(16);

        // Pre-allocated static buffer for discovered targets in scene
        private static readonly List<TargetEMSignature> sceneSignatures = new List<TargetEMSignature>(64);

        private float currentBeamAzimuthDeg = 0f;
        private float currentBeamDir = 1f;
        private const float SpeedOfLight = 299792458f;

        public string SaveKey => $"Radar_{gameObject.name}_{GetInstanceID()}";
        public RadarSensorMode Mode => currentMode;
        public Transform LockedTarget => lockedTargetTransform;
        public int ContactCount => activeContacts.Count;

        // =========================================================================
        // IVisionEmitter IMPLEMENTATION (FOG OF WAR INTEGRATION)
        // =========================================================================

        public Vector3 Position => transform.position;
        public float SightRadius => maxInstrumentedRangeMeters;
        public int HeightAdvantage => Mathf.RoundToInt(transform.position.y / 10f);
        public bool IsVisionActive => currentMode != RadarSensorMode.StandbyPassive;

        // =========================================================================
        // INITIALIZATION
        // =========================================================================

        private void Awake()
        {
            if (Instance == null) Instance = this;
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

        public static void RegisterTargetSignature(TargetEMSignature sig)
        {
            if (sig != null && !sceneSignatures.Contains(sig))
                sceneSignatures.Add(sig);
        }

        public static void UnregisterTargetSignature(TargetEMSignature sig)
        {
            if (sig != null)
                sceneSignatures.Remove(sig);
        }

        public void AcquireHardLock(Transform target)
        {
            lockedTargetTransform = target;
            currentMode = RadarSensorMode.SingleTargetTrackLocked;
            EventBus<RadarLockAcquiredEvent>.Raise(new RadarLockAcquiredEvent(
                GetInstanceID(), target.gameObject.GetInstanceID(), target.position));
        }

        public void BreakLock()
        {
            if (lockedTargetTransform != null)
            {
                EventBus<RadarLockLostEvent>.Raise(new RadarLockLostEvent(
                    GetInstanceID(), lockedTargetTransform.gameObject.GetInstanceID()));
                lockedTargetTransform = null;
            }
            currentMode = RadarSensorMode.ActiveSearchSweep;
        }

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EXECUTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            // 1. Sweep Radar Beam
            if (currentMode != RadarSensorMode.SingleTargetTrackLocked)
            {
                currentBeamAzimuthDeg += scanSpeedDegPerSec * currentBeamDir * tickDeltaTime;
                if (Mathf.Abs(currentBeamAzimuthDeg) >= azimuthScanAngleDeg)
                {
                    currentBeamAzimuthDeg = Mathf.Sign(currentBeamAzimuthDeg) * azimuthScanAngleDeg;
                    currentBeamDir = -currentBeamDir; // Reverse sweep
                }
            }

            // 2. Clear old/decayed contacts
            float currentTime = Time.time;
            for (int i = activeContacts.Count - 1; i >= 0; i--)
            {
                if (currentTime - activeContacts[i].LastDetectedTime > 2.5f)
                {
                    activeContacts.RemoveAt(i);
                }
            }

            // 3. Evaluate detection across all EM targets in range
            Vector3 radarPos = transform.position;
            Vector3 radarForward = is2DMode ? transform.right : transform.forward;

            for (int i = 0; i < sceneSignatures.Count; i++)
            {
                TargetEMSignature targetSig = sceneSignatures[i];
                if (targetSig == null || targetSig.gameObject == gameObject) continue;

                Vector3 targetPos = targetSig.transform.position;
                Vector3 toTarget = targetPos - radarPos;
                float range = toTarget.magnitude;

                if (range > maxInstrumentedRangeMeters || range < 50f)
                    continue;

                Vector3 toTargetDir = toTarget / range;

                // Beam Azimuth and Elevation check
                float angleFromBoresight = Vector3.Angle(radarForward, toTargetDir);
                if (angleFromBoresight > azimuthScanAngleDeg)
                    continue;

                // 3D elevation check
                if (!is2DMode)
                {
                    float elevationAngle = Mathf.Abs(Mathf.Asin(Mathf.Clamp(toTargetDir.y, -1f, 1f)) * Mathf.Rad2Deg);
                    if (elevationAngle > elevationScanAngleDeg)
                        continue;
                }

                // Doppler ground clutter filtering for low-flying targets
                var targetRb = targetSig.GetComponent<Rigidbody>();
                float targetSpeed = targetRb != null ? targetRb.linearVelocity.magnitude : 20f;
                if (targetPos.y < 50f && targetSpeed < groundClutterFilterSpeedM_s)
                    continue;

                // Terrain Line-of-Sight Masking (Shadowcasting behind mountains)
                if (IsLineOfSightBlocked(radarPos, targetPos))
                    continue;

                // 4. Physical Radar Range Equation:
                // Pr = (Pt * G^2 * lambda^2 * sigma) / ((4*pi)^3 * R^4)
                float wavelengthMeters = SpeedOfLight / (radarFrequencyMHz * 1000000f);
                float rcsSigma = targetSig.GetAspectRCS(toTargetDir);

                // Convert G from dBi to linear factor
                float gLinear = Mathf.Pow(10f, antennaGain_dBi / 10f);

                float numerator = transmitterPowerWatts * (gLinear * gLinear) * (wavelengthMeters * wavelengthMeters) * rcsSigma;
                float fourPiCube = 1984.4f; // (4 * pi)^3
                float r4 = range * range * range * range;
                float denominator = fourPiCube * r4;

                float receivedPowerWatts = numerator / denominator;
                float receivedPower_dBm = 10f * Mathf.Log10(Mathf.Max(1e-15f, receivedPowerWatts * 1000f));

                // Add Jamming noise interference
                float effectiveSensitivity = receiverSensitivity_dBm + receivedJammingNoise_dB;
                float snr_dB = receivedPower_dBm - effectiveSensitivity;

                if (snr_dB >= 3.0f) // Detection threshold: SNR >= 3 dB
                {
                    UpdateContact(targetSig, targetPos, range, angleFromBoresight, snr_dB);

                    EventBus<RadarContactDetectedEvent>.Raise(new RadarContactDetectedEvent(
                        GetInstanceID(), targetSig.gameObject.GetInstanceID(), targetPos, range, snr_dB));
                }
            }

            // 5. Deception Jamming: Inject Ghost Decoys if active
            if (activeJamming == JammingTechnique.RangeGatePullOffDeception)
            {
                InjectGhostDecoy(radarPos, radarForward);
            }
        }

        private bool IsLineOfSightBlocked(Vector3 from, Vector3 to)
        {
            if (is2DMode)
            {
                return Physics2D.Linecast(new Vector2(from.x, from.y), new Vector2(to.x, to.y), terrainOcclusionMask);
            }
            return Physics.Linecast(from, to, terrainOcclusionMask);
        }

        private void UpdateContact(TargetEMSignature sig, Vector3 pos, float range, float bearing, float snr)
        {
            int targetId = sig.gameObject.GetInstanceID();
            for (int i = 0; i < activeContacts.Count; i++)
            {
                if (activeContacts[i].TargetInstanceId == targetId)
                {
                    activeContacts[i] = new RadarContact
                    {
                        TargetInstanceId = targetId,
                        EstimatedPosition = pos,
                        RangeMeters = range,
                        BearingDeg = bearing,
                        SignalToNoiseRatio_dB = snr,
                        IsGhostDecoy = false,
                        LastDetectedTime = Time.time
                    };
                    return;
                }
            }

            activeContacts.Add(new RadarContact
            {
                TargetInstanceId = targetId,
                EstimatedPosition = pos,
                RangeMeters = range,
                BearingDeg = bearing,
                SignalToNoiseRatio_dB = snr,
                IsGhostDecoy = false,
                LastDetectedTime = Time.time
            });
        }

        private void InjectGhostDecoy(Vector3 radarPos, Vector3 forward)
        {
            if (activeContacts.Count < 10)
            {
                Vector3 ghostPos = radarPos + (forward * UnityEngine.Random.Range(3000f, 8000f)) + (UnityEngine.Random.insideUnitSphere * 500f);
                activeContacts.Add(new RadarContact
                {
                    TargetInstanceId = -999,
                    EstimatedPosition = ghostPos,
                    RangeMeters = Vector3.Distance(radarPos, ghostPos),
                    BearingDeg = 15f,
                    SignalToNoiseRatio_dB = 8f,
                    IsGhostDecoy = true,
                    LastDetectedTime = Time.time
                });
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class RadarSaveData
        {
            public int Mode;
            public float Azimuth;
        }

        public object CaptureState()
        {
            return new RadarSaveData
            {
                Mode = (int)currentMode,
                Azimuth = currentBeamAzimuthDeg
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<RadarSaveData>(stateJson);
            if (data == null) return;

            currentMode = (RadarSensorMode)data.Mode;
            currentBeamAzimuthDeg = data.Azimuth;
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            Vector3 myPos = transform.position;
            Vector3 forward = is2DMode ? transform.right : transform.forward;

            // Draw Radar Scan Cone
            Gizmos.color = currentMode == RadarSensorMode.SingleTargetTrackLocked ? Color.red : new Color(0f, 1f, 0.4f, 0.2f);
            if (is2DMode)
            {
                Gizmos.DrawWireSphere(myPos, Mathf.Min(maxInstrumentedRangeMeters, 50f));
            }
            else
            {
                Gizmos.DrawWireSphere(myPos, Mathf.Min(maxInstrumentedRangeMeters, 50f));
            }

            // Draw Active Contacts
            for (int i = 0; i < activeContacts.Count; i++)
            {
                var c = activeContacts[i];
                Gizmos.color = c.IsGhostDecoy ? Color.yellow : (c.SignalToNoiseRatio_dB > 10f ? Color.red : Color.cyan);
                Gizmos.DrawWireCube(c.EstimatedPosition, Vector3.one * 1.5f);
                Gizmos.DrawLine(myPos, c.EstimatedPosition);
            }

            // Draw Hard Lock line
            if (lockedTargetTransform != null)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawLine(myPos, lockedTargetTransform.position);
            }
        }
    }
}
