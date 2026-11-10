using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Physiological impairment state experienced by the pilot under extreme G-forces.
/// </summary>
public enum PilotPhysiologyState
{
    NominalConscious,
    GreyoutTunnelVision,
    BlackoutBlindness,
    GLOC_Unconscious,
    RedoutNegativeG
}

/// <summary>
/// Event raised when a pilot suffers G-LOC (Loss of Consciousness).
/// </summary>
public readonly struct PilotGLOCOccurredEvent : IGameEvent
{
    public readonly int PilotId;
    public readonly float SustainedG;
    public readonly float IncapacitationDurationSeconds;

    public PilotGLOCOccurredEvent(int pilotId, float sustainedG, float duration)
    {
        PilotId = pilotId;
        SustainedG = sustainedG;
        IncapacitationDurationSeconds = duration;
    }
}

/// <summary>
/// Event raised when a pilot regains consciousness after G-LOC.
/// </summary>
public readonly struct PilotConsciousnessRecoveredEvent : IGameEvent
{
    public readonly int PilotId;

    public PilotConsciousnessRecoveredEvent(int pilotId)
    {
        PilotId = pilotId;
    }
}

/// <summary>
/// Event raised when pilot visual impairment (tunnel vision, greyout, redout) changes.
/// </summary>
public readonly struct PilotVisualImpairmentChangedEvent : IGameEvent
{
    public readonly int PilotId;
    public readonly float TunnelVisionRadius01; // 1 = clear 180 deg, 0 = complete blackout
    public readonly float GreyoutDesaturation01;
    public readonly float RedoutIntensity01;

    public PilotVisualImpairmentChangedEvent(int pilotId, float tunnel, float grey, float red)
    {
        PilotId = pilotId;
        TunnelVisionRadius01 = tunnel;
        GreyoutDesaturation01 = grey;
        RedoutIntensity01 = red;
    }
}

/// <summary>
/// Aerospace medicine physiology simulator modeling positive/negative G-forces,
/// cerebral blood pressure, Greyout, Redout, and G-LOC with dual 2D/3D support.
/// </summary>
[DisallowMultipleComponent]
public class GFatigueAndPilotPhysiologySim : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Rigidbodies")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 150;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;

    [Header("Pilot Baseline Physiology")]
    [SerializeField, Range(0.5f, 2.0f)] private float crewSkillLevel = 1.0f; // Multiplier on stamina and tolerance
    [SerializeField] private bool hasAntiGSuit = true;
    [SerializeField] private float antiGSuitBonusG = 2.0f; // Adds +2.0 G tolerance
    [SerializeField] private float basePositiveGTolerance = 5.0f; // Baseline black-out onset
    [SerializeField] private float baseNegativeGTolerance = -2.5f; // Baseline red-out onset

    [Header("Stamina & Incapacitation")]
    [SerializeField] private float maxStaminaSeconds = 35f;
    [SerializeField] private float glocAbsoluteIncapacitationDuration = 10f;
    [SerializeField] private float glocConfusionRecoveryDuration = 6f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime physiological state
    private Vector3 previousVelocity;
    private float currentGz = 1.0f;
    private float currentStamina;
    private float glocTimer = 0f;
    private bool isInGLOC = false;

    // Output visual impairments for post-processing shaders
    public PilotPhysiologyState CurrentState { get; private set; } = PilotPhysiologyState.NominalConscious;
    public float TunnelVisionFactor { get; private set; } = 1.0f; // 1.0 = clear, 0.0 = black
    public float GreyoutFactor { get; private set; } = 0.0f;       // 0.0 = full color, 1.0 = desaturated
    public float RedoutFactor { get; private set; } = 0.0f;        // 0.0 = clear, 1.0 = deep red
    public float CurrentGz => currentGz;
    public bool IsInGLOC => isInGLOC;
    public float StaminaFraction => Mathf.Clamp01(currentStamina / maxStaminaSeconds);

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "PilotPhysiology_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();

        currentStamina = maxStaminaSeconds * crewSkillLevel;

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
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        CalculateGForce(deltaTime);
        UpdatePhysiologyAndGLOC(deltaTime);
    }

    private void CalculateGForce(float dt)
    {
        Vector3 currentVel = Vector3.zero;
        if (!defaultTo2D && rb3D != null) currentVel = rb3D.linearVelocity;
        else if (defaultTo2D && rb2D != null) currentVel = new Vector3(rb2D.linearVelocity.x, rb2D.linearVelocity.y, 0f);

        Vector3 acc = (currentVel - previousVelocity) / Mathf.Max(0.0001f, dt);
        previousVelocity = currentVel;

        if (!defaultTo2D)
        {
            // In 3D: Acceleration along aircraft vertical axis (transform.up) plus 1G gravity
            Vector3 localAcc = transform.InverseTransformDirection(acc + (Vector3.up * 9.81f));
            currentGz = localAcc.y / 9.81f;
        }
        else
        {
            // In 2D: Turn centripetal load factor
            float speed = currentVel.magnitude;
            float rotSpeed = rb2D != null ? rb2D.angularVelocity * Mathf.Deg2Rad : 0f;
            currentGz = 1.0f + (Mathf.Abs(speed * rotSpeed) / 9.81f);
        }
    }

    private void UpdatePhysiologyAndGLOC(float dt)
    {
        // G-LOC Recovery state machine
        if (isInGLOC)
        {
            glocTimer -= dt;
            if (glocTimer <= 0f)
            {
                isInGLOC = false;
                CurrentState = PilotPhysiologyState.NominalConscious;
                TunnelVisionFactor = 1.0f;
                GreyoutFactor = 0.0f;
                RedoutFactor = 0.0f;
                EventBus<PilotConsciousnessRecoveredEvent>.Raise(new PilotConsciousnessRecoveredEvent(gameObject.GetInstanceID()));
            }
            return;
        }

        // Compute effective G tolerance thresholds
        float suitBonus = hasAntiGSuit ? antiGSuitBonusG : 0f;
        float staminaRatio = StaminaFraction;
        float effectivePositiveTolerance = (basePositiveGTolerance + suitBonus) * (0.6f + (0.4f * staminaRatio)) * crewSkillLevel;
        float effectiveNegativeTolerance = baseNegativeGTolerance * crewSkillLevel;

        // Drain or recover stamina
        if (currentGz > 4.5f)
        {
            // AGSM and high G muscle strain burns stamina rapidly
            float drainRate = Mathf.Pow((currentGz - 4.0f), 1.6f);
            currentStamina = Mathf.Max(0f, currentStamina - (drainRate * dt));
        }
        else if (currentGz < 2.5f)
        {
            // Cardiovascular recovery under low G
            currentStamina = Mathf.Min(maxStaminaSeconds * crewSkillLevel, currentStamina + (2.5f * dt));
        }

        // Positive G Impairments (Greyout -> Blackout -> G-LOC)
        if (currentGz > effectivePositiveTolerance)
        {
            float excessG = currentGz - effectivePositiveTolerance;
            GreyoutFactor = Mathf.Clamp01(excessG / 1.5f);
            TunnelVisionFactor = Mathf.Clamp01(1.0f - (excessG / 2.5f));
            RedoutFactor = 0f;

            if (excessG > 2.5f)
            {
                CurrentState = PilotPhysiologyState.BlackoutBlindness;
            }
            else
            {
                CurrentState = PilotPhysiologyState.GreyoutTunnelVision;
            }

            // G-LOC trigger threshold
            if (excessG > 3.5f || (excessG > 1.8f && currentStamina <= 0.01f))
            {
                TriggerGLOC();
                return;
            }
        }
        // Negative G Impairments (Redout)
        else if (currentGz < effectiveNegativeTolerance)
        {
            float excessNegG = effectiveNegativeTolerance - currentGz;
            RedoutFactor = Mathf.Clamp01(excessNegG / 1.8f);
            GreyoutFactor = 0f;
            TunnelVisionFactor = 1.0f;
            CurrentState = PilotPhysiologyState.RedoutNegativeG;
        }
        // Nominal
        else
        {
            GreyoutFactor = Mathf.MoveTowards(GreyoutFactor, 0f, 1.5f * dt);
            RedoutFactor = Mathf.MoveTowards(RedoutFactor, 0f, 1.5f * dt);
            TunnelVisionFactor = Mathf.MoveTowards(TunnelVisionFactor, 1.0f, 1.2f * dt);
            CurrentState = PilotPhysiologyState.NominalConscious;
        }

        EventBus<PilotVisualImpairmentChangedEvent>.Raise(new PilotVisualImpairmentChangedEvent(
            gameObject.GetInstanceID(),
            TunnelVisionFactor,
            GreyoutFactor,
            RedoutFactor
        ));
    }

    private void TriggerGLOC()
    {
        isInGLOC = true;
        CurrentState = PilotPhysiologyState.GLOC_Unconscious;
        TunnelVisionFactor = 0.0f;
        GreyoutFactor = 1.0f;
        RedoutFactor = 0.0f;

        float totalDuration = (glocAbsoluteIncapacitationDuration + glocConfusionRecoveryDuration) / crewSkillLevel;
        glocTimer = totalDuration;

        EventBus<PilotGLOCOccurredEvent>.Raise(new PilotGLOCOccurredEvent(
            gameObject.GetInstanceID(),
            currentGz,
            totalDuration
        ));
    }

    /// <summary>
    /// Modifies pilot flight control inputs: returns zero/neutral stick during G-LOC.
    /// </summary>
    public void FilterPilotControls(ref float pitch, ref float roll, ref float yaw)
    {
        if (isInGLOC)
        {
            pitch = 0f;
            roll = 0f;
            yaw = 0f;
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public float Stamina;
        public bool InGLOC;
        public float GLOCTimer;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Stamina = currentStamina,
            InGLOC = isInGLOC,
            GLOCTimer = glocTimer
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentStamina = container.Stamina;
        isInGLOC = container.InGLOC;
        glocTimer = container.GLOCTimer;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 hudPos = transform.position + (Vector3.up * 1.8f);
        if (defaultTo2D) hudPos = transform.position;

        // Draw G-Force meter dial
        Color gCol = isInGLOC ? Color.black : (currentGz > 6.0f ? Color.red : (currentGz < 0f ? Color.magenta : Color.green));
        Gizmos.color = gCol;
        Gizmos.DrawWireSphere(hudPos, 0.35f);

        // Draw Tunnel Vision Cone
        Gizmos.color = new Color(0.2f, 0.2f, 0.2f, 0.6f);
        Vector3 fwd = defaultTo2D ? (Vector3)transform.up : transform.forward;
        Gizmos.DrawRay(hudPos, fwd * (TunnelVisionFactor * 2.5f));
    }
}
