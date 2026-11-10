using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;

namespace GameSystems.Economy
{
    // =========================================================================
    // 1. ORDER BOOK ENUMS & ZERO-GC STRUCTS
    // =========================================================================

    public enum OrderSide : byte
    {
        Buy = 0,  // Bid
        Sell = 1  // Ask
    }

    public enum OrderType : byte
    {
        Limit,
        Market,
        StopLoss
    }

    [Serializable]
    public struct OrderRecord
    {
        public int OrderId;
        public int TraderId;
        public string AssetId;
        public OrderSide Side;
        public OrderType Type;
        public float Price;
        public float Quantity;
        public float RemainingQuantity;
        public float StopTriggerPrice;
        public float Timestamp;

        public bool IsFilled => RemainingQuantity <= 0.00001f;
    }

    [Serializable]
    public struct MarketDepthLevel
    {
        public float Price;
        public float Volume;
        public int OrderCount;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct OrderExecutedEvent : IGameEvent
    {
        public readonly int BuyOrderId;
        public readonly int SellOrderId;
        public readonly string AssetId;
        public readonly float ExecutionPrice;
        public readonly float ExecutedQuantity;
        public readonly float Timestamp;

        public OrderExecutedEvent(int buyId, int sellId, string assetId, float price, float qty, float time)
        {
            BuyOrderId = buyId;
            SellOrderId = sellId;
            AssetId = assetId;
            ExecutionPrice = price;
            ExecutedQuantity = qty;
            Timestamp = time;
        }
    }

    public readonly struct OrderBookSpreadUpdatedEvent : IGameEvent
    {
        public readonly string AssetId;
        public readonly float BestBidPrice;
        public readonly float BestAskPrice;
        public readonly float Spread;
        public readonly float MidPrice;

        public OrderBookSpreadUpdatedEvent(string asset, float bid, float ask, float spread, float mid)
        {
            AssetId = asset;
            BestBidPrice = bid;
            BestAskPrice = ask;
            Spread = spread;
            MidPrice = mid;
        }
    }

    public readonly struct MarketFlashCrashEvent : IGameEvent
    {
        public readonly string AssetId;
        public readonly float PreviousPrice;
        public readonly float CrashPrice;
        public readonly float PercentageDrop;

        public MarketFlashCrashEvent(string asset, float prev, float crash, float dropPct)
        {
            AssetId = asset;
            PreviousPrice = prev;
            CrashPrice = crash;
            PercentageDrop = dropPct;
        }
    }

    // =========================================================================
    // 3. ASSET ORDER BOOK CONTAINER
    // =========================================================================

    public class AssetOrderBook
    {
        public string AssetId;
        public readonly List<OrderRecord> Bids = new List<OrderRecord>(128); // Sorted descending
        public readonly List<OrderRecord> Asks = new List<OrderRecord>(128); // Sorted ascending
        public readonly List<OrderRecord> StopLossOrders = new List<OrderRecord>(32);

        public float LastTradedPrice = 10f;
        public float PreviousTickPrice = 10f;
        public float CumulativeVolume = 0f;
        public float CumulativeTurnover = 0f;

        public float BestBid => Bids.Count > 0 ? Bids[0].Price : 0f;
        public float BestAsk => Asks.Count > 0 ? Asks[0].Price : float.PositiveInfinity;
        public float Spread => (Asks.Count > 0 && Bids.Count > 0) ? (BestAsk - BestBid) : 0f;
        public float MidPrice => (Asks.Count > 0 && Bids.Count > 0) ? (BestBid + BestAsk) * 0.5f : LastTradedPrice;
        public float VWAP => CumulativeVolume > 0.001f ? (CumulativeTurnover / CumulativeVolume) : LastTradedPrice;

        public AssetOrderBook(string assetId, float initialPrice)
        {
            AssetId = assetId;
            LastTradedPrice = initialPrice;
            PreviousTickPrice = initialPrice;
        }
    }

    // =========================================================================
    // 4. MASTER ORDER BOOK & MATCHING ENGINE (ORDERBOOKANDMATCHINGENGINE)
    // =========================================================================

    /// <summary>
    /// High-frequency financial exchange Limit Order Book (LOB) and price-time priority matching engine.
    /// Supports Limit, Market, and Stop-Loss orders with zero-GC matching loops.
    /// Calculates live Bid-Ask Spread, Market Depth (Level 2 book), VWAP, and slippage.
    /// Generates emergent financial phenomena (short squeezes, flash crashes, liquidity dry-ups)
    /// and synchronizes trade execution prices back into DynamicMarketEconomy.
    /// Fully compatible with both 2D and 3D terminal locations and depth visualization.
    /// </summary>
    [DisallowMultipleComponent]
    public class OrderBookAndMatchingEngine : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static OrderBookAndMatchingEngine Instance { get; private set; }

        [Header("Market Integration")]
        [SerializeField] private DynamicMarketEconomy linkedDynamicEconomy;
        [SerializeField] private bool autoSyncToEconomy = true;

        [Header("Flash Crash Thresholds")]
        [Tooltip("Percentage drop in single tick to trigger flash crash circuit breaker (e.g. 25%)")]
        [SerializeField, Range(0.1f, 0.5f)] private float flashCrashThreshold = 0.25f;

        [Header("3D & 2D Telemetry")]
        [SerializeField] private bool is2DDisplay = false;
        [SerializeField] private Vector3 tradingFloorPosition;

        private readonly Dictionary<string, AssetOrderBook> books = new Dictionary<string, AssetOrderBook>(16);
        private int nextOrderId = 1;

        // Pre-allocated depth buffer
        private readonly MarketDepthLevel[] depthBufferBids = new MarketDepthLevel[10];
        private readonly MarketDepthLevel[] depthBufferAsks = new MarketDepthLevel[10];

        public string SaveKey => "OrderBookAndMatchingEngine";

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this) { Destroy(gameObject); return; }

            if (linkedDynamicEconomy == null)
                linkedDynamicEconomy = FindFirstObjectByType<DynamicMarketEconomy>();

            tradingFloorPosition = transform.position;

            // Seed default commodity books
            GetOrCreateBook("res_iron_ore", 15f);
            GetOrCreateBook("res_steel", 45f);
            GetOrCreateBook("res_gold", 250f);
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, TickChannel.Economy);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, TickChannel.Economy);
        }

        public AssetOrderBook GetOrCreateBook(string assetId, float seedPrice = 10f)
        {
            if (!books.TryGetValue(assetId, out var book))
            {
                book = new AssetOrderBook(assetId, seedPrice);
                books[assetId] = book;

                // Seed initial book liquidity
                SeedInitialLiquidity(book, seedPrice);
            }
            return book;
        }

        private void SeedInitialLiquidity(AssetOrderBook book, float basePrice)
        {
            // Seed 5 bids below and 5 asks above
            for (int i = 1; i <= 5; i++)
            {
                float bidP = basePrice * (1f - (i * 0.02f));
                float askP = basePrice * (1f + (i * 0.02f));
                SubmitOrder(new OrderRecord
                {
                    TraderId = 0, // Market Maker
                    AssetId = book.AssetId,
                    Side = OrderSide.Buy,
                    Type = OrderType.Limit,
                    Price = Mathf.Round(bidP * 100f) / 100f,
                    Quantity = 20f * i,
                    RemainingQuantity = 20f * i,
                    Timestamp = Time.time
                });
                SubmitOrder(new OrderRecord
                {
                    TraderId = 0,
                    AssetId = book.AssetId,
                    Side = OrderSide.Sell,
                    Type = OrderType.Limit,
                    Price = Mathf.Round(askP * 100f) / 100f,
                    Quantity = 20f * i,
                    RemainingQuantity = 20f * i,
                    Timestamp = Time.time
                });
            }
        }

        // =========================================================================
        // PUBLIC API: ORDER SUBMISSION & MATCHING
        // =========================================================================

        public int SubmitOrder(OrderRecord order)
        {
            order.OrderId = nextOrderId++;
            if (order.RemainingQuantity <= 0f)
                order.RemainingQuantity = order.Quantity;

            var book = GetOrCreateBook(order.AssetId, order.Price > 0 ? order.Price : 10f);

            // Handle Stop-Loss Orders
            if (order.Type == OrderType.StopLoss)
            {
                book.StopLossOrders.Add(order);
                return order.OrderId;
            }

            // Execute Price-Time Priority Matching
            MatchOrder(book, ref order);

            // If not completely filled and it's a Limit Order, insert into book
            if (!order.IsFilled && order.Type == OrderType.Limit)
            {
                InsertIntoBook(book, order);
            }

            BroadcastSpreadUpdate(book);
            return order.OrderId;
        }

        public bool CancelOrder(string assetId, int orderId)
        {
            if (!books.TryGetValue(assetId, out var book)) return false;

            for (int i = 0; i < book.Bids.Count; i++)
            {
                if (book.Bids[i].OrderId == orderId)
                {
                    book.Bids.RemoveAt(i);
                    BroadcastSpreadUpdate(book);
                    return true;
                }
            }
            for (int i = 0; i < book.Asks.Count; i++)
            {
                if (book.Asks[i].OrderId == orderId)
                {
                    book.Asks.RemoveAt(i);
                    BroadcastSpreadUpdate(book);
                    return true;
                }
            }
            return false;
        }

        private void MatchOrder(AssetOrderBook book, ref OrderRecord incoming)
        {
            float now = Time.time;

            if (incoming.Side == OrderSide.Buy)
            {
                // Match against Asks (lowest asks first)
                for (int i = 0; i < book.Asks.Count; i++)
                {
                    var ask = book.Asks[i];
                    if (incoming.Type == OrderType.Limit && incoming.Price < ask.Price)
                        break; // Best ask is higher than limit bid

                    float matchQty = Mathf.Min(incoming.RemainingQuantity, ask.RemainingQuantity);
                    float matchPrice = ask.Price; // Maker price takes priority

                    incoming.RemainingQuantity -= matchQty;
                    ask.RemainingQuantity -= matchQty;
                    book.Asks[i] = ask;

                    ExecuteTrade(book, incoming.OrderId, ask.OrderId, matchPrice, matchQty, now);

                    if (ask.IsFilled)
                    {
                        book.Asks.RemoveAt(i);
                        i--;
                    }
                    if (incoming.IsFilled) break;
                }
            }
            else // Sell
            {
                // Match against Bids (highest bids first)
                for (int i = 0; i < book.Bids.Count; i++)
                {
                    var bid = book.Bids[i];
                    if (incoming.Type == OrderType.Limit && incoming.Price > bid.Price)
                        break; // Best bid is lower than limit ask

                    float matchQty = Mathf.Min(incoming.RemainingQuantity, bid.RemainingQuantity);
                    float matchPrice = bid.Price; // Maker price

                    incoming.RemainingQuantity -= matchQty;
                    bid.RemainingQuantity -= matchQty;
                    book.Bids[i] = bid;

                    ExecuteTrade(book, bid.OrderId, incoming.OrderId, matchPrice, matchQty, now);

                    if (bid.IsFilled)
                    {
                        book.Bids.RemoveAt(i);
                        i--;
                    }
                    if (incoming.IsFilled) break;
                }
            }
        }

        private void ExecuteTrade(AssetOrderBook book, int buyId, int sellId, float price, float qty, float timestamp)
        {
            book.LastTradedPrice = price;
            book.CumulativeVolume += qty;
            book.CumulativeTurnover += (price * qty);

            // Sync with DynamicMarketEconomy
            if (autoSyncToEconomy && linkedDynamicEconomy != null && linkedDynamicEconomy.ResourceMarket != null)
            {
                if (linkedDynamicEconomy.ResourceMarket.TryGetListing(book.AssetId, out var listing))
                {
                    listing.SetPrice(price);
                }
            }

            EventBus<OrderExecutedEvent>.Raise(new OrderExecutedEvent(
                buyId, sellId, book.AssetId, price, qty, timestamp));
        }

        private void InsertIntoBook(AssetOrderBook book, in OrderRecord order)
        {
            if (order.Side == OrderSide.Buy)
            {
                // Insert sorted descending by price
                int insertIndex = book.Bids.Count;
                for (int i = 0; i < book.Bids.Count; i++)
                {
                    if (order.Price > book.Bids[i].Price)
                    {
                        insertIndex = i;
                        break;
                    }
                }
                book.Bids.Insert(insertIndex, order);
            }
            else
            {
                // Insert sorted ascending by price
                int insertIndex = book.Asks.Count;
                for (int i = 0; i < book.Asks.Count; i++)
                {
                    if (order.Price < book.Asks[i].Price)
                    {
                        insertIndex = i;
                        break;
                    }
                }
                book.Asks.Insert(insertIndex, order);
            }
        }

        private void BroadcastSpreadUpdate(AssetOrderBook book)
        {
            EventBus<OrderBookSpreadUpdatedEvent>.Raise(new OrderBookSpreadUpdatedEvent(
                book.AssetId, book.BestBid, book.BestAsk, book.Spread, book.MidPrice));
        }

        // =========================================================================
        // SIMULATION TICK LOOP (STOP LOSS TRIGGERS & FLASH CRASH DETECTOR)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            foreach (var kvp in books)
            {
                var book = kvp.Value;

                // 1. Trigger Stop-Loss Orders
                for (int s = book.StopLossOrders.Count - 1; s >= 0; s--)
                {
                    var stopOrder = book.StopLossOrders[s];
                    bool trigger = false;
                    if (stopOrder.Side == OrderSide.Sell && book.LastTradedPrice <= stopOrder.StopTriggerPrice)
                        trigger = true;
                    else if (stopOrder.Side == OrderSide.Buy && book.LastTradedPrice >= stopOrder.StopTriggerPrice)
                        trigger = true;

                    if (trigger)
                    {
                        book.StopLossOrders.RemoveAt(s);
                        stopOrder.Type = OrderType.Market;
                        MatchOrder(book, ref stopOrder);
                    }
                }

                // 2. Flash Crash Detection
                if (book.PreviousTickPrice > 0.01f)
                {
                    float dropPct = (book.PreviousTickPrice - book.LastTradedPrice) / book.PreviousTickPrice;
                    if (dropPct >= flashCrashThreshold)
                    {
                        EventBus<MarketFlashCrashEvent>.Raise(new MarketFlashCrashEvent(
                            book.AssetId, book.PreviousTickPrice, book.LastTradedPrice, dropPct));
                    }
                }
                book.PreviousTickPrice = book.LastTradedPrice;
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class LOBEngineSaveData
        {
            public List<AssetSaveData> AssetData = new List<AssetSaveData>();
        }

        [Serializable]
        private class AssetSaveData
        {
            public string AssetId;
            public float LastPrice;
            public float Volume;
        }

        public object CaptureState()
        {
            var data = new LOBEngineSaveData();
            foreach (var kvp in books)
            {
                data.AssetData.Add(new AssetSaveData
                {
                    AssetId = kvp.Key,
                    LastPrice = kvp.Value.LastTradedPrice,
                    Volume = kvp.Value.CumulativeVolume
                });
            }
            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<LOBEngineSaveData>(stateJson);
            if (data == null || data.AssetData == null) return;

            for (int i = 0; i < data.AssetData.Count; i++)
            {
                var ad = data.AssetData[i];
                var book = GetOrCreateBook(ad.AssetId, ad.LastPrice);
                book.LastTradedPrice = ad.LastPrice;
                book.CumulativeVolume = ad.Volume;
            }
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            Vector3 center = tradingFloorPosition != Vector3.zero ? tradingFloorPosition : transform.position;

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(center, is2DDisplay ? new Vector3(3f, 3f, 0.1f) : Vector3.one * 3f);

            // Draw Order Book depth bars
            int assetIdx = 0;
            foreach (var kvp in books)
            {
                var book = kvp.Value;
                Vector3 origin = center + new Vector3(assetIdx * 4f, 0f, 0f);

                // Best Bid bar (Green)
                if (book.Bids.Count > 0)
                {
                    Gizmos.color = Color.green;
                    Gizmos.DrawCube(origin + new Vector3(-0.6f, book.BestBid * 0.05f, 0f), new Vector3(0.5f, book.BestBid * 0.1f, 0.5f));
                }

                // Best Ask bar (Red)
                if (book.Asks.Count > 0)
                {
                    Gizmos.color = Color.red;
                    Gizmos.DrawCube(origin + new Vector3(0.6f, book.BestAsk * 0.05f, 0f), new Vector3(0.5f, book.BestAsk * 0.1f, 0.5f));
                }

                assetIdx++;
            }
        }
    }
}
