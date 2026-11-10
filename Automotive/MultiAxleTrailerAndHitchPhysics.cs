using System;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Inventory;

/// <summary>
/// Type of mechanical coupling hitch connecting tractor to trailer.
/// </summary>
public enum HitchType
{
    FifthWheelKingpin,
    DrawbarBallPintle,
    TowCableWinch
}

/// <summary>
/// Event raised when the trailer enters or recovers from a jackknifing instability state.
/// </summary>
public readonly struct JackknifeStateChangedEvent : IGameEvent
{
    public readonly int TrailerId;
    public readonly int TractorId;
    public readonly bool IsJackknifing;
    public readonly float ArticulationAngleDeg;

    public JackknifeStateChangedEvent(int trailerId, int tractorId, bool isJackknifing, float angle)
    {
        TrailerId = trailerId;
        TractorId = tractorId;
        IsJackknifing = isJackknifing;
        ArticulationAngleDeg = angle;
    }
}

/// <summary>
/// Event raised when a trailer is coupled or decoupled from a tractor vehicle.
/// </summary>
public readonly struct TrailerCoupledEvent : IGameEvent
{
    public readonly int TrailerId;
    public readonly int TractorId;
    public readonly bool IsCoupled;

    public TrailerCoupledEvent(int trailerId, int tractorId, bool coupled)
    {
        TrailerId = trailerId;
        TractorId = tractorId;
        IsCoupled = coupled;
    }
}

/// <summary>
/// Articulated fifth-wheel semi-trailers, drawbar trailers, multi-trailer road trains,
/// dynamic cargo mass shifts, kingpin weight transfer, and jackknifing physics.
/// Supports both 3D Rigidbody and 2D Rigidbody2D vehicles.
/// </summary>
[DisallowMultipleComponent]
public class MultiAxleTrailerAndHitchPhysics : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Vehicles")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private GameObject tractorVehicle;
    [SerializeField] private UniversalInventoryContainer cargoContainer;

    [Header("Hitch Configuration")]
    [SerializeField] private HitchType hitchType = HitchType.FifthWheelKingpin;
    [SerializeField] private Vector3 tractorHitchPointLocal = new Vector3(0f, 0.4f, -1.8f);
    [SerializeField] private Vector3 trailerKingpinPointLocal = new Vector3(0f, 0.4f, 4.2f);
    [SerializeField] private float maxArticulationAngleDeg = 85f;
    [SerializeField] private float hitchSpringStiffness = 250000f; // Joint penalty spring N/m
    [SerializeField] private float hitchDamperCoeff = 18000f;

    [Header("Trailer Dimensions & Axles")]
    [SerializeField] private float emptyTrailerMassKg = 4800f;
    [SerializeField] private Vector3 emptyCenterOfMassLocal = new Vector3(0f, 0.6f, -0.5f);
    [SerializeField, Range(1, 4)] private int trailerAxleCount = 2;
    [SerializeField] private float rearAxleGroupOffsetZ = -3.2f;
    [SerializeField] private float trailerTireCorneringStiffness = 45000f; // N/rad per axle

    [Header("Brakes & Jackknife Thresholds")]
    [SerializeField] private float trailerBrakeProportion = 0.85f; // Trailer service brake ratio
    [SerializeField] private float jackknifeWarningAngleDeg = 18f;
    [SerializeField] private float jackknifeCriticalAngleDeg = 45f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Rigidbodies
    private Rigidbody tractorRb3D;
    private Rigidbody trailerRb3D;
    private Rigidbody2D tractorRb2D;
    private Rigidbody2D trailerRb2D;

    // Runtime state
    public bool IsCoupled { get; private set; } = true;
    public bool IsJackknifing { get; private set; }
    public float ArticulationAngleDeg { get; private set; }
    public float TotalTrailerMassKg { get; private set; }
    public Vector3 CurrentCenterOfMassLocal { get; private set; }
    public float KingpinTongueWeightN { get; private set; }

    public string SaveKey => "TrailerHitch_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;

    private void Awake()
    {
        trailerRb3D = GetComponent<Rigidbody>();
        trailerRb2D = GetComponent<Rigidbody2D>();

        if (cargoContainer == null)
        {
            cargoContainer = GetComponent<UniversalInventoryContainer>();
        }

        if (tractorVehicle != null)
        {
            CoupleToTractor(tractorVehicle);
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
    // COUPLING AND DECOUPLING
    // =========================================================================

    public void CoupleToTractor(GameObject tractor)
    {
        tractorVehicle = tractor;
        if (tractorVehicle != null)
        {
            tractorRb3D = tractorVehicle.GetComponent<Rigidbody>();
            tractorRb2D = tractorVehicle.GetComponent<Rigidbody2D>();
            IsCoupled = true;

            EventBus<TrailerCoupledEvent>.Raise(new TrailerCoupledEvent(
                gameObject.GetInstanceID(),
                tractorVehicle.GetInstanceID(),
                coupled: true
            ));
        }
    }

    public void Decouple()
    {
        if (!IsCoupled) return;
        int oldTractorId = tractorVehicle != null ? tractorVehicle.GetInstanceID() : -1;
        tractorVehicle = null;
        tractorRb3D = null;
        tractorRb2D = null;
        IsCoupled = false;

        EventBus<TrailerCoupledEvent>.Raise(new TrailerCoupledEvent(
            gameObject.GetInstanceID(),
            oldTractorId,
            coupled: false
        ));
    }

    // =========================================================================
    // PHYSICAL SIMULATION UPDATE (FixedUpdate)
    // =========================================================================

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateDynamicCargoMass();

        if (IsCoupled && tractorVehicle != null)
        {
            CalculateArticulationAndJackknifing();
            SimulateHitchConstraint(dt);
            SimulateTrailerAxleLateralFriction(dt);
        }
    }

    private void UpdateDynamicCargoMass()
    {
        float cargoMassKg = 0f;
        if (cargoContainer != null)
        {
            cargoMassKg = cargoContainer.GetTotalWeight();
        }

        TotalTrailerMassKg = emptyTrailerMassKg + cargoMassKg;

        // Dynamic center-of-mass shift
        Vector3 cargoCoMLocal = new Vector3(0f, 1.2f, 0f); // Cargo bed position
        CurrentCenterOfMassLocal = ((emptyTrailerMassKg * emptyCenterOfMassLocal) + (cargoMassKg * cargoCoMLocal)) / TotalTrailerMassKg;

        if (!defaultTo2D && trailerRb3D != null)
        {
            trailerRb3D.mass = TotalTrailerMassKg;
            trailerRb3D.centerOfMass = CurrentCenterOfMassLocal;
        }
        else if (defaultTo2D && trailerRb2D != null)
        {
            trailerRb2D.mass = TotalTrailerMassKg;
        }

        // Calculate Kingpin Tongue Weight (downward load on tractor fifth wheel)
        // Ratio of distance from trailer rear axle to kingpin
        float totalLength = Mathf.Abs(trailerKingpinPointLocal.z - rearAxleGroupOffsetZ);
        float comToAxle = Mathf.Abs(CurrentCenterOfMassLocal.z - rearAxleGroupOffsetZ);
        float tongueWeightFraction = Mathf.Clamp01(comToAxle / Mathf.Max(0.5f, totalLength));

        KingpinTongueWeightN = TotalTrailerMassKg * 9.81f * tongueWeightFraction;
    }

    private void CalculateArticulationAndJackknifing()
    {
        Vector3 tractorFwd = defaultTo2D ? (Vector3)tractorVehicle.transform.up : tractorVehicle.transform.forward;
        Vector3 trailerFwd = defaultTo2D ? (Vector3)transform.up : transform.forward;

        if (defaultTo2D)
        {
            ArticulationAngleDeg = Vector2.SignedAngle(tractorFwd, trailerFwd);
        }
        else
        {
            ArticulationAngleDeg = Vector3.SignedAngle(tractorFwd, trailerFwd, Vector3.up);
        }

        bool wasJackknifing = IsJackknifing;
        float absAngle = Mathf.Abs(ArticulationAngleDeg);

        // Jackknifing occurs when angle exceeds critical threshold and tractor velocity is opposing
        if (absAngle >= jackknifeWarningAngleDeg)
        {
            Vector3 vel = defaultTo2D
                ? (Vector3)(tractorRb2D != null ? tractorRb2D.linearVelocity : Vector2.zero)
                : (tractorRb3D != null ? tractorRb3D.linearVelocity : Vector3.zero);

            float forwardSpeed = Vector3.Dot(vel, tractorFwd);
            // Rapid deceleration or high angle with forward speed
            if (absAngle >= jackknifeCriticalAngleDeg || (forwardSpeed > 5f && absAngle > jackknifeWarningAngleDeg))
            {
                IsJackknifing = true;
            }
        }
        else
        {
            IsJackknifing = false;
        }

        if (IsJackknifing != wasJackknifing)
        {
            EventBus<JackknifeStateChangedEvent>.Raise(new JackknifeStateChangedEvent(
                gameObject.GetInstanceID(),
                tractorVehicle.GetInstanceID(),
                IsJackknifing,
                ArticulationAngleDeg
            ));
        }
    }

    private void SimulateHitchConstraint(float dt)
    {
        Vector3 worldHitchTractor = tractorVehicle.transform.TransformPoint(tractorHitchPointLocal);
        Vector3 worldKingpinTrailer = transform.TransformPoint(trailerKingpinPointLocal);

        if (defaultTo2D)
        {
            worldHitchTractor.z = 0f;
            worldKingpinTrailer.z = 0f;
        }

        // Spring-damper constraint force maintaining hitch connection
        Vector3 deltaPos = worldHitchTractor - worldKingpinTrailer;
        Vector3 velTractor = GetPointVelocity(tractorVehicle, worldHitchTractor);
        Vector3 velTrailer = GetPointVelocity(gameObject, worldKingpinTrailer);
        Vector3 deltaVel = velTractor - velTrailer;

        Vector3 hitchForce = (deltaPos * hitchSpringStiffness) + (deltaVel * hitchDamperCoeff);

        // Apply downward kingpin tongue weight onto tractor
        Vector3 tongueForce = defaultTo2D ? Vector3.zero : (-Vector3.up * KingpinTongueWeightN);

        if (!defaultTo2D)
        {
            if (trailerRb3D != null) trailerRb3D.AddForceAtPosition(hitchForce, worldKingpinTrailer, ForceMode.Force);
            if (tractorRb3D != null) tractorRb3D.AddForceAtPosition(-hitchForce + tongueForce, worldHitchTractor, ForceMode.Force);
        }
        else
        {
            if (trailerRb2D != null)
            {
                Vector2 f2 = new Vector2(hitchForce.x, hitchForce.y);
                trailerRb2D.AddForceAtPosition(f2, new Vector2(worldKingpinTrailer.x, worldKingpinTrailer.y), ForceMode2D.Force);
            }
            if (tractorRb2D != null)
            {
                Vector2 f2 = new Vector2(-hitchForce.x, -hitchForce.y);
                tractorRb2D.AddForceAtPosition(f2, new Vector2(worldHitchTractor.x, worldHitchTractor.y), ForceMode2D.Force);
            }
        }
    }

    private void SimulateTrailerAxleLateralFriction(float dt)
    {
        Vector3 axleCenterLocal = new Vector3(0f, 0f, rearAxleGroupOffsetZ);
        Vector3 axleCenterWorld = transform.TransformPoint(axleCenterLocal);
        Vector3 vel = GetPointVelocity(gameObject, axleCenterWorld);

        Vector3 rightDir = defaultTo2D ? (Vector3)transform.right : transform.right;
        float lateralSpeed = Vector3.Dot(vel, rightDir);

        // Lateral tire restoring force resisting trailer sliding sideways
        float totalCorneringStiffness = trailerTireCorneringStiffness * trailerAxleCount;
        float lateralForceMagnitude = -lateralSpeed * totalCorneringStiffness * 0.1f;

        // Cap by trailer tire normal load
        float trailerTireNormalLoadN = TotalTrailerMassKg * 9.81f - KingpinTongueWeightN;
        float maxLateralForce = trailerTireNormalLoadN * 0.95f; // mu = 0.95
        lateralForceMagnitude = Mathf.Clamp(lateralForceMagnitude, -maxLateralForce, maxLateralForce);

        Vector3 latForce = rightDir * lateralForceMagnitude;

        if (!defaultTo2D && trailerRb3D != null)
        {
            trailerRb3D.AddForceAtPosition(latForce, axleCenterWorld, ForceMode.Force);
        }
        else if (defaultTo2D && trailerRb2D != null)
        {
            Vector2 f2 = new Vector2(latForce.x, latForce.y);
            trailerRb2D.AddForceAtPosition(f2, new Vector2(axleCenterWorld.x, axleCenterWorld.y), ForceMode2D.Force);
        }
    }

    private Vector3 GetPointVelocity(GameObject go, Vector3 worldPoint)
    {
        if (!defaultTo2D)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) return rb.GetPointVelocity(worldPoint);
        }
        else
        {
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                Vector2 v = rb.GetPointVelocity(new Vector2(worldPoint.x, worldPoint.y));
                return new Vector3(v.x, v.y, 0f);
            }
        }
        return Vector3.zero;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public bool Coupled;
        public string TractorName;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            Coupled = IsCoupled,
            TractorName = tractorVehicle != null ? tractorVehicle.name : string.Empty
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        IsCoupled = container.Coupled;
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 kingpinWorld = transform.TransformPoint(trailerKingpinPointLocal);
        Vector3 axlesWorld = transform.TransformPoint(new Vector3(0f, 0f, rearAxleGroupOffsetZ));
        Vector3 comWorld = transform.TransformPoint(CurrentCenterOfMassLocal);

        if (defaultTo2D)
        {
            kingpinWorld.z = 0f;
            axlesWorld.z = 0f;
            comWorld.z = 0f;
        }

        // Draw Trailer Spine
        Gizmos.color = IsJackknifing ? Color.red : Color.cyan;
        Gizmos.DrawLine(kingpinWorld, axlesWorld);

        // Draw Kingpin Joint
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(kingpinWorld, 0.25f);

        // Draw Center of Mass
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(comWorld, 0.2f);

        // Draw Axles
        Gizmos.color = Color.white;
        Vector3 right = defaultTo2D ? (Vector3)transform.right : transform.right;
        Gizmos.DrawLine(axlesWorld - (right * 1.2f), axlesWorld + (right * 1.2f));

        // Draw Connection to Tractor Hitch
        if (tractorVehicle != null)
        {
            Vector3 tractorHitchWorld = tractorVehicle.transform.TransformPoint(tractorHitchPointLocal);
            if (defaultTo2D) tractorHitchWorld.z = 0f;

            Gizmos.color = Color.green;
            Gizmos.DrawLine(kingpinWorld, tractorHitchWorld);
        }
    }
}
