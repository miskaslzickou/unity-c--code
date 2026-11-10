using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.PhysicsAndCombat;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Event raised when an aircraft fuel tank begins leaking after damage or structural puncture.
/// </summary>
public readonly struct FuelTankLeakingEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly int TankIndex;
    public readonly string TankName;
    public readonly float LeakRateKgPerSec;

    public FuelTankLeakingEvent(int aircraftId, int tankIndex, string tankName, float leakRate)
    {
        AircraftId = aircraftId;
        TankIndex = tankIndex;
        TankName = tankName;
        LeakRateKgPerSec = leakRate;
    }
}

/// <summary>
/// Event raised when external drop tanks are jettisoned.
/// </summary>
public readonly struct DropTankJettisonedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly int TankIndex;
    public readonly float JettisonedFuelKg;

    public DropTankJettisonedEvent(int aircraftId, int tankIndex, float fuelKg)
    {
        AircraftId = aircraftId;
        TankIndex = tankIndex;
        JettisonedFuelKg = fuelKg;
    }
}

/// <summary>
/// Event raised when total usable fuel is completely exhausted.
/// </summary>
public readonly struct FuelDepletedEvent : IGameEvent
{
    public readonly int AircraftId;

    public FuelDepletedEvent(int aircraftId)
    {
        AircraftId = aircraftId;
    }
}

/// <summary>
/// Individual fuel tank representation with capacity, leak modeling, and spatial position.
/// </summary>
[System.Serializable]
public class AircraftFuelTank
{
    public string TankName;
    public Vector3 LocalOffset;
    public float CapacityKg;
    public float CurrentFuelKg;
    public bool IsSelfSealing;
    public bool IsExternalDropTank;
    public bool IsLeaking;
    public float LeakRateKgPerSec;
    public bool IsJettisoned;

    public AircraftFuelTank(string name, Vector3 offset, float capacityKg, bool selfSealing = true, bool isExternal = false)
    {
        TankName = name;
        LocalOffset = offset;
        CapacityKg = capacityKg;
        CurrentFuelKg = capacityKg;
        IsSelfSealing = selfSealing;
        IsExternalDropTank = isExternal;
        IsLeaking = false;
        LeakRateKgPerSec = 0f;
        IsJettisoned = false;
    }

    public float FillFraction => CapacityKg > 0f ? Mathf.Clamp01(CurrentFuelKg / CapacityKg) : 0f;
}

/// <summary>
/// Multi-tank aircraft fuel network with real-time Center of Gravity (CG) shifting,
/// cross-feed pump balancing, external drop tanks, and ballistic leak modeling.
/// Supports both 3D Rigidbody and 2D Rigidbody2D aircraft.
/// </summary>
[DisallowMultipleComponent]
public class AircraftFuelAndCGShiftManager : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Components")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 160;
    [SerializeField] private Rigidbody rb3D;
    [SerializeField] private Rigidbody2D rb2D;
    [SerializeField] private JetAndPropellerEngineSim[] engines;

    [Header("Airframe Dry Inertia Specifications")]
    [SerializeField] private float emptyDryMassKg = 9500f;
    [SerializeField] private Vector3 dryCenterOfMassLocal = new Vector3(0f, 0.2f, 0.1f);
    [SerializeField] private Vector3 dryMomentOfInertia = new Vector3(12800f, 54000f, 62000f);

    [Header("Fuel Tanks Configuration")]
    [SerializeField] private List<AircraftFuelTank> tanks = new List<AircraftFuelTank>(8);

    [Header("Pumps & Dumping")]
    [SerializeField] private bool autoCrossfeedBalancing = true;
    [SerializeField] private float crossfeedPumpRateKgPerSec = 8.5f;
    [SerializeField] private float dumpValveRateKgPerSec = 45f;
    [SerializeField] private bool isDumpingFuel = false;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Runtime state
    public float TotalMassKg { get; private set; }
    public float TotalFuelKg { get; private set; }
    public float TotalFuelCapacityKg { get; private set; }
    public Vector3 CurrentCenterOfMassLocal { get; private set; }
    public bool IsFuelDepleted { get; private set; }

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "FuelSystem_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public IReadOnlyList<AircraftFuelTank> Tanks => tanks;

    private void Awake()
    {
        if (rb3D == null) rb3D = GetComponent<Rigidbody>();
        if (rb2D == null) rb2D = GetComponent<Rigidbody2D>();

        if (engines == null || engines.Length == 0)
        {
            engines = GetComponentsInChildren<JetAndPropellerEngineSim>();
        }

        if (tanks.Count == 0)
        {
            InitializeDefaultTanks();
        }

        EventBus<ArmorPenetrationEvent>.Register(OnArmorPenetration);

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }
    }

    private void OnDestroy()
    {
        EventBus<ArmorPenetrationEvent>.Unregister(OnArmorPenetration);

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }
    }

    private void InitializeDefaultTanks()
    {
        // Fighter layout: Left Wing, Right Wing, Forward Fuselage, Aft Fuselage, Centerline Drop Tank
        tanks.Add(new AircraftFuelTank("LeftWingTank",        new Vector3(-2.8f, 0f, -0.4f), 1200f, selfSealing: true));
        tanks.Add(new AircraftFuelTank("RightWingTank",       new Vector3( 2.8f, 0f, -0.4f), 1200f, selfSealing: true));
        tanks.Add(new AircraftFuelTank("ForwardFuselageTank", new Vector3( 0.0f, 0.3f, 1.2f), 1600f, selfSealing: true));
        tanks.Add(new AircraftFuelTank("AftFuselageTank",     new Vector3( 0.0f, 0.4f, -1.8f), 1400f, selfSealing: true));
        tanks.Add(new AircraftFuelTank("CenterlineDropTank",  new Vector3( 0.0f, -0.5f, 0.0f), 1100f, selfSealing: false, isExternal: true));
    }

    // =========================================================================
    // FUEL CONTROL APIS
    // =========================================================================

    public void SetFuelDumping(bool active)
    {
        isDumpingFuel = active;
    }

    public void JettisonDropTanks()
    {
        for (int i = 0; i < tanks.Count; i++)
        {
            AircraftFuelTank t = tanks[i];
            if (t.IsExternalDropTank && !t.IsJettisoned)
            {
                t.IsJettisoned = true;
                EventBus<DropTankJettisonedEvent>.Raise(new DropTankJettisonedEvent(gameObject.GetInstanceID(), i, t.CurrentFuelKg));
                t.CurrentFuelKg = 0f;
            }
        }
        RecalculateMassAndCG();
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        // 1. Consume engine fuel demand
        float totalDemandKg = 0f;
        for (int i = 0; i < engines.Length; i++)
        {
            if (engines[i] != null)
            {
                totalDemandKg += engines[i].CurrentFuelFlowKgPerSec * deltaTime;
            }
        }

        if (totalDemandKg > 0f)
        {
            DrawFuel(totalDemandKg);
        }

        // 2. Emergency Fuel Dumping
        if (isDumpingFuel)
        {
            DrawFuel(dumpValveRateKgPerSec * deltaTime);
        }

        // 3. Process Fuel Leaks
        UpdateFuelLeaks(deltaTime);

        // 4. Automated Cross-Feed Balancing
        if (autoCrossfeedBalancing)
        {
            PerformCrossfeedBalancing(deltaTime);
        }

        // 5. Update Center of Gravity and Moments of Inertia
        RecalculateMassAndCG();
    }

    private void DrawFuel(float amountKg)
    {
        float remaining = amountKg;

        // Priority 1: Burn external drop tanks first
        for (int i = 0; i < tanks.Count && remaining > 0f; i++)
        {
            AircraftFuelTank t = tanks[i];
            if (t.IsExternalDropTank && !t.IsJettisoned && t.CurrentFuelKg > 0f)
            {
                float drawn = Mathf.Min(t.CurrentFuelKg, remaining);
                t.CurrentFuelKg -= drawn;
                remaining -= drawn;
            }
        }

        // Priority 2: Burn internal tanks evenly
        if (remaining > 0f)
        {
            int internalCount = 0;
            for (int i = 0; i < tanks.Count; i++)
            {
                if (!tanks[i].IsExternalDropTank && tanks[i].CurrentFuelKg > 0f) internalCount++;
            }

            if (internalCount > 0)
            {
                float perTank = remaining / internalCount;
                for (int i = 0; i < tanks.Count; i++)
                {
                    AircraftFuelTank t = tanks[i];
                    if (!tanks[i].IsExternalDropTank && t.CurrentFuelKg > 0f)
                    {
                        float drawn = Mathf.Min(t.CurrentFuelKg, perTank);
                        t.CurrentFuelKg -= drawn;
                    }
                }
            }
        }
    }

    private void UpdateFuelLeaks(float dt)
    {
        for (int i = 0; i < tanks.Count; i++)
        {
            AircraftFuelTank t = tanks[i];
            if (t.IsLeaking && t.CurrentFuelKg > 0f)
            {
                float leak = t.LeakRateKgPerSec * dt;
                t.CurrentFuelKg = Mathf.Max(0f, t.CurrentFuelKg - leak);

                // Self-sealing tanks gradually reduce small leak rates over time
                if (t.IsSelfSealing && t.LeakRateKgPerSec < 5f)
                {
                    t.LeakRateKgPerSec = Mathf.Max(0f, t.LeakRateKgPerSec - (0.5f * dt));
                    if (t.LeakRateKgPerSec <= 0.05f)
                    {
                        t.IsLeaking = false;
                        t.LeakRateKgPerSec = 0f;
                    }
                }
            }
        }
    }

    private void PerformCrossfeedBalancing(float dt)
    {
        // Balance Left Wing vs Right Wing tanks
        AircraftFuelTank leftWing = null;
        AircraftFuelTank rightWing = null;

        for (int i = 0; i < tanks.Count; i++)
        {
            if (tanks[i].TankName == "LeftWingTank") leftWing = tanks[i];
            else if (tanks[i].TankName == "RightWingTank") rightWing = tanks[i];
        }

        if (leftWing != null && rightWing != null)
        {
            float imbalance = leftWing.CurrentFuelKg - rightWing.CurrentFuelKg;
            if (Mathf.Abs(imbalance) > 20f)
            {
                float transfer = Mathf.Min(Mathf.Abs(imbalance) * 0.5f, crossfeedPumpRateKgPerSec * dt);
                if (imbalance > 0f)
                {
                    leftWing.CurrentFuelKg -= transfer;
                    rightWing.CurrentFuelKg += transfer;
                }
                else
                {
                    rightWing.CurrentFuelKg -= transfer;
                    leftWing.CurrentFuelKg += transfer;
                }
            }
        }
    }

    private void RecalculateMassAndCG()
    {
        float totalFuel = 0f;
        float totalCapacity = 0f;
        Vector3 weightedCoMSum = emptyDryMassKg * dryCenterOfMassLocal;

        for (int i = 0; i < tanks.Count; i++)
        {
            AircraftFuelTank t = tanks[i];
            if (t.IsJettisoned) continue;

            totalFuel += t.CurrentFuelKg;
            totalCapacity += t.CapacityKg;
            weightedCoMSum += (t.CurrentFuelKg * t.LocalOffset);
        }

        TotalFuelKg = totalFuel;
        TotalFuelCapacityKg = totalCapacity;
        TotalMassKg = emptyDryMassKg + TotalFuelKg;

        CurrentCenterOfMassLocal = weightedCoMSum / TotalMassKg;

        // Apply updated mass and CoM to Rigidbody
        if (!defaultTo2D && rb3D != null)
        {
            rb3D.mass = TotalMassKg;
            rb3D.centerOfMass = CurrentCenterOfMassLocal;
        }
        else if (defaultTo2D && rb2D != null)
        {
            rb2D.mass = TotalMassKg;
        }

        // Check fuel depletion
        if (TotalFuelKg <= 0.05f && !IsFuelDepleted)
        {
            IsFuelDepleted = true;
            EventBus<FuelDepletedEvent>.Raise(new FuelDepletedEvent(gameObject.GetInstanceID()));
        }
    }

    // =========================================================================
    // BALLISTIC DAMAGE INTERACTION
    // =========================================================================

    private void OnArmorPenetration(in ArmorPenetrationEvent evt)
    {
        Vector3 localHit = transform.InverseTransformPoint(evt.ImpactPoint);

        // Check which fuel tank was punctured
        for (int i = 0; i < tanks.Count; i++)
        {
            AircraftFuelTank t = tanks[i];
            if (t.IsJettisoned) continue;

            float dist = Vector3.Distance(localHit, t.LocalOffset);
            if (dist < 1.2f) // Penetration into tank volume
            {
                t.IsLeaking = true;
                float newLeak = Mathf.Clamp(evt.CaliberMm * 0.15f, 1.0f, 25.0f);
                t.LeakRateKgPerSec = Mathf.Max(t.LeakRateKgPerSec, newLeak);

                EventBus<FuelTankLeakingEvent>.Raise(new FuelTankLeakingEvent(
                    gameObject.GetInstanceID(),
                    i,
                    t.TankName,
                    t.LeakRateKgPerSec
                ));
            }
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<AircraftFuelTank> SavedTanks = new List<AircraftFuelTank>();
        public bool Dumping;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer
        {
            SavedTanks = tanks,
            Dumping = isDumpingFuel
        };
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null || container.SavedTanks == null) return;

        tanks = container.SavedTanks;
        isDumpingFuel = container.Dumping;
        RecalculateMassAndCG();
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        for (int i = 0; i < tanks.Count; i++)
        {
            AircraftFuelTank t = tanks[i];
            if (t.IsJettisoned) continue;

            Vector3 worldPos = transform.TransformPoint(t.LocalOffset);
            if (defaultTo2D) worldPos.z = 0f;

            Color tankCol = t.IsLeaking ? Color.red : Color.Lerp(Color.yellow, Color.green, t.FillFraction);
            Gizmos.color = tankCol;

            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(worldPos, 0.4f);
            }
            else
            {
                Gizmos.DrawWireCube(worldPos, new Vector3(0.6f, 0.5f, 0.8f));
            }
        }

        // Draw Composite Center of Mass (CoM) marker
        Vector3 comWorld = transform.TransformPoint(CurrentCenterOfMassLocal);
        if (defaultTo2D) comWorld.z = 0f;

        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(comWorld, 0.22f);
    }
}
