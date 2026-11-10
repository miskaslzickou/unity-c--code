using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Logistics;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Operational role of an airport ground network node.
/// </summary>
public enum ATCNodeType
{
    ParkingApronHangar,
    TaxiwayIntersection,
    HoldShortLine,
    RunwayThreshold,
    RunwayRollout,
    AirborneHoldingFix
}

/// <summary>
/// Current flight plan state of an aircraft under ATC control.
/// </summary>
public enum ATCAircraftState
{
    ParkedAtGate,
    TaxiingOut,
    HoldingShort,
    ClearedForTakeoff,
    AirborneHolding,
    ClearedToLand,
    RolloutDeceleration,
    TaxiingIn,
    TurnaroundServicing
}

/// <summary>
/// Type of clearance instruction issued by Air Traffic Control.
/// </summary>
public enum ATCClearanceType
{
    TaxiToRunway,
    HoldShort,
    ClearedTakeoff,
    EnterHoldingPattern,
    ClearedApproach,
    WaveOffGoAround,
    TaxiToApron,
    BeginTurnaround
}

/// <summary>
/// Node in the airport taxiway, runway, and holding pattern graph.
/// </summary>
[System.Serializable]
public class ATCGraphNode
{
    public int NodeId;
    public string Name;
    public Vector3 Position;
    public ATCNodeType Type;
    public List<int> ConnectedNodeIds = new List<int>(4);
    public bool IsOccupied;
    public int OccupyingAircraftId = -1;

    public ATCGraphNode(int id, string name, Vector3 pos, ATCNodeType type)
    {
        NodeId = id;
        Name = name;
        Position = pos;
        Type = type;
        IsOccupied = false;
        OccupyingAircraftId = -1;
    }
}

/// <summary>
/// Active runway strip tracked by ATC for slot sequencing and wake turbulence separation.
/// </summary>
[System.Serializable]
public class ATCRunway
{
    public int RunwayId;
    public string Designator; // e.g. "09L"
    public Vector3 ThresholdPos;
    public Vector3 EndPos;
    public float HeadingDeg;
    public float LengthM;
    public bool IsOccupied;
    public int ActiveAircraftId = -1;
    public float WakeTurbulenceTimer;

    public ATCRunway(int id, string designator, Vector3 threshold, Vector3 end, float heading)
    {
        RunwayId = id;
        Designator = designator;
        ThresholdPos = threshold;
        EndPos = end;
        HeadingDeg = heading;
        LengthM = Vector3.Distance(threshold, end);
        IsOccupied = false;
        WakeTurbulenceTimer = 0f;
    }
}

/// <summary>
/// Flight tracked within the ATC operational sector.
/// </summary>
[System.Serializable]
public class ATCTrackedAircraft
{
    public int AircraftId;
    public string Callsign;
    public GameObject GameObject;
    public ATCAircraftState State;
    public int AssignedRunwayId;
    public int AssignedGateNodeId;
    public float HoldingAltitudeM;
    public float TurnaroundTimer;
    public AircraftFuelAndCGShiftManager FuelManager;

    public ATCTrackedAircraft(int id, string callsign, GameObject go, ATCAircraftState state)
    {
        AircraftId = id;
        Callsign = callsign;
        GameObject = go;
        State = state;
        AssignedRunwayId = -1;
        AssignedGateNodeId = -1;
        HoldingAltitudeM = 1500f;
        TurnaroundTimer = 0f;
        FuelManager = go != null ? go.GetComponent<AircraftFuelAndCGShiftManager>() : null;
    }
}

/// <summary>
/// Event raised when ATC issues a flight clearance command.
/// </summary>
public readonly struct ATCClearanceIssuedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly string Callsign;
    public readonly ATCClearanceType Clearance;
    public readonly string RunwayOrNodeName;

    public ATCClearanceIssuedEvent(int aircraftId, string callsign, ATCClearanceType clearance, string name)
    {
        AircraftId = aircraftId;
        Callsign = callsign;
        Clearance = clearance;
        RunwayOrNodeName = name;
    }
}

/// <summary>
/// Event raised when an emergency go-around or wave-off is issued due to an obstructed runway.
/// </summary>
public readonly struct WaveOffIssuedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly string Callsign;
    public readonly string RunwayDesignator;
    public readonly string Reason;

    public WaveOffIssuedEvent(int aircraftId, string callsign, string runway, string reason)
    {
        AircraftId = aircraftId;
        Callsign = callsign;
        RunwayDesignator = runway;
        Reason = reason;
    }
}

/// <summary>
/// Event raised when an aircraft finishes ground turnaround service (refueled, rearmed, inspected).
/// </summary>
public readonly struct TurnaroundCompletedEvent : IGameEvent
{
    public readonly int AircraftId;
    public readonly string Callsign;
    public readonly int GateNodeId;

    public TurnaroundCompletedEvent(int aircraftId, string callsign, int gateId)
    {
        AircraftId = aircraftId;
        Callsign = callsign;
        GateNodeId = gateId;
    }
}

/// <summary>
/// Airport and military airbase Air Traffic Control (ATC) manager handling taxiway graphs,
/// holding patterns, runway slot sequencing, wave-offs, and ground turnaround logistics.
/// Dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class AirTrafficControlAndRunwayGraph : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Execution Order")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 135;

    [Header("Airfield Separation Standards")]
    [SerializeField] private float wakeTurbulenceSeparationSeconds = 45f;
    [SerializeField] private float finalApproachSeparationDistanceM = 3500f;
    [SerializeField] private float holdingPatternRadiusM = 1800f;
    [SerializeField] private float turnaroundDurationSeconds = 15f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Graph elements
    private readonly List<ATCGraphNode> nodes = new List<ATCGraphNode>(64);
    private readonly Dictionary<int, ATCGraphNode> nodeMap = new Dictionary<int, ATCGraphNode>(64);
    private readonly List<ATCRunway> runways = new List<ATCRunway>(8);

    // Tracked flights
    private readonly List<ATCTrackedAircraft> trackedAircraft = new List<ATCTrackedAircraft>(32);
    private readonly Dictionary<int, ATCTrackedAircraft> aircraftMap = new Dictionary<int, ATCTrackedAircraft>(32);

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "ATC_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public IReadOnlyList<ATCGraphNode> Nodes => nodes;
    public IReadOnlyList<ATCRunway> Runways => runways;

    private void Awake()
    {
        if (nodes.Count == 0 && runways.Count == 0)
        {
            GenerateDefaultAirfieldGraph();
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

    private void GenerateDefaultAirfieldGraph()
    {
        // 1. Create Runway 09
        Vector3 rwyStart = transform.position + new Vector3(-600f, 0f, 0f);
        Vector3 rwyEnd = transform.position + new Vector3(600f, 0f, 0f);
        if (defaultTo2D) { rwyStart.z = 0f; rwyEnd.z = 0f; }

        var runway09 = new ATCRunway(1, "09L", rwyStart, rwyEnd, 90f);
        runways.Add(runway09);

        // 2. Nodes: Apron Gates, Taxiways, Hold-Short, Threshold, Holding Fix
        AddNode(new ATCGraphNode(10, "Apron Gate 1", transform.position + new Vector3(-350f, 0f, 150f), ATCNodeType.ParkingApronHangar));
        AddNode(new ATCGraphNode(11, "Apron Gate 2", transform.position + new Vector3(-200f, 0f, 150f), ATCNodeType.ParkingApronHangar));
        AddNode(new ATCGraphNode(20, "Taxiway Alpha 1", transform.position + new Vector3(-350f, 0f, 80f), ATCNodeType.TaxiwayIntersection));
        AddNode(new ATCGraphNode(21, "Taxiway Alpha 2", transform.position + new Vector3(-200f, 0f, 80f), ATCNodeType.TaxiwayIntersection));
        AddNode(new ATCGraphNode(30, "Hold-Short 09L", transform.position + new Vector3(-580f, 0f, 40f), ATCNodeType.HoldShortLine));
        AddNode(new ATCGraphNode(40, "Threshold 09L", rwyStart, ATCNodeType.RunwayThreshold));
        AddNode(new ATCGraphNode(50, "Holding Fix ALPHA", transform.position + new Vector3(-2500f, 1500f, 0f), ATCNodeType.AirborneHoldingFix));

        // Connect graph edges
        ConnectNodes(10, 20);
        ConnectNodes(11, 21);
        ConnectNodes(20, 21);
        ConnectNodes(20, 30);
        ConnectNodes(30, 40);
    }

    public void AddNode(ATCGraphNode node)
    {
        if (node == null || nodeMap.ContainsKey(node.NodeId)) return;
        nodes.Add(node);
        nodeMap[node.NodeId] = node;
    }

    public void ConnectNodes(int idA, int idB)
    {
        if (nodeMap.TryGetValue(idA, out ATCGraphNode a) && nodeMap.TryGetValue(idB, out ATCGraphNode b))
        {
            if (!a.ConnectedNodeIds.Contains(idB)) a.ConnectedNodeIds.Add(idB);
            if (!b.ConnectedNodeIds.Contains(idA)) b.ConnectedNodeIds.Add(idA);
        }
    }

    // =========================================================================
    // AIRCRAFT REGISTRATION & OPERATIONS
    // =========================================================================

    public void RegisterAircraft(ATCTrackedAircraft craft)
    {
        if (craft == null || aircraftMap.ContainsKey(craft.AircraftId)) return;
        trackedAircraft.Add(craft);
        aircraftMap[craft.AircraftId] = craft;
    }

    public void RequestDepartureClearance(int aircraftId)
    {
        if (!aircraftMap.TryGetValue(aircraftId, out ATCTrackedAircraft craft)) return;

        if (craft.State == ATCAircraftState.ParkedAtGate)
        {
            craft.State = ATCAircraftState.TaxiingOut;
            craft.AssignedRunwayId = 1;
            EventBus<ATCClearanceIssuedEvent>.Raise(new ATCClearanceIssuedEvent(
                craft.AircraftId,
                craft.Callsign,
                ATCClearanceType.TaxiToRunway,
                "Hold-Short 09L"
            ));
        }
    }

    public void RequestLandingClearance(int aircraftId)
    {
        if (!aircraftMap.TryGetValue(aircraftId, out ATCTrackedAircraft craft)) return;

        ATCRunway rwy = runways.Count > 0 ? runways[0] : null;
        if (rwy == null) return;

        // Check if runway is obstructed or within wake-turbulence separation window
        if (rwy.IsOccupied || rwy.WakeTurbulenceTimer > 0f)
        {
            // Direct into holding pattern stack
            craft.State = ATCAircraftState.AirborneHolding;
            craft.AssignedRunwayId = rwy.RunwayId;
            EventBus<ATCClearanceIssuedEvent>.Raise(new ATCClearanceIssuedEvent(
                craft.AircraftId,
                craft.Callsign,
                ATCClearanceType.EnterHoldingPattern,
                "Holding Fix ALPHA"
            ));
        }
        else
        {
            // Cleared straight-in approach
            craft.State = ATCAircraftState.ClearedToLand;
            craft.AssignedRunwayId = rwy.RunwayId;
            EventBus<ATCClearanceIssuedEvent>.Raise(new ATCClearanceIssuedEvent(
                craft.AircraftId,
                craft.Callsign,
                ATCClearanceType.ClearedApproach,
                rwy.Designator
            ));
        }
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        // 1. Update Runways (Wake turbulence separation timer decay)
        for (int i = 0; i < runways.Count; i++)
        {
            ATCRunway rwy = runways[i];
            if (rwy.WakeTurbulenceTimer > 0f)
            {
                rwy.WakeTurbulenceTimer = Mathf.Max(0f, rwy.WakeTurbulenceTimer - deltaTime);
            }
        }

        // 2. Update Tracked Aircraft Operations
        for (int i = 0; i < trackedAircraft.Count; i++)
        {
            ATCTrackedAircraft craft = trackedAircraft[i];
            UpdateAircraftATCLogic(craft, deltaTime);
        }
    }

    private void UpdateAircraftATCLogic(ATCTrackedAircraft craft, float dt)
    {
        if (craft.GameObject == null) return;
        Vector3 craftPos = craft.GameObject.transform.position;

        switch (craft.State)
        {
            case ATCAircraftState.ClearedToLand:
                // Check if runway became obstructed during final approach
                if (craft.AssignedRunwayId >= 0 && craft.AssignedRunwayId <= runways.Count)
                {
                    ATCRunway rwy = runways[craft.AssignedRunwayId - 1];
                    if (rwy.IsOccupied && rwy.ActiveAircraftId != craft.AircraftId)
                    {
                        // Emergency Wave-Off / Go-Around!
                        craft.State = ATCAircraftState.AirborneHolding;
                        EventBus<WaveOffIssuedEvent>.Raise(new WaveOffIssuedEvent(
                            craft.AircraftId,
                            craft.Callsign,
                            rwy.Designator,
                            "Runway obstructed by active traffic"
                        ));
                        return;
                    }

                    // Check touchdown
                    float distToThreshold = Vector3.Distance(craftPos, rwy.ThresholdPos);
                    if (distToThreshold < 60f)
                    {
                        rwy.IsOccupied = true;
                        rwy.ActiveAircraftId = craft.AircraftId;
                        craft.State = ATCAircraftState.RolloutDeceleration;
                    }
                }
                break;

            case ATCAircraftState.RolloutDeceleration:
                // When aircraft slows down to taxi speed, clear runway and assign apron gate
                float speed = GetAircraftSpeed(craft.GameObject);
                if (speed < 12.0f) // Taxi speed reached
                {
                    if (craft.AssignedRunwayId >= 0 && craft.AssignedRunwayId <= runways.Count)
                    {
                        ATCRunway rwy = runways[craft.AssignedRunwayId - 1];
                        rwy.IsOccupied = false;
                        rwy.ActiveAircraftId = -1;
                        rwy.WakeTurbulenceTimer = wakeTurbulenceSeparationSeconds;
                    }

                    craft.State = ATCAircraftState.TaxiingIn;
                    craft.AssignedGateNodeId = 10; // Gate 1
                    EventBus<ATCClearanceIssuedEvent>.Raise(new ATCClearanceIssuedEvent(
                        craft.AircraftId,
                        craft.Callsign,
                        ATCClearanceType.TaxiToApron,
                        "Apron Gate 1"
                    ));
                }
                break;

            case ATCAircraftState.TaxiingIn:
                // Check arrival at gate
                if (nodeMap.TryGetValue(craft.AssignedGateNodeId, out ATCGraphNode gate))
                {
                    if (Vector3.Distance(craftPos, gate.Position) < 15f)
                    {
                        craft.State = ATCAircraftState.TurnaroundServicing;
                        craft.TurnaroundTimer = turnaroundDurationSeconds;
                        gate.IsOccupied = true;
                        gate.OccupyingAircraftId = craft.AircraftId;

                        EventBus<ATCClearanceIssuedEvent>.Raise(new ATCClearanceIssuedEvent(
                            craft.AircraftId,
                            craft.Callsign,
                            ATCClearanceType.BeginTurnaround,
                            gate.Name
                        ));
                    }
                }
                break;

            case ATCAircraftState.TurnaroundServicing:
                // Ground crew refueling and service
                craft.TurnaroundTimer -= dt;

                // Refuel aircraft fuel tanks via AircraftFuelAndCGShiftManager
                if (craft.FuelManager != null)
                {
                    for (int t = 0; t < craft.FuelManager.Tanks.Count; t++)
                    {
                        AircraftFuelTank tank = craft.FuelManager.Tanks[t];
                        if (!tank.IsJettisoned)
                        {
                            tank.CurrentFuelKg = Mathf.MoveTowards(tank.CurrentFuelKg, tank.CapacityKg, 85f * dt);
                        }
                    }
                }

                if (craft.TurnaroundTimer <= 0f)
                {
                    craft.State = ATCAircraftState.ParkedAtGate;
                    EventBus<TurnaroundCompletedEvent>.Raise(new TurnaroundCompletedEvent(
                        craft.AircraftId,
                        craft.Callsign,
                        craft.AssignedGateNodeId
                    ));
                }
                break;

            case ATCAircraftState.AirborneHolding:
                // Check if runway cleared for approach
                if (craft.AssignedRunwayId >= 0 && craft.AssignedRunwayId <= runways.Count)
                {
                    ATCRunway rwy = runways[craft.AssignedRunwayId - 1];
                    if (!rwy.IsOccupied && rwy.WakeTurbulenceTimer <= 0f)
                    {
                        craft.State = ATCAircraftState.ClearedToLand;
                        EventBus<ATCClearanceIssuedEvent>.Raise(new ATCClearanceIssuedEvent(
                            craft.AircraftId,
                            craft.Callsign,
                            ATCClearanceType.ClearedApproach,
                            rwy.Designator
                        ));
                    }
                }
                break;
        }
    }

    private float GetAircraftSpeed(GameObject go)
    {
        var rb3 = go.GetComponent<Rigidbody>();
        if (!defaultTo2D && rb3 != null) return rb3.linearVelocity.magnitude;

        var rb2 = go.GetComponent<Rigidbody2D>();
        if (defaultTo2D && rb2 != null) return rb2.linearVelocity.magnitude;

        return 0f;
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<ATCGraphNode> Nodes = new List<ATCGraphNode>();
        public List<ATCRunway> Runways = new List<ATCRunway>();
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer();
        container.Nodes.AddRange(nodes);
        container.Runways.AddRange(runways);
        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        nodes.Clear();
        nodeMap.Clear();
        runways.Clear();

        for (int i = 0; i < container.Nodes.Count; i++)
        {
            AddNode(container.Nodes[i]);
        }
        runways.AddRange(container.Runways);
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Runways
        for (int i = 0; i < runways.Count; i++)
        {
            ATCRunway rwy = runways[i];
            Gizmos.color = rwy.IsOccupied ? Color.red : Color.green;
            Vector3 start = rwy.ThresholdPos;
            Vector3 end = rwy.EndPos;
            if (defaultTo2D) { start.z = 0f; end.z = 0f; }

            Gizmos.DrawLine(start, end);
            Gizmos.DrawWireCube(start, Vector3.one * 8f);
        }

        // Draw Taxiway Graph Nodes & Edges
        for (int i = 0; i < nodes.Count; i++)
        {
            ATCGraphNode node = nodes[i];
            Vector3 pos = node.Position;
            if (defaultTo2D) pos.z = 0f;

            switch (node.Type)
            {
                case ATCNodeType.ParkingApronHangar: Gizmos.color = Color.cyan; break;
                case ATCNodeType.HoldShortLine: Gizmos.color = Color.yellow; break;
                case ATCNodeType.AirborneHoldingFix: Gizmos.color = Color.magenta; break;
                default: Gizmos.color = Color.white; break;
            }

            Gizmos.DrawSphere(pos, 2.5f);

            for (int c = 0; c < node.ConnectedNodeIds.Count; c++)
            {
                if (nodeMap.TryGetValue(node.ConnectedNodeIds[c], out ATCGraphNode neighbor))
                {
                    Vector3 nPos = neighbor.Position;
                    if (defaultTo2D) nPos.z = 0f;
                    Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
                    Gizmos.DrawLine(pos, nPos);
                }
            }
        }
    }
}
