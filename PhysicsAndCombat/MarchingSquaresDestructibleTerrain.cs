using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.PhysicsAndCombat;
using GameSystems.Events;
using GameSystems.Persistence;

/// <summary>
/// Event raised whenever a region of the terrain scalar field is modified.
/// </summary>
public readonly struct TerrainDeformedEvent : IGameEvent
{
    public readonly Vector3 Center;
    public readonly float Radius;
    public readonly bool IsCarve;

    public TerrainDeformedEvent(Vector3 center, float radius, bool isCarve)
    {
        Center = center;
        Radius = radius;
        IsCarve = isCarve;
    }
}

/// <summary>
/// Represents a single spatial chunk within the destructible terrain grid.
/// </summary>
public class TerrainChunk
{
    public int ChunkX;
    public int ChunkY;
    public Vector2 WorldOrigin;
    public float[,] Density;
    public bool IsDirty;
    public GameObject GameObject;
    public MeshFilter MeshFilter;
    public MeshRenderer MeshRenderer;
    public MeshCollider MeshCollider;
    public PolygonCollider2D PolygonCollider2D;
    public Mesh GeneratedMesh;

    public TerrainChunk(int cx, int cy, Vector2 origin, int resolution)
    {
        ChunkX = cx;
        ChunkY = cy;
        WorldOrigin = origin;
        Density = new float[resolution + 1, resolution + 1];
        IsDirty = true;
    }
}

/// <summary>
/// Real-time destructible 2D/2.5D scalar-field terrain using the Marching Squares isoline algorithm
/// with linear interpolation, cave carving, explosion craters, and zero-GC chunk regeneration.
/// Supports both 2D (PolygonCollider2D) and 3D (Mesh / MeshCollider).
/// </summary>
[DisallowMultipleComponent]
public class MarchingSquaresDestructibleTerrain : MonoBehaviour, ISaveableSystem
{
    [Header("Mode & Resolution")]
    [SerializeField] private bool defaultTo2D = false;
    [SerializeField] private int chunkResolution = 16;       // Cells per chunk edge
    [SerializeField] private float cellSize = 0.5f;          // Size of each grid cell in world units
    [SerializeField] private int chunksX = 4;                // Number of chunks on X axis
    [SerializeField] private int chunksY = 4;                // Number of chunks on Y axis
    [SerializeField] private float isoLevel = 0.5f;          // Density threshold defining solid matter
    [SerializeField] private float extrusionDepth = 1.0f;    // 3D extrusion depth for 2.5D meshes

    [Header("Rendering & Physics")]
    [SerializeField] private Material terrainMaterial;
    [SerializeField] private bool enableCollision = true;
    [SerializeField] private bool autoListenBallistics = true;

    [Header("Gizmo Telemetry")]
    [SerializeField] private bool drawGizmos = true;

    // Chunk grid storage
    private TerrainChunk[,] chunks;
    private readonly List<TerrainChunk> dirtyChunksList = new List<TerrainChunk>(32);

    // Scratch buffers for zero-GC mesh and contour generation
    private readonly List<Vector3> scratchVertices = new List<Vector3>(1024);
    private readonly List<int> scratchTriangles = new List<int>(2048);
    private readonly List<Vector2> scratchUVs = new List<Vector2>(1024);
    private readonly List<Vector3> scratchNormals = new List<Vector3>(1024);

    // Scratch buffers for 2D collider contour paths
    private readonly List<Vector2> scratchContourPoints = new List<Vector2>(512);
    private readonly List<List<Vector2>> scratchPaths = new List<List<Vector2>>(16);

    public string SaveKey => "MarchingSquaresDestructibleTerrain_" + gameObject.name;
    public bool DefaultTo2D => defaultTo2D;
    public float IsoLevel => isoLevel;

    private void Awake()
    {
        InitializeChunks();

        if (terrainMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            terrainMaterial = new Material(shader);
        }

        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.RegisterSaveable(this);
        }

        if (autoListenBallistics)
        {
            EventBus<ArmorPenetrationEvent>.Register(OnArmorPenetration);
        }
    }

    private void OnDestroy()
    {
        if (SaveLoadSerializer.Instance != null)
        {
            SaveLoadSerializer.Instance.UnregisterSaveable(this);
        }

        if (autoListenBallistics)
        {
            EventBus<ArmorPenetrationEvent>.Unregister(OnArmorPenetration);
        }
    }

    private void Start()
    {
        GenerateInitialTerrain();
        RebuildAllDirtyChunks();
    }

    private void LateUpdate()
    {
        if (dirtyChunksList.Count > 0)
        {
            RebuildDirtyChunks();
        }
    }

    // =========================================================================
    // INITIALIZATION & PROCEDURAL GENERATION
    // =========================================================================

    private void InitializeChunks()
    {
        chunks = new TerrainChunk[chunksX, chunksY];
        float chunkSize = chunkResolution * cellSize;

        for (int cx = 0; cx < chunksX; cx++)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                Vector2 origin = new Vector2(cx * chunkSize, cy * chunkSize);
                var chunk = new TerrainChunk(cx, cy, origin, chunkResolution);

                GameObject go = new GameObject($"Chunk_{cx}_{cy}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(origin.x, origin.y, 0f);

                chunk.GameObject = go;
                chunk.MeshFilter = go.AddComponent<MeshFilter>();
                chunk.MeshRenderer = go.AddComponent<MeshRenderer>();
                chunk.MeshRenderer.sharedMaterial = terrainMaterial;

                if (enableCollision)
                {
                    if (defaultTo2D)
                    {
                        chunk.PolygonCollider2D = go.AddComponent<PolygonCollider2D>();
                    }
                    else
                    {
                        chunk.MeshCollider = go.AddComponent<MeshCollider>();
                    }
                }

                chunk.GeneratedMesh = new Mesh { name = $"ChunkMesh_{cx}_{cy}" };
                chunk.MeshFilter.sharedMesh = chunk.GeneratedMesh;

                chunks[cx, cy] = chunk;
            }
        }
    }

    private void GenerateInitialTerrain()
    {
        // Procedural baseline generation: solid ground below half-height with Perlin hills
        float totalHeight = chunksY * chunkResolution * cellSize;

        for (int cx = 0; cx < chunksX; cx++)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                TerrainChunk chunk = chunks[cx, cy];
                for (int x = 0; x <= chunkResolution; x++)
                {
                    for (int y = 0; y <= chunkResolution; y++)
                    {
                        float worldX = chunk.WorldOrigin.x + (x * cellSize);
                        float worldY = chunk.WorldOrigin.y + (y * cellSize);

                        float surfaceHeight = (totalHeight * 0.45f) + (Mathf.PerlinNoise(worldX * 0.08f, 0f) * 6f);
                        float density = (surfaceHeight - worldY) / 3f;

                        // Add cave holes via 2D Perlin noise
                        float caveNoise = Mathf.PerlinNoise(worldX * 0.12f, worldY * 0.12f);
                        if (caveNoise > 0.65f)
                        {
                            density -= (caveNoise - 0.65f) * 3f;
                        }

                        chunk.Density[x, y] = Mathf.Clamp01(density + 0.5f);
                    }
                }
                MarkChunkDirty(chunk);
            }
        }
    }

    // =========================================================================
    // DEFORMATION & CARVING APIS
    // =========================================================================

    /// <summary>
    /// Carves a spherical or circular crater in the terrain at world coordinates.
    /// </summary>
    public void CarveCrater(Vector3 worldPos, float radius, float carveStrength = 1.0f)
    {
        ModifyDensityInRadius(worldPos, radius, -carveStrength, isCarve: true);
    }

    /// <summary>
    /// Adds terrain mass (e.g. soil, concrete, or sediment) in the specified radius.
    /// </summary>
    public void AddMaterial(Vector3 worldPos, float radius, float addStrength = 1.0f)
    {
        ModifyDensityInRadius(worldPos, radius, addStrength, isCarve: false);
    }

    private void ModifyDensityInRadius(Vector3 worldPos, float radius, float deltaDensity, bool isCarve)
    {
        Vector2 pos2D = new Vector2(worldPos.x, worldPos.y);
        float sqrRadius = radius * radius;
        float chunkSize = chunkResolution * cellSize;

        int minCX = Mathf.Max(0, Mathf.FloorToInt((pos2D.x - radius) / chunkSize));
        int maxCX = Mathf.Min(chunksX - 1, Mathf.FloorToInt((pos2D.x + radius) / chunkSize));
        int minCY = Mathf.Max(0, Mathf.FloorToInt((pos2D.y - radius) / chunkSize));
        int maxCY = Mathf.Min(chunksY - 1, Mathf.FloorToInt((pos2D.y + radius) / chunkSize));

        bool modifiedAny = false;

        for (int cx = minCX; cx <= maxCX; cx++)
        {
            for (int cy = minCY; cy <= maxCY; cy++)
            {
                TerrainChunk chunk = chunks[cx, cy];
                bool chunkModified = false;

                for (int x = 0; x <= chunkResolution; x++)
                {
                    for (int y = 0; y <= chunkResolution; y++)
                    {
                        float sampleX = chunk.WorldOrigin.x + (x * cellSize);
                        float sampleY = chunk.WorldOrigin.y + (y * cellSize);
                        float sqrDist = (new Vector2(sampleX, sampleY) - pos2D).sqrMagnitude;

                        if (sqrDist <= sqrRadius)
                        {
                            float dist = Mathf.Sqrt(sqrDist);
                            float falloff = 1f - (dist / radius);
                            chunk.Density[x, y] = Mathf.Clamp01(chunk.Density[x, y] + (deltaDensity * falloff));
                            chunkModified = true;
                            modifiedAny = true;
                        }
                    }
                }

                if (chunkModified)
                {
                    MarkChunkDirty(chunk);
                }
            }
        }

        if (modifiedAny)
        {
            EventBus<TerrainDeformedEvent>.Raise(new TerrainDeformedEvent(worldPos, radius, isCarve));
        }
    }

    private void OnArmorPenetration(in ArmorPenetrationEvent evt)
    {
        // Explosive impact carve
        float craterRadius = Mathf.Max(1.0f, evt.CaliberMm * 0.02f);
        CarveCrater(evt.ImpactPoint, craterRadius, 1.0f);
    }

    private void MarkChunkDirty(TerrainChunk chunk)
    {
        if (!chunk.IsDirty)
        {
            chunk.IsDirty = true;
            dirtyChunksList.Add(chunk);
        }
    }

    // =========================================================================
    // MARCHING SQUARES ISOLINE & REBUILD PIPELINE (ZERO-GC)
    // =========================================================================

    public void RebuildAllDirtyChunks()
    {
        for (int cx = 0; cx < chunksX; cx++)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                MarkChunkDirty(chunks[cx, cy]);
            }
        }
        RebuildDirtyChunks();
    }

    private void RebuildDirtyChunks()
    {
        for (int i = 0; i < dirtyChunksList.Count; i++)
        {
            RebuildChunk(dirtyChunksList[i]);
            dirtyChunksList[i].IsDirty = false;
        }
        dirtyChunksList.Clear();
    }

    private void RebuildChunk(TerrainChunk chunk)
    {
        scratchVertices.Clear();
        scratchTriangles.Clear();
        scratchUVs.Clear();
        scratchNormals.Clear();

        if (defaultTo2D && enableCollision && chunk.PolygonCollider2D != null)
        {
            scratchPaths.Clear();
        }

        for (int x = 0; x < chunkResolution; x++)
        {
            for (int y = 0; y < chunkResolution; y++)
            {
                ProcessCell(chunk, x, y);
            }
        }

        // Apply generated geometry to chunk Mesh
        chunk.GeneratedMesh.Clear();
        if (scratchVertices.Count > 0)
        {
            chunk.GeneratedMesh.SetVertices(scratchVertices);
            chunk.GeneratedMesh.SetTriangles(scratchTriangles, 0);
            chunk.GeneratedMesh.SetUVs(0, scratchUVs);
            chunk.GeneratedMesh.RecalculateNormals();
            chunk.GeneratedMesh.RecalculateBounds();

            if (enableCollision)
            {
                if (!defaultTo2D && chunk.MeshCollider != null)
                {
                    chunk.MeshCollider.sharedMesh = null;
                    chunk.MeshCollider.sharedMesh = chunk.GeneratedMesh;
                }
                else if (defaultTo2D && chunk.PolygonCollider2D != null)
                {
                    Apply2DColliderPaths(chunk);
                }
            }
        }
        else
        {
            if (chunk.MeshCollider != null) chunk.MeshCollider.sharedMesh = null;
            if (chunk.PolygonCollider2D != null) chunk.PolygonCollider2D.pathCount = 0;
        }
    }

    private void ProcessCell(TerrainChunk chunk, int x, int y)
    {
        float v0 = chunk.Density[x, y];         // Bottom-Left
        float v1 = chunk.Density[x + 1, y];     // Bottom-Right
        float v2 = chunk.Density[x + 1, y + 1]; // Top-Right
        float v3 = chunk.Density[x, y + 1];     // Top-Left

        int config = 0;
        if (v0 >= isoLevel) config |= 1;
        if (v1 >= isoLevel) config |= 2;
        if (v2 >= isoLevel) config |= 4;
        if (v3 >= isoLevel) config |= 8;

        if (config == 0) return; // Completely empty

        Vector3 p0 = new Vector3(x * cellSize, y * cellSize, 0f);
        Vector3 p1 = new Vector3((x + 1) * cellSize, y * cellSize, 0f);
        Vector3 p2 = new Vector3((x + 1) * cellSize, (y + 1) * cellSize, 0f);
        Vector3 p3 = new Vector3(x * cellSize, (y + 1) * cellSize, 0f);

        // Linear interpolation along edges
        Vector3 midBottom = Interpolate(p0, p1, v0, v1);
        Vector3 midRight = Interpolate(p1, p2, v1, v2);
        Vector3 midTop = Interpolate(p3, p2, v3, v2);
        Vector3 midLeft = Interpolate(p0, p3, v0, v3);

        switch (config)
        {
            case 1: AddTriangle(p0, midBottom, midLeft); break;
            case 2: AddTriangle(p1, midRight, midBottom); break;
            case 3: AddQuad(p0, p1, midRight, midLeft); break;
            case 4: AddTriangle(p2, midTop, midRight); break;
            case 5: AddTriangle(p0, midBottom, midLeft); AddTriangle(p2, midTop, midRight); break;
            case 6: AddQuad(p1, p2, midTop, midBottom); break;
            case 7: AddPentagon(p0, p1, p2, midTop, midLeft); break;
            case 8: AddTriangle(p3, midLeft, midTop); break;
            case 9: AddQuad(p0, midBottom, midTop, p3); break;
            case 10: AddTriangle(p3, midLeft, midTop); AddTriangle(p1, midRight, midBottom); break;
            case 11: AddPentagon(p0, midBottom, midRight, p2, p3); break;
            case 12: AddQuad(midLeft, midRight, p2, p3); break;
            case 13: AddPentagon(p0, p1, midRight, midTop, p3); break;
            case 14: AddPentagon(midBottom, p1, p2, p3, midLeft); break;
            case 15: AddQuad(p0, p1, p2, p3); break;
        }

        // Extrude back-faces if 3D extrusion is active
        if (!defaultTo2D && extrusionDepth > 0.001f && config == 15)
        {
            Vector3 backOffset = new Vector3(0f, 0f, extrusionDepth);
            AddQuad(p3 + backOffset, p2 + backOffset, p1 + backOffset, p0 + backOffset);
        }
    }

    private Vector3 Interpolate(Vector3 pA, Vector3 pB, float vA, float vB)
    {
        float diff = vB - vA;
        float t = Mathf.Abs(diff) > 0.0001f ? Mathf.Clamp01((isoLevel - vA) / diff) : 0.5f;
        return Vector3.Lerp(pA, pB, t);
    }

    private void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        int idx = scratchVertices.Count;
        scratchVertices.Add(a);
        scratchVertices.Add(b);
        scratchVertices.Add(c);

        scratchUVs.Add(new Vector2(a.x, a.y));
        scratchUVs.Add(new Vector2(b.x, b.y));
        scratchUVs.Add(new Vector2(c.x, c.y));

        scratchTriangles.Add(idx);
        scratchTriangles.Add(idx + 1);
        scratchTriangles.Add(idx + 2);
    }

    private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    private void AddPentagon(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
        AddTriangle(a, d, e);
    }

    private void Apply2DColliderPaths(TerrainChunk chunk)
    {
        // Simple bounding contour generation for 2D collider
        if (scratchVertices.Count < 3)
        {
            chunk.PolygonCollider2D.pathCount = 0;
            return;
        }

        // Extract simplified outer vertices
        scratchContourPoints.Clear();
        int step = Mathf.Max(1, scratchVertices.Count / 32);
        for (int i = 0; i < scratchVertices.Count; i += step)
        {
            scratchContourPoints.Add(new Vector2(scratchVertices[i].x, scratchVertices[i].y));
        }

        chunk.PolygonCollider2D.pathCount = 1;
        chunk.PolygonCollider2D.SetPath(0, scratchContourPoints.ToArray());
    }

    // =========================================================================
    // SAVE & RESTORE (ISaveableSystem)
    // =========================================================================

    [System.Serializable]
    private class SaveDataContainer
    {
        public int ChunksX;
        public int ChunksY;
        public int Resolution;
        public float[] FlattenedDensities;
    }

    public object CaptureState() => SaveData();
    public void RestoreState(string stateJson) => LoadData(stateJson);

    public string SaveData()
    {
        int resPlus1 = chunkResolution + 1;
        int totalFloats = chunksX * chunksY * resPlus1 * resPlus1;
        float[] flat = new float[totalFloats];
        int idx = 0;

        for (int cx = 0; cx < chunksX; cx++)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                var d = chunks[cx, cy].Density;
                for (int x = 0; x < resPlus1; x++)
                {
                    for (int y = 0; y < resPlus1; y++)
                    {
                        flat[idx++] = d[x, y];
                    }
                }
            }
        }

        var container = new SaveDataContainer
        {
            ChunksX = chunksX,
            ChunksY = chunksY,
            Resolution = chunkResolution,
            FlattenedDensities = flat
        };

        return JsonUtility.ToJson(container);
    }

    public void LoadData(string data)
    {
        if (string.IsNullOrEmpty(data)) return;

        var container = JsonUtility.FromJson<SaveDataContainer>(data);
        if (container == null || container.FlattenedDensities == null) return;

        int resPlus1 = chunkResolution + 1;
        int idx = 0;

        for (int cx = 0; cx < chunksX; cx++)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                TerrainChunk chunk = chunks[cx, cy];
                for (int x = 0; x < resPlus1; x++)
                {
                    for (int y = 0; y < resPlus1; y++)
                    {
                        if (idx < container.FlattenedDensities.Length)
                        {
                            chunk.Density[x, y] = container.FlattenedDensities[idx++];
                        }
                    }
                }
                MarkChunkDirty(chunk);
            }
        }

        RebuildDirtyChunks();
    }

    // =========================================================================
    // GIZMOS AND VISUALIZATION
    // =========================================================================

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        float chunkSize = chunkResolution * cellSize;
        Gizmos.color = new Color(0.2f, 0.9f, 0.3f, 0.4f);

        for (int cx = 0; cx < chunksX; cx++)
        {
            for (int cy = 0; cy < chunksY; cy++)
            {
                Vector3 center = transform.position + new Vector3((cx + 0.5f) * chunkSize, (cy + 0.5f) * chunkSize, 0f);
                Vector3 size = new Vector3(chunkSize, chunkSize, defaultTo2D ? 0.1f : extrusionDepth);
                Gizmos.DrawWireCube(center, size);
            }
        }
    }
}
