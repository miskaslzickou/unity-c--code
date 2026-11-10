using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Type of aircraft propulsion unit.
/// </summary>
public enum AircraftEngineType
{
    Turbojet,
    Turbofan,
    AfterburningTurbofan,
    PistonPropellerConstantSpeed,
    Turboprop
}

/// <summary>
/// Event raised when an aircraft jet engine suffers a compressor stall or flameout.
/// </summary>
public readonly struct CompressorStallEvent : IGameEvent
{
    public readonly int EngineId;
    public readonly bool IsFlameout;
    public readonly float AngleOfAttackDeg;

    public CompressorStallEvent(int engineId, bool isFlameout, float aoa)
    {
        EngineId = engineId;
        IsFlameout = isFlameout;
        AngleOfAttackDeg = aoa;
    }
}

/// <summary>
/// Event raised when the afterburner / reheat is engaged or disengaged.
/// </summary>
public readonly struct AfterburnerToggledEvent : IGameEvent
{
    public readonly int EngineId;
    public readonly bool IsActive;
    public readonly float TotalThrustN;

    public AfterburnerToggledEvent(int engineId, bool active, float thrust)
    {
        EngineId = engineId;
        IsActive = active;
        TotalThrustN = thrust;
    }
}

/// <summary>
/// Physical aircraft propulsion simulator covering Turbojet/Turbofan engines with Afterburner,
/// 2D/3D Thrust Vectoring Nozzles, and Constant-Speed Variable-Pitch Propellers with Feathering.
/// Supports both 3D Rigidbody and 2D Rigidbody2D aircraft.
/// </summary>
[DisallowMultipleComponent]
public class JetAndPropellerEngineSim : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Rigidbodies")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private ProceduralAudioSynthesizer audioSynth;

    [Header("Engine Type & Ratings")]
    [SerializeField] private AircraftEngineType engineType = AircraftEngineType.AfterburningTurbofan;
    [SerializeField] private float maxDryThrustN = 75000f;       // 75 kN dry thrust
    [SerializeField] private float maxAfterburnerThrustN = 125000f; // 125 kN wet thrust
    [SerializeField] private float spoolUpTimeSeconds = 2.8f;    // N1/N2 spool lag
    [SerializeField] private float drySpecificFuelConsumption = 0.8f; // kg / (daN * h)
    [SerializeField] private float wetSpecificFuelConsumption = 2.2f; // Afterburner fuel spike

    [Header("Compressor Stall & Intake Aerodynamics")]
    [SerializeField] private float stallCriticalAoADeg = 28f;
    [SerializeField] private float stallRecoveryAoADeg = 18f;
    [SerializeField] private float restartDurationSeconds = 4.0f;

    [Header("Thrust Vectoring Nozzle (TVC)")]
    [SerializeField] private bool hasThrustVectoring = true;
    [SerializeField] private float maxPitchNozzleDeg = 20f;
    [SerializeField] private float maxYawNozzleDeg = 15f;
    [SerializeField] private float nozzleSlewRateDegPerSec = 60f;

    [Header("Constant-Speed Propeller Specifications")]
    [SerializeField] private float propDiameterM = 2.4f;
    [SerializeField] private float governorTargetRPM = 2400f;
    [SerializeField] private float minBladePitchDeg = 12f;
    [SerializeField] private float maxBladePitchDeg = 55f;
    [SerializeField] private float featheredBladePitchDeg = 88f;

    [Header("Mounting Location")]
    [SerializeField] private Vector3 nozzleLocalOffset = new Vector3(0f, 0f, -4.5f);

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime state
    private float commandedThrottle; // 0..1 (dry) or 1..2 (afterburner)
    private float n1SpoolRPMPercent = 0f; // 0..100%
    private float n2SpoolRPMPercent = 0f;
    private bool isAfterburnerActive = false;
    private bool isCompressorStalled = false;
    private bool isFlameout = false;
    private float restartTimer = 0f;

    // Propeller runtime state
    private float currentBladePitchDeg = 20f;
    private bool isFeathered = false;

    // TVC runtime nozzle angles
    private float currentNozzlePitchDeg = 0f;
    private float currentNozzleYawDeg = 0f;

    public string SaveKey => "AviationEngine_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public float CurrentThrustN { get; private set; }
    public float CurrentFuelFlowKgPerSec { get; private set; }
    public float PropwashVelocityM_s { get; private set; }
    public float N1Percent => n1SpoolRPMPercent;
    public bool IsAfterburnerActive => isAfterburnerActive;
    public bool IsCompressorStalled => isCompressorStalled;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();
        if (audioSynth == null) audioSynth = GetComponent<ProceduralAudioSynthesizer>();

        n1SpoolRPMPercent = 20f; // Baseline idle
        n2SpoolRPMPercent = 55f;

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
    // CONTROL APIS
    // =========================================================================

    /// <summary>
    /// Sets throttle level: 0.0 to 1.0 (MIL / Dry Thrust), >1.0 to 2.0 (Afterburner / Reheat).
    /// </summary>
    public void SetThrottle(float throttle)
    {
        commandedThrottle = Mathf.Clamp(throttle, 0f, 2.0f);
    }

    public void SetThrustVectoring(float pitchInput, float yawInput)
    {
        if (!hasThrustVectoring) return;
        currentNozzlePitchDeg = Mathf.Clamp(pitchInput * maxPitchNozzleDeg, -maxPitchNozzleDeg, maxPitchNozzleDeg);
        currentNozzleYawDeg = Mathf.Clamp(yawInput * maxYawNozzleDeg, -maxYawNozzleDeg, maxYawNozzleDeg);
    }

    public void ToggleFeather(bool feather)
    {
        isFeathered = feather;
    }

    public void AttemptEngineRelight()
    {
        if (isFlameout && restartTimer <= 0f)
        {
            restartTimer = restartDurationSeconds;
        }
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateSpoolAndState(dt);
        CalculateThrustAndFuelFlow(dt);
        ApplyThrustToAircraft();

        if (audioSynth != null)
        {
            audioSynth.SetEngineParameters(n1SpoolRPMPercent * 100f, commandedThrottle);
        }
    }

    private void UpdateSpoolAndState(float dt)
    {
        // Engine relight state machine
        if (isFlameout)
        {
            if (restartTimer > 0f)
            {
                restartTimer -= dt;
                if (restartTimer <= 0f)
                {
                    isFlameout = false;
                    isCompressorStalled = false;
                }
            }
            n1SpoolRPMPercent = Mathf.MoveTowards(n1SpoolRPMPercent, 0f, 15f * dt);
            n2SpoolRPMPercent = Mathf.MoveTowards(n2SpoolRPMPercent, 0f, 20f * dt);
            return;
        }

        // Check Angle of Attack for Compressor Stall
        float airspeed = GetAirspeed();
        float currentAoADeg = CalculateAoADeg();

        if (currentAoADeg > stallCriticalAoADeg && airspeed > 30f && n2SpoolRPMPercent > 70f)
        {
            if (!isCompressorStalled)
            {
                isCompressorStalled = true;
                EventBus<CompressorStallEvent>.Raise(new CompressorStallEvent(gameObject.GetInstanceID(), isFlameout: false, currentAoADeg));

                if (audioSynth != null)
                {
                    audioSynth.TriggerLaserTransient(250f, 0.25f); // Compressor pop/surge
                }
            }
        }
        else if (isCompressorStalled && currentAoADeg < stallRecoveryAoADeg)
        {
            isCompressorStalled = false;
        }

        // Spool-up calculation (N1 / N2 turbine inertia)
        float targetN1 = Mathf.Clamp(commandedThrottle * 50f, 20f, 100f);
        if (isCompressorStalled) targetN1 = 25f; // Rapid loss of core pressure

        float spoolRate = (100f / Mathf.Max(0.5f, spoolUpTimeSeconds)) * dt;
        n1SpoolRPMPercent = Mathf.MoveTowards(n1SpoolRPMPercent, targetN1, spoolRate);
        n2SpoolRPMPercent = Mathf.MoveTowards(n2SpoolRPMPercent, targetN1 * 1.05f, spoolRate * 1.2f);

        // Afterburner gate
        bool wasAb = isAfterburnerActive;
        isAfterburnerActive = (commandedThrottle > 1.05f) && (n1SpoolRPMPercent >= 92f) && !isCompressorStalled;

        if (isAfterburnerActive != wasAb)
        {
            EventBus<AfterburnerToggledEvent>.Raise(new AfterburnerToggledEvent(
                gameObject.GetInstanceID(),
                isAfterburnerActive,
                CurrentThrustN
            ));
        }
    }

    private void CalculateThrustAndFuelFlow(float dt)
    {
        float airspeed = GetAirspeed();
        float altitude = transform.position.y;

        // 1. Air density lapse: rho(h) = rho0 * exp(-h / H0)
        float rho0 = 1.225f;
        float airDensity = rho0 * Mathf.Exp(-Mathf.Max(0f, altitude) / 8500f);
        float densityRatio = airDensity / rho0;

        // 2. Ram-Air recovery (Mach effect)
        float speedOfSound = 340f;
        float mach = airspeed / speedOfSound;
        float ramAirFactor = 1.0f + (0.2f * mach * mach);

        if (engineType == AircraftEngineType.PistonPropellerConstantSpeed || engineType == AircraftEngineType.Turboprop)
        {
            // Propeller engine calculation
            if (isFeathered)
            {
                currentBladePitchDeg = Mathf.MoveTowards(currentBladePitchDeg, featheredBladePitchDeg, 15f * dt);
                CurrentThrustN = 0f;
                PropwashVelocityM_s = airspeed;
                return;
            }

            // Governor adjusts blade pitch to maintain target RPM
            float targetPitch = Mathf.Lerp(minBladePitchDeg, maxBladePitchDeg, Mathf.Clamp01(airspeed / 120f));
            currentBladePitchDeg = Mathf.MoveTowards(currentBladePitchDeg, targetPitch, 8f * dt);

            float propArea = Mathf.PI * Mathf.Pow(propDiameterM * 0.5f, 2f);
            float basePropThrust = maxDryThrustN * (n1SpoolRPMPercent / 100f) * densityRatio;
            CurrentThrustN = Mathf.Max(0f, basePropThrust - (airspeed * 25f)); // Speed degradation

            // Induced prop-wash velocity over wing surfaces
            float thrustPerArea = (2f * CurrentThrustN) / Mathf.Max(0.1f, airDensity * propArea);
            PropwashVelocityM_s = Mathf.Sqrt((airspeed * airspeed) + Mathf.Max(0f, thrustPerArea));

            CurrentFuelFlowKgPerSec = (CurrentThrustN / 10000f) * drySpecificFuelConsumption / 3600f;
        }
        else
        {
            // Jet Engine Calculation
            float spoolFactor = Mathf.Pow(n1SpoolRPMPercent / 100f, 1.8f);
            float dryThrust = maxDryThrustN * spoolFactor * Mathf.Pow(densityRatio, 0.85f) * ramAirFactor;

            if (isAfterburnerActive)
            {
                float abFraction = Mathf.Clamp01(commandedThrottle - 1.0f);
                float abThrustBonus = (maxAfterburnerThrustN - maxDryThrustN) * abFraction * densityRatio;
                CurrentThrustN = dryThrust + abThrustBonus;
                CurrentFuelFlowKgPerSec = ((dryThrust * drySpecificFuelConsumption) + (abThrustBonus * wetSpecificFuelConsumption)) / 36000f;
            }
            else
            {
                CurrentThrustN = dryThrust;
                CurrentFuelFlowKgPerSec = (dryThrust * drySpecificFuelConsumption) / 36000f;
            }

            if (isCompressorStalled || isFlameout)
            {
                CurrentThrustN *= 0.05f;
            }
        }
    }

    private void ApplyThrustToAircraft()
    {
        Vector3 nozzleWorldPos = transform.TransformPoint(nozzleLocalOffset);

        // Compute Thrust Vector based on TVC Nozzle Angles
        Quaternion nozzleRot = defaultTo2D
            ? Quaternion.Euler(0f, 0f, currentNozzlePitchDeg)
            : Quaternion.Euler(currentNozzlePitchDeg, currentNozzleYawDeg, 0f);

        Vector3 forwardDir = defaultTo2D ? (Vector3)transform.up : transform.forward;
        Vector3 thrustDirection = nozzleRot * forwardDir;
        Vector3 thrustForce = thrustDirection * CurrentThrustN;

        if (!defaultTo2D && rb3D != null)
        {
            rb3D.AddForceAtPosition(thrustForce, nozzleWorldPos, ForceMode.Force);
        }
        else if (defaultTo2D && rb2D != null)
        {
            Vector2 f2 = new Vector2(thrustForce.x, thrustForce.y);
            Vector2 p2 = new Vector2(nozzleWorldPos.x, nozzleWorldPos.y);
            rb2D.AddForceAtPosition(f2, p2, ForceMode2D.Force);
        }
    }

    private float GetAirspeed()
    {
        if (!defaultTo2D && rb3D != null) return rb3D.linearVelocity.magnitude;
        if (defaultTo2D && rb2D != null) return rb2D.linearVelocity.magnitude;
        return 0f;
    }

    private float CalculateAoADeg()
    {
        Vector3 vel = defaultTo2D
            ? (Vector3)(rb2D != null ? rb2D.linearVelocity : Vector2.zero)
            : (rb3D != null ? rb3D.linearVelocity : Vector3.zero);

        if (vel.sqrMagnitude < 4.0f) return 0f;

        Vector3 localVel = transform.InverseTransformDirection(vel);
        if (defaultTo2D)
        {
            return -Mathf.Atan2(localVel.x, localVel.y) * Mathf.Rad2Deg;
        }
        return Mathf.Atan2(-localVel.y, localVel.z) * Mathf.Rad2Deg;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public float N1;
        public float Throttle;
        public bool Stalled;
        public bool Flameout;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            N1 = n1SpoolRPMPercent,
            Throttle = commandedThrottle,
            Stalled = isCompressorStalled,
            Flameout = isFlameout
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        n1SpoolRPMPercent = container.N1;
        commandedThrottle = container.Throttle;
        isCompressorStalled = container.Stalled;
        isFlameout = container.Flameout;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 nozzlePos = transform.TransformPoint(nozzleLocalOffset);
        if (defaultTo2D) nozzlePos.z = 0f;

        // Draw Thrust Vector
        Quaternion nozzleRot = defaultTo2D
            ? Quaternion.Euler(0f, 0f, currentNozzlePitchDeg)
            : Quaternion.Euler(currentNozzlePitchDeg, currentNozzleYawDeg, 0f);

        Vector3 forward = defaultTo2D ? (Vector3)transform.up : transform.forward;
        Vector3 thrustDir = nozzleRot * forward;

        Gizmos.color = isAfterburnerActive ? Color.magenta : (isCompressorStalled ? Color.red : Color.cyan);
        Gizmos.DrawRay(nozzlePos, thrustDir * (CurrentThrustN * 0.0001f));
        Gizmos.DrawWireSphere(nozzlePos, 0.35f);
    }
}
