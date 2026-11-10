using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Driving surface type affecting tire friction coefficient.
/// </summary>
public enum RoadSurfaceType
{
    DryTarmac,
    WetTarmac,
    Gravel,
    SnowIce,
    GrassDirt
}

/// <summary>
/// Event raised when a tire skids excessively (spawning tire smoke and skidmark telemetry).
/// </summary>
public readonly struct TireSkidEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly int WheelIndex;
    public readonly Vector3 ContactPoint;
    public readonly float SlipMagnitude;
    public readonly float SurfaceTemperature;

    public TireSkidEvent(int vehicleId, int wheelIndex, Vector3 point, float slip, float temp)
    {
        VehicleId = vehicleId;
        WheelIndex = wheelIndex;
        ContactPoint = point;
        SlipMagnitude = slip;
        SurfaceTemperature = temp;
    }
}

/// <summary>
/// Event raised when a tire blows out due to total compound wear or severe thermal degradation.
/// </summary>
public readonly struct TireBlownEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly int WheelIndex;

    public TireBlownEvent(int vehicleId, int wheelIndex)
    {
        VehicleId = vehicleId;
        WheelIndex = wheelIndex;
    }
}

/// <summary>
/// Individual wheel state and physical tire dynamics.
/// </summary>
[System.Serializable]
public class WheelTireData
{
    public string WheelName;
    public Vector3 LocalOffset;
    public bool IsSteered;
    public bool IsDriven;
    public float WheelRadius = 0.33f; // meters
    public float WheelInertia = 1.2f;  // kg * m^2

    // Runtime physical state
    [HideInInspector] public float AngularVelocityRadPerSec;
    [HideInInspector] public float SteerAngleDeg;
    [HideInInspector] public float DriveTorqueNm;
    [HideInInspector] public float BrakeTorqueNm;
    [HideInInspector] public float NormalLoadN;
    [HideInInspector] public float SlipRatio;     // kappa
    [HideInInspector] public float SlipAngleDeg;  // alpha
    [HideInInspector] public float LongForceN;    // Fx
    [HideInInspector] public float LatForceN;     // Fy
    [HideInInspector] public float SurfaceTempC = 30f;
    [HideInInspector] public float CoreTempC = 30f;
    [HideInInspector] public float Wear01 = 0f;
    [HideInInspector] public bool IsBlown = false;

    public WheelTireData(string name, Vector3 offset, bool steered, bool driven)
    {
        WheelName = name;
        LocalOffset = offset;
        IsSteered = steered;
        IsDriven = driven;
        SurfaceTempC = 30f;
        CoreTempC = 30f;
        Wear01 = 0f;
        IsBlown = false;
    }
}

/// <summary>
/// High-fidelity Pacejka "Magic Formula" tire grip, dynamic weight transfer,
/// friction ellipse, and drift physics for both 3D Rigidbody and 2D Rigidbody2D vehicles.
/// </summary>
[DisallowMultipleComponent]
public class PacejkaTireAndDriftPhysics : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Rigidbodies")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private DrivetrainAndDifferentialSolver drivetrain;

    [Header("Vehicle Dimensions & Weight")]
    [SerializeField] private float vehicleMassKg = 1450f;
    [SerializeField] private float cgHeightM = 0.45f;
    [SerializeField] private float wheelbaseM = 2.65f;
    [SerializeField] private float trackWidthM = 1.60f;
    [SerializeField, Range(0.3f, 0.7f)] private float weightDistributionFront = 0.52f; // 52% front

    [Header("Pacejka Magic Formula Coefficients (B, C, D, E)")]
    [SerializeField] private float stiffnessB_Long = 10.0f;
    [SerializeField] private float shapeC_Long = 1.65f;
    [SerializeField] private float peakFrictionD_Long = 1.15f;
    [SerializeField] private float curvatureE_Long = 0.97f;

    [SerializeField] private float stiffnessB_Lat = 8.5f;
    [SerializeField] private float shapeC_Lat = 1.30f;
    [SerializeField] private float peakFrictionD_Lat = 1.10f;
    [SerializeField] private float curvatureE_Lat = -1.20f;

    [Header("Surface & Thermal Settings")]
    [SerializeField] private RoadSurfaceType currentSurface = RoadSurfaceType.DryTarmac;
    [SerializeField] private float optimalTempMinC = 70f;
    [SerializeField] private float optimalTempMaxC = 95f;
    [SerializeField] private float thermalConductivity = 0.08f;
    [SerializeField] private float coolingCoeff = 0.05f;

    [Header("Steering & Brakes")]
    [SerializeField] private float maxSteerAngleDeg = 36f;
    [SerializeField] private float maxBrakeTorqueNm = 2500f;

    [Header("Wheels Configuration (FL, FR, RL, RR)")]
    [SerializeField] private WheelTireData[] wheels;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime inputs
    private float currentSteerInput = 0f; // -1..+1
    private float currentBrakeInput = 0f; // 0..1
    private float currentHandbrakeInput = 0f; // 0..1

    public string SaveKey => "PacejkaTire_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public WheelTireData[] Wheels => wheels;
    public RoadSurfaceType CurrentSurface { get => currentSurface; set => currentSurface = value; }

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();
        if (drivetrain == null) drivetrain = GetComponent<DrivetrainAndDifferentialSolver>();

        InitializeDefaultWheels();

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

    private void InitializeDefaultWheels()
    {
        if (wheels != null && wheels.Length == 4) return;

        float halfL = wheelbaseM * 0.5f;
        float halfT = trackWidthM * 0.5f;

        wheels = new WheelTireData[4]
        {
            new WheelTireData("FrontLeft",  new Vector3(-halfT, halfL, 0f), steered: true,  driven: false),
            new WheelTireData("FrontRight", new Vector3( halfT, halfL, 0f), steered: true,  driven: false),
            new WheelTireData("RearLeft",   new Vector3(-halfT, -halfL, 0f), steered: false, driven: true),
            new WheelTireData("RearRight",  new Vector3( halfT, -halfL, 0f), steered: false, driven: true)
        };
    }

    // =========================================================================
    // CONTROL INPUTS
    // =========================================================================

    public void SetSteering(float steer) => currentSteerInput = Mathf.Clamp(steer, -1f, 1f);
    public void SetBrakes(float brake) => currentBrakeInput = Mathf.Clamp01(brake);
    public void SetHandbrake(float handbrake) => currentHandbrakeInput = Mathf.Clamp01(handbrake);

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateSteerAndBrakeTorques();
        CalculateDynamicWeightTransfer();
        SolveTireForcesAndSlip(dt);
        ApplyForcesToRigidbody();
        FeedWheelSpeedsToDrivetrain();
    }

    private void UpdateSteerAndBrakeTorques()
    {
        float steerAngle = currentSteerInput * maxSteerAngleDeg;

        for (int i = 0; i < wheels.Length; i++)
        {
            WheelTireData w = wheels[i];
            if (w.IsSteered)
            {
                // Ackermann geometry simplification
                w.SteerAngleDeg = steerAngle;
            }

            // Normal braking (front-biased 65/35)
            float bias = (i < 2) ? 0.65f : 0.35f;
            w.BrakeTorqueNm = currentBrakeInput * maxBrakeTorqueNm * bias;

            // Rear handbrake lock
            if (i >= 2 && currentHandbrakeInput > 0.01f)
            {
                w.BrakeTorqueNm += currentHandbrakeInput * maxBrakeTorqueNm * 1.5f;
            }

            // Read drive torque from drivetrain
            if (drivetrain != null)
            {
                switch (i)
                {
                    case 0: w.DriveTorqueNm = drivetrain.TorqueFrontLeft; break;
                    case 1: w.DriveTorqueNm = drivetrain.TorqueFrontRight; break;
                    case 2: w.DriveTorqueNm = drivetrain.TorqueRearLeft; break;
                    case 3: w.DriveTorqueNm = drivetrain.TorqueRearRight; break;
                }
            }
        }
    }

    private void CalculateDynamicWeightTransfer()
    {
        // Compute longitudinal and lateral accelerations
        Vector3 localAcc = Vector3.zero;
        if (!defaultTo2D && rb3D != null)
        {
            // Inverse transform world acceleration
            localAcc = transform.InverseTransformDirection(rb3D.linearVelocity);
        }
        else if (defaultTo2D && rb2D != null)
        {
            Vector2 v = rb2D.linearVelocity;
            localAcc = transform.InverseTransformDirection(new Vector3(v.x, v.y, 0f));
        }

        float totalWeightN = vehicleMassKg * 9.81f;
        float staticFrontAxleN = totalWeightN * weightDistributionFront;
        float staticRearAxleN = totalWeightN * (1f - weightDistributionFront);

        // Longitudinal weight transfer: Delta Fz = (M * ax * h) / L
        float deltaZ_Long = (vehicleMassKg * localAcc.z * cgHeightM) / Mathf.Max(0.1f, wheelbaseM);
        float frontAxleN = Mathf.Max(100f, staticFrontAxleN - deltaZ_Long);
        float rearAxleN = Mathf.Max(100f, staticRearAxleN + deltaZ_Long);

        // Lateral weight transfer: Delta Fz_lat = (M * ay * h) / Track
        float deltaZ_LatFront = (frontAxleN * localAcc.x * cgHeightM) / Mathf.Max(0.1f, trackWidthM);
        float deltaZ_LatRear = (rearAxleN * localAcc.x * cgHeightM) / Mathf.Max(0.1f, trackWidthM);

        wheels[0].NormalLoadN = Mathf.Max(50f, (frontAxleN * 0.5f) - deltaZ_LatFront); // FL
        wheels[1].NormalLoadN = Mathf.Max(50f, (frontAxleN * 0.5f) + deltaZ_LatFront); // FR
        wheels[2].NormalLoadN = Mathf.Max(50f, (rearAxleN * 0.5f) - deltaZ_LatRear);   // RL
        wheels[3].NormalLoadN = Mathf.Max(50f, (rearAxleN * 0.5f) + deltaZ_LatRear);   // RR
    }

    private void SolveTireForcesAndSlip(float dt)
    {
        float surfaceMu = GetSurfaceFriction(currentSurface);

        for (int i = 0; i < wheels.Length; i++)
        {
            WheelTireData w = wheels[i];
            if (w.IsBlown)
            {
                w.LongForceN = 0f;
                w.LatForceN = 0f;
                continue;
            }

            Vector3 wheelWorldPos = GetWheelWorldPosition(w);
            Vector3 wheelVel = GetPointVelocity(wheelWorldPos);

            // Transform velocity into wheel heading coordinate frame
            Quaternion wheelRot = Quaternion.Euler(0f, 0f, defaultTo2D ? w.SteerAngleDeg : 0f);
            if (!defaultTo2D) wheelRot = Quaternion.Euler(0f, w.SteerAngleDeg, 0f);

            Vector3 forwardDir = wheelRot * (defaultTo2D ? transform.up : transform.forward);
            Vector3 sideDir = wheelRot * (defaultTo2D ? transform.right : transform.right);

            float vx = Vector3.Dot(wheelVel, forwardDir);
            float vy = Vector3.Dot(wheelVel, sideDir);

            // 1. Wheel rotational acceleration: I * d_omega / dt = T_drive - T_brake - (Fx * R)
            float brakeSign = Mathf.Sign(w.AngularVelocityRadPerSec);
            float netTorque = w.DriveTorqueNm - (w.BrakeTorqueNm * brakeSign) - (w.LongForceN * w.WheelRadius);
            float angularAcc = netTorque / w.WheelInertia;
            w.AngularVelocityRadPerSec += angularAcc * dt;

            // Prevent brake lock oscillation
            if (w.BrakeTorqueNm > Mathf.Abs(w.DriveTorqueNm) && Mathf.Abs(w.AngularVelocityRadPerSec) < 1f)
            {
                w.AngularVelocityRadPerSec = 0f;
            }

            // 2. Slip Ratio (kappa) and Slip Angle (alpha)
            float wheelLinSpeed = w.AngularVelocityRadPerSec * w.WheelRadius;
            w.SlipRatio = (wheelLinSpeed - vx) / Mathf.Max(Mathf.Abs(vx), 0.5f);
            w.SlipAngleDeg = -Mathf.Atan2(vy, Mathf.Max(Mathf.Abs(vx), 0.5f)) * Mathf.Rad2Deg;

            // 3. Thermal Grip Modifier
            float thermalMultiplier = 1.0f;
            if (w.SurfaceTempC < optimalTempMinC)
            {
                thermalMultiplier = Mathf.Lerp(0.75f, 1.0f, (w.SurfaceTempC - 20f) / (optimalTempMinC - 20f));
            }
            else if (w.SurfaceTempC > optimalTempMaxC)
            {
                thermalMultiplier = Mathf.Lerp(1.0f, 0.65f, (w.SurfaceTempC - optimalTempMaxC) / 60f);
            }

            float effectiveMu = surfaceMu * thermalMultiplier * (1f - (w.Wear01 * 0.4f));

            // 4. Pacejka Pure Forces
            float Fx0 = EvaluatePacejka(w.SlipRatio, stiffnessB_Long, shapeC_Long, peakFrictionD_Long * effectiveMu * w.NormalLoadN, curvatureE_Long);
            float Fy0 = EvaluatePacejka(w.SlipAngleDeg * Mathf.Deg2Rad, stiffnessB_Lat, shapeC_Lat, peakFrictionD_Lat * effectiveMu * w.NormalLoadN, curvatureE_Lat);

            // 5. Friction Ellipse Combined Slip (Smooth Drift Transition)
            float rho = Mathf.Sqrt(Mathf.Pow(w.SlipRatio / 0.15f, 2f) + Mathf.Pow((w.SlipAngleDeg * Mathf.Deg2Rad) / 0.12f, 2f));
            if (rho > 1.0f)
            {
                w.LongForceN = Fx0 / rho;
                w.LatForceN = Fy0 / rho;
            }
            else
            {
                w.LongForceN = Fx0;
                w.LatForceN = Fy0;
            }

            // 6. Thermal Update & Skidmark Event
            float slipPower = Mathf.Abs(w.LongForceN * (wheelLinSpeed - vx)) + Mathf.Abs(w.LatForceN * vy);
            w.SurfaceTempC += (slipPower * 0.0003f * dt);
            w.SurfaceTempC = Mathf.MoveTowards(w.SurfaceTempC, w.CoreTempC, thermalConductivity * dt);
            w.CoreTempC = Mathf.MoveTowards(w.CoreTempC, 25f, coolingCoeff * dt);

            // Tire wear accumulation
            w.Wear01 += (slipPower * 0.00000005f * dt);
            if (w.Wear01 >= 1.0f && !w.IsBlown)
            {
                w.IsBlown = true;
                EventBus<TireBlownEvent>.Raise(new TireBlownEvent(gameObject.GetInstanceID(), i));
            }

            if (rho > 1.25f)
            {
                EventBus<TireSkidEvent>.Raise(new TireSkidEvent(gameObject.GetInstanceID(), i, wheelWorldPos, rho, w.SurfaceTempC));
            }
        }
    }

    private float EvaluatePacejka(float slip, float B, float C, float D, float E)
    {
        return D * Mathf.Sin(C * Mathf.Atan(B * slip - E * (B * slip - Mathf.Atan(B * slip))));
    }

    private void ApplyForcesToRigidbody()
    {
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelTireData w = wheels[i];
            if (w.IsBlown) continue;

            Vector3 wheelPos = GetWheelWorldPosition(w);
            Quaternion wheelRot = Quaternion.Euler(0f, 0f, defaultTo2D ? w.SteerAngleDeg : 0f);
            if (!defaultTo2D) wheelRot = Quaternion.Euler(0f, w.SteerAngleDeg, 0f);

            Vector3 fwd = wheelRot * (defaultTo2D ? transform.up : transform.forward);
            Vector3 side = wheelRot * (defaultTo2D ? transform.right : transform.right);
            Vector3 force = (fwd * w.LongForceN) + (side * w.LatForceN);

            if (!defaultTo2D && rb3D != null)
            {
                rb3D.AddForceAtPosition(force, wheelPos, ForceMode.Force);
            }
            else if (defaultTo2D && rb2D != null)
            {
                Vector2 force2D = new Vector2(force.x, force.y);
                Vector2 pos2D = new Vector2(wheelPos.x, wheelPos.y);
                rb2D.AddForceAtPosition(force2D, pos2D, ForceMode2D.Force);
            }
        }
    }

    private void FeedWheelSpeedsToDrivetrain()
    {
        if (drivetrain != null)
        {
            drivetrain.FeedWheelSpeeds(
                wheels[0].AngularVelocityRadPerSec,
                wheels[1].AngularVelocityRadPerSec,
                wheels[2].AngularVelocityRadPerSec,
                wheels[3].AngularVelocityRadPerSec
            );
        }
    }

    private Vector3 GetWheelWorldPosition(WheelTireData w)
    {
        return transform.TransformPoint(w.LocalOffset);
    }

    private Vector3 GetPointVelocity(Vector3 point)
    {
        if (!defaultTo2D && rb3D != null)
        {
            return rb3D.GetPointVelocity(point);
        }
        else if (defaultTo2D && rb2D != null)
        {
            Vector2 p2 = new Vector2(point.x, point.y);
            Vector2 v2 = rb2D.GetPointVelocity(p2);
            return new Vector3(v2.x, v2.y, 0f);
        }
        return Vector3.zero;
    }

    public static float GetSurfaceFriction(RoadSurfaceType type)
    {
        switch (type)
        {
            case RoadSurfaceType.DryTarmac: return 1.0f;
            case RoadSurfaceType.WetTarmac: return 0.75f;
            case RoadSurfaceType.Gravel: return 0.55f;
            case RoadSurfaceType.SnowIce: return 0.22f;
            case RoadSurfaceType.GrassDirt: return 0.60f;
            default: return 1.0f;
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public RoadSurfaceType Surface;
        public float[] WheelWear;
        public float[] WheelTemp;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Surface = currentSurface,
            WheelWear = new float[wheels.Length],
            WheelTemp = new float[wheels.Length]
        };
        for (int i = 0; i < wheels.Length; i++)
        {
            container.WheelWear[i] = wheels[i].Wear01;
            container.WheelTemp[i] = wheels[i].SurfaceTempC;
        }
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        currentSurface = container.Surface;
        if (container.WheelWear != null && container.WheelTemp != null)
        {
            for (int i = 0; i < Mathf.Min(wheels.Length, container.WheelWear.Length); i++)
            {
                wheels[i].Wear01 = container.WheelWear[i];
                wheels[i].SurfaceTempC = container.WheelTemp[i];
            }
        }
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || wheels == null) return;

        for (int i = 0; i < wheels.Length; i++)
        {
            WheelTireData w = wheels[i];
            Vector3 pos = GetWheelWorldPosition(w);

            // Draw tire cylinder or circle
            Gizmos.color = w.IsBlown ? Color.black : (w.IsDriven ? Color.cyan : Color.white);
            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(pos, w.WheelRadius);
            }
            else
            {
                Gizmos.DrawSphere(pos, w.WheelRadius * 0.4f);
            }

            // Draw force vectors
            Vector3 fwd = defaultTo2D ? transform.up : transform.forward;
            Vector3 side = defaultTo2D ? transform.right : transform.right;

            Gizmos.color = Color.green;
            Gizmos.DrawLine(pos, pos + (fwd * (w.LongForceN * 0.0005f)));

            Gizmos.color = Color.red;
            Gizmos.DrawLine(pos, pos + (side * (w.LatForceN * 0.0005f)));
        }
    }
}
