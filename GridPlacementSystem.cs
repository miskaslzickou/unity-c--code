using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Navigation;
using GameSystems.Vision;

namespace GameSystems.Construction
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A SCRIPTABLE OBJECT PRO STAVBU
    // =========================================================================

    /// <summary>
    /// Čtyři základní 90° rotace budov na mřížce.
    /// </summary>
    public enum BuildingRotation
    {
        Rot_0 = 0,
        Rot_90 = 90,
        Rot_180 = 180,
        Rot_270 = 270
    }

    /// <summary>
    /// Konfigurační předpis budovy (ScriptableObject) pro multi-cell půdorysy,
    /// vlastní masky obsazenosti (L-tvary), náklady a prefabrikované modely.
    /// </summary>
    [CreateAssetMenu(fileName = "NewPlaceableBuilding", menuName = "Construction/Placeable Building")]
    public class PlaceableBuildingSO : ScriptableObject
    {
        [Header("Základní identifikace")]
        [SerializeField] private string buildingId = "bld_factory";
        [SerializeField] private string displayName = "Standard Factory";

        [Header("Půdorys a obsazenost (Footprint)")]
        [SerializeField] private Vector2Int footprintSize = new Vector2Int(2, 2);
        [SerializeField, Tooltip("Volitelná binární maska pro nepravidelné budovy (L-tvary). Pokud je prázdná, obsadí celý obdélník.")]
        private bool[] customOccupancyMask = null;

        [Header("Ekonomika")]
        [SerializeField, Min(0f)] private float constructionCost = 100f;
        [SerializeField, Range(0f, 1f)] private float demolishRefundRatio = 0.5f;

        [Header("Modely a Prefaby")]
        [SerializeField] private GameObject buildingPrefab = null;
        [SerializeField] private GameObject ghostPreviewPrefab = null;

        [Header("Herní interakce a vlivy")]
        [SerializeField] private bool blocksPathfinding = true;
        [SerializeField] private bool blocksVision = false;
        [SerializeField] private float sightRadius = 8f;

        public string BuildingId => buildingId;
        public string DisplayName => displayName;
        public Vector2Int FootprintSize => footprintSize;
        public bool[] CustomOccupancyMask => customOccupancyMask;
        public float ConstructionCost => constructionCost;
        public float DemolishRefundRatio => demolishRefundRatio;
        public GameObject BuildingPrefab => buildingPrefab;
        public GameObject GhostPreviewPrefab => ghostPreviewPrefab;
        public bool BlocksPathfinding => blocksPathfinding;
        public bool BlocksVision => blocksVision;
        public float SightRadius => sightRadius;

        public bool IsCellOccupiedInMask(int lx, int ly)
        {
            if (customOccupancyMask == null || customOccupancyMask.Length == 0) return true;
            int idx = ly * footprintSize.x + lx;
            if (idx >= 0 && idx < customOccupancyMask.Length)
            {
                return customOccupancyMask[idx];
            }
            return true;
        }
    }

    /// <summary>
    /// Běžící instance umístěné budovy na herní scéně.
    /// </summary>
    public class PlacedBuilding
    {
        public string InstanceId { get; }
        public PlaceableBuildingSO Definition { get; }
        public Vector2Int OriginGridPos { get; }
        public BuildingRotation Rotation { get; }
        public GameObject GameObjectInstance { get; }
        public List<Vector2Int> OccupiedCells { get; }

        public PlacedBuilding(PlaceableBuildingSO def, Vector2Int origin, BuildingRotation rot, GameObject instance, List<Vector2Int> cells)
        {
            InstanceId = Guid.NewGuid().ToString("N");
            Definition = def;
            OriginGridPos = origin;
            Rotation = rot;
            GameObjectInstance = instance;
            OccupiedCells = new List<Vector2Int>(cells);
        }
    }

    // =========================================================================
    // 2. HLAVNÍ MANAŽER STAVEBNÍHO SYSTÉMU (GRID PLACEMENT SYSTEM)
    // =========================================================================

    /// <summary>
    /// Kompletní mřížkový stavební systém s přichytáváním (snapping), 90° rotací,
    /// multi-cell půdorysy (např. 3x2, 4x4, L-tvary), ghost náhledem a validací terénu.
    /// Přímo komunikuje s AutoTileBitmasker, FlowFieldPathfinder a FogOfWarManager.
    /// </summary>
    [DisallowMultipleComponent]
    public class GridPlacementSystem : MonoBehaviour
    {
        public static GridPlacementSystem Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno po úspěšném postavení budovy (pro sítě, AI, audio a ekonomiku).</summary>
        public static event Action<PlacedBuilding> OnBuildingPlaced;

        /// <summary>Vyvoláno po zbourání budovy (uvolnění buněk, refundace surovin).</summary>
        public static event Action<PlacedBuilding> OnBuildingDemolished;

        /// <summary>Vyvoláno při změně rotace ghost náhledu.</summary>
        public static event Action<BuildingRotation> OnRotationChanged;

        /// <summary>Vyvoláno při změně validity umístění pod kurzorem (pro UI nebo SFX).</summary>
        public static event Action<bool> OnPlacementValidityChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Konfigurace mřížky")]
        [SerializeField, Range(16, 512)] private int gridWidth = 100;
        [SerializeField, Range(16, 512)] private int gridHeight = 100;
        [SerializeField, Min(0.1f)] private float cellSize = 1f;
        [SerializeField] private Vector3 gridOrigin = Vector3.zero;

        [Header("Vstup a kurzor")]
        [SerializeField] private LayerMask groundLayerMask = ~0;
        [SerializeField] private Camera targetCamera;

        [Header("Barvy Ghost náhledu")]
        [SerializeField] private Color validGhostColor = new Color(0.2f, 1f, 0.2f, 0.6f);
        [SerializeField] private Color invalidGhostColor = new Color(1f, 0.2f, 0.2f, 0.6f);

        [Header("Ladění")]
        [SerializeField] private bool drawGizmos = true;

        // =========================================================================
        // VNITŘNÍ STAV A OBSAZENOST MŘÍŽKY
        // =========================================================================

        // Rychlé O(1) mapování obsazených buněk na instanci budovy
        private readonly Dictionary<Vector2Int, PlacedBuilding> occupancyMap = new Dictionary<Vector2Int, PlacedBuilding>(512);
        private readonly List<PlacedBuilding> allPlacedBuildings = new List<PlacedBuilding>(128);

        // Stav aktivního stavění
        private PlaceableBuildingSO activeBuildingDef;
        private BuildingRotation currentRotation = BuildingRotation.Rot_0;
        private GameObject ghostInstance;
        private MaterialPropertyBlock ghostPropertyBlock;
        private MeshRenderer[] ghostRenderers;

        private Vector2Int currentHoveredCell;
        private bool isCurrentPlacementValid;

        // Pre-alokované seznamy pro výpočty půdorysu (zero-GC)
        private readonly List<Vector2Int> footprintCalculationBuffer = new List<Vector2Int>(32);

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC API)
        // =========================================================================

        public int Width => gridWidth;
        public int Height => gridHeight;
        public float CellSize => cellSize;
        public Vector3 GridOrigin => gridOrigin;
        public BuildingRotation CurrentRotation => currentRotation;
        public bool IsBuildingActive => activeBuildingDef != null;
        public IReadOnlyList<PlacedBuilding> AllPlacedBuildings => allPlacedBuildings;

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

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            ghostPropertyBlock = new MaterialPropertyBlock();
        }

        private void Update()
        {
            if (activeBuildingDef == null) return;

            HandleInput();
            UpdateGhostPosition();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (ghostInstance != null)
            {
                Destroy(ghostInstance);
            }
        }

        // =========================================================================
        // AKTIVACE A OVLÁDÁNÍ STAVBY (PUBLIC API)
        // =========================================================================

        /// <summary>
        /// Zvolí budovu ke stavbě a zobrazí ghost náhled pod myší.
        /// </summary>
        public void SelectBuildingToPlace(PlaceableBuildingSO definition)
        {
            if (definition == null)
            {
                CancelPlacement();
                return;
            }

            activeBuildingDef = definition;
            currentRotation = BuildingRotation.Rot_0;
            SpawnGhostPreview();
        }

        /// <summary>
        /// Zruší aktuální režim stavby a skryje ghost náhled.
        /// </summary>
        public void CancelPlacement()
        {
            activeBuildingDef = null;
            if (ghostInstance != null)
            {
                Destroy(ghostInstance);
                ghostInstance = null;
                ghostRenderers = null;
            }
        }

        /// <summary>
        /// Otočí ghost náhled o 90 stupňů po směru hodinových ručiček.
        /// </summary>
        public void RotateCurrentBuilding()
        {
            currentRotation = (BuildingRotation)(((int)currentRotation + 90) % 360);
            OnRotationChanged?.Invoke(currentRotation);

            if (ghostInstance != null)
            {
                ghostInstance.transform.rotation = Quaternion.Euler(0f, (float)currentRotation, 0f);
            }
        }

        // =========================================================================
        // ZPRACOVÁNÍ VSTUPU A GHOST NÁHLEDU
        // =========================================================================

        private void HandleInput()
        {
            // Klávesa R pro otočení
            if (Input.GetKeyDown(KeyCode.R))
            {
                RotateCurrentBuilding();
            }

            // Klávesa Escape pro zrušení
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
            {
                CancelPlacement();
                return;
            }

            // Levé tlačítko myši pro postavení
            if (Input.GetMouseButtonDown(0))
            {
                if (isCurrentPlacementValid)
                {
                    TryPlaceBuilding(activeBuildingDef, currentHoveredCell, currentRotation, out _);
                }
            }
        }

        private void UpdateGhostPosition()
        {
            if (targetCamera == null) return;

            Ray ray = targetCamera.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 500f, groundLayerMask))
            {
                currentHoveredCell = WorldToGrid(hit.point);
                bool valid = CanPlaceBuilding(activeBuildingDef, currentHoveredCell, currentRotation);

                if (valid != isCurrentPlacementValid)
                {
                    isCurrentPlacementValid = valid;
                    OnPlacementValidityChanged?.Invoke(valid);
                    UpdateGhostColor(valid ? validGhostColor : invalidGhostColor);
                }

                if (ghostInstance != null)
                {
                    Vector3 worldPos = GridToWorldCenter(currentHoveredCell, activeBuildingDef, currentRotation);
                    ghostInstance.transform.position = worldPos;
                }
            }
        }

        private void SpawnGhostPreview()
        {
            if (ghostInstance != null)
            {
                Destroy(ghostInstance);
            }

            GameObject prefabToUse = activeBuildingDef.GhostPreviewPrefab != null
                ? activeBuildingDef.GhostPreviewPrefab
                : activeBuildingDef.BuildingPrefab;

            if (prefabToUse != null)
            {
                ghostInstance = Instantiate(prefabToUse);
                ghostInstance.name = "[GhostPreview]";

                // Vypnutí případných kolizí a skriptů na preview objektu
                Collider[] colliders = ghostInstance.GetComponentsInChildren<Collider>();
                for (int i = 0; i < colliders.Length; i++) colliders[i].enabled = false;

                ghostRenderers = ghostInstance.GetComponentsInChildren<MeshRenderer>();
                UpdateGhostColor(validGhostColor);
            }
        }

        private void UpdateGhostColor(Color color)
        {
            if (ghostRenderers == null) return;

            ghostPropertyBlock.SetColor("_BaseColor", color);
            ghostPropertyBlock.SetColor("_Color", color);

            for (int i = 0; i < ghostRenderers.Length; i++)
            {
                if (ghostRenderers[i] != null)
                {
                    ghostRenderers[i].SetPropertyBlock(ghostPropertyBlock);
                }
            }
        }

        // =========================================================================
        // JÁDRO POSTAVENÍ A DEMOLICE (ZERO-GC MULTI-CELL OCCUPANCY)
        // =========================================================================

        /// <summary>
        /// Pokusí se postavit budovu na zadanou buňku. Zkontroluje kolize a provede razítka.
        /// </summary>
        public bool TryPlaceBuilding(PlaceableBuildingSO def, Vector2Int originCell, BuildingRotation rot, out PlacedBuilding placedBuilding)
        {
            placedBuilding = null;
            if (def == null || !CanPlaceBuilding(def, originCell, rot)) return false;

            // Získání obsazených buněk
            GetRotatedFootprintCells(def, originCell, rot, footprintCalculationBuffer);

            // Vytvoření herního objektu ve scéně
            Vector3 worldCenter = GridToWorldCenter(originCell, def, rot);
            GameObject instance = null;

            if (def.BuildingPrefab != null)
            {
                instance = Instantiate(def.BuildingPrefab, worldCenter, Quaternion.Euler(0f, (float)rot, 0f));
            }

            placedBuilding = new PlacedBuilding(def, originCell, rot, instance, footprintCalculationBuffer);

            // Zápis do mřížky obsazenosti
            for (int i = 0; i < footprintCalculationBuffer.Count; i++)
            {
                occupancyMap[footprintCalculationBuffer[i]] = placedBuilding;
            }
            allPlacedBuildings.Add(placedBuilding);

            // Propojení na FlowFieldPathfinder (pokud budova blokuje průchod)
            if (def.BlocksPathfinding && FlowFieldPathfinder.Instance != null)
            {
                for (int i = 0; i < footprintCalculationBuffer.Count; i++)
                {
                    FlowFieldPathfinder.Instance.SetCost(footprintCalculationBuffer[i], FlowFieldPathfinder.CostImpassable);
                }
            }

            // Propojení na FogOfWarManager (pokud budova blokuje zrak)
            if (def.BlocksVision && FogOfWarManager.Instance != null)
            {
                for (int i = 0; i < footprintCalculationBuffer.Count; i++)
                {
                    FogOfWarManager.Instance.SetVisionBlocker(footprintCalculationBuffer[i], true);
                }
            }

            OnBuildingPlaced?.Invoke(placedBuilding);
            return true;
        }

        /// <summary>
        /// Zbourá budovu na zadané buňce a uvolní všechny její obsazené buňky.
        /// </summary>
        public bool DemolishBuildingAt(Vector2Int gridPos)
        {
            if (!occupancyMap.TryGetValue(gridPos, out PlacedBuilding building))
            {
                return false;
            }

            // Uvolnění všech buněk obsazených touto budovou
            for (int i = 0; i < building.OccupiedCells.Count; i++)
            {
                Vector2Int cell = building.OccupiedCells[i];
                occupancyMap.Remove(cell);

                // Uvolnění v pathfinderu
                if (building.Definition.BlocksPathfinding && FlowFieldPathfinder.Instance != null)
                {
                    FlowFieldPathfinder.Instance.SetCost(cell, FlowFieldPathfinder.CostWalkable);
                }

                // Uvolnění v mlze války
                if (building.Definition.BlocksVision && FogOfWarManager.Instance != null)
                {
                    FogOfWarManager.Instance.SetVisionBlocker(cell, false);
                }
            }

            allPlacedBuildings.Remove(building);

            if (building.GameObjectInstance != null)
            {
                Destroy(building.GameObjectInstance);
            }

            OnBuildingDemolished?.Invoke(building);
            return true;
        }

        public PlacedBuilding GetBuildingAt(Vector2Int gridPos)
        {
            occupancyMap.TryGetValue(gridPos, out PlacedBuilding b);
            return b;
        }

        // =========================================================================
        // OVĚŘENÍ MOŽNOSTI POSTAVENÍ (VALIDATION)
        // =========================================================================

        /// <summary>
        /// Zkontroluje, zda lze danou budovu umístit na zadanou buňku mřížky.
        /// </summary>
        public bool CanPlaceBuilding(PlaceableBuildingSO def, Vector2Int originCell, BuildingRotation rot)
        {
            if (def == null) return false;

            GetRotatedFootprintCells(def, originCell, rot, footprintCalculationBuffer);
            int count = footprintCalculationBuffer.Count;

            for (int i = 0; i < count; i++)
            {
                Vector2Int cell = footprintCalculationBuffer[i];

                // 1. Kontrola hranic mřížky
                if (!IsValidCell(cell.x, cell.y)) return false;

                // 2. Kontrola kolize s existující budovou
                if (occupancyMap.ContainsKey(cell)) return false;
            }

            return true;
        }

        // =========================================================================
        // VÝPOČET OTOČENÉHO PŮDORYSU (ROTATED FOOTPRINT MATH)
        // =========================================================================

        /// <summary>
        /// Spočítá všechny obsazené buňky mřížky pro budovu při rotaci 0°, 90°, 180° nebo 270°.
        /// </summary>
        public void GetRotatedFootprintCells(PlaceableBuildingSO def, Vector2Int origin, BuildingRotation rot, List<Vector2Int> resultsBuffer)
        {
            resultsBuffer.Clear();
            Vector2Int size = def.FootprintSize;

            for (int ly = 0; ly < size.y; ly++)
            {
                for (int lx = 0; lx < size.x; lx++)
                {
                    if (!def.IsCellOccupiedInMask(lx, ly)) continue;

                    Vector2Int rotatedOffset;
                    switch (rot)
                    {
                        case BuildingRotation.Rot_90:
                            rotatedOffset = new Vector2Int(size.y - 1 - ly, lx);
                            break;
                        case BuildingRotation.Rot_180:
                            rotatedOffset = new Vector2Int(size.x - 1 - lx, size.y - 1 - ly);
                            break;
                        case BuildingRotation.Rot_270:
                            rotatedOffset = new Vector2Int(ly, size.x - 1 - lx);
                            break;
                        case BuildingRotation.Rot_0:
                        default:
                            rotatedOffset = new Vector2Int(lx, ly);
                            break;
                    }

                    resultsBuffer.Add(origin + rotatedOffset);
                }
            }
        }

        /// <summary>
        /// Spočítá přesný střed půdorysu ve světových souřadnicích pro správné vycentrování modelu.
        /// </summary>
        public Vector3 GridToWorldCenter(Vector2Int origin, PlaceableBuildingSO def, BuildingRotation rot)
        {
            Vector2Int size = (rot == BuildingRotation.Rot_90 || rot == BuildingRotation.Rot_270)
                ? new Vector2Int(def.FootprintSize.y, def.FootprintSize.x)
                : def.FootprintSize;

            float centerX = (origin.x + size.x * 0.5f) * cellSize;
            float centerZ = (origin.y + size.y * 0.5f) * cellSize;

            return gridOrigin + new Vector3(centerX, 0f, centerZ);
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
            if (!drawGizmos) return;

            // Rámec mřížky
            Gizmos.color = Color.white;
            Vector3 center = gridOrigin + new Vector3(gridWidth * cellSize * 0.5f, 0f, gridHeight * cellSize * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(gridWidth * cellSize, 0.1f, gridHeight * cellSize));

            // Zobrazení obsazených buněk
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.5f);
            foreach (var kvp in occupancyMap)
            {
                Gizmos.DrawCube(GridToWorld(kvp.Key), Vector3.one * (cellSize * 0.9f));
            }
        }
    }
}
