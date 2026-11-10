using System;
using UnityEngine;

namespace GameSystems.Simulation
{
    // =========================================================================
    // 1. ZÁKLADNÍ STRUKTURY A DATOVÉ MODELY BUŇKOVÉHO AUTOMATU
    // =========================================================================

    /// <summary>
    /// Typ stavu buňky v simulační mřížce.
    /// </summary>
    public enum CellType : byte
    {
        Empty = 0,
        Flammable = 1,  // Dřevo, vegetace, les, hořlavá budova
        Burning = 2,    // Aktivní požár
        Burnt = 3,      // Popel / spálená půda
        Water = 4,      // Vodní plocha / zaplavení
        Wall = 5        // Nepropustná překážka / kamenná zeď
    }

    /// <summary>
    /// Výběr sousedství pro výpočty šíření.
    /// </summary>
    public enum NeighborhoodType
    {
        VonNeumann4, // Pouze 4 ortogonální sousedé (nahoru, dolů, vlevo, vpravo)
        Moore8       // Všech 8 sousedů (včetně diagonál)
    }

    /// <summary>
    /// Stav jednotlivé buňky v poli. Optimalizováno pro nulovou alokaci paměti (blittable struct).
    /// </summary>
    [Serializable]
    public struct CellState
    {
        public CellType Type;
        public float Fuel;           // Množství hořlavého materiálu (0.0 až 1.0+)
        public float Moisture;       // Vlhkost tlumící zápal (0.0 až 1.0)
        public float LiquidLevel;    // Hladina vody / kapaliny pro proudění
        public float PollutionLevel; // Koncentrace smogu / znečištění (0.0 až 10.0+)
    }

    // =========================================================================
    // 2. HLAVNÍ SIMULAČNÍ SKRIPT (CELLULAR AUTOMATA)
    // =========================================================================

    /// <summary>
    /// Vysoce optimalizovaný 2D buňkový automat (Double-Buffered Flat Grid) pro simulaci
    /// šíření požárů, proudění vody, difúze smogu a růstu vegetace.
    /// Integruje se přímo s TickSimulationManager (ITickable) a vystavuje čisté veřejné API.
    /// </summary>
    [DisallowMultipleComponent]
    public class CellularAutomataSim : MonoBehaviour, ITickable
    {
        public static CellularAutomataSim Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS PRO PROPOJENÍ S JINÝMI SYSTÉMY)
        // =========================================================================

        /// <summary>Vyvoláno při změně stavu konkrétní buňky (např. pro SFX, VFX, AI reakci).</summary>
        public event Action<Vector2Int, CellType, CellType> OnCellStateChanged; // pos, oldType, newType

        /// <summary>Vyvoláno při vzplanutí nové buňky (pos, intenzita).</summary>
        public event Action<Vector2Int, float> OnFireIgnited;

        /// <summary>Vyvoláno při uhašení buňky vodou nebo vyhořením.</summary>
        public event Action<Vector2Int> OnFireExtinguished;

        /// <summary>Vyvoláno, když znečištění v buňce překročí kritický práh (pos, úroveň).</summary>
        public event Action<Vector2Int, float> OnPollutionHazardThresholdReached;

        // =========================================================================
        // INSPEKTOR KONFIGURACE MŘÍŽKY
        // =========================================================================

        [Header("Rozměry mřížky")]
        [SerializeField, Range(16, 512)] private int gridWidth = 64;
        [SerializeField, Range(16, 512)] private int gridHeight = 64;
        [SerializeField, Min(0.1f)] private float cellSize = 1f;
        [SerializeField] private Vector3 gridOrigin = Vector3.zero;

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Fast;

        [Header("Parametry větru")]
        [SerializeField] private Vector2 windDirection = new Vector2(1f, 0.2f);
        [SerializeField, Range(0f, 5f)] private float windSpeed = 1.5f;

        [Header("Pravidla ohně")]
        [SerializeField, Range(0.01f, 1f)] private float baseIgnitionChance = 0.25f;
        [SerializeField, Range(0.05f, 2f)] private float fireBurnRate = 0.2f;
        [SerializeField, Range(0f, 2f)] private float windFireSpreadFactor = 0.6f;

        [Header("Pravidla kapalin")]
        [SerializeField, Range(0.01f, 1f)] private float liquidViscosity = 0.35f;
        [SerializeField, Range(0.001f, 0.1f)] private float minLiquidFlow = 0.01f;

        [Header("Pravidla znečištění a smogu")]
        [SerializeField, Range(0.01f, 0.5f)] private float pollutionDiffusionRate = 0.12f;
        [SerializeField, Range(0.001f, 0.1f)] private float pollutionDecayRate = 0.008f;
        [SerializeField, Range(0.1f, 5f)] private float pollutionHazardThreshold = 3.0f;

        [Header("Růst vegetace")]
        [SerializeField] private bool enableForestRegrowth = true;
        [SerializeField, Range(0.0001f, 0.05f)] private float regrowthChance = 0.002f;

        [Header("Vizualizace (Texture2D & Gizmos)")]
        [SerializeField] private bool updateTextureVisualization = false;
        [SerializeField] private Renderer targetRenderer = null;
        [SerializeField] private bool drawGizmos = false;

        // =========================================================================
        // VNITŘNÍ DOUBLE-BUFFER PAMĚŤ (ZERO-GC FLAT ARRAYS)
        // =========================================================================

        private CellState[] currentGrid;
        private CellState[] nextGrid;

        private Texture2D visualizationTexture;
        private Color32[] pixelBuffer;

        // Pre-alokované směrové offsety (bez alokací v cyklech)
        private static readonly Vector2Int[] VonNeumannOffsets = {
            new Vector2Int(0, 1),
            new Vector2Int(1, 0),
            new Vector2Int(0, -1),
            new Vector2Int(-1, 0)
        };

        private static readonly Vector2Int[] MooreOffsets = {
            new Vector2Int(0, 1),
            new Vector2Int(1, 0),
            new Vector2Int(0, -1),
            new Vector2Int(-1, 0),
            new Vector2Int(1, 1),
            new Vector2Int(1, -1),
            new Vector2Int(-1, 1),
            new Vector2Int(-1, -1)
        };

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC API)
        // =========================================================================

        public int Width => gridWidth;
        public int Height => gridHeight;
        public float CellSize => cellSize;
        public Vector3 GridOrigin => gridOrigin;
        public Vector2 WindDirection => windDirection.normalized;
        public float WindSpeed => windSpeed;
        public Texture2D VisualizationTexture => visualizationTexture;

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

            InitializeBuffers();
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

            if (visualizationTexture != null)
            {
                Destroy(visualizationTexture);
            }
        }

        // =========================================================================
        // INICIALIZACE A ALOKACE
        // =========================================================================

        /// <summary>
        /// Inicializuje nebo znovu alokuje mřížky na zadané rozměry.
        /// </summary>
        public void InitializeBuffers()
        {
            int totalCells = gridWidth * gridHeight;
            currentGrid = new CellState[totalCells];
            nextGrid = new CellState[totalCells];

            if (updateTextureVisualization || targetRenderer != null)
            {
                pixelBuffer = new Color32[totalCells];
                visualizationTexture = new Texture2D(gridWidth, gridHeight, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };

                if (targetRenderer != null && targetRenderer.sharedMaterial != null)
                {
                    targetRenderer.material.mainTexture = visualizationTexture;
                }
            }

            // Výchozí vyčištění mřížky
            for (int i = 0; i < totalCells; i++)
            {
                currentGrid[i] = new CellState
                {
                    Type = CellType.Empty,
                    Fuel = 0f,
                    Moisture = 0.2f,
                    LiquidLevel = 0f,
                    PollutionLevel = 0f
                };
                nextGrid[i] = currentGrid[i];
            }
        }

        // =========================================================================
        // ROZHRANÍ ITICKABLE (DETERMINISTICKÁ SIMULACE)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            StepSimulation(tickDeltaTime);
        }

        // =========================================================================
        // JÁDRO VÝPOČTU JEDNOHO KROKU (ZERO-GC DOUBLE-BUFFER SWAP)
        // =========================================================================

        /// <summary>
        /// Provede jeden simulační krok celého buňkového pole s fixním nebo dynamickým deltaTime.
        /// </summary>
        public void StepSimulation(float deltaTime)
        {
            if (currentGrid == null || nextGrid == null) return;

            Vector2 normWind = windDirection.sqrMagnitude > 0.001f ? windDirection.normalized : Vector2.zero;
            float currentWindSpeed = windSpeed;

            // 1. Zkopírování současného stavu do bufferu příštího stavu
            Array.Copy(currentGrid, nextGrid, currentGrid.Length);

            // 2. Procházení každé buňky v paměťovém bloku
            for (int y = 0; y < gridHeight; y++)
            {
                int rowOffset = y * gridWidth;

                for (int x = 0; x < gridWidth; x++)
                {
                    int index = rowOffset + x;
                    CellState cur = currentGrid[index];

                    // Přeskočit nepohyblivé kamenné zdi
                    if (cur.Type == CellType.Wall) continue;

                    // --- A. SIMULACE OHNĚ A HOŘENÍ ---
                    if (cur.Type == CellType.Burning)
                    {
                        // Ubývání paliva
                        float fuelBurned = fireBurnRate * deltaTime;
                        nextGrid[index].Fuel = Mathf.Max(0f, cur.Fuel - fuelBurned);

                        // Šíření ohně na okolní hořlavé buňky (Moore 8)
                        PropagateFire(x, y, cur, normWind, currentWindSpeed, deltaTime);

                        // Vyhoření na popel
                        if (nextGrid[index].Fuel <= 0f)
                        {
                            nextGrid[index].Type = CellType.Burnt;
                            OnCellStateChanged?.Invoke(new Vector2Int(x, y), CellType.Burning, CellType.Burnt);
                            OnFireExtinguished?.Invoke(new Vector2Int(x, y));
                        }
                    }

                    // --- B. UHAŠENÍ OHNĚ VODOU ---
                    if (cur.Type == CellType.Burning && cur.LiquidLevel > 0.1f)
                    {
                        nextGrid[index].Type = CellType.Empty;
                        nextGrid[index].Moisture = Mathf.Clamp01(cur.Moisture + 0.5f);
                        nextGrid[index].LiquidLevel = Mathf.Max(0f, cur.LiquidLevel - 0.2f);
                        OnCellStateChanged?.Invoke(new Vector2Int(x, y), CellType.Burning, CellType.Empty);
                        OnFireExtinguished?.Invoke(new Vector2Int(x, y));
                    }

                    // --- C. PROUDĚNÍ KAPALINY (VODA) ---
                    if (cur.LiquidLevel > minLiquidFlow)
                    {
                        SimulateLiquidFlow(x, y, index, cur, deltaTime);
                    }

                    // --- D. DIFÚZE A ADVEKCE SMOGU / ZNEČIŠTĚNÍ ---
                    if (cur.PollutionLevel > 0.01f)
                    {
                        SimulatePollution(x, y, index, cur, normWind, currentWindSpeed, deltaTime);
                    }

                    // --- E. PŘIROZENÝ RŮST VEGETACE ---
                    if (enableForestRegrowth && cur.Type == CellType.Empty && cur.Fuel <= 0.05f)
                    {
                        SimulateRegrowth(x, y, index, cur);
                    }
                }
            }

            // 3. Prohození pointerů bufferů (Double-Buffer Swap) bez alokace paměti
            CellState[] temp = currentGrid;
            currentGrid = nextGrid;
            nextGrid = temp;

            // 4. Volitelná aktualizace textury
            if (updateTextureVisualization && visualizationTexture != null)
            {
                UpdateTextureBuffer();
            }
        }

        // =========================================================================
        // VNITŘNÍ VÝPOČETNÍ MODULY
        // =========================================================================

        private void PropagateFire(int x, int y, CellState sourceCell, Vector2 normWind, float windPwr, float dt)
        {
            for (int i = 0; i < MooreOffsets.Length; i++)
            {
                int nx = x + MooreOffsets[i].x;
                int ny = y + MooreOffsets[i].y;

                if (!IsInBounds(nx, ny)) continue;

                int nIdx = ny * gridWidth + nx;
                CellState neighbor = currentGrid[nIdx];

                if (neighbor.Type == CellType.Flammable && nextGrid[nIdx].Type != CellType.Burning)
                {
                    // Vliv směru větru na šíření
                    Vector2 dirToNeighbor = new Vector2(MooreOffsets[i].x, MooreOffsets[i].y).normalized;
                    float windAlignment = Vector2.Dot(dirToNeighbor, normWind);
                    float windMultiplier = 1f + (windAlignment * windPwr * windFireSpreadFactor);
                    windMultiplier = Mathf.Max(0.1f, windMultiplier);

                    // Vlhkost snižuje šanci na vznícení
                    float moisturePenalty = 1f - Mathf.Clamp01(neighbor.Moisture);

                    float igniteProbability = baseIgnitionChance * windMultiplier * moisturePenalty * dt * 10f;

                    if (UnityEngine.Random.value < igniteProbability)
                    {
                        nextGrid[nIdx].Type = CellType.Burning;
                        nextGrid[nIdx].Moisture = 0f;
                        OnCellStateChanged?.Invoke(new Vector2Int(nx, ny), CellType.Flammable, CellType.Burning);
                        OnFireIgnited?.Invoke(new Vector2Int(nx, ny), neighbor.Fuel);
                    }
                }
            }
        }

        private void SimulateLiquidFlow(int x, int y, int index, CellState cur, float dt)
        {
            // Ortogonální proudění (Von Neumann 4)
            for (int i = 0; i < VonNeumannOffsets.Length; i++)
            {
                int nx = x + VonNeumannOffsets[i].x;
                int ny = y + VonNeumannOffsets[i].y;

                if (!IsInBounds(nx, ny)) continue;

                int nIdx = ny * gridWidth + nx;
                CellState neighbor = currentGrid[nIdx];

                if (neighbor.Type == CellType.Wall) continue;

                // Rozdíl hladin tekutiny
                float levelDiff = cur.LiquidLevel - neighbor.LiquidLevel;
                if (levelDiff > minLiquidFlow)
                {
                    float flowAmount = levelDiff * liquidViscosity * 0.25f * dt * 10f;
                    flowAmount = Mathf.Min(flowAmount, nextGrid[index].LiquidLevel);

                    nextGrid[index].LiquidLevel -= flowAmount;
                    nextGrid[nIdx].LiquidLevel += flowAmount;

                    // Pokud stoupne voda, změní typ buňky na vodu
                    if (nextGrid[nIdx].LiquidLevel > 0.4f && nextGrid[nIdx].Type == CellType.Empty)
                    {
                        nextGrid[nIdx].Type = CellType.Water;
                        OnCellStateChanged?.Invoke(new Vector2Int(nx, ny), CellType.Empty, CellType.Water);
                    }
                }
            }

            // Pokud tekutina z buňky odtekla
            if (nextGrid[index].LiquidLevel <= minLiquidFlow && nextGrid[index].Type == CellType.Water)
            {
                nextGrid[index].Type = CellType.Empty;
                nextGrid[index].Moisture = 0.8f; // Zůstane vlhká půda
                OnCellStateChanged?.Invoke(new Vector2Int(x, y), CellType.Water, CellType.Empty);
            }
        }

        private void SimulatePollution(int x, int y, int index, CellState cur, Vector2 normWind, float windPwr, float dt)
        {
            // Přirozený rozpad znečištění
            float decay = pollutionDecayRate * dt * 10f;
            nextGrid[index].PollutionLevel = Mathf.Max(0f, cur.PollutionLevel - decay);

            // Difúze do všech 8 sousedů
            float diffusePool = cur.PollutionLevel * pollutionDiffusionRate * dt * 5f;
            if (diffusePool <= 0.001f) return;

            float portionPerNeighbor = diffusePool / MooreOffsets.Length;

            for (int i = 0; i < MooreOffsets.Length; i++)
            {
                int nx = x + MooreOffsets[i].x;
                int ny = y + MooreOffsets[i].y;

                if (!IsInBounds(nx, ny)) continue;

                int nIdx = ny * gridWidth + nx;
                if (currentGrid[nIdx].Type == CellType.Wall) continue;

                // Směrové unášení větrem (Advekce)
                Vector2 dir = new Vector2(MooreOffsets[i].x, MooreOffsets[i].y).normalized;
                float windBias = 1f + (Vector2.Dot(dir, normWind) * windPwr);
                float finalAmount = portionPerNeighbor * Mathf.Max(0.2f, windBias);

                nextGrid[index].PollutionLevel = Mathf.Max(0f, nextGrid[index].PollutionLevel - finalAmount);
                nextGrid[nIdx].PollutionLevel += finalAmount;

                // Upozornění na smogové riziko pro ekosystém nebo obyvatele
                if (nextGrid[nIdx].PollutionLevel >= pollutionHazardThreshold &&
                    currentGrid[nIdx].PollutionLevel < pollutionHazardThreshold)
                {
                    OnPollutionHazardThresholdReached?.Invoke(new Vector2Int(nx, ny), nextGrid[nIdx].PollutionLevel);
                }
            }
        }

        private void SimulateRegrowth(int x, int y, int index, CellState cur)
        {
            // Šance na růst vegetace, pokud je v sousedství jiná flóra a dostatek vláhy
            if (cur.Moisture < 0.3f || cur.PollutionLevel > 1.5f) return;

            int flammableNeighbors = 0;
            for (int i = 0; i < VonNeumannOffsets.Length; i++)
            {
                int nx = x + VonNeumannOffsets[i].x;
                int ny = y + VonNeumannOffsets[i].y;
                if (IsInBounds(nx, ny) && currentGrid[ny * gridWidth + nx].Type == CellType.Flammable)
                {
                    flammableNeighbors++;
                }
            }

            if (flammableNeighbors > 0 && UnityEngine.Random.value < regrowthChance * flammableNeighbors)
            {
                nextGrid[index].Type = CellType.Flammable;
                nextGrid[index].Fuel = 0.8f;
                OnCellStateChanged?.Invoke(new Vector2Int(x, y), CellType.Empty, CellType.Flammable);
            }
        }

        // =========================================================================
        // TEXTURE2D VIZUALIZACE (ZERO-GC SETPIXELS32)
        // =========================================================================

        private void UpdateTextureBuffer()
        {
            int total = gridWidth * gridHeight;
            for (int i = 0; i < total; i++)
            {
                CellState cell = currentGrid[i];
                Color32 color;

                switch (cell.Type)
                {
                    case CellType.Wall:
                        color = new Color32(50, 50, 50, 255); // Šedá zeď
                        break;
                    case CellType.Burning:
                        color = new Color32(255, 60, 0, 255); // Jasně oranžový oheň
                        break;
                    case CellType.Flammable:
                        color = new Color32(34, 139, 34, 255); // Lesní zeleň
                        break;
                    case CellType.Burnt:
                        color = new Color32(20, 20, 20, 255); // Černý popel
                        break;
                    case CellType.Water:
                        byte blueShade = (byte)Mathf.Clamp(150 + (int)(cell.LiquidLevel * 50f), 150, 255);
                        color = new Color32(30, 100, blueShade, 255); // Modrá voda
                        break;
                    case CellType.Empty:
                    default:
                        // Podbarvení podle vlhkosti a smogu
                        byte smog = (byte)Mathf.Clamp((int)(cell.PollutionLevel * 40f), 0, 180);
                        byte greenSoil = (byte)Mathf.Clamp((int)(cell.Moisture * 80f), 40, 120);
                        color = new Color32((byte)(60 + smog), (byte)(50 + greenSoil), 40, 255);
                        break;
                }

                pixelBuffer[i] = color;
            }

            visualizationTexture.SetPixels32(pixelBuffer);
            visualizationTexture.Apply(false);
        }

        // =========================================================================
        // VEŘEJNÉ API PRO OSTATNÍ SYSTÉMY A MANAŽERY V REPOZITÁŘI
        // =========================================================================

        /// <summary>
        /// Zapálí konkrétní buňku na mřížce.
        /// </summary>
        public bool IgniteCell(Vector2Int gridPos, float initialIntensity = 1f)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return false;

            int idx = gridPos.y * gridWidth + gridPos.x;
            CellState state = currentGrid[idx];

            if (state.Type == CellType.Wall || state.Type == CellType.Water) return false;

            CellType old = state.Type;
            currentGrid[idx].Type = CellType.Burning;
            currentGrid[idx].Fuel = Mathf.Max(0.5f, state.Fuel);
            currentGrid[idx].Moisture = 0f;

            OnCellStateChanged?.Invoke(gridPos, old, CellType.Burning);
            OnFireIgnited?.Invoke(gridPos, initialIntensity);
            return true;
        }

        /// <summary>
        /// Zapálí buňku podle světových souřadnic ve scéně.
        /// </summary>
        public bool IgniteCellWorld(Vector3 worldPos, float initialIntensity = 1f)
        {
            return IgniteCell(WorldToGrid(worldPos), initialIntensity);
        }

        /// <summary>
        /// Uhashí požár na dané buňce.
        /// </summary>
        public void ExtinguishCell(Vector2Int gridPos)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return;

            int idx = gridPos.y * gridWidth + gridPos.x;
            if (currentGrid[idx].Type == CellType.Burning)
            {
                currentGrid[idx].Type = CellType.Empty;
                currentGrid[idx].Moisture = 0.8f;
                OnCellStateChanged?.Invoke(gridPos, CellType.Burning, CellType.Empty);
                OnFireExtinguished?.Invoke(gridPos);
            }
        }

        /// <summary>
        /// Přidá kapalinu (vodu) do dané buňky (např. prasklé potrubí, déšť, řeka).
        /// </summary>
        public void AddLiquid(Vector2Int gridPos, float amount)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return;

            int idx = gridPos.y * gridWidth + gridPos.x;
            if (currentGrid[idx].Type == CellType.Wall) return;

            currentGrid[idx].LiquidLevel += amount;
            if (currentGrid[idx].LiquidLevel > 0.4f && currentGrid[idx].Type == CellType.Empty)
            {
                CellType old = currentGrid[idx].Type;
                currentGrid[idx].Type = CellType.Water;
                OnCellStateChanged?.Invoke(gridPos, old, CellType.Water);
            }
        }

        public void AddLiquidWorld(Vector3 worldPos, float amount)
        {
            AddLiquid(WorldToGrid(worldPos), amount);
        }

        /// <summary>
        /// Přidá smog/znečištění (voláno z ProductionChainNode nebo dopravy).
        /// </summary>
        public void AddPollution(Vector2Int gridPos, float amount)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return;

            int idx = gridPos.y * gridWidth + gridPos.x;
            currentGrid[idx].PollutionLevel += amount;

            if (currentGrid[idx].PollutionLevel >= pollutionHazardThreshold)
            {
                OnPollutionHazardThresholdReached?.Invoke(gridPos, currentGrid[idx].PollutionLevel);
            }
        }

        public void AddPollutionWorld(Vector3 worldPos, float amount)
        {
            AddPollution(WorldToGrid(worldPos), amount);
        }

        /// <summary>
        /// Nastaví buňku jako neprostupnou zeď nebo ji uvolní (např. GridPlacementSystem).
        /// </summary>
        public void SetObstacle(Vector2Int gridPos, bool isObstacle)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return;

            int idx = gridPos.y * gridWidth + gridPos.x;
            CellType old = currentGrid[idx].Type;
            CellType target = isObstacle ? CellType.Wall : CellType.Empty;

            if (old != target)
            {
                currentGrid[idx].Type = target;
                if (isObstacle)
                {
                    currentGrid[idx].LiquidLevel = 0f;
                    currentGrid[idx].Fuel = 0f;
                }
                OnCellStateChanged?.Invoke(gridPos, old, target);
            }
        }

        public void SetObstacleWorld(Vector3 worldPos, bool isObstacle)
        {
            SetObstacle(WorldToGrid(worldPos), isObstacle);
        }

        /// <summary>
        /// Plná konfigurace stavu buňky.
        /// </summary>
        public void SetCell(Vector2Int gridPos, CellType type, float fuel = 1f, float moisture = 0f, float liquid = 0f)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return;

            int idx = gridPos.y * gridWidth + gridPos.x;
            CellType old = currentGrid[idx].Type;

            currentGrid[idx].Type = type;
            currentGrid[idx].Fuel = fuel;
            currentGrid[idx].Moisture = moisture;
            currentGrid[idx].LiquidLevel = liquid;

            if (old != type)
            {
                OnCellStateChanged?.Invoke(gridPos, old, type);
            }
        }

        /// <summary>
        /// Nastaví vektor a sílu větru.
        /// </summary>
        public void SetWind(Vector2 direction, float speed)
        {
            windDirection = direction;
            windSpeed = Mathf.Max(0f, speed);
        }

        /// <summary>
        /// Získá stav buňky podle souřadnic mřížky.
        /// </summary>
        public CellState GetCell(Vector2Int gridPos)
        {
            if (!IsInBounds(gridPos.x, gridPos.y)) return default;
            return currentGrid[gridPos.y * gridWidth + gridPos.x];
        }

        public CellState GetCell(int x, int y)
        {
            if (!IsInBounds(x, y)) return default;
            return currentGrid[y * gridWidth + x];
        }

        /// <summary>
        /// Získá stav buňky podle pozice ve světě Unity.
        /// </summary>
        public CellState GetCellWorld(Vector3 worldPos)
        {
            return GetCell(WorldToGrid(worldPos));
        }

        // =========================================================================
        // SOUŘADNICOVÉ TRANSFORMACE A VALIDACE
        // =========================================================================

        public bool IsInBounds(int x, int y)
        {
            return x >= 0 && x < gridWidth && y >= 0 && y < gridHeight;
        }

        public Vector2Int WorldToGrid(Vector3 worldPos)
        {
            Vector3 local = worldPos - gridOrigin;
            int x = Mathf.FloorToInt(local.x / cellSize);
            int y = Mathf.FloorToInt(local.z / cellSize); // Předpoklad mřížky na rovině XZ
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
            Vector3 size = new Vector3(gridWidth * cellSize, 0.1f, gridHeight * cellSize);
            Gizmos.DrawWireCube(center, size);

            // Směr větru
            Gizmos.color = Color.yellow;
            Vector3 windStart = center + Vector3.up * 1f;
            Vector3 windVector = new Vector3(windDirection.x, 0f, windDirection.y).normalized * windSpeed * 2f;
            Gizmos.DrawRay(windStart, windVector);

            if (currentGrid == null) return;

            // Zobrazení aktivního ohně a překážek
            for (int y = 0; y < gridHeight; y += 2) // Odběr vzorku pro plynulost Gizmos
            {
                for (int x = 0; x < gridWidth; x += 2)
                {
                    CellState c = currentGrid[y * gridWidth + x];
                    if (c.Type == CellType.Burning)
                    {
                        Gizmos.color = Color.red;
                        Gizmos.DrawCube(GridToWorld(new Vector2Int(x, y)), Vector3.one * (cellSize * 0.8f));
                    }
                    else if (c.Type == CellType.Wall)
                    {
                        Gizmos.color = Color.gray;
                        Gizmos.DrawCube(GridToWorld(new Vector2Int(x, y)), Vector3.one * (cellSize * 0.8f));
                    }
                }
            }
        }
    }
}
