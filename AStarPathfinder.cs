using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace UniversalAStarPathfinding
{
    public enum PathfindingDimension
    {
        TopDown2D,       // 4 or 8-way movement on top-down grids (orthogonal / diagonal)
        Platformer2D,    // Side-scroller grid with jumping, falling, and gravity-aware connections
        ThreeDimensional // 3D Grid pathfinding (XZ plane with Y height levels)
    }

    public class AStarNode
    {
        public Vector3Int gridPosition; // 3D/2D unified grid position
        public bool isWalkable;
        public float gCost;
        public float hCost;
        public float FCost => gCost + hCost;
        public AStarNode parent;

        public AStarNode(Vector3Int pos, bool walkable)
        {
            gridPosition = pos;
            isWalkable = walkable;
            gCost = float.MaxValue;
            hCost = 0f;
            parent = null;
        }
    }

    public class AStarPathfinder : MonoBehaviour
    {
        public static AStarPathfinder Instance;

        [Header("Configuration")]
        public PathfindingDimension dimension = PathfindingDimension.TopDown2D;
        public Tilemap obstacleTilemap; // For 2D Tilemaps
        public LayerMask obstacleLayer3D; // For 3D Physics/Collider checks
        public bool allowDiagonals = true;

        [Header("3D Grid Settings")]
        public float nodeSize = 1.0f;
        public int gridWidth = 50;
        public int gridDepth = 50;
        public int gridHeight = 10;

        [Header("Platformer Specific Settings")]
        public int maxJumpHeight = 3;
        public int maxJumpDistance = 4;

        void Awake()
        {
            Instance = this;
        }

        public List<Vector3> FindPath(Vector3 worldStart, Vector3 worldTarget, Tilemap tilemap = null)
        {
            if (dimension == PathfindingDimension.ThreeDimensional)
            {
                return FindPath3D(worldStart, worldTarget);
            }
            else
            {
                return FindPath2D(worldStart, worldTarget, tilemap);
            }
        }

        // --- 2D PATHFINDING (TopDown & Platformer) ---
        private List<Vector3> FindPath2D(Vector3 worldStart, Vector3 worldTarget, Tilemap tilemap)
        {
            if (tilemap == null) tilemap = obstacleTilemap;
            if (tilemap == null) return new List<Vector3> { worldTarget };

            Vector3Int startCell = tilemap.WorldToCell(worldStart);
            Vector3Int targetCell = tilemap.WorldToCell(worldTarget);

            Dictionary<Vector3Int, AStarNode> allNodes = new Dictionary<Vector3Int, AStarNode>();

            AStarNode GetOrCreateNode(Vector3Int pos, bool walkable)
            {
                if (!allNodes.ContainsKey(pos))
                {
                    allNodes[pos] = new AStarNode(pos, walkable);
                }
                return allNodes[pos];
            }

            bool IsCellWalkable(Vector3Int pos)
            {
                return !tilemap.HasTile(pos);
            }

            AStarNode startNode = GetOrCreateNode(startCell, true);
            AStarNode targetNode = GetOrCreateNode(targetCell, IsCellWalkable(targetCell));

            List<AStarNode> openSet = new List<AStarNode>();
            HashSet<AStarNode> closedSet = new HashSet<AStarNode>();

            openSet.Add(startNode);
            startNode.gCost = 0;
            startNode.hCost = GetHeuristic2D(startCell, targetCell);

            int safetyCounter = 0;
            while (openSet.Count > 0 && safetyCounter < 5000)
            {
                safetyCounter++;

                AStarNode currentNode = openSet[0];
                for (int i = 1; i < openSet.Count; i++)
                {
                    if (openSet[i].FCost < currentNode.FCost ||
                       (openSet[i].FCost == currentNode.FCost && openSet[i].hCost < currentNode.hCost))
                    {
                        currentNode = openSet[i];
                    }
                }

                if (currentNode.gridPosition == targetCell)
                {
                    return RetracePath2D(startNode, currentNode, tilemap);
                }

                openSet.Remove(currentNode);
                closedSet.Add(currentNode);

                foreach (AStarNode neighbor in GetValidNeighbors2D(currentNode, GetOrCreateNode, IsCellWalkable))
                {
                    if (closedSet.Contains(neighbor) || !neighbor.isWalkable)
                        continue;

                    float tentativeGCost = currentNode.gCost + Vector3Int.Distance(currentNode.gridPosition, neighbor.gridPosition);

                    if (tentativeGCost < neighbor.gCost || !openSet.Contains(neighbor))
                    {
                        neighbor.gCost = tentativeGCost;
                        neighbor.hCost = GetHeuristic2D(neighbor.gridPosition, targetCell);
                        neighbor.parent = currentNode;

                        if (!openSet.Contains(neighbor))
                            openSet.Add(neighbor);
                    }
                }
            }

            return new List<Vector3> { worldTarget };
        }

        private float GetHeuristic2D(Vector3Int a, Vector3Int b)
        {
            if (dimension == PathfindingDimension.TopDown2D && allowDiagonals)
            {
                int dx = Mathf.Abs(a.x - b.x);
                int dy = Mathf.Abs(a.y - b.y);
                int min = Mathf.Min(dx, dy);
                int max = Mathf.Max(dx, dy);
                return (min * 1.414f) + (max - min);
            }
            else
            {
                return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
            }
        }

        private List<AStarNode> GetValidNeighbors2D(AStarNode node, System.Func<Vector3Int, bool, AStarNode> nodeFactory, System.Func<Vector3Int, bool> walkableCheck)
        {
            List<AStarNode> neighbors = new List<AStarNode>();
            Vector3Int pos = node.gridPosition;

            if (dimension == PathfindingDimension.TopDown2D)
            {
                Vector3Int[] directions = allowDiagonals ?
                    new Vector3Int[] {
                        new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0), new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
                        new Vector3Int(1, 1, 0), new Vector3Int(1, -1, 0), new Vector3Int(-1, 1, 0), new Vector3Int(-1, -1, 0)
                    } :
                    new Vector3Int[] {
                        new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0), new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0)
                    };

                foreach (var dir in directions)
                {
                    Vector3Int neighborPos = pos + dir;
                    bool walkable = walkableCheck(neighborPos);
                    neighbors.Add(nodeFactory(neighborPos, walkable));
                }
            }
            else if (dimension == PathfindingDimension.Platformer2D)
            {
                for (int xDir = -1; xDir <= 1; xDir += 2)
                {
                    Vector3Int walkPos = new Vector3Int(pos.x + xDir, pos.y, 0);
                    if (walkableCheck(walkPos))
                    {
                        neighbors.Add(nodeFactory(walkPos, true));

                        for (int jx = 1; jx <= maxJumpDistance; jx++)
                        {
                            for (int jy = 1; jy <= maxJumpHeight; jy++)
                            {
                                Vector3Int jumpPos = new Vector3Int(pos.x + (xDir * jx), pos.y + jy, 0);
                                if (walkableCheck(jumpPos))
                                {
                                    neighbors.Add(nodeFactory(jumpPos, true));
                                }
                            }
                        }
                    }
                }

                Vector3Int fallPos = new Vector3Int(pos.x, pos.y - 1, 0);
                if (walkableCheck(fallPos))
                {
                    neighbors.Add(nodeFactory(fallPos, true));
                }
            }

            return neighbors;
        }

        private List<Vector3> RetracePath2D(AStarNode startNode, AStarNode endNode, Tilemap tilemap)
        {
            List<Vector3> path = new List<Vector3>();
            AStarNode currentNode = endNode;

            while (currentNode != null && currentNode != startNode)
            {
                Vector3 worldPos = tilemap.GetCellCenterWorld(currentNode.gridPosition);
                path.Add(worldPos);
                currentNode = currentNode.parent;
            }

            path.Reverse();
            return path;
        }

        // --- 3. 3D PATHFINDING ---
        private List<Vector3> FindPath3D(Vector3 worldStart, Vector3 worldTarget)
        {
            Vector3Int startCell = WorldToGrid3D(worldStart);
            Vector3Int targetCell = WorldToGrid3D(worldTarget);

            Dictionary<Vector3Int, AStarNode> allNodes = new Dictionary<Vector3Int, AStarNode>();

            AStarNode GetOrCreateNode(Vector3Int pos, bool walkable)
            {
                if (!allNodes.ContainsKey(pos))
                {
                    allNodes[pos] = new AStarNode(pos, walkable);
                }
                return allNodes[pos];
            }

            bool IsCellWalkable3D(Vector3Int pos)
            {
                Vector3 worldCenter = GridToWorld3D(pos);
                // Check physics overlap sphere for obstacles in 3D
                bool hitObstacle = Physics.CheckSphere(worldCenter, nodeSize * 0.4f, obstacleLayer3D);
                return !hitObstacle;
            }

            AStarNode startNode = GetOrCreateNode(startCell, true);
            AStarNode targetNode = GetOrCreateNode(targetCell, IsCellWalkable3D(targetCell));

            List<AStarNode> openSet = new List<AStarNode>();
            HashSet<AStarNode> closedSet = new HashSet<AStarNode>();

            openSet.Add(startNode);
            startNode.gCost = 0;
            startNode.hCost = Vector3Int.Distance(startCell, targetCell);

            int safetyCounter = 0;
            while (openSet.Count > 0 && safetyCounter < 6000)
            {
                safetyCounter++;

                AStarNode currentNode = openSet[0];
                for (int i = 1; i < openSet.Count; i++)
                {
                    if (openSet[i].FCost < currentNode.FCost ||
                       (openSet[i].FCost == currentNode.FCost && openSet[i].hCost < currentNode.hCost))
                    {
                        currentNode = openSet[i];
                    }
                }

                if (currentNode.gridPosition == targetCell)
                {
                    return RetracePath3D(startNode, currentNode);
                }

                openSet.Remove(currentNode);
                closedSet.Add(currentNode);

                foreach (AStarNode neighbor in GetValidNeighbors3D(currentNode, GetOrCreateNode, IsCellWalkable3D))
                {
                    if (closedSet.Contains(neighbor) || !neighbor.isWalkable)
                        continue;

                    float tentativeGCost = currentNode.gCost + Vector3Int.Distance(currentNode.gridPosition, neighbor.gridPosition);

                    if (tentativeGCost < neighbor.gCost || !openSet.Contains(neighbor))
                    {
                        neighbor.gCost = tentativeGCost;
                        neighbor.hCost = Vector3Int.Distance(neighbor.gridPosition, targetCell);
                        neighbor.parent = currentNode;

                        if (!openSet.Contains(neighbor))
                            openSet.Add(neighbor);
                    }
                }
            }

            return new List<Vector3> { worldTarget };
        }

        private List<AStarNode> GetValidNeighbors3D(AStarNode node, System.Func<Vector3Int, bool, AStarNode> nodeFactory, System.Func<Vector3Int, bool> walkableCheck)
        {
            List<AStarNode> neighbors = new List<AStarNode>();
            Vector3Int pos = node.gridPosition;

            // 6-way or 26-way 3D directions (orthogonal X, Y, Z)
            Vector3Int[] directions = new Vector3Int[] {
                new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0),
                new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
                new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
            };

            foreach (var dir in directions)
            {
                Vector3Int neighborPos = pos + dir;
                bool walkable = walkableCheck(neighborPos);
                neighbors.Add(nodeFactory(neighborPos, walkable));
            }

            return neighbors;
        }

        private List<Vector3> RetracePath3D(AStarNode startNode, AStarNode endNode)
        {
            List<Vector3> path = new List<Vector3>();
            AStarNode currentNode = endNode;

            while (currentNode != null && currentNode != startNode)
            {
                Vector3 worldPos = GridToWorld3D(currentNode.gridPosition);
                path.Add(worldPos);
                currentNode = currentNode.parent;
            }

            path.Reverse();
            return path;
        }

        private Vector3Int WorldToGrid3D(Vector3 worldPos)
        {
            return new Vector3Int(
                Mathf.RoundToInt(worldPos.x / nodeSize),
                Mathf.RoundToInt(worldPos.y / nodeSize),
                Mathf.RoundToInt(worldPos.z / nodeSize)
            );
        }

        private Vector3 GridToWorld3D(Vector3Int gridPos)
        {
            return new Vector3(
                gridPos.x * nodeSize,
                gridPos.y * nodeSize,
                gridPos.z * nodeSize
            );
        }
    }
}
