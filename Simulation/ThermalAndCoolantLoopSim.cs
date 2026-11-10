using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.PhysicsAndCombat;

namespace GameSystems.Thermal
{
    // =========================================================================
    // 1. THERMODYNAMIC ENUMS & ZERO-GC STRUCTS
    // =========================================================================

    public enum CoolantFluidPhase : byte
    {
        SubcooledLiquid,
        SaturatedVapor,
        SuperheatedSteam
    }

    public enum ThermalNodeType : byte
    {
        HeatSourceReactor,
        HeatSourceEngine,
        CoolantPipe,
        CoolantPump,
        HeatExchanger,
        RadiatorPanel,
        HeatSinkChassis
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct ReactorMeltdownImminentEvent : IGameEvent
    {
        public readonly int NodeId;
        public readonly float TemperatureKelvin;
        public readonly float CriticalThresholdKelvin;
        public readonly Vector3 WorldPosition;

        public ReactorMeltdownImminentEvent(int nodeId, float temp, float crit, Vector3 pos)
        {
            NodeId = nodeId;
            TemperatureKelvin = temp;
            CriticalThresholdKelvin = crit;
            WorldPosition = pos;
        }
    }

    public readonly struct PipeOverpressureBurstEvent : IGameEvent
    {
        public readonly int PipeNodeId;
        public readonly float PressureBars;
        public readonly Vector3 WorldPosition;

        public PipeOverpressureBurstEvent(int pipeId, float pressure, Vector3 pos)
        {
            PipeNodeId = pipeId;
            PressureBars = pressure;
            WorldPosition = pos;
        }
    }

    public readonly struct CoolantPhaseChangeEvent : IGameEvent
    {
        public readonly int NodeId;
        public readonly CoolantFluidPhase NewPhase;
        public readonly float TemperatureKelvin;

        public CoolantPhaseChangeEvent(int nodeId, CoolantFluidPhase phase, float temp)
        {
            NodeId = nodeId;
            NewPhase = phase;
            TemperatureKelvin = temp;
        }
    }

    // =========================================================================
    // 3. THERMAL NODE DEFINITION
    // =========================================================================

    [Serializable]
    public class ThermalNode
    {
        [Header("Identity & Geometry")]
        public int NodeId;
        public string Label = "Reactor Core";
        public ThermalNodeType NodeType = ThermalNodeType.HeatSourceReactor;
        public Vector3 Position;

        [Header("Thermal Mass & Capacity")]
        [Tooltip("Mass in kg")]
        public float MassKg = 1000f;
        [Tooltip("Specific Heat Capacity in J/(kg*K). (Water: 4184, Iron: 450, Copper: 385)")]
        public float SpecificHeatJ_KgK = 450f;
        [Tooltip("Thermal Conductivity in W/(m*K)")]
        public float ThermalConductivityW_mK = 50f;
        [Tooltip("Surface area in m^2 for radiation and conduction")]
        public float SurfaceAreaM2 = 2.0f;

        [Header("Current Temperature & State")]
        [Tooltip("Current temperature in Kelvin (293.15 K = 20 C)")]
        public float TemperatureKelvin = 320f;
        [Tooltip("Critical structural melting threshold in Kelvin (e.g. 1473 K = 1200 C)")]
        public float MeltdownThresholdKelvin = 1500f;
        public bool IsMeltedDown = false;

        [Header("Heat Generation / Output")]
        [Tooltip("Active heat generation in Watts (Joules/sec)")]
        public float HeatGenerationWatts = 0f;

        [Header("Coolant Fluid Properties")]
        public float FluidMassKg = 50f;
        public float FluidBoilingPointKelvin = 373.15f; // 100 C
        public float FluidLatentHeatJ_Kg = 2260000f;   // Water vaporization heat
        public CoolantFluidPhase CurrentPhase = CoolantFluidPhase.SubcooledLiquid;
        public float PressureBars = 1.0f;
        public float MaxSafePressureBars = 25.0f;
        public bool IsRuptured = false;

        [Header("Coolant Convection & Neighbors")]
        public List<int> ConnectedNeighborNodeIds = new List<int>();
        public int NextCoolantLoopNodeId = -1; // Directional closed-loop flow
        public float PumpFlowRateKg_s = 0f;
        public float RadiatorEmissivity = 0.85f;

        public float TemperatureCelsius => TemperatureKelvin - 273.15f;
    }

    // =========================================================================
    // 4. MASTER THERMAL AND COOLANT LOOP ENGINE
    // =========================================================================

    /// <summary>
    /// Advanced thermodynamics and closed-loop coolant simulation engine.
    /// Simulates conduction, convection, Stefan-Boltzmann radiation, phase change,
    /// overpressure pipe bursts, and nuclear/engine meltdown catastrophes.
    /// Interconnects directly with PIDControllerAndAutopilotSuite to regulate automated cooling.
    /// Compatible with both 2D and 3D spatial thermal layouts.
    /// </summary>
    [DisallowMultipleComponent]
    public class ThermalAndCoolantLoopSim : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static ThermalAndCoolantLoopSim Instance { get; private set; }

        [Header("Environmental Conditions")]
        [SerializeField] private float ambientTemperatureKelvin = 293.15f; // 20 C
        [SerializeField] private bool isInSpaceVacuum = false;
        [SerializeField] private bool is2DLayout = false;

        [Header("Simulation Timing")]
        [SerializeField] private bool autoRegisterTick = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Automated PID Regulation Hook")]
        [SerializeField] private bool enableAutomatedPIDCooling = true;
        [SerializeField] private int monitoredReactorNodeId = 1;
        [SerializeField] private float targetReactorTempKelvin = 573.15f; // 300 C
        [SerializeField] private FloatPIDController coolingValvePID = new FloatPIDController(2.0f, 0.1f, 1.0f, 0f, 100f, 20f);

        [Header("Thermal Nodes")]
        [SerializeField] private List<ThermalNode> nodes = new List<ThermalNode>(32);

        // Pre-allocated heat delta buffer to guarantee zero GC during ticks
        private float[] netHeatBuffer = new float[64];
        private readonly Dictionary<int, ThermalNode> idToNodeMap = new Dictionary<int, ThermalNode>(32);

        // Stefan-Boltzmann constant: 5.670374e-8 W/(m^2 * K^4)
        private const float StefanBoltzmann = 5.670374e-8f;

        public string SaveKey => "ThermalAndCoolantLoopSim";
        public int NodeCount => nodes.Count;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }

            RebuildNodeMap();
        }

        private void OnEnable()
        {
            if (autoRegisterTick && TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);
        }

        private void OnDisable()
        {
            if (autoRegisterTick && TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
        }

        public void RebuildNodeMap()
        {
            idToNodeMap.Clear();
            if (netHeatBuffer.Length < nodes.Count)
                netHeatBuffer = new float[nodes.Count + 16];

            for (int i = 0; i < nodes.Count; i++)
            {
                idToNodeMap[nodes[i].NodeId] = nodes[i];
            }
        }

        public ThermalNode GetNode(int nodeId) =>
            idToNodeMap.TryGetValue(nodeId, out var node) ? node : null;

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EXECUTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            int count = nodes.Count;
            if (netHeatBuffer.Length < count)
                netHeatBuffer = new float[count + 16];

            Array.Clear(netHeatBuffer, 0, count);

            // 1. Automated PID control loop for cooling pumps
            if (enableAutomatedPIDCooling)
            {
                UpdateAutomatedPIDCooling(tickDeltaTime);
            }

            // 2. Compute heat generation, radiation, and conduction
            for (int i = 0; i < count; i++)
            {
                ThermalNode node = nodes[i];
                if (node.IsMeltedDown) continue;

                // Heat Generation (e.g. nuclear core / engine)
                netHeatBuffer[i] += node.HeatGenerationWatts * tickDeltaTime;

                // Radiation to ambient atmosphere or space (Stefan-Boltzmann Law)
                float t4 = node.TemperatureKelvin * node.TemperatureKelvin * node.TemperatureKelvin * node.TemperatureKelvin;
                float effectiveAmbientKelvin = isInSpaceVacuum ? 2.725f : ambientTemperatureKelvin; // Cosmic microwave background in vacuum
                float amb4 = effectiveAmbientKelvin * effectiveAmbientKelvin * effectiveAmbientKelvin * effectiveAmbientKelvin;
                float qRad = node.RadiatorEmissivity * StefanBoltzmann * node.SurfaceAreaM2 * (t4 - amb4) * tickDeltaTime;

                netHeatBuffer[i] -= qRad;

                // Conduction with adjacent structural neighbor nodes
                for (int c = 0; c < node.ConnectedNeighborNodeIds.Count; c++)
                {
                    int neighborId = node.ConnectedNeighborNodeIds[c];
                    if (idToNodeMap.TryGetValue(neighborId, out var neighbor))
                    {
                        if (neighbor.NodeId <= node.NodeId) continue; // calculate pair once

                        float dist = is2DLayout ?
                            Vector2.Distance(node.Position, neighbor.Position) :
                            Vector3.Distance(node.Position, neighbor.Position);
                        dist = Mathf.Max(0.1f, dist);

                        float avgConductivity = (node.ThermalConductivityW_mK + neighbor.ThermalConductivityW_mK) * 0.5f;
                        float avgArea = Mathf.Min(node.SurfaceAreaM2, neighbor.SurfaceAreaM2);
                        float tempDiff = node.TemperatureKelvin - neighbor.TemperatureKelvin;

                        // Fourier's Law of Heat Conduction: Q = k * A * (dT / dx) * dt
                        float qCond = avgConductivity * avgArea * (tempDiff / dist) * tickDeltaTime;

                        int neighborIndex = nodes.IndexOf(neighbor);
                        netHeatBuffer[i] -= qCond;
                        if (neighborIndex >= 0)
                            netHeatBuffer[neighborIndex] += qCond;
                    }
                }

                // Coolant Loop Convection (Directional fluid mass flow)
                if (node.NextCoolantLoopNodeId >= 0 && node.PumpFlowRateKg_s > 0f && !node.IsRuptured)
                {
                    if (idToNodeMap.TryGetValue(node.NextCoolantLoopNodeId, out var nextNode))
                    {
                        float massMoved = Mathf.Min(node.FluidMassKg, node.PumpFlowRateKg_s * tickDeltaTime);
                        float fluidCp = 4184f; // Water specific heat
                        float tempDiff = node.TemperatureKelvin - nextNode.TemperatureKelvin;
                        float qConv = massMoved * fluidCp * tempDiff;

                        int nextIndex = nodes.IndexOf(nextNode);
                        netHeatBuffer[i] -= qConv;
                        if (nextIndex >= 0)
                            netHeatBuffer[nextIndex] += qConv;
                    }
                }
            }

            // 3. Apply net heat delta and evaluate phase changes & structural integrity
            for (int i = 0; i < count; i++)
            {
                ThermalNode node = nodes[i];
                if (node.IsMeltedDown) continue;

                float netHeat = netHeatBuffer[i];
                float totalHeatCapacity = (node.MassKg * node.SpecificHeatJ_KgK) + (node.FluidMassKg * 4184f);
                float tempDelta = netHeat / Mathf.Max(1f, totalHeatCapacity);

                node.TemperatureKelvin += tempDelta;

                // Phase change & overpressure
                EvaluateCoolantPhaseAndPressure(node);

                // Meltdown threshold check
                if (node.TemperatureKelvin >= node.MeltdownThresholdKelvin)
                {
                    node.IsMeltedDown = true;
                    EventBus<ReactorMeltdownImminentEvent>.Raise(new ReactorMeltdownImminentEvent(
                        node.NodeId, node.TemperatureKelvin, node.MeltdownThresholdKelvin, node.Position));
                }
            }
        }

        private void UpdateAutomatedPIDCooling(float dt)
        {
            if (idToNodeMap.TryGetValue(monitoredReactorNodeId, out var reactor))
            {
                // Regulate coolant pump flow rate based on reactor temperature
                float flowRateOutput = coolingValvePID.Update(targetReactorTempKelvin, reactor.TemperatureKelvin, dt);
                flowRateOutput = Mathf.Max(0f, flowRateOutput);

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].NodeType == ThermalNodeType.CoolantPump)
                    {
                        nodes[i].PumpFlowRateKg_s = flowRateOutput;
                    }
                }
            }
        }

        private void EvaluateCoolantPhaseAndPressure(ThermalNode node)
        {
            if (node.FluidMassKg <= 0.01f || node.IsRuptured) return;

            // Phase detection
            CoolantFluidPhase prevPhase = node.CurrentPhase;
            if (node.TemperatureKelvin < node.FluidBoilingPointKelvin)
            {
                node.CurrentPhase = CoolantFluidPhase.SubcooledLiquid;
                node.PressureBars = Mathf.Max(1.0f, node.PressureBars - 0.5f);
            }
            else if (node.TemperatureKelvin < node.FluidBoilingPointKelvin + 50f)
            {
                node.CurrentPhase = CoolantFluidPhase.SaturatedVapor;
                // Steam expansion increases pressure rapidly
                node.PressureBars += 2.0f;
            }
            else
            {
                node.CurrentPhase = CoolantFluidPhase.SuperheatedSteam;
                node.PressureBars += 5.0f;
            }

            if (node.CurrentPhase != prevPhase)
            {
                EventBus<CoolantPhaseChangeEvent>.Raise(new CoolantPhaseChangeEvent(
                    node.NodeId, node.CurrentPhase, node.TemperatureKelvin));
            }

            // Overpressure pipe burst
            if (node.PressureBars >= node.MaxSafePressureBars)
            {
                node.IsRuptured = true;
                node.FluidMassKg = 0f;
                EventBus<PipeOverpressureBurstEvent>.Raise(new PipeOverpressureBurstEvent(
                    node.NodeId, node.PressureBars, node.Position));
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class ThermalSaveData
        {
            public List<NodeThermalRecord> Records = new List<NodeThermalRecord>();
        }

        [Serializable]
        private class NodeThermalRecord
        {
            public int NodeId;
            public float TempK;
            public float Pressure;
            public bool IsMelted;
            public bool IsRuptured;
        }

        public object CaptureState()
        {
            var data = new ThermalSaveData();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                data.Records.Add(new NodeThermalRecord
                {
                    NodeId = n.NodeId,
                    TempK = n.TemperatureKelvin,
                    Pressure = n.PressureBars,
                    IsMelted = n.IsMeltedDown,
                    IsRuptured = n.IsRuptured
                });
            }
            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<ThermalSaveData>(stateJson);
            if (data == null || data.Records == null) return;

            for (int i = 0; i < data.Records.Count; i++)
            {
                var r = data.Records[i];
                if (idToNodeMap.TryGetValue(r.NodeId, out var node))
                {
                    node.TemperatureKelvin = r.TempK;
                    node.PressureBars = r.Pressure;
                    node.IsMeltedDown = r.IsMelted;
                    node.IsRuptured = r.IsRuptured;
                }
            }
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                Vector3 pos = n.Position != Vector3.zero ? n.Position : transform.position + new Vector3(i * 2f, 0, 0);

                // Heatmap Color: Blue (20C) -> Yellow (200C) -> Red (600C) -> White (1200C+)
                float normTemp = Mathf.InverseLerp(293.15f, 1500f, n.TemperatureKelvin);
                Color heatColor = Color.Lerp(Color.cyan, Color.red, normTemp);
                if (normTemp > 0.8f) heatColor = Color.Lerp(Color.red, Color.white, (normTemp - 0.8f) * 5f);
                if (n.IsMeltedDown) heatColor = Color.black;

                Gizmos.color = heatColor;
                if (is2DLayout)
                    Gizmos.DrawWireCube(pos, new Vector3(0.9f, 0.9f, 0.1f));
                else
                    Gizmos.DrawWireCube(pos, Vector3.one * 0.9f);

                // Draw coolant pipes to next loop node
                if (n.NextCoolantLoopNodeId >= 0 && idToNodeMap.TryGetValue(n.NextCoolantLoopNodeId, out var nextNode))
                {
                    Gizmos.color = n.IsRuptured ? Color.gray : Color.blue;
                    Gizmos.DrawLine(pos, nextNode.Position);
                }
            }
        }
    }
}
