using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Status of aircraft carrier recovery arrestment.
/// </summary>
public enum ArrestorTrapStatus
{
    None,
    Approaching,
    TrappedWire1,
    TrappedWire2,
    TrappedWire3, // Optimum 3-Wire Trap
    TrappedWire4,
    BolterMissedWires
}

/// <summary>
/// Event raised when an aircraft successfully traps an arrestor wire or bolters on a carrier deck.
/// </summary>
public readonly struct ArrestorWireTrappedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly ArrestorTrapStatus TrapResult;
    public readonly float TrappingSpeedKmh;
    public readonly float StoppingDistanceM;

    public ArrestorWireTrappedEvent(int aircraftId, ArrestorTrapStatus result, float speed, float distance)
    {
        AircraftId = aircraftId;
        TrapResult = result;
        TrappingSpeedKmh = speed;
        StoppingDistanceM = distance;
    }
}

/// <summary>
/// Event raised when the catapult launches an aircraft.
/// </summary>
public readonly struct CatapultLaunchedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly float EndStrokeSpeedKmh;

    public CatapultLaunchedEvent(int aircraftId, float speed)
    {
        AircraftId = aircraftId;
        EndStrokeSpeedKmh = speed;
    }
}

/// <summary>
/// Cross-deck pendant arrestor wire on an aircraft carrier recovery deck.
/// </summary>
[System.Serializable]
public class ArrestorWire
{
    public int WireNumber;
    public Vector3 CenterPointLocal;
    public float WireWidthM = 28f;
    public float HydraulicRunoutMaxM = 92f;
    [HideInInspector] public bool IsEngaged;
    [HideInInspector] public float CurrentRunoutM;
}

/// <summary>
/// Instrument Landing System (ILS) glideslope/localizer beam guidance,
/// IFLOLS "Meatball" optical landing aid, Tailhook arrestor gear, and Catapult launch stroke.
/// Dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class ILSAndCarrierLandingSystem : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Rigidbodies")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private Rigidbody aircraftRb3D;
    [SerializeField] private Rigidbody2D aircraftRb2D;

    [Header("ILS & Runway Geometry")]
    [SerializeField] private Vector3 touchdownThresholdPos = Vector3.zero;
    [SerializeField] private float runwayHeadingDeg = 0f;
    [SerializeField] private float glideSlopeAngleDeg = 3.5f; // Standard carrier glideslope
    [SerializeField] private float angledDeckOffsetDeg = -9.0f; // Carrier angled deck angle

    [Header("Arrestor Gear (Wires 1 to 4)")]
    [SerializeField] private ArrestorWire[] arrestorWires;
    [SerializeField] private float arrestorDecelTargetG = 3.2f; // ~3.2G constant runout deceleration

    [Header("Catapult System (Steam / EMALS)")]
    [SerializeField] private Vector3 catapultStartPosLocal = new Vector3(0f, 0f, 0f);
    [SerializeField] private float catapultStrokeLengthM = 90f;
    [SerializeField] private float catapultEndSpeedM_s = 75f; // ~270 km/h
    [SerializeField] private float catapultStrokeDurationSeconds = 2.4f;

    [Header("Tailhook Configuration")]
    [SerializeField] private bool isHookDeployed = true;
    [SerializeField] private Vector3 tailhookTipOffsetLocal = new Vector3(0f, -0.6f, -3.8f);

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // ILS Output Needles (-1..+1 dots of deviation)
    public float LocalizerDeviationDots { get; private set; }
    public float GlideSlopeDeviationDots { get; private set; }
    public float MeatballElevationNormalized { get; private set; } // -1 (low red wave-off), 0 (centered datum), +1 (high)
    public ArrestorTrapStatus CurrentTrapStatus { get; private set; } = ArrestorTrapStatus.None;

    // Catapult State
    public bool IsCatapultStrokeActive { get; private set; }
    private float catapultTimer = 0f;

    // Trapping runtime state
    private int trappedWireIndex = -1;
    private float trappedRunout = 0f;
    private float initialTrapSpeedM_s = 0f;

    public string SaveKey => "CarrierILS_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public bool IsHookDeployed { get => isHookDeployed; set => isHookDeployed = value; }

    private void Awake()
    {
        if (aircraftRb3D == null) aircraftRb3D = GetComponent<Rigidbody>();
        if (aircraftRb2D == null) aircraftRb2D = GetComponent<Rigidbody2D>();

        if (arrestorWires == null || arrestorWires.Length == 0)
        {
            InitializeDefaultWires();
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

    private void InitializeDefaultWires()
    {
        // 4 cross-deck wires spaced 12 meters apart
        arrestorWires = new ArrestorWire[4];
        for (int i = 0; i < 4; i++)
        {
            arrestorWires[i] = new ArrestorWire
            {
                WireNumber = i + 1,
                CenterPointLocal = touchdownThresholdPos + new Vector3(0f, 0f, i * 12f),
                WireWidthM = 26f,
                HydraulicRunoutMaxM = 95f
            };
        }
    }

    // =========================================================================
    // ILS PRECISION GUIDANCE & MEATBALL CALCULATION
    // =========================================================================

    public void UpdateILSGuidance(Vector3 aircraftPos)
    {
        Vector3 delta = aircraftPos - touchdownThresholdPos;
        if (defaultTo2D) delta.z = 0f;

        // Rotate delta into runway heading coordinates
        float effectiveHeading = runwayHeadingDeg + angledDeckOffsetDeg;
        Quaternion invRunwayRot = Quaternion.Euler(0f, -effectiveHeading, 0f);
        Vector3 localOffset = invRunwayRot * delta;

        // Distance along final approach (negative if inbound before threshold)
        float distToThreshold = -localOffset.z;

        if (distToThreshold > 50f)
        {
            // 1. Localizer Azimuth Deviation (Full scale +/- 2.5 deg)
            float azimuthDeg = Mathf.Atan2(localOffset.x, distToThreshold) * Mathf.Rad2Deg;
            LocalizerDeviationDots = Mathf.Clamp(azimuthDeg / 2.5f, -2.5f, 2.5f);

            // 2. Glideslope Elevation Deviation (Full scale +/- 0.7 deg)
            float actualElevDeg = Mathf.Atan2(localOffset.y, distToThreshold) * Mathf.Rad2Deg;
            float glideErrorDeg = actualElevDeg - glideSlopeAngleDeg;
            GlideSlopeDeviationDots = Mathf.Clamp(glideErrorDeg / 0.7f, -2.5f, 2.5f);

            // 3. IFLOLS "Meatball" indicator
            MeatballElevationNormalized = Mathf.Clamp(glideErrorDeg / 0.5f, -1.0f, 1.0f);
        }
        else
        {
            LocalizerDeviationDots = 0f;
            GlideSlopeDeviationDots = 0f;
            MeatballElevationNormalized = 0f;
        }
    }

    // =========================================================================
    // CATAPULT LAUNCH EXECUTION
    // =========================================================================

    public void TriggerCatapultLaunch()
    {
        if (IsCatapultStrokeActive) return;
        IsCatapultStrokeActive = true;
        catapultTimer = 0f;
    }

    private void UpdateCatapult(float dt)
    {
        if (!IsCatapultStrokeActive) return;

        catapultTimer += dt;
        float progress = Mathf.Clamp01(catapultTimer / catapultStrokeDurationSeconds);

        // Constant acceleration stroke: v = a * t
        float currentSpeed = Mathf.Lerp(0f, catapultEndSpeedM_s, progress);
        Vector3 catFwd = defaultTo2D ? (Vector3)transform.up : transform.forward;

        if (!defaultTo2D && aircraftRb3D != null)
        {
            aircraftRb3D.linearVelocity = catFwd * currentSpeed;
        }
        else if (defaultTo2D && aircraftRb2D != null)
        {
            aircraftRb2D.linearVelocity = new Vector2(catFwd.x, catFwd.y) * currentSpeed;
        }

        if (progress >= 1.0f)
        {
            IsCatapultStrokeActive = false;
            EventBus<CatapultLaunchedEvent>.Raise(new CatapultLaunchedEvent(gameObject.GetInstanceID(), currentSpeed * 3.6f));
        }
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateILSGuidance(transform.position);

        if (IsCatapultStrokeActive)
        {
            UpdateCatapult(dt);
            return;
        }

        if (isHookDeployed)
        {
            SimulateArrestorGearTrapping(dt);
        }
    }

    private void SimulateArrestorGearTrapping(float dt)
    {
        Vector3 hookTipWorld = transform.TransformPoint(tailhookTipOffsetLocal);
        if (defaultTo2D) hookTipWorld.z = 0f;

        // If not yet trapped, check if tailhook intercepts any of the 4 wires
        if (trappedWireIndex < 0)
        {
            for (int i = 0; i < arrestorWires.Length; i++)
            {
                ArrestorWire wire = arrestorWires[i];
                Vector3 wirePos = wire.CenterPointLocal;
                if (defaultTo2D) wirePos.z = 0f;

                float dist = Vector3.Distance(hookTipWorld, wirePos);
                if (dist < 1.8f) // Hook catches wire
                {
                    trappedWireIndex = i;
                    wire.IsEngaged = true;
                    trappedRunout = 0f;

                    float speedKmh = 0f;
                    if (!defaultTo2D && aircraftRb3D != null)
                    {
                        initialTrapSpeedM_s = aircraftRb3D.linearVelocity.magnitude;
                        speedKmh = initialTrapSpeedM_s * 3.6f;
                    }
                    else if (defaultTo2D && aircraftRb2D != null)
                    {
                        initialTrapSpeedM_s = aircraftRb2D.linearVelocity.magnitude;
                        speedKmh = initialTrapSpeedM_s * 3.6f;
                    }

                    switch (i)
                    {
                        case 0: CurrentTrapStatus = ArrestorTrapStatus.TrappedWire1; break;
                        case 1: CurrentTrapStatus = ArrestorTrapStatus.TrappedWire2; break;
                        case 2: CurrentTrapStatus = ArrestorTrapStatus.TrappedWire3; break; // Golden 3-wire
                        case 3: CurrentTrapStatus = ArrestorTrapStatus.TrappedWire4; break;
                    }

                    EventBus<ArrestorWireTrappedEvent>.Raise(new ArrestorWireTrappedEvent(
                        gameObject.GetInstanceID(),
                        CurrentTrapStatus,
                        speedKmh,
                        0f
                    ));
                    break;
                }
            }
        }
        else
        {
            // Decelerating via hydraulic arrestor engine runout
            ArrestorWire wire = arrestorWires[trappedWireIndex];
            Vector3 wireCenter = wire.CenterPointLocal;
            if (defaultTo2D) wireCenter.z = 0f;

            Vector3 pullDir = (wireCenter - hookTipWorld).normalized;
            trappedRunout = Vector3.Distance(wireCenter, hookTipWorld);

            float decelForceMagnitude = 12000f * arrestorDecelTargetG * 9.81f; // Heavy arresting tension
            Vector3 tensionForce = pullDir * decelForceMagnitude;

            float currentSpeed = 0f;
            if (!defaultTo2D && aircraftRb3D != null)
            {
                aircraftRb3D.AddForceAtPosition(tensionForce, hookTipWorld, ForceMode.Force);
                currentSpeed = aircraftRb3D.linearVelocity.magnitude;
            }
            else if (defaultTo2D && aircraftRb2D != null)
            {
                Vector2 f2 = new Vector2(tensionForce.x, tensionForce.y);
                aircraftRb2D.AddForceAtPosition(f2, new Vector2(hookTipWorld.x, hookTipWorld.y), ForceMode2D.Force);
                currentSpeed = aircraftRb2D.linearVelocity.magnitude;
            }

            // Aircraft brought to complete stop
            if (currentSpeed < 0.5f)
            {
                wire.IsEngaged = false;
                trappedWireIndex = -1;
            }
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public bool HookDeployed;
        public ArrestorTrapStatus TrapStatus;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            HookDeployed = isHookDeployed,
            TrapStatus = CurrentTrapStatus
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        isHookDeployed = container.HookDeployed;
        CurrentTrapStatus = container.TrapStatus;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw ILS Glideslope beam from Touchdown threshold
        Gizmos.color = Color.green;
        Vector3 glideslopeDir = Quaternion.Euler(glideSlopeAngleDeg, runwayHeadingDeg + angledDeckOffsetDeg, 0f) * -Vector3.forward;
        Gizmos.DrawRay(touchdownThresholdPos, glideslopeDir * 150f);

        // Draw Arrestor Wires
        if (arrestorWires != null)
        {
            Gizmos.color = Color.yellow;
            Vector3 right = Quaternion.Euler(0f, runwayHeadingDeg + angledDeckOffsetDeg, 0f) * Vector3.right;
            for (int i = 0; i < arrestorWires.Length; i++)
            {
                Vector3 center = arrestorWires[i].CenterPointLocal;
                Gizmos.DrawLine(center - (right * (arrestorWires[i].WireWidthM * 0.5f)),
                                center + (right * (arrestorWires[i].WireWidthM * 0.5f)));
            }
        }

        // Draw Tailhook tip
        Gizmos.color = isHookDeployed ? Color.cyan : Color.gray;
        Vector3 hookWorld = transform.TransformPoint(tailhookTipOffsetLocal);
        Gizmos.DrawWireSphere(hookWorld, 0.2f);
    }
}
