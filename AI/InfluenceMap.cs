using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Simulation;

namespace GameSystems.Tactical
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A DATOVÉ STRUKTURY PRO INFLUENCE MAP
    // =========================================================================

    /// <summary>
    /// Předdefinované typy vrstev vlivu pro taktickou AI, strategie a city-buildery.
    /// </summary>
    public enum InfluenceLayerType
    {
        Threat = 0,         // Nepřátelská přítomnost, palebná síla a nebezpečí
        Friendly = 1,       // Spřátelené jednotky, kontrola území a obrana
        Pollution = 2,      // Znečištění, kouř a hluk z továren (propojení s buňkovým automatem)
        LandValue = 3,      // Atraktivita území a hodnota pozemků (propojení s trhem/budováním)
        Crime = 4,          // Kriminalita, strach obyvatel a nepokoje
        ResourceDemand = 5, // Logistická poptávka po zdrojích
        CustomTactical = 6  // Libovolná uživatelská vrstva pro herní specifika
    }

    /// <summary>
    /// Typ útlumu vlivu od středu emitoru k jeho okraji.
    /// </summary>
    public enum FalloffCurveType
    {
        Linear,      // Plynulý lineární pokles: 1 - (dist / radius)
        Exponential, // Rychlý exponenciální pokles: (1 - dist/radius)^2
        SmoothStep,  // Hladký S-křivkový pokles: 3x^2 - 2x^3
        FlatConst    // Konstantní plná hodnota po celém poloměru
    }

    // =========================================================================
    // 2. HLAVNÍ KOMPONENTA MULTI-LAYER INFLUENCE MAP
    // =========================================================================

    /// <summary>
    /// Vysoce výkonná multi-vrstvá 2D mřížka vlivu (Influence Map) pro taktickou AI,
    /// vyhodnocování bezpečných pozic (cover/retreat), hodnoty pozemků a šíření vlivu.
    /// Podporuje momentum/difúzi, přirozený rozpad (decay), razítkování emitorů a vrstvovou algebru.
    /// </summary>
    [DisallowMultipleComponent]
    public class InfluenceMap : MonoBehaviour, ITickable
    {
        public static InfluenceMap Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno po dokončení difúze a rozpadu dané vrstvy.</summary>
        public event Action<InfluenceLayerType> OnLayerUpdated;

        /// <summary>Vyvoláno, pokud vliv na buňce překročí zadaný varovný práh.</summary>
        public event Action<InfluenceLayerType, Vector2Int, float> OnInfluenceThresholdExceeded;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Rozměry mřížky")]
        [SerializeField, Range(16, 256)] private int gridWidth = 64;
        [SerializeField, Range(16, 256)] private int gridHeight = 64;
        [SerializeField, Min(0.2f)] private float cellSize = 1f;
        [SerializeField] private Vector3 gridOrigin = Vector3.zero;

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Globální dynamika vlivu")]
        [SerializeField, Range(0f, 1f)] private float defaultDecayRate = 0.08f;
        [SerializeField, Range(0f, 0.5f)] private float defaultDiffusionRate = 0.15f;
        [SerializeField, Range(1f, 100f)] private float warningThreshold = 10f;

        [Header("Ladící vizualizace (Gizmos)")]
        [SerializeField] private bool drawGizmos = false;
        [SerializeField] private InfluenceLayerType gizmoLayer = InfluenceLayerType.Threat;
        [SerializeField, Range(1, 4)] private int gizmoStep = 1;

        // =========================================================================
        // VNITŘNÍ STRUKTURA JEDNOTLIVÉ VRSTVY (ZERO-GC FLAT BUFFER)
        // =========================================================================

        [Serializable]
        public class LayerData
        {
            public InfluenceLayerType Type;
            public float DecayRate;
            public float DiffusionRate;
            public float MinValue;
            public float MaxValue;

            public float[] CurrentValues;
            public float[] BufferValues;

            public LayerData(InfluenceLayerType type, int totalCells, float decay, float diffusion, float min, float max)
            {
                Type = type;
                DecayRate = decay;
                DiffusionRate = diffusion;
                MinValue = min;
                MaxValue = max;
                CurrentValues = new float[totalCells];
                BufferValues = new float[totalCells];
            }
        }

        private readonly Dictionary<InfluenceLayerType, LayerData> layers = new Dictionary<InfluenceLayerType, LayerData>(8);

        // Pre-alokované ortogonální offsety pro 4-směrnou difúzi (Laplace stencil)
        private static readonly int[] Dx = { 0, 1, 0, -1 };
        private static readonly int[] Dy = { 1, 0, -1, 0 };

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

            InitializeLayers();
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
        // INICIALIZACE VRSTEV (ZERO-GC PRE-ALLOCATION)
        // =========================================================================

        public void InitializeLayers()
        {
            int totalCells = gridWidth * gridHeight;
            layers.Clear();

            // Vytvoření standardních vrstev s optimalizovanými parametry
            CreateLayer(InfluenceLayerType.Threat, totalCells, defaultDecayRate, defaultDiffusionRate, 0f, 100f);
            CreateLayer(InfluenceLayerType.Friendly, totalCells, defaultDecayRate, defaultDiffusionRate, 0f, 100f);
            CreateLayer(InfluenceLayerType.Pollution, totalCells, 0.02f, 0.08f, 0f, 50f);
            CreateLayer(InfluenceLayerType.LandValue, totalCells, 0.01f, 0.04f, -50f, 100f);
            CreateLayer(InfluenceLayerType.Crime, totalCells, 0.04f, 0.06f, 0f, 50f);
            CreateLayer(InfluenceLayerType.ResourceDemand, totalCells, 0.1f, 0.02f, 0f, 100f);
            CreateLayer(InfluenceLayerType.CustomTactical, totalCells, defaultDecayRate, defaultDiffusionRate, -100f, 100f);
        }

        private void CreateLayer(InfluenceLayerType type, int totalCells, float decay, float diffusion, float min, float max)
        {
            layers[type] = new LayerData(type, totalCells, decay, diffusion, min, max);
        }

        // =========================================================================
        // ITICKABLE IMPLEMENTACE: DIFÚZE A ROZPAD (ZERO-GC HOT-PATH)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            StepAllLayers(tickDeltaTime);
        }

        /// <summary>
        /// Provede krok difúze a rozpadu pro všechny aktivní vrstvy vlivu.
        /// </summary>
        public void StepAllLayers(float deltaTime)
        {
            foreach (var kvp in layers)
            {
                StepLayer(kvp.Value, deltaTime);
                OnLayerUpdated?.Invoke(kvp.Key);
            }
        }

        private void StepLayer(LayerData layer, float dt)
        {
            float[] current = layer.CurrentValues;
            float[] buffer = layer.BufferValues;
            float decayFactor = Mathf.Clamp01(1f - (layer.DecayRate * dt));
            float diffRate = Mathf.Clamp01(layer.DiffusionRate * dt);
            float minVal = layer.MinValue;
            float maxVal = layer.MaxValue;

            int w = gridWidth;
            int h = gridHeight;

            // 1. Laplace difúze do vyrovnávacího bufferu
            for (int y = 0; y < h; y++)
            {
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = rowOffset + x;
                    float centerVal = current[idx];

                    // Součet vlivu sousedů pro difúzi
                    float neighborSum = 0f;
                    int validNeighbors = 0;

                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + Dx[d];
                        int ny = y + Dy[d];
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                        {
                            neighborSum += current[ny * w + nx];
                            validNeighbors++;
                        }
                    }

                    // Výpočet difúze (přenos části hodnoty na průměr sousedů)
                    float neighborAvg = validNeighbors > 0 ? (neighborSum / validNeighbors) : centerVal;
                    float diffusedVal = Mathf.Lerp(centerVal, neighborAvg, diffRate);

                    // Aplikace rozpadu (decay)
                    float finalVal = Mathf.Clamp(diffusedVal * decayFactor, minVal, maxVal);
                    buffer[idx] = finalVal;
                }
            }

            // 2. Přenos bufferu zpět bez alokace paměti
            Array.Copy(buffer, current, current.Length);
        }

        // =========================================================================
        // RAZÍTKOVÁNÍ EMITORŮ VLIVU (INFLUENCE STAMPING)
        // =========================================================================

        /// <summary>
        /// Vyrazí vliv emitoru (kladný nebo záporný) do zadané vrstvy podle světové pozice.
        /// </summary>
        public void StampInfluence(InfluenceLayerType layerType, Vector3 worldPos, float value, float radius, FalloffCurveType falloff = FalloffCurveType.Linear)
        {
            Vector2Int center = WorldToGrid(worldPos);
            StampInfluenceGrid(layerType, center, value, radius, falloff);
        }

        /// <summary>
        /// Vyrazí vliv emitoru do zadané vrstvy podle souřadnic mřížky.
        /// </summary>
        public void StampInfluenceGrid(InfluenceLayerType layerType, Vector2Int centerGrid, float value, float radiusInWorldUnits, FalloffCurveType falloff = FalloffCurveType.Linear)
        {
            if (!layers.TryGetValue(layerType, out LayerData layer)) return;

            int cellRadius = Mathf.CeilToInt(radiusInWorldUnits / cellSize);
            float radiusSqr = radiusInWorldUnits * radiusInWorldUnits;
            float[] current = layer.CurrentValues;

            int minX = Mathf.Max(0, centerGrid.x - cellRadius);
            int maxX = Mathf.Min(gridWidth - 1, centerGrid.x + cellRadius);
            int minY = Mathf.Max(0, centerGrid.y - cellRadius);
            int maxY = Mathf.Min(gridHeight - 1, centerGrid.y + cellRadius);

            Vector3 centerWorld = GridToWorld(centerGrid);

            for (int y = minY; y <= maxY; y++)
            {
                int rowOffset = y * gridWidth;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = rowOffset + x;
                    Vector3 cellWorld = GridToWorld(new Vector2Int(x, y));

                    float dx = cellWorld.x - centerWorld.x;
                    float dz = cellWorld.z - centerWorld.z;
                    float distSqr = dx * dx + dz * dz;

                    if (distSqr <= radiusSqr)
                    {
                        float dist = Mathf.Sqrt(distSqr);
                        float normalizedDist = Mathf.Clamp01(dist / radiusInWorldUnits);
                        float factor = 1f;

                        switch (falloff)
                        {
                            case FalloffCurveType.Linear:
                                factor = 1f - normalizedDist;
                                break;
                            case FalloffCurveType.Exponential:
                                factor = (1f - normalizedDist) * (1f - normalizedDist);
                                break;
                            case FalloffCurveType.SmoothStep:
                                factor = 1f - (normalizedDist * normalizedDist * (3f - 2f * normalizedDist));
                                break;
                            case FalloffCurveType.FlatConst:
                                factor = 1f;
                                break;
                        }

                        current[idx] = Mathf.Clamp(current[idx] + value * factor, layer.MinValue, layer.MaxValue);

                        if (current[idx] >= warningThreshold)
                        {
                            OnInfluenceThresholdExceeded?.Invoke(layerType, new Vector2Int(x, y), current[idx]);
                        }
                    }
                }
            }
        }

        // =========================================================================
        // MATEMATICKÉ OPERACE NAD VRSTVAMI (LAYER ALGEBRA)
        // =========================================================================

        /// <summary>
        /// Spočítá vážený součet dvou vrstev do cílové vrstvy: Dest = LayerA * WeightA + LayerB * WeightB.
        /// </summary>
        public void CombineLayers(InfluenceLayerType destLayer, InfluenceLayerType layerA, float weightA, InfluenceLayerType layerB, float weightB)
        {
            if (!layers.TryGetValue(destLayer, out LayerData dest) ||
                !layers.TryGetValue(layerA, out LayerData a) ||
                !layers.TryGetValue(layerB, out LayerData b))
            {
                return;
            }

            int total = gridWidth * gridHeight;
            float[] destVals = dest.CurrentValues;
            float[] aVals = a.CurrentValues;
            float[] bVals = b.CurrentValues;

            for (int i = 0; i < total; i++)
            {
                float combined = aVals[i] * weightA + bVals[i] * weightB;
                destVals[i] = Mathf.Clamp(combined, dest.MinValue, dest.MaxValue);
            }

            OnLayerUpdated?.Invoke(destLayer);
        }

        /// <summary>
        /// Odečte vrstvu B od vrstvy A do cílové vrstvy: Dest = LayerA - LayerB.
        /// </summary>
        public void SubtractLayers(InfluenceLayerType destLayer, InfluenceLayerType layerA, InfluenceLayerType layerB)
        {
            CombineLayers(destLayer, layerA, 1f, layerB, -1f);
        }

        /// <summary>
        /// Vynásobí hodnoty dvou vrstev buňku po buňce do cílové vrstvy.
        /// </summary>
        public void MultiplyLayers(InfluenceLayerType destLayer, InfluenceLayerType layerA, InfluenceLayerType layerB)
        {
            if (!layers.TryGetValue(destLayer, out LayerData dest) ||
                !layers.TryGetValue(layerA, out LayerData a) ||
                !layers.TryGetValue(layerB, out LayerData b))
            {
                return;
            }

            int total = gridWidth * gridHeight;
            float[] destVals = dest.CurrentValues;
            float[] aVals = a.CurrentValues;
            float[] bVals = b.CurrentValues;

            for (int i = 0; i < total; i++)
            {
                destVals[i] = Mathf.Clamp(aVals[i] * bVals[i], dest.MinValue, dest.MaxValue);
            }

            OnLayerUpdated?.Invoke(destLayer);
        }

        // =========================================================================
        // TAKTICKÉ DOTAZY PRO AI (QUERIES)
        // =========================================================================

        /// <summary>
        /// Získá hodnotu vlivu v konkrétní vrstvě podle světové pozice.
        /// </summary>
        public float GetInfluence(InfluenceLayerType layerType, Vector3 worldPos)
        {
            return GetInfluenceGrid(layerType, WorldToGrid(worldPos));
        }

        /// <summary>
        /// Získá hodnotu vlivu podle souřadnic mřížky.
        /// </summary>
        public float GetInfluenceGrid(InfluenceLayerType layerType, Vector2Int gridPos)
        {
            if (!layers.TryGetValue(layerType, out LayerData layer)) return 0f;
            if (!IsValidCell(gridPos.x, gridPos.y)) return 0f;

            return layer.CurrentValues[gridPos.y * gridWidth + gridPos.x];
        }

        /// <summary>
        /// Nalezne buňku s nejvyšší hodnotou vlivu v zadaném poloměru (např. nejsilnější cíl útoku).
        /// </summary>
        public bool FindHighestInfluenceCell(InfluenceLayerType layerType, Vector3 searchCenter, float searchRadius, out Vector3 bestWorldPos, out float bestValue)
        {
            bestWorldPos = searchCenter;
            bestValue = float.MinValue;
            if (!layers.TryGetValue(layerType, out LayerData layer)) return false;

            Vector2Int center = WorldToGrid(searchCenter);
            int cellRadius = Mathf.CeilToInt(searchRadius / cellSize);
            float radiusSqr = searchRadius * searchRadius;
            float[] vals = layer.CurrentValues;
            bool found = false;

            int minX = Mathf.Max(0, center.x - cellRadius);
            int maxX = Mathf.Min(gridWidth - 1, center.x + cellRadius);
            int minY = Mathf.Max(0, center.y - cellRadius);
            int maxY = Mathf.Min(gridHeight - 1, center.y + cellRadius);

            for (int y = minY; y <= maxY; y++)
            {
                int rowOffset = y * gridWidth;
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 world = GridToWorld(new Vector2Int(x, y));
                    float dx = world.x - searchCenter.x;
                    float dz = world.z - searchCenter.z;

                    if (dx * dx + dz * dz <= radiusSqr)
                    {
                        float val = vals[rowOffset + x];
                        if (val > bestValue)
                        {
                            bestValue = val;
                            bestWorldPos = world;
                            found = true;
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Nalezne nejbezpečnější buňku v dosahu pro taktický ústup nebo krytí
        /// (kombinuje vysokou přítomnost spojenců a minimální hrozbu nepřátel).
        /// </summary>
        public bool FindSafestCellInRadius(Vector3 searchCenter, float searchRadius, out Vector3 safestPos, float friendlyWeight = 1f, float threatWeight = -2f)
        {
            safestPos = searchCenter;
            if (!layers.TryGetValue(InfluenceLayerType.Friendly, out LayerData friendly) ||
                !layers.TryGetValue(InfluenceLayerType.Threat, out LayerData threat))
            {
                return false;
            }

            Vector2Int center = WorldToGrid(searchCenter);
            int cellRadius = Mathf.CeilToInt(searchRadius / cellSize);
            float radiusSqr = searchRadius * searchRadius;
            float bestSafetyScore = float.MinValue;
            bool found = false;

            int minX = Mathf.Max(0, center.x - cellRadius);
            int maxX = Mathf.Min(gridWidth - 1, center.x + cellRadius);
            int minY = Mathf.Max(0, center.y - cellRadius);
            int maxY = Mathf.Min(gridHeight - 1, center.y + cellRadius);

            float[] fVals = friendly.CurrentValues;
            float[] tVals = threat.CurrentValues;

            for (int y = minY; y <= maxY; y++)
            {
                int rowOffset = y * gridWidth;
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 world = GridToWorld(new Vector2Int(x, y));
                    float dx = world.x - searchCenter.x;
                    float dz = world.z - searchCenter.z;

                    if (dx * dx + dz * dz <= radiusSqr)
                    {
                        int idx = rowOffset + x;
                        float score = fVals[idx] * friendlyWeight + tVals[idx] * threatWeight;

                        if (score > bestSafetyScore)
                        {
                            bestSafetyScore = score;
                            safestPos = world;
                            found = true;
                        }
                    }
                }
            }

            return found;
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
            if (!drawGizmos || !layers.TryGetValue(gizmoLayer, out LayerData layer)) return;

            float[] vals = layer.CurrentValues;
            if (vals == null) return;

            // Rámec mřížky
            Gizmos.color = Color.white;
            Vector3 center = gridOrigin + new Vector3(gridWidth * cellSize * 0.5f, 0f, gridHeight * cellSize * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(gridWidth * cellSize, 0.1f, gridHeight * cellSize));

            for (int y = 0; y < gridHeight; y += gizmoStep)
            {
                int rowOffset = y * gridWidth;
                for (int x = 0; x < gridWidth; x += gizmoStep)
                {
                    float val = vals[rowOffset + x];
                    if (Mathf.Abs(val) > 0.1f)
                    {
                        float norm = Mathf.Clamp01(val / 50f);
                        Gizmos.color = gizmoLayer == InfluenceLayerType.Threat
                            ? new Color(1f, 0f, 0f, norm)
                            : new Color(0f, 0.5f, 1f, norm);

                        Vector3 cellPos = GridToWorld(new Vector2Int(x, y));
                        Gizmos.DrawCube(cellPos, Vector3.one * (cellSize * 0.8f));
                    }
                }
            }
        }
    }
}
