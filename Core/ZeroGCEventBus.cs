using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameSystems.Events
{
    // =========================================================================
    // 1. ROZHRANÍ A DELEGÁTY (INTERFACES & DELEGATES)
    // =========================================================================

    /// <summary>
    /// Marker rozhraní pro všechny události sběrnice.
    /// Všechny události MUSÍ být definovány jako 'readonly struct', což garantuje
    /// nulovou alokaci na haldě (Zero-GC) a zamezuje boxingu.
    /// </summary>
    public interface IGameEvent
    {
    }

    /// <summary>
    /// Výkonnostní delegát pro odběr událostí předávaných referencí 'in' (readonly ref).
    /// Zabraňuje kopírování i u rozsáhlejších struct payloadů.
    /// </summary>
    /// <typeparam name="T">Typ události implementující IGameEvent.</typeparam>
    /// <param name="eventData">Neměnná reference na data události.</param>
    public delegate void EventCallback<T>(in T eventData) where T : struct, IGameEvent;

    /// <summary>
    /// Interní negenerické rozhraní kanálu pro centrální registraci a resetování sběrnice.
    /// </summary>
    internal interface IEventChannel
    {
        void Clear();
        int ListenerCount { get; }
        Type EventType { get; }
    }

    // =========================================================================
    // 2. GENERICKÝ KANÁL UDÁLOSTÍ (GENERIC STATIC EVENT CHANNEL)
    // =========================================================================

    /// <summary>
    /// Typově bezpečný, vysoce optimalizovaný statický kanál událostí pro konkrétní struct typ T.
    /// Nabízí re-entrantní bezpečnost (bezpečný odběr i odhlášení během publikace události)
    /// bez vyvolání InvalidOperationException a bez alokací na GC haldě.
    /// </summary>
    /// <typeparam name="T">Struktura události implementující IGameEvent.</typeparam>
    public static class EventBus<T> where T : struct, IGameEvent
    {
        private static readonly List<EventCallback<T>> s_Listeners = new List<EventCallback<T>>(16);
        private static readonly List<EventCallback<T>> s_PendingAdd = new List<EventCallback<T>>(4);
        private static int s_PublishingDepth = 0;
        private static bool s_NeedsCompaction = false;

        static EventBus()
        {
            ZeroGCEventBus.RegisterChannel(new ChannelWrapper());
        }

        /// <summary>Počet aktivně registrovaných posluchačů na tomto kanálu.</summary>
        public static int ListenerCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < s_Listeners.Count; i++)
                {
                    if (s_Listeners[i] != null) count++;
                }
                return count + s_PendingAdd.Count;
            }
        }

        /// <summary>
        /// Přihlásí posluchače k odběru události typu T.
        /// Bezpečné i při volání uvnitř callbacku právě publikované události.
        /// </summary>
        public static void Subscribe(EventCallback<T> listener)
        {
            if (listener == null) return;

            if (s_PublishingDepth > 0)
            {
                // Během iterace zařadíme do čekací fronty, pokud již není přihlášen
                if (!s_Listeners.Contains(listener) && !s_PendingAdd.Contains(listener))
                {
                    s_PendingAdd.Add(listener);
                }
                return;
            }

            if (!s_Listeners.Contains(listener))
            {
                s_Listeners.Add(listener);
            }
        }

        public static void Register(EventCallback<T> listener) => Subscribe(listener);

        /// <summary>
        /// Odhlásí posluchače z odběru události typu T.
        /// Bezpečné i při volání uvnitř callbacku právě publikované události.
        /// </summary>
        public static void Unsubscribe(EventCallback<T> listener)
        {
            if (listener == null) return;

            if (s_PublishingDepth > 0)
            {
                // Pokud probíhá publikace, nahradíme posluchače null hodnotou (tombstone)
                int idx = s_Listeners.IndexOf(listener);
                if (idx >= 0)
                {
                    s_Listeners[idx] = null;
                    s_NeedsCompaction = true;
                }
                s_PendingAdd.Remove(listener);
                return;
            }

            s_Listeners.Remove(listener);
        }

        public static void Unregister(EventCallback<T> listener) => Unsubscribe(listener);

        /// <summary>
        /// Okamžitě vyvolá událost a předá data všem registrovaným posluchačům.
        /// Předání probíhá referencí 'in' s nulovou alokací.
        /// </summary>
        public static void Raise(in T eventData)
        {
            s_PublishingDepth++;
            try
            {
                int count = s_Listeners.Count;
                for (int i = 0; i < count; i++)
                {
                    var listener = s_Listeners[i];
                    if (listener != null)
                    {
                        listener(in eventData);
                    }
                }
            }
            finally
            {
                s_PublishingDepth--;
                if (s_PublishingDepth == 0)
                {
                    FlushPendingMutations();
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ZeroGCEventBus.RecordHistory(typeof(T), s_Listeners.Count);
#endif
        }

        public static void Publish(in T eventData) => Raise(in eventData);

        /// <summary>
        /// Vyčistí všechny posluchače na tomto kanálu.
        /// </summary>
        public static void ClearChannel()
        {
            s_Listeners.Clear();
            s_PendingAdd.Clear();
            s_PublishingDepth = 0;
            s_NeedsCompaction = false;
        }

        private static void FlushPendingMutations()
        {
            // Komprese pole - odstranění null prvků bez alokace nového listu
            if (s_NeedsCompaction)
            {
                int writeIdx = 0;
                for (int readIdx = 0; readIdx < s_Listeners.Count; readIdx++)
                {
                    if (s_Listeners[readIdx] != null)
                    {
                        s_Listeners[writeIdx++] = s_Listeners[readIdx];
                    }
                }

                if (writeIdx < s_Listeners.Count)
                {
                    s_Listeners.RemoveRange(writeIdx, s_Listeners.Count - writeIdx);
                }
                s_NeedsCompaction = false;
            }

            // Přidání čekajících posluchačů
            if (s_PendingAdd.Count > 0)
            {
                for (int i = 0; i < s_PendingAdd.Count; i++)
                {
                    var pending = s_PendingAdd[i];
                    if (!s_Listeners.Contains(pending))
                    {
                        s_Listeners.Add(pending);
                    }
                }
                s_PendingAdd.Clear();
            }
        }

        private sealed class ChannelWrapper : IEventChannel
        {
            public Type EventType => typeof(T);
            public int ListenerCount => EventBus<T>.ListenerCount;
            public void Clear() => EventBus<T>.ClearChannel();
        }
    }

    // =========================================================================
    // 3. CENTRÁLNÍ REGISTR SBĚRNICE (CENTRAL REGISTRY & FACADE)
    // =========================================================================

    /// <summary>
    /// Centrální statická fasáda sběrnice ZeroGCEventBus.
    /// Stará se o registraci všech typových kanálů, jejich automatické pročištění
    /// při resetu domény / Play módu a volitelné sledování historie pro ladění.
    /// </summary>
    public static class ZeroGCEventBus
    {
        private static readonly List<IEventChannel> s_Channels = new List<IEventChannel>(64);

        /// <summary>
        /// Zda se mají automaticky odpojit všechny kanály při načtení nové scény.
        /// Ve výchozím stavu vypnuto (kanály přetrvávají napříč scénami).
        /// </summary>
        public static bool AutoClearOnSceneUnload { get; set; } = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDomainReload()
        {
            ClearAllChannels();
            s_HistoryCount = 0;
            s_HistoryIndex = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitSceneCallbacks()
        {
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (AutoClearOnSceneUnload)
            {
                ClearAllChannels();
            }
        }

        internal static void RegisterChannel(IEventChannel channel)
        {
            if (channel != null && !s_Channels.Contains(channel))
            {
                s_Channels.Add(channel);
            }
        }

        /// <summary>
        /// Zkratka pro přihlášení k odběru události typu T.
        /// </summary>
        public static void Subscribe<T>(EventCallback<T> listener) where T : struct, IGameEvent
        {
            EventBus<T>.Subscribe(listener);
        }

        public static void Register<T>(EventCallback<T> listener) where T : struct, IGameEvent => Subscribe<T>(listener);

        /// <summary>
        /// Zkratka pro odhlášení z odběru události typu T.
        /// </summary>
        public static void Unsubscribe<T>(EventCallback<T> listener) where T : struct, IGameEvent
        {
            EventBus<T>.Unsubscribe(listener);
        }

        public static void Unregister<T>(EventCallback<T> listener) where T : struct, IGameEvent => Unsubscribe<T>(listener);

        /// <summary>
        /// Zkratka pro vyvolání události typu T s předáním dat referencí.
        /// </summary>
        public static void Raise<T>(in T eventData) where T : struct, IGameEvent
        {
            EventBus<T>.Raise(in eventData);
        }

        public static void Publish<T>(in T eventData) where T : struct, IGameEvent => Raise<T>(in eventData);

        /// <summary>
        /// Vrátí počet posluchačů pro daný typ události.
        /// </summary>
        public static int GetListenerCount<T>() where T : struct, IGameEvent
        {
            return EventBus<T>.ListenerCount;
        }

        /// <summary>
        /// Vyčistí všechny registrované kanály v celém projektu.
        /// Zabraňuje memory leakům a visícím odkazům při ukončení Play módu.
        /// </summary>
        public static void ClearAllChannels()
        {
            for (int i = 0; i < s_Channels.Count; i++)
            {
                s_Channels[i]?.Clear();
            }
        }

        // =====================================================================
        // Ladicí historie událostí (Debug History Ring Buffer)
        // =====================================================================

        public readonly struct DebugEventRecord
        {
            public readonly float Time;
            public readonly int Frame;
            public readonly Type EventType;
            public readonly int ListenersNotified;

            public DebugEventRecord(float time, int frame, Type eventType, int listeners)
            {
                Time = time;
                Frame = frame;
                EventType = eventType;
                ListenersNotified = listeners;
            }
        }

        private const int MaxHistoryCapacity = 128;
        private static readonly DebugEventRecord[] s_HistoryBuffer = new DebugEventRecord[MaxHistoryCapacity];
        private static int s_HistoryIndex = 0;
        private static int s_HistoryCount = 0;

        internal static void RecordHistory(Type eventType, int listenerCount)
        {
            s_HistoryBuffer[s_HistoryIndex] = new DebugEventRecord(
                Time.time,
                Time.frameCount,
                eventType,
                listenerCount
            );
            s_HistoryIndex = (s_HistoryIndex + 1) % MaxHistoryCapacity;
            if (s_HistoryCount < MaxHistoryCapacity) s_HistoryCount++;
        }

        /// <summary>
        /// Zkopíruje záznamy historie vyvolaných událostí do předaného bufferu bez alokace.
        /// </summary>
        public static int CopyDebugHistory(DebugEventRecord[] destinationBuffer)
        {
            if (destinationBuffer == null || destinationBuffer.Length == 0) return 0;
            int count = Mathf.Min(destinationBuffer.Length, s_HistoryCount);
            int startIdx = (s_HistoryIndex - s_HistoryCount + MaxHistoryCapacity) % MaxHistoryCapacity;

            for (int i = 0; i < count; i++)
            {
                int bufferPos = (startIdx + i) % MaxHistoryCapacity;
                destinationBuffer[i] = s_HistoryBuffer[bufferPos];
            }
            return count;
        }
    }

    // =========================================================================
    // 4. PŘEDPŘIPRAVENÉ ZÁKLADNÍ UDÁLOSTI (CORE STRUCT EVENTS)
    // =========================================================================

    /// <summary>
    /// Událost změny zdraví entity (léčení, poškození).
    /// </summary>
    public readonly struct HealthChangedEvent : IGameEvent
    {
        public readonly GameObject Entity;
        public readonly float OldHealth;
        public readonly float NewHealth;
        public readonly float MaxHealth;
        public readonly GameObject Instigator;

        public float Delta => NewHealth - OldHealth;
        public float HealthPercent => MaxHealth > 0f ? Mathf.Clamp01(NewHealth / MaxHealth) : 0f;
        public bool IsDamage => Delta < 0f;
        public bool IsHeal => Delta > 0f;

        public HealthChangedEvent(GameObject entity, float oldHealth, float newHealth, float maxHealth, GameObject instigator = null)
        {
            Entity = entity;
            OldHealth = oldHealth;
            NewHealth = newHealth;
            MaxHealth = maxHealth;
            Instigator = instigator;
        }
    }

    /// <summary>
    /// Událost smrti / zničení entity.
    /// </summary>
    public readonly struct EntityDiedEvent : IGameEvent
    {
        public readonly GameObject Entity;
        public readonly GameObject Killer;
        public readonly Vector3 DeathPosition;

        public EntityDiedEvent(GameObject entity, GameObject killer, Vector3 deathPosition)
        {
            Entity = entity;
            Killer = killer;
            DeathPosition = deathPosition;
        }
    }

    /// <summary>
    /// Událost přesunu herní suroviny mezi dvěma pozicemi / kontejnery.
    /// </summary>
    public readonly struct ResourceTransferredEvent : IGameEvent
    {
        public readonly string ResourceId;
        public readonly int Amount;
        public readonly Vector3 FromPosition;
        public readonly Vector3 ToPosition;

        public ResourceTransferredEvent(string resourceId, int amount, Vector3 fromPosition, Vector3 toPosition)
        {
            ResourceId = resourceId;
            Amount = amount;
            FromPosition = fromPosition;
            ToPosition = toPosition;
        }
    }

    /// <summary>
    /// Událost úspěšného umístění budovy na mřížku.
    /// </summary>
    public readonly struct BuildingPlacedEvent : IGameEvent
    {
        public readonly string BuildingId;
        public readonly Vector3Int GridPosition;
        public readonly GameObject BuildingInstance;

        public BuildingPlacedEvent(string buildingId, Vector3Int gridPosition, GameObject buildingInstance)
        {
            BuildingId = buildingId;
            GridPosition = gridPosition;
            BuildingInstance = buildingInstance;
        }
    }

    /// <summary>
    /// Událost cenového upozornění nebo výkyvu na dynamickém trhu.
    /// </summary>
    public readonly struct MarketPriceAlertEvent : IGameEvent
    {
        public readonly string AssetId;
        public readonly float OldPrice;
        public readonly float NewPrice;
        public readonly float Volatility;

        public float PriceDifference => NewPrice - OldPrice;
        public float PercentageChange => OldPrice > 0.0001f ? ((NewPrice - OldPrice) / OldPrice) * 100f : 0f;

        public MarketPriceAlertEvent(string assetId, float oldPrice, float newPrice, float volatility)
        {
            AssetId = assetId;
            OldPrice = oldPrice;
            NewPrice = newPrice;
            Volatility = volatility;
        }
    }
}
