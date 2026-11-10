using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Operational weapon targeting mode of the Fire Control Computer.
/// </summary>
public enum FireControlMode
{
    AirToAirGunLead_EEGS,
    AirToGroundBomb_CCIP,
    AirToGroundBomb_CCRP,
    MissileSeekerLock_Fox2
}

/// <summary>
/// Event raised when the Fire Control Computer acquires or loses target lock.
/// </summary>
public readonly struct TargetLockedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly int TargetId;
    public readonly bool HasLock;
    public readonly float TargetRangeM;

    public TargetLockedEvent(int aircraftId, int targetId, bool hasLock, float range)
    {
        AircraftId = aircraftId;
        TargetId = targetId;
        HasLock = hasLock;
        TargetRangeM = range;
    }
}

/// <summary>
/// Fighter aircraft and AA weapon Fire Control Computer computing gun lead (EEGS/LCOS),
/// bomb impact points (CCIP/CCRP), and missile seeker uncaged tracking.
/// Dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class FireControlAndLeadComputingSight : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Targeting")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private FireControlMode currentMode = FireControlMode.AirToAirGunLead_EEGS;
    [SerializeField] private Transform targetEntity;

    [Header("Gun Ballistics (EEGS / LCOS)")]
    [SerializeField] private Vector3 gunMuzzleOffsetLocal = new Vector3(0f, 0f, 2.5f);
    [SerializeField] private float muzzleVelocityM_s = 1050f; // M61 Vulcan 20mm
    [SerializeField] private float bulletDragCoeff = 0.00025f;
    [SerializeField] private float maxGunRangeM = 2200f;

    [Header("Bomb Ballistics (CCIP / CCRP)")]
    [SerializeField] private float bombDragCoeff = 0.00018f;
    [SerializeField] private float ccipSimTimeStep = 0.08f;
    [SerializeField] private int ccipMaxSimSteps = 45;
    [SerializeField] private Vector3 designatedTargetCoord = Vector3.zero;

    [Header("Missile Seeker (Fox 2 / IR)")]
    [SerializeField] private float seekerGimbalLimitDeg = 42f;
    [SerializeField] private float seekerLockThresholdAngleDeg = 4.5f;
    [SerializeField] private float maxMissileLockRangeM = 12000f;

    [Header("Audio Interop")]
    [SerializeField] private ProceduralAudioSynthesizer audioSynth;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime computed outputs
    public Vector3 LeadAimPointWorld { get; private set; }
    public Vector3 PredictedBombImpactWorld { get; private set; }
    public float TargetRangeM { get; private set; }
    public float BulletTimeOfFlightSeconds { get; private set; }
    public bool IsTargetInRange { get; private set; }
    public bool HasMissileLock { get; private set; }
    public bool IsCCRPReleaseCueActive { get; private set; }

    // Target tracking kinematics
    private Vector3 previousTargetPos;
    private Vector3 targetVelocity;

    public string SaveKey => "FireControl_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public FireControlMode CurrentMode => currentMode;

    private void Awake()
    {
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
    // TARGET & MODE ASSIGNMENT
    // =========================================================================

    public void SetTarget(Transform target)
    {
        targetEntity = target;
        if (target != null)
        {
            previousTargetPos = target.position;
            targetVelocity = Vector3.zero;
        }
    }

    public void SetMode(FireControlMode mode)
    {
        currentMode = mode;
    }

    public void DesignateGroundTarget(Vector3 worldTarget)
    {
        designatedTargetCoord = defaultTo2D ? new Vector3(worldTarget.x, worldTarget.y, 0f) : worldTarget;
    }

    // =========================================================================
    // EXECUTION UPDATE (LateUpdate for smoothed rendering)
    // =========================================================================

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        UpdateTargetTracking(dt);

        switch (currentMode)
        {
            case FireControlMode.AirToAirGunLead_EEGS:
                SolveAirToAirGunLead();
                break;

            case FireControlMode.AirToGroundBomb_CCIP:
                SolveBombCCIP();
                break;

            case FireControlMode.AirToGroundBomb_CCRP:
                SolveBombCCRP();
                break;

            case FireControlMode.MissileSeekerLock_Fox2:
                SolveMissileSeekerLock();
                break;
        }
    }

    private void UpdateTargetTracking(float dt)
    {
        if (targetEntity == null) return;

        Vector3 currentTargetPos = targetEntity.position;
        if (defaultTo2D) currentTargetPos.z = 0f;

        targetVelocity = (currentTargetPos - previousTargetPos) / dt;
        previousTargetPos = currentTargetPos;

        TargetRangeM = Vector3.Distance(transform.position, currentTargetPos);
    }

    // =========================================================================
    // 1. AIR-TO-AIR GUN LEAD SOLVER (EEGS / LCOS)
    // =========================================================================

    private void SolveAirToAirGunLead()
    {
        if (targetEntity == null)
        {
            LeadAimPointWorld = transform.TransformPoint(gunMuzzleOffsetLocal) + (transform.forward * 500f);
            IsTargetInRange = false;
            return;
        }

        Vector3 gunMuzzle = transform.TransformPoint(gunMuzzleOffsetLocal);
        Vector3 ownVelocity = GetOwnVelocity();
        Vector3 targetPos = targetEntity.position;

        if (defaultTo2D)
        {
            gunMuzzle.z = 0f;
            targetPos.z = 0f;
        }

        Vector3 toTarget = targetPos - gunMuzzle;
        float dist = toTarget.magnitude;
        IsTargetInRange = dist <= maxGunRangeM;

        // Iterative Time-of-Flight Solver (Newton-Raphson approximation, 4 passes)
        float tof = dist / Mathf.Max(100f, muzzleVelocityM_s);
        Vector3 predictedTarget = targetPos;

        for (int iter = 0; iter < 4; iter++)
        {
            predictedTarget = targetPos + (targetVelocity * tof);
            float currentDist = Vector3.Distance(gunMuzzle, predictedTarget);
            // Effective bullet velocity considering drag
            float effectiveSpeed = muzzleVelocityM_s * (1f - (bulletDragCoeff * currentDist * 0.5f));
            tof = currentDist / Mathf.Max(100f, effectiveSpeed);
        }

        BulletTimeOfFlightSeconds = tof;

        // Gravity drop compensation: Delta P_drop = 0.5 * g * tof^2
        Vector3 gravityDrop = 0.5f * Physics.gravity * (tof * tof);

        // Owncraft velocity forward inheritance
        Vector3 ownForwardLead = ownVelocity * tof;

        // Lead Aim Point that pilot must place pipper over
        LeadAimPointWorld = predictedTarget - gravityDrop - ownForwardLead;
    }

    // =========================================================================
    // 2. CONTINUOUSLY COMPUTED IMPACT POINT (CCIP)
    // =========================================================================

    private void SolveBombCCIP()
    {
        Vector3 simPos = transform.position;
        Vector3 simVel = GetOwnVelocity();
        float dt = ccipSimTimeStep;

        PredictedBombImpactWorld = simPos;

        for (int step = 0; step < ccipMaxSimSteps; step++)
        {
            Vector3 nextPos = simPos + (simVel * dt);
            Vector3 drag = -simVel.normalized * (bombDragCoeff * simVel.sqrMagnitude);
            simVel += (Physics.gravity + drag) * dt;

            // Check raycast intersection with ground terrain
            if (!defaultTo2D)
            {
                if (Physics.Linecast(simPos, nextPos, out RaycastHit hit))
                {
                    PredictedBombImpactWorld = hit.point;
                    break;
                }
            }
            else
            {
                RaycastHit2D hit2D = Physics2D.Linecast(new Vector2(simPos.x, simPos.y), new Vector2(nextPos.x, nextPos.y));
                if (hit2D.collider != null)
                {
                    PredictedBombImpactWorld = new Vector3(hit2D.point.x, hit2D.point.y, 0f);
                    break;
                }
            }

            simPos = nextPos;
            PredictedBombImpactWorld = nextPos;
        }
    }

    // =========================================================================
    // 3. CONTINUOUSLY COMPUTED RELEASE POINT (CCRP)
    // =========================================================================

    private void SolveBombCCRP()
    {
        SolveBombCCIP();

        // Distance from predicted CCIP impact to designated target
        float missDistance = Vector3.Distance(PredictedBombImpactWorld, designatedTargetCoord);

        // Release cue is triggered when impact point sweeps through target coordinate
        IsCCRPReleaseCueActive = missDistance < 15.0f;
    }

    // =========================================================================
    // 4. MISSILE SEEKER LOCK (FOX 2 / IR)
    // =========================================================================

    private void SolveMissileSeekerLock()
    {
        if (targetEntity == null)
        {
            HasMissileLock = false;
            return;
        }

        Vector3 toTarget = targetEntity.position - transform.position;
        if (defaultTo2D) toTarget.z = 0f;

        float range = toTarget.magnitude;
        Vector3 boresight = defaultTo2D ? (Vector3)transform.up : transform.forward;
        float angleDeg = Vector3.Angle(boresight, toTarget);

        bool wasLocked = HasMissileLock;
        HasMissileLock = (range <= maxMissileLockRangeM) && (angleDeg <= seekerLockThresholdAngleDeg);

        if (HasMissileLock != wasLocked)
        {
            EventBus<TargetLockedEvent>.Raise(new TargetLockedEvent(
                gameObject.GetInstanceID(),
                targetEntity.gameObject.GetInstanceID(),
                HasMissileLock,
                range
            ));

            if (HasMissileLock && audioSynth != null)
            {
                audioSynth.TriggerRWRWarning(missileHardLock: true); // High pitch missile lock tone
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
        public FireControlMode Mode;
        public Vector3 DesignatedCoord;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Mode = currentMode,
            DesignatedCoord = designatedTargetCoord
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentMode = container.Mode;
        designatedTargetCoord = container.DesignatedCoord;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Gun Lead Pipper
        if (currentMode == FireControlMode.AirToAirGunLead_EEGS)
        {
            Gizmos.color = IsTargetInRange ? Color.green : Color.red;
            Gizmos.DrawWireSphere(LeadAimPointWorld, 0.45f);
            Gizmos.DrawLine(transform.position, LeadAimPointWorld);
        }

        // Draw Bomb CCIP / CCRP Impact Diamond
        if (currentMode == FireControlMode.AirToGroundBomb_CCIP || currentMode == FireControlMode.AirToGroundBomb_CCRP)
        {
            Gizmos.color = IsCCRPReleaseCueActive ? Color.yellow : Color.cyan;
            Gizmos.DrawWireCube(PredictedBombImpactWorld, Vector3.one * 0.8f);
            Gizmos.DrawLine(transform.position, PredictedBombImpactWorld);
        }

        // Draw Missile Seeker Cone
        if (currentMode == FireControlMode.MissileSeekerLock_Fox2)
        {
            Gizmos.color = HasMissileLock ? Color.green : new Color(1f, 0.5f, 0f, 0.3f);
            Vector3 boresight = defaultTo2D ? (Vector3)transform.up : transform.forward;
            Gizmos.DrawRay(transform.position, boresight * 100f);
        }
    }
}
