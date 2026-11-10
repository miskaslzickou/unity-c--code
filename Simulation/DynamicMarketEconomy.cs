using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameSystems.Economy
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ (KONTRAKTY PRO GENERICITU)
    // =========================================================================

    /// <summary>
    /// Základní rozhraní pro jakékoliv obchodovatelné aktivum (surovina, komodita, akcie, měna).
    /// </summary>
    public interface ITradableAsset
    {
        string AssetId { get; }
        string DisplayName { get; }
        float BaseValue { get; }
        float MinValue { get; }
        float MaxValue { get; }
        float Volatility { get; }
    }

    /// <summary>
    /// Vyměnitelný matematický algoritmus pro výpočet ceny konkrétního typu aktiva.
    /// Jinak se počítá cena uhlí na skladě a jinak cena akcií korporace.
    /// </summary>
    public interface IPricingStrategy<T> where T : ITradableAsset
    {
        float CalculateNewPrice(MarketListing<T> listing, float globalInflation, float deltaTime);
    }

    // =========================================================================
    // 2. KONKRÉTNÍ TYPY AKTIV (MATERIÁLY VS. AKCIE)
    // =========================================================================

    public enum ResourceCategory
    {
        RawMaterial,      // Dřevo, železná ruda, ropa, kámen
        RefinedMaterial,  // Ocel, prkna, palivo, plast
        Commodity,        // Jídlo, oblečení, elektronika
        Utility           // Elektřina, voda, výpočetní výkon
    }

    /// <summary>
    /// ScriptableObject pro fyzické suroviny a komodity – snadno přidáš nové v Unity Editoru.
    /// </summary>
    [CreateAssetMenu(fileName = "NewResource", menuName = "Economy/Physical Resource Asset")]
    public class PhysicalResourceAsset : ScriptableObject, ITradableAsset
    {
        [Header("Základní identifikace")]
        [SerializeField] private string assetId = "res_iron_ore";
        [SerializeField] private string displayName = "Iron Ore";
        [SerializeField] private ResourceCategory category = ResourceCategory.RawMaterial;

        [Header("Cenové parametry")]
        [SerializeField, Min(0.01f)] private float baseValue = 10f;
        [SerializeField, Min(0.01f)] private float minValue = 1f;
        [SerializeField, Min(0.01f)] private float maxValue = 100f;
        [SerializeField, Range(0.01f, 2f)] private float volatility = 0.25f;

        [Header("Fyzické vlastnosti (pro logistiku a sklady)")]
        [SerializeField, Min(0.01f)] private float weightPerUnit = 1f;
        [SerializeField, Range(0f, 1f)] private float spoilageRatePerTick = 0f; // Kazivost (např. jídlo)

        public string AssetId => assetId;
        public string DisplayName => displayName;
        public ResourceCategory Category => category;
        public float BaseValue => baseValue;
        public float MinValue => minValue;
        public float MaxValue => maxValue;
        public float Volatility => volatility;
        public float WeightPerUnit => weightPerUnit;
        public float SpoilageRatePerTick => spoilageRatePerTick;
    }

    /// <summary>
    /// Třída pro akcie firem (Stocks) – lze vytvářet v Editoru i dynamicky za běhu hry, když vznikne nová firma.
    /// </summary>
    [Serializable]
    public class StockAsset : ITradableAsset
    {
        [SerializeField] private string tickerSymbol = "CORP";
        [SerializeField] private string companyName = "MegaCorp Industries";
        [SerializeField] private float ipoSharePrice = 100f;
        [SerializeField] private float volatility = 0.4f;

        [Header("Korporátní metriky")]
        public long TotalSharesIssued = 100000;
        public float CompanyNetWorth = 10000000f;
        public float QuarterlyProfit = 250000f;
        public float DividendYield = 0.02f;
        public float MarketSentiment = 0f; // -1 (panika) až +1 (euforie/hype)

        public string AssetId => tickerSymbol;
        public string DisplayName => companyName;
        public float BaseValue => ipoSharePrice;
        public float MinValue => 0.01f; // Akcie může spadnout na penny stock
        public float MaxValue => 1000000f;
        public float Volatility => volatility;

        public StockAsset(string ticker, string name, float initialPrice, long shares, float netWorth)
        {
            tickerSymbol = ticker;
            companyName = name;
            ipoSharePrice = initialPrice;
            TotalSharesIssued = shares;
            CompanyNetWorth = netWorth;
        }
    }

    // =========================================================================
    // 3. ZÁZNAM AKTIVA NA TRHU + ZERO-GC HISTORIE CEN (RING BUFFER)
    // =========================================================================

    /// <summary>
    /// Drží živá tržní data konkrétního aktiva (nabídka, poptávka, cena, historie pro grafy).
    /// </summary>
    public class MarketListing<T> where T : ITradableAsset
    {
        public readonly T Asset;
        public float CurrentPrice { get; private set; }
        public float Supply { get; set; }
        public float Demand { get; set; }
        public float EquilibriumSupply { get; set; }

        // Zero-GC kruhový buffer pro vykreslování grafů (např. posledních 128 ticků)
        private readonly float[] priceHistory;
        private int historyHead;
        private int historyCount;

        public int HistoryCount => historyCount;

        public MarketListing(T asset, float initialSupply, float initialDemand, int historyCapacity = 128)
        {
            Asset = asset;
            CurrentPrice = asset.BaseValue;
            Supply = Mathf.Max(0f, initialSupply);
            Demand = Mathf.Max(0f, initialDemand);
            EquilibriumSupply = Mathf.Max(1f, initialSupply);

            priceHistory = new float[Mathf.Max(8, historyCapacity)];
            RecordPriceToHistory(CurrentPrice);
        }

        public void SetPrice(float newPrice)
        {
            CurrentPrice = Mathf.Clamp(newPrice, Asset.MinValue, Asset.MaxValue);
            RecordPriceToHistory(CurrentPrice);
        }

        private void RecordPriceToHistory(float price)
        {
            priceHistory[historyHead] = price;
            historyHead = (historyHead + 1) % priceHistory.Length;
            if (historyCount < priceHistory.Length)
            {
                historyCount++;
            }
        }

        /// <summary>
        /// Vrátí historickou cenu od nejstarší (index 0) po nejnovější (index HistoryCount - 1) bez alokací.
        /// </summary>
        public float GetHistoricalPrice(int chronologicalIndex)
        {
            if (chronologicalIndex < 0 || chronologicalIndex >= historyCount) return CurrentPrice;
            int start = historyCount == priceHistory.Length ? historyHead : 0;
            int actualIndex = (start + chronologicalIndex) % priceHistory.Length;
            return priceHistory[actualIndex];
        }
    }

    // =========================================================================
    // 4. VÝCHOZÍ CENOVÉ STRATEGIE (KOMODITY VS. AKCIE)
    // =========================================================================

    /// <summary>
    /// Cenotvorba pro suroviny a komodity založená na poměru nabídky a poptávky + kazivosti.
    /// </summary>
    public class PhysicalResourcePricingStrategy : IPricingStrategy<PhysicalResourceAsset>
    {
        public float CalculateNewPrice(MarketListing<PhysicalResourceAsset> listing, float globalInflation, float deltaTime)
        {
            // Aplikace kazivosti na skladové zásoby (např. potraviny)
            if (listing.Asset.SpoilageRatePerTick > 0f)
            {
                listing.Supply = Mathf.Max(0f, listing.Supply * (1f - listing.Asset.SpoilageRatePerTick * deltaTime));
            }

            float safeSupply = Mathf.Max(1f, listing.Supply);
            float safeDemand = Mathf.Max(1f, listing.Demand);

            // Poměr poptávky k nabídce logaritmicky tlumený, aby cena nevystřelila do nekonečna během vteřiny
            float ratio = safeDemand / safeSupply;
            float scarcityFactor = Mathf.Pow(ratio, listing.Asset.Volatility);

            float targetPrice = listing.Asset.BaseValue * scarcityFactor * globalInflation;
            return Mathf.Lerp(listing.CurrentPrice, targetPrice, Mathf.Clamp01(deltaTime * 2f));
        }
    }

    /// <summary>
    /// Cenotvorba pro akcie založená na vnitřní hodnotě firmy (Book Value), zisku, sentimentu a objemu obchodů.
    /// </summary>
    public class StockPricingStrategy : IPricingStrategy<StockAsset>
    {
        public float CalculateNewPrice(MarketListing<StockAsset> listing, float globalInflation, float deltaTime)
        {
            StockAsset stock = listing.Asset;
            long safeShares = Math.Max(1L, stock.TotalSharesIssued);

            // Fundamentální cena akcie: (Hodnota firmy + 4x kvartální zisk) / počet akcií
            float fundamentalPrice = (stock.CompanyNetWorth + (stock.QuarterlyProfit * 4f)) / safeShares;

            // Tlak nákupů a prodejů (Order Imbalance)
            float orderDifference = listing.Demand - listing.Supply;
            float orderImpact = (orderDifference / safeShares) * stock.Volatility * 10f;

            // Vliv nálady na trhu (Sentiment / Hype / Panika)
            float sentimentMultiplier = 1f + (stock.MarketSentiment * stock.Volatility);

            float targetPrice = Mathf.Max(0.01f, fundamentalPrice * sentimentMultiplier * (1f + orderImpact) * globalInflation);
            return Mathf.Lerp(listing.CurrentPrice, targetPrice, Mathf.Clamp01(deltaTime * 3f));
        }
    }

    // =========================================================================
    // 5. GENERICKÝ TRŽNÍ POOL (MARKET SECTOR)
    // =========================================================================

    /// <summary>
    /// Samostatný tržní segment pro libovolný typ ITradableAsset.
    /// Můžeš mít jeden pro suroviny, druhý pro akcie, třetí pro černý trh nebo měny.
    /// </summary>
    public class MarketPool<T> where T : ITradableAsset
    {
        private readonly Dictionary<string, MarketListing<T>> listingsById = new Dictionary<string, MarketListing<T>>(64);
        private readonly List<MarketListing<T>> activeListings = new List<MarketListing<T>>(64);
        private IPricingStrategy<T> pricingStrategy;

        public IReadOnlyList<MarketListing<T>> ActiveListings => activeListings;
        public event Action<MarketListing<T>, float, float> OnPriceChanged; // listing, oldPrice, newPrice

        public MarketPool(IPricingStrategy<T> strategy)
        {
            pricingStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        }

        public void SetPricingStrategy(IPricingStrategy<T> newStrategy)
        {
            if (newStrategy != null) pricingStrategy = newStrategy;
        }

        public MarketListing<T> RegisterAsset(T asset, float initialSupply, float initialDemand, int historySize = 128)
        {
            if (asset == null || string.IsNullOrEmpty(asset.AssetId)) return null;

            if (listingsById.TryGetValue(asset.AssetId, out MarketListing<T> existing))
            {
                return existing;
            }

            var listing = new MarketListing<T>(asset, initialSupply, initialDemand, historySize);
            listingsById.Add(asset.AssetId, listing);
            activeListings.Add(listing);
            return listing;
        }

        public bool TryGetListing(string assetId, out MarketListing<T> listing)
        {
            return listingsById.TryGetValue(assetId, out listing);
        }

        /// <summary>
        /// Nákup z trhu (snižuje nabídku, zvyšuje poptávku, vrací celkovou cenu transakce s poplatkem).
        /// </summary>
        public float ExecuteBuy(string assetId, float amount, float taxOrFeeRate = 0f)
        {
            if (amount <= 0f || !listingsById.TryGetValue(assetId, out MarketListing<T> listing)) return 0f;

            listing.Supply = Mathf.Max(0f, listing.Supply - amount);
            listing.Demand += amount;

            float subtotal = amount * listing.CurrentPrice;
            return subtotal * (1f + Mathf.Max(0f, taxOrFeeRate));
        }

        /// <summary>
        /// Prodej na trh (zvyšuje nabídku, mírní poptávku, vrací čistý zisk po odečtení daně/cla).
        /// </summary>
        public float ExecuteSell(string assetId, float amount, float taxOrFeeRate = 0f)
        {
            if (amount <= 0f || !listingsById.TryGetValue(assetId, out MarketListing<T> listing)) return 0f;

            listing.Supply += amount;
            listing.Demand = Mathf.Max(0f, listing.Demand - (amount * 0.5f));

            float subtotal = amount * listing.CurrentPrice;
            return subtotal * Mathf.Clamp01(1f - taxOrFeeRate);
        }

        /// <summary>
        /// Přepočet všech cen v tomto sektoru (Zero-GC smyčka přes indexovaný List).
        /// </summary>
        public void TickMarket(float globalInflation, float deltaTime)
        {
            int count = activeListings.Count;
            for (int i = 0; i < count; i++)
            {
                MarketListing<T> listing = activeListings[i];
                float oldPrice = listing.CurrentPrice;
                float newPrice = pricingStrategy.CalculateNewPrice(listing, globalInflation, deltaTime);

                listing.SetPrice(newPrice);

                // Postupné vracení krátkodobé poptávky k normálu (decay), pokud nikdo nenakupuje
                listing.Demand = Mathf.Lerp(listing.Demand, listing.EquilibriumSupply, deltaTime * 0.1f);

                if (Mathf.Abs(newPrice - oldPrice) > 0.001f)
                {
                    OnPriceChanged?.Invoke(listing, oldPrice, newPrice);
                }
            }
        }
    }

    // =========================================================================
    // 6. HLAVNÍ MANAŽER EKONOMIKY (MONOBEHAVIOUR)
    // =========================================================================

    public class DynamicMarketEconomy : MonoBehaviour
    {
        [Header("Globální makroekonomické parametry")]
        [SerializeField, Range(0.1f, 10f)] private float tickIntervalSeconds = 1.0f;
        [SerializeField, Min(0.1f)] private float globalInflation = 1.0f;
        [SerializeField, Range(0f, 0.5f)] private float tradeTaxRate = 0.05f;
        [SerializeField, Range(8, 512)] private int priceHistoryLength = 128;

        [Header("Výchozí suroviny a komodity (ScriptableObjects)")]
        [SerializeField] private List<PhysicalResourceAsset> initialResources = new List<PhysicalResourceAsset>();

        [Header("Výchozí akciový trh (Stocks)")]
        [SerializeField] private List<StockAsset> initialStocks = new List<StockAsset>();

        // Předpřipravené tržní sektory
        public MarketPool<PhysicalResourceAsset> ResourceMarket { get; private set; }
        public MarketPool<StockAsset> StockMarket { get; private set; }

        // Slovník pro registraci jakýchkoliv budoucích vlastních trhů (např. dluhopisy, krypto, pozemky)
        private readonly Dictionary<Type, object> customMarkets = new Dictionary<Type, object>();

        private float tickTimer;

        public float GlobalInflation
        {
            get => globalInflation;
            set => globalInflation = Mathf.Max(0.01f, value);
        }

        public float TradeTaxRate
        {
            get => tradeTaxRate;
            set => tradeTaxRate = Mathf.Clamp01(value);
        }

        private void Awake()
        {
            // Inicializace komoditního a akciového trhu s jejich vlastními strategiemi
            ResourceMarket = new MarketPool<PhysicalResourceAsset>(new PhysicalResourcePricingStrategy());
            StockMarket = new MarketPool<StockAsset>(new StockPricingStrategy());

            RegisterCustomMarket(ResourceMarket);
            RegisterCustomMarket(StockMarket);

            // Registrace surovin z Inspectoru
            for (int i = 0; i < initialResources.Count; i++)
            {
                if (initialResources[i] != null)
                {
                    ResourceMarket.RegisterAsset(initialResources[i], 1000f, 1000f, priceHistoryLength);
                }
            }

            // Registrace akcií z Inspectoru
            for (int i = 0; i < initialStocks.Count; i++)
            {
                if (initialStocks[i] != null && !string.IsNullOrEmpty(initialStocks[i].AssetId))
                {
                    StockMarket.RegisterAsset(initialStocks[i], 1000f, 1000f, priceHistoryLength);
                }
            }
        }

        private void Update()
        {
            tickTimer += Time.deltaTime;
            if (tickTimer >= tickIntervalSeconds)
            {
                StepEconomy(tickTimer);
                tickTimer = 0f;
            }
        }

        /// <summary>
        /// Provede jeden ekonomický krok. Lze volat i ručně z externího TickSimulationManageru.
        /// </summary>
        public void StepEconomy(float elapsedSeconds)
        {
            ResourceMarket.TickMarket(globalInflation, elapsedSeconds);
            StockMarket.TickMarket(globalInflation, elapsedSeconds);
        }

        /// <summary>
        /// Umožňuje v budoucnu zaregistrovat úplně nový typ trhu (např. MarketPool<BondAsset> nebo MarketPool<LandPlotAsset>).
        /// </summary>
        public void RegisterCustomMarket<T>(MarketPool<T> marketPool) where T : ITradableAsset
        {
            customMarkets[typeof(T)] = marketPool;
        }

        /// <summary>
        /// Vrátí tržní pool pro libovolný typ aktiva T.
        /// </summary>
        public MarketPool<T> GetMarket<T>() where T : ITradableAsset
        {
            if (customMarkets.TryGetValue(typeof(T), out object pool))
            {
                return (MarketPool<T>)pool;
            }
            return null;
        }
    }
}