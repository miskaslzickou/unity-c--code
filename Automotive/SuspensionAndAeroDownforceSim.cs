using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Event raised when a vehicle enters or exits a slipstream drafting pocket behind a leading car.
/// </summary>
public readonly struct DraftingStateChangedEvent : IGameEvent
{
    public readonly int VehicleId;
    public readonly int LeaderVehicleId;
    public readonly bool IsDrafting;
    public readonly float DragReductionFactor;

    public DraftingStateChangedEvent(int vehicleId, int leaderId, bool isDrafting, float reduction)
    {
        VehicleId = vehicleId;
        LeaderVehicleId = leaderId;
        IsDrafting = isDrafting;
        DragReductionFactor = reduction;
    }
}

/// <summary>
/// Per-wheel suspension strut data with asymmetric damping and travel limits.
/// </summary>
[System.Serializable]
public class SuspensionStrut
{
    public string StrutName;
    public Vector3 MountPointLocal;
    public float RestLengthM = 0.55f;
    public float SpringStiffnessN_m = 38000f; // 38 kN/m
    public float LowSpeedBumpCoeff = 2800f;
    public float HighSpeedBumpCoeff = 1600f;
    public float LowSpeedReboundCoeff = 4200f;
    public float HighSpeedReboundCoeff = 2600f;
    public float VelocitySplitM_s = 0.15f;

    [HideInInspector] public float CurrentCompressionM;
    [HideInInspector] public float PreviousCompressionM;
    [HideInInspector] public float DamperVelocityM_s;
    [HideInInspector] public float TotalSpringForceN;

    public SuspensionStrut(string name, Vector3 mount)
    {
        StrutName = name;
        MountPointLocal = mount;
        RestLengthM = 0.55f;
        CurrentCompressionM = 0.15f; // Static sag
    }
}

/// <summary>
/// High-fidelity spring-damper suspension kinematics, asymmetric bump/rebound shock absorbers,
/// anti-roll bars (sway bars), aerodynamic downforce, and slipstream drafting.
/// Supports both 3D Rigidbody vehicles and 2D Rigidbody2D top-down vehicles.
/// </summary>
[DisallowMultipleComponent]
public class SuspensionAndAeroDownforceSim : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Rigidbodies")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private PacejkaTireAndDriftPhysics tirePhysics;

    [Header("Suspension Struts (FL, FR, RL, RR)")]
    [SerializeField] private SuspensionStrut[] struts;
    [SerializeField] private LayerMask groundLayerMask = ~0;

    [Header("Anti-Roll Bars (Sway Bars)")]
    [SerializeField] private float frontAntiRollBarStiffnessN_m = 12000f; // 12 kN/m
    [SerializeField] private float rearAntiRollBarStiffnessN_m = 9500f;   // 9.5 kN/m

    [Header("Aerodynamics & Downforce")]
    [SerializeField] private float airDensity = 1.225f; // kg/m^3
    [SerializeField] private float frontalAreaM2 = 2.1f;
    [SerializeField] private float dragCoeffCd = 0.32f;
    [SerializeField] private float frontDownforceCoeffCl = 0.45f;
    [SerializeField] private float rearDownforceCoeffCl = 0.65f;
    [SerializeField] private Vector3 frontWingOffset = new Vector3(0f, 0.2f, 1.8f);
    [SerializeField] private Vector3 rearWingOffset = new Vector3(0f, 0.8f, -1.6f);

    [Header("Slipstream / Drafting")]
    [SerializeField] private bool enableSlipstream = true;
    [SerializeField] private float maxDraftingDistanceM = 28f;
    [SerializeField] private float maxDraftingAngleDeg = 14f;
    [SerializeField, Range(0.1f, 0.5f)] private float maxDragReduction = 0.35f; // 35% drag reduction

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime aerodynamic state
    public float CurrentSpeedM_s { get; private set; }
    public float CurrentDragForceN { get; private set; }
    public float FrontDownforceN { get; private set; }
    public float RearDownforceN { get; private set; }
    public bool IsDrafting { get; private set; }
    public float EffectiveDragCoeff { get; private set; }

    public string SaveKey => "SuspensionAero_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();
        if (tirePhysics == null) tirePhysics = GetComponent<PacejkaTireAndDriftPhysics>();

        InitializeDefaultStruts();

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

    private void InitializeDefaultStruts()
    {
        if (struts != null && struts.Length == 4) return;

        struts = new SuspensionStrut[4]
        {
            new SuspensionStrut("FrontLeft",  new Vector3(-0.8f, 0f,  1.3f)),
            new SuspensionStrut("FrontRight", new Vector3( 0.8f, 0f,  1.3f)),
            new SuspensionStrut("RearLeft",   new Vector3(-0.8f, 0f, -1.3f)),
            new SuspensionStrut("RearRight",  new Vector3( 0.8f, 0f, -1.3f))
        };
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateVehicleSpeed();
        SimulateAerodynamicsAndDrafting();
        SimulateSuspensionStruts(dt);
        SimulateAntiRollBars();
        FeedAeroLoadsToTires();
    }

    private void UpdateVehicleSpeed()
    {
        if (!defaultTo2D && rb3D != null)
        {
            CurrentSpeedM_s = rb3D.linearVelocity.magnitude;
        }
        else if (defaultTo2D && rb2D != null)
        {
            CurrentSpeedM_s = rb2D.linearVelocity.magnitude;
        }
        else
        {
            CurrentSpeedM_s = 0f;
        }
    }

    // =========================================================================
    // 1. AERODYNAMICS & SLIPSTREAM DRAFTING
    // =========================================================================

    private void SimulateAerodynamicsAndDrafting()
    {
        float vSq = CurrentSpeedM_s * CurrentSpeedM_s;
        float dynamicPressure = 0.5f * airDensity * vSq;

        // Check slipstream drafting behind leading vehicle
        float draftFactor = 1.0f;
        int leaderId = -1;

        if (enableSlipstream && CurrentSpeedM_s > 10.0f)
        {
            Vector3 rayOrigin = transform.position + (Vector3.up * 0.5f);
            Vector3 fwd = defaultTo2D ? (Vector3)transform.up : transform.forward;

            if (Physics.Raycast(rayOrigin, fwd, out RaycastHit hit, maxDraftingDistanceM, groundLayerMask))
            {
                var leaderAero = hit.collider.GetComponentInParent<SuspensionAndAeroDownforceSim>();
                if (leaderAero != null && leaderAero != this)
                {
                    leaderId = leaderAero.gameObject.GetInstanceID();
                    float dist = hit.distance;
                    float proximityFactor = 1.0f - Mathf.Clamp01(dist / maxDraftingDistanceM);
                    draftFactor = 1.0f - (proximityFactor * maxDragReduction);
                }
            }
        }

        bool wasDrafting = IsDrafting;
        IsDrafting = draftFactor < 0.98f;
        EffectiveDragCoeff = dragCoeffCd * draftFactor;

        if (IsDrafting != wasDrafting)
        {
            EventBus<DraftingStateChangedEvent>.Raise(new DraftingStateChangedEvent(
                gameObject.GetInstanceID(),
                leaderId,
                IsDrafting,
                1.0f - draftFactor
            ));
        }

        // Calculate Drag and Downforce
        CurrentDragForceN = dynamicPressure * frontalAreaM2 * EffectiveDragCoeff;

        // In drafting dirty air, front downforce suffers slight loss (washout understeer)
        float frontAeroLoss = IsDrafting ? 0.85f : 1.0f;
        FrontDownforceN = dynamicPressure * frontalAreaM2 * frontDownforceCoeffCl * frontAeroLoss;
        RearDownforceN = dynamicPressure * frontalAreaM2 * rearDownforceCoeffCl;

        // Apply Drag and Downforce to Rigidbody
        Vector3 dragDir = defaultTo2D
            ? -(Vector3)(rb2D != null ? rb2D.linearVelocity.normalized : Vector2.zero)
            : -(rb3D != null ? rb3D.linearVelocity.normalized : Vector3.zero);

        Vector3 downDir = defaultTo2D ? Vector3.back : -Vector3.up;

        if (!defaultTo2D && rb3D != null)
        {
            // Drag at center of mass
            rb3D.AddForce(dragDir * CurrentDragForceN, ForceMode.Force);

            // Front wing downforce
            Vector3 fwPos = transform.TransformPoint(frontWingOffset);
            rb3D.AddForceAtPosition(downDir * FrontDownforceN, fwPos, ForceMode.Force);

            // Rear wing downforce
            Vector3 rwPos = transform.TransformPoint(rearWingOffset);
            rb3D.AddForceAtPosition(downDir * RearDownforceN, rwPos, ForceMode.Force);
        }
        else if (defaultTo2D && rb2D != null)
        {
            Vector2 drag2D = new Vector2(dragDir.x, dragDir.y) * CurrentDragForceN;
            rb2D.AddForce(drag2D, ForceMode2D.Force);
        }
    }

    // =========================================================================
    // 2. SUSPENSION STRUTS (SPRING & ASYMMETRIC DAMPING)
    // =========================================================================

    private void SimulateSuspensionStruts(float dt)
    {
        for (int i = 0; i < struts.Length; i++)
        {
            SuspensionStrut s = struts[i];
            Vector3 mountWorld = transform.TransformPoint(s.MountPointLocal);

            if (!defaultTo2D)
            {
                // 3D Raycast to ground surface
                if (Physics.Raycast(mountWorld, -transform.up, out RaycastHit hit, s.RestLengthM * 1.5f, groundLayerMask))
                {
                    float measuredLength = hit.distance;
                    s.CurrentCompressionM = Mathf.Clamp(s.RestLengthM - measuredLength, 0f, s.RestLengthM * 0.8f);
                }
                else
                {
                    s.CurrentCompressionM = 0f; // Dropped / Airborne
                }
            }
            else
            {
                // 2D approximation based on vehicle body roll and pitch
                s.CurrentCompressionM = Mathf.Clamp(0.12f + (s.MountPointLocal.y * 0.02f), 0f, s.RestLengthM);
            }

            s.DamperVelocityM_s = (s.CurrentCompressionM - s.PreviousCompressionM) / dt;
            s.PreviousCompressionM = s.CurrentCompressionM;

            // Spring Force: Fs = k * x
            float springForce = s.SpringStiffnessN_m * s.CurrentCompressionM;

            // Asymmetric Shock Absorber Damping
            float dampingCoeff;
            if (s.DamperVelocityM_s > 0f)
            {
                // Bump (Compression)
                dampingCoeff = (s.DamperVelocityM_s < s.VelocitySplitM_s) ? s.LowSpeedBumpCoeff : s.HighSpeedBumpCoeff;
            }
            else
            {
                // Rebound (Extension)
                dampingCoeff = (Mathf.Abs(s.DamperVelocityM_s) < s.VelocitySplitM_s) ? s.LowSpeedReboundCoeff : s.HighSpeedReboundCoeff;
            }

            float damperForce = dampingCoeff * s.DamperVelocityM_s;
            s.TotalSpringForceN = Mathf.Max(0f, springForce + damperForce);

            // Apply suspension push force to 3D Rigidbody
            if (!defaultTo2D && rb3D != null && s.CurrentCompressionM > 0.001f)
            {
                rb3D.AddForceAtPosition(transform.up * s.TotalSpringForceN, mountWorld, ForceMode.Force);
            }
        }
    }

    // =========================================================================
    // 3. ANTI-ROLL BARS (SWAY BARS)
    // =========================================================================

    private void SimulateAntiRollBars()
    {
        if (struts.Length < 4) return;

        // Front Axle (Struts 0: FL, 1: FR)
        float deltaCompressionFront = struts[0].CurrentCompressionM - struts[1].CurrentCompressionM;
        float arbForceFront = deltaCompressionFront * frontAntiRollBarStiffnessN_m;

        struts[0].TotalSpringForceN += arbForceFront;
        struts[1].TotalSpringForceN -= arbForceFront;

        // Rear Axle (Struts 2: RL, 3: RR)
        float deltaCompressionRear = struts[2].CurrentCompressionM - struts[3].CurrentCompressionM;
        float arbForceRear = deltaCompressionRear * rearAntiRollBarStiffnessN_m;

        struts[2].TotalSpringForceN += arbForceRear;
        struts[3].TotalSpringForceN -= arbForceRear;

        // Apply anti-roll torque in 3D
        if (!defaultTo2D && rb3D != null)
        {
            Vector3 mountFL = transform.TransformPoint(struts[0].MountPointLocal);
            Vector3 mountFR = transform.TransformPoint(struts[1].MountPointLocal);
            rb3D.AddForceAtPosition(transform.up * arbForceFront, mountFL, ForceMode.Force);
            rb3D.AddForceAtPosition(-transform.up * arbForceFront, mountFR, ForceMode.Force);
        }
    }

    // =========================================================================
    // 4. FEED AERO DOWNFORCE INTO TIRE NORMAL LOADS
    // =========================================================================

    private void FeedAeroLoadsToTires()
    {
        if (tirePhysics == null || tirePhysics.Wheels == null || tirePhysics.Wheels.Length < 4)
            return;

        // Distribute aerodynamic downforce directly onto tire contact patches
        float halfFrontAero = FrontDownforceN * 0.5f;
        float halfRearAero = RearDownforceN * 0.5f;

        tirePhysics.Wheels[0].NormalLoadN += halfFrontAero; // FL
        tirePhysics.Wheels[1].NormalLoadN += halfFrontAero; // FR
        tirePhysics.Wheels[2].NormalLoadN += halfRearAero;  // RL
        tirePhysics.Wheels[3].NormalLoadN += halfRearAero;  // RR
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public float FrontDownforceCoeff;
        public float RearDownforceCoeff;
        public float DragCoeff;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            FrontDownforceCoeff = frontDownforceCoeffCl,
            RearDownforceCoeff = rearDownforceCoeffCl,
            DragCoeff = dragCoeffCd
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        frontDownforceCoeffCl = container.FrontDownforceCoeff;
        rearDownforceCoeffCl = container.RearDownforceCoeff;
        dragCoeffCd = container.DragCoeff;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Suspension Struts
        if (struts != null)
        {
            Gizmos.color = Color.green;
            for (int i = 0; i < struts.Length; i++)
            {
                Vector3 mount = transform.TransformPoint(struts[i].MountPointLocal);
                Vector3 end = mount - (transform.up * (struts[i].RestLengthM - struts[i].CurrentCompressionM));
                Gizmos.DrawLine(mount, end);
                Gizmos.DrawWireSphere(end, 0.08f);
            }
        }

        // Draw Downforce Vectors
        Gizmos.color = Color.blue;
        Vector3 fw = transform.TransformPoint(frontWingOffset);
        Gizmos.DrawLine(fw, fw - (Vector3.up * (FrontDownforceN * 0.0004f)));

        Vector3 rw = transform.TransformPoint(rearWingOffset);
        Gizmos.DrawLine(rw, rw - (Vector3.up * (RearDownforceN * 0.0004f)));

        // Draw Slipstream Cone
        if (enableSlipstream)
        {
            Gizmos.color = IsDrafting ? Color.yellow : new Color(1f, 1f, 1f, 0.2f);
            Vector3 fwd = defaultTo2D ? (Vector3)transform.up : transform.forward;
            Gizmos.DrawRay(transform.position, fwd * maxDraftingDistanceM);
        }
    }
}
