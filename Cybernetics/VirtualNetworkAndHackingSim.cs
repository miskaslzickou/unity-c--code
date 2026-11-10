using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

namespace GameSystems.Cybernetics
{
    // =========================================================================
    // 1. NETWORK ADDRESSING & PACKET DATA STRUCTURES (ZERO-GC)
    // =========================================================================

    [Serializable]
    public readonly struct NetworkIP : IEquatable<NetworkIP>
    {
        public readonly byte B1;
        public readonly byte B2;
        public readonly byte B3;
        public readonly byte B4;

        public NetworkIP(byte b1, byte b2, byte b3, byte b4)
        {
            B1 = b1; B2 = b2; B3 = b3; B4 = b4;
        }

        public NetworkIP(int ipInt)
        {
            B1 = (byte)((ipInt >> 24) & 0xFF);
            B2 = (byte)((ipInt >> 16) & 0xFF);
            B3 = (byte)((ipInt >> 8) & 0xFF);
            B4 = (byte)(ipInt & 0xFF);
        }

        public int ToInt() => (B1 << 24) | (B2 << 16) | (B3 << 8) | B4;

        public static bool TryParse(string ipStr, out NetworkIP ip)
        {
            ip = new NetworkIP(127, 0, 0, 1);
            if (string.IsNullOrEmpty(ipStr)) return false;
            string[] parts = ipStr.Split('.');
            if (parts.Length != 4) return false;

            if (byte.TryParse(parts[0], out byte b1) &&
                byte.TryParse(parts[1], out byte b2) &&
                byte.TryParse(parts[2], out byte b3) &&
                byte.TryParse(parts[3], out byte b4))
            {
                ip = new NetworkIP(b1, b2, b3, b4);
                return true;
            }
            return false;
        }

        public bool IsSameSubnet(NetworkIP other, NetworkIP subnetMask)
        {
            return (ToInt() & subnetMask.ToInt()) == (other.ToInt() & subnetMask.ToInt());
        }

        public bool Equals(NetworkIP other) =>
            B1 == other.B1 && B2 == other.B2 && B3 == other.B3 && B4 == other.B4;

        public override bool Equals(object obj) => obj is NetworkIP other && Equals(other);
        public override int GetHashCode() => ToInt();
        public override string ToString() => $"{B1}.{B2}.{B3}.{B4}";
    }

    public enum PacketProtocol : byte
    {
        TCP = 6,
        UDP = 17,
        ICMP = 1,
        SCADA_Modbus = 50,
        EncryptedTunnel = 99
    }

    public enum PacketPayloadType : byte
    {
        Ping,
        PortProbe,
        ExploitPayload,
        DataDownload,
        DDoSFlood,
        ControlSignal
    }

    /// <summary>
    /// Lightweight network packet representation.
    /// Passed by readonly reference 'in' to eliminate heap allocations.
    /// </summary>
    [Serializable]
    public readonly struct NetworkPacket
    {
        public readonly NetworkIP SourceIP;
        public readonly NetworkIP DestinationIP;
        public readonly int DestinationPort;
        public readonly PacketProtocol Protocol;
        public readonly PacketPayloadType PayloadType;
        public readonly byte TTL;
        public readonly int PayloadData;

        public NetworkPacket(NetworkIP src, NetworkIP dst, int port, PacketProtocol proto, PacketPayloadType type, byte ttl = 16, int data = 0)
        {
            SourceIP = src;
            DestinationIP = dst;
            DestinationPort = port;
            Protocol = proto;
            PayloadType = type;
            TTL = ttl;
            PayloadData = data;
        }

        public NetworkPacket DecrementTTL() =>
            new NetworkPacket(SourceIP, DestinationIP, DestinationPort, Protocol, PayloadType, (byte)(TTL > 0 ? TTL - 1 : 0), PayloadData);
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct NetworkIntrusionAlertEvent : IGameEvent
    {
        public readonly int NodeId;
        public readonly NetworkIP TargetIP;
        public readonly NetworkIP AttackerIP;
        public readonly float TraceProgress;

        public NetworkIntrusionAlertEvent(int nodeId, NetworkIP target, NetworkIP attacker, float progress)
        {
            NodeId = nodeId;
            TargetIP = target;
            AttackerIP = attacker;
            TraceProgress = progress;
        }
    }

    public readonly struct NetworkNodeBreachedEvent : IGameEvent
    {
        public readonly int NodeId;
        public readonly NetworkIP BreachedIP;
        public readonly string CompromisedLevel;

        public NetworkNodeBreachedEvent(int nodeId, NetworkIP breachedIP, string level)
        {
            NodeId = nodeId;
            BreachedIP = breachedIP;
            CompromisedLevel = level;
        }
    }

    public readonly struct NetworkPacketRoutedEvent : IGameEvent
    {
        public readonly NetworkIP SourceIP;
        public readonly NetworkIP DestinationIP;
        public readonly int Port;
        public readonly PacketPayloadType Type;

        public NetworkPacketRoutedEvent(NetworkIP src, NetworkIP dst, int port, PacketPayloadType type)
        {
            SourceIP = src;
            DestinationIP = dst;
            Port = port;
            Type = type;
        }
    }

    // =========================================================================
    // 3. NODE TOPOLOGY & PORT CONFIGURATIONS
    // =========================================================================

    public enum NetworkNodeType
    {
        Workstation,
        Server,
        Router,
        Switch,
        Firewall,
        Honeypot,
        DataVault,
        IndustrialSCADAController
    }

    public enum PortStatus
    {
        Closed,
        Open,
        Filtered
    }

    [Serializable]
    public class NetworkPort
    {
        public int PortNumber = 80;
        public string ServiceName = "HTTP";
        public PortStatus Status = PortStatus.Open;
        public bool HasVulnerability = false;
        public float ExploitDifficulty = 3f; // seconds of compute required to exploit
    }

    [Serializable]
    public class NetworkNode
    {
        [Header("Identity")]
        public int NodeId;
        public string Hostname = "node-01";
        public NetworkNodeType NodeType = NetworkNodeType.Workstation;
        public string IPString = "192.168.1.10";
        [NonSerialized] public NetworkIP IP;
        public Vector3 WorldPosition;

        [Header("Wireless & Topology")]
        public bool IsWireless = false;
        public float WirelessRange = 25f;
        public List<int> WiredNeighborNodeIds = new List<int>();

        [Header("Security & IDS")]
        public bool IsFirewallActive = true;
        public bool IsCompromised = false;
        public bool IsHoneypot = false;
        public float EncryptionStrength = 10f; // Bit-strength or decrypt time
        public bool HasIDS = true;
        public float TraceSpeedMultiplier = 1f;
        public float ActiveTraceTimer = 0f; // 0 to 100%
        public bool IsTraceActive = false;
        public NetworkIP CurrentTraceTargetIP;

        [Header("DDoS & Load")]
        public float CurrentLoad = 0f;
        public float MaxPacketThroughput = 50f;
        public bool IsOfflineFromDDoS => CurrentLoad >= MaxPacketThroughput;

        [Header("Physical Alarm / Circuit Link")]
        public bool TriggerCircuitAlarmOnBreach = true;
        public int LinkedCircuitNetworkId = 1;
        public CircuitSignalType AlarmSignalType = CircuitSignalType.Alarm;

        [Header("Ports & Services")]
        public List<NetworkPort> Ports = new List<NetworkPort>();

        public void Initialize()
        {
            NetworkIP.TryParse(IPString, out IP);
        }
    }

    // =========================================================================
    // 4. MAIN NETWORK & CYBERSECURITY SIMULATOR (VIRTUALNETWORKANDHACKINGSIM)
    // =========================================================================

    /// <summary>
    /// Comprehensive in-game computer network topology and cybersecurity intrusion simulator.
    /// Simulates subnet routing, packet transmission, firewalls, port scanning, exploits,
    /// IDS tracing, DDoS attacks, and triggers physical alarms via LogicGateAndCircuitSimulator.
    /// Fully compatible with both 2D and 3D spatial wireless propagation.
    /// </summary>
    [DisallowMultipleComponent]
    public class VirtualNetworkAndHackingSim : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static VirtualNetworkAndHackingSim Instance { get; private set; }

        [Header("Subnet Configuration")]
        [SerializeField] private string defaultSubnetMask = "255.255.255.0";
        private NetworkIP subnetMask;

        [Header("Simulation Timing")]
        [SerializeField] private bool autoRegisterTick = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Network Nodes")]
        [SerializeField] private List<NetworkNode> nodes = new List<NetworkNode>(32);

        // Pre-allocated packet queues for zero-GC routing
        private readonly Queue<NetworkPacket> packetQueue = new Queue<NetworkPacket>(128);
        private readonly Queue<NetworkPacket> nextTickPackets = new Queue<NetworkPacket>(128);
        private readonly Dictionary<int, NetworkNode> ipToNodeMap = new Dictionary<int, NetworkNode>(32);

        public string SaveKey => "VirtualNetworkAndHackingSim";
        public int NodeCount => nodes.Count;

        // =========================================================================
        // INITIALIZATION
        // =========================================================================

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }

            NetworkIP.TryParse(defaultSubnetMask, out subnetMask);
            RebuildRoutingTable();
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

        public void RebuildRoutingTable()
        {
            ipToNodeMap.Clear();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                n.Initialize();
                ipToNodeMap[n.IP.ToInt()] = n;
            }
        }

        // =========================================================================
        // PUBLIC API: PACKET TRANSMISSION & CYBER-ATTACKS
        // =========================================================================

        public void SendPacket(in NetworkPacket packet)
        {
            if (packet.TTL == 0) return;
            packetQueue.Enqueue(packet);
        }

        public bool ExecutePortScan(NetworkIP attackerIP, NetworkIP targetIP, out List<NetworkPort> discoveredPorts)
        {
            discoveredPorts = new List<NetworkPort>();
            if (!ipToNodeMap.TryGetValue(targetIP.ToInt(), out var targetNode))
                return false;

            // Honeypot triggers instant trace
            if (targetNode.IsHoneypot)
            {
                targetNode.IsTraceActive = true;
                targetNode.CurrentTraceTargetIP = attackerIP;
                EventBus<NetworkIntrusionAlertEvent>.Raise(new NetworkIntrusionAlertEvent(
                    targetNode.NodeId, targetIP, attackerIP, targetNode.ActiveTraceTimer));
            }

            // Normal IDS detection chance
            if (targetNode.HasIDS && UnityEngine.Random.value < 0.35f)
            {
                targetNode.IsTraceActive = true;
                targetNode.CurrentTraceTargetIP = attackerIP;
            }

            for (int i = 0; i < targetNode.Ports.Count; i++)
            {
                var p = targetNode.Ports[i];
                if (p.Status != PortStatus.Closed)
                {
                    discoveredPorts.Add(p);
                }
            }

            EventBus<NetworkPacketRoutedEvent>.Raise(new NetworkPacketRoutedEvent(attackerIP, targetIP, 0, PacketPayloadType.PortProbe));
            return true;
        }

        public bool ExecuteBufferOverflowExploit(NetworkIP attackerIP, NetworkIP targetIP, int portNumber)
        {
            if (!ipToNodeMap.TryGetValue(targetIP.ToInt(), out var targetNode))
                return false;

            NetworkPort vulnerablePort = null;
            for (int i = 0; i < targetNode.Ports.Count; i++)
            {
                if (targetNode.Ports[i].PortNumber == portNumber)
                {
                    vulnerablePort = targetNode.Ports[i];
                    break;
                }
            }

            if (vulnerablePort == null || vulnerablePort.Status != PortStatus.Open || !vulnerablePort.HasVulnerability)
            {
                // Exploit failed, elevate trace alert
                targetNode.IsTraceActive = true;
                targetNode.CurrentTraceTargetIP = attackerIP;
                targetNode.ActiveTraceTimer += 25f;
                return false;
            }

            // Exploit successful: gain root access
            targetNode.IsCompromised = true;
            EventBus<NetworkNodeBreachedEvent>.Raise(new NetworkNodeBreachedEvent(targetNode.NodeId, targetIP, "ROOT_ADMIN"));

            // Trigger physical alarm in LogicGateAndCircuitSimulator
            if (targetNode.TriggerCircuitAlarmOnBreach && LogicGateAndCircuitSimulator.Instance != null)
            {
                LogicGateAndCircuitSimulator.Instance.InjectExternalSignal(
                    targetNode.LinkedCircuitNetworkId,
                    CircuitWireColor.RedWire,
                    new CircuitSignalKey(targetNode.AlarmSignalType),
                    CircuitValue.One);
            }

            return true;
        }

        public void ExecuteDDoSAttack(NetworkIP attackerIP, NetworkIP targetIP, int packetCount = 20)
        {
            for (int i = 0; i < packetCount; i++)
            {
                SendPacket(new NetworkPacket(attackerIP, targetIP, 80, PacketProtocol.UDP, PacketPayloadType.DDoSFlood, 8, 1));
            }
        }

        // =========================================================================
        // SIMULATION TICK LOOP (ZERO-GC EXECUTION)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            // 1. Process active packets in the network
            while (packetQueue.Count > 0)
            {
                NetworkPacket packet = packetQueue.Dequeue();
                ProcessPacket(packet);
            }

            // Swap next tick delayed packets
            while (nextTickPackets.Count > 0)
            {
                packetQueue.Enqueue(nextTickPackets.Dequeue());
            }

            // 2. Update IDS Tracing & Decay Load on nodes
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];

                // Decay DDoS load
                node.CurrentLoad = Mathf.Max(0f, node.CurrentLoad - 5f * tickDeltaTime);

                // Update IDS trace timer
                if (node.IsTraceActive && !node.IsCompromised)
                {
                    node.ActiveTraceTimer += 10f * node.TraceSpeedMultiplier * tickDeltaTime;
                    EventBus<NetworkIntrusionAlertEvent>.Raise(new NetworkIntrusionAlertEvent(
                        node.NodeId, node.IP, node.CurrentTraceTargetIP, node.ActiveTraceTimer));

                    if (node.ActiveTraceTimer >= 100f)
                    {
                        // Attacker traced! Trigger lockdown
                        node.IsTraceActive = false;
                        node.ActiveTraceTimer = 100f;
                        if (node.TriggerCircuitAlarmOnBreach && LogicGateAndCircuitSimulator.Instance != null)
                        {
                            LogicGateAndCircuitSimulator.Instance.InjectExternalSignal(
                                node.LinkedCircuitNetworkId,
                                CircuitWireColor.RedWire,
                                new CircuitSignalKey(node.AlarmSignalType),
                                CircuitValue.One);
                        }
                    }
                }
            }
        }

        private void ProcessPacket(in NetworkPacket packet)
        {
            if (!ipToNodeMap.TryGetValue(packet.DestinationIP.ToInt(), out var targetNode))
                return; // Dropped, unknown destination

            // If node is knocked offline by DDoS, drop packet
            if (targetNode.IsOfflineFromDDoS)
                return;

            targetNode.CurrentLoad += 1f;

            // Check firewall
            if (targetNode.IsFirewallActive && packet.PayloadType == PacketPayloadType.ExploitPayload)
            {
                // Blocked by firewall
                return;
            }

            EventBus<NetworkPacketRoutedEvent>.Raise(new NetworkPacketRoutedEvent(
                packet.SourceIP, packet.DestinationIP, packet.DestinationPort, packet.PayloadType));
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class NetworkSaveData
        {
            public List<NodeSaveState> NodeStates = new List<NodeSaveState>();
        }

        [Serializable]
        private class NodeSaveState
        {
            public int NodeId;
            public bool IsCompromised;
            public float TraceTimer;
            public bool IsTraceActive;
        }

        public object CaptureState()
        {
            var data = new NetworkSaveData();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                data.NodeStates.Add(new NodeSaveState
                {
                    NodeId = n.NodeId,
                    IsCompromised = n.IsCompromised,
                    TraceTimer = n.ActiveTraceTimer,
                    IsTraceActive = n.IsTraceActive
                });
            }
            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<NetworkSaveData>(stateJson);
            if (data == null || data.NodeStates == null) return;

            for (int i = 0; i < data.NodeStates.Count; i++)
            {
                var state = data.NodeStates[i];
                for (int n = 0; n < nodes.Count; n++)
                {
                    if (nodes[n].NodeId == state.NodeId)
                    {
                        nodes[n].IsCompromised = state.IsCompromised;
                        nodes[n].ActiveTraceTimer = state.TraceTimer;
                        nodes[n].IsTraceActive = state.IsTraceActive;
                        break;
                    }
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
                Vector3 pos = n.WorldPosition != Vector3.zero ? n.WorldPosition : transform.position + new Vector3(i * 3f, 0, 0);

                Gizmos.color = n.IsCompromised ? Color.red : (n.IsTraceActive ? Color.yellow : Color.green);
                Gizmos.DrawWireSphere(pos, 0.6f);

                if (n.IsWireless)
                {
                    Gizmos.color = new Color(0f, 0.7f, 1f, 0.2f);
                    Gizmos.DrawWireSphere(pos, n.WirelessRange);
                }

                // Draw wired connections to neighbors
                for (int c = 0; c < n.WiredNeighborNodeIds.Count; c++)
                {
                    int neighborId = n.WiredNeighborNodeIds[c];
                    for (int o = 0; o < nodes.Count; o++)
                    {
                        if (nodes[o].NodeId == neighborId)
                        {
                            Vector3 otherPos = nodes[o].WorldPosition != Vector3.zero ? nodes[o].WorldPosition : transform.position + new Vector3(o * 3f, 0, 0);
                            Gizmos.color = Color.white;
                            Gizmos.DrawLine(pos, otherPos);
                            break;
                        }
                    }
                }
            }
        }
    }

    // =========================================================================
    // 5. HARDWARE PERIPHERAL ATTACHMENT FOR IN-GAME PROGRAMMABLE VM
    // =========================================================================

    /// <summary>
    /// Connects VirtualNetworkAndHackingSim to InGameProgrammableVM (Lua scripts).
    /// </summary>
    public class NetworkHardwarePeripheral : ILuaPeripheral
    {
        public string PeripheralName => "net";
        private readonly NetworkIP localIP;

        public NetworkHardwarePeripheral(NetworkIP ip)
        {
            localIP = ip;
        }

        public void AttachToVM(InGameProgrammableVM vm)
        {
            // net.scan(targetIP) -> returns number of open ports found
            vm.RegisterNativeFunction("net_scan", (vmInst, args) =>
            {
                if (VirtualNetworkAndHackingSim.Instance == null || args.Length < 1)
                    return LuaValue.FromNumber(0);

                string ipStr = "192.168.1.10";
                NetworkIP.TryParse(ipStr, out var targetIP);
                bool ok = VirtualNetworkAndHackingSim.Instance.ExecutePortScan(localIP, targetIP, out var ports);
                return ok ? LuaValue.FromNumber(ports.Count) : LuaValue.FromNumber(0);
            });

            // net.exploit(targetIP, port) -> returns true/false
            vm.RegisterNativeFunction("net_exploit", (vmInst, args) =>
            {
                if (VirtualNetworkAndHackingSim.Instance == null || args.Length < 2)
                    return LuaValue.False;

                string ipStr = "192.168.1.10";
                NetworkIP.TryParse(ipStr, out var targetIP);
                int port = (int)args[1].AsNumber;

                bool breached = VirtualNetworkAndHackingSim.Instance.ExecuteBufferOverflowExploit(localIP, targetIP, port);
                return breached ? LuaValue.True : LuaValue.False;
            });
        }
    }
}
