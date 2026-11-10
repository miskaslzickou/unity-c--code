using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Discrete telemetry snapshot logged at high frequency into zero-GC ring buffers.
/// </summary>
[System.Serializable]
public struct TelemetryFrame
{
    public float Timestamp;
    public float SpeedKmh;
    public float LongitudinalG;
    public float LateralG;
    public float EngineRPM;
    public float BoostBar;
    public int CurrentGear;
    public float Throttle;
    public float Brake;
    public float SuspensionTravelFL;
    public float SuspensionTravelFR;
    public float SuspensionTravelRL;
    public float SuspensionTravelRR;
    public float TireSlipFL;
    public float TireSlipFR;
    public float TireSlipRL;
    public float TireSlipRR;
}

/// <summary>
/// Dyno sweep measurement point recording torque and calculated horsepower at an exact RPM.
/// </summary>
[System.Serializable]
public struct DynoSample
{
    public float RPM;
    public float TorqueNm;
    public float PowerKw;
    public float Horsepower;
}

/// <summary>
/// Event raised when a dyno power/torque sweep is completed.
/// </summary>
public readonly struct DynoSweepFinishedEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly float PeakTorqueNm;
    public readonly float PeakTorqueRPM;
    public readonly float PeakHorsepower;
    public readonly float PeakHorsepowerRPM;

    public DynoSweepFinishedEvent(int vehicleId, float peakT, float peakTrpm, float peakHp, float peakHprpm)
    {
        VehicleId = vehicleId;
        PeakTorqueNm = peakT;
        PeakTorqueRPM = peakTrpm;
        PeakHorsepower = peakHp;
        PeakHorsepowerRPM = peakHprpm;
    }
}

/// <summary>
/// Event raised when the vehicle crosses the start/finish line completing a lap.
/// </summary>
public readonly struct LapCompletedEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly int LapNumber;
    public readonly float LapTimeSeconds;
    public readonly float BestLapTimeSeconds;
    public readonly bool IsNewBest;

    public LapCompletedEvent(int vehicleId, int lap, float lapTime, float bestTime, bool isNewBest)
    {
        VehicleId = vehicleId;
        LapNumber = lap;
        LapTimeSeconds = lapTime;
        BestLapTimeSeconds = bestTime;
        IsNewBest = isNewBest;
    }
}

/// <summary>
/// Chassis dynamometer (Dyno) analyzer and zero-GC motorsport telemetry logger.
/// Records G-G diagrams, lap deltas, suspension travel, and tire slip with dual 2D/3D support.
/// </summary>
[DisallowMultipleComponent]
public class VehicleTelemetryAndDynoLogger : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Component Coupling")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 190;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private InternalCombustionAndTurboSim engine;
    [SerializeField] private DrivetrainAndDifferentialSolver drivetrain;
    [SerializeField] private PacejkaTireAndDriftPhysics tirePhysics;
    [SerializeField] private SuspensionAndAeroDownforceSim suspensionAero;

    [Header("Telemetry Ring Buffer")]
    [SerializeField] private int ringBufferSize = 600; // 10 seconds at 60 Hz
    [SerializeField] private float telemetrySampleInterval = 0.0166f;

    [Header("Dyno Configuration")]
    [SerializeField] private int dynoSampleResolution = 80;
    [SerializeField] private float dynoSweepDuration = 4.5f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float ggDiagramRadius = 1.8f;

    // Pre-allocated ring buffer
    private TelemetryFrame[] ringBuffer;
    private int writeIndex = 0;
    private float sampleTimer = 0f;

    // Previous velocity for G-force calculation
    private Vector3 previousVelocity;

    // Lap and Sector Timers
    public int CurrentLap { get; private set; } = 1;
    public float CurrentLapTime { get; private set; }
    public float BestLapTime { get; private set; } = float.MaxValue;
    public float LastLapTime { get; private set; }
    public float Sector1Time { get; private set; }
    public float Sector2Time { get; private set; }
    public float Sector3Time { get; private set; }

    // Dyno State
    private DynoSample[] dynoResults;
    public bool IsRunningDynoSweep { get; private set; }
    private float dynoTimer = 0f;
    public float PeakTorqueNm { get; private set; }
    public float PeakTorqueRPM { get; private set; }
    public float PeakHorsepower { get; private set; }
    public float PeakHorsepowerRPM { get; private set; }

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "TelemetryDyno_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public DynoSample[] DynoResults => dynoResults;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();
        if (engine == null) engine = GetComponent<InternalCombustionAndTurboSim>();
        if (drivetrain == null) drivetrain = GetComponent<DrivetrainAndDifferentialSolver>();
        if (tirePhysics == null) tirePhysics = GetComponent<PacejkaTireAndDriftPhysics>();
        if (suspensionAero == null) suspensionAero = GetComponent<SuspensionAndAeroDownforceSim>();

        ringBuffer = new TelemetryFrame[Mathf.Max(60, ringBufferSize)];
        dynoResults = new DynoSample[dynoSampleResolution];

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
    // DYNO SWEEP ROUTINE
    // =========================================================================

    [ContextMenu("Run Dyno Sweep")]
    public void StartDynoSweep()
    {
        if (engine == null || IsRunningDynoSweep) return;

        IsRunningDynoSweep = true;
        dynoTimer = 0f;
        PeakTorqueNm = 0f;
        PeakHorsepower = 0f;
    }

    private void UpdateDynoSweep(float dt)
    {
        if (!IsRunningDynoSweep || engine == null) return;

        dynoTimer += dt;
        float progress = Mathf.Clamp01(dynoTimer / dynoSweepDuration);

        float minRPM = 1000f;
        float maxRPM = 7500f;
        float testRPM = Mathf.Lerp(minRPM, maxRPM, progress);

        // Force engine throttle 100% during sweep
        engine.SetThrottle(1.0f);

        int sampleIdx = Mathf.Min(dynoResults.Length - 1, Mathf.FloorToInt(progress * (dynoResults.Length - 1)));

        // Torque and power calculations
        float torque = engine.OutputTorqueNm;
        float kw = (torque * testRPM * 2f * Mathf.PI) / 60000f;
        float hp = (torque * testRPM) / 7127f; // Formula: HP = (Torque_Nm * RPM) / 7127

        dynoResults[sampleIdx] = new DynoSample
        {
            RPM = testRPM,
            TorqueNm = torque,
            PowerKw = kw,
            Horsepower = hp
        };

        if (torque > PeakTorqueNm)
        {
            PeakTorqueNm = torque;
            PeakTorqueRPM = testRPM;
        }

        if (hp > PeakHorsepower)
        {
            PeakHorsepower = hp;
            PeakHorsepowerRPM = testRPM;
        }

        if (progress >= 1.0f)
        {
            IsRunningDynoSweep = false;
            EventBus<DynoSweepFinishedEvent>.Raise(new DynoSweepFinishedEvent(
                gameObject.GetInstanceID(),
                PeakTorqueNm,
                PeakTorqueRPM,
                PeakHorsepower,
                PeakHorsepowerRPM
            ));
        }
    }

    // =========================================================================
    // LAP & SECTOR TIMING
    // =========================================================================

    public void TriggerStartFinishLine()
    {
        if (CurrentLapTime <= 2.0f) return; // Debounce

        LastLapTime = CurrentLapTime;
        bool isNewBest = false;

        if (LastLapTime < BestLapTime)
        {
            BestLapTime = LastLapTime;
            isNewBest = true;
        }

        EventBus<LapCompletedEvent>.Raise(new LapCompletedEvent(
            gameObject.GetInstanceID(),
            CurrentLap,
            LastLapTime,
            BestLapTime,
            isNewBest
        ));

        CurrentLap++;
        CurrentLapTime = 0f;
    }

    public void TriggerSector(int sectorIndex)
    {
        switch (sectorIndex)
        {
            case 1: Sector1Time = CurrentLapTime; break;
            case 2: Sector2Time = CurrentLapTime - Sector1Time; break;
            case 3: Sector3Time = CurrentLapTime - (Sector1Time + Sector2Time); break;
        }
    }

    // =========================================================================
    // SIMULATION TICK & TELEMETRY LOGGING (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        CurrentLapTime += deltaTime;

        if (IsRunningDynoSweep)
        {
            UpdateDynoSweep(deltaTime);
        }

        sampleTimer += deltaTime;
        if (sampleTimer >= telemetrySampleInterval)
        {
            sampleTimer = 0f;
            RecordTelemetryFrame(deltaTime);
        }
    }

    private void RecordTelemetryFrame(float dt)
    {
        // 1. Calculate accelerations and G-forces
        Vector3 currentVel = Vector3.zero;
        if (!defaultTo2D && rb3D != null) currentVel = rb3D.linearVelocity;
        else if (defaultTo2D && rb2D != null) currentVel = new Vector3(rb2D.linearVelocity.x, rb2D.linearVelocity.y, 0f);

        Vector3 worldAcc = (currentVel - previousVelocity) / Mathf.Max(0.0001f, dt);
        previousVelocity = currentVel;

        Vector3 localAcc = transform.InverseTransformDirection(worldAcc);
        float gLong = (defaultTo2D ? localAcc.y : localAcc.z) / 9.81f;
        float gLat = localAcc.x / 9.81f;

        // 2. Fetch subsystems telemetry
        float speedKmh = currentVel.magnitude * 3.6f;
        float rpm = engine != null ? engine.CurrentRPM : 0f;
        float boost = engine != null ? engine.CurrentBoostBar : 0f;
        int gear = drivetrain != null ? drivetrain.CurrentGear : 0;
        float throt = engine != null ? engine.CurrentThrottle : 0f;

        float sFL = 0f, sFR = 0f, sRL = 0f, sRR = 0f;
        if (tirePhysics != null && tirePhysics.Wheels != null && tirePhysics.Wheels.Length >= 4)
        {
            sFL = Mathf.Abs(tirePhysics.Wheels[0].SlipRatio);
            sFR = Mathf.Abs(tirePhysics.Wheels[1].SlipRatio);
            sRL = Mathf.Abs(tirePhysics.Wheels[2].SlipRatio);
            sRR = Mathf.Abs(tirePhysics.Wheels[3].SlipRatio);
        }

        // 3. Store frame in ring buffer
        ringBuffer[writeIndex] = new TelemetryFrame
        {
            Timestamp = Time.time,
            SpeedKmh = speedKmh,
            LongitudinalG = gLong,
            LateralG = gLat,
            EngineRPM = rpm,
            BoostBar = boost,
            CurrentGear = gear,
            Throttle = throt,
            TireSlipFL = sFL,
            TireSlipFR = sFR,
            TireSlipRL = sRL,
            TireSlipRR = sRR
        };

        writeIndex = (writeIndex + 1) % ringBuffer.Length;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public int BestLap;
        public float BestLapTime;
        public float PeakHp;
        public float PeakTorque;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            BestLapTime = BestLapTime,
            PeakHp = PeakHorsepower,
            PeakTorque = PeakTorqueNm
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        BestLapTime = container.BestLapTime;
        PeakHorsepower = container.PeakHp;
        PeakTorqueNm = container.PeakTorque;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 hudPos = transform.position + (Vector3.up * 2.2f);
        if (defaultTo2D) hudPos = transform.position;

        // Draw G-G Diagram Polar Circle
        Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
        Gizmos.DrawWireSphere(hudPos, ggDiagramRadius);

        // Draw Current G-Force Vector
        if (ringBuffer != null && ringBuffer.Length > 0)
        {
            int lastIdx = (writeIndex - 1 + ringBuffer.Length) % ringBuffer.Length;
            TelemetryFrame f = ringBuffer[lastIdx];

            Vector3 gOffset = new Vector3(f.LateralG, f.LongitudinalG, 0f) * (ggDiagramRadius / 2.0f);
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(hudPos + gOffset, 0.12f);
            Gizmos.DrawLine(hudPos, hudPos + gOffset);
        }
    }
}
