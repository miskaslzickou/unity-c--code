using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace UniversalLevelToolkit
{
    public enum GenerationMode
    {
        Horizontal1D,       // Side-scroller / Platformer style (Perlin noise height columns + premade chunks)
        TopDownDungeon2D,   // Room-to-room dungeon chaining or grid map
        OpenWorldGrid3D     // 3D Voxel / Block terrain generation (XZ plane with height)
    }

    public enum SegmentType
    {
        Generated,          // Procedural generation (Noise / Cellular Automata)
        Premade             // Hand-crafted prefab tilemap / template
    }

    [System.Serializable]
    public class LevelSegment
    {
        public string name = "Segment";
        public SegmentType type = SegmentType.Generated;

        [Header("1D / Horizontal Settings")]
        public int generatedWidth = 20;

        [Header("2D / 3D Grid Bounds")]
        public int generatedHeight = 20;
        public int generatedDepth = 20;

        [Header("Premade Templates")]
        [Tooltip("Prefab tilemaps to instantiate or copy chunks from.")]
        public Tilemap[] premadeVariants;

        [Header("Offsets")]
        public int xOffset = 0;
        public int yOffset = 0;
        public int zOffset = 0;
    }

    public class UniversalLevelGenerator : MonoBehaviour
    {
        public static UniversalLevelGenerator Instance;

        [Header("Architecture Mode")]
        public GenerationMode generationMode = GenerationMode.Horizontal1D;

        [Header("Tilemaps & Tiles")]
        public Tilemap primaryTilemap;     // Ground / Floor
        public Tilemap secondaryTilemap;   // Walls / Details / Roofs
        public TileBase primaryTile;
        public TileBase secondaryTile;

        [Header("Segments Sequence")]
        public List<LevelSegment> segments = new List<LevelSegment>();

        [Header("Procedural Noise Parameters")]
        public int mapWidth = 100;
        public int mapHeight = 30;
        public int mapDepth = 50;
        public float noiseScale = 0.05f;
        public float seed;
        public bool useRandomSeed = true;
        public int minGroundHeight = 3;
        public int airSpace = 5;

        [Header("Runtime & Spawning")]
        public bool generateOnStart = false;
        public Transform player;
        public bool randomSpawn = false;
        public int spawnX = 5;
        public int spawnY = 5;
        public int spawnZ = 5;
        public float spawnOffsetY = 0.5f;
        public string spawnMarkerName = "PlayerSpawn";

        [HideInInspector] public bool hasSavedSpawn;
        [HideInInspector] public Vector3 savedSpawnPosition;
        [HideInInspector] public int totalWidth;

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            if (generateOnStart)
                GenerateInEditor();

            SpawnPlayer();
        }

        public void GenerateInEditor()
        {
            if (useRandomSeed)
                seed = Random.Range(-10000f, 10000f);

            BuildLevel();

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.SetDirty(this);
                if (primaryTilemap != null) UnityEditor.EditorUtility.SetDirty(primaryTilemap);
                if (secondaryTilemap != null) UnityEditor.EditorUtility.SetDirty(secondaryTilemap);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
        }

        public void ClearMap()
        {
            if (primaryTilemap != null) primaryTilemap.ClearAllTiles();
            if (secondaryTilemap != null) secondaryTilemap.ClearAllTiles();
            hasSavedSpawn = false;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.SetDirty(this);
                if (primaryTilemap != null) UnityEditor.EditorUtility.SetDirty(primaryTilemap);
                if (secondaryTilemap != null) UnityEditor.EditorUtility.SetDirty(secondaryTilemap);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
        }

        private void BuildLevel()
        {
            if (primaryTilemap != null) primaryTilemap.ClearAllTiles();
            if (secondaryTilemap != null) secondaryTilemap.ClearAllTiles();
            hasSavedSpawn = false;

            switch (generationMode)
            {
                case GenerationMode.Horizontal1D:
                    BuildHorizontal1D();
                    break;
                case GenerationMode.TopDownDungeon2D:
                    BuildTopDownDungeon2D();
                    break;
                case GenerationMode.OpenWorldGrid3D:
                    BuildOpenWorldGrid3D();
                    break;
            }
        }

        // --- 1. HORIZONTAL 1D (Side-Scroller / Platformer) ---
        private void BuildHorizontal1D()
        {
            int cursorX = 0;

            if (segments == null || segments.Count == 0)
            {
                cursorX += PlaceGeneratedHorizontalChunk(new LevelSegment { generatedWidth = mapWidth, yOffset = 0 }, cursorX);
            }
            else
            {
                foreach (LevelSegment segment in segments)
                {
                    int targetX = cursorX + segment.xOffset;
                    if (segment.type == SegmentType.Generated)
                        cursorX += PlaceGeneratedHorizontalChunk(segment, targetX);
                    else
                        cursorX += PlacePremadeChunk(segment, targetX, segment.yOffset);
                }
            }

            totalWidth = cursorX;
        }

        private int PlaceGeneratedHorizontalChunk(LevelSegment segment, int startX)
        {
            if (primaryTilemap == null || primaryTile == null) return segment.generatedWidth;

            int maxHeight = mapHeight - airSpace;

            for (int i = 0; i < segment.generatedWidth; i++)
            {
                int globalX = startX + i;
                float noiseVal = Mathf.PerlinNoise(globalX * noiseScale, seed);

                int groundHeight = Mathf.Clamp(
                    minGroundHeight + Mathf.RoundToInt(noiseVal * (maxHeight - minGroundHeight)),
                    0,
                    mapHeight - 1
                );

                for (int y = 0; y <= groundHeight; y++)
                {
                    primaryTilemap.SetTile(new Vector3Int(globalX, y + segment.yOffset, 0), primaryTile);
                }
            }

            return segment.generatedWidth;
        }

        // --- 2. TOP-DOWN 2D DUNGEON ---
        private void BuildTopDownDungeon2D()
        {
            int cursorX = 0;
            int cursorY = 0;

            if (segments == null || segments.Count == 0)
            {
                // Fallback procedural room/cave grid
                BuildOpenWorldGrid3D(); // Can act as 2D grid when Z = 1
                return;
            }

            foreach (LevelSegment segment in segments)
            {
                int targetX = cursorX + segment.xOffset;
                int targetY = cursorY + segment.yOffset;

                if (segment.type == SegmentType.Generated)
                {
                    cursorX += PlaceGeneratedDungeonRoom2D(segment, targetX, targetY);
                }
                else
                {
                    cursorX += PlacePremadeChunk(segment, targetX, targetY) + 2; // corridor gap
                }
            }

            totalWidth = cursorX;
        }

        private int PlaceGeneratedDungeonRoom2D(LevelSegment segment, int startX, int startY)
        {
            if (primaryTilemap == null || primaryTile == null) return segment.generatedWidth;

            int w = segment.generatedWidth > 0 ? segment.generatedWidth : 15;
            int h = segment.generatedHeight > 0 ? segment.generatedHeight : 15;

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int gx = startX + x;
                    int gy = startY + y;

                    // Walls on borders, floor inside
                    bool isBorder = (x == 0 || x == w - 1 || y == 0 || y == h - 1);

                    if (isBorder && secondaryTilemap != null && secondaryTile != null)
                    {
                        secondaryTilemap.SetTile(new Vector3Int(gx, gy, 0), secondaryTile);
                    }
                    else
                    {
                        primaryTilemap.SetTile(new Vector3Int(gx, gy, 0), primaryTile);
                    }
                }
            }

            return w;
        }

        // --- 3. 3D OPEN WORLD GRID / VOXEL TERRAIN ---
        private void BuildOpenWorldGrid3D()
        {
            if (primaryTilemap == null || primaryTile == null) return;

            for (int x = 0; x < mapWidth; x++)
            {
                for (int z = 0; z < mapDepth; z++)
                {
                    float sampleX = (x + seed) * noiseScale;
                    float sampleZ = (z + seed) * noiseScale;
                    float noiseVal = Mathf.PerlinNoise(sampleX, sampleZ);

                    int colHeight = Mathf.RoundToInt(noiseVal * mapHeight);

                    for (int y = 0; y <= colHeight; y++)
                    {
                        // Map 3D coordinates into 2D Tilemap grid using isometric/pseudo-3D or standard projection
                        int projectedCellX = x + (z * 2); // staggered pseudo-3D grid projection
                        int projectedCellY = y - z;

                        primaryTilemap.SetTile(new Vector3Int(projectedCellX, projectedCellY, 0), primaryTile);
                    }
                }
            }

            totalWidth = mapWidth;
        }

        // --- SHARED PREMADE CHUNK STITCHER ---
        private int PlacePremadeChunk(LevelSegment segment, int startX, int startY)
        {
            if (segment.premadeVariants == null || segment.premadeVariants.Length == 0)
            {
                Debug.LogWarning($"UniversalLevelGenerator: Segment '{segment.name}' has no premade variants.");
                return 10;
            }

            Tilemap source = segment.premadeVariants[Random.Range(0, segment.premadeVariants.Length)];
            if (source == null || primaryTilemap == null) return 10;

            BoundsInt bounds = source.cellBounds;
            TileBase[] tiles = source.GetTilesBlock(bounds);

            Vector3Int targetOrigin = new Vector3Int(startX, startY, 0);
            BoundsInt targetBounds = new BoundsInt(targetOrigin, bounds.size);

            primaryTilemap.SetTilesBlock(targetBounds, tiles);

            TryExtractSpawnMarker(source, targetOrigin, bounds);

            return bounds.size.x;
        }

        private void TryExtractSpawnMarker(Tilemap source, Vector3Int targetOrigin, BoundsInt bounds)
        {
            if (!hasSavedSpawn && !string.IsNullOrEmpty(spawnMarkerName) && primaryTilemap != null)
            {
                Transform marker = FindChildRecursive(source.transform.root, spawnMarkerName);
                if (marker != null)
                {
                    Vector3 local = marker.position - source.transform.position;
                    Vector3 shift = new Vector3(
                        targetOrigin.x - bounds.xMin,
                        targetOrigin.y - bounds.yMin,
                        0f
                    );

                    Vector3 cellPos = local + shift;
                    savedSpawnPosition = primaryTilemap.transform.TransformPoint(
                        Vector3.Scale(cellPos, primaryTilemap.layoutGrid.cellSize)
                    );
                    hasSavedSpawn = true;
                }
            }
        }

        private Transform FindChildRecursive(Transform parent, string childName)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == childName)
                    return child;
            }
            return null;
        }

        private void SpawnPlayer()
        {
            if (player == null || primaryTilemap == null) return;

            Vector3 pos;

            if (hasSavedSpawn && !randomSpawn)
            {
                pos = savedSpawnPosition;
            }
            else
            {
                if (generationMode == GenerationMode.Horizontal1D)
                {
                    int width = Mathf.Max(1, totalWidth > 0 ? totalWidth : primaryTilemap.cellBounds.xMax);
                    int x = randomSpawn ? Random.Range(0, width) : Mathf.Clamp(spawnX, 0, width - 1);
                    pos = GetSurfaceWorldPosition(x);
                }
                else
                {
                    pos = primaryTilemap.GetCellCenterWorld(new Vector3Int(spawnX, spawnY, 0));
                }
            }

            pos.y += spawnOffsetY;
            player.position = pos;

            Rigidbody2D rb2d = player.GetComponent<Rigidbody2D>();
            if (rb2d != null) rb2d.linearVelocity = Vector2.zero;

            Rigidbody rb = player.GetComponent<Rigidbody>();
            if (rb != null) rb.linearVelocity = Vector3.zero;
        }

        public int GetGroundHeight(int x)
        {
            if (primaryTilemap == null) return 0;
            BoundsInt b = primaryTilemap.cellBounds;

            for (int y = b.yMax - 1; y >= b.yMin; y--)
            {
                if (primaryTilemap.HasTile(new Vector3Int(x, y, 0)))
                    return y;
            }
            return 0;
        }

        public Vector3 GetSurfaceWorldPosition(int x)
        {
            if (primaryTilemap == null) return Vector3.zero;
            int y = GetGroundHeight(x);
            return primaryTilemap.GetCellCenterWorld(new Vector3Int(x, y + 1, 0));
        }
    }
}
