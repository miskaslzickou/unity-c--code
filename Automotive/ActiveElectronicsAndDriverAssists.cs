using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Type of active driver assist intervention.
/// </summary>
public enum AssistType
{
    ABS,
    TCS,
    ESC,
    LaunchControl
}

/// <summary>
/// Event raised when an active electronic assist intervenes.
/// </summary>
public readonly struct AssistInterventionEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly AssistType Assist;
    public readonly float InterventionIntensity;

    public AssistInterventionEvent(int vehicleId, AssistType assist, float intensity)
    {
        VehicleId = vehicleId;
        Assist = assist;
        InterventionIntensity = intensity;
    }
}

/// <summary>
/// Real-time automotive electronic control unit (ECU) stability and traction assists
/// (ABS, TCS, ESC, and Launch Control) wired to wheel sensors with dual 2D/3D vehicle support.
/// </summary>
[DisallowMultipleComponent]
public class ActiveElectronicsAndDriverAssists : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Mechanical Coupling")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private PacejkaTireAndDriftPhysics tirePhysics;
    [SerializeField] private InternalCombustionAndTurboSim engine;
    [SerializeField] private DrivetrainAndDifferentialSolver drivetrain;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;

    [Header("ABS (Anti-Lock Braking System)")]
    [SerializeField] private bool enableABS = true;
    [SerializeField, Range(-0.4f, -0.1f)] private float absLockupThresholdSlip = -0.20f;
    [SerializeField, Range(10f, 40f)] private float absPulsationFrequencyHz = 22f;

    [Header("TCS (Traction Control System)")]
    [SerializeField] private bool enableTCS = true;
    [SerializeField, Range(0.05f, 0.4f)] private float tcsSlipThreshold = 0.16f;
    [SerializeField, Range(0.1f, 2.0f)] private float tcsThrottleCutGain = 1.2f;

    [Header("ESC (Electronic Stability Control)")]
    [SerializeField] private bool enableESC = true;
    [SerializeField] private float understeerGradient = 0.002f; // deg / (m/s^2)
    [SerializeField] private float escYawDeadzoneRadPerSec = 0.08f;
    [SerializeField] private float escCorrectiveBrakeTorqueNm = 900f;

    [Header("Launch Control")]
    [SerializeField] private bool enableLaunchControl = true;
    [SerializeField] private float launchTargetRPM = 4200f;
    [SerializeField] private float launchSpeedThresholdM_s = 1.2f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Active Assist Status
    public bool IsABSActive { get; private set; }
    public bool IsTCSActive { get; private set; }
    public bool IsESCActive { get; private set; }
    public bool IsLaunchControlArmed { get; private set; }
    public bool IsLaunchControlActive { get; private set; }

    // ABS Modulation timer per wheel
    private readonly float[] absTimers = new float[4];
    private readonly bool[] absDumping = new bool[4];

    public string SaveKey => "ECU_Assists_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        if (tirePhysics == null) tirePhysics = GetComponent<PacejkaTireAndDriftPhysics>();
        if (engine == null) engine = GetComponent<InternalCombustionAndTurboSim>();
        if (drivetrain == null) drivetrain = GetComponent<DrivetrainAndDifferentialSolver>();
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();

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
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f || tirePhysics == null || tirePhysics.Wheels == null || tirePhysics.Wheels.Length < 4)
            return;

        UpdateABS(dt);
        UpdateTCS();
        UpdateESC();
        UpdateLaunchControl();
    }

    // =========================================================================
    // 1. ABS (ANTI-LOCK BRAKING SYSTEM)
    // =========================================================================

    private void UpdateABS(float dt)
    {
        if (!enableABS)
        {
            IsABSActive = false;
            return;
        }

        bool anyActive = false;
        float pulsePeriod = 1f / absPulsationFrequencyHz;

        for (int i = 0; i < 4; i++)
        {
            WheelTireData w = tirePhysics.Wheels[i];
            if (w.BrakeTorqueNm > 50f && w.SlipRatio < absLockupThresholdSlip)
            {
                anyActive = true;
                absTimers[i] += dt;
                if (absTimers[i] >= pulsePeriod)
                {
                    absTimers[i] = 0f;
                    absDumping[i] = !absDumping[i];
                }

                if (absDumping[i])
                {
                    // Dump brake pressure to restore wheel rotation
                    w.BrakeTorqueNm *= 0.15f;
                }
            }
            else
            {
                absTimers[i] = 0f;
                absDumping[i] = false;
            }
        }

        IsABSActive = anyActive;
        if (anyActive)
        {
            EventBus<AssistInterventionEvent>.Raise(new AssistInterventionEvent(gameObject.GetInstanceID(), AssistType.ABS, 1.0f));
        }
    }

    // =========================================================================
    // 2. TCS (TRACTION CONTROL SYSTEM)
    // =========================================================================

    private void UpdateTCS()
    {
        if (!enableTCS || engine == null)
        {
            IsTCSActive = false;
            return;
        }

        float maxPositiveSlip = 0f;
        for (int i = 0; i < 4; i++)
        {
            WheelTireData w = tirePhysics.Wheels[i];
            if (w.IsDriven && w.SlipRatio > maxPositiveSlip)
            {
                maxPositiveSlip = w.SlipRatio;
            }
        }

        if (maxPositiveSlip > tcsSlipThreshold)
        {
            IsTCSActive = true;
            float excessSlip = maxPositiveSlip - tcsSlipThreshold;
            float throttleCut = Mathf.Clamp01(excessSlip * tcsThrottleCutGain);

            float currentThrot = engine.CurrentThrottle;
            engine.SetThrottle(currentThrot * (1f - throttleCut));

            EventBus<AssistInterventionEvent>.Raise(new AssistInterventionEvent(gameObject.GetInstanceID(), AssistType.TCS, throttleCut));
        }
        else
        {
            IsTCSActive = false;
        }
    }

    // =========================================================================
    // 3. ESC (ELECTRONIC STABILITY CONTROL)
    // =========================================================================

    private void UpdateESC()
    {
        if (!enableESC)
        {
            IsESCActive = false;
            return;
        }

        // Measure longitudinal speed and yaw rate
        float speed = 0f;
        float actualYawRate = 0f;

        if (!defaultTo2D && rb3D != null)
        {
            speed = transform.InverseTransformDirection(rb3D.linearVelocity).z;
            actualYawRate = rb3D.angularVelocity.y;
        }
        else if (defaultTo2D && rb2D != null)
        {
            Vector2 v = rb2D.linearVelocity;
            speed = transform.InverseTransformDirection(new Vector3(v.x, v.y, 0f)).y;
            actualYawRate = rb2D.angularVelocity * Mathf.Deg2Rad;
        }

        if (Mathf.Abs(speed) < 3.0f)
        {
            IsESCActive = false;
            return; // Inactive at very low speeds
        }

        // Calculate intended yaw rate from steering angle (Bicycle Kinematic Model)
        float steerAngleRad = tirePhysics.Wheels[0].SteerAngleDeg * Mathf.Deg2Rad;
        float wheelbase = 2.65f;
        float intendedYawRate = (speed / (wheelbase + understeerGradient * speed * speed)) * steerAngleRad;

        float yawRateError = intendedYawRate - actualYawRate;

        if (Mathf.Abs(yawRateError) > escYawDeadzoneRadPerSec)
        {
            IsESCActive = true;
            float intensity = Mathf.Clamp01(Mathf.Abs(yawRateError) / 0.5f);

            if (Mathf.Abs(actualYawRate) > Mathf.Abs(intendedYawRate) && Mathf.Sign(actualYawRate) == Mathf.Sign(intendedYawRate))
            {
                // OVERSTEER: Chassis is spinning. Asymmetrically brake OUTSIDE FRONT wheel to create restoring yaw
                if (actualYawRate > 0f)
                {
                    // Turning right, oversteering -> brake FrontLeft (Outside Front)
                    tirePhysics.Wheels[0].BrakeTorqueNm += escCorrectiveBrakeTorqueNm * intensity;
                }
                else
                {
                    // Turning left, oversteering -> brake FrontRight (Outside Front)
                    tirePhysics.Wheels[1].BrakeTorqueNm += escCorrectiveBrakeTorqueNm * intensity;
                }
            }
            else
            {
                // UNDERSTEER: Car plowing straight. Asymmetrically brake INSIDE REAR wheel to rotate into corner
                if (intendedYawRate > 0f)
                {
                    // Steer right, understeering -> brake RearRight (Inside Rear)
                    tirePhysics.Wheels[3].BrakeTorqueNm += escCorrectiveBrakeTorqueNm * intensity;
                }
                else
                {
                    // Steer left, understeering -> brake RearLeft (Inside Rear)
                    tirePhysics.Wheels[2].BrakeTorqueNm += escCorrectiveBrakeTorqueNm * intensity;
                }
            }

            EventBus<AssistInterventionEvent>.Raise(new AssistInterventionEvent(gameObject.GetInstanceID(), AssistType.ESC, intensity));
        }
        else
        {
            IsESCActive = false;
        }
    }

    // =========================================================================
    // 4. LAUNCH CONTROL
    // =========================================================================

    private void UpdateLaunchControl()
    {
        if (!enableLaunchControl || engine == null)
        {
            IsLaunchControlArmed = false;
            IsLaunchControlActive = false;
            return;
        }

        float speed = 0f;
        if (!defaultTo2D && rb3D != null) speed = rb3D.linearVelocity.magnitude;
        else if (defaultTo2D && rb2D != null) speed = rb2D.linearVelocity.magnitude;

        bool isStationary = speed < launchSpeedThresholdM_s;
        bool fullThrottle = engine.CurrentThrottle > 0.9f;
        bool clutchDisengaged = drivetrain != null && (drivetrain.ClutchPedal > 0.7f || drivetrain.CurrentGear == 0);

        if (isStationary && fullThrottle && clutchDisengaged)
        {
            IsLaunchControlArmed = true;
            IsLaunchControlActive = true;

            // Hold engine RPM at optimal launch RPM target
            if (engine.CurrentRPM > launchTargetRPM)
            {
                engine.SetThrottle(0.2f); // Cut throttle to hold RPM at target
            }
        }
        else if (IsLaunchControlActive)
        {
            // Transitioning into launch execution
            if (speed > 8.0f || !fullThrottle)
            {
                IsLaunchControlArmed = false;
                IsLaunchControlActive = false;
            }
        }
        else
        {
            IsLaunchControlArmed = false;
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public bool ABS;
        public bool TCS;
        public bool ESC;
        public bool Launch;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            ABS = enableABS,
            TCS = enableTCS,
            ESC = enableESC,
            Launch = enableLaunchControl
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        enableABS = container.ABS;
        enableTCS = container.TCS;
        enableESC = container.ESC;
        enableLaunchControl = container.Launch;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 root = transform.position + (Vector3.up * 1.2f);
        if (defaultTo2D) root = transform.position;

        // Draw indicator spheres for ECU assist status
        Gizmos.color = IsABSActive ? Color.red : Color.gray;
        Gizmos.DrawWireSphere(root + (Vector3.left * 0.6f), 0.15f);

        Gizmos.color = IsTCSActive ? Color.yellow : Color.gray;
        Gizmos.DrawWireSphere(root + (Vector3.left * 0.2f), 0.15f);

        Gizmos.color = IsESCActive ? Color.cyan : Color.gray;
        Gizmos.DrawWireSphere(root + (Vector3.right * 0.2f), 0.15f);

        Gizmos.color = IsLaunchControlActive ? Color.green : Color.gray;
        Gizmos.DrawWireSphere(root + (Vector3.right * 0.6f), 0.15f);
    }
}
