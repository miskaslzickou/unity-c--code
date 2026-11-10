using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.PhysicsAndCombat;

namespace GameSystems.Forensics
{
    // =========================================================================
    // 1. EVIDENCE ENUMS & ZERO-GC STRUCTS
    // =========================================================================

    public enum EvidenceType : byte
    {
        Fingerprint,
        FootprintShoeprint,
        SpentCartridgeCasing,
        BulletImpactTrajectory,
        BloodSplatterDNA,
        CCTVSighting
    }

    public enum BloodType : byte
    {
        O_Pos, O_Neg,
        A_Pos, A_Neg,
        B_Pos, B_Neg,
        AB_Pos, AB_Neg,
        SyntheticAndroid
    }

    [Serializable]
    public struct SuspectProfile
    {
        public int SuspectId;
        public string FullName;
        public int FingerprintWhorlHash;
        public int DNASequenceHash;
        public BloodType Blood;
        public int ShoeSize;
        public string ShoeTreadPattern;
    }

    [Serializable]
    public class EvidenceRecord
    {
        public int EvidenceId;
        public EvidenceType Type;
        public Vector3 WorldPosition;
        public Vector3 SurfaceNormal;
        public float Timestamp;
        [Range(0f, 1f)] public float DegradationPercent = 0f; // 1.0 = completely degraded/unusable
        public bool IsContaminated = false;

        // Specific forensic identifiers
        public int FingerprintHash;
        public int DNAHash;
        public BloodType Blood;
        public int ShoeSize;
        public string ShoeTread;
        public float CaliberMm;
        public Vector3 BulletTrajectoryVector;
        public string CCTVCapturedDescription;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct EvidenceDepositedEvent : IGameEvent
    {
        public readonly int EvidenceId;
        public readonly EvidenceType Type;
        public readonly Vector3 Location;

        public EvidenceDepositedEvent(int id, EvidenceType type, Vector3 loc)
        {
            EvidenceId = id;
            Type = type;
            Location = loc;
        }
    }

    public readonly struct EvidenceAnalyzedEvent : IGameEvent
    {
        public readonly int EvidenceId;
        public readonly int MatchedSuspectId;
        public readonly float MatchConfidence;

        public EvidenceAnalyzedEvent(int id, int suspectId, float conf)
        {
            EvidenceId = id;
            MatchedSuspectId = suspectId;
            MatchConfidence = conf;
        }
    }

    // =========================================================================
    // 3. MASTER CRIME SCENE & FORENSICS TRACKER (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Procedural forensic evidence deposition, scene investigation, and suspect profiling system.
    /// Records spatio-temporal physical evidence (fingerprints, shoeprints, shell casings, bullet trajectories,
    /// DNA blood splatters, and CCTV sightings) with progressive environmental degradation.
    /// Interconnects directly with BallisticsAndPenetrationSolver to record ballistic evidence automatically.
    /// Compatible with both 3D and 2D spatial coordinates.
    /// </summary>
    [DisallowMultipleComponent]
    public class CrimeSceneAndForensicsTracker : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static CrimeSceneAndForensicsTracker Instance { get; private set; }

        [Header("Environmental Degradation")]
        [Tooltip("Degradation rate per minute for outdoor evidence exposed to rain/wind")]
        [SerializeField, Range(0.01f, 0.5f)] private float outdoorDegradationRatePerMin = 0.08f;
        [Tooltip("Degradation rate per minute for indoor preserved evidence")]
        [SerializeField, Range(0.001f, 0.1f)] private float indoorDegradationRatePerMin = 0.005f;
        [SerializeField] private bool isOutdoorEnvironment = false;

        [Header("Spatial Mode")]
        [SerializeField] private bool is2DMode = false;

        [Header("Tracked Evidence Database")]
        [SerializeField] private List<EvidenceRecord> activeEvidence = new List<EvidenceRecord>(256);
        [SerializeField] private List<SuspectProfile> knownSuspects = new List<SuspectProfile>(16);

        private int nextEvidenceId = 1;

        public string SaveKey => "CrimeSceneAndForensicsTracker";
        public int TotalEvidenceCount => activeEvidence.Count;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, TickChannel.Normal);

            // Subscribe to ballistic impacts to deposit shell/bullet trajectories
            EventBus<ArmorPenetrationEvent>.Subscribe(OnBallisticImpact);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, TickChannel.Normal);

            EventBus<ArmorPenetrationEvent>.Unsubscribe(OnBallisticImpact);
        }

        // =========================================================================
        // PUBLIC API: EVIDENCE DEPOSITION & INVESTIGATION
        // =========================================================================

        public EvidenceRecord DepositFingerprint(Vector3 position, Vector3 normal, int fingerprintHash)
        {
            var record = new EvidenceRecord
            {
                EvidenceId = nextEvidenceId++,
                Type = EvidenceType.Fingerprint,
                WorldPosition = position,
                SurfaceNormal = normal,
                Timestamp = Time.time,
                FingerprintHash = fingerprintHash
            };
            activeEvidence.Add(record);
            EventBus<EvidenceDepositedEvent>.Raise(new EvidenceDepositedEvent(record.EvidenceId, record.Type, position));
            return record;
        }

        public EvidenceRecord DepositShoeprint(Vector3 position, Vector3 forwardDir, int shoeSize, string treadPattern)
        {
            var record = new EvidenceRecord
            {
                EvidenceId = nextEvidenceId++,
                Type = EvidenceType.FootprintShoeprint,
                WorldPosition = position,
                SurfaceNormal = Vector3.up,
                Timestamp = Time.time,
                ShoeSize = shoeSize,
                ShoeTread = treadPattern,
                BulletTrajectoryVector = forwardDir.normalized
            };
            activeEvidence.Add(record);
            EventBus<EvidenceDepositedEvent>.Raise(new EvidenceDepositedEvent(record.EvidenceId, record.Type, position));
            return record;
        }

        public EvidenceRecord DepositBloodSplatter(Vector3 position, BloodType blood, int dnaHash)
        {
            var record = new EvidenceRecord
            {
                EvidenceId = nextEvidenceId++,
                Type = EvidenceType.BloodSplatterDNA,
                WorldPosition = position,
                SurfaceNormal = Vector3.up,
                Timestamp = Time.time,
                Blood = blood,
                DNAHash = dnaHash
            };
            activeEvidence.Add(record);
            EventBus<EvidenceDepositedEvent>.Raise(new EvidenceDepositedEvent(record.EvidenceId, record.Type, position));
            return record;
        }

        public EvidenceRecord DepositBallisticEvidence(Vector3 impactPoint, Vector3 trajectoryDir, float caliberMm)
        {
            var record = new EvidenceRecord
            {
                EvidenceId = nextEvidenceId++,
                Type = EvidenceType.BulletImpactTrajectory,
                WorldPosition = impactPoint,
                SurfaceNormal = -trajectoryDir.normalized,
                Timestamp = Time.time,
                CaliberMm = caliberMm,
                BulletTrajectoryVector = trajectoryDir.normalized
            };
            activeEvidence.Add(record);
            EventBus<EvidenceDepositedEvent>.Raise(new EvidenceDepositedEvent(record.EvidenceId, record.Type, impactPoint));
            return record;
        }

        public void QueryEvidenceInRadius(Vector3 center, float radius, List<EvidenceRecord> resultsBuffer)
        {
            if (resultsBuffer == null) return;
            resultsBuffer.Clear();

            float sqrRadius = radius * radius;
            for (int i = 0; i < activeEvidence.Count; i++)
            {
                var ev = activeEvidence[i];
                float sqrDist = is2DMode ?
                    (new Vector2(ev.WorldPosition.x - center.x, ev.WorldPosition.y - center.y)).sqrMagnitude :
                    (ev.WorldPosition - center).sqrMagnitude;

                if (sqrDist <= sqrRadius)
                {
                    resultsBuffer.Add(ev);
                }
            }
        }

        public float MatchEvidenceAgainstSuspect(EvidenceRecord ev, in SuspectProfile suspect)
        {
            if (ev.DegradationPercent >= 0.95f) return 0f; // Evidence destroyed

            float confidence = 0f;
            float degradationWeight = 1f - ev.DegradationPercent;

            switch (ev.Type)
            {
                case EvidenceType.Fingerprint:
                    if (ev.FingerprintHash == suspect.FingerprintWhorlHash)
                        confidence = 0.99f;
                    break;

                case EvidenceType.BloodSplatterDNA:
                    if (ev.DNAHash == suspect.DNASequenceHash)
                        confidence = 0.999f;
                    else if (ev.Blood == suspect.Blood)
                        confidence = 0.35f;
                    break;

                case EvidenceType.FootprintShoeprint:
                    if (ev.ShoeSize == suspect.ShoeSize && string.Equals(ev.ShoeTread, suspect.ShoeTreadPattern, StringComparison.OrdinalIgnoreCase))
                        confidence = 0.75f;
                    else if (ev.ShoeSize == suspect.ShoeSize)
                        confidence = 0.30f;
                    break;
            }

            confidence *= degradationWeight;

            if (confidence > 0.5f)
            {
                EventBus<EvidenceAnalyzedEvent>.Raise(new EvidenceAnalyzedEvent(ev.EvidenceId, suspect.SuspectId, confidence));
            }

            return confidence;
        }

        // =========================================================================
        // BALLISTICS EVENT HOOK
        // =========================================================================

        private void OnBallisticImpact(in ArmorPenetrationEvent evt)
        {
            DepositBallisticEvidence(evt.ImpactPoint, -evt.Normal, evt.CaliberMm);
        }

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EVIDENCE DEGRADATION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            float ratePerSec = (isOutdoorEnvironment ? outdoorDegradationRatePerMin : indoorDegradationRatePerMin) / 60f;

            for (int i = activeEvidence.Count - 1; i >= 0; i--)
            {
                var ev = activeEvidence[i];
                ev.DegradationPercent = Mathf.Min(1.0f, ev.DegradationPercent + (ratePerSec * tickDeltaTime));

                // Completely degraded evidence fades out of active scene
                if (ev.DegradationPercent >= 1.0f)
                {
                    activeEvidence.RemoveAt(i);
                }
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class ForensicsSaveData
        {
            public List<EvidenceRecord> Evidence = new List<EvidenceRecord>();
        }

        public object CaptureState()
        {
            return new ForensicsSaveData
            {
                Evidence = new List<EvidenceRecord>(activeEvidence)
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<ForensicsSaveData>(stateJson);
            if (data == null || data.Evidence == null) return;

            activeEvidence.Clear();
            activeEvidence.AddRange(data.Evidence);
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            for (int i = 0; i < activeEvidence.Count; i++)
            {
                var ev = activeEvidence[i];
                Color c = ev.Type switch
                {
                    EvidenceType.Fingerprint => Color.cyan,
                    EvidenceType.FootprintShoeprint => Color.yellow,
                    EvidenceType.SpentCartridgeCasing => Color.white,
                    EvidenceType.BulletImpactTrajectory => Color.magenta,
                    EvidenceType.BloodSplatterDNA => Color.red,
                    EvidenceType.CCTVSighting => Color.green,
                    _ => Color.gray
                };

                Gizmos.color = c;
                if (is2DMode)
                    Gizmos.DrawWireSphere(ev.WorldPosition, 0.2f);
                else
                    Gizmos.DrawWireSphere(ev.WorldPosition, 0.2f);

                if (ev.Type == EvidenceType.BulletImpactTrajectory && ev.BulletTrajectoryVector.sqrMagnitude > 0.01f)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawRay(ev.WorldPosition, -ev.BulletTrajectoryVector * 2.5f);
                }
            }
        }
    }
}
