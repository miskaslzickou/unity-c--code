using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace GameSystems.Simulation
{
    // =========================================================================
    // 1. ZÁKLADNÍ ENUMY A ROZHRANÍ (KONTRAKTY PRO CELÝ REPOZITÁŘ)
    // =========================================================================

    /// <summary>
    /// Nastavení rychlosti plynutí simulačního času.
    /// </summary>
    public enum SimulationSpeed
    {
        Paused = 0,
        Normal_1x = 1,
        Fast_2x = 2,
        Faster_5x = 5,
        Super_10x = 10
    }

    /// <summary>
    /// Frekvenční kanály pro různé typy herních systémů.
    /// Umožňuje oddělit rychlé AI/fyzikální reakce od pomalých ekonomických a makro výpočtů.
    /// </summary>
    public enum TickChannel
    {
        /// <summary>Rychlý tick (např. 10x za sekundu) – lokální AI, boids, buňkový automat, senzory.</summary>
        Fast = 0,
        /// <summary>Standardní tick (např. 2x za sekundu) – továrny, utility sítě, pohyb na silnicích.</summary>
        Normal = 1,
        /// <summary>Ekonomický tick (např. 1x za sekundu) – nabídka/poptávka trhu, mzdy, daně, logistika.</summary>
        Economy = 2,
        /// <summary>Makro tick (např. 1x za 5 až 10 sekund) – demografie, roční období, dlouhodobá AI strategie.</summary>
        Macro = 3
    }

    /// <summary>
    /// Hlavní rozhraní pro jakýkoliv herní prvek nebo manažer, který vyžaduje deterministickou
    /// simulační aktualizaci nezávislou na snímkové frekvenci (framerate).
    /// </summary>
    public interface ITickable
    {
        /// <summary>
        /// Vyvoláno simulačním taktem manažeru.
        /// </summary>
        /// <param name="tickIndex">Celkový počet proběhlých ticků v daném kanálu od spuštění.</param>
        /// <param name="tickDeltaTime">Fixní krok času simulace v sekundách pro daný kanál.</param>
        void OnSimulationTick(int tickIndex, float tickDeltaTime);
    }

    /// <summary>
    /// Rozhraní pro rozsáhlé kolekce entit (až 10 000+), které se neaktualizují naráz v jednom ticku,
    /// ale jsou rozkouskovány do časových řezů (time-slicing) přes jednotlivé snímky.
    /// </summary>
    public interface ITimeSlicedTickable : ITickable
    {
        /// <summary>
        /// Indikuje, zda je entita stále aktivní a má být v tomto časovém řezu zpracována.
        /// </summary>
        bool IsTickableActive { get; }
    }

    // =========================================================================
    // 2. HLAVNÍ SIMULAČNÍ MANAŽER
    // =========================================================================

    /// <summary>
    /// Vysoce výkonný, deterministický simulační manažer oddělený od vykreslovacího framerate.
    /// Spravuje 4 nezávislé frekvenční kanály a time-slicing frontu s měřeným rozpočtem (Stopwatch)
    /// zaručující nulové záseky snímkové frekvence (Zero-GC v hot path).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)] // Spouští se před běžnými herními skripty
    public class TickSimulationManager : MonoBehaviour
    {
        // Singleton instance pro přímý globální přístup
        public static TickSimulationManager Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (EVENTS) PRO DECOUPLED INTEGRACI OSTATNÍCH MANAŽERŮ
        // =========================================================================

        /// <summary>Vyvoláno při změně rychlosti simulace (včetně pauzy).</summary>
        public static event Action<SimulationSpeed, float> OnSimulationSpeedChanged;

        /// <summary>Vyvoláno při každém rychlém taktu (Fast Tick: tickIndex, tickDeltaTime).</summary>
        public static event Action<int, float> OnFastTick;

        /// <summary>Vyvoláno při každém standardním taktu (Normal Tick: tickIndex, tickDeltaTime).</summary>
        public static event Action<int, float> OnNormalTick;

        /// <summary>Vyvoláno při každém ekonomickém taktu (Economy Tick: tickIndex, tickDeltaTime).</summary>
        public static event Action<int, float> OnEconomyTick;

        /// <summary>Vyvoláno při každém makro taktu (Macro Tick: tickIndex, tickDeltaTime).</summary>
        public static event Action<int, float> OnMacroTick;

        /// <summary>Vyvoláno při provedení kroku v režimu Single-Step (krokování pauzy).</summary>
        public static event Action<TickChannel, int> OnSingleStepExecuted;

        // =========================================================================
        // INSPEKTOR KONFIGURACE FREKVENCÍ A ROZPOČTU
        // =========================================================================

        [Header("Ovládání simulace")]
        [SerializeField] private SimulationSpeed initialSpeed = SimulationSpeed.Normal_1x;
        [SerializeField, Tooltip("Umožňuje plynulé násobení mimo standardní stupně rychlosti")]
        private float customMultiplier = 1f;

        [Header("Frekvence kanálů (v Hz / ticků za sekundu)")]
        [SerializeField, Range(1f, 60f), Tooltip("Rychlý takt (AI, buňky, senzory)")]
        private float fastTickRate = 10f;

        [SerializeField, Range(0.5f, 20f), Tooltip("Standardní takt (továrny, sítě)")]
        private float normalTickRate = 2f;

        [SerializeField, Range(0.1f, 10f), Tooltip("Ekonomický takt (trh, poptávka, finance)")]
        private float economyTickRate = 1f;

        [SerializeField, Range(0.01f, 2f), Tooltip("Makro takt (dlouhodobé trendy, statistiky)")]
        private float macroTickRate = 0.2f; // Každých 5 sekund

        [Header("Time-Slicing rozpočet (pro 10 000+ entit)")]
        [SerializeField, Tooltip("Maximální čas v milisekundách, který smí time-slicing spotřebovat za jeden frame")]
        [Range(0.2f, 10f)]
        private float maxTimeSliceMsPerFrame = 2.0f;

        [SerializeField, Tooltip("Maximální počet zpracovaných entit za jeden frame (bezpečnostní strop)")]
        private int maxTimeSliceEntitiesPerFrame = 1000;

        [Header("Diagnostika a ladění")]
        [SerializeField] private bool showDebugOverlay = true;
        [SerializeField] private bool logTickDeviations = false;

        // =========================================================================
        // VNITŘNÍ STRUKTURY PRO KANÁLY
        // =========================================================================

        private class ChannelState
        {
            public readonly TickChannel Channel;
            public float Interval;
            public float Accumulator;
            public int CurrentTickIndex;

            // Dvojitý seznam pro eliminaci alokací a bezpečné přidávání/odebírání během iterace
            public readonly List<ITickable> RegisteredTickables = new List<ITickable>(128);
            public readonly List<ITickable> PendingAdd = new List<ITickable>(32);
            public readonly HashSet<ITickable> PendingRemove = new HashSet<ITickable>();

            public ChannelState(TickChannel channel, float rate)
            {
                Channel = channel;
                Interval = 1f / Mathf.Max(0.001f, rate);
                Accumulator = 0f;
                CurrentTickIndex = 0;
            }

            public void UpdateInterval(float rate)
            {
                Interval = 1f / Mathf.Max(0.001f, rate);
            }
        }

        private readonly ChannelState[] channels = new ChannelState[4];

        // Fronta entit pro time-slicing
        private readonly List<ITimeSlicedTickable> timeSlicedQueue = new List<ITimeSlicedTickable>(4096);
        private readonly List<ITimeSlicedTickable> pendingTimeSlicedAdd = new List<ITimeSlicedTickable>(256);
        private readonly HashSet<ITimeSlicedTickable> pendingTimeSlicedRemove = new HashSet<ITimeSlicedTickable>();
        private int timeSliceCursor = 0;

        // Stopky pro přesný časový rozpočet bez GC
        private readonly Stopwatch frameStopwatch = new Stopwatch();

        // Stav běhu
        private SimulationSpeed currentSpeed;
        private float currentMultiplier = 1f;
        private bool isSingleStepRequested = false;
        private TickChannel requestedStepChannel = TickChannel.Fast;
        private double totalSimulationTime = 0.0;

        // Metriky pro UI a diagnostiku
        private float lastSliceExecutionTimeMs = 0f;
        private int lastSliceEntitiesProcessed = 0;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC API)
        // =========================================================================

        /// <summary>Aktuální stupeň rychlosti simulace.</summary>
        public SimulationSpeed CurrentSpeed => currentSpeed;

        /// <summary>Zda je simulace v současné chvíli pozastavena.</summary>
        public bool IsPaused => currentSpeed == SimulationSpeed.Paused;

        /// <summary>Skutečný výsledný časový multiplikátor (včetně custom multiplikátoru).</summary>
        public float EffectiveTimeScale => IsPaused ? 0f : ((float)currentSpeed * currentMultiplier);

        /// <summary>Celkový počet uplynulých simulačních sekund od startu scény.</summary>
        public double TotalSimulationTime => totalSimulationTime;

        /// <summary>Celkový počet registrovaných time-sliced entit.</summary>
        public int TotalTimeSlicedEntities => timeSlicedQueue.Count;

        /// <summary>Poslední naměřený čas time-slice zpracování v milisekundách.</summary>
        public float LastSliceExecutionTimeMs => lastSliceExecutionTimeMs;

        /// <summary>Počet entit zpracovaných v posledním time-slice rámci.</summary>
        public int LastSliceEntitiesProcessed => lastSliceEntitiesProcessed;

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

            // Inicializace frekvenčních kanálů
            channels[(int)TickChannel.Fast] = new ChannelState(TickChannel.Fast, fastTickRate);
            channels[(int)TickChannel.Normal] = new ChannelState(TickChannel.Normal, normalTickRate);
            channels[(int)TickChannel.Economy] = new ChannelState(TickChannel.Economy, economyTickRate);
            channels[(int)TickChannel.Macro] = new ChannelState(TickChannel.Macro, macroTickRate);

            currentSpeed = initialSpeed;
            currentMultiplier = Mathf.Max(0.01f, customMultiplier);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            // Aktualizace intervalů, pokud byly změněny v inspectoru
            channels[(int)TickChannel.Fast].UpdateInterval(fastTickRate);
            channels[(int)TickChannel.Normal].UpdateInterval(normalTickRate);
            channels[(int)TickChannel.Economy].UpdateInterval(economyTickRate);
            channels[(int)TickChannel.Macro].UpdateInterval(macroTickRate);

            // Zpracování Single-Step kroku
            if (isSingleStepRequested)
            {
                isSingleStepRequested = false;
                ExecuteSingleStep(requestedStepChannel);
                return;
            }

            float dt = Time.deltaTime;
            float scale = EffectiveTimeScale;

            if (scale > 0f && dt > 0f)
            {
                float simDelta = dt * scale;
                totalSimulationTime += simDelta;

                // 1. Zpracování pevných simulačních kanálů
                for (int i = 0; i < channels.Length; i++)
                {
                    ChannelState ch = channels[i];
                    ch.Accumulator += simDelta;

                    // Ochrana proti spirále smrti při propadu FPS (max 5 kroků na frame)
                    int stepsTaken = 0;
                    while (ch.Accumulator >= ch.Interval && stepsTaken < 5)
                    {
                        ch.Accumulator -= ch.Interval;
                        ch.CurrentTickIndex++;
                        stepsTaken++;

                        DispatchTick(ch);
                    }

                    if (stepsTaken >= 5 && logTickDeviations)
                    {
                        UnityEngine.Debug.LogWarning($"[TickSimulationManager] Kanál {ch.Channel} přesáhl maximální povolený počet kroků za frame. Akumulátor byl vyresetován.");
                        ch.Accumulator = 0f;
                    }
                }
            }

            // 2. Zpracování rozsáhlých kolekcí v časových řezech (Time-slicing)
            ProcessTimeSlicedQueue();
        }

        // =========================================================================
        // JÁDRO DETERMINISTICKÉHO DISPEČINKU (ZERO-GC V HOT-PATH)
        // =========================================================================

        private void DispatchTick(ChannelState state)
        {
            // Aplikace čekajících registrací a deregistrací
            ApplyPendingModifications(state);

            int tickIndex = state.CurrentTickIndex;
            float interval = state.Interval;

            // Vyvolání specifického globálního eventu pro kanál
            switch (state.Channel)
            {
                case TickChannel.Fast:
                    OnFastTick?.Invoke(tickIndex, interval);
                    break;
                case TickChannel.Normal:
                    OnNormalTick?.Invoke(tickIndex, interval);
                    break;
                case TickChannel.Economy:
                    OnEconomyTick?.Invoke(tickIndex, interval);
                    break;
                case TickChannel.Macro:
                    OnMacroTick?.Invoke(tickIndex, interval);
                    break;
            }

            // Iterace přes registrované odběratele
            List<ITickable> list = state.RegisteredTickables;
            int count = list.Count;

            for (int i = 0; i < count; i++)
            {
                ITickable tickable = list[i];
                if (tickable != null)
                {
                    tickable.OnSimulationTick(tickIndex, interval);
                }
            }
        }

        private void ApplyPendingModifications(ChannelState state)
        {
            // Odbavení odebrání
            if (state.PendingRemove.Count > 0)
            {
                for (int i = state.RegisteredTickables.Count - 1; i >= 0; i--)
                {
                    if (state.PendingRemove.Contains(state.RegisteredTickables[i]))
                    {
                        state.RegisteredTickables.RemoveAt(i);
                    }
                }
                state.PendingRemove.Clear();
            }

            // Odbavení přidání
            if (state.PendingAdd.Count > 0)
            {
                for (int i = 0; i < state.PendingAdd.Count; i++)
                {
                    ITickable item = state.PendingAdd[i];
                    if (item != null && !state.RegisteredTickables.Contains(item))
                    {
                        state.RegisteredTickables.Add(item);
                    }
                }
                state.PendingAdd.Clear();
            }
        }

        // =========================================================================
        // TIME-SLICING ROZVRHOVAČ PRO MASIVNÍ ENTITY (AŽ 10 000+)
        // =========================================================================

        private void ProcessTimeSlicedQueue()
        {
            // Aplikace změn ve frontě
            if (pendingTimeSlicedRemove.Count > 0)
            {
                for (int i = timeSlicedQueue.Count - 1; i >= 0; i--)
                {
                    if (pendingTimeSlicedRemove.Contains(timeSlicedQueue[i]))
                    {
                        timeSlicedQueue.RemoveAt(i);
                    }
                }
                pendingTimeSlicedRemove.Clear();
                if (timeSliceCursor >= timeSlicedQueue.Count)
                {
                    timeSliceCursor = 0;
                }
            }

            if (pendingTimeSlicedAdd.Count > 0)
            {
                for (int i = 0; i < pendingTimeSlicedAdd.Count; i++)
                {
                    ITimeSlicedTickable item = pendingTimeSlicedAdd[i];
                    if (item != null && !timeSlicedQueue.Contains(item))
                    {
                        timeSlicedQueue.Add(item);
                    }
                }
                pendingTimeSlicedAdd.Clear();
            }

            int totalCount = timeSlicedQueue.Count;
            if (totalCount == 0 || IsPaused)
            {
                lastSliceExecutionTimeMs = 0f;
                lastSliceEntitiesProcessed = 0;
                return;
            }

            frameStopwatch.Restart();
            double maxTicks = (maxTimeSliceMsPerFrame / 1000.0) * Stopwatch.Frequency;
            int processedCount = 0;
            int scanBudget = Mathf.Min(totalCount, maxTimeSliceEntitiesPerFrame);

            ChannelState fastChannel = channels[(int)TickChannel.Fast];
            int currentTick = fastChannel.CurrentTickIndex;
            float dt = fastChannel.Interval;

            while (processedCount < scanBudget)
            {
                if (timeSliceCursor >= totalCount)
                {
                    timeSliceCursor = 0;
                }

                ITimeSlicedTickable candidate = timeSlicedQueue[timeSliceCursor];
                timeSliceCursor++;
                processedCount++;

                if (candidate != null && candidate.IsTickableActive)
                {
                    candidate.OnSimulationTick(currentTick, dt);
                }

                // Kontrola překročení času podle Stopwatch
                if (frameStopwatch.ElapsedTicks >= maxTicks)
                {
                    break;
                }
            }

            frameStopwatch.Stop();
            lastSliceExecutionTimeMs = (float)((double)frameStopwatch.ElapsedTicks / Stopwatch.Frequency * 1000.0);
            lastSliceEntitiesProcessed = processedCount;
        }

        // =========================================================================
        // VEŘEJNÉ API: REGISTRACE ENTIT A SYSTÉMŮ
        // =========================================================================

        /// <summary>
        /// Zaregistruje instanci ITickable do konkrétního frekvenčního kanálu.
        /// </summary>
        /// <param name="tickable">Instance k pravidelné aktualizaci.</param>
        /// <param name="channel">Cílový kanál určující frekvenci volání.</param>
        public void RegisterTickable(ITickable tickable, TickChannel channel = TickChannel.Normal)
        {
            if (tickable == null) return;
            ChannelState state = channels[(int)channel];

            if (state.PendingRemove.Contains(tickable))
            {
                state.PendingRemove.Remove(tickable);
            }

            if (!state.RegisteredTickables.Contains(tickable) && !state.PendingAdd.Contains(tickable))
            {
                state.PendingAdd.Add(tickable);
            }
        }

        /// <summary>
        /// Odregistruje instanci ITickable ze všech nebo konkrétního kanálu.
        /// </summary>
        public void UnregisterTickable(ITickable tickable, TickChannel channel = TickChannel.Normal)
        {
            if (tickable == null) return;
            ChannelState state = channels[(int)channel];

            if (state.PendingAdd.Contains(tickable))
            {
                state.PendingAdd.Remove(tickable);
            }

            if (!state.PendingRemove.Contains(tickable))
            {
                state.PendingRemove.Add(tickable);
            }
        }

        /// <summary>
        /// Zaregistruje rozsáhlou entitu (např. jednotku, budovu, buňku) do time-slicing fronty.
        /// Entita bude aktualizována rovnoměrně rozprostřená v čase napříč snímky.
        /// </summary>
        public void RegisterTimeSliced(ITimeSlicedTickable tickable)
        {
            if (tickable == null) return;

            if (pendingTimeSlicedRemove.Contains(tickable))
            {
                pendingTimeSlicedRemove.Remove(tickable);
            }

            if (!timeSlicedQueue.Contains(tickable) && !pendingTimeSlicedAdd.Contains(tickable))
            {
                pendingTimeSlicedAdd.Add(tickable);
            }
        }

        /// <summary>
        /// Odregistruje entitu z time-slicing fronty.
        /// </summary>
        public void UnregisterTimeSliced(ITimeSlicedTickable tickable)
        {
            if (tickable == null) return;

            if (pendingTimeSlicedAdd.Contains(tickable))
            {
                pendingTimeSlicedAdd.Remove(tickable);
            }

            if (!pendingTimeSlicedRemove.Contains(tickable))
            {
                pendingTimeSlicedRemove.Add(tickable);
            }
        }

        // =========================================================================
        // OVLÁDÁNÍ RYCHLOSTI, PAUZY A LADĚNÍ
        // =========================================================================

        /// <summary>
        /// Nastaví rychlost simulace na předdefinovaný stupeň (Pause, 1x, 2x, 5x, 10x).
        /// </summary>
        public void SetSpeed(SimulationSpeed speed)
        {
            if (currentSpeed == speed) return;
            currentSpeed = speed;
            OnSimulationSpeedChanged?.Invoke(currentSpeed, EffectiveTimeScale);
        }

        /// <summary>
        /// Nastaví libovolný škálovací koeficient simulace.
        /// </summary>
        public void SetCustomMultiplier(float multiplier)
        {
            customMultiplier = Mathf.Max(0.01f, multiplier);
            currentMultiplier = customMultiplier;
            OnSimulationSpeedChanged?.Invoke(currentSpeed, EffectiveTimeScale);
        }

        /// <summary>
        /// Přepne stav pauzy (mezi Paused a 1x, popř. předchozí rychlostí).
        /// </summary>
        public void TogglePause()
        {
            if (currentSpeed == SimulationSpeed.Paused)
            {
                SetSpeed(SimulationSpeed.Normal_1x);
            }
            else
            {
                SetSpeed(SimulationSpeed.Paused);
            }
        }

        /// <summary>
        /// Vyžádá okamžité provedení právě jednoho simulačního kroku v pozastaveném stavu.
        /// </summary>
        public void StepSingleTick(TickChannel channel = TickChannel.Fast)
        {
            isSingleStepRequested = true;
            requestedStepChannel = channel;
        }

        private void ExecuteSingleStep(TickChannel channel)
        {
            ChannelState state = channels[(int)channel];
            state.CurrentTickIndex++;
            DispatchTick(state);
            OnSingleStepExecuted?.Invoke(channel, state.CurrentTickIndex);
        }

        /// <summary>
        /// Získá aktuální pořadové číslo ticku pro zvolený frekvenční kanál.
        /// </summary>
        public int GetTickIndex(TickChannel channel)
        {
            return channels[(int)channel].CurrentTickIndex;
        }

        /// <summary>
        /// Získá fixní krok času (interval) v sekundách pro daný kanál.
        /// </summary>
        public float GetChannelInterval(TickChannel channel)
        {
            return channels[(int)channel].Interval;
        }

        // =========================================================================
        // ONGUI VIZUALIZACE A OVLÁDACÍ PRVKY PRO RYCHLÉ TESTOVÁNÍ
        // =========================================================================

        private void OnGUI()
        {
            if (!showDebugOverlay) return;

            const int panelWidth = 260;
            const int panelHeight = 210;
            Rect panelRect = new Rect(10, 10, panelWidth, panelHeight);

            GUI.Box(panelRect, "⏱️ Tick Simulation Manager");

            GUILayout.BeginArea(new Rect(15, 35, panelWidth - 10, panelHeight - 35));

            GUILayout.Label($"Rychlost: <b>{currentSpeed}</b> (x{EffectiveTimeScale:F1})");
            GUILayout.Label($"Sim Čas: {totalSimulationTime:F1}s | Ticks: F={channels[0].CurrentTickIndex} N={channels[1].CurrentTickIndex} E={channels[2].CurrentTickIndex}");
            GUILayout.Label($"Time-Slice: {lastSliceEntitiesProcessed}/{timeSlicedQueue.Count} ({lastSliceExecutionTimeMs:F2} ms)");

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(IsPaused ? "▶ Obnovit" : "⏸ Pauza"))
            {
                TogglePause();
            }
            if (GUILayout.Button("Krok ⏭") && IsPaused)
            {
                StepSingleTick(TickChannel.Fast);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("1x")) SetSpeed(SimulationSpeed.Normal_1x);
            if (GUILayout.Button("2x")) SetSpeed(SimulationSpeed.Fast_2x);
            if (GUILayout.Button("5x")) SetSpeed(SimulationSpeed.Faster_5x);
            if (GUILayout.Button("10x")) SetSpeed(SimulationSpeed.Super_10x);
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }
    }
}
