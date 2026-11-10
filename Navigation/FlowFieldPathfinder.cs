using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameSystems.Navigation
{
    // =========================================================================
    // 1. ROZHRANÍ PRO INTEGRACI NAVIGACE
    // =========================================================================

    /// <summary>
    /// Veřejné rozhraní pro navigaci velkých skupin jednotek (RTS / City-Builder)
    /// s O(1) vyhledávací režií na jednotku.
    /// </summary>
    public interface IFlowFieldPathfinder
    {
        int Width { get; }
        int Height { get; }
        float CellSize { get; }
        Vector3 GridOrigin { get; }

        void GenerateFlowField(Vector3 targetWorldPos);
        void GenerateMultiGoalFlowField(IReadOnlyList<Vector3> worldGoals);
        Vector2 SampleFlowDirection(Vector3 worldPos);
        void SetCostWorld(Vector3 worldPos, byte cost);
        void StampObstacleRadius(Vector3 worldCenter, float radius, byte cost);
    }

    // =========================================================================
    // 2. HLAVNÍ MANAŽER FLOW FIELD PATHFINDER
    // =========================================================================

    /// <summary>
    /// Vysoce výkonný Vector Field / Dijkstra Grid Pathfinder pro navigaci tisíců jednotek
    /// s konstantní režií O(1) na entitu. Využívá plochá 1D pole (cache locality),
    /// podporuje multi-cíl cíle, dynamické razítkování překážek a bilineární interpolaci směrů.
    /// </summary>
    [DisallowMultipleComponent]
    public class FlowFieldPathfinder : MonoBehaviour, IFlowFieldPathfinder
    {
        public static FlowFieldPathfinder Instance { get; private set; }

        // Konstanty průchodnosti
        public const byte CostWalkable = 1;
        public const byte CostRoughTerrain = 5;
        public const byte CostImpassable = 255;
        public const ushort IntegrationUnreachable = ushort.MaxValue;

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno po úspěšném přepočtu směrového pole (flow field).</summary>
        public static event Action OnFlowFieldUpdated;

        /// <summary>Vyvoláno při změně nákladu konkrétní buňky mřížky.</summary>
        public static event Action<Vector2Int, byte> OnCellCostChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Rozměry a rozlišení mřížky")]
        [SerializeField, Range(16, 512)] private int gridWidth = 64;
        [SerializeField, Range(16, 512)] private int gridHeight = 64;
        [SerializeField, Min(0.1f)] private float cellSize = 1f;
        [SerializeField] private Vector3 gridOrigin = Vector3.zero;

        [Header("Ladění a Gizmos")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField, Range(1, 4)] private int gizmoStep = 1;

        // =========================================================================
        // PLOCHÁ 1D POLE PRO MAXIMÁLNÍ VÝKON A ZERO-GC
        // =========================================================================

        private byte[] costField;          // 1 = volno, 2..254 = obtížný terén, 255 = neprostupno
        private ushort[] integrationField; // Dijkstra kumulativní vzdálenost k cíli
        private Vector2[] flowField;       // Výsledné normalizované vektory směru

        // Kruhová fronta indexů pro BFS bez alokací paměti
        private int[] bfsQueue;
        private int queueHead;
        private int queueTail;
        private int queueCount;

        // Pre-alokované směrové sousedy (8 směrů: 4 ortogonální + 4 diagonální)
        private struct NeighborOffset
        {
            public int dx;
            public int dy;
            public ushort moveCost; // 100 pro rovně, 141 pro diagonálu
            public Vector2 direction;
        }

        private static readonly NeighborOffset[] Neighbors = new NeighborOffset[]
        {
            new NeighborOffset { dx =  0, dy =  1, moveCost = 100, direction = new Vector2( 0,  1) },
            new NeighborOffset { dx =  1, dy =  0, moveCost = 100, direction = new Vector2( 1,  0) },
            new NeighborOffset { dx =  0, dy = -1, moveCost = 100, direction = new Vector2( 0, -1) },
            new NeighborOffset { dx = -1, dy =  0, moveCost = 100, direction = new Vector2(-1,  0) },
            new NeighborOffset { dx =  1, dy =  1, moveCost = 141, direction = new Vector2( 1,  1).normalized },
            new NeighborOffset { dx =  1, dy = -1, moveCost = 141, direction = new Vector2( 1, -1).normalized },
            new NeighborOffset { dx = -1, dy =  1, moveCost = 141, direction = new Vector2(-1,  1).normalized },
            new NeighborOffset { dx = -1, dy = -1, moveCost = 141, direction = new Vector2(-1, -1).normalized }
        };

        // Cache cílů pro vizualizaci
        private readonly List<Vector2Int> currentGoals = new List<Vector2Int>(16);

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public int Width => gridWidth;
        public int Height => gridHeight;
        public float CellSize => cellSize;
        public Vector3 GridOrigin => gridOrigin;
        public byte[] CostField => costField;
        public ushort[] IntegrationField => integrationField;
        public Vector2[] FlowField => flowField;

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

            InitializeGrid();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // =========================================================================
        // INICIALIZACE MŘÍŽEK (ZERO-GC FLAT ALLOCATION)
        // =========================================================================

        public void InitializeGrid()
        {
            int totalCells = gridWidth * gridHeight;
            costField = new byte[totalCells];
            integrationField = new ushort[totalCells];
            flowField = new Vector2[totalCells];
            bfsQueue = new int[totalCells];

            for (int i = 0; i < totalCells; i++)
            {
                costField[i] = CostWalkable;
                integrationField[i] = IntegrationUnreachable;
                flowField[i] = Vector2.zero;
            }
        }

        // =========================================================================
        // GENERACE FLOW FIELD: JEDINÝ CÍL I VÍCENÁSOBNÉ CÍLE (MULTI-GOAL)
        // =========================================================================

        /// <summary>
        /// Vygeneruje Integration Field a Flow Field k jedinému cíli ve světě.
        /// </summary>
        public void GenerateFlowField(Vector3 targetWorldPos)
        {
            Vector2Int gridTarget = WorldToGrid(targetWorldPos);
            if (!IsValidCell(gridTarget.x, gridTarget.y)) return;

            currentGoals.Clear();
            currentGoals.Add(gridTarget);
            ComputeIntegrationAndFlowFields();
        }

        /// <summary>
        /// Vygeneruje Integration Field a Flow Field k libovolnému množství cílů najednou
        /// (např. obvod velké budovy, linie zákopů nebo pobřeží).
        /// </summary>
        public void GenerateMultiGoalFlowField(IReadOnlyList<Vector3> worldGoals)
        {
            if (worldGoals == null || worldGoals.Count == 0) return;

            currentGoals.Clear();
            int count = worldGoals.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2Int g = WorldToGrid(worldGoals[i]);
                if (IsValidCell(g.x, g.y) && !currentGoals.Contains(g))
                {
                    currentGoals.Add(g);
                }
            }

            ComputeIntegrationAndFlowFields();
        }

        public void GenerateMultiGoalFlowField(IReadOnlyList<Vector2Int> goalCells)
        {
            if (goalCells == null || goalCells.Count == 0) return;

            currentGoals.Clear();
            int count = goalCells.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2Int g = goalCells[i];
                if (IsValidCell(g.x, g.y) && !currentGoals.Contains(g))
                {
                    currentGoals.Add(g);
                }
            }

            ComputeIntegrationAndFlowFields();
        }

        // =========================================================================
        // JÁDRO DIJKSTRA PROPAGACE A VÝPOČET VEKTORŮ (ZERO-GC)
        // =========================================================================

        private void ComputeIntegrationAndFlowFields()
        {
            if (currentGoals.Count == 0) return;

            int totalCells = gridWidth * gridHeight;

            // 1. Reset integračního pole a vektorového pole
            for (int i = 0; i < totalCells; i++)
            {
                integrationField[i] = IntegrationUnreachable;
                flowField[i] = Vector2.zero;
            }

            // 2. Inicializace kruhové fronty bez alokací
            queueHead = 0;
            queueTail = 0;
            queueCount = 0;

            for (int i = 0; i < currentGoals.Count; i++)
            {
                Vector2Int g = currentGoals[i];
                int idx = g.y * gridWidth + g.x;
                integrationField[idx] = 0;
                Enqueue(idx);
            }

            // 3. Vlnová Dijkstra propagace
            while (queueCount > 0)
            {
                int currIdx = Dequeue();
                ushort currentCost = integrationField[currIdx];

                int cx = currIdx % gridWidth;
                int cy = currIdx / gridWidth;

                for (int i = 0; i < Neighbors.Length; i++)
                {
                    int nx = cx + Neighbors[i].dx;
                    int ny = cy + Neighbors[i].dy;

                    if (!IsValidCell(nx, ny)) continue;

                    int nIdx = ny * gridWidth + nx;
                    byte cellCost = costField[nIdx];

                    if (cellCost == CostImpassable) continue;

                    // Výpočet kandidátní ceny s ohledem na ortogonální vs diagonální krok
                    ushort stepCost = (ushort)((cellCost * Neighbors[i].moveCost) / 100);
                    ushort candidateCost = (ushort)(currentCost + stepCost);

                    if (candidateCost < integrationField[nIdx])
                    {
                        integrationField[nIdx] = candidateCost;
                        Enqueue(nIdx);
                    }
                }
            }

            // 4. Výpočet vektorového pole (Flow Field)
            for (int y = 0; y < gridHeight; y++)
            {
                int rowOffset = y * gridWidth;
                for (int x = 0; x < gridWidth; x++)
                {
                    int idx = rowOffset + x;

                    if (costField[idx] == CostImpassable || integrationField[idx] == IntegrationUnreachable)
                    {
                        flowField[idx] = Vector2.zero;
                        continue;
                    }

                    // Pokud je buňka přímo cílem
                    if (integrationField[idx] == 0)
                    {
                        flowField[idx] = Vector2.zero;
                        continue;
                    }

                    ushort bestNeighborCost = integrationField[idx];
                    Vector2 bestDir = Vector2.zero;

                    for (int i = 0; i < Neighbors.Length; i++)
                    {
                        int nx = x + Neighbors[i].dx;
                        int ny = y + Neighbors[i].dy;

                        if (!IsValidCell(nx, ny)) continue;

                        int nIdx = ny * gridWidth + nx;
                        ushort neighborCost = integrationField[nIdx];

                        if (neighborCost < bestNeighborCost)
                        {
                            bestNeighborCost = neighborCost;
                            bestDir = Neighbors[i].direction;
                        }
                    }

                    flowField[idx] = bestDir.normalized;
                }
            }

            OnFlowFieldUpdated?.Invoke();
        }

        private void Enqueue(int index)
        {
            bfsQueue[queueTail] = index;
            queueTail = (queueTail + 1) % bfsQueue.Length;
            queueCount++;
        }

        private int Dequeue()
        {
            int index = bfsQueue[queueHead];
            queueHead = (queueHead + 1) % bfsQueue.Length;
            queueCount--;
            return index;
        }

        // =========================================================================
        // BILINEÁRNÍ INTERPOLACE SMĚRU (BILINEAR SAMPLING)
        // =========================================================================

        /// <summary>
        /// Získá plynule bilineárně interpolovaný směr pohybu na libovolné světové pozici.
        /// Odstraňuje skákání jednotek při přechodu hranic buněk.
        /// </summary>
        public Vector2 SampleFlowDirection(Vector3 worldPos)
        {
            Vector3 local = worldPos - gridOrigin;
            float fx = (local.x / cellSize) - 0.5f;
            float fz = (local.z / cellSize) - 0.5f;

            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            int x1 = x0 + 1;
            int z1 = z0 + 1;

            float u = fx - x0;
            float v = fz - z0;

            Vector2 v00 = GetDirectCellFlow(x0, z0);
            Vector2 v10 = GetDirectCellFlow(x1, z0);
            Vector2 v01 = GetDirectCellFlow(x0, z1);
            Vector2 v11 = GetDirectCellFlow(x1, z1);

            // Bilineární směšování
            Vector2 top = Vector2.Lerp(v00, v10, u);
            Vector2 bottom = Vector2.Lerp(v01, v11, u);
            Vector2 result = Vector2.Lerp(top, bottom, v);

            return result.sqrMagnitude > 0.001f ? result.normalized : result;
        }

        public Vector2 GetDirectCellFlow(int x, int y)
        {
            if (!IsValidCell(x, y)) return Vector2.zero;
            return flowField[y * gridWidth + x];
        }

        // =========================================================================
        // DYNAMICKÉ RAZÍTKOVÁNÍ PŘEKÁŽEK (OBSTACLE STAMPING)
        // =========================================================================

        /// <summary>
        /// Nastaví náklad jedné buňky v souřadnicích mřížky.
        /// </summary>
        public void SetCost(Vector2Int gridPos, byte cost)
        {
            if (!IsValidCell(gridPos.x, gridPos.y)) return;

            int idx = gridPos.y * gridWidth + gridPos.x;
            if (costField[idx] != cost)
            {
                costField[idx] = cost;
                OnCellCostChanged?.Invoke(gridPos, cost);
            }
        }

        /// <summary>
        /// Nastaví náklad buňky podle světové pozice.
        /// </summary>
        public void SetCostWorld(Vector3 worldPos, byte cost)
        {
            SetCost(WorldToGrid(worldPos), cost);
        }

        /// <summary>
        /// Vyrazí kruhovou překážku nebo oblast zhoršeného terénu (např. kráter, bažina, les).
        /// </summary>
        public void StampObstacleRadius(Vector3 worldCenter, float radius, byte cost)
        {
            Vector2Int center = WorldToGrid(worldCenter);
            int cellRadius = Mathf.CeilToInt(radius / cellSize);
            float radiusSqr = radius * radius;

            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    int gx = center.x + dx;
                    int gy = center.y + dy;

                    if (!IsValidCell(gx, gy)) continue;

                    Vector3 cellWorld = GridToWorld(new Vector2Int(gx, gy));
                    float dxWorld = cellWorld.x - worldCenter.x;
                    float dzWorld = cellWorld.z - worldCenter.z;

                    if (dxWorld * dxWorld + dzWorld * dzWorld <= radiusSqr)
                    {
                        SetCost(new Vector2Int(gx, gy), cost);
                    }
                }
            }
        }

        /// <summary>
        /// Vyrazí obdélníkovou překážku (např. budovu z GridPlacementSystem).
        /// </summary>
        public void StampObstacleBox(Vector3 worldCenter, Vector2 size, byte cost)
        {
            Vector3 halfSize = new Vector3(size.x * 0.5f, 0f, size.y * 0.5f);
            Vector2Int minGrid = WorldToGrid(worldCenter - halfSize);
            Vector2Int maxGrid = WorldToGrid(worldCenter + halfSize);

            for (int y = minGrid.y; y <= maxGrid.y; y++)
            {
                for (int x = minGrid.x; x <= maxGrid.x; x++)
                {
                    if (IsValidCell(x, y))
                    {
                        SetCost(new Vector2Int(x, y), cost);
                    }
                }
            }
        }

        /// <summary>
        /// Vyčistí všechny překážky na výchozí hodnotu.
        /// </summary>
        public void ClearAllObstacles()
        {
            int total = gridWidth * gridHeight;
            for (int i = 0; i < total; i++)
            {
                costField[i] = CostWalkable;
            }
        }

        // =========================================================================
        // SOUŘADNICOVÉ PŘEVODY A VALIDACE
        // =========================================================================

        public bool IsValidCell(int x, int y)
        {
            return x >= 0 && x < gridWidth && y >= 0 && y < gridHeight;
        }

        public Vector2Int WorldToGrid(Vector3 worldPos)
        {
            Vector3 local = worldPos - gridOrigin;
            int x = Mathf.FloorToInt(local.x / cellSize);
            int y = Mathf.FloorToInt(local.z / cellSize);
            return new Vector2Int(x, y);
        }

        public Vector3 GridToWorld(Vector2Int gridPos)
        {
            return gridOrigin + new Vector3((gridPos.x + 0.5f) * cellSize, 0f, (gridPos.y + 0.5f) * cellSize);
        }

        // =========================================================================
        // LADÍCÍ GIZMOS PRO SCENE VIEW
        // =========================================================================

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos || flowField == null) return;

            // Rámec mřížky
            Gizmos.color = Color.white;
            Vector3 center = gridOrigin + new Vector3(gridWidth * cellSize * 0.5f, 0f, gridHeight * cellSize * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(gridWidth * cellSize, 0.1f, gridHeight * cellSize));

            // Vektory Flow Field
            Gizmos.color = Color.cyan;
            for (int y = 0; y < gridHeight; y += gizmoStep)
            {
                for (int x = 0; x < gridWidth; x += gizmoStep)
                {
                    int idx = y * gridWidth + x;
                    Vector3 worldPos = GridToWorld(new Vector2Int(x, y));

                    if (costField != null && costField[idx] == CostImpassable)
                    {
                        Gizmos.color = Color.red;
                        Gizmos.DrawCube(worldPos, Vector3.one * (cellSize * 0.5f));
                        Gizmos.color = Color.cyan;
                        continue;
                    }

                    Vector2 dir = flowField[idx];
                    if (dir != Vector2.zero)
                    {
                        Gizmos.DrawRay(worldPos, new Vector3(dir.x, 0f, dir.y) * (cellSize * 0.45f));
                    }
                }
            }

            // Zobrazení cílů (Zlatá)
            Gizmos.color = Color.yellow;
            for (int i = 0; i < currentGoals.Count; i++)
            {
                Gizmos.DrawWireSphere(GridToWorld(currentGoals[i]), cellSize * 0.5f);
            }
        }
    }
}
