using System;
using UnityEngine;
using GameSystems.PhysicsAndCombat;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Active Fly-By-Wire control law.
/// </summary>
public enum FlightControlLaw
{
    NormalLawProtected,
    AlternateLawDegraded,
    DirectLawManualOverride
}

/// <summary>
/// Flight envelope boundary exceeded, triggering FBW limiting intervention.
/// </summary>
public enum EnvelopeBoundary
{
    AlphaStallLimit,
    PositiveGLimit,
    NegativeGLimit,
    OverspeedLimit,
    RollRateLimit
}

/// <summary>
/// Event raised when an aerodynamic flight envelope protection limit is reached.
/// </summary>
public readonly struct FlightEnvelopeLimitReachedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly EnvelopeBoundary Boundary;
    public readonly float MeasuredValue;
    public readonly float LimitValue;

    public FlightEnvelopeLimitReachedEvent(int aircraftId, EnvelopeBoundary boundary, float measured, float limit)
    {
        AircraftId = aircraftId;
        Boundary = boundary;
        MeasuredValue = measured;
        LimitValue = limit;
    }
}

/// <summary>
/// Event raised when the flight control law changes (e.g. pilot switches to Direct Law for Cobra).
/// </summary>
public readonly struct FlightControlLawChangedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly FlightControlLaw NewLaw;

    public FlightControlLawChangedEvent(int aircraftId, FlightControlLaw newLaw)
    {
        AircraftId = aircraftId;
        NewLaw = newLaw;
    }
}

/// <summary>
/// Digital Fly-By-Wire (FBW) flight control computer with automatic pitch auto-trim,
/// cascaded PID rate controllers, and aerodynamic envelope protection (Alpha Floor, G-Limiter, Overspeed).
/// Supports both 3D Rigidbody and 2D Rigidbody2D aircraft.
/// </summary>
[DisallowMultipleComponent]
public class FlyByWireAndFlightEnvelopeProtection : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Mechanical Coupling")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private AerodynamicsAndLiftSurfaceSolver aeroSolver;

    [Header("Control Law & Rates")]
    [SerializeField] private FlightControlLaw currentLaw = FlightControlLaw.NormalLawProtected;
    [SerializeField] private float maxPitchRateDegPerSec = 38f;
    [SerializeField] private float maxRollRateDegPerSec = 220f;
    [SerializeField] private float maxYawRateDegPerSec = 24f;

    [Header("Rate PID Tuning")]
    [SerializeField] private FloatPIDController pitchRatePID = new FloatPIDController(0.045f, 0.015f, 0.004f, -1f, 1f);
    [SerializeField] private FloatPIDController rollRatePID = new FloatPIDController(0.025f, 0.008f, 0.002f, -1f, 1f);
    [SerializeField] private FloatPIDController yawRatePID = new FloatPIDController(0.035f, 0.010f, 0.003f, -1f, 1f);

    [Header("Flight Envelope Protection Limits")]
    [SerializeField] private float maxAlphaDeg = 24f;       // Stall angle limit
    [SerializeField] private float maxPositiveG = 9.0f;     // Airframe max G
    [SerializeField] private float maxNegativeG = -3.0f;    // Pilot negative G
    [SerializeField] private float maxOverspeedM_s = 380f;  // Airframe V_ne

    [Header("Auto-Trim")]
    [SerializeField] private bool enableAutoTrim = true;
    [SerializeField] private float autoTrimRate = 0.1f;

    [Header("Pilot Raw Stick Inputs")]
    [Range(-1f, 1f)] public float RawPitchInput = 0f;
    [Range(-1f, 1f)] public float RawRollInput = 0f;
    [Range(-1f, 1f)] public float RawYawInput = 0f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime state
    private float autoTrimOffset = 0f;
    public float CurrentAlphaDeg { get; private set; }
    public float CurrentLoadFactorG { get; private set; }
    public bool IsAlphaLimiterActive { get; private set; }
    public bool IsGLimiterActive { get; private set; }
    public FlightControlLaw CurrentLaw => currentLaw;

    public string SaveKey => "FBW_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();
        if (aeroSolver == null) aeroSolver = GetComponent<AerodynamicsAndLiftSurfaceSolver>();

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
    // CONTROL LAW MANAGEMENT
    // =========================================================================

    public void SetControlLaw(FlightControlLaw newLaw)
    {
        if (currentLaw != newLaw)
        {
            currentLaw = newLaw;
            EventBus<FlightControlLawChangedEvent>.Raise(new FlightControlLawChangedEvent(gameObject.GetInstanceID(), newLaw));
        }
    }

    public void ToggleDirectLawOverride()
    {
        SetControlLaw(currentLaw == FlightControlLaw.DirectLawManualOverride ? FlightControlLaw.NormalLawProtected : FlightControlLaw.DirectLawManualOverride);
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f || aeroSolver == null) return;

        UpdateAerodynamicState();

        if (currentLaw == FlightControlLaw.DirectLawManualOverride)
        {
            // Direct Law: Raw unassisted stick commands
            aeroSolver.PitchInput = RawPitchInput;
            aeroSolver.RollInput = RawRollInput;
            aeroSolver.YawInput = RawYawInput;
            IsAlphaLimiterActive = false;
            IsGLimiterActive = false;
            return;
        }

        // Normal Law: Envelope Protection & Rate PID
        EvaluateEnvelopeProtections();
        ComputeFlyByWireSurfaceOutputs(dt);
    }

    private void UpdateAerodynamicState()
    {
        Vector3 vel = Vector3.zero;
        Vector3 angVel = Vector3.zero;

        if (!defaultTo2D && rb3D != null)
        {
            vel = rb3D.linearVelocity;
            angVel = rb3D.angularVelocity * Mathf.Rad2Deg;
        }
        else if (defaultTo2D && rb2D != null)
        {
            vel = new Vector3(rb2D.linearVelocity.x, rb2D.linearVelocity.y, 0f);
            angVel = new Vector3(0f, 0f, rb2D.angularVelocity);
        }

        if (vel.sqrMagnitude > 4.0f)
        {
            Vector3 localVel = transform.InverseTransformDirection(vel);
            if (defaultTo2D)
            {
                CurrentAlphaDeg = -Mathf.Atan2(localVel.x, localVel.y) * Mathf.Rad2Deg;
            }
            else
            {
                CurrentAlphaDeg = Mathf.Atan2(-localVel.y, localVel.z) * Mathf.Rad2Deg;
            }
        }
        else
        {
            CurrentAlphaDeg = 0f;
        }

        // Calculate Load Factor (G-force)
        float speed = vel.magnitude;
        float pitchRateRad = (defaultTo2D ? angVel.z : angVel.x) * Mathf.Deg2Rad;
        float centripetalAcc = speed * pitchRateRad;
        CurrentLoadFactorG = 1.0f + (centripetalAcc / 9.81f);
    }

    private void EvaluateEnvelopeProtections()
    {
        IsAlphaLimiterActive = false;
        IsGLimiterActive = false;

        // 1. Alpha Floor / Stall Prevention
        if (CurrentAlphaDeg >= maxAlphaDeg && RawPitchInput > 0f)
        {
            IsAlphaLimiterActive = true;
            EventBus<FlightEnvelopeLimitReachedEvent>.Raise(new FlightEnvelopeLimitReachedEvent(
                gameObject.GetInstanceID(),
                EnvelopeBoundary.AlphaStallLimit,
                CurrentAlphaDeg,
                maxAlphaDeg
            ));
        }

        // 2. G-Limiter
        if (CurrentLoadFactorG >= maxPositiveG && RawPitchInput > 0f)
        {
            IsGLimiterActive = true;
            EventBus<FlightEnvelopeLimitReachedEvent>.Raise(new FlightEnvelopeLimitReachedEvent(
                gameObject.GetInstanceID(),
                EnvelopeBoundary.PositiveGLimit,
                CurrentLoadFactorG,
                maxPositiveG
            ));
        }
        else if (CurrentLoadFactorG <= maxNegativeG && RawPitchInput < 0f)
        {
            IsGLimiterActive = true;
            EventBus<FlightEnvelopeLimitReachedEvent>.Raise(new FlightEnvelopeLimitReachedEvent(
                gameObject.GetInstanceID(),
                EnvelopeBoundary.NegativeGLimit,
                CurrentLoadFactorG,
                maxNegativeG
            ));
        }
    }

    private void ComputeFlyByWireSurfaceOutputs(float dt)
    {
        // Commanded angular rates from pilot stick
        float targetPitchRate = RawPitchInput * maxPitchRateDegPerSec;
        float targetRollRate = RawRollInput * maxRollRateDegPerSec;
        float targetYawRate = RawYawInput * maxYawRateDegPerSec;

        // Apply Envelope Limiter Overrides
        if (IsAlphaLimiterActive)
        {
            // Override with nose-down recovery pitch command
            targetPitchRate = -10f;
        }
        else if (IsGLimiterActive)
        {
            // Clamp pitch rate to cap G-load
            targetPitchRate = Mathf.Sign(targetPitchRate) * 2f;
        }

        // Current measured rates
        float actualPitchRate = 0f;
        float actualRollRate = 0f;
        float actualYawRate = 0f;

        if (!defaultTo2D && rb3D != null)
        {
            actualPitchRate = rb3D.angularVelocity.x * Mathf.Rad2Deg;
            actualYawRate = rb3D.angularVelocity.y * Mathf.Rad2Deg;
            actualRollRate = rb3D.angularVelocity.z * Mathf.Rad2Deg;
        }
        else if (defaultTo2D && rb2D != null)
        {
            actualPitchRate = rb2D.angularVelocity;
        }

        // Auto-Trim: when stick is neutral, adjust trim offset to maintain 1G level flight
        if (enableAutoTrim && Mathf.Abs(RawPitchInput) < 0.05f)
        {
            float gError = 1.0f - CurrentLoadFactorG;
            autoTrimOffset = Mathf.Clamp(autoTrimOffset + (gError * autoTrimRate * dt), -0.3f, 0.3f);
        }

        // Execute PID loops to compute surface deflections
        float pitchCommand = pitchRatePID.Update(targetPitchRate, actualPitchRate, dt) + autoTrimOffset;
        float rollCommand = rollRatePID.Update(targetRollRate, actualRollRate, dt);
        float yawCommand = yawRatePID.Update(targetYawRate, actualYawRate, dt);

        // Apply sanitized outputs directly to lifting surface solver
        aeroSolver.PitchInput = Mathf.Clamp(pitchCommand, -1f, 1f);
        aeroSolver.RollInput = Mathf.Clamp(rollCommand, -1f, 1f);
        aeroSolver.YawInput = Mathf.Clamp(yawCommand, -1f, 1f);
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public FlightControlLaw Law;
        public float AutoTrim;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Law = currentLaw,
            AutoTrim = autoTrimOffset
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentLaw = container.Law;
        autoTrimOffset = container.AutoTrim;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 hudPos = transform.position + (Vector3.up * 1.5f);
        if (defaultTo2D) hudPos = transform.position;

        // Draw FBW Status Indicator
        Gizmos.color = currentLaw == FlightControlLaw.NormalLawProtected ? Color.green : Color.red;
        Gizmos.DrawWireSphere(hudPos, 0.25f);

        // Draw Alpha Stall Warning arc
        if (IsAlphaLimiterActive)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawRay(hudPos, -transform.up * 1.2f);
        }

        // Draw G-Limiter Warning
        if (IsGLimiterActive)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(hudPos, transform.up * 1.2f);
        }
    }
}
