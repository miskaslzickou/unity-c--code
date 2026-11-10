using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameSystems.Spatial
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A POMOCNÉ STRUKTURY
    // =========================================================================

    /// <summary>
    /// Režim dimenzionality mřížky:
    /// XZ = Standardní RTS, strategie a city-buildery (světová zem).
    /// XY = 2D plošinovky a top-down hry.
    /// XYZ = Plný 3D prostor (letecké/vesmírné simulátory).
    /// </summary>
    public enum SpatialDimensionMode
    {
        PlanarXZ, // Výchozí pro RTS (výška Y je ignorována pro indexaci buněk)
        PlanarXY, // Pro 2D hry (osa Z je ignorována)
        Full3D    // Plný 3D prostor
    }

    /// <summary>
    /// Interní záznam polohy a dosahu registrované entity pro zero-GC aktualizace.
    /// </summary>
    public struct SpatialEntityRecord
    {
        public Vector3 Position;
        public float Radius;
        public int MinCellX;
        public int MaxCellX;
        public int MinCellY;
        public int MaxCellY;
        public int MinCellZ;
        public int MaxCellZ;
    }

    // =========================================================================
    // 2. GENERICKÁ TŘÍDA SPATIAL HASH GRID (ZERO-GC SPATIAL INDEXER)
    // =========================================================================

    /// <summary>
    /// Vysoce výkonná generická prostorová indexační mřížka Spatial Hash Grid.
    /// Zcela eliminuje drahé Physics.OverlapSphere a Physics2D.OverlapCircle alokace.
    /// Využívá předem alokovaný pool seznamů (zero GC v hot-path) a hashování do 64-bitového long klíče.
    /// </summary>
    public class SpatialHashGrid<T> where T : class
    {
        private readonly float cellSize;
        private readonly float inverseCellSize;
        private readonly SpatialDimensionMode dimensionMode;

        // Mapování prostorového klíče na seznam entit v dané buňce
        private readonly Dictionary<long, List<T>> buckets;

        // Sledování entit a jejich aktuálně obsazených buněk pro rychlý UpdatePosition
        private readonly Dictionary<T, SpatialEntityRecord> entityRecords;

        // Objektový pool pro List<T>, aby se při vzniku/zániku buněk nealokovala paměť na haldě (heap)
        private readonly Stack<List<T>> listPool;

        // Interní cache pro zamezení duplicit při dotazech přes více buněk
        private readonly HashSet<T> deduplicationSet;

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (EVENTS)
        // =========================================================================

        public event Action<T, Vector3> OnEntityRegistered;
        public event Action<T> OnEntityUnregistered;
        public event Action<T, Vector3, Vector3> OnEntityMoved; // entity, oldPos, newPos

        public int Count => entityRecords.Count;
        public float CellSize => cellSize;
        public SpatialDimensionMode Mode => dimensionMode;

        public SpatialHashGrid(float cellSize = 5f, int initialCapacity = 512, SpatialDimensionMode mode = SpatialDimensionMode.PlanarXZ)
        {
            this.cellSize = Mathf.Max(0.1f, cellSize);
            this.inverseCellSize = 1f / this.cellSize;
            this.dimensionMode = mode;

            buckets = new Dictionary<long, List<T>>(initialCapacity);
            entityRecords = new Dictionary<T, SpatialEntityRecord>(initialCapacity);
            listPool = new Stack<List<T>>(128);
            deduplicationSet = new HashSet<T>();
        }

        // =========================================================================
        // VEŘEJNÉ API: REGISTRACE, POHYB A ODSTRANĚNÍ ENTIT
        // =========================================================================

        /// <summary>
        /// Zaregistruje novou entitu s danou počáteční pozicí a volitelným poloměrem.
        /// </summary>
        public bool Register(T entity, Vector3 position, float radius = 0f)
        {
            if (entity == null || entityRecords.ContainsKey(entity)) return false;

            radius = Mathf.Max(0f, radius);
            SpatialEntityRecord record = ComputeRecord(position, radius);
            entityRecords.Add(entity, record);

            InsertIntoCells(entity, record);
            OnEntityRegistered?.Invoke(entity, position);
            return true;
        }

        /// <summary>
        /// Aktualizuje pozici již registrované entity.
        /// Pokud entita zůstává ve stejných buňkách, nedochází k žádným změnám v seznamech.
        /// </summary>
        public bool UpdatePosition(T entity, Vector3 newPosition)
        {
            if (entity == null || !entityRecords.TryGetValue(entity, out SpatialEntityRecord oldRecord))
            {
                return false;
            }

            // Rychlá kontrola: Pokud je posun zanedbatelný a buňky se nezměnily
            SpatialEntityRecord newRecord = ComputeRecord(newPosition, oldRecord.Radius);
            bool cellsChanged = (oldRecord.MinCellX != newRecord.MinCellX || oldRecord.MaxCellX != newRecord.MaxCellX ||
                                 oldRecord.MinCellY != newRecord.MinCellY || oldRecord.MaxCellY != newRecord.MaxCellY ||
                                 oldRecord.MinCellZ != newRecord.MinCellZ || oldRecord.MaxCellZ != newRecord.MaxCellZ);

            Vector3 oldPos = oldRecord.Position;

            if (cellsChanged)
            {
                RemoveFromCells(entity, oldRecord);
                InsertIntoCells(entity, newRecord);
            }

            entityRecords[entity] = newRecord;
            OnEntityMoved?.Invoke(entity, oldPos, newPosition);
            return true;
        }

        /// <summary>
        /// Odstraní entitu z indexační mřížky.
        /// </summary>
        public bool Unregister(T entity)
        {
            if (entity == null || !entityRecords.TryGetValue(entity, out SpatialEntityRecord record))
            {
                return false;
            }

            RemoveFromCells(entity, record);
            entityRecords.Remove(entity);
            OnEntityUnregistered?.Invoke(entity);
            return true;
        }

        /// <summary>
        /// Vyčistí celou mřížku a vrátí všechny listy do objektového poolu (zero-GC).
        /// </summary>
        public void Clear()
        {
            foreach (var kvp in buckets)
            {
                List<T> list = kvp.Value;
                list.Clear();
                listPool.Push(list);
            }

            buckets.Clear();
            entityRecords.Clear();
            deduplicationSet.Clear();
        }

        // =========================================================================
        // ZERO-GC DOTAZOVACÍ METODY (QUERIES)
        // =========================================================================

        /// <summary>
        /// Nalezne všechny entity v zadaném poloměru (radius) od středu.
        /// Výsledky zapíše do předem alokovaného Listu resultsBuffer (vrací celkový počet nalezených).
        /// </summary>
        public int QueryRadius(Vector3 center, float radius, List<T> resultsBuffer, Predicate<T> filter = null)
        {
            if (resultsBuffer == null) throw new ArgumentNullException(nameof(resultsBuffer));
            resultsBuffer.Clear();
            deduplicationSet.Clear();

            float radiusSqr = radius * radius;
            GetCellBounds(center, radius, out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        long key = ComputeHashKey(x, y, z);
                        if (buckets.TryGetValue(key, out List<T> cellList))
                        {
                            int count = cellList.Count;
                            for (int i = 0; i < count; i++)
                            {
                                T candidate = cellList[i];
                                if (deduplicationSet.Add(candidate))
                                {
                                    if (filter != null && !filter(candidate)) continue;

                                    SpatialEntityRecord rec = entityRecords[candidate];
                                    float combinedRadius = radius + rec.Radius;
                                    float distSqr = GetDistanceSqr(center, rec.Position);

                                    if (distSqr <= combinedRadius * combinedRadius)
                                    {
                                        resultsBuffer.Add(candidate);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return resultsBuffer.Count;
        }

        /// <summary>
        /// Nalezne všechny entity v zadaném osově zarovnaném kvádru (AABB bounds).
        /// Výsledky zapíše do předem alokovaného Listu resultsBuffer.
        /// </summary>
        public int QueryAABB(Vector3 min, Vector3 max, List<T> resultsBuffer, Predicate<T> filter = null)
        {
            if (resultsBuffer == null) throw new ArgumentNullException(nameof(resultsBuffer));
            resultsBuffer.Clear();
            deduplicationSet.Clear();

            int minX = Mathf.FloorToInt(min.x * inverseCellSize);
            int maxX = Mathf.FloorToInt(max.x * inverseCellSize);
            int minY = dimensionMode == SpatialDimensionMode.Full3D ? Mathf.FloorToInt(min.y * inverseCellSize) : 0;
            int maxY = dimensionMode == SpatialDimensionMode.Full3D ? Mathf.FloorToInt(max.y * inverseCellSize) : 0;
            int minZ = dimensionMode != SpatialDimensionMode.PlanarXY ? Mathf.FloorToInt(min.z * inverseCellSize) : 0;
            int maxZ = dimensionMode != SpatialDimensionMode.PlanarXY ? Mathf.FloorToInt(max.z * inverseCellSize) : 0;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        long key = ComputeHashKey(x, y, z);
                        if (buckets.TryGetValue(key, out List<T> cellList))
                        {
                            int count = cellList.Count;
                            for (int i = 0; i < count; i++)
                            {
                                T candidate = cellList[i];
                                if (deduplicationSet.Add(candidate))
                                {
                                    if (filter != null && !filter(candidate)) continue;

                                    SpatialEntityRecord rec = entityRecords[candidate];
                                    if (IsInsideAABB(rec.Position, rec.Radius, min, max))
                                    {
                                        resultsBuffer.Add(candidate);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return resultsBuffer.Count;
        }

        /// <summary>
        /// Nalezne nejbližší entitu od středu v maximálním zadaném dosahu.
        /// </summary>
        public bool FindNearest(Vector3 center, float maxRadius, out T nearest, out float nearestDistanceSqr, Predicate<T> filter = null)
        {
            nearest = null;
            nearestDistanceSqr = maxRadius * maxRadius;
            bool found = false;

            deduplicationSet.Clear();
            GetCellBounds(center, maxRadius, out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        long key = ComputeHashKey(x, y, z);
                        if (buckets.TryGetValue(key, out List<T> cellList))
                        {
                            int count = cellList.Count;
                            for (int i = 0; i < count; i++)
                            {
                                T candidate = cellList[i];
                                if (deduplicationSet.Add(candidate))
                                {
                                    if (filter != null && !filter(candidate)) continue;

                                    SpatialEntityRecord rec = entityRecords[candidate];
                                    float distSqr = GetDistanceSqr(center, rec.Position);

                                    if (distSqr <= nearestDistanceSqr)
                                    {
                                        nearestDistanceSqr = distSqr;
                                        nearest = candidate;
                                        found = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return found;
        }

        // =========================================================================
        // VNITŘNÍ VÝPOČETNÍ METODY A SPRÁVA BUNĚK
        // =========================================================================

        private SpatialEntityRecord ComputeRecord(Vector3 pos, float radius)
        {
            GetCellBounds(pos, radius, out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ);

            return new SpatialEntityRecord
            {
                Position = pos,
                Radius = radius,
                MinCellX = minX,
                MaxCellX = maxX,
                MinCellY = minY,
                MaxCellY = maxY,
                MinCellZ = minZ,
                MaxCellZ = maxZ
            };
        }

        private void GetCellBounds(Vector3 pos, float radius, out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ)
        {
            minX = Mathf.FloorToInt((pos.x - radius) * inverseCellSize);
            maxX = Mathf.FloorToInt((pos.x + radius) * inverseCellSize);

            if (dimensionMode == SpatialDimensionMode.Full3D)
            {
                minY = Mathf.FloorToInt((pos.y - radius) * inverseCellSize);
                maxY = Mathf.FloorToInt((pos.y + radius) * inverseCellSize);
            }
            else if (dimensionMode == SpatialDimensionMode.PlanarXY)
            {
                minY = Mathf.FloorToInt((pos.y - radius) * inverseCellSize);
                maxY = Mathf.FloorToInt((pos.y + radius) * inverseCellSize);
            }
            else
            {
                minY = 0;
                maxY = 0;
            }

            if (dimensionMode != SpatialDimensionMode.PlanarXY)
            {
                minZ = Mathf.FloorToInt((pos.z - radius) * inverseCellSize);
                maxZ = Mathf.FloorToInt((pos.z + radius) * inverseCellSize);
            }
            else
            {
                minZ = 0;
                maxZ = 0;
            }
        }

        private void InsertIntoCells(T entity, SpatialEntityRecord record)
        {
            for (int x = record.MinCellX; x <= record.MaxCellX; x++)
            {
                for (int y = record.MinCellY; y <= record.MaxCellY; y++)
                {
                    for (int z = record.MinCellZ; z <= record.MaxCellZ; z++)
                    {
                        long key = ComputeHashKey(x, y, z);
                        if (!buckets.TryGetValue(key, out List<T> list))
                        {
                            list = listPool.Count > 0 ? listPool.Pop() : new List<T>(16);
                            buckets[key] = list;
                        }
                        list.Add(entity);
                    }
                }
            }
        }

        private void RemoveFromCells(T entity, SpatialEntityRecord record)
        {
            for (int x = record.MinCellX; x <= record.MaxCellX; x++)
            {
                for (int y = record.MinCellY; y <= record.MaxCellY; y++)
                {
                    for (int z = record.MinCellZ; z <= record.MaxCellZ; z++)
                    {
                        long key = ComputeHashKey(x, y, z);
                        if (buckets.TryGetValue(key, out List<T> list))
                        {
                            list.Remove(entity);
                            if (list.Count == 0)
                            {
                                buckets.Remove(key);
                                listPool.Push(list);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Vysoce efektivní 64-bitové bit-packing hashování prostorových indexů bez kolizí v běžném prostoru.
        /// </summary>
        public long ComputeHashKey(int cx, int cy, int cz)
        {
            if (dimensionMode == SpatialDimensionMode.PlanarXZ)
            {
                return ((long)cx << 32) ^ (uint)cz;
            }
            if (dimensionMode == SpatialDimensionMode.PlanarXY)
            {
                return ((long)cx << 32) ^ (uint)cy;
            }

            // Plný 3D mód: 21 bitů na osu (rozsah +- 1 000 000 buněk)
            return (((long)(cx & 0x1FFFFF)) << 42) |
                   (((long)(cy & 0x1FFFFF)) << 21) |
                   ((long)(cz & 0x1FFFFF));
        }

        private float GetDistanceSqr(Vector3 a, Vector3 b)
        {
            if (dimensionMode == SpatialDimensionMode.PlanarXZ)
            {
                float dx = a.x - b.x;
                float dz = a.z - b.z;
                return dx * dx + dz * dz;
            }
            if (dimensionMode == SpatialDimensionMode.PlanarXY)
            {
                float dx = a.x - b.x;
                float dy = a.y - b.y;
                return dx * dx + dy * dy;
            }

            return (a - b).sqrMagnitude;
        }

        private bool IsInsideAABB(Vector3 pos, float radius, Vector3 min, Vector3 max)
        {
            if (dimensionMode == SpatialDimensionMode.PlanarXZ)
            {
                return (pos.x + radius >= min.x && pos.x - radius <= max.x &&
                        pos.z + radius >= min.z && pos.z - radius <= max.z);
            }
            if (dimensionMode == SpatialDimensionMode.PlanarXY)
            {
                return (pos.x + radius >= min.x && pos.x - radius <= max.x &&
                        pos.y + radius >= min.y && pos.y - radius <= max.y);
            }

            return (pos.x + radius >= min.x && pos.x - radius <= max.x &&
                    pos.y + radius >= min.y && pos.y - radius <= max.y &&
                    pos.z + radius >= min.z && pos.z - radius <= max.z);
        }
    }

    // =========================================================================
    // 3. MONOBEHAVIOUR MANAŽER PRO SCÉNU A INSPECTOR
    // =========================================================================

    /// <summary>
    /// Globální scéna komponenta pro transformy a obecné objekty (např. jednotky, střely, překážky).
    /// Umožňuje přímé zapojení a vizualizaci mřížky v Scene View.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpatialHashGridManager : MonoBehaviour
    {
        public static SpatialHashGridManager Instance { get; private set; }

        [Header("Konfigurace mřížky")]
        [SerializeField, Range(1f, 50f)] private float cellSize = 5f;
        [SerializeField] private SpatialDimensionMode dimensionMode = SpatialDimensionMode.PlanarXZ;
        [SerializeField] private int initialCapacity = 1024;

        [Header("Gizmos ladění")]
        [SerializeField] private bool drawGridGizmos = false;
        [SerializeField] private int gizmoRadiusInCells = 5;

        private SpatialHashGrid<Transform> transformGrid;

        public SpatialHashGrid<Transform> TransformGrid => transformGrid;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            transformGrid = new SpatialHashGrid<Transform>(cellSize, initialCapacity, dimensionMode);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGridGizmos) return;

            Gizmos.color = new Color(0f, 0.8f, 1f, 0.25f);
            Vector3 center = transform.position;
            int cxCenter = Mathf.FloorToInt(center.x / cellSize);
            int czCenter = Mathf.FloorToInt(center.z / cellSize);

            for (int x = cxCenter - gizmoRadiusInCells; x <= cxCenter + gizmoRadiusInCells; x++)
            {
                for (int z = czCenter - gizmoRadiusInCells; z <= czCenter + gizmoRadiusInCells; z++)
                {
                    Vector3 cellPos = new Vector3((x + 0.5f) * cellSize, center.y, (z + 0.5f) * cellSize);
                    Gizmos.DrawWireCube(cellPos, new Vector3(cellSize, 0.1f, cellSize));
                }
            }
        }
    }
}
