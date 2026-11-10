using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Basic Fighter Maneuvers (BFM) tactical state in combat.
/// </summary>
public enum BFMState
{
    NeutralPatrol,
    OffensiveMerge,
    TwoCircleRateFight,
    OneCircleRadiusFight,
    HighYoYoOvershootDefense,
    LowYoYoClosureClosure,
    ImmelmannTurn,
    SplitSDive,
    RollingScissors,
    DefensiveBeamingAndNotch
}

/// <summary>
/// Event raised when the tactical dogfight AI transitions to a new BFM maneuver.
/// </summary>
public readonly struct BFMManeuverExecutedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly int BanditId;
    public readonly BFMState Maneuver;
    public readonly float SpecificEnergyMeters;

    public BFMManeuverExecutedEvent(int aircraftId, int banditId, BFMState maneuver, float energy)
    {
        AircraftId = aircraftId;
        BanditId = banditId;
        Maneuver = maneuver;
        SpecificEnergyMeters = energy;
    }
}

/// <summary>
/// Tactical Basic Fighter Maneuvers (BFM) AI brain based on Boyd's Energy-Maneuverability theory.
/// Solves 1-Circle/2-Circle rate fights, High/Low Yo-Yo, Scissors, and Defensive Doppler Notching.
/// Supports both 3D and 2D dogfights.
/// </summary>
[DisallowMultipleComponent]
public class DogfightAIAndBFMController : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Components")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 140;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private FlyByWireAndFlightEnvelopeProtection fbw;
    [SerializeField] private JetAndPropellerEngineSim engine;
    [SerializeField] private FireControlAndLeadComputingSight fcs;
    [SerializeField] private CountermeasuresAndEWDispenser countermeasures;

    [Header("Combat Target (Bandit)")]
    [SerializeField] private Transform banditTarget;

    [Header("Energy-Maneuverability Performance Limits")]
    [SerializeField] private float cornerSpeedM_s = 210f; // ~750 km/h optimal turn rate
    [SerializeField] private float minCombatSpeedM_s = 90f;
    [SerializeField] private float maxSpeedM_s = 360f;

    [Header("Tactical AI State")]
    [SerializeField] private BFMState currentState = BFMState.OffensiveMerge;
    [SerializeField] private float maneuverTimer = 0f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Tactical Geometry Metrics
    public float RangeToBanditM { get; private set; }
    public float AspectAngleDeg { get; private set; }
    public float AntennaTrainAngleDeg { get; private set; }
    public float ClosureRateM_s { get; private set; }
    public float SpecificEnergyM { get; private set; } // E_s = h + v^2 / 2g

    private Vector3 previousBanditPos;
    private Vector3 banditVelocity;

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "DogfightAI_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public BFMState CurrentState => currentState;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();
        if (fbw == null) fbw = GetComponent<FlyByWireAndFlightEnvelopeProtection>();
        if (engine == null) engine = GetComponent<JetAndPropellerEngineSim>();
        if (fcs == null) fcs = GetComponent<FireControlAndLeadComputingSight>();
        if (countermeasures == null) countermeasures = GetComponent<CountermeasuresAndEWDispenser>();

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

    public void SetBandit(Transform bandit)
    {
        banditTarget = bandit;
        if (bandit != null)
        {
            previousBanditPos = bandit.position;
            banditVelocity = Vector3.zero;
            if (fcs != null) fcs.SetTarget(bandit);
        }
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        if (banditTarget == null) return;

        UpdateCombatGeometry(deltaTime);
        EvaluateEnergyAndManeuverSelection(deltaTime);
        ExecuteBFMControls(deltaTime);
    }

    private void UpdateCombatGeometry(float dt)
    {
        Vector3 ownPos = transform.position;
        Vector3 banditPos = banditTarget.position;
        if (defaultTo2D)
        {
            ownPos.z = 0f;
            banditPos.z = 0f;
        }

        Vector3 toBandit = banditPos - ownPos;
        float currentRange = toBandit.magnitude;

        // Bandit velocity and closure rate
        banditVelocity = (banditPos - previousBanditPos) / dt;
        previousBanditPos = banditPos;

        Vector3 ownVel = GetOwnVelocity();
        ClosureRateM_s = (RangeToBanditM - currentRange) / dt;
        RangeToBanditM = currentRange;

        // Antenna Train Angle (AOT): angle between own nose and bandit
        Vector3 ownFwd = defaultTo2D ? (Vector3)transform.up : transform.forward;
        AntennaTrainAngleDeg = Vector3.Angle(ownFwd, toBandit);

        // Aspect Angle: angle between bandit velocity and line-of-sight
        Vector3 banditFwd = banditVelocity.sqrMagnitude > 1f ? banditVelocity.normalized : banditTarget.forward;
        AspectAngleDeg = Vector3.Angle(banditFwd, -toBandit);

        // Boyd's Specific Energy: E_s = h + v^2 / 2g
        float altitude = ownPos.y;
        float speed = ownVel.magnitude;
        SpecificEnergyM = altitude + ((speed * speed) / (2f * 9.81f));
    }

    private void EvaluateEnergyAndManeuverSelection(float dt)
    {
        maneuverTimer += dt;
        float speed = GetOwnVelocity().magnitude;

        // Defensive override: if bandit is on our tail (AOT high on bandit, low range)
        if (AspectAngleDeg > 135f && RangeToBanditM < 1800f)
        {
            if (currentState != BFMState.DefensiveBeamingAndNotch && currentState != BFMState.RollingScissors)
            {
                TransitionToManeuver(BFMState.DefensiveBeamingAndNotch);
                return;
            }
        }

        // Check overshoot conditions
        if (RangeToBanditM < 450f && ClosureRateM_s > 60f && AntennaTrainAngleDeg < 30f)
        {
            if (currentState != BFMState.HighYoYoOvershootDefense)
            {
                TransitionToManeuver(BFMState.HighYoYoOvershootDefense);
                return;
            }
        }

        // Periodic tactical re-evaluation
        if (maneuverTimer > 4.5f)
        {
            maneuverTimer = 0f;

            if (RangeToBanditM > 2500f)
            {
                TransitionToManeuver(BFMState.OffensiveMerge);
            }
            else if (speed > cornerSpeedM_s * 1.15f)
            {
                // Excess energy: use 2-Circle rate fight or High Yo-Yo
                TransitionToManeuver(BFMState.TwoCircleRateFight);
            }
            else if (speed < cornerSpeedM_s * 0.85f)
            {
                // Low energy: dive with Low Yo-Yo to regain speed
                TransitionToManeuver(BFMState.LowYoYoClosureClosure);
            }
            else
            {
                TransitionToManeuver(BFMState.OneCircleRadiusFight);
            }
        }
    }

    private void TransitionToManeuver(BFMState newState)
    {
        currentState = newState;
        maneuverTimer = 0f;

        EventBus<BFMManeuverExecutedEvent>.Raise(new BFMManeuverExecutedEvent(
            gameObject.GetInstanceID(),
            banditTarget != null ? banditTarget.gameObject.GetInstanceID() : -1,
            newState,
            SpecificEnergyM
        ));
    }

    // =========================================================================
    // FLIGHT CONTROL COMMAND EXECUTION
    // =========================================================================

    private void ExecuteBFMControls(float dt)
    {
        if (fbw == null || banditTarget == null) return;

        Vector3 toBandit = banditTarget.position - transform.position;
        if (defaultTo2D) toBandit.z = 0f;

        float targetPitch = 0f;
        float targetRoll = 0f;
        float targetYaw = 0f;
        float throttle = 1.0f; // Default military dry power

        switch (currentState)
        {
            case BFMState.OffensiveMerge:
                // Full throttle / afterburner intercept course
                throttle = 1.5f;
                SteerTowards(banditTarget.position, out targetPitch, out targetRoll, out targetYaw);
                break;

            case BFMState.TwoCircleRateFight:
                // Sustained maximum rate turn at corner velocity
                throttle = (GetOwnVelocity().magnitude < cornerSpeedM_s) ? 1.5f : 0.85f;
                targetPitch = 0.95f; // Pull hard Gs at rate speed
                // Roll into bandit's turning plane
                AlignRollWith(banditTarget.position, out targetRoll);
                break;

            case BFMState.OneCircleRadiusFight:
                // Minimum radius nose-to-nose turn
                throttle = 0.65f;
                targetPitch = 1.0f;
                AlignRollWith(banditTarget.position, out targetRoll);
                break;

            case BFMState.HighYoYoOvershootDefense:
                // Pitch up into vertical to trade kinetic energy for altitude, reducing closure rate
                targetPitch = 1.0f;
                throttle = 0.4f; // Cut throttle to prevent overshoot
                targetRoll = 0.4f; // Quarter-roll to keep bandit visual
                break;

            case BFMState.LowYoYoClosureClosure:
                // Dive to gain airspeed and cut across bandit's turning circle
                targetPitch = -0.5f;
                throttle = 1.3f;
                SteerTowards(banditTarget.position, out _, out targetRoll, out targetYaw);
                break;

            case BFMState.RollingScissors:
                // Barrel roll reversal to force bandit in front
                throttle = 0.3f;
                targetRoll = 1.0f;
                targetPitch = Mathf.Sin(Time.time * 3f) * 0.7f;
                break;

            case BFMState.DefensiveBeamingAndNotch:
                // Turn 90 degrees perpendicular to bandit line-of-sight to notch Doppler radar
                Vector3 notchDir = defaultTo2D
                    ? Vector3.Cross(toBandit.normalized, Vector3.forward)
                    : Vector3.Cross(toBandit.normalized, Vector3.up);

                SteerTowards(transform.position + (notchDir * 500f) - (Vector3.up * 100f), out targetPitch, out targetRoll, out targetYaw);
                throttle = 1.5f;

                // Dispense countermeasures during defensive notching
                if (countermeasures != null && UnityEngine.Random.value < 0.25f)
                {
                    countermeasures.DispenseSingle(CountermeasureType.DipoleChaffBundle);
                    countermeasures.DispenseSingle(CountermeasureType.PyrotechnicFlare);
                }
                break;
        }

        // Apply commands to FBW
        fbw.RawPitchInput = Mathf.Clamp(targetPitch, -1f, 1f);
        fbw.RawRollInput = Mathf.Clamp(targetRoll, -1f, 1f);
        fbw.RawYawInput = Mathf.Clamp(targetYaw, -1f, 1f);

        if (engine != null)
        {
            engine.SetThrottle(throttle);
        }
    }

    private void SteerTowards(Vector3 targetWorld, out float pitch, out float roll, out float yaw)
    {
        Vector3 localDir = transform.InverseTransformDirection(targetWorld - transform.position);
        if (defaultTo2D)
        {
            pitch = Mathf.Clamp(localDir.y * 0.1f, -1f, 1f);
            yaw = Mathf.Clamp(-localDir.x * 0.1f, -1f, 1f);
            roll = 0f;
            return;
        }

        pitch = Mathf.Clamp(-localDir.y * 0.05f, -1f, 1f);
        yaw = Mathf.Clamp(localDir.x * 0.05f, -1f, 1f);
        roll = Mathf.Clamp(localDir.x * 0.08f, -1f, 1f);
    }

    private void AlignRollWith(Vector3 targetWorld, out float roll)
    {
        if (defaultTo2D)
        {
            roll = 0f;
            return;
        }

        Vector3 localTarget = transform.InverseTransformPoint(targetWorld);
        float rollAngle = Mathf.Atan2(localTarget.x, localTarget.y) * Mathf.Rad2Deg;
        roll = Mathf.Clamp(rollAngle * 0.03f, -1f, 1f);
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
        public BFMState State;
        public float Timer;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            State = currentState,
            Timer = maneuverTimer
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentState = container.State;
        maneuverTimer = container.Timer;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || banditTarget == null) return;

        // Draw Line-Of-Sight to Bandit
        Gizmos.color = currentState == BFMState.DefensiveBeamingAndNotch ? Color.red : Color.green;
        Gizmos.DrawLine(transform.position, banditTarget.position);

        // Draw Specific Energy indicator bar
        Vector3 hudPos = transform.position + (Vector3.up * 2.2f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(hudPos, hudPos + (Vector3.up * (SpecificEnergyM * 0.001f)));
    }
}
