using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.Utilities;
using GameSystems.Logistics;

namespace GameSystems.PhysicsAndCombat
{
    // =========================================================================
    // 1. COMPARTMENT & DAMAGE CONTROL ENUMS
    // =========================================================================

    public enum CompartmentRole
    {
        BridgeCommand,
        ReactorCore,
        EngineRoom,
        WeaponsBay,
        LifeSupport,
        CargoHold,
        CrewQuarters,
        AccessCorridor
    }

    public enum DoorState
    {
        Open,
        Closed,
        LockedSecurity,
        BreachedDestroyed
    }

    public enum ElectricalStatus
    {
        NominalPower,
        ShortCircuitWarning,
        BlackoutOffline
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct CompartmentBreachedEvent : IGameEvent
    {
        public readonly int CompartmentId;
        public readonly CompartmentRole Role;
        public readonly float HullIntegrity;
        public readonly Vector3 BreachPoint;

        public CompartmentBreachedEvent(int compId, CompartmentRole role, float hull, Vector3 point)
        {
            CompartmentId = compId;
            Role = role;
            HullIntegrity = hull;
            BreachPoint = point;
        }
    }

    public readonly struct CompartmentFireIgnitedEvent : IGameEvent
    {
        public readonly int CompartmentId;
        public readonly float FireIntensity;
        public readonly Vector3 Location;

        public CompartmentFireIgnitedEvent(int compId, float intensity, Vector3 loc)
        {
            CompartmentId = compId;
            FireIntensity = intensity;
            Location = loc;
        }
    }

    public readonly struct CompartmentFloodedEvent : IGameEvent
    {
        public readonly int CompartmentId;
        public readonly float FloodPercent;

        public CompartmentFloodedEvent(int compId, float flood)
        {
            CompartmentId = compId;
            FloodPercent = flood;
        }
    }

    public readonly struct BulkheadDoorToggledEvent : IGameEvent
    {
        public readonly int DoorId;
        public readonly DoorState NewState;

        public BulkheadDoorToggledEvent(int doorId, DoorState state)
        {
            DoorId = doorId;
            NewState = state;
        }
    }

    // =========================================================================
    // 3. COMPARTMENT & BULKHEAD DOOR DEFINITIONS
    // =========================================================================

    [Serializable]
    public class BulkheadDoor
    {
        public int DoorId = 1;
        public string DoorName = "Bulkhead A-B";
        public int CompartmentIdA = 1;
        public int CompartmentIdB = 2;
        public DoorState State = DoorState.Closed;
        public Vector3 WorldPosition;
        [Range(0f, 100f)] public float IntegrityPercent = 100f;
    }

    [Serializable]
    public class HullCompartment
    {
        [Header("Identity & Geometry")]
        public int CompartmentId = 1;
        public string CompartmentName = "Reactor Compartment";
        public CompartmentRole Role = CompartmentRole.ReactorCore;
        public Vector3 CenterPosition;
        public Vector3 Size3D = new Vector3(6f, 4f, 6f);
        public Rect Bounds2D = new Rect(-3f, -2f, 6f, 4f);

        [Header("Integrity & Hazards")]
        [Range(0f, 100f)] public float HullIntegrityPercent = 100f;
        [Range(0f, 100f)] public float OxygenPercent = 100f;
        [Range(0f, 100f)] public float WaterFloodPercent = 0f;
        [Range(0f, 100f)] public float FireIntensityPercent = 0f;
        public ElectricalStatus ElectricalState = ElectricalStatus.NominalPower;

        [Header("Drainage & Fire Suppression")]
        public bool HasBilgePump = true;
        public float BilgePumpRatePercent_s = 2.0f;
        public bool HasHalonFireSuppression = false;
        public bool IsSuppressionActive = false;

        [Header("External Links")]
        public int LinkedResourcePowerNodeId = -1;
        public List<int> ConnectedDoorIds = new List<int>();

        public Bounds GetWorldBounds3D() => new Bounds(CenterPosition, Size3D);

        public bool ContainsPoint(Vector3 point, bool is2D)
        {
            if (is2D)
            {
                Rect worldRect = new Rect(CenterPosition.x + Bounds2D.x, CenterPosition.y + Bounds2D.y, Bounds2D.width, Bounds2D.height);
                return worldRect.Contains(new Vector2(point.x, point.y));
            }
            return GetWorldBounds3D().Contains(point);
        }
    }

    // =========================================================================
    // 4. MASTER DAMAGE CONTROL AND COMPARTMENT SIMULATOR
    // =========================================================================

    /// <summary>
    /// Vehicle, warship, and submarine interior compartment damage control simulator.
    /// Simulates modular hull breaches, progressive flooding, decompression, fire propagation,
    /// bulkhead door sealing, and electrical short-circuits.
    /// Interconnects with BallisticsAndPenetrationSolver, ResourceNetworkGraph, and LogisticsJobSystem.
    /// Compatible with both 2D and 3D interior vehicle layouts.
    /// </summary>
    [DisallowMultipleComponent]
    public class DamageControlAndCompartmentSim : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static DamageControlAndCompartmentSim Instance { get; private set; }

        [Header("Environmental Conditions")]
        [Tooltip("If true, exterior is space vacuum (decompression). If false, submerged in water (flooding).")]
        [SerializeField] private bool isInSpaceVacuum = false;
        [SerializeField] private bool is2DLayout = false;

        [Header("Simulation Timing")]
        [SerializeField] private bool autoRegisterTick = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Interconnected Systems")]
        [SerializeField] private ResourceNetworkGraph linkedPowerGrid;

        [Header("Interior Compartments & Bulkheads")]
        [SerializeField] private List<HullCompartment> compartments = new List<HullCompartment>();
        [SerializeField] private List<BulkheadDoor> bulkheadDoors = new List<BulkheadDoor>();

        private readonly Dictionary<int, HullCompartment> idToCompMap = new Dictionary<int, HullCompartment>();
        private readonly Dictionary<int, BulkheadDoor> idToDoorMap = new Dictionary<int, BulkheadDoor>();

        public string SaveKey => "DamageControlAndCompartmentSim";
        public int CompartmentCount => compartments.Count;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }

            RebuildLookupTables();
        }

        private void OnEnable()
        {
            if (autoRegisterTick && TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);

            // Subscribe to ballistic armor penetrations
            EventBus<ArmorPenetrationEvent>.Subscribe(OnArmorPenetrationImpact);
        }

        private void OnDisable()
        {
            if (autoRegisterTick && TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);

            EventBus<ArmorPenetrationEvent>.Unsubscribe(OnArmorPenetrationImpact);
        }

        public void RebuildLookupTables()
        {
            idToCompMap.Clear();
            idToDoorMap.Clear();

            for (int i = 0; i < compartments.Count; i++)
                idToCompMap[compartments[i].CompartmentId] = compartments[i];

            for (int d = 0; d < bulkheadDoors.Count; d++)
                idToDoorMap[bulkheadDoors[d].DoorId] = bulkheadDoors[d];
        }

        // =========================================================================
        // PUBLIC CONTROL API
        // =========================================================================

        public void ToggleDoor(int doorId, bool open)
        {
            if (idToDoorMap.TryGetValue(doorId, out var door))
            {
                if (door.State != DoorState.BreachedDestroyed && door.State != DoorState.LockedSecurity)
                {
                    door.State = open ? DoorState.Open : DoorState.Closed;
                    EventBus<BulkheadDoorToggledEvent>.Raise(new BulkheadDoorToggledEvent(doorId, door.State));
                }
            }
        }

        public void SealAllBulkheadDoors()
        {
            for (int i = 0; i < bulkheadDoors.Count; i++)
            {
                if (bulkheadDoors[i].State == DoorState.Open)
                {
                    bulkheadDoors[i].State = DoorState.Closed;
                    EventBus<BulkheadDoorToggledEvent>.Raise(new BulkheadDoorToggledEvent(bulkheadDoors[i].DoorId, DoorState.Closed));
                }
            }
        }

        public void TriggerFireSuppression(int compartmentId)
        {
            if (idToCompMap.TryGetValue(compartmentId, out var comp) && comp.HasHalonFireSuppression)
            {
                comp.IsSuppressionActive = true;
                comp.OxygenPercent = 0f; // Suffocates fire & crew
                comp.FireIntensityPercent = Mathf.Max(0f, comp.FireIntensityPercent - 60f);
            }
        }

        // =========================================================================
        // BALLISTICS & PENETRATION INTEGRATION HOOK
        // =========================================================================

        private void OnArmorPenetrationImpact(in ArmorPenetrationEvent evt)
        {
            if (evt.Result != ImpactResultType.FullPenetration && evt.Result != ImpactResultType.Overpenetration)
                return;

            Vector3 impactPt = evt.ImpactPoint;
            for (int i = 0; i < compartments.Count; i++)
            {
                var comp = compartments[i];
                if (comp.ContainsPoint(impactPt, is2DLayout))
                {
                    // Cause hull breach
                    float damage = Mathf.Clamp(evt.CaliberMm * 0.4f, 15f, 60f);
                    comp.HullIntegrityPercent = Mathf.Max(0f, comp.HullIntegrityPercent - damage);

                    // Chance of igniting fire from hot penetrator fragments
                    if (comp.FireIntensityPercent <= 0.01f && UnityEngine.Random.value < 0.45f)
                    {
                        comp.FireIntensityPercent = 35f;
                        EventBus<CompartmentFireIgnitedEvent>.Raise(new CompartmentFireIgnitedEvent(
                            comp.CompartmentId, comp.FireIntensityPercent, comp.CenterPosition));
                    }

                    EventBus<CompartmentBreachedEvent>.Raise(new CompartmentBreachedEvent(
                        comp.CompartmentId, comp.Role, comp.HullIntegrityPercent, impactPt));
                    break;
                }
            }
        }

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EXECUTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            int compCount = compartments.Count;

            // 1. Process internal hazard dynamics per compartment
            for (int i = 0; i < compCount; i++)
            {
                var comp = compartments[i];

                // Hull Breach Effects
                if (comp.HullIntegrityPercent < 90f)
                {
                    float breachSeverity = (100f - comp.HullIntegrityPercent) / 100f;

                    if (isInSpaceVacuum)
                    {
                        // Atmospheric decompression (losing oxygen rapidly)
                        comp.OxygenPercent = Mathf.Max(0f, comp.OxygenPercent - (20f * breachSeverity * tickDeltaTime));
                    }
                    else
                    {
                        // Flooding from outside water
                        comp.WaterFloodPercent = Mathf.Min(100f, comp.WaterFloodPercent + (15f * breachSeverity * tickDeltaTime));
                        EventBus<CompartmentFloodedEvent>.Raise(new CompartmentFloodedEvent(comp.CompartmentId, comp.WaterFloodPercent));
                    }
                }

                // Bilge pumps draining water
                if (comp.HasBilgePump && comp.WaterFloodPercent > 0f && comp.ElectricalState != ElectricalStatus.BlackoutOffline)
                {
                    comp.WaterFloodPercent = Mathf.Max(0f, comp.WaterFloodPercent - (comp.BilgePumpRatePercent_s * tickDeltaTime));
                }

                // Fire Dynamics
                if (comp.FireIntensityPercent > 0.01f)
                {
                    if (comp.OxygenPercent > 5f)
                    {
                        // Fire grows and consumes oxygen
                        comp.FireIntensityPercent = Mathf.Min(100f, comp.FireIntensityPercent + (5f * tickDeltaTime));
                        comp.OxygenPercent = Mathf.Max(0f, comp.OxygenPercent - (10f * tickDeltaTime));
                    }
                    else
                    {
                        // Suffocates without oxygen
                        comp.FireIntensityPercent = Mathf.Max(0f, comp.FireIntensityPercent - (15f * tickDeltaTime));
                    }

                    // Water puts out fire
                    if (comp.WaterFloodPercent > 20f)
                    {
                        comp.FireIntensityPercent = Mathf.Max(0f, comp.FireIntensityPercent - (30f * tickDeltaTime));
                    }
                }

                // Electrical short circuits from deep water or heavy fire
                if (comp.WaterFloodPercent > 50f || comp.FireIntensityPercent > 60f)
                {
                    comp.ElectricalState = ElectricalStatus.ShortCircuitWarning;

                    // Knock offline linked power node in ResourceNetworkGraph
                    if (linkedPowerGrid != null && comp.LinkedResourcePowerNodeId >= 0)
                    {
                        linkedPowerGrid.SetNodeEnabled(comp.LinkedResourcePowerNodeId, false);
                    }
                }
            }

            // 2. Propagate hazards across open bulkhead doors
            for (int d = 0; d < bulkheadDoors.Count; d++)
            {
                var door = bulkheadDoors[d];
                if (door.State != DoorState.Open) continue;

                if (idToCompMap.TryGetValue(door.CompartmentIdA, out var compA) &&
                    idToCompMap.TryGetValue(door.CompartmentIdB, out var compB))
                {
                    // Equalize flooding
                    float avgFlood = (compA.WaterFloodPercent + compB.WaterFloodPercent) * 0.5f;
                    compA.WaterFloodPercent = Mathf.MoveTowards(compA.WaterFloodPercent, avgFlood, 5f * tickDeltaTime);
                    compB.WaterFloodPercent = Mathf.MoveTowards(compB.WaterFloodPercent, avgFlood, 5f * tickDeltaTime);

                    // Equalize oxygen
                    float avgO2 = (compA.OxygenPercent + compB.OxygenPercent) * 0.5f;
                    compA.OxygenPercent = Mathf.MoveTowards(compA.OxygenPercent, avgO2, 10f * tickDeltaTime);
                    compB.OxygenPercent = Mathf.MoveTowards(compB.OxygenPercent, avgO2, 10f * tickDeltaTime);

                    // Fire spreads through open doorway
                    if (compA.FireIntensityPercent > 30f && compB.FireIntensityPercent <= 0.01f && compB.OxygenPercent > 10f)
                    {
                        compB.FireIntensityPercent = 20f;
                        EventBus<CompartmentFireIgnitedEvent>.Raise(new CompartmentFireIgnitedEvent(
                            compB.CompartmentId, compB.FireIntensityPercent, compB.CenterPosition));
                    }
                    else if (compB.FireIntensityPercent > 30f && compA.FireIntensityPercent <= 0.01f && compA.OxygenPercent > 10f)
                    {
                        compA.FireIntensityPercent = 20f;
                        EventBus<CompartmentFireIgnitedEvent>.Raise(new CompartmentFireIgnitedEvent(
                            compA.CompartmentId, compA.FireIntensityPercent, compA.CenterPosition));
                    }
                }
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class DamageControlSaveData
        {
            public List<CompSaveRecord> Compartments = new List<CompSaveRecord>();
            public List<DoorSaveRecord> Doors = new List<DoorSaveRecord>();
        }

        [Serializable]
        private class CompSaveRecord
        {
            public int Id;
            public float Hull;
            public float O2;
            public float Water;
            public float Fire;
        }

        [Serializable]
        private class DoorSaveRecord
        {
            public int Id;
            public int State;
        }

        public object CaptureState()
        {
            var data = new DamageControlSaveData();
            for (int i = 0; i < compartments.Count; i++)
            {
                var c = compartments[i];
                data.Compartments.Add(new CompSaveRecord
                {
                    Id = c.CompartmentId,
                    Hull = c.HullIntegrityPercent,
                    O2 = c.OxygenPercent,
                    Water = c.WaterFloodPercent,
                    Fire = c.FireIntensityPercent
                });
            }
            for (int d = 0; d < bulkheadDoors.Count; d++)
            {
                data.Doors.Add(new DoorSaveRecord
                {
                    Id = bulkheadDoors[d].DoorId,
                    State = (int)bulkheadDoors[d].State
                });
            }
            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<DamageControlSaveData>(stateJson);
            if (data == null) return;

            for (int i = 0; i < data.Compartments.Count; i++)
            {
                var r = data.Compartments[i];
                if (idToCompMap.TryGetValue(r.Id, out var c))
                {
                    c.HullIntegrityPercent = r.Hull;
                    c.OxygenPercent = r.O2;
                    c.WaterFloodPercent = r.Water;
                    c.FireIntensityPercent = r.Fire;
                }
            }

            for (int d = 0; d < data.Doors.Count; d++)
            {
                var dr = data.Doors[d];
                if (idToDoorMap.TryGetValue(dr.Id, out var door))
                {
                    door.State = (DoorState)dr.State;
                }
            }
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            for (int i = 0; i < compartments.Count; i++)
            {
                var c = compartments[i];
                // Color based on status: Red = Fire, Blue = Flooding, Yellow = Breached, Green = Intact
                Color compColor = Color.green;
                if (c.FireIntensityPercent > 5f) compColor = Color.red;
                else if (c.WaterFloodPercent > 5f) compColor = Color.blue;
                else if (c.HullIntegrityPercent < 90f) compColor = Color.yellow;

                Gizmos.color = compColor;
                if (is2DLayout)
                {
                    Vector3 center = new Vector3(c.CenterPosition.x + c.Bounds2D.center.x, c.CenterPosition.y + c.Bounds2D.center.y, 0f);
                    Gizmos.DrawWireCube(center, new Vector3(c.Bounds2D.width, c.Bounds2D.height, 0.1f));
                }
                else
                {
                    Gizmos.DrawWireCube(c.CenterPosition, c.Size3D);
                }
            }

            // Draw Doors
            for (int d = 0; d < bulkheadDoors.Count; d++)
            {
                var door = bulkheadDoors[d];
                Gizmos.color = door.State == DoorState.Open ? Color.cyan : (door.State == DoorState.Closed ? Color.white : Color.magenta);
                Gizmos.DrawWireSphere(door.WorldPosition, 0.35f);
            }
        }
    }
}
