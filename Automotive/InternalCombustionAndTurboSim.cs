using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Type of forced induction system fitted to the internal combustion engine.
/// </summary>
public enum ForcedInductionType
{
    NaturallyAspirated,
    Turbocharger,
    Supercharger,
    TwinTurbo
}

/// <summary>
/// Event raised when the engine hits the rev limiter.
/// </summary>
public readonly struct RevLimiterHitEvent : IGameEvent
{
    public readonly int EngineId;
    public readonly float RPM;
    public readonly bool IsHardCut;

    public RevLimiterHitEvent(int engineId, float rpm, bool hardCut)
    {
        EngineId = engineId;
        RPM = rpm;
        IsHardCut = hardCut;
    }
}

/// <summary>
/// Event raised when the Blow-Off Valve (BOV) vents pressurized boost upon throttle closure.
/// </summary>
public readonly struct BlowOffValveVentedEvent : IGameEvent
{
    public readonly int EngineId;
    public readonly float VentedPressureBar;

    public BlowOffValveVentedEvent(int engineId, float pressure)
    {
        EngineId = engineId;
        VentedPressureBar = pressure;
    }
}

/// <summary>
/// High-fidelity physical internal combustion engine (ICE) and forced-induction
/// (turbocharger / supercharger) simulator with dual 2D/3D vehicle compatibility.
/// </summary>
[DisallowMultipleComponent]
public class InternalCombustionAndTurboSim : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Audio Interop")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private ProceduralAudioSynthesizer audioSynth;

    [Header("Engine Core Specifications")]
    [SerializeField, Range(1, 16)] private int cylinderCount = 4;
    [SerializeField, Range(500f, 1500f)] private float idleRPM = 850f;
    [SerializeField, Range(4000f, 12000f)] private float redlineRPM = 7200f;
    [SerializeField, Range(0.05f, 1.5f)] private float flywheelInertia = 0.25f; // kg * m^2
    [SerializeField] private AnimationCurve naturalTorqueCurveNm;

    [Header("Friction & Engine Braking")]
    [SerializeField] private float idleFrictionTorqueNm = 25f;
    [SerializeField] private float viscousFrictionCoeff = 0.08f;
    [SerializeField] private float pumpingLossFactor = 45f; // Closed-throttle engine braking

    [Header("Forced Induction & Turbo Dynamics")]
    [SerializeField] private ForcedInductionType forcedInduction = ForcedInductionType.Turbocharger;
    [SerializeField, Range(0.1f, 3.5f)] private float wastegateBoostBar = 1.2f; // Max target boost gauge
    [SerializeField, Range(0.5f, 5.0f)] private float turboSpoolRate = 2.2f;   // Spool-up response rate
    [SerializeField, Range(0.5f, 5.0f)] private float turboLagInertia = 1.5f;  // Resistance to spooling
    [SerializeField] private float bovReleaseThresholdBar = 0.35f;

    [Header("Rev Limiter Settings")]
    [SerializeField] private float softLimiterMarginRPM = 150f;
    [SerializeField] private float hardCutDurationSeconds = 0.04f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float gizmoGaugeRadius = 0.6f;

    // Runtime state
    private float currentRPM;
    private float currentThrottle; // 0..1
    private float externalLoadTorqueNm; // Applied by drivetrain/clutch
    private float currentBoostBar; // 0..wastegate
    private float previousThrottle;
    private float revLimiterCutTimer;
    private bool isIgnitionCut;

    public string SaveKey => "ICE_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public float CurrentRPM => currentRPM;
    public float CurrentBoostBar => currentBoostBar;
    public float CurrentThrottle => currentThrottle;
    public bool IsRevLimiterActive => isIgnitionCut;

    /// <summary>
    /// Crankshaft angular velocity in radians per second.
    /// </summary>
    public float CrankAngularVelocityRadPerSec => (currentRPM * 2f * Mathf.PI) / 60f;

    /// <summary>
    /// Total instantaneous engine output torque available at the flywheel.
    /// </summary>
    public float OutputTorqueNm { get; private set; }

    private void Awake()
    {
        currentRPM = idleRPM;
        if (naturalTorqueCurveNm == null || naturalTorqueCurveNm.length < 2)
        {
            naturalTorqueCurveNm = CreateDefaultTorqueCurve();
        }

        if (audioSynth == null)
        {
            audioSynth = GetComponent<ProceduralAudioSynthesizer>();
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

    private AnimationCurve CreateDefaultTorqueCurve()
    {
        var curve = new AnimationCurve();
        curve.AddKey(new Keyframe(800f, 120f));
        curve.AddKey(new Keyframe(2500f, 210f));
        curve.AddKey(new Keyframe(4500f, 280f));
        curve.AddKey(new Keyframe(6000f, 260f));
        curve.AddKey(new Keyframe(7500f, 190f));
        return curve;
    }

    // =========================================================================
    // ENGINE INPUT CONTROL & EXTERNAL COUPLING
    // =========================================================================

    public void SetThrottle(float throttle)
    {
        currentThrottle = Mathf.Clamp01(throttle);
    }

    public void ApplyExternalLoad(float loadTorqueNm)
    {
        externalLoadTorqueNm = Mathf.Max(0f, loadTorqueNm);
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        SimulateEnginePhysics(Time.fixedDeltaTime);

        if (audioSynth != null)
        {
            audioSynth.SetEngineParameters(currentRPM, currentThrottle);
        }
    }

    public void SimulateEnginePhysics(float dt)
    {
        if (dt <= 0f) return;

        // 1. Check Blow-Off Valve (BOV) venting on rapid throttle lift
        if (forcedInduction == ForcedInductionType.Turbocharger || forcedInduction == ForcedInductionType.TwinTurbo)
        {
            if (previousThrottle > 0.4f && currentThrottle < 0.1f && currentBoostBar >= bovReleaseThresholdBar)
            {
                EventBus<BlowOffValveVentedEvent>.Raise(new BlowOffValveVentedEvent(gameObject.GetInstanceID(), currentBoostBar));
                if (audioSynth != null)
                {
                    audioSynth.TriggerLaserTransient(3000f, 0.12f); // BOV hiss transient
                }
                currentBoostBar = Mathf.Max(0f, currentBoostBar - 0.5f);
            }
        }
        previousThrottle = currentThrottle;

        // 2. Forced Induction Boost Dynamics (Turbo Spool / Supercharger)
        SimulateBoost(dt);

        // 3. Rev Limiter evaluation
        UpdateRevLimiter(dt);

        // 4. Torque Generation
        float combustionTorque = 0f;
        if (!isIgnitionCut)
        {
            float baseTorque = naturalTorqueCurveNm.Evaluate(currentRPM);
            // Boost multiplier: manifold pressure / atmospheric pressure (1 bar)
            float manifoldPressureMultiplier = 1.0f + currentBoostBar;
            combustionTorque = baseTorque * currentThrottle * manifoldPressureMultiplier;

            // Idle Air Control (IAC): prevents engine stall below idle RPM
            if (currentRPM < idleRPM)
            {
                float iacBoost = Mathf.Clamp01((idleRPM - currentRPM) / 300f);
                combustionTorque += baseTorque * iacBoost * 0.4f;
            }
        }

        // 5. Internal Engine Friction & Pumping Losses (Engine Braking)
        float radPerSec = CrankAngularVelocityRadPerSec;
        float internalFriction = idleFrictionTorqueNm + (viscousFrictionCoeff * radPerSec);
        float pumpingLoss = (1f - currentThrottle) * (pumpingLossFactor * (currentRPM / redlineRPM));
        float totalResistiveTorque = internalFriction + pumpingLoss + externalLoadTorqueNm;

        // 6. Net Flywheel Angular Acceleration: alpha = (T_comb - T_resist) / Inertia
        float netTorque = combustionTorque - totalResistiveTorque;
        float angularAcceleration = netTorque / flywheelInertia;

        radPerSec += angularAcceleration * dt;
        currentRPM = Mathf.Max(200f, (radPerSec * 60f) / (2f * Mathf.PI));

        OutputTorqueNm = Mathf.Max(0f, combustionTorque - internalFriction);
    }

    private void SimulateBoost(float dt)
    {
        switch (forcedInduction)
        {
            case ForcedInductionType.NaturallyAspirated:
                currentBoostBar = 0f;
                break;

            case ForcedInductionType.Turbocharger:
            case ForcedInductionType.TwinTurbo:
                // Exhaust gas mass flow proportional to RPM and throttle
                float exhaustEnergy = (currentRPM / redlineRPM) * currentThrottle;
                float targetBoost = Mathf.Clamp(exhaustEnergy * wastegateBoostBar * 1.3f, 0f, wastegateBoostBar);

                // Spool up with turbo lag inertia
                float spoolRate = (targetBoost > currentBoostBar) ? (turboSpoolRate / turboLagInertia) : (turboSpoolRate * 1.5f);
                currentBoostBar = Mathf.MoveTowards(currentBoostBar, targetBoost, spoolRate * dt);
                break;

            case ForcedInductionType.Supercharger:
                // Instant belt-driven boost directly linked to RPM
                currentBoostBar = Mathf.Clamp((currentRPM / redlineRPM) * wastegateBoostBar * currentThrottle, 0f, wastegateBoostBar);
                break;
        }
    }

    private void UpdateRevLimiter(float dt)
    {
        if (revLimiterCutTimer > 0f)
        {
            revLimiterCutTimer -= dt;
            if (revLimiterCutTimer <= 0f)
            {
                isIgnitionCut = false;
            }
        }

        if (currentRPM >= redlineRPM)
        {
            isIgnitionCut = true;
            revLimiterCutTimer = hardCutDurationSeconds;
            EventBus<RevLimiterHitEvent>.Raise(new RevLimiterHitEvent(gameObject.GetInstanceID(), currentRPM, hardCut: true));
        }
        else if (currentRPM >= redlineRPM - softLimiterMarginRPM)
        {
            // Soft spark retard / partial cut
            if (UnityEngine.Random.value < 0.4f)
            {
                isIgnitionCut = true;
                revLimiterCutTimer = 0.015f;
            }
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public float RPM;
        public float BoostBar;
        public float Throttle;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            RPM = currentRPM,
            BoostBar = currentBoostBar,
            Throttle = currentThrottle
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentRPM = container.RPM;
        currentBoostBar = container.BoostBar;
        currentThrottle = container.Throttle;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 pos = transform.position;
        if (defaultTo2D) pos.z = 0f;

        // Draw RPM Dial Gauge
        Gizmos.color = isIgnitionCut ? Color.red : Color.green;
        Gizmos.DrawWireSphere(pos, gizmoGaugeRadius);

        float rpmFraction = Mathf.Clamp01(currentRPM / redlineRPM);
        float needleAngle = Mathf.Lerp(135f, -135f, rpmFraction);
        Vector3 needleDir = Quaternion.Euler(0f, 0f, needleAngle) * Vector3.up;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(pos, pos + (needleDir * gizmoGaugeRadius * 0.9f));

        // Draw Boost indicator arc/vector
        if (forcedInduction != ForcedInductionType.NaturallyAspirated)
        {
            Gizmos.color = Color.cyan;
            Vector3 boostOffset = pos + (Vector3.up * (gizmoGaugeRadius + 0.2f));
            Gizmos.DrawLine(boostOffset, boostOffset + (Vector3.right * (currentBoostBar * 0.5f)));
        }
    }
}
