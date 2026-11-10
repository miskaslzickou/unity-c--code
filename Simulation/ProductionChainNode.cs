using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Simulation;
using GameSystems.Utilities;
using GameSystems.Logistics;
using GameSystems.Tactical;

namespace GameSystems.Factory
{
    // =========================================================================
    // 1. DATOVÝ PŘEDPIS VÝROBNÍHO RECEPTU (PRODUCTION RECIPE SCRIPTABLE OBJECT)
    // =========================================================================

    [Serializable]
    public struct RecipeItem
    {
        public string ResourceId;
        public float Amount;
    }

    /// <summary>
    /// Recept pro továrny, rafinerie a dílny.
    /// Definuje vstupy, výstupy (včetně vedlejších produktů/odpadu), čas a požadavky na energie a dělníky.
    /// </summary>
    [CreateAssetMenu(fileName = "NewProductionRecipe", menuName = "Factory/Production Recipe")]
    public class ProductionRecipeSO : ScriptableObject
    {
        [Header("Identifikace")]
        [SerializeField] private string recipeId = "recipe_steel_ingot";
        [SerializeField] private string recipeName = "Smelt Steel Ingot";

        [Header("Vstupní suroviny")]
        [SerializeField] private RecipeItem[] inputs = new RecipeItem[]
        {
            new RecipeItem { ResourceId = "res_iron_ore", Amount = 2f },
            new RecipeItem { ResourceId = "res_coal", Amount = 1f }
        };

        [Header("Výstupy (produkty a vedlejší zplodiny)")]
        [SerializeField] private RecipeItem[] outputs = new RecipeItem[]
        {
            new RecipeItem { ResourceId = "res_steel", Amount = 1f }
        };

        [Header("Čas a provozní nároky")]
        [SerializeField, Min(0.1f)] private float craftTimeSeconds = 4f;
        [SerializeField, Min(0f)] private float powerDemandKW = 25f;
        [SerializeField, Min(0f)] private float waterDemandLiters = 0f;
        [SerializeField, Range(0, 50)] private int requiredWorkers = 4;
        [SerializeField, Range(0f, 10f)] private float pollutionEmitted = 1.5f;

        public string RecipeId => recipeId;
        public string RecipeName => recipeName;
        public RecipeItem[] Inputs => inputs;
        public RecipeItem[] Outputs => outputs;
        public float CraftTimeSeconds => craftTimeSeconds;
        public float PowerDemandKW => powerDemandKW;
        public float WaterDemandLiters => waterDemandLiters;
        public int RequiredWorkers => requiredWorkers;
        public float PollutionEmitted => pollutionEmitted;
    }

    public enum FactoryStatus
    {
        IdleNoRecipe,
        StarvedForInputs,
        Unpowered,
        Understaffed,
        Crafting,
        StalledOutputFull
    }

    // =========================================================================
    // 2. HLAVNÍ VÝROBNÍ UZEL (PRODUCTION CHAIN NODE)
    // =========================================================================

    /// <summary>
    /// Univerzální výrobní uzel pro továrny, hutě, rafinerie a dílny.
    /// Spravuje inventární zásobníky vstupů i výstupů, dynamicky škáluje efektivitu
    /// podle ResourceNetworkGraph (proud/voda) a dělníků a automaticky napojuje
    /// požadavky a zásoby do LogisticsJobSystem (zero-GC).
    /// </summary>
    [DisallowMultipleComponent]
    public class ProductionChainNode : MonoBehaviour, ITickable
    {
        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při zahájení výrobního cyklu receptu.</summary>
        public event Action<ProductionRecipeSO> OnCraftStarted;

        /// <summary>Vyvoláno po úspěšném dokončení výrobního cyklu.</summary>
        public event Action<ProductionRecipeSO> OnCraftCompleted;

        /// <summary>Vyvoláno při změně stavu továrny (výpadek proudu, chybějící suroviny, plný sklad).</summary>
        public event Action<FactoryStatus> OnStatusChanged;

        /// <summary>Vyvoláno při změně celkové provozní efektivity (0.0 až 1.0+).</summary>
        public event Action<float> OnEfficiencyChanged;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Aktivní recept")]
        [SerializeField] private ProductionRecipeSO currentRecipe = null;

        [Header("Integrace s TickSimulationManager")]
        [SerializeField] private bool autoRegisterWithTickManager = true;
        [SerializeField] private TickChannel tickChannel = TickChannel.Normal;

        [Header("Pracovní síla a kapacita")]
        [SerializeField, Range(0, 100)] private int currentWorkers = 4;
        [SerializeField, Min(1f)] private float bufferMultiplier = 5f; // Kolikrát více surovin se vejde do zásobníku oproti receptu

        [Header("Automatická logistika (LogisticsJobSystem)")]
        [SerializeField] private bool autoHookLogistics = true;
        [SerializeField, Range(0.1f, 0.9f)] private float restockThresholdRatio = 0.5f;

        [Header("Ladění a diagnostika")]
        [SerializeField] private FactoryStatus currentStatus = FactoryStatus.IdleNoRecipe;
        [SerializeField, Range(0f, 1f)] private float craftProgress = 0f;
        [SerializeField] private float effectiveEfficiency = 1f;

        // =========================================================================
        // VNITŘNÍ INVENTÁRNÍ ZÁSOBNÍKY (ZERO-GC)
        // =========================================================================

        private readonly Dictionary<string, float> inputBuffers = new Dictionary<string, float>(8);
        private readonly Dictionary<string, float> outputBuffers = new Dictionary<string, float>(8);

        // Registrované logistické uzly
        private readonly Dictionary<string, LogisticsRequester> logisticsRequesters = new Dictionary<string, LogisticsRequester>(8);
        private readonly Dictionary<string, LogisticsProvider> logisticsProviders = new Dictionary<string, LogisticsProvider>(8);

        // Síťový uzel v ResourceNetworkGraph
        private int networkNodeId = -1;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public ProductionRecipeSO CurrentRecipe => currentRecipe;
        public FactoryStatus Status => currentStatus;
        public float CraftProgress => craftProgress;
        public float EffectiveEfficiency => effectiveEfficiency;
        public int CurrentWorkers => currentWorkers;

        // =========================================================================
        // UNITY ŽIVOTNÍ CYKLUS
        // =========================================================================

        private void Awake()
        {
            InitializeBuffers();
        }

        private void Start()
        {
            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.RegisterTickable(this, tickChannel);
            }

            // Registrace odběru energie v ResourceNetworkGraph
            RegisterUtilityConsumer();

            // Nastavení logistických bran
            if (autoHookLogistics)
            {
                SetupLogisticsNodes();
            }
        }

        private void OnDestroy()
        {
            if (autoRegisterWithTickManager && TickSimulationManager.Instance != null)
            {
                TickSimulationManager.Instance.UnregisterTickable(this, tickChannel);
            }

            if (networkNodeId != -1 && ResourceNetworkGraph.Instance != null)
            {
                ResourceNetworkGraph.Instance.UnregisterNode(networkNodeId);
            }

            TearDownLogisticsNodes();
        }

        // =========================================================================
        // INICIALIZACE A SPRÁVA RECEPTŮ
        // =========================================================================

        public void InitializeBuffers()
        {
            inputBuffers.Clear();
            outputBuffers.Clear();

            if (currentRecipe != null)
            {
                for (int i = 0; i < currentRecipe.Inputs.Length; i++)
                {
                    inputBuffers[currentRecipe.Inputs[i].ResourceId] = 0f;
                }
                for (int i = 0; i < currentRecipe.Outputs.Length; i++)
                {
                    outputBuffers[currentRecipe.Outputs[i].ResourceId] = 0f;
                }
            }
        }

        public void SetRecipe(ProductionRecipeSO newRecipe)
        {
            currentRecipe = newRecipe;
            craftProgress = 0f;
            InitializeBuffers();

            if (autoHookLogistics)
            {
                TearDownLogisticsNodes();
                SetupLogisticsNodes();
            }

            UpdateUtilityDemand();
        }

        // =========================================================================
        // ITICKABLE IMPLEMENTACE: HLAVNÍ VÝROBNÍ CYKLUS
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            StepProduction(tickDeltaTime);
        }

        public void StepProduction(float dt)
        {
            if (currentRecipe == null)
            {
                SetStatus(FactoryStatus.IdleNoRecipe);
                return;
            }

            // 1. Výpočet provozní efektivity (Energie ze sítě * Dělníci)
            float powerSatisfaction = 1f;
            if (networkNodeId != -1 && ResourceNetworkGraph.Instance != null)
            {
                powerSatisfaction = ResourceNetworkGraph.Instance.GetNodeSatisfaction(networkNodeId);
            }

            if (powerSatisfaction < 0.05f)
            {
                SetStatus(FactoryStatus.Unpowered);
                effectiveEfficiency = 0f;
                return;
            }

            float workforceRatio = currentRecipe.RequiredWorkers > 0
                ? Mathf.Clamp01((float)currentWorkers / currentRecipe.RequiredWorkers)
                : 1f;

            if (workforceRatio < 0.05f)
            {
                SetStatus(FactoryStatus.Understaffed);
                effectiveEfficiency = 0f;
                return;
            }

            float newEfficiency = powerSatisfaction * workforceRatio;
            if (Mathf.Abs(newEfficiency - effectiveEfficiency) > 0.01f)
            {
                effectiveEfficiency = newEfficiency;
                OnEfficiencyChanged?.Invoke(effectiveEfficiency);
            }

            // 2. Kontrola volného místa ve výstupních zásobnících
            if (!HasRoomForOutputs())
            {
                SetStatus(FactoryStatus.StalledOutputFull);
                return;
            }

            // 3. Kontrola dostatku vstupních surovin pro započetí/pokračování
            if (!HasInputIngredients())
            {
                SetStatus(FactoryStatus.StarvedForInputs);
                return;
            }

            // 4. Aktivní výroba (Crafting)
            SetStatus(FactoryStatus.Crafting);

            if (craftProgress <= 0.001f)
            {
                OnCraftStarted?.Invoke(currentRecipe);
            }

            craftProgress += (dt * effectiveEfficiency) / currentRecipe.CraftTimeSeconds;

            // 5. Dokončení výrobního cyklu
            if (craftProgress >= 1.0f)
            {
                CompleteCraftCycle();
                craftProgress = 0f;
            }

            // 6. Aktualizace logistických poptávek
            if (autoHookLogistics)
            {
                UpdateLogisticsThresholds();
            }
        }

        // =========================================================================
        // DOKONČENÍ VÝROBNÍHO KROKU (SPOTŘEBA, VÝROBA, EMITOVÁNÍ ZNEČIŠTĚNÍ)
        // =========================================================================

        private void CompleteCraftCycle()
        {
            // Spotřeba vstupů
            for (int i = 0; i < currentRecipe.Inputs.Length; i++)
            {
                RecipeItem inItem = currentRecipe.Inputs[i];
                inputBuffers[inItem.ResourceId] = Mathf.Max(0f, inputBuffers[inItem.ResourceId] - inItem.Amount);
            }

            // Přidání výstupů
            for (int i = 0; i < currentRecipe.Outputs.Length; i++)
            {
                RecipeItem outItem = currentRecipe.Outputs[i];
                outputBuffers[outItem.ResourceId] = outputBuffers[outItem.ResourceId] + outItem.Amount;
            }

            // Vypuštění smogu a znečištění (propojení s buňkovým automatem a influence mapou)
            if (currentRecipe.PollutionEmitted > 0.01f)
            {
                if (CellularAutomataSim.Instance != null)
                {
                    CellularAutomataSim.Instance.AddPollutionWorld(transform.position, currentRecipe.PollutionEmitted);
                }
                if (InfluenceMap.Instance != null)
                {
                    InfluenceMap.Instance.StampInfluence(InfluenceLayerType.Pollution, transform.position, currentRecipe.PollutionEmitted, 5f);
                }
            }

            OnCraftCompleted?.Invoke(currentRecipe);
        }

        // =========================================================================
        // KONTROLY ZÁSOBNÍKŮ
        // =========================================================================

        private bool HasInputIngredients()
        {
            for (int i = 0; i < currentRecipe.Inputs.Length; i++)
            {
                RecipeItem item = currentRecipe.Inputs[i];
                if (!inputBuffers.TryGetValue(item.ResourceId, out float current) || current < item.Amount)
                {
                    return false;
                }
            }
            return true;
        }

        private bool HasRoomForOutputs()
        {
            for (int i = 0; i < currentRecipe.Outputs.Length; i++)
            {
                RecipeItem item = currentRecipe.Outputs[i];
                float maxCap = item.Amount * bufferMultiplier;
                if (outputBuffers.TryGetValue(item.ResourceId, out float current) && current + item.Amount > maxCap)
                {
                    return false;
                }
            }
            return true;
        }

        private void SetStatus(FactoryStatus status)
        {
            if (currentStatus != status)
            {
                currentStatus = status;
                OnStatusChanged?.Invoke(currentStatus);
            }
        }

        // =========================================================================
        // AUTOMATICKÉ PROPOJENÍ S RESOURCE NETWORK GRAPH A LOGISTICS JOB SYSTEM
        // =========================================================================

        private void RegisterUtilityConsumer()
        {
            if (ResourceNetworkGraph.Instance == null || currentRecipe == null) return;

            networkNodeId = ResourceNetworkGraph.Instance.RegisterNode(
                UtilityType.Electricity,
                UtilityNodeType.Consumer,
                transform.position,
                prod: 0f,
                demand: currentRecipe.PowerDemandKW,
                userData: this
            );
        }

        private void UpdateUtilityDemand()
        {
            if (networkNodeId != -1 && ResourceNetworkGraph.Instance != null && currentRecipe != null)
            {
                ResourceNetworkGraph.Instance.SetNodeDemand(networkNodeId, currentRecipe.PowerDemandKW);
            }
        }

        private void SetupLogisticsNodes()
        {
            if (LogisticsJobSystem.Instance == null || currentRecipe == null) return;

            // Požadavky na vstupy
            for (int i = 0; i < currentRecipe.Inputs.Length; i++)
            {
                RecipeItem item = currentRecipe.Inputs[i];
                float maxCapacity = item.Amount * bufferMultiplier;
                var req = LogisticsJobSystem.Instance.RegisterRequester(transform.position, item.ResourceId, maxCapacity, priority: 3);
                logisticsRequesters[item.ResourceId] = req;
            }

            // Nabídka výstupů pro odvoz
            for (int i = 0; i < currentRecipe.Outputs.Length; i++)
            {
                RecipeItem item = currentRecipe.Outputs[i];
                var prov = LogisticsJobSystem.Instance.RegisterProvider(transform.position, item.ResourceId, 0f);
                logisticsProviders[item.ResourceId] = prov;
            }
        }

        private void TearDownLogisticsNodes()
        {
            if (LogisticsJobSystem.Instance == null) return;

            foreach (var req in logisticsRequesters.Values) LogisticsJobSystem.Instance.UnregisterRequester(req);
            foreach (var prov in logisticsProviders.Values) LogisticsJobSystem.Instance.UnregisterProvider(prov);

            logisticsRequesters.Clear();
            logisticsProviders.Clear();
        }

        private void UpdateLogisticsThresholds()
        {
            // Aktualizace poptávky podle aktuálního stavu vstupního zásobníku s prahem doplnění
            foreach (var kvp in logisticsRequesters)
            {
                string resId = kvp.Key;
                LogisticsRequester req = kvp.Value;
                if (inputBuffers.TryGetValue(resId, out float currentStock))
                {
                    req.CurrentAmount = currentStock;
                    // Pokud zásoba klesne pod práh, aktivuje se objednávka na zbytek kapacity
                    bool needsRestock = currentStock <= (req.DesiredAmount * restockThresholdRatio);
                    if (!needsRestock)
                    {
                        req.CurrentAmount = req.DesiredAmount; // Dočasně utlumí poptávku
                    }
                }
            }

            // Aktualizace nabídky hotových výrobků k odvozu
            foreach (var kvp in logisticsProviders)
            {
                string resId = kvp.Key;
                LogisticsProvider prov = kvp.Value;
                if (outputBuffers.TryGetValue(resId, out float currentStock))
                {
                    prov.AvailableStock = currentStock;
                }
            }
        }

        // =========================================================================
        // VEŘEJNÉ API PRO RUČNÍ NEBO EXTERNÍ PŘÍSTUP K INVENTÁŘŮM
        // =========================================================================

        public bool TryAddInputResource(string resourceId, float amount, out float accepted)
        {
            accepted = 0f;
            if (currentRecipe == null) return false;

            for (int i = 0; i < currentRecipe.Inputs.Length; i++)
            {
                if (currentRecipe.Inputs[i].ResourceId == resourceId)
                {
                    float maxCapacity = currentRecipe.Inputs[i].Amount * bufferMultiplier;
                    float current = inputBuffers[resourceId];
                    float room = Mathf.Max(0f, maxCapacity - current);

                    accepted = Mathf.Min(room, amount);
                    inputBuffers[resourceId] += accepted;
                    return true;
                }
            }
            return false;
        }

        public bool TryTakeOutputResource(string resourceId, float amount, out float taken)
        {
            taken = 0f;
            if (outputBuffers.TryGetValue(resourceId, out float current))
            {
                taken = Mathf.Min(current, amount);
                outputBuffers[resourceId] -= taken;
                return true;
            }
            return false;
        }

        public float GetInputStock(string resourceId)
        {
            inputBuffers.TryGetValue(resourceId, out float val);
            return val;
        }

        public float GetOutputStock(string resourceId)
        {
            outputBuffers.TryGetValue(resourceId, out float val);
            return val;
        }

        public void SetWorkers(int count)
        {
            currentWorkers = Mathf.Max(0, count);
        }
    }
}
