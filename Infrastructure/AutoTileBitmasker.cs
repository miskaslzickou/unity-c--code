using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameSystems.Tiles
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A SCRIPTABLE OBJECT PRO TILESETY
    // =========================================================================

    public enum BitmaskMode
    {
        Cardinal4Bit, // 16 variant (Sever, Východ, Jih, Západ) – trubky, cesty, ploty
        Blob8Bit      // 47 kanonických variant (včetně rohů) – terén, stěny, voda
    }

    /// <summary>
    /// Konfigurace sady dlaždic nebo 3D prefabů pro automatické bitmaskování.
    /// </summary>
    [CreateAssetMenu(fileName = "NewAutoTileSet", menuName = "Tiles/Auto Tile Set")]
    public class AutoTileSetSO : ScriptableObject
    {
        [Header("Základní identifikace")]
        [SerializeField] private string tileSetName = "StoneWall";
        [SerializeField] private int categoryId = 1;
        [SerializeField] private BitmaskMode mode = BitmaskMode.Blob8Bit;

        [Header("16 variant pro 4-Bit Cardinal (0..15)")]
        [Tooltip("0 = Izolovaný, 1 = S, 2 = V, 3 = S+V, 4 = J, 5 = S+J, atd.")]
        [SerializeField] private GameObject[] cardinal16Prefabs = new GameObject[16];

        [Header("47 kanonických variant pro 8-Bit Blob (0..46)")]
        [SerializeField] private GameObject[] blob47Prefabs = new GameObject[47];

        public string TileSetName => tileSetName;
        public int CategoryId => categoryId;
        public BitmaskMode Mode => mode;
        public GameObject[] Cardinal16Prefabs => cardinal16Prefabs;
        public GameObject[] Blob47Prefabs => blob47Prefabs;

        public GameObject GetPrefabForIndex(int index)
        {
            if (mode == BitmaskMode.Cardinal4Bit)
            {
                if (cardinal16Prefabs != null && index >= 0 && index < cardinal16Prefabs.Length)
                {
                    return cardinal16Prefabs[index];
                }
            }
            else
            {
                if (blob47Prefabs != null && index >= 0 && index < blob47Prefabs.Length)
                {
                    return blob47Prefabs[index];
                }
            }
            return null;
        }
    }

    // =========================================================================
    // 2. HLAVNÍ BITMASKER (AUTO TILE BITMASKER)
    // =========================================================================

    /// <summary>
    /// Automatický kalkulátor 4-bitových (16 variant) a 8-bitových (47 kanonických z 256)
    /// masek sousedství pro dynamické spojování zdí, plotů, potrubí a cest.
    /// Zajišťuje kaskádovou aktualizaci striktně na 9 buňkách bez zbytečné rekurze a zero GC.
    /// </summary>
    [DisallowMultipleComponent]
    public class AutoTileBitmasker : MonoBehaviour
    {
        public static AutoTileBitmasker Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při změně bitmasky buňky (gridPos, rawMask, canonicalIndex).</summary>
        public static event Action<Vector2Int, int, int> OnTileVisualUpdated;

        /// <summary>Vyvoláno při umístění nebo smazání typu dlaždice.</summary>
        public static event Action<Vector2Int, int> OnTileCategoryChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Rozměry mřížky")]
        [SerializeField, Range(16, 512)] private int gridWidth = 100;
        [SerializeField, Range(16, 512)] private int gridHeight = 100;
        [SerializeField, Min(0.1f)] private float cellSize = 1f;
        [SerializeField] private Vector3 gridOrigin = Vector3.zero;

        [Header("Výchozí TileSet")]
        [SerializeField] private AutoTileSetSO defaultTileSet = null;

        [Header("Ladění")]
        [SerializeField] private bool drawGizmos = false;

        // =========================================================================
        // VNITŘNÍ PAMĚŤ A LOOKUP TABULKY (ZERO-GC)
        // =========================================================================

        // Plochá mřížka kategorií dlaždic (0 = prázdno, 1+ = konkrétní materiál/zeď)
        private int[] tileGrid;

        // Plochá mřížka instancí objektů ve scéně
        private GameObject[] spawnedInstances;

        // Globální převodní tabulka: 256 surových 8-bit masek -> 47 kanonických indexů
        private static readonly byte[] Raw256ToCanonical47 = new byte[256];
        private static bool isLookupTableInitialized = false;

        // 8 sousedních směrů pro kaskádovou aktualizaci
        private static readonly Vector2Int[] NeighborOffsets = new Vector2Int[]
        {
            new Vector2Int( 0,  1), // Sever (North)
            new Vector2Int( 1,  0), // Východ (East)
            new Vector2Int( 0, -1), // Jih (South)
            new Vector2Int(-1,  0), // Západ (West)
            new Vector2Int(-1,  1), // Severozápad (NW)
            new Vector2Int( 1,  1), // Severovýchod (NE)
            new Vector2Int( 1, -1), // Jihovýchod (SE)
            new Vector2Int(-1, -1)  // Jihozápad (SW)
        };

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public int Width => gridWidth;
        public int Height => gridHeight;
        public float CellSize => cellSize;
        public Vector3 GridOrigin => gridOrigin;

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

            EnsureLookupTableInitialized();
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
        // INICIALIZACE MŘÍŽKY A 47-BLOB LOOKUP TABULKY
        // =========================================================================

        public void InitializeGrid()
        {
            int total = gridWidth * gridHeight;
            tileGrid = new int[total];
            spawnedInstances = new GameObject[total];
        }

        /// <summary>
        /// Vygeneruje deterministickou mapovací tabulku z 256 surových stavů na 47 kanonických dlaždic.
        /// Rohové bity jsou maskovány, pokud chybí sousedící ortogonální strany!
        /// </summary>
        private static void EnsureLookupTableInitialized()
        {
            if (isLookupTableInitialized) return;

            // Seznam 47 unikátních platných kanonických masek po aplikaci rohové redukce
            var uniqueMasks = new List<byte>(47);

            for (int raw = 0; raw < 256; raw++)
            {
                byte filtered = FilterIrrelevantCorners((byte)raw);

                int index = uniqueMasks.IndexOf(filtered);
                if (index == -1)
                {
                    index = uniqueMasks.Count;
                    uniqueMasks.Add(filtered);
                }

                Raw256ToCanonical47[raw] = (byte)index;
            }

            isLookupTableInitialized = true;
        }

        /// <summary>
        /// Ignoruje rohové diagonální bity, pokud chybí alespoň jeden ze sousedících ortogonálních sousedů.
        /// Bit 0 (NW) vyžaduje Bit 1 (N) a Bit 3 (W).
        /// Bit 2 (NE) vyžaduje Bit 1 (N) a Bit 4 (E).
        /// Bit 5 (SW) vyžaduje Bit 6 (S) a Bit 3 (W).
        /// Bit 7 (SE) vyžaduje Bit 6 (S) a Bit 4 (E).
        /// </summary>
        public static byte FilterIrrelevantCorners(byte raw)
        {
            bool n  = (raw & (1 << 1)) != 0;
            bool w  = (raw & (1 << 3)) != 0;
            bool e  = (raw & (1 << 4)) != 0;
            bool s  = (raw & (1 << 6)) != 0;

            byte result = raw;

            // NW (bit 0)
            if (!n || !w) result &= unchecked((byte)~(1 << 0));
            // NE (bit 2)
            if (!n || !e) result &= unchecked((byte)~(1 << 2));
            // SW (bit 5)
            if (!s || !w) result &= unchecked((byte)~(1 << 5));
            // SE (bit 7)
            if (!s || !e) result &= unchecked((byte)~(1 << 7));

            return result;
        }

        // =========================================================================
        // VÝPOČTY BITMASEK (4-BIT A 8-BIT)
        // =========================================================================

        /// <summary>
        /// Spočítá 4-bitovou masku (0..15): N=1, E=2, S=4, W=8 pro zadanou kategorii dlaždice.
        /// </summary>
        public int Calculate4BitMask(Vector2Int pos, int categoryId)
        {
            int mask = 0;

            if (IsMatchingCategory(pos.x, pos.y + 1, categoryId)) mask |= 1; // N
            if (IsMatchingCategory(pos.x + 1, pos.y, categoryId)) mask |= 2; // E
            if (IsMatchingCategory(pos.x, pos.y - 1, categoryId)) mask |= 4; // S
            if (IsMatchingCategory(pos.x - 1, pos.y, categoryId)) mask |= 8; // W

            return mask;
        }

        /// <summary>
        /// Spočítá plnou 8-bitovou masku (0..255) a rovnou vrátí i kanonický 0..46 index.
        /// </summary>
        public int Calculate8BitMask(Vector2Int pos, int categoryId, out int canonicalIndex)
        {
            int rawMask = 0;

            // Ortogonální
            bool n = IsMatchingCategory(pos.x, pos.y + 1, categoryId);
            bool e = IsMatchingCategory(pos.x + 1, pos.y, categoryId);
            bool s = IsMatchingCategory(pos.x, pos.y - 1, categoryId);
            bool w = IsMatchingCategory(pos.x - 1, pos.y, categoryId);

            // Diagonální
            bool nw = IsMatchingCategory(pos.x - 1, pos.y + 1, categoryId);
            bool ne = IsMatchingCategory(pos.x + 1, pos.y + 1, categoryId);
            bool sw = IsMatchingCategory(pos.x - 1, pos.y - 1, categoryId);
            bool se = IsMatchingCategory(pos.x + 1, pos.y - 1, categoryId);

            if (nw) rawMask |= (1 << 0);
            if (n)  rawMask |= (1 << 1);
            if (ne) rawMask |= (1 << 2);
            if (w)  rawMask |= (1 << 3);
            if (e)  rawMask |= (1 << 4);
            if (sw) rawMask |= (1 << 5);
            if (s)  rawMask |= (1 << 6);
            if (se) rawMask |= (1 << 7);

            canonicalIndex = Raw256ToCanonical47[rawMask];
            return rawMask;
        }

        public int GetCanonicalIndexFromRaw(byte rawMask)
        {
            EnsureLookupTableInitialized();
            return Raw256ToCanonical47[rawMask];
        }

        // =========================================================================
        // KASKÁDOVÉ AKTUALIZACE STRIKTNĚ NA 9 BUŇKÁCH (ZERO-GC)
        // =========================================================================

        /// <summary>
        /// Nastaví dlaždici na danou pozici a spustí okamžitou kaskádovou aktualizaci pro ni i 8 sousedů.
        /// </summary>
        public void SetTileAndCascadeUpdate(Vector2Int pos, int categoryId, AutoTileSetSO tileSet = null)
        {
            if (!IsValidCell(pos.x, pos.y)) return;

            int idx = pos.y * gridWidth + pos.x;
            tileGrid[idx] = categoryId;
            OnTileCategoryChanged?.Invoke(pos, categoryId);

            // Kaskádová aktualizace středu a 8 sousedů (striktně 9 buněk)
            RefreshCellVisual(pos, tileSet ?? defaultTileSet);

            for (int i = 0; i < NeighborOffsets.Length; i++)
            {
                Vector2Int nPos = pos + NeighborOffsets[i];
                if (IsValidCell(nPos.x, nPos.y))
                {
                    RefreshCellVisual(nPos, tileSet ?? defaultTileSet);
                }
            }
        }

        /// <summary>
        /// Vyčistí dlaždici na dané pozici a kaskádově aktualizuje 8 sousedů.
        /// </summary>
        public void ClearTileAndCascadeUpdate(Vector2Int pos, AutoTileSetSO tileSet = null)
        {
            SetTileAndCascadeUpdate(pos, 0, tileSet);
        }

        private void RefreshCellVisual(Vector2Int pos, AutoTileSetSO tileSet)
        {
            int idx = pos.y * gridWidth + pos.x;
            int cat = tileGrid[idx];

            if (cat <= 0)
            {
                if (spawnedInstances[idx] != null)
                {
                    Destroy(spawnedInstances[idx]);
                    spawnedInstances[idx] = null;
                }
                return;
            }

            if (tileSet == null) return;

            int visualIndex;
            int rawMask;

            if (tileSet.Mode == BitmaskMode.Cardinal4Bit)
            {
                rawMask = Calculate4BitMask(pos, cat);
                visualIndex = rawMask;
            }
            else
            {
                rawMask = Calculate8BitMask(pos, cat, out visualIndex);
            }

            OnTileVisualUpdated?.Invoke(pos, rawMask, visualIndex);

            // Výměna 3D modelu nebo vizuálu
            GameObject prefab = tileSet.GetPrefabForIndex(visualIndex);
            if (prefab != null)
            {
                if (spawnedInstances[idx] != null)
                {
                    Destroy(spawnedInstances[idx]);
                }

                Vector3 worldPos = GridToWorld(pos);
                spawnedInstances[idx] = Instantiate(prefab, worldPos, Quaternion.identity, transform);
            }
        }

        // =========================================================================
        // SOUŘADNICOVÉ PŘEVODY A VALIDACE
        // =========================================================================

        public bool IsValidCell(int x, int y)
        {
            return x >= 0 && x < gridWidth && y >= 0 && y < gridHeight;
        }

        public bool IsMatchingCategory(int x, int y, int categoryId)
        {
            if (!IsValidCell(x, y)) return false;
            return tileGrid[y * gridWidth + x] == categoryId;
        }

        public int GetTileCategory(Vector2Int pos)
        {
            if (!IsValidCell(pos.x, pos.y)) return 0;
            return tileGrid[pos.y * gridWidth + pos.x];
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
            if (!drawGizmos || tileGrid == null) return;

            Gizmos.color = Color.white;
            Vector3 center = gridOrigin + new Vector3(gridWidth * cellSize * 0.5f, 0f, gridHeight * cellSize * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(gridWidth * cellSize, 0.1f, gridHeight * cellSize));

            for (int y = 0; y < gridHeight; y++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int cat = tileGrid[y * gridWidth + x];
                    if (cat > 0)
                    {
                        Gizmos.color = Color.green;
                        Gizmos.DrawCube(GridToWorld(new Vector2Int(x, y)), Vector3.one * (cellSize * 0.8f));
                    }
                }
            }
        }
    }
}
