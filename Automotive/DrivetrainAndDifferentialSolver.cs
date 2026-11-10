using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Layout of driven wheels in the vehicle chassis.
/// </summary>
public enum DrivetrainLayout
{
    RWD,
    FWD,
    AWD
}

/// <summary>
/// Operational transmission mode.
/// </summary>
public enum TransmissionType
{
    Manual,
    Sequential,
    Automatic
}

/// <summary>
/// Differential locking and torque-biasing mechanism.
/// </summary>
public enum DifferentialType
{
    Open,
    LockedSpool,
    TorsenTorqueSensing,
    ClutchPackLSD_1Way,
    ClutchPackLSD_1Point5Way,
    ClutchPackLSD_2Way
}

/// <summary>
/// Event raised when the gearbox shifts into a new gear.
/// </summary>
public readonly struct GearShiftedEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly int OldGear;
    public readonly int NewGear;
    public readonly float Ratio;

    public GearShiftedEvent(int vehicleId, int oldGear, int newGear, float ratio)
    {
        VehicleId = vehicleId;
        OldGear = oldGear;
        NewGear = newGear;
        Ratio = ratio;
    }
}

/// <summary>
/// Event raised when the clutch slips excessively under severe torque or heat.
/// </summary>
public readonly struct ClutchSlippedEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly float SlipSpeedRadPerSec;
    public readonly float ClutchTemperatureCelsius;

    public ClutchSlippedEvent(int vehicleId, float slipSpeed, float temp)
    {
        VehicleId = vehicleId;
        SlipSpeedRadPerSec = slipSpeed;
        ClutchTemperatureCelsius = temp;
    }
}

/// <summary>
/// High-fidelity mechanical powertrain coupling engine to wheels: friction clutch,
/// multi-gear transmission with rev-matching, and torque-biasing differentials.
/// Supports both 3D Rigidbody and 2D Rigidbody2D top-down vehicles.
/// </summary>
[DisallowMultipleComponent]
public class DrivetrainAndDifferentialSolver : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Engine Coupling")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private InternalCombustionAndTurboSim engine;

    [Header("Drivetrain Architecture")]
    [SerializeField] private DrivetrainLayout layout = DrivetrainLayout.RWD;
    [SerializeField] private TransmissionType transmission = TransmissionType.Automatic;
    [SerializeField] private DifferentialType frontDifferential = DifferentialType.Open;
    [SerializeField] private DifferentialType rearDifferential = DifferentialType.ClutchPackLSD_1Point5Way;
    [SerializeField, Range(0f, 1f)] private float awdFrontTorqueBias = 0.4f; // 40% front, 60% rear

    [Header("Clutch Specifications")]
    [SerializeField] private float maxClutchTorqueCapacityNm = 650f;
    [SerializeField] private float clutchBitePoint = 0.3f; // 0..1 pedal engagement bite threshold
    [SerializeField] private float clutchThermalDissipationRate = 12f; // deg/s cooling

    [Header("Gearbox & Ratios")]
    [SerializeField] private float reverseRatio = -3.45f;
    [SerializeField] private float[] forwardGearRatios = new float[] { 3.82f, 2.20f, 1.52f, 1.22f, 1.02f, 0.84f };
    [SerializeField] private float finalDriveRatio = 3.73f;
    [SerializeField] private float shiftDelaySeconds = 0.15f;
    [SerializeField] private bool enableRevMatching = true;

    [Header("Automatic Shift Thresholds")]
    [SerializeField] private float autoUpshiftRPM = 6200f;
    [SerializeField] private float autoDownshiftRPM = 2800f;

    [Header("Limited-Slip Differential Tuning")]
    [SerializeField, Range(1f, 5f)] private float torsenBiasRatio = 3.0f;
    [SerializeField, Range(0f, 1f)] private float lsdPowerLockRate = 0.75f;
    [SerializeField, Range(0f, 1f)] private float lsdCoastLockRate = 0.45f;
    [SerializeField] private float lsdPreloadTorqueNm = 40f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime state
    private int currentGear = 1; // -1: Reverse, 0: Neutral, 1..N: Forward
    private float clutchPedal = 0f; // 0: engaged, 1: fully disengaged
    private float clutchSlipRadPerSec;
    private float clutchTemperatureCelsius = 25f;
    private float shiftTimer = 0f;
    private bool isShifting = false;

    // Output wheel torques in Newton-meters (Nm)
    public float TorqueFrontLeft { get; private set; }
    public float TorqueFrontRight { get; private set; }
    public float TorqueRearLeft { get; private set; }
    public float TorqueRearRight { get; private set; }

    // Average wheel angular velocities fed back from tires (rad/s)
    private float omegaFrontLeft;
    private float omegaFrontRight;
    private float omegaRearLeft;
    private float omegaRearRight;

    public string SaveKey => "Drivetrain_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public int CurrentGear => currentGear;
    public float ClutchPedal => clutchPedal;
    public float ClutchTemperature => clutchTemperatureCelsius;
    public bool IsShifting => isShifting;

    private void Awake()
    {
        if (engine == null)
        {
            engine = GetComponent<InternalCombustionAndTurboSim>();
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

    // =========================================================================
    // CONTROL & GEARBOX APIS
    // =========================================================================

    public void ShiftUp()
    {
        if (isShifting) return;
        if (currentGear < forwardGearRatios.Length)
        {
            InitiateShift(currentGear + 1);
        }
    }

    public void ShiftDown()
    {
        if (isShifting) return;
        if (currentGear > -1)
        {
            InitiateShift(currentGear - 1);
        }
    }

    public void SetGear(int gear)
    {
        if (isShifting) return;
        if (gear >= -1 && gear <= forwardGearRatios.Length)
        {
            InitiateShift(gear);
        }
    }

    public void SetClutchPedal(float pedal)
    {
        clutchPedal = Mathf.Clamp01(pedal);
    }

    public void FeedWheelSpeeds(float wFL, float wFR, float wRL, float wRR)
    {
        omegaFrontLeft = wFL;
        omegaFrontRight = wFR;
        omegaRearLeft = wRL;
        omegaRearRight = wRR;
    }

    private void InitiateShift(int targetGear)
    {
        if (targetGear == currentGear) return;

        int oldGear = currentGear;
        currentGear = targetGear;
        isShifting = true;
        shiftTimer = shiftDelaySeconds;

        // Auto rev-matching throttle blip on downshifts
        if (enableRevMatching && engine != null && targetGear > 0 && targetGear < oldGear)
        {
            engine.SetThrottle(0.6f);
        }

        EventBus<GearShiftedEvent>.Raise(new GearShiftedEvent(gameObject.GetInstanceID(), oldGear, targetGear, GetGearRatio(targetGear)));
    }

    public float GetGearRatio(int gear)
    {
        if (gear == 0) return 0f;
        if (gear == -1) return reverseRatio;
        if (gear > 0 && gear <= forwardGearRatios.Length)
        {
            return forwardGearRatios[gear - 1];
        }
        return 0f;
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateGearboxAndAutoShift(dt);
        SolveDrivetrainTorques(dt);
    }

    private void UpdateGearboxAndAutoShift(float dt)
    {
        if (isShifting)
        {
            shiftTimer -= dt;
            if (shiftTimer <= 0f)
            {
                isShifting = false;
            }
        }

        // Automatic Transmission Shift Logic
        if (transmission == TransmissionType.Automatic && !isShifting && engine != null)
        {
            float rpm = engine.CurrentRPM;

            if (currentGear > 0)
            {
                if (rpm >= autoUpshiftRPM && currentGear < forwardGearRatios.Length)
                {
                    ShiftUp();
                }
                else if (rpm <= autoDownshiftRPM && currentGear > 1)
                {
                    ShiftDown();
                }
            }
        }
    }

    private void SolveDrivetrainTorques(float dt)
    {
        if (engine == null) return;

        float ratio = GetGearRatio(currentGear);
        float totalRatio = ratio * finalDriveRatio;

        // 1. Calculate transmission input shaft speed from driven wheels
        float drivenWheelSpeed = 0f;
        switch (layout)
        {
            case DrivetrainLayout.FWD:
                drivenWheelSpeed = (omegaFrontLeft + omegaFrontRight) * 0.5f;
                break;
            case DrivetrainLayout.RWD:
                drivenWheelSpeed = (omegaRearLeft + omegaRearRight) * 0.5f;
                break;
            case DrivetrainLayout.AWD:
                drivenWheelSpeed = (omegaFrontLeft + omegaFrontRight + omegaRearLeft + omegaRearRight) * 0.25f;
                break;
        }

        float transInputOmega = drivenWheelSpeed * totalRatio;
        float engineOmega = engine.CrankAngularVelocityRadPerSec;

        // 2. Friction Clutch Torque Transmission
        float effectiveEngagement = Mathf.Clamp01((1f - clutchPedal - clutchBitePoint) / (1f - clutchBitePoint));
        if (isShifting || currentGear == 0) effectiveEngagement = 0f;

        float clutchCapacity = maxClutchTorqueCapacityNm * effectiveEngagement;
        clutchSlipRadPerSec = engineOmega - transInputOmega;

        // Dynamic clutch slip torque
        float clutchTorque = Mathf.Clamp(clutchSlipRadPerSec * 8.0f, -clutchCapacity, clutchCapacity);

        // Feedback load back onto ICE crankshaft
        engine.ApplyExternalLoad(Mathf.Abs(clutchTorque));

        // Clutch thermal dynamics
        float slipPowerWatts = Mathf.Abs(clutchTorque * clutchSlipRadPerSec);
        clutchTemperatureCelsius += (slipPowerWatts * 0.0001f * dt);
        clutchTemperatureCelsius = Mathf.MoveTowards(clutchTemperatureCelsius, 25f, clutchThermalDissipationRate * dt);

        if (Mathf.Abs(clutchSlipRadPerSec) > 30f && clutchCapacity > 100f)
        {
            EventBus<ClutchSlippedEvent>.Raise(new ClutchSlippedEvent(gameObject.GetInstanceID(), clutchSlipRadPerSec, clutchTemperatureCelsius));
        }

        // 3. Torque Multiplication through Gearbox and Final Drive
        float driveTorque = clutchTorque * totalRatio;

        // 4. Center Torque Split (AWD)
        float frontDriveTorque = 0f;
        float rearDriveTorque = 0f;

        switch (layout)
        {
            case DrivetrainLayout.FWD:
                frontDriveTorque = driveTorque;
                rearDriveTorque = 0f;
                break;
            case DrivetrainLayout.RWD:
                frontDriveTorque = 0f;
                rearDriveTorque = driveTorque;
                break;
            case DrivetrainLayout.AWD:
                frontDriveTorque = driveTorque * awdFrontTorqueBias;
                rearDriveTorque = driveTorque * (1f - awdFrontTorqueBias);
                break;
        }

        // 5. Axle Differential Solvers (Front & Rear)
        SolveDifferential(frontDifferential, frontDriveTorque, omegaFrontLeft, omegaFrontRight, out float tFL, out float tFR);
        SolveDifferential(rearDifferential, rearDriveTorque, omegaRearLeft, omegaRearRight, out float tRL, out float tRR);

        TorqueFrontLeft = tFL;
        TorqueFrontRight = tFR;
        TorqueRearLeft = tRL;
        TorqueRearRight = tRR;
    }

    private void SolveDifferential(DifferentialType diffType, float inputTorque, float wLeft, float wRight, out float torqueLeft, out float torqueRight)
    {
        float halfTorque = inputTorque * 0.5f;

        switch (diffType)
        {
            case DifferentialType.Open:
                torqueLeft = halfTorque;
                torqueRight = halfTorque;
                break;

            case DifferentialType.LockedSpool:
                torqueLeft = halfTorque;
                torqueRight = halfTorque;
                break;

            case DifferentialType.TorsenTorqueSensing:
                float speedDelta = wLeft - wRight;
                float bias = Mathf.Clamp(Mathf.Abs(speedDelta) * 0.1f, 1f, torsenBiasRatio);
                if (speedDelta > 0f)
                {
                    // Left wheel slipping, transfer to right
                    torqueRight = inputTorque * (bias / (1f + bias));
                    torqueLeft = inputTorque - torqueRight;
                }
                else
                {
                    torqueLeft = inputTorque * (bias / (1f + bias));
                    torqueRight = inputTorque - torqueLeft;
                }
                break;

            case DifferentialType.ClutchPackLSD_1Way:
            case DifferentialType.ClutchPackLSD_1Point5Way:
            case DifferentialType.ClutchPackLSD_2Way:
                float lockRate = 0f;
                if (inputTorque > 0f)
                {
                    // Acceleration / Drive lock
                    lockRate = lsdPowerLockRate;
                }
                else
                {
                    // Deceleration / Coast lock
                    if (diffType == DifferentialType.ClutchPackLSD_1Way) lockRate = 0f;
                    else if (diffType == DifferentialType.ClutchPackLSD_1Point5Way) lockRate = lsdCoastLockRate;
                    else lockRate = lsdPowerLockRate; // 2-Way
                }

                float lockingTorque = (Mathf.Abs(inputTorque) * lockRate) + lsdPreloadTorqueNm;
                float deltaSpeed = wLeft - wRight;

                float transfer = Mathf.Clamp(deltaSpeed * 15f, -lockingTorque, lockingTorque);
                torqueLeft = halfTorque - transfer;
                torqueRight = halfTorque + transfer;
                break;

            default:
                torqueLeft = halfTorque;
                torqueRight = halfTorque;
                break;
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public int CurrentGear;
        public float ClutchPedal;
        public float ClutchTemperature;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            CurrentGear = currentGear,
            ClutchPedal = clutchPedal,
            ClutchTemperature = clutchTemperatureCelsius
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentGear = container.CurrentGear;
        clutchPedal = container.ClutchPedal;
        clutchTemperatureCelsius = container.ClutchTemperature;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 root = transform.position;
        if (defaultTo2D) root.z = 0f;

        Gizmos.color = Color.yellow;
        // Draw Transmission and Diff Centerlines
        Vector3 forward = defaultTo2D ? (Vector3)transform.up : transform.forward;
        Vector3 right = defaultTo2D ? (Vector3)transform.right : transform.right;

        Gizmos.DrawLine(root, root + (forward * 1.5f));
        Gizmos.DrawLine(root - (right * 1.0f), root + (right * 1.0f));

        // Draw Gear indicator text / line
        Gizmos.color = currentGear > 0 ? Color.green : (currentGear == -1 ? Color.red : Color.gray);
        Gizmos.DrawWireCube(root, new Vector3(0.4f, 0.4f, defaultTo2D ? 0.01f : 0.4f));
    }
}
