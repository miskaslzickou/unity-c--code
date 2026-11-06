using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Simulation;

namespace GameSystems.Utilities
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A DATOVÉ TYPY ENERGETICKÝCH A POTRUBNÍCH SÍTÍ
    // =========================================================================

    public enum UtilityType
    {
        Electricity, // Elektrická síť (elektrárny, baterie, stožáry, továrny)
        Water,       // Vodovodní síť (čerpadla, vodojemy, trubky)
        Heat,        // Teplárenská síť (výtopny, parovody)
        DataNetwork  // Datová síť (servery, optické kabely)
    }

    public enum UtilityNodeType
    {
        Producer,          // Výrobce zdroje (např. uhelná/solární elektrárna, vodárna)
        Consumer,          // Spotřebič (např. továrna, obytný dům, nemocnice)
        Storage,           // Akumulátor / vodojem (nabíjí při přebytku, kryje špičky při nedostatku)
        ConductorOrSwitch  // Vodič / trubka / rozvodna (vede energii, lze jej vypnout vypínačem)
    }

    /// <summary>
    /// Uzel v grafu inženýrských sítí (továrna, sloup, elektrárna).
    /// </summary>
    public class UtilityNode
    {
        public int Id { get; }
        public UtilityType NetworkType { get; set; }
        public UtilityNodeType NodeType { get; set; }
        public Vector3 Position { get; set; }

        public bool IsEnabled { get; set; }
        public float ProductionRate { get; set; }      // kW nebo litrů/s
        public float ConsumptionDemand { get; set; }    // kW nebo litrů/s
        public float StorageCapacity { get; set; }      // Max kapacita úložiště
        public float CurrentStoredAmount { get; set; }  // Aktuálně nabitá energie / voda
        public float MaxChargeRate { get; set; }        // Max rychlost nabíjení
        public float MaxDischargeRate { get; set; }     // Max rychlost vybíjení

        /// <summary>Poměr uspokojení poptávky (0.0 = výpadek / blackout, 1.0 = 100% napájení).</summary>
        public float SatisfactionRatio { get; set; }

        public List<int> ConnectedNeighbors { get; }
        public object UserData { get; set; }

        public UtilityNode(int id, UtilityType netType, UtilityNodeType nodeType, Vector3 pos)
        {
            Id = id;
            NetworkType = netType;
            NodeType = nodeType;
            Position = pos;
            IsEnabled = true;
            ProductionRate = 0f;
            ConsumptionDemand = 0f;
            StorageCapacity = 0f;
            CurrentStoredAmount = 0f;
            MaxChargeRate = 50f;
            MaxDischargeRate = 50f;
            SatisfactionRatio = 1f;
            ConnectedNeighbors = new List<int>(4);
        }
    }

    /// <summary>
    /// Samostatná souvislá komponenta sítě (např. izolovaný ostrovní rozvod elektřiny).
    /// </summary>
    public class SubGrid
    {
        public int Id { get; }
        public UtilityType NetworkType { get; }

        public readonly List<UtilityNode> Producers = new List<UtilityNode>(8);
        public readonly List<UtilityNode> Consumers = new List<UtilityNode>(32);
        public readonly List<UtilityNode> Storages = new List<UtilityNode>(8);
        public readonly List<UtilityNode> Conductors = new List<UtilityNode>(64);

        public float TotalProduction { get; set; }
        public float TotalDemand { get; set; }
        public float TotalStored { get; set; }
        public float TotalStorageCapacity { get; set; }
        public float SatisfactionRatio { get; set; }

        public bool IsBrownout => SatisfactionRatio > 0.001f && SatisfactionRatio < 0.999f;
        public bool IsBlackout => SatisfactionRatio <= 0.001f && TotalDemand > 0.001f;

        public SubGrid(int id, UtilityType type)
        {
            Id = id;
            NetworkType = type;
            SatisfactionRatio = 1f;
        }

        public void Clear()
        {
            Producers.Clear();
            Consumers.Clear();
            Storages.Clear();
            Conductors.Clear();
            TotalProduction = 0f;
            TotalDemand = 0f;
            TotalStored = 0f;
            TotalStorageCapacity = 0f;
            SatisfactionRatio = 1f;
        }
    }

    // =========================================================================
    // 2. HLAVNÍ SOLVER INŽENÝRSKÝCH SÍTÍ (RESOURCE NETWORK GRAPH)
    // =========================================================================

    /// <summary>
    /// Grafový řešič inženýrských sítí pro elektřinu, vodu, teplo a data.
    /// Využívá Union-Find (DSU) pro přepočet komponent pouze při změně topologie sítě.
    /// Každý simulační takt počítá rovnováhu výroby, spotřeby, akumulátorů a brownoutů (zero-GC).
    /// </summary>
    [DisallowMultipleComponent]
    public class ResourceNetworkGraph : MonoBehaviour, ITickable
    {
        public static ResourceNetworkGraph Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS PRO TOVÁRNY, UI A ZVUKY)
        // =========================================================================

        /// <summary>Vyvoláno po vyřešení bilance v podsíti.</summary>
        public static event Action<UtilityType, SubGrid> OnSubGridSolved;

        /// <summary>Vyvoláno při vzniku podpětí / nedostatku (Brownout) s koeficientem uspokojení.</summary>
        public static event Action<UtilityType, int, float> OnBrownoutOccurred; // netType, subGridId, satisfaction

        /// <summary>Vyvoláno při úplném výpadku proudu / vody (Blackout).</summary>
        public static event Action<UtilityType, int> OnBlackoutOccurred;

        /// <summary>Vyvoláno při změně topologie sítě (postavení sloupu, přerušení kabelu).</summary>
        public static event Action<UtilityType> OnTopologyChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Ladící vizualizace (Gizmos)")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private UtilityType gizmoNetworkType = UtilityType.Electricity;

        // =========================================================================
        // VNITŘNÍ REGISTRY UZLŮ A PODSÍTÍ (ZERO-GC)
        // =========================================================================

        private readonly Dictionary<int, UtilityNode> allNodes = new Dictionary<int, UtilityNode>(128);
        private readonly List<SubGrid> activeSubGrids = new List<SubGrid>(16);
        private readonly Stack<SubGrid> subGridPool = new Stack<SubGrid>(16);

        private bool isTopologyDirty = true;
        private int nextNodeId = 1;
        private int nextSubGridId = 1;

        // Pomocné pole pro Union-Find bez alokací
        private readonly Dictionary<int, int> dsuParent = new Dictionary<int, int>(128);

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public IReadOnlyList<SubGrid> ActiveSubGrids => activeSubGrids;
        public int NodeCount => allNodes.Count;

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
            }
        }

        // =========================================================================
        // ITICKABLE IMPLEMENTACE: SOLVER BILANCE SÍTĚ (ZERO-GC)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            // 1. Přestavba topologie podsítí (pouze pokud se změnily uzly nebo spoje)
            if (isTopologyDirty)
            {
                RebuildConnectedComponents();
                isTopologyDirty = false;
            }

            // 2. Výpočet výroby, spotřeby a akumulace v každé podsíti
            SolveSubGridsBalance(tickDeltaTime);
        }

        // =========================================================================
        // FAST UNION-FIND (DSU) PŘESTAVBA PODSÍTÍ POUZE PŘI ZMĚNĚ TOPOLOGIE
        // =========================================================================

        public void RebuildConnectedComponents()
        {
            // Vrácení starých podsítí do poolu
            for (int i = 0; i < activeSubGrids.Count; i++)
            {
                activeSubGrids[i].Clear();
                subGridPool.Push(activeSubGrids[i]);
            }
            activeSubGrids.Clear();
            dsuParent.Clear();

            if (allNodes.Count == 0) return;

            // Inicializace Union-Find pro každý uzel
            foreach (var kvp in allNodes)
            {
                dsuParent[kvp.Key] = kvp.Key;
            }

            // Spojení hran aktivních a povolených uzlů
            foreach (var kvp in allNodes)
            {
                UtilityNode node = kvp.Value;
                if (!node.IsEnabled) continue;

                int rootA = FindRoot(node.Id);
                for (int i = 0; i < node.ConnectedNeighbors.Count; i++)
                {
                    int neighborId = node.ConnectedNeighbors[i];
                    if (allNodes.TryGetValue(neighborId, out UtilityNode neighbor) && neighbor.IsEnabled && neighbor.NetworkType == node.NetworkType)
                    {
                        int rootB = FindRoot(neighbor.Id);
                        if (rootA != rootB)
                        {
                            dsuParent[rootB] = rootA;
                        }
                    }
                }
            }

            // Seskupení uzlů do SubGrid instancí podle reprezentanta DSU
            var groups = new Dictionary<int, SubGrid>(16);

            foreach (var kvp in allNodes)
            {
                UtilityNode node = kvp.Value;
                if (!node.IsEnabled)
                {
                    node.SatisfactionRatio = 0f;
                    continue;
                }

                int root = FindRoot(node.Id);
                if (!groups.TryGetValue(root, out SubGrid subGrid))
                {
                    subGrid = subGridPool.Count > 0 ? subGridPool.Pop() : new SubGrid(nextSubGridId++, node.NetworkType);
                    groups[root] = subGrid;
                    activeSubGrids.Add(subGrid);
                }

                // Zařazení uzlu do příslušného seznamu podsítě
                switch (node.NodeType)
                {
                    case UtilityNodeType.Producer:
                        subGrid.Producers.Add(node);
                        break;
                    case UtilityNodeType.Consumer:
                        subGrid.Consumers.Add(node);
                        break;
                    case UtilityNodeType.Storage:
                        subGrid.Storages.Add(node);
                        break;
                    case UtilityNodeType.ConductorOrSwitch:
                        subGrid.Conductors.Add(node);
                        break;
                }
            }

            OnTopologyChanged?.Invoke(UtilityType.Electricity);
        }

        private int FindRoot(int id)
        {
            if (dsuParent[id] == id) return id;
            dsuParent[id] = FindRoot(dsuParent[id]); // Path compression
            return dsuParent[id];
        }

        // =========================================================================
        // JÁDRO VÝPOČTU ENERGETICKÉ BILANCE V PODSÍTÍCH (ZERO-GC)
        // =========================================================================

        private void SolveSubGridsBalance(float dt)
        {
            int count = activeSubGrids.Count;
            for (int i = 0; i < count; i++)
            {
                SubGrid grid = activeSubGrids[i];

                // 1. Sečtení celkové výroby
                float totalProd = 0f;
                for (int p = 0; p < grid.Producers.Count; p++)
                {
                    totalProd += grid.Producers[p].ProductionRate;
                }

                // 2. Sečtení celkové poptávky spotřebičů
                float totalDemand = 0f;
                for (int c = 0; c < grid.Consumers.Count; c++)
                {
                    totalDemand += grid.Consumers[c].ConsumptionDemand;
                }

                // 3. Stav úložišť (baterie / vodojemy)
                float totalStored = 0f;
                float totalCapacity = 0f;
                for (int s = 0; s < grid.Storages.Count; s++)
                {
                    totalStored += grid.Storages[s].CurrentStoredAmount;
                    totalCapacity += grid.Storages[s].StorageCapacity;
                }

                grid.TotalProduction = totalProd;
                grid.TotalDemand = totalDemand;
                grid.TotalStored = totalStored;
                grid.TotalStorageCapacity = totalCapacity;

                float netBalance = totalProd - totalDemand;

                // --- PŘÍPAD A: PŘEBYTEK ENERGIE / VODY (NET BALANCE >= 0) ---
                if (netBalance >= 0f)
                {
                    grid.SatisfactionRatio = 1.0f;

                    // Nabíjení akumulátorů přebytkem
                    if (netBalance > 0.001f && grid.Storages.Count > 0)
                    {
                        float chargePerStorage = (netBalance * dt) / grid.Storages.Count;
                        for (int s = 0; s < grid.Storages.Count; s++)
                        {
                            UtilityNode st = grid.Storages[s];
                            float maxChargeThisStep = st.MaxChargeRate * dt;
                            float actualCharge = Mathf.Min(chargePerStorage, maxChargeThisStep);
                            st.CurrentStoredAmount = Mathf.Min(st.StorageCapacity, st.CurrentStoredAmount + actualCharge);
                        }
                    }
                }
                // --- PŘÍPAD B: NEDOSTATEK ENERGIE / VODY (NET BALANCE < 0) ---
                else
                {
                    float deficit = -netBalance;
                    float powerFromStorage = 0f;

                    // Pokus o pokrytí deficitu z akumulátorů
                    if (grid.Storages.Count > 0 && totalStored > 0.001f)
                    {
                        for (int s = 0; s < grid.Storages.Count; s++)
                        {
                            UtilityNode st = grid.Storages[s];
                            if (st.CurrentStoredAmount > 0.001f)
                            {
                                float maxDischargeThisStep = st.MaxDischargeRate * dt;
                                float wantedDischarge = (deficit * dt) / grid.Storages.Count;
                                float actualDischarge = Mathf.Min(wantedDischarge, Mathf.Min(maxDischargeThisStep, st.CurrentStoredAmount));

                                st.CurrentStoredAmount -= actualDischarge;
                                powerFromStorage += (actualDischarge / dt);
                            }
                        }
                    }

                    float totalAvailable = totalProd + powerFromStorage;
                    grid.SatisfactionRatio = totalDemand > 0.001f ? Mathf.Clamp01(totalAvailable / totalDemand) : 1f;

                    if (grid.IsBlackout)
                    {
                        OnBlackoutOccurred?.Invoke(grid.NetworkType, grid.Id);
                    }
                    else if (grid.IsBrownout)
                    {
                        OnBrownoutOccurred?.Invoke(grid.NetworkType, grid.Id, grid.SatisfactionRatio);
                    }
                }

                // 4. Předání SatisfactionRatio všem spotřebičům v podsíti
                for (int c = 0; c < grid.Consumers.Count; c++)
                {
                    grid.Consumers[c].SatisfactionRatio = grid.SatisfactionRatio;
                }

                OnSubGridSolved?.Invoke(grid.NetworkType, grid);
            }
        }

        // =========================================================================
        // VEŘEJNÉ API: MANIPULACE S UZLY A SPOJI
        // =========================================================================

        /// <summary>
        /// Zaregistruje nový uzel sítě (elektrárnu, továrnu, sloup, potrubí).
        /// </summary>
        public int RegisterNode(UtilityType netType, UtilityNodeType nodeType, Vector3 position, float prod = 0f, float demand = 0f, float storageCap = 0f, object userData = null)
        {
            int id = nextNodeId++;
            var node = new UtilityNode(id, netType, nodeType, position)
            {
                ProductionRate = prod,
                ConsumptionDemand = demand,
                StorageCapacity = storageCap,
                UserData = userData
            };

            allNodes[id] = node;
            isTopologyDirty = true;
            return id;
        }

        /// <summary>
        /// Odregistruje uzel a rozpojí všechny jeho sousední vazby.
        /// </summary>
        public bool UnregisterNode(int nodeId)
        {
            if (!allNodes.TryGetValue(nodeId, out UtilityNode node)) return false;

            // Odpojení od všech sousedů
            for (int i = 0; i < node.ConnectedNeighbors.Count; i++)
            {
                if (allNodes.TryGetValue(node.ConnectedNeighbors[i], out UtilityNode neighbor))
                {
                    neighbor.ConnectedNeighbors.Remove(nodeId);
                }
            }

            allNodes.Remove(nodeId);
            isTopologyDirty = true;
            return true;
        }

        /// <summary>
        /// Propojí dva uzly sítě elektrickým vedením nebo potrubím.
        /// </summary>
        public void ConnectNodes(int nodeAId, int nodeBId)
        {
            if (nodeAId == nodeBId) return;

            if (allNodes.TryGetValue(nodeAId, out UtilityNode a) && allNodes.TryGetValue(nodeBId, out UtilityNode b))
            {
                if (a.NetworkType == b.NetworkType)
                {
                    if (!a.ConnectedNeighbors.Contains(nodeBId)) a.ConnectedNeighbors.Add(nodeBId);
                    if (!b.ConnectedNeighbors.Contains(nodeAId)) b.ConnectedNeighbors.Add(nodeAId);
                    isTopologyDirty = true;
                }
            }
        }

        /// <summary>
        /// Rozpojí vazbu mezi dvěma uzly.
        /// </summary>
        public void DisconnectNodes(int nodeAId, int nodeBId)
        {
            if (allNodes.TryGetValue(nodeAId, out UtilityNode a) && allNodes.TryGetValue(nodeBId, out UtilityNode b))
            {
                a.ConnectedNeighbors.Remove(nodeBId);
                b.ConnectedNeighbors.Remove(nodeAId);
                isTopologyDirty = true;
            }
        }

        public void SetNodeEnabled(int nodeId, bool isEnabled)
        {
            if (allNodes.TryGetValue(nodeId, out UtilityNode node) && node.IsEnabled != isEnabled)
            {
                node.IsEnabled = isEnabled;
                isTopologyDirty = true;
            }
        }

        public void SetNodeProduction(int nodeId, float production)
        {
            if (allNodes.TryGetValue(nodeId, out UtilityNode node))
            {
                node.ProductionRate = Mathf.Max(0f, production);
            }
        }

        public void SetNodeDemand(int nodeId, float demand)
        {
            if (allNodes.TryGetValue(nodeId, out UtilityNode node))
            {
                node.ConsumptionDemand = Mathf.Max(0f, demand);
            }
        }

        public float GetNodeSatisfaction(int nodeId)
        {
            if (allNodes.TryGetValue(nodeId, out UtilityNode node))
            {
                return node.SatisfactionRatio;
            }
            return 0f;
        }

        public UtilityNode GetNode(int nodeId)
        {
            allNodes.TryGetValue(nodeId, out UtilityNode n);
            return n;
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;

            foreach (var kvp in allNodes)
            {
                UtilityNode node = kvp.Value;
                if (node.NetworkType != gizmoNetworkType) continue;

                // Barva uzlu podle typu
                switch (node.NodeType)
                {
                    case UtilityNodeType.Producer: Gizmos.color = Color.green; break;
                    case UtilityNodeType.Consumer:
                        Gizmos.color = node.SatisfactionRatio > 0.99f ? Color.cyan : (node.SatisfactionRatio > 0.01f ? Color.yellow : Color.red);
                        break;
                    case UtilityNodeType.Storage: Gizmos.color = Color.magenta; break;
                    case UtilityNodeType.ConductorOrSwitch: Gizmos.color = Color.white; break;
                }

                Gizmos.DrawSphere(node.Position, 0.4f);

                // Spojnice s napojenými sousedy
                Gizmos.color = Color.yellow;
                for (int i = 0; i < node.ConnectedNeighbors.Count; i++)
                {
                    if (allNodes.TryGetValue(node.ConnectedNeighbors[i], out UtilityNode neighbor))
                    {
                        if (neighbor.Id > node.Id) // Vykreslit každou linku pouze jednou
                        {
                            Gizmos.DrawLine(node.Position, neighbor.Position);
                        }
                    }
                }
            }
        }
    }
}
