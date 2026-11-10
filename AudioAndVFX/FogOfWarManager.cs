using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Simulation;

namespace GameSystems.Vision
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A ROZHRANÍ PRO MLHU VÁLKY (FOG OF WAR)
    // =========================================================================

    /// <summary>
    /// Tři základní stavy viditelnosti pro RTS a strategické hry:
    /// Unexplored = Černá neprozkoumaná mapa.
    /// Explored = Prozkoumaná šedá mapa (shroud), budovy a terén jsou vidět, ale nepřátelé jsou skryti.
    /// Visible = Plně odhalená zóna pod přímým dohledem jednotky nebo radaru.
    /// </summary>
    public enum FogState : byte
    {
        Unexplored = 0,
        Explored = 1,
        Visible = 2
    }

    /// <summary>
    /// Rozhraní pro libovolný objekt vyzařující zrak (jednotka, budova, hlídková věž, dron).
    /// </summary>
    public interface IVisionEmitter
    {
        Vector3 Position { get; }
        float SightRadius { get; }
        int HeightAdvantage { get; }
        bool IsVisionActive { get; }
    }

    // =========================================================================
    // 2. HLAVNÍ MANAŽER FOG OF WAR
    // =========================================================================

    /// <summary>
    /// Vysoce výkonný manažer mlhy války (Fog of War) kombinující mřížkové rekurzivní oktantové
    /// stínování (Recursive Octant Shadowcasting) a plynule interpolovanou Texture2D pro shadery.
    /// Zajišťuje zero-GC dotazy viditelnosti pro culling AI, UI a renderování jednotek.
    /// </summary>
    [DisallowMultipleComponent]
    public class FogOfWarManager : MonoBehaviour, ITickable
    {
        public static FogOfWarManager Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při prvním odhalení dosud neprozkoumané buňky.</summary>
        public static event Action<Vector2Int> OnCellFirstExplored;

        /// <summary>Vyvoláno po dokončení přepočtu viditelnosti na mřížce.</summary>
        public static event Action OnVisibilityUpdated;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Rozměry mřížky a mapy")]
        [SerializeField, Range(32, 512)] private int gridWidth = 128;
        [SerializeField, Range(32, 512)] private int gridHeight = 128;
        [SerializeField, Min(0.2f)] private float cellSize = 1f;
        [SerializeField] private Vector3 gridOrigin = Vector3.zero;

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Fast;

        [Header("Vizuální parametry mlhy a shaderu")]
        [SerializeField, Range(1f, 20f), Tooltip("Rychlost plynulého stmívání a rozjasňování")]
        private float temporalFadeSpeed = 8f;

        [SerializeField, Tooltip("Globální název textury v shaderech")]
        private string globalShaderTextureName = "_FogOfWarTex";

        [SerializeField, Tooltip("Úroveň ztmavení pro shroud / Explored (0..255)")]
        private byte shroudOpacity = 140;

        [SerializeField, Tooltip("Úroveň ztmavení pro Unexplored (0..255)")]
        private byte unexploredOpacity = 255;

        [Header("Ladění")]
        [SerializeField] private bool drawGizmos = false;
        [SerializeField] private bool revealAllForDebug = false;

        // =========================================================================
        // VNITŘNÍ STAVY A PLOCHÉ BUFFER (ZERO-GC)
        // =========================================================================

        private FogState[] cellStates;         // Unexplored, Explored, Visible
        private byte[] visionBlockers;         // 1 = blokuje zrak (zeď, vysoká hora), 0 = průhledno

        // Buffery pro časovou interpolaci textury bez trhání
        private byte[] targetOpacityBuffer;
        private float[] currentOpacityBuffer;
        private Color32[] pixelBuffer;
        private Texture2D fogTexture;

        private readonly List<IVisionEmitter> emitters = new List<IVisionEmitter>(64);

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public int Width => gridWidth;
        public int Height => gridHeight;
        public float CellSize => cellSize;
        public Vector3 GridOrigin => gridOrigin;
        public Texture2D FogTexture => fogTexture;

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

        private void Update()
        {
            // Plynulá interpolace textury v každém render framu
            InterpolateFogTexture(Time.deltaTime);
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

            if (fogTexture != null)
            {
                Destroy(fogTexture);
            }
        }

        // =========================================================================
        // INICIALIZACE A ALOKACE BUFFERŮ
        // =========================================================================

        public void InitializeBuffers()
        {
            int totalCells = gridWidth * gridHeight;
            cellStates = new FogState[totalCells];
            visionBlockers = new byte[totalCells];
            targetOpacityBuffer = new byte[totalCells];
            currentOpacityBuffer = new float[totalCells];
            pixelBuffer = new Color32[totalCells];

            fogTexture = new Texture2D(gridWidth, gridHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int i = 0; i < totalCells; i++)
            {
                cellStates[i] = FogState.Unexplored;
                visionBlockers[i] = 0;
                targetOpacityBuffer[i] = unexploredOpacity;
                currentOpacityBuffer[i] = unexploredOpacity;
                pixelBuffer[i] = new Color32(0, 0, 0, unexploredOpacity);
            }

            fogTexture.SetPixels32(pixelBuffer);
            fogTexture.Apply(false);

            if (!string.IsNullOrEmpty(globalShaderTextureName))
            {
                Shader.SetGlobalTexture(globalShaderTextureName, fogTexture);
                Shader.SetGlobalVector("_FogOfWarParams", new Vector4(gridOrigin.x, gridOrigin.z, cellSize, gridWidth));
            }
        }

        // =========================================================================
        // ITICKABLE IMPLEMENTACE: PERIODICKÝ VÝPOČET ZRAKU (RECURSIVE SHADOWCASTING)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            ComputeFogOfWar();
        }

        /// <summary>
        /// Provede přepočet zorných polí všech registrovaných emitorů viditelnosti.
        /// </summary>
        public void ComputeFogOfWar()
        {
            int totalCells = gridWidth * gridHeight;

            if (revealAllForDebug)
            {
                for (int i = 0; i < totalCells; i++)
                {
                    cellStates[i] = FogState.Visible;
                    targetOpacityBuffer[i] = 0;
                }
                OnVisibilityUpdated?.Invoke();
                return;
            }

            // 1. Změna předchozích Visible buněk na Explored (shroud)
            for (int i = 0; i < totalCells; i++)
            {
                if (cellStates[i] == FogState.Visible)
                {
                    cellStates[i] = FogState.Explored;
                    targetOpacityBuffer[i] = shroudOpacity;
                }
            }

            // 2. Procházení všech aktivních zrakových emitorů (jednotek)
            int emitterCount = emitters.Count;
            for (int e = 0; e < emitterCount; e++)
            {
                IVisionEmitter emitter = emitters[e];
                if (emitter == null || !emitter.IsVisionActive) continue;

                Vector2Int emitterGridPos = WorldToGrid(emitter.Position);
                if (!IsValidCell(emitterGridPos.x, emitterGridPos.y)) continue;

                int sightRadiusCells = Mathf.CeilToInt((emitter.SightRadius + emitter.HeightAdvantage) / cellSize);

                // Označení samotné buňky emitoru
                MarkCellVisible(emitterGridPos.x, emitterGridPos.y);

                // Rekurzivní Shadowcasting přes 8 oktantů
                for (int octant = 0; octant < 8; octant++)
                {
                    CastOctant(octant, emitterGridPos.x, emitterGridPos.y, 1, 1.0f, 0.0f, sightRadiusCells);
                }
            }

            OnVisibilityUpdated?.Invoke();
        }

        // =========================================================================
        // REKURZIVNÍ OKTANTOVÉ STÍNOVÁNÍ (RECURSIVE OCTANT SHADOWCASTING)
        // =========================================================================

        private void CastOctant(int octant, int cx, int cy, int row, float startSlope, float endSlope, int radius)
        {
            if (startSlope < endSlope) return;

            float radiusSqr = radius * radius;

            for (int j = row; j <= radius; j++)
            {
                int dx = -j - 1;
                int dy = -j;
                bool blocked = false;
                float newStartSlope = 0.0f;

                while (dx <= 0)
                {
                    dx++;
                    // Mapování oktantových souřadnic do mřížky
                    int mapX = cx;
                    int mapY = cy;

                    switch (octant)
                    {
                        case 0: mapX += dx; mapY -= dy; break;
                        case 1: mapX += dy; mapY -= dx; break;
                        case 2: mapX += dy; mapY += dx; break;
                        case 3: mapX += dx; mapY += dy; break;
                        case 4: mapX -= dx; mapY += dy; break;
                        case 5: mapX -= dy; mapY += dx; break;
                        case 6: mapX -= dy; mapY -= dx; break;
                        case 7: mapX -= dx; mapY -= dy; break;
                    }

                    float lSlope = (dx - 0.5f) / (dy + 0.5f);
                    float rSlope = (dx + 0.5f) / (dy - 0.5f);

                    if (startSlope < rSlope) continue;
                    if (endSlope > lSlope) break;

                    if (dx * dx + dy * dy <= radiusSqr)
                    {
                        if (IsValidCell(mapX, mapY))
                        {
                            MarkCellVisible(mapX, mapY);
                        }
                    }

                    if (blocked)
                    {
                        if (IsValidCell(mapX, mapY) && visionBlockers[mapY * gridWidth + mapX] > 0)
                        {
                            newStartSlope = rSlope;
                            continue;
                        }
                        else
                        {
                            blocked = false;
                            startSlope = newStartSlope;
                        }
                    }
                    else
                    {
                        if (IsValidCell(mapX, mapY) && visionBlockers[mapY * gridWidth + mapX] > 0 && j < radius)
                        {
                            blocked = true;
                            CastOctant(octant, cx, cy, j + 1, startSlope, lSlope, radius);
                            newStartSlope = rSlope;
                        }
                    }
                }

                if (blocked) break;
            }
        }

        private void MarkCellVisible(int x, int y)
        {
            int idx = y * gridWidth + x;
            FogState prev = cellStates[idx];

            cellStates[idx] = FogState.Visible;
            targetOpacityBuffer[idx] = 0; // Plná průhlednost (viditelno)

            if (prev == FogState.Unexplored)
            {
                OnCellFirstExplored?.Invoke(new Vector2Int(x, y));
            }
        }

        // =========================================================================
        // TEMPORAL FADING & TEXTURE APPLY (ZERO-GC INTERPOLATION)
        // =========================================================================

        private void InterpolateFogTexture(float dt)
        {
            int total = gridWidth * gridHeight;
            float lerpRate = Mathf.Clamp01(temporalFadeSpeed * dt);
            bool dirty = false;

            for (int i = 0; i < total; i++)
            {
                float target = targetOpacityBuffer[i];
                float current = currentOpacityBuffer[i];

                if (Mathf.Abs(target - current) > 0.5f)
                {
                    current = Mathf.Lerp(current, target, lerpRate);
                    currentOpacityBuffer[i] = current;

                    byte alphaByte = (byte)Mathf.Clamp(Mathf.RoundToInt(current), 0, 255);
                    pixelBuffer[i].a = alphaByte;
                    dirty = true;
                }
            }

            if (dirty)
            {
                fogTexture.SetPixels32(pixelBuffer);
                fogTexture.Apply(false);
            }
        }

        // =========================================================================
        // VEŘEJNÉ API PRO REGISTRACI EMITORŮ A BLOKERŮ
        // =========================================================================

        /// <summary>
        /// Zaregistruje zrakový emitor (jednotku/věž) do sledování.
        /// </summary>
        public void RegisterEmitter(IVisionEmitter emitter)
        {
            if (emitter != null && !emitters.Contains(emitter))
            {
                emitters.Add(emitter);
            }
        }

        /// <summary>
        /// Odregistruje zrakový emitor.
        /// </summary>
        public void UnregisterEmitter(IVisionEmitter emitter)
        {
            if (emitter != null)
            {
                emitters.Remove(emitter);
            }
        }

        /// <summary>
        /// Nastaví buňku jako blokující zrak (zeď, budova, kopec).
        /// </summary>
        public void SetVisionBlocker(Vector2Int gridPos, bool blocks)
        {
            if (!IsValidCell(gridPos.x, gridPos.y)) return;
            visionBlockers[gridPos.y * gridWidth + gridPos.x] = blocks ? (byte)1 : (byte)0;
        }

        public void SetVisionBlockerWorld(Vector3 worldPos, bool blocks)
        {
            SetVisionBlocker(WorldToGrid(worldPos), blocks);
        }

        /// <summary>
        /// Otestuje, zda je světová pozice v současné chvíli pod přímým dohledem jednotek (Visible).
        /// Používá se pro culling renderingu nepřátelských jednotek a UI prvků.
        /// </summary>
        public bool IsWorldPositionVisible(Vector3 worldPos)
        {
            Vector2Int g = WorldToGrid(worldPos);
            if (!IsValidCell(g.x, g.y)) return false;
            return cellStates[g.y * gridWidth + g.x] == FogState.Visible;
        }

        /// <summary>
        /// Otestuje, zda byla daná pozice již alespoň jednou v historii prozkoumána (Explored nebo Visible).
        /// </summary>
        public bool IsWorldPositionExplored(Vector3 worldPos)
        {
            Vector2Int g = WorldToGrid(worldPos);
            if (!IsValidCell(g.x, g.y)) return false;
            return cellStates[g.y * gridWidth + g.x] != FogState.Unexplored;
        }

        public FogState GetCellState(Vector2Int gridPos)
        {
            if (!IsValidCell(gridPos.x, gridPos.y)) return FogState.Unexplored;
            return cellStates[gridPos.y * gridWidth + gridPos.x];
        }

        /// <summary>
        /// Resetuje celou mlhu zpět do neprozkoumaného stavu.
        /// </summary>
        public void ResetFog()
        {
            int total = gridWidth * gridHeight;
            for (int i = 0; i < total; i++)
            {
                cellStates[i] = FogState.Unexplored;
                targetOpacityBuffer[i] = unexploredOpacity;
                currentOpacityBuffer[i] = unexploredOpacity;
                pixelBuffer[i].a = unexploredOpacity;
            }
            fogTexture.SetPixels32(pixelBuffer);
            fogTexture.Apply(false);
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
            if (!drawGizmos || cellStates == null) return;

            // Rámec mřížky
            Gizmos.color = Color.white;
            Vector3 center = gridOrigin + new Vector3(gridWidth * cellSize * 0.5f, 0f, gridHeight * cellSize * 0.5f);
            Gizmos.DrawWireCube(center, new Vector3(gridWidth * cellSize, 0.1f, gridHeight * cellSize));

            // Zobrazení viditelných zón a blokérů
            for (int y = 0; y < gridHeight; y += 2)
            {
                for (int x = 0; x < gridWidth; x += 2)
                {
                    int idx = y * gridWidth + x;
                    if (visionBlockers[idx] > 0)
                    {
                        Gizmos.color = Color.black;
                        Gizmos.DrawCube(GridToWorld(new Vector2Int(x, y)), Vector3.one * (cellSize * 0.8f));
                    }
                    else if (cellStates[idx] == FogState.Visible)
                    {
                        Gizmos.color = new Color(1f, 1f, 0f, 0.2f);
                        Gizmos.DrawCube(GridToWorld(new Vector2Int(x, y)), Vector3.one * (cellSize * 0.5f));
                    }
                }
            }
        }
    }
}
