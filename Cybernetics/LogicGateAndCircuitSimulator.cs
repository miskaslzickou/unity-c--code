using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.Utilities;
using GameSystems.Factory;
using GameSystems.Inventory;

namespace GameSystems.Cybernetics
{
    // =========================================================================
    // 1. SIGNAL DEFINITIONS & MULTI-CHANNEL BUS STRUCTURES
    // =========================================================================

    /// <summary>
    /// Virtual and physical signal categories for circuit buses.
    /// </summary>
    public enum CircuitSignalType
    {
        Custom = 0,
        // Virtual Signals (Factorio style)
        SignalA = 1, SignalB = 2, SignalC = 3, SignalD = 4, SignalE = 5,
        SignalF = 6, SignalG = 7, SignalH = 8, SignalI = 9, SignalJ = 10,
        Signal0 = 20, Signal1 = 21, Signal2 = 22, Signal3 = 23, Signal4 = 24,
        Signal5 = 25, Signal6 = 26, Signal7 = 27, Signal8 = 28, Signal9 = 29,
        // Color channels
        Red = 40, Green = 41, Blue = 42, Yellow = 43, White = 44,
        // Utility telemetry signals
        Power = 50, EnergyStored = 51, Water = 52, Heat = 53,
        Blackout = 54, Brownout = 55, Clock = 56, Alarm = 57,
        ItemCount = 60, InventoryCapacity = 61, EntityProximity = 62
    }

    /// <summary>
    /// Wire network color bus (wires can carry identical signal IDs without cross-talk).
    /// </summary>
    public enum CircuitWireColor
    {
        RedWire = 0,
        GreenWire = 1,
        DataBus = 2
    }

    /// <summary>
    /// Zero-GC discriminated union or struct representing a circuit value.
    /// Supports both integer and floating-point continuous analog values.
    /// </summary>
    [Serializable]
    public readonly struct CircuitValue : IEquatable<CircuitValue>
    {
        public readonly int IntValue;
        public readonly float FloatValue;

        public static readonly CircuitValue Zero = new CircuitValue(0, 0f);
        public static readonly CircuitValue One = new CircuitValue(1, 1f);

        public CircuitValue(int intVal)
        {
            IntValue = intVal;
            FloatValue = intVal;
        }

        public CircuitValue(float floatVal)
        {
            FloatValue = floatVal;
            IntValue = Mathf.RoundToInt(floatVal);
        }

        public CircuitValue(int intVal, float floatVal)
        {
            IntValue = intVal;
            FloatValue = floatVal;
        }

        public bool IsHigh => IntValue != 0 || Mathf.Abs(FloatValue) > 0.0001f;
        public bool IsZero => IntValue == 0 && Mathf.Abs(FloatValue) <= 0.0001f;

        public static CircuitValue operator +(in CircuitValue a, in CircuitValue b) =>
            new CircuitValue(a.IntValue + b.IntValue, a.FloatValue + b.FloatValue);

        public static CircuitValue operator -(in CircuitValue a, in CircuitValue b) =>
            new CircuitValue(a.IntValue - b.IntValue, a.FloatValue - b.FloatValue);

        public static CircuitValue operator *(in CircuitValue a, in CircuitValue b) =>
            new CircuitValue(a.IntValue * b.IntValue, a.FloatValue * b.FloatValue);

        public static CircuitValue operator /(in CircuitValue a, in CircuitValue b)
        {
            if (b.IntValue == 0 && Mathf.Abs(b.FloatValue) <= 0.000001f)
                return Zero;
            int iv = b.IntValue != 0 ? a.IntValue / b.IntValue : 0;
            float fv = Mathf.Abs(b.FloatValue) > 0.000001f ? a.FloatValue / b.FloatValue : 0f;
            return new CircuitValue(iv, fv);
        }

        public bool Equals(CircuitValue other) =>
            IntValue == other.IntValue && Mathf.Approximately(FloatValue, other.FloatValue);

        public override bool Equals(object obj) => obj is CircuitValue other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(IntValue, FloatValue);
        public override string ToString() => $"[Int: {IntValue}, Float: {FloatValue:F2}]";
    }

    /// <summary>
    /// Represents an individual signal packet keyed by signal hash or standard enum.
    /// </summary>
    [Serializable]
    public struct CircuitSignalKey : IEquatable<CircuitSignalKey>
    {
        public CircuitSignalType SignalType;
        public int CustomIdHash;

        public CircuitSignalKey(CircuitSignalType type)
        {
            SignalType = type;
            CustomIdHash = (int)type;
        }

        public CircuitSignalKey(string customName)
        {
            SignalType = CircuitSignalType.Custom;
            CustomIdHash = string.IsNullOrEmpty(customName) ? 0 : Animator.StringToHash(customName);
        }

        public bool Equals(CircuitSignalKey other) =>
            SignalType == other.SignalType && CustomIdHash == other.CustomIdHash;

        public override bool Equals(object obj) => obj is CircuitSignalKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)SignalType, CustomIdHash);
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct CircuitSignalChangedEvent : IGameEvent
    {
        public readonly int CircuitNetworkId;
        public readonly CircuitWireColor WireColor;
        public readonly CircuitSignalKey SignalKey;
        public readonly CircuitValue PreviousValue;
        public readonly CircuitValue NewValue;

        public CircuitSignalChangedEvent(int networkId, CircuitWireColor wireColor, in CircuitSignalKey key, in CircuitValue prev, in CircuitValue next)
        {
            CircuitNetworkId = networkId;
            WireColor = wireColor;
            SignalKey = key;
            PreviousValue = prev;
            NewValue = next;
        }
    }

    public readonly struct CircuitRelayStateChangedEvent : IGameEvent
    {
        public readonly int ComponentId;
        public readonly int TargetEntityId;
        public readonly bool IsEnergized;

        public CircuitRelayStateChangedEvent(int componentId, int targetEntityId, bool isEnergized)
        {
            ComponentId = componentId;
            TargetEntityId = targetEntityId;
            IsEnergized = isEnergized;
        }
    }

    public readonly struct CircuitAlarmTriggeredEvent : IGameEvent
    {
        public readonly int ComponentId;
        public readonly Vector3 WorldPosition;
        public readonly CircuitValue TriggerSignalValue;

        public CircuitAlarmTriggeredEvent(int componentId, Vector3 position, in CircuitValue value)
        {
            ComponentId = componentId;
            WorldPosition = position;
            TriggerSignalValue = value;
        }
    }

    // =========================================================================
    // 3. ENUMS FOR COMPONENT LOGIC & OPERATION
    // =========================================================================

    public enum LogicGateType
    {
        None = 0,
        AND,
        OR,
        XOR,
        NAND,
        NOR,
        NOT,
        Buffer
    }

    public enum ArithmeticOperation
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
        Power,
        Min,
        Max
    }

    public enum DeciderCondition
    {
        Greater,
        Less,
        GreaterOrEqual,
        LessOrEqual,
        Equal,
        NotEqual
    }

    public enum DeciderOutputMode
    {
        ConstantOne,
        PassInputCount
    }

    public enum SensorSourceType
    {
        Proximity2DOr3D,
        ResourceNetworkPower,
        ProductionChainStatus,
        UniversalInventoryStock
    }

    public enum CircuitDimensionMode
    {
        DualCompatibility2DAnd3D,
        Pure2D,
        Pure3D
    }

    // =========================================================================
    // 4. COMPONENT STATE & LOGIC DATA STRUCTS
    // =========================================================================

    /// <summary>
    /// Modular circuit component representation (Gate, Combinator, Memory, Sensor, or Relay).
    /// </summary>
    [Serializable]
    public class CircuitComponent
    {
        [Header("Identity & Networks")]
        public int ComponentId;
        public string Label = "Gate";
        public bool IsEnabled = true;
        public Vector3 Position;

        [Header("Wire Connections")]
        public int RedNetworkId = 0;
        public int GreenNetworkId = 0;
        public CircuitWireColor ReadBus = CircuitWireColor.RedWire;
        public CircuitWireColor WriteBus = CircuitWireColor.RedWire;

        [Header("Component Category")]
        public ComponentCategory Category = ComponentCategory.LogicGate;

        // --- Logic Gate ---
        [Header("Logic Gate Settings")]
        public LogicGateType GateType = LogicGateType.AND;
        public CircuitSignalKey InputSignalA = new CircuitSignalKey(CircuitSignalType.SignalA);
        public CircuitSignalKey InputSignalB = new CircuitSignalKey(CircuitSignalType.SignalB);
        public CircuitSignalKey OutputSignal = new CircuitSignalKey(CircuitSignalType.Signal1);

        // --- Arithmetic Combinator ---
        [Header("Arithmetic Combinator")]
        public ArithmeticOperation ArithmeticOp = ArithmeticOperation.Add;
        public bool UseConstantOperandB = false;
        public float ConstantOperandB = 1f;

        // --- Decider Combinator ---
        [Header("Decider Combinator")]
        public DeciderCondition DeciderCond = DeciderCondition.Greater;
        public DeciderOutputMode OutputMode = DeciderOutputMode.ConstantOne;
        public bool CompareAgainstConstant = true;
        public float ConstantThreshold = 0f;

        // --- Latches (SR / D) ---
        [Header("Latch / Memory Cell")]
        public bool IsSRLatch = true; // true = SR latch, false = D latch / flip-flop
        public CircuitSignalKey ResetOrDataSignal = new CircuitSignalKey(CircuitSignalType.SignalB);
        public CircuitValue LatchedValue = CircuitValue.Zero;
        public bool LastClockState = false;

        // --- Delay Line ---
        [Header("Delay Line")]
        [Range(1, 64)] public int DelayTicks = 1;
        [NonSerialized] public CircuitValue[] DelayBuffer = new CircuitValue[64];
        [NonSerialized] public int DelayHead = 0;

        // --- Oscillator / Timer ---
        [Header("Oscillator / Timer")]
        [Min(1)] public int ClockPeriodTicks = 10;
        [Min(1)] public int PulseWidthTicks = 5;
        [NonSerialized] public int ClockTickCounter = 0;

        // --- Sensor Settings ---
        [Header("Sensor Configuration")]
        public SensorSourceType SensorType = SensorSourceType.Proximity2DOr3D;
        public float ProximityRadius = 5f;
        public LayerMask ProximityLayerMask = ~0;
        public string TargetResourceId = "res_iron_ore";

        // References for sensors & actuators
        public ResourceNetworkGraph MonitoredPowerGraph;
        public int MonitoredUtilityNodeId = -1;
        public ProductionChainNode MonitoredFactoryNode;
        public UniversalInventoryContainer MonitoredInventory;

        // --- Actuator / Relay Settings ---
        [Header("Actuator / Relay Target")]
        public bool ActuatorControlsPowerRelay = false;
        public ResourceNetworkGraph TargetPowerGraph;
        public int TargetPowerNodeId = -1;
        public bool ActuatorControlsFactory = false;
        public ProductionChainNode TargetFactory;
        public bool InvertRelayLogic = false;

        public enum ComponentCategory
        {
            LogicGate,
            ArithmeticCombinator,
            DeciderCombinator,
            DelayLine,
            Oscillator,
            MemoryLatch,
            Sensor,
            ActuatorRelay
        }
    }

    // =========================================================================
    // 5. MASTER SIMULATOR ENGINE (CIRCUIT SIMULATOR)
    // =========================================================================

    /// <summary>
    /// High-performance digital and analog circuit network simulator with multi-channel
    /// Red/Green/Data wire buses, Factorio-style combinators, and full 2D/3D physics sensor compatibility.
    /// Interconnects seamlessly with ResourceNetworkGraph, ProductionChainNode, and UniversalInventoryContainer.
    /// </summary>
    [DisallowMultipleComponent]
    public class LogicGateAndCircuitSimulator : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static LogicGateAndCircuitSimulator Instance { get; private set; }

        [Header("Simulation Timing")]
        [SerializeField] private bool autoRegisterTick = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Spatial & Physics Compatibility")]
        [Tooltip("Enables both 2D (Physics2D) and 3D (Physics) spatial detection for circuit sensors.")]
        [SerializeField] private CircuitDimensionMode dimensionMode = CircuitDimensionMode.DualCompatibility2DAnd3D;

        [Header("Circuit Components")]
        [SerializeField] private List<CircuitComponent> components = new List<CircuitComponent>(64);

        // Pre-allocated multi-channel bus storage:
        // Key: NetworkId, Value: Dictionary of SignalKey -> Aggregated CircuitValue
        // To guarantee zero-GC in hot loops, we use pooled dictionary tables per network.
        private readonly Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>> redWireNetworks
            = new Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>>(32);

        private readonly Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>> greenWireNetworks
            = new Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>>(32);

        private readonly Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>> pendingRedBuses
            = new Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>>(32);

        private readonly Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>> pendingGreenBuses
            = new Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>>(32);

        // Pre-allocated non-alloc physics buffers for 2D and 3D
        private readonly Collider2D[] overlapBuffer2D = new Collider2D[32];
        private readonly Collider[] overlapBuffer3D = new Collider[32];

        public string SaveKey => "LogicGateAndCircuitSimulator";

        public int ComponentCount => components.Count;

        // =========================================================================
        // INITIALIZATION & LIFECYCLE
        // =========================================================================

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else if (Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            for (int i = 0; i < components.Count; i++)
            {
                InitializeComponent(components[i]);
            }
        }

        private void OnEnable()
        {
            if (autoRegisterTick && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);
            }
        }

        private void OnDisable()
        {
            if (autoRegisterTick && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
            }
        }

        private void InitializeComponent(CircuitComponent comp)
        {
            if (comp.DelayBuffer == null || comp.DelayBuffer.Length < 64)
            {
                comp.DelayBuffer = new CircuitValue[64];
            }
            comp.DelayHead = 0;
            comp.ClockTickCounter = 0;

            EnsureNetworkAllocated(comp.RedNetworkId);
            EnsureNetworkAllocated(comp.GreenNetworkId);
        }

        private void EnsureNetworkAllocated(int networkId)
        {
            if (networkId <= 0) return;

            if (!redWireNetworks.ContainsKey(networkId))
            {
                redWireNetworks[networkId] = new Dictionary<CircuitSignalKey, CircuitValue>(16);
                pendingRedBuses[networkId] = new Dictionary<CircuitSignalKey, CircuitValue>(16);
            }
            if (!greenWireNetworks.ContainsKey(networkId))
            {
                greenWireNetworks[networkId] = new Dictionary<CircuitSignalKey, CircuitValue>(16);
                pendingGreenBuses[networkId] = new Dictionary<CircuitSignalKey, CircuitValue>(16);
            }
        }

        // =========================================================================
        // PUBLIC API: COMPONENT & WIRE MANAGEMENT
        // =========================================================================

        public void AddComponent(CircuitComponent component)
        {
            if (component == null) return;
            if (component.ComponentId <= 0)
                component.ComponentId = components.Count + 1;

            InitializeComponent(component);
            components.Add(component);
        }

        public bool RemoveComponent(int componentId)
        {
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i].ComponentId == componentId)
                {
                    components.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public CircuitComponent GetComponentById(int componentId)
        {
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i].ComponentId == componentId)
                    return components[i];
            }
            return null;
        }

        public CircuitValue ReadNetworkSignal(int networkId, CircuitWireColor wireColor, in CircuitSignalKey key)
        {
            if (networkId <= 0) return CircuitValue.Zero;

            var table = wireColor == CircuitWireColor.RedWire ? redWireNetworks : greenWireNetworks;
            if (table.TryGetValue(networkId, out var signals))
            {
                if (signals.TryGetValue(key, out var val))
                    return val;
            }
            return CircuitValue.Zero;
        }

        public void InjectExternalSignal(int networkId, CircuitWireColor wireColor, in CircuitSignalKey key, in CircuitValue value)
        {
            if (networkId <= 0) return;
            EnsureNetworkAllocated(networkId);

            var pending = wireColor == CircuitWireColor.RedWire ? pendingRedBuses : pendingGreenBuses;
            if (pending.TryGetValue(networkId, out var dict))
            {
                if (dict.TryGetValue(key, out var existing))
                    dict[key] = existing + value;
                else
                    dict[key] = value;
            }
        }

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EXECUTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            // 1. Clear pending write buffers
            foreach (var kvp in pendingRedBuses)
            {
                kvp.Value.Clear();
            }
            foreach (var kvp in pendingGreenBuses)
            {
                kvp.Value.Clear();
            }

            // 2. Evaluate all circuit components sequentially
            for (int i = 0; i < components.Count; i++)
            {
                CircuitComponent comp = components[i];
                if (!comp.IsEnabled) continue;

                EvaluateComponent(comp, tickIndex, tickDeltaTime);
            }

            // 3. Swap and propagate new signals to wire networks & fire events on changes
            CommitBusUpdates(redWireNetworks, pendingRedBuses, CircuitWireColor.RedWire);
            CommitBusUpdates(greenWireNetworks, pendingGreenBuses, CircuitWireColor.GreenWire);
        }

        private void EvaluateComponent(CircuitComponent comp, int tickIndex, float dt)
        {
            switch (comp.Category)
            {
                case CircuitComponent.ComponentCategory.LogicGate:
                    EvaluateLogicGate(comp);
                    break;
                case CircuitComponent.ComponentCategory.ArithmeticCombinator:
                    EvaluateArithmeticCombinator(comp);
                    break;
                case CircuitComponent.ComponentCategory.DeciderCombinator:
                    EvaluateDeciderCombinator(comp);
                    break;
                case CircuitComponent.ComponentCategory.DelayLine:
                    EvaluateDelayLine(comp);
                    break;
                case CircuitComponent.ComponentCategory.Oscillator:
                    EvaluateOscillator(comp);
                    break;
                case CircuitComponent.ComponentCategory.MemoryLatch:
                    EvaluateMemoryLatch(comp);
                    break;
                case CircuitComponent.ComponentCategory.Sensor:
                    EvaluateSensor(comp);
                    break;
                case CircuitComponent.ComponentCategory.ActuatorRelay:
                    EvaluateActuatorRelay(comp);
                    break;
            }
        }

        // =========================================================================
        // INDIVIDUAL COMPONENT SOLVERS
        // =========================================================================

        private void EvaluateLogicGate(CircuitComponent comp)
        {
            CircuitValue valA = ReadInput(comp, comp.InputSignalA);
            CircuitValue valB = ReadInput(comp, comp.InputSignalB);

            bool a = valA.IsHigh;
            bool b = valB.IsHigh;
            bool result = false;

            switch (comp.GateType)
            {
                case LogicGateType.AND:
                    result = a && b;
                    break;
                case LogicGateType.OR:
                    result = a || b;
                    break;
                case LogicGateType.XOR:
                    result = a ^ b;
                    break;
                case LogicGateType.NAND:
                    result = !(a && b);
                    break;
                case LogicGateType.NOR:
                    result = !(a || b);
                    break;
                case LogicGateType.NOT:
                    result = !a;
                    break;
                case LogicGateType.Buffer:
                    result = a;
                    break;
            }

            CircuitValue outVal = result ? CircuitValue.One : CircuitValue.Zero;
            WriteOutput(comp, comp.OutputSignal, outVal);
        }

        private void EvaluateArithmeticCombinator(CircuitComponent comp)
        {
            CircuitValue valA = ReadInput(comp, comp.InputSignalA);
            float opB = comp.UseConstantOperandB ? comp.ConstantOperandB : ReadInput(comp, comp.InputSignalB).FloatValue;
            float opA = valA.FloatValue;
            float res = 0f;

            switch (comp.ArithmeticOp)
            {
                case ArithmeticOperation.Add:
                    res = opA + opB;
                    break;
                case ArithmeticOperation.Subtract:
                    res = opA - opB;
                    break;
                case ArithmeticOperation.Multiply:
                    res = opA * opB;
                    break;
                case ArithmeticOperation.Divide:
                    res = Mathf.Abs(opB) > 0.000001f ? opA / opB : 0f;
                    break;
                case ArithmeticOperation.Modulo:
                    res = Mathf.Abs(opB) > 0.000001f ? opA % opB : 0f;
                    break;
                case ArithmeticOperation.Power:
                    res = Mathf.Pow(opA, opB);
                    break;
                case ArithmeticOperation.Min:
                    res = Mathf.Min(opA, opB);
                    break;
                case ArithmeticOperation.Max:
                    res = Mathf.Max(opA, opB);
                    break;
            }

            CircuitValue outVal = new CircuitValue(Mathf.RoundToInt(res), res);
            WriteOutput(comp, comp.OutputSignal, outVal);
        }

        private void EvaluateDeciderCombinator(CircuitComponent comp)
        {
            CircuitValue inVal = ReadInput(comp, comp.InputSignalA);
            float threshold = comp.CompareAgainstConstant ? comp.ConstantThreshold : ReadInput(comp, comp.InputSignalB).FloatValue;
            float leftVal = inVal.FloatValue;

            bool satisfied = false;
            switch (comp.DeciderCond)
            {
                case DeciderCondition.Greater:
                    satisfied = leftVal > threshold;
                    break;
                case DeciderCondition.Less:
                    satisfied = leftVal < threshold;
                    break;
                case DeciderCondition.GreaterOrEqual:
                    satisfied = leftVal >= threshold;
                    break;
                case DeciderCondition.LessOrEqual:
                    satisfied = leftVal <= threshold;
                    break;
                case DeciderCondition.Equal:
                    satisfied = Mathf.Approximately(leftVal, threshold);
                    break;
                case DeciderCondition.NotEqual:
                    satisfied = !Mathf.Approximately(leftVal, threshold);
                    break;
            }

            CircuitValue outVal = CircuitValue.Zero;
            if (satisfied)
            {
                outVal = comp.OutputMode == DeciderOutputMode.ConstantOne ? CircuitValue.One : inVal;
            }

            WriteOutput(comp, comp.OutputSignal, outVal);
        }

        private void EvaluateDelayLine(CircuitComponent comp)
        {
            CircuitValue inVal = ReadInput(comp, comp.InputSignalA);
            int bufferLen = comp.DelayBuffer.Length;
            int delay = Mathf.Clamp(comp.DelayTicks, 1, bufferLen);

            comp.DelayBuffer[comp.DelayHead] = inVal;
            int readIndex = (comp.DelayHead - delay + bufferLen) % bufferLen;
            CircuitValue delayedVal = comp.DelayBuffer[readIndex];

            comp.DelayHead = (comp.DelayHead + 1) % bufferLen;
            WriteOutput(comp, comp.OutputSignal, delayedVal);
        }

        private void EvaluateOscillator(CircuitComponent comp)
        {
            comp.ClockTickCounter++;
            if (comp.ClockTickCounter >= comp.ClockPeriodTicks)
            {
                comp.ClockTickCounter = 0;
            }

            bool isHigh = comp.ClockTickCounter < comp.PulseWidthTicks;
            CircuitValue outVal = isHigh ? CircuitValue.One : CircuitValue.Zero;
            WriteOutput(comp, comp.OutputSignal, outVal);
        }

        private void EvaluateMemoryLatch(CircuitComponent comp)
        {
            if (comp.IsSRLatch)
            {
                // Set-Reset Latch
                bool set = ReadInput(comp, comp.InputSignalA).IsHigh;
                bool reset = ReadInput(comp, comp.ResetOrDataSignal).IsHigh;

                if (reset)
                {
                    comp.LatchedValue = CircuitValue.Zero;
                }
                else if (set)
                {
                    comp.LatchedValue = CircuitValue.One;
                }
            }
            else
            {
                // D-Latch / Flip-Flop (triggered on clock rising edge)
                bool clock = ReadInput(comp, comp.InputSignalA).IsHigh;
                CircuitValue data = ReadInput(comp, comp.ResetOrDataSignal);

                if (clock && !comp.LastClockState)
                {
                    comp.LatchedValue = data;
                }
                comp.LastClockState = clock;
            }

            WriteOutput(comp, comp.OutputSignal, comp.LatchedValue);
        }

        private void EvaluateSensor(CircuitComponent comp)
        {
            CircuitValue sensedValue = CircuitValue.Zero;

            switch (comp.SensorType)
            {
                case SensorSourceType.Proximity2DOr3D:
                    sensedValue = MeasureProximitySensor(comp);
                    break;

                case SensorSourceType.ResourceNetworkPower:
                    if (comp.MonitoredPowerGraph != null && comp.MonitoredUtilityNodeId >= 0)
                    {
                        var node = comp.MonitoredPowerGraph.GetNode(comp.MonitoredUtilityNodeId);
                        if (node != null)
                        {
                            // Output satisfaction ratio percentage (0 to 100)
                            float satisfactionPct = node.SatisfactionRatio * 100f;
                            sensedValue = new CircuitValue(Mathf.RoundToInt(satisfactionPct), satisfactionPct);
                        }
                    }
                    break;

                case SensorSourceType.ProductionChainStatus:
                    if (comp.MonitoredFactoryNode != null)
                    {
                        // 1 if active crafting, 0 otherwise
                        bool isCrafting = comp.MonitoredFactoryNode.EffectiveEfficiency > 0.05f;
                        sensedValue = isCrafting ? CircuitValue.One : CircuitValue.Zero;
                    }
                    break;

                case SensorSourceType.UniversalInventoryStock:
                    if (comp.MonitoredInventory != null)
                    {
                        int count = comp.MonitoredInventory.GetAvailableSupply(comp.TargetResourceId);
                        sensedValue = new CircuitValue(count, count);
                    }
                    break;
            }

            WriteOutput(comp, comp.OutputSignal, sensedValue);
        }

        private CircuitValue MeasureProximitySensor(CircuitComponent comp)
        {
            int entityCount = 0;
            Vector3 center = comp.Position;

            // Dual 2D & 3D compatibility
            if (dimensionMode == CircuitDimensionMode.DualCompatibility2DAnd3D || dimensionMode == CircuitDimensionMode.Pure2D)
            {
                Vector2 center2D = new Vector2(center.x, center.y);
                int hits2D = Physics2D.OverlapCircleNonAlloc(center2D, comp.ProximityRadius, overlapBuffer2D, comp.ProximityLayerMask);
                entityCount += hits2D;
            }

            if (dimensionMode == CircuitDimensionMode.DualCompatibility2DAnd3D || dimensionMode == CircuitDimensionMode.Pure3D)
            {
                int hits3D = Physics.OverlapSphereNonAlloc(center, comp.ProximityRadius, overlapBuffer3D, comp.ProximityLayerMask);
                entityCount += hits3D;
            }

            return new CircuitValue(entityCount, entityCount);
        }

        private void EvaluateActuatorRelay(CircuitComponent comp)
        {
            CircuitValue controlSignal = ReadInput(comp, comp.InputSignalA);
            bool activate = controlSignal.IsHigh;
            if (comp.InvertRelayLogic)
                activate = !activate;

            // 1. Actuate ResourceNetworkGraph Power Node
            if (comp.ActuatorControlsPowerRelay && comp.TargetPowerGraph != null && comp.TargetPowerNodeId >= 0)
            {
                var node = comp.TargetPowerGraph.GetNode(comp.TargetPowerNodeId);
                if (node != null && node.IsEnabled != activate)
                {
                    comp.TargetPowerGraph.SetNodeEnabled(comp.TargetPowerNodeId, activate);
                    EventBus<CircuitRelayStateChangedEvent>.Raise(new CircuitRelayStateChangedEvent(
                        comp.ComponentId, comp.TargetPowerNodeId, activate));
                }
            }

            // 2. Actuate ProductionChainNode Factory
            if (comp.ActuatorControlsFactory && comp.TargetFactory != null)
            {
                if (comp.TargetFactory.enabled != activate)
                {
                    comp.TargetFactory.enabled = activate;
                    EventBus<CircuitRelayStateChangedEvent>.Raise(new CircuitRelayStateChangedEvent(
                        comp.ComponentId, comp.TargetFactory.GetInstanceID(), activate));
                }
            }

            // Write output state as confirmation
            CircuitValue stateVal = activate ? CircuitValue.One : CircuitValue.Zero;
            WriteOutput(comp, comp.OutputSignal, stateVal);
        }

        // =========================================================================
        // SIGNAL I/O HELPERS
        // =========================================================================

        private CircuitValue ReadInput(CircuitComponent comp, in CircuitSignalKey key)
        {
            int networkId = comp.ReadBus == CircuitWireColor.RedWire ? comp.RedNetworkId : comp.GreenNetworkId;
            return ReadNetworkSignal(networkId, comp.ReadBus, key);
        }

        private void WriteOutput(CircuitComponent comp, in CircuitSignalKey key, in CircuitValue value)
        {
            int networkId = comp.WriteBus == CircuitWireColor.RedWire ? comp.RedNetworkId : comp.GreenNetworkId;
            if (networkId <= 0) return;

            var pending = comp.WriteBus == CircuitWireColor.RedWire ? pendingRedBuses : pendingGreenBuses;
            if (pending.TryGetValue(networkId, out var dict))
            {
                if (dict.TryGetValue(key, out var existing))
                    dict[key] = existing + value;
                else
                    dict[key] = value;
            }
        }

        private void CommitBusUpdates(
            Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>> currentNetworks,
            Dictionary<int, Dictionary<CircuitSignalKey, CircuitValue>> pendingBuses,
            CircuitWireColor wireColor)
        {
            foreach (var kvp in pendingBuses)
            {
                int netId = kvp.Key;
                var pendingDict = kvp.Value;
                var currentDict = currentNetworks[netId];

                // Detect signal changes and publish events without boxing
                foreach (var sKvp in pendingDict)
                {
                    CircuitSignalKey key = sKvp.Key;
                    CircuitValue newVal = sKvp.Value;
                    currentDict.TryGetValue(key, out CircuitValue prevVal);

                    if (!prevVal.Equals(newVal))
                    {
                        currentDict[key] = newVal;
                        EventBus<CircuitSignalChangedEvent>.Raise(new CircuitSignalChangedEvent(
                            netId, wireColor, key, prevVal, newVal));
                    }
                }

                // Check for signals that ceased in pending (drop to zero)
                foreach (var curKvp in currentDict)
                {
                    CircuitSignalKey key = curKvp.Key;
                    if (!pendingDict.ContainsKey(key) && !curKvp.Value.IsZero)
                    {
                        CircuitValue prevVal = curKvp.Value;
                        currentDict[key] = CircuitValue.Zero;
                        EventBus<CircuitSignalChangedEvent>.Raise(new CircuitSignalChangedEvent(
                            netId, wireColor, key, prevVal, CircuitValue.Zero));
                    }
                }
            }
        }

        // =========================================================================
        // SAVE / LOAD SYSTEM (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class SimulatorSaveData
        {
            public int ComponentCount;
            public List<ComponentSaveRecord> Records = new List<ComponentSaveRecord>();
        }

        [Serializable]
        private class ComponentSaveRecord
        {
            public int ComponentId;
            public bool IsEnabled;
            public int LatchedIntValue;
            public float LatchedFloatValue;
            public int ClockTick;
        }

        public object CaptureState()
        {
            var data = new SimulatorSaveData
            {
                ComponentCount = components.Count
            };

            for (int i = 0; i < components.Count; i++)
            {
                var comp = components[i];
                data.Records.Add(new ComponentSaveRecord
                {
                    ComponentId = comp.ComponentId,
                    IsEnabled = comp.IsEnabled,
                    LatchedIntValue = comp.LatchedValue.IntValue,
                    LatchedFloatValue = comp.LatchedValue.FloatValue,
                    ClockTick = comp.ClockTickCounter
                });
            }

            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<SimulatorSaveData>(stateJson);
            if (data == null || data.Records == null) return;

            for (int r = 0; r < data.Records.Count; r++)
            {
                var record = data.Records[r];
                var comp = GetComponentById(record.ComponentId);
                if (comp != null)
                {
                    comp.IsEnabled = record.IsEnabled;
                    comp.LatchedValue = new CircuitValue(record.LatchedIntValue, record.LatchedFloatValue);
                    comp.ClockTickCounter = record.ClockTick;
                }
            }
        }

        // =========================================================================
        // SCENE GIZMOS & VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            for (int i = 0; i < components.Count; i++)
            {
                var comp = components[i];
                Vector3 pos = comp.Position != Vector3.zero ? comp.Position : transform.position + new Vector3(i * 1.5f, 0, 0);

                Gizmos.color = comp.IsEnabled ? Color.cyan : Color.gray;
                if (dimensionMode == CircuitDimensionMode.Pure2D)
                {
                    Gizmos.DrawWireCube(pos, new Vector3(0.8f, 0.8f, 0.05f));
                }
                else
                {
                    Gizmos.DrawWireCube(pos, Vector3.one * 0.8f);
                }

                // Draw sensor proximity circle/sphere
                if (comp.Category == CircuitComponent.ComponentCategory.Sensor && comp.SensorType == SensorSourceType.Proximity2DOr3D)
                {
                    Gizmos.color = new Color(0f, 1f, 0.4f, 0.35f);
                    Gizmos.DrawWireSphere(pos, comp.ProximityRadius);
                }

                // Draw wire connection line to targets
                if (comp.Category == CircuitComponent.ComponentCategory.ActuatorRelay)
                {
                    Gizmos.color = Color.yellow;
                    if (comp.TargetFactory != null)
                    {
                        Gizmos.DrawLine(pos, comp.TargetFactory.transform.position);
                    }
                    if (comp.TargetPowerGraph != null && comp.TargetPowerNodeId >= 0)
                    {
                        var pNode = comp.TargetPowerGraph.GetNode(comp.TargetPowerNodeId);
                        if (pNode != null)
                            Gizmos.DrawLine(pos, pNode.Position);
                    }
                }
            }
        }
    }
}
