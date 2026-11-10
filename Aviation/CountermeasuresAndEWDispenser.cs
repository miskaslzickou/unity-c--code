using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Type of expendable defensive countermeasure.
/// </summary>
public enum CountermeasureType
{
    PyrotechnicFlare,
    DipoleChaffBundle,
    TowedRFDecoy
}

/// <summary>
/// Radar Warning Receiver (RWR) threat severity level.
/// </summary>
public enum RWRThreatLevel
{
    None,
    SurveillanceSearch,
    RadarTrackingLock,
    ActiveMissileLaunch
}

/// <summary>
/// Active deployed decoy tracked in the simulation without dynamic heap allocation.
/// </summary>
[System.Serializable]
public struct ActiveDecoy
{
    public bool IsActive;
    public CountermeasureType Type;
    public Vector3 Position;
    public Vector3 Velocity;
    public float AgeSeconds;
    public float MaxLifetimeSeconds;
    public float IRSignatureWatts;
    public float RadarCrossSectionM2;
}

/// <summary>
/// Threat contact tracked by the aircraft's Radar Warning Receiver.
/// </summary>
[System.Serializable]
public struct RWRThreatContact
{
    public int ThreatId;
    public RWRThreatLevel Level;
    public float RelativeAzimuthDeg; // 0..360 deg
    public float SignalStrength;
    public float TimeSinceLastPing;
}

/// <summary>
/// Event raised when countermeasures (flares or chaff) are dispensed.
/// </summary>
public readonly struct CountermeasureDispensedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly CountermeasureType Type;
    public readonly Vector3 DispensePosition;
    public readonly int RemainingCount;

    public CountermeasureDispensedEvent(int aircraftId, CountermeasureType type, Vector3 pos, int remaining)
    {
        AircraftId = aircraftId;
        Type = type;
        DispensePosition = pos;
        RemainingCount = remaining;
    }
}

/// <summary>
/// Event raised when the RWR detects radar tracking or an active missile launch.
/// </summary>
public readonly struct RWRThreatDetectedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly int ThreatSourceId;
    public readonly RWRThreatLevel ThreatLevel;
    public readonly float AzimuthDeg;

    public RWRThreatDetectedEvent(int aircraftId, int sourceId, RWRThreatLevel level, float azimuth)
    {
        AircraftId = aircraftId;
        ThreatSourceId = sourceId;
        ThreatLevel = level;
        AzimuthDeg = azimuth;
    }
}

/// <summary>
/// Airborne defensive countermeasures dispenser (Flares, Chaff, Towed Decoys)
/// and Radar Warning Receiver (RWR) electronic warfare suite.
/// Dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class CountermeasuresAndEWDispenser : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Audio Interop")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 145;
    [SerializeField] private ProceduralAudioSynthesizer audioSynth;

    [Header("Magazines & Dispenser")]
    [SerializeField] private int maxFlareCount = 60;
    [SerializeField] private int maxChaffCount = 60;
    [SerializeField] private Vector3 leftDispenserOffsetLocal = new Vector3(-1.2f, -0.2f, -2.5f);
    [SerializeField] private Vector3 rightDispenserOffsetLocal = new Vector3(1.2f, -0.2f, -2.5f);
    [SerializeField] private float flareEjectionVelocityM_s = 22f;
    [SerializeField] private float chaffEjectionVelocityM_s = 18f;

    [Header("Decoy Physical Characteristics")]
    [SerializeField] private float flareBurnDurationSeconds = 4.0f;
    [SerializeField] private float peakFlareIRWatts = 2800f;
    [SerializeField] private float chaffCloudDurationSeconds = 8.0f;
    [SerializeField] private float peakChaffRCSM2 = 85f;

    [Header("Salvo Program Tuning")]
    [SerializeField] private int burstCount = 4;
    [SerializeField] private float burstIntervalSeconds = 0.14f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Magazines
    public int CurrentFlares { get; private set; }
    public int CurrentChaff { get; private set; }

    // Pre-allocated active decoy pool
    private const int MAX_ACTIVE_DECOYS = 64;
    private readonly ActiveDecoy[] activeDecoys = new ActiveDecoy[MAX_ACTIVE_DECOYS];

    // RWR Threat Contacts
    private const int MAX_RWR_CONTACTS = 16;
    private readonly RWRThreatContact[] rwrContacts = new RWRThreatContact[MAX_RWR_CONTACTS];
    private int rwrContactCount = 0;

    // Salvo execution state
    private bool isSalvoRunning = false;
    private int salvoShotsRemaining = 0;
    private float salvoTimer = 0f;
    private CountermeasureType salvoType;

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "CountermeasuresEW_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        CurrentFlares = maxFlareCount;
        CurrentChaff = maxChaffCount;

        if (audioSynth == null) audioSynth = GetComponent<ProceduralAudioSynthesizer>();

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

    // =========================================================================
    // DISPENSER CONTROL APIS
    // =========================================================================

    public void DispenseSingle(CountermeasureType type)
    {
        if (type == CountermeasureType.PyrotechnicFlare && CurrentFlares > 0)
        {
            CurrentFlares--;
            SpawnDecoy(CountermeasureType.PyrotechnicFlare, leftDispenserOffsetLocal);
            EventBus<CountermeasureDispensedEvent>.Raise(new CountermeasureDispensedEvent(gameObject.GetInstanceID(), type, transform.position, CurrentFlares));
        }
        else if (type == CountermeasureType.DipoleChaffBundle && CurrentChaff > 0)
        {
            CurrentChaff--;
            SpawnDecoy(CountermeasureType.DipoleChaffBundle, rightDispenserOffsetLocal);
            EventBus<CountermeasureDispensedEvent>.Raise(new CountermeasureDispensedEvent(gameObject.GetInstanceID(), type, transform.position, CurrentChaff));
        }
    }

    public void TriggerSalvo(CountermeasureType type)
    {
        if (isSalvoRunning) return;
        isSalvoRunning = true;
        salvoType = type;
        salvoShotsRemaining = burstCount;
        salvoTimer = 0f;
    }

    private void SpawnDecoy(CountermeasureType type, Vector3 localOffset)
    {
        // Find inactive slot in pre-allocated decoy pool
        int freeSlot = -1;
        for (int i = 0; i < MAX_ACTIVE_DECOYS; i++)
        {
            if (!activeDecoys[i].IsActive)
            {
                freeSlot = i;
                break;
            }
        }
        if (freeSlot < 0) return; // Pool saturated

        Vector3 spawnWorld = transform.TransformPoint(localOffset);
        Vector3 ownVel = GetOwnVelocity();

        // Ejection direction: downward and outwards
        Vector3 ejectDir = defaultTo2D
            ? -(Vector3)transform.up + ((localOffset.x > 0 ? (Vector3)transform.right : -(Vector3)transform.right) * 0.5f)
            : -transform.up + ((localOffset.x > 0 ? transform.right : -transform.right) * 0.6f);

        float ejectSpeed = (type == CountermeasureType.PyrotechnicFlare) ? flareEjectionVelocityM_s : chaffEjectionVelocityM_s;
        Vector3 initialVel = ownVel + (ejectDir.normalized * ejectSpeed);

        activeDecoys[freeSlot] = new ActiveDecoy
        {
            IsActive = true,
            Type = type,
            Position = defaultTo2D ? new Vector3(spawnWorld.x, spawnWorld.y, 0f) : spawnWorld,
            Velocity = initialVel,
            AgeSeconds = 0f,
            MaxLifetimeSeconds = (type == CountermeasureType.PyrotechnicFlare) ? flareBurnDurationSeconds : chaffCloudDurationSeconds,
            IRSignatureWatts = (type == CountermeasureType.PyrotechnicFlare) ? peakFlareIRWatts : 0f,
            RadarCrossSectionM2 = (type == CountermeasureType.DipoleChaffBundle) ? peakChaffRCSM2 : 0.05f
        };
    }

    // =========================================================================
    // RWR THREAT DETECTION & INGESTION
    // =========================================================================

    public void ReportRadarThreat(int sourceId, Vector3 emitterPosition, RWRThreatLevel level)
    {
        Vector3 toEmitter = emitterPosition - transform.position;
        if (defaultTo2D) toEmitter.z = 0f;

        Vector3 fwd = defaultTo2D ? (Vector3)transform.up : transform.forward;
        float azimuth = defaultTo2D ? Vector2.SignedAngle(fwd, toEmitter) : Vector3.SignedAngle(fwd, toEmitter, Vector3.up);
        if (azimuth < 0f) azimuth += 360f;

        // Update or insert into RWR table
        bool found = false;
        for (int i = 0; i < rwrContactCount; i++)
        {
            if (rwrContacts[i].ThreatId == sourceId)
            {
                rwrContacts[i].Level = level;
                rwrContacts[i].RelativeAzimuthDeg = azimuth;
                rwrContacts[i].TimeSinceLastPing = 0f;
                found = true;
                break;
            }
        }

        if (!found && rwrContactCount < MAX_RWR_CONTACTS)
        {
            rwrContacts[rwrContactCount++] = new RWRThreatContact
            {
                ThreatId = sourceId,
                Level = level,
                RelativeAzimuthDeg = azimuth,
                SignalStrength = 1.0f,
                TimeSinceLastPing = 0f
            };
        }

        // Trigger threat audio warning
        if (audioSynth != null)
        {
            if (level == RWRThreatLevel.ActiveMissileLaunch)
            {
                audioSynth.TriggerRWRWarning(missileHardLock: true);
            }
            else if (level == RWRThreatLevel.RadarTrackingLock)
            {
                audioSynth.TriggerRWRWarning(missileHardLock: false);
            }
        }

        EventBus<RWRThreatDetectedEvent>.Raise(new RWRThreatDetectedEvent(gameObject.GetInstanceID(), sourceId, level, azimuth));
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        // 1. Process Salvo sequence
        if (isSalvoRunning)
        {
            salvoTimer += deltaTime;
            if (salvoTimer >= burstIntervalSeconds)
            {
                salvoTimer = 0f;
                DispenseSingle(salvoType);
                salvoShotsRemaining--;
                if (salvoShotsRemaining <= 0)
                {
                    isSalvoRunning = false;
                }
            }
        }

        // 2. Simulate Ballistics and Signatures of Active Decoys
        SimulateDecoyPhysics(deltaTime);

        // 3. Update RWR Contact persistence
        UpdateRWRTimeouts(deltaTime);
    }

    private void SimulateDecoyPhysics(float dt)
    {
        for (int i = 0; i < MAX_ACTIVE_DECOYS; i++)
        {
            if (!activeDecoys[i].IsActive) continue;

            activeDecoys[i].AgeSeconds += dt;
            if (activeDecoys[i].AgeSeconds >= activeDecoys[i].MaxLifetimeSeconds)
            {
                activeDecoys[i].IsActive = false;
                continue;
            }

            // High aerodynamic drag deceleration
            float dragMultiplier = (activeDecoys[i].Type == CountermeasureType.DipoleChaffBundle) ? 1.8f : 0.6f;
            Vector3 vel = activeDecoys[i].Velocity;
            Vector3 dragForce = -vel.normalized * (vel.sqrMagnitude * dragMultiplier * dt);
            Vector3 gravity = Physics.gravity * dt;

            activeDecoys[i].Velocity += (dragForce + gravity);
            activeDecoys[i].Position += activeDecoys[i].Velocity * dt;

            // Decay thermal / radar bloom intensity over lifetime
            float lifeFraction = activeDecoys[i].AgeSeconds / activeDecoys[i].MaxLifetimeSeconds;
            if (activeDecoys[i].Type == CountermeasureType.PyrotechnicFlare)
            {
                activeDecoys[i].IRSignatureWatts = Mathf.Lerp(peakFlareIRWatts, 100f, lifeFraction);
            }
            else if (activeDecoys[i].Type == CountermeasureType.DipoleChaffBundle)
            {
                activeDecoys[i].RadarCrossSectionM2 = Mathf.Lerp(peakChaffRCSM2, 2f, lifeFraction);
            }
        }
    }

    private void UpdateRWRTimeouts(float dt)
    {
        for (int i = rwrContactCount - 1; i >= 0; i--)
        {
            rwrContacts[i].TimeSinceLastPing += dt;
            if (rwrContacts[i].TimeSinceLastPing > 3.5f)
            {
                // Remove expired radar contact
                rwrContacts[i] = rwrContacts[rwrContactCount - 1];
                rwrContactCount--;
            }
        }
    }

    private Vector3 GetOwnVelocity()
    {
        var rb3 = GetComponent<Rigidbody>();
        if (!defaultTo2D && rb3 != null) return rb3.linearVelocity;

        var rb2 = GetComponent<Rigidbody2D>();
        if (defaultTo2D && rb2 != null) return new Vector3(rb2.linearVelocity.x, rb2.linearVelocity.y, 0f);

        return Vector3.zero;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public int Flares;
        public int Chaff;
    }

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Flares = CurrentFlares,
            Chaff = CurrentChaff
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        CurrentFlares = container.Flares;
        CurrentChaff = container.Chaff;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Active Decoys
        for (int i = 0; i < MAX_ACTIVE_DECOYS; i++)
        {
            if (!activeDecoys[i].IsActive) continue;

            if (activeDecoys[i].Type == CountermeasureType.PyrotechnicFlare)
            {
                Gizmos.color = Color.Lerp(Color.yellow, Color.red, activeDecoys[i].AgeSeconds / activeDecoys[i].MaxLifetimeSeconds);
                Gizmos.DrawSphere(activeDecoys[i].Position, 0.4f);
            }
            else
            {
                Gizmos.color = new Color(0.8f, 0.8f, 0.8f, 0.4f);
                Gizmos.DrawWireSphere(activeDecoys[i].Position, 1.2f);
            }
        }

        // Draw RWR Radar Threat Azimuth Rose
        Vector3 rwrOrigin = transform.position + (Vector3.up * 1.5f);
        if (defaultTo2D) rwrOrigin = transform.position;

        Gizmos.color = new Color(0.2f, 1f, 0.2f, 0.3f);
        Gizmos.DrawWireSphere(rwrOrigin, 1.2f);

        for (int i = 0; i < rwrContactCount; i++)
        {
            RWRThreatContact c = rwrContacts[i];
            Quaternion rot = defaultTo2D
                ? Quaternion.Euler(0f, 0f, -c.RelativeAzimuthDeg)
                : Quaternion.Euler(0f, c.RelativeAzimuthDeg, 0f);

            Vector3 fwd = defaultTo2D ? (Vector3)transform.up : transform.forward;
            Vector3 threatRay = rot * fwd;

            Gizmos.color = (c.Level == RWRThreatLevel.ActiveMissileLaunch) ? Color.red : Color.yellow;
            Gizmos.DrawRay(rwrOrigin, threatRay * 1.4f);
        }
    }
}
