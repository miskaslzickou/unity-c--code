using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

/// <summary>
/// Types of social relationships represented in the graph.
/// </summary>
public enum SocialRelationKind
{
    Trust,
    Fear,
    Debt,
    Romantic,
    Kinship,
    CommandHierarchy,
    Blackmail
}

/// <summary>
/// Interrogation technique used when probing a suspect node.
/// </summary>
public enum InterrogationMethod
{
    Rapport,
    Intimidation,
    FinancialBribery,
    BlackmailLeverage,
    PolygraphStress
}

/// <summary>
/// Directed weighted edge connecting two NPC nodes in the social graph.
/// </summary>
[System.Serializable]
public struct SocialEdge
{
    public int FromId;
    public int ToId;
    public SocialRelationKind Kind;
    [Range(-1f, 1f)] public float Weight;
    public bool IsSecret;
    public bool IsCompromised;

    public SocialEdge(int fromId, int toId, SocialRelationKind kind, float weight, bool isSecret = false)
    {
        FromId = fromId;
        ToId = toId;
        Kind = kind;
        Weight = Mathf.Clamp(weight, -1f, 1f);
        IsSecret = isSecret;
        IsCompromised = false;
    }
}

/// <summary>
/// NPC node in the social and conspiracy graph.
/// </summary>
[System.Serializable]
public class SocialNode
{
    public int Id;
    public string Name;
    public string Role;
    public Vector3 Position;
    public Transform TargetTransform;
    [Range(0f, 1f)] public float Willpower;
    [Range(0f, 1f)] public float Stress;
    public bool IsCompromised;
    public int SecretOrgId;
    public int CellId;
    public int HierarchyRank; // 0: operative, 1: cell leader/handler, 2: lieutenant, 3: grandmaster

    public SocialNode(int id, string name, string role, Vector3 position, float willpower = 0.5f, int secretOrgId = -1, int cellId = -1, int hierarchyRank = 0)
    {
        Id = id;
        Name = name;
        Role = role;
        Position = position;
        TargetTransform = null;
        Willpower = Mathf.Clamp01(willpower);
        Stress = 0f;
        IsCompromised = false;
        SecretOrgId = secretOrgId;
        CellId = cellId;
        HierarchyRank = hierarchyRank;
    }

    public Vector3 GetWorldPosition(bool is2D)
    {
        if (TargetTransform != null)
        {
            Vector3 pos = TargetTransform.position;
            return is2D ? new Vector3(pos.x, pos.y, 0f) : pos;
        }
        return is2D ? new Vector3(Position.x, Position.y, 0f) : Position;
    }
}

/// <summary>
/// Compartmentalized clandestine cell representation.
/// </summary>
[System.Serializable]
public class ConspiracyCell
{
    public int CellId;
    public int OrgId;
    public string CellName;
    public int HandlerId;
    public List<int> MemberIds = new List<int>(8);
    public Vector3 RendezvousPoint;
    public bool IsExposed;
}

/// <summary>
/// Secret organization containing multiple compartmentalized cells.
/// </summary>
[System.Serializable]
public class ConspiracyOrganization
{
    public int OrgId;
    public string OrgName;
    public int LeaderId;
    public List<int> CellIds = new List<int>(8);
    [Range(0f, 1f)] public float Paranoia;
    public bool IsDismantled;
}

/// <summary>
/// Result data returned from an interrogation attempt.
/// </summary>
public struct InterrogationResult
{
    public bool Succeeded;
    public float BreakdownPercentage;
    public int RevealedEdgesCount;
    public int RevealedHandlerId;
    public int RevealedCellId;
    public string ConfessionLog;
}

/// <summary>
/// Event raised when a social relationship edge is created or modified.
/// </summary>
public readonly struct SocialEdgeModifiedEvent : IGameEvent
{
    public readonly int FromId;
    public readonly int ToId;
    public readonly SocialRelationKind Kind;
    public readonly float NewWeight;
    public readonly bool IsSecret;

    public SocialEdgeModifiedEvent(int fromId, int toId, SocialRelationKind kind, float newWeight, bool isSecret)
    {
        FromId = fromId;
        ToId = toId;
        Kind = kind;
        NewWeight = newWeight;
        IsSecret = isSecret;
    }
}

/// <summary>
/// Event raised when a conspiracy member cracks under interrogation or investigation.
/// </summary>
public readonly struct ConspiracyNodeCompromisedEvent : IGameEvent
{
    public readonly int NodeId;
    public readonly int OrgId;
    public readonly int CellId;
    public readonly int HandlerId;

    public ConspiracyNodeCompromisedEvent(int nodeId, int orgId, int cellId, int handlerId)
    {
        NodeId = nodeId;
        OrgId = orgId;
        CellId = cellId;
        HandlerId = handlerId;
    }
}

/// <summary>
/// Event raised when an entire clandestine cell has been fully unmasked.
/// </summary>
public readonly struct SecretCellExposedEvent : IGameEvent
{
    public readonly int OrgId;
    public readonly int CellId;
    public readonly string CellName;

    public SecretCellExposedEvent(int orgId, int cellId, string cellName)
    {
        OrgId = orgId;
        CellId = cellId;
        CellName = cellName;
    }
}

/// <summary>
/// Directed weighted social graph modeling relationships, debts, blackmail, family trees,
/// and covert cell hierarchies across hundreds of NPCs with dual 2D/3D compatibility.
/// </summary>
[DisallowMultipleComponent]
public class SocialNetworkAndConspiracyGraph : MonoBehaviour, ITickable, ISaveableSystem
{
    [Header("Mode & Execution Order")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int tickExecutionOrder = 180;

    [Header("Simulation Dynamics")]
    [SerializeField, Range(0.001f, 0.1f)] private float stressDecayRate = 0.02f;
    [SerializeField, Range(0.001f, 0.1f)] private float paranoiaSpreadRate = 0.05f;
    [SerializeField, Range(0f, 1f)] private float interrogationThresholdBase = 0.65f;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private float nodeGizmoRadius = 0.5f;

    // Nodes and Fast Adjacency
    private readonly Dictionary<int, SocialNode> nodes = new Dictionary<int, SocialNode>(256);
    private readonly List<SocialEdge> allEdges = new List<SocialEdge>(512);
    private readonly Dictionary<int, List<int>> outgoingEdgeIndices = new Dictionary<int, List<int>>(256);
    private readonly Dictionary<int, List<int>> incomingEdgeIndices = new Dictionary<int, List<int>>(256);

    // Organizations and Cells
    private readonly Dictionary<int, ConspiracyOrganization> organizations = new Dictionary<int, ConspiracyOrganization>(16);
    private readonly Dictionary<int, ConspiracyCell> cells = new Dictionary<int, ConspiracyCell>(64);

    // Scratch buffers for zero-GC querying
    private readonly List<int> scratchNodeIds = new List<int>(64);
    private readonly Queue<int> bfsQueue = new Queue<int>(64);
    private readonly HashSet<int> visitedNodes = new HashSet<int>(64);

    public int ExecutionOrder => tickExecutionOrder;
    public string SaveKey => "SocialNetworkAndConspiracyGraph_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public int NodeCount => nodes.Count;
    public int EdgeCount => allEdges.Count;

    private void Awake()
    {
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
    // GRAPH MANIPULATION APIS
    // =========================================================================

    /// <summary>
    /// Registers or updates a social node in the graph.
    /// </summary>
    public void AddOrUpdateNode(SocialNode node)
    {
        if (node == null) return;

        if (!nodes.ContainsKey(node.Id))
        {
            nodes[node.Id] = node;
            outgoingEdgeIndices[node.Id] = new List<int>(8);
            incomingEdgeIndices[node.Id] = new List<int>(8);
        }
        else
        {
            nodes[node.Id] = node;
        }
    }

    /// <summary>
    /// Attempts to retrieve a node by its unique ID.
    /// </summary>
    public bool TryGetNode(int nodeId, out SocialNode node)
    {
        return nodes.TryGetValue(nodeId, out node);
    }

    /// <summary>
    /// Adds or updates a directed edge from one node to another.
    /// </summary>
    public void SetEdge(int fromId, int toId, SocialRelationKind kind, float weight, bool isSecret = false)
    {
        if (!nodes.ContainsKey(fromId) || !nodes.ContainsKey(toId))
            return;

        int existingIndex = FindEdgeIndex(fromId, toId, kind);
        if (existingIndex >= 0)
        {
            SocialEdge edge = allEdges[existingIndex];
            edge.Weight = Mathf.Clamp(weight, -1f, 1f);
            edge.IsSecret = isSecret;
            allEdges[existingIndex] = edge;

            EventBus<SocialEdgeModifiedEvent>.Raise(new SocialEdgeModifiedEvent(fromId, toId, kind, edge.Weight, isSecret));
            return;
        }

        int newIndex = allEdges.Count;
        SocialEdge newEdge = new SocialEdge(fromId, toId, kind, weight, isSecret);
        allEdges.Add(newEdge);

        if (!outgoingEdgeIndices.TryGetValue(fromId, out var outList))
        {
            outList = new List<int>(8);
            outgoingEdgeIndices[fromId] = outList;
        }
        outList.Add(newIndex);

        if (!incomingEdgeIndices.TryGetValue(toId, out var inList))
        {
            inList = new List<int>(8);
            incomingEdgeIndices[toId] = inList;
        }
        inList.Add(newIndex);

        EventBus<SocialEdgeModifiedEvent>.Raise(new SocialEdgeModifiedEvent(fromId, toId, kind, newEdge.Weight, isSecret));
    }

    /// <summary>
    /// Attempts to find the edge matching from, to, and relation kind.
    /// </summary>
    public bool TryGetEdge(int fromId, int toId, SocialRelationKind kind, out SocialEdge edge)
    {
        int index = FindEdgeIndex(fromId, toId, kind);
        if (index >= 0)
        {
            edge = allEdges[index];
            return true;
        }
        edge = default;
        return false;
    }

    private int FindEdgeIndex(int fromId, int toId, SocialRelationKind kind)
    {
        if (outgoingEdgeIndices.TryGetValue(fromId, out var edgeIndices))
        {
            for (int i = 0; i < edgeIndices.Count; i++)
            {
                int edgeIdx = edgeIndices[i];
                if (edgeIdx >= 0 && edgeIdx < allEdges.Count)
                {
                    SocialEdge edge = allEdges[edgeIdx];
                    if (edge.ToId == toId && edge.Kind == kind)
                    {
                        return edgeIdx;
                    }
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Zero-GC retrieval of outgoing edges for a node.
    /// </summary>
    public int GetOutgoingEdges(int nodeId, SocialEdge[] outBuffer)
    {
        if (outBuffer == null || !outgoingEdgeIndices.TryGetValue(nodeId, out var list))
            return 0;

        int count = Mathf.Min(list.Count, outBuffer.Length);
        for (int i = 0; i < count; i++)
        {
            outBuffer[i] = allEdges[list[i]];
        }
        return count;
    }

    /// <summary>
    /// Zero-GC retrieval of outgoing edges into a destination List.
    /// </summary>
    public void GetOutgoingEdges(int nodeId, List<SocialEdge> outList)
    {
        if (outList == null) return;
        outList.Clear();

        if (outgoingEdgeIndices.TryGetValue(nodeId, out var list))
        {
            for (int i = 0; i < list.Count; i++)
            {
                outList.Add(allEdges[list[i]]);
            }
        }
    }

    // =========================================================================
    // CONSPIRACY CELL GENERATION & MANAGEMENT
    // =========================================================================

    /// <summary>
    /// Procedurally constructs a clandestine, compartmentalized cell organization.
    /// Operatives know only their handler; handlers know only their direct commander.
    /// </summary>
    public ConspiracyOrganization GenerateCompartmentalizedOrganization(
        int orgId,
        string orgName,
        int cellCount,
        int membersPerCell,
        Vector3 centerPos,
        float spatialSpread,
        float baseParanoia = 0.3f)
    {
        var org = new ConspiracyOrganization
        {
            OrgId = orgId,
            OrgName = orgName,
            Paranoia = Mathf.Clamp01(baseParanoia),
            IsDismantled = false
        };

        // Create Grandmaster / Leader
        int leaderId = orgId * 1000;
        Vector3 leaderPos = centerPos + UnityEngine.Random.insideUnitSphere * (spatialSpread * 0.2f);
        if (defaultTo2D) leaderPos.z = 0f;

        var leaderNode = new SocialNode(leaderId, $"{orgName} Leader", "Grandmaster", leaderPos, willpower: 0.95f, secretOrgId: orgId, cellId: -1, hierarchyRank: 3);
        AddOrUpdateNode(leaderNode);
        org.LeaderId = leaderId;

        // Create Cells
        for (int c = 0; c < cellCount; c++)
        {
            int cellId = orgId * 100 + c;
            Vector3 cellCenter = centerPos + UnityEngine.Random.insideUnitSphere * spatialSpread;
            if (defaultTo2D) cellCenter.z = 0f;

            var cell = new ConspiracyCell
            {
                CellId = cellId,
                OrgId = orgId,
                CellName = $"{orgName} Cell {c + 1}",
                RendezvousPoint = cellCenter,
                IsExposed = false
            };

            // Create Cell Handler
            int handlerId = cellId * 10;
            Vector3 handlerPos = cellCenter + UnityEngine.Random.insideUnitSphere * (spatialSpread * 0.15f);
            if (defaultTo2D) handlerPos.z = 0f;

            var handlerNode = new SocialNode(handlerId, $"{cell.CellName} Handler", "Handler", handlerPos, willpower: 0.8f, secretOrgId: orgId, cellId: cellId, hierarchyRank: 1);
            AddOrUpdateNode(handlerNode);
            cell.HandlerId = handlerId;

            // Link Handler to Leader (Secret Command Edge)
            SetEdge(leaderId, handlerId, SocialRelationKind.CommandHierarchy, 0.9f, isSecret: true);
            SetEdge(handlerId, leaderId, SocialRelationKind.Fear, 0.5f + (org.Paranoia * 0.4f), isSecret: true);

            // Create Cell Operatives (Compartmentalized)
            for (int m = 0; m < membersPerCell; m++)
            {
                int operativeId = handlerId + m + 1;
                Vector3 opPos = cellCenter + UnityEngine.Random.insideUnitSphere * (spatialSpread * 0.3f);
                if (defaultTo2D) opPos.z = 0f;

                float will = UnityEngine.Random.Range(0.4f, 0.75f);
                var opNode = new SocialNode(operativeId, $"{cell.CellName} Operative {m + 1}", "Operative", opPos, willpower: will, secretOrgId: orgId, cellId: cellId, hierarchyRank: 0);
                AddOrUpdateNode(opNode);

                cell.MemberIds.Add(operativeId);

                // Operative only knows their Handler
                SetEdge(handlerId, operativeId, SocialRelationKind.CommandHierarchy, 0.8f, isSecret: true);
                SetEdge(operativeId, handlerId, SocialRelationKind.Trust, 0.6f, isSecret: true);

                // Tangled leverage/debt/fear hooks
                if (m % 2 == 0)
                {
                    SetEdge(handlerId, operativeId, SocialRelationKind.Blackmail, 0.7f, isSecret: true);
                }
                else
                {
                    SetEdge(operativeId, handlerId, SocialRelationKind.Debt, 0.5f, isSecret: false);
                }
            }

            cells[cellId] = cell;
            org.CellIds.Add(cellId);
        }

        organizations[orgId] = org;
        return org;
    }

    // =========================================================================
    // INTERROGATION & INVESTIGATION PROPAGATION
    // =========================================================================

    /// <summary>
    /// Interrogates an NPC node. If pressure overcomes the node's resistance (willpower, stress, trust, leverage),
    /// the node cracks, exposing secrets, handlers, or subordinate operatives.
    /// </summary>
    public bool InterrogateNode(int nodeId, float appliedPressure, InterrogationMethod method, out InterrogationResult result)
    {
        result = new InterrogationResult
        {
            Succeeded = false,
            BreakdownPercentage = 0f,
            RevealedEdgesCount = 0,
            RevealedHandlerId = -1,
            RevealedCellId = -1,
            ConfessionLog = string.Empty
        };

        if (!nodes.TryGetValue(nodeId, out SocialNode node))
        {
            result.ConfessionLog = "Target node does not exist in graph.";
            return false;
        }

        // Calculate leverage and relationship modifiers
        float leverageBonus = 0f;
        float trustFactor = 0f;

        if (incomingEdgeIndices.TryGetValue(nodeId, out var inEdges))
        {
            for (int i = 0; i < inEdges.Count; i++)
            {
                SocialEdge e = allEdges[inEdges[i]];
                if (e.Kind == SocialRelationKind.Blackmail && method == InterrogationMethod.BlackmailLeverage)
                {
                    leverageBonus += Mathf.Abs(e.Weight) * 0.4f;
                }
                else if (e.Kind == SocialRelationKind.Debt && method == InterrogationMethod.FinancialBribery)
                {
                    leverageBonus += Mathf.Abs(e.Weight) * 0.35f;
                }
                else if (e.Kind == SocialRelationKind.Trust)
                {
                    trustFactor += e.Weight * 0.25f;
                }
            }
        }

        // Resistance calculation
        float currentResistance = node.Willpower + (trustFactor * 0.5f) - (node.Stress * 0.4f);
        if (method == InterrogationMethod.Intimidation)
        {
            currentResistance -= (node.Stress * 0.2f);
        }
        else if (method == InterrogationMethod.Rapport)
        {
            currentResistance -= 0.15f;
        }

        float effectivePressure = appliedPressure + leverageBonus;
        float breakdown = Mathf.Clamp01(effectivePressure / Mathf.Max(0.01f, currentResistance + interrogationThresholdBase));
        result.BreakdownPercentage = breakdown;

        // Increase suspect stress from interrogation
        node.Stress = Mathf.Clamp01(node.Stress + (appliedPressure * 0.35f));

        if (breakdown >= 1.0f || node.IsCompromised)
        {
            node.IsCompromised = true;
            result.Succeeded = true;
            result.RevealedCellId = node.CellId;

            // Uncover secrets connected to this node
            int revealedCount = 0;
            if (outgoingEdgeIndices.TryGetValue(nodeId, out var outEdges))
            {
                for (int i = 0; i < outEdges.Count; i++)
                {
                    SocialEdge edge = allEdges[outEdges[i]];
                    if (edge.IsSecret && !edge.IsCompromised)
                    {
                        edge.IsCompromised = true;
                        allEdges[outEdges[i]] = edge;
                        revealedCount++;
                    }
                }
            }

            if (incomingEdgeIndices.TryGetValue(nodeId, out inEdges))
            {
                for (int i = 0; i < inEdges.Count; i++)
                {
                    SocialEdge edge = allEdges[inEdges[i]];
                    if (edge.IsSecret && !edge.IsCompromised)
                    {
                        edge.IsCompromised = true;
                        allEdges[inEdges[i]] = edge;
                        revealedCount++;
                    }
                    if (edge.Kind == SocialRelationKind.CommandHierarchy)
                    {
                        result.RevealedHandlerId = edge.FromId;
                    }
                }
            }

            result.RevealedEdgesCount = revealedCount;
            result.ConfessionLog = $"{node.Name} cracked! Revealed {revealedCount} secret connections. Handler: {result.RevealedHandlerId}. Cell: {node.CellId}.";

            EventBus<ConspiracyNodeCompromisedEvent>.Raise(new ConspiracyNodeCompromisedEvent(node.Id, node.SecretOrgId, node.CellId, result.RevealedHandlerId));

            // Check if cell is exposed
            if (node.CellId >= 0 && cells.TryGetValue(node.CellId, out ConspiracyCell cell))
            {
                CheckCellExposure(cell);
            }

            // Paranoia cascade: increase stress on handler or connected superiors
            if (result.RevealedHandlerId >= 0 && nodes.TryGetValue(result.RevealedHandlerId, out SocialNode handler))
            {
                handler.Stress = Mathf.Clamp01(handler.Stress + 0.3f);
            }

            return true;
        }

        result.ConfessionLog = $"{node.Name} resisted interrogation. Breakdown: {breakdown * 100f:F1}%.";
        return false;
    }

    private void CheckCellExposure(ConspiracyCell cell)
    {
        if (cell.IsExposed) return;

        bool handlerCompromised = false;
        if (nodes.TryGetValue(cell.HandlerId, out SocialNode handler) && handler.IsCompromised)
        {
            handlerCompromised = true;
        }

        int compromisedMembers = 0;
        for (int i = 0; i < cell.MemberIds.Count; i++)
        {
            if (nodes.TryGetValue(cell.MemberIds[i], out SocialNode member) && member.IsCompromised)
            {
                compromisedMembers++;
            }
        }

        // If handler is broken or majority of cell members are broken, cell is exposed
        if (handlerCompromised || (cell.MemberIds.Count > 0 && compromisedMembers >= (cell.MemberIds.Count + 1) / 2))
        {
            cell.IsExposed = true;
            EventBus<SecretCellExposedEvent>.Raise(new SecretCellExposedEvent(cell.OrgId, cell.CellId, cell.CellName));

            // Increase paranoia in organization
            if (organizations.TryGetValue(cell.OrgId, out ConspiracyOrganization org))
            {
                org.Paranoia = Mathf.Clamp01(org.Paranoia + 0.25f);
                CheckOrganizationCollapse(org);
            }
        }
    }

    private void CheckOrganizationCollapse(ConspiracyOrganization org)
    {
        if (org.IsDismantled) return;

        int exposedCells = 0;
        for (int i = 0; i < org.CellIds.Count; i++)
        {
            if (cells.TryGetValue(org.CellIds[i], out ConspiracyCell cell) && cell.IsExposed)
            {
                exposedCells++;
            }
        }

        bool leaderCompromised = false;
        if (nodes.TryGetValue(org.LeaderId, out SocialNode leader) && leader.IsCompromised)
        {
            leaderCompromised = true;
        }

        if (leaderCompromised || exposedCells == org.CellIds.Count)
        {
            org.IsDismantled = true;
            Debug.Log($"[SocialNetworkAndConspiracyGraph] Secret organization '{org.OrgName}' has been dismantled!");
        }
    }

    // =========================================================================
    // SPATIAL AND PATH QUERYING (DUAL 2D / 3D)
    // =========================================================================

    /// <summary>
    /// Finds all NPCs within a specified spatial radius in either 2D or 3D.
    /// </summary>
    public int QueryNPCsInRadius(Vector3 center, float radius, SocialNode[] outBuffer)
    {
        if (outBuffer == null || outBuffer.Length == 0) return 0;

        float sqrRadius = radius * radius;
        int count = 0;

        foreach (var kvp in nodes)
        {
            SocialNode node = kvp.Value;
            Vector3 pos = node.GetWorldPosition(defaultTo2D);

            float sqrDist = defaultTo2D
                ? (new Vector2(pos.x - center.x, pos.y - center.y)).sqrMagnitude
                : (pos - center).sqrMagnitude;

            if (sqrDist <= sqrRadius)
            {
                outBuffer[count++] = node;
                if (count >= outBuffer.Length) break;
            }
        }

        return count;
    }

    /// <summary>
    /// Finds the shortest influence path between two NPCs using BFS (Zero-GC execution).
    /// </summary>
    public bool TryFindShortestPath(int startNodeId, int targetNodeId, List<int> outPath)
    {
        if (outPath == null) return false;
        outPath.Clear();

        if (startNodeId == targetNodeId)
        {
            outPath.Add(startNodeId);
            return true;
        }

        if (!nodes.ContainsKey(startNodeId) || !nodes.ContainsKey(targetNodeId))
            return false;

        bfsQueue.Clear();
        visitedNodes.Clear();
        var parentMap = new Dictionary<int, int>(64);

        bfsQueue.Enqueue(startNodeId);
        visitedNodes.Add(startNodeId);

        bool reached = false;

        while (bfsQueue.Count > 0)
        {
            int current = bfsQueue.Dequeue();
            if (current == targetNodeId)
            {
                reached = true;
                break;
            }

            if (outgoingEdgeIndices.TryGetValue(current, out var edgeIndices))
            {
                for (int i = 0; i < edgeIndices.Count; i++)
                {
                    int toId = allEdges[edgeIndices[i]].ToId;
                    if (!visitedNodes.Contains(toId))
                    {
                        visitedNodes.Add(toId);
                        parentMap[toId] = current;
                        bfsQueue.Enqueue(toId);
                    }
                }
            }
        }

        if (!reached) return false;

        // Reconstruct path
        int curr = targetNodeId;
        while (curr != startNodeId)
        {
            outPath.Add(curr);
            curr = parentMap[curr];
        }
        outPath.Add(startNodeId);
        outPath.Reverse();
        return true;
    }

    // =========================================================================
    // SIMULATION TICK (ITickable)
    // =========================================================================

    public void OnSimulationTick(int tickIndex, float tickDeltaTime) => OnSimulationTick(tickDeltaTime);

    public void OnSimulationTick(float deltaTime)
    {
        // Decay stress over time
        foreach (var kvp in nodes)
        {
            SocialNode node = kvp.Value;
            if (node.Stress > 0f)
            {
                node.Stress = Mathf.Max(0f, node.Stress - (stressDecayRate * deltaTime));
            }
        }

        // Paranoia dynamics in organizations
        foreach (var orgKvp in organizations)
        {
            ConspiracyOrganization org = orgKvp.Value;
            if (org.Paranoia > 0.05f && !org.IsDismantled)
            {
                // Paranoia slowly decays back toward baseline unless exposed cells exist
                int exposedCount = 0;
                for (int i = 0; i < org.CellIds.Count; i++)
                {
                    if (cells.TryGetValue(org.CellIds[i], out var cell) && cell.IsExposed)
                    {
                        exposedCount++;
                    }
                }

                if (exposedCount == 0)
                {
                    org.Paranoia = Mathf.Max(0.05f, org.Paranoia - (paranoiaSpreadRate * 0.1f * deltaTime));
                }
            }
        }
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public List<SocialNode> Nodes = new List<SocialNode>();
        public List<SocialEdge> Edges = new List<SocialEdge>();
        public List<ConspiracyOrganization> Organizations = new List<ConspiracyOrganization>();
        public List<ConspiracyCell> Cells = new List<ConspiracyCell>();
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        var container = new SaveDataContainer();
        foreach (var kvp in nodes) container.Nodes.Add(kvp.Value);
        container.Edges.AddRange(allEdges);
        foreach (var kvp in organizations) container.Organizations.Add(kvp.Value);
        foreach (var kvp in cells) container.Cells.Add(kvp.Value);

        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null) return;

        nodes.Clear();
        allEdges.Clear();
        outgoingEdgeIndices.Clear();
        incomingEdgeIndices.Clear();
        organizations.Clear();
        cells.Clear();

        for (int i = 0; i < container.Nodes.Count; i++)
        {
            AddOrUpdateNode(container.Nodes[i]);
        }

        for (int i = 0; i < container.Edges.Count; i++)
        {
            SocialEdge e = container.Edges[i];
            SetEdge(e.FromId, e.ToId, e.Kind, e.Weight, e.IsSecret);
            if (e.IsCompromised)
            {
                int idx = FindEdgeIndex(e.FromId, e.ToId, e.Kind);
                if (idx >= 0)
                {
                    SocialEdge edge = allEdges[idx];
                    edge.IsCompromised = true;
                    allEdges[idx] = edge;
                }
            }
        }

        for (int i = 0; i < container.Organizations.Count; i++)
        {
            organizations[container.Organizations[i].OrgId] = container.Organizations[i];
        }

        for (int i = 0; i < container.Cells.Count; i++)
        {
            cells[container.Cells[i].CellId] = container.Cells[i];
        }
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        // Draw Nodes
        foreach (var kvp in nodes)
        {
            SocialNode node = kvp.Value;
            Vector3 pos = node.GetWorldPosition(defaultTo2D);

            Color nodeColor;
            if (node.IsCompromised)
            {
                nodeColor = Color.red;
            }
            else if (node.SecretOrgId >= 0)
            {
                switch (node.HierarchyRank)
                {
                    case 3: nodeColor = new Color(0.7f, 0.2f, 0.9f); break; // Grandmaster (Purple)
                    case 2: nodeColor = new Color(0.9f, 0.5f, 0.1f); break; // Lieutenant (Orange)
                    case 1: nodeColor = Color.yellow; break;                // Handler (Yellow)
                    default: nodeColor = Color.cyan; break;                 // Operative (Cyan)
                }
            }
            else
            {
                nodeColor = Color.green; // Civilian / Neutral
            }

            Gizmos.color = nodeColor;
            if (defaultTo2D)
            {
                Gizmos.DrawWireSphere(pos, nodeGizmoRadius);
            }
            else
            {
                Gizmos.DrawSphere(pos, nodeGizmoRadius);
            }
        }

        // Draw Edges
        for (int i = 0; i < allEdges.Count; i++)
        {
            SocialEdge edge = allEdges[i];
            if (!nodes.TryGetValue(edge.FromId, out SocialNode fromNode) ||
                !nodes.TryGetValue(edge.ToId, out SocialNode toNode))
                continue;

            Vector3 fromPos = fromNode.GetWorldPosition(defaultTo2D);
            Vector3 toPos = toNode.GetWorldPosition(defaultTo2D);

            Color edgeColor;
            switch (edge.Kind)
            {
                case SocialRelationKind.Trust: edgeColor = Color.green; break;
                case SocialRelationKind.Fear: edgeColor = Color.red; break;
                case SocialRelationKind.Debt: edgeColor = Color.yellow; break;
                case SocialRelationKind.Romantic: edgeColor = Color.magenta; break;
                case SocialRelationKind.Kinship: edgeColor = new Color(0.2f, 0.8f, 0.8f); break;
                case SocialRelationKind.CommandHierarchy: edgeColor = Color.blue; break;
                case SocialRelationKind.Blackmail: edgeColor = new Color(0.5f, 0f, 0.5f); break;
                default: edgeColor = Color.white; break;
            }

            if (edge.IsSecret && !edge.IsCompromised)
            {
                edgeColor.a = 0.25f; // Faint if secret and unrevealed
            }

            Gizmos.color = edgeColor;
            Gizmos.DrawLine(fromPos, toPos);
        }
    }
}
