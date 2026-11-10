using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;
using GameSystems.Simulation;
using GameSystems.Inventory;
using GameSystems.PhysicsAndCombat;

namespace GameSystems.Economy
{
    // =========================================================================
    // 1. CONTRABAND & CUSTOMS ENUMS AND STRUCTS
    // =========================================================================

    public enum ContrabandCategory
    {
        Narcotics,
        IllegalWeapons,
        CounterfeitCurrency,
        StolenRelics,
        ExoticAlienTech,
        BiohazardSpecimen
    }

    public enum ScannerTechnologyLevel
    {
        ManualSearchDog = 1,
        XRayBackscatterScanner = 2,
        NeutronResonanceScanner = 3,
        QuantumSpectrometer = 4
    }

    public enum InspectionOutcome
    {
        ClearedNoContraband,
        ClearedBribeAccepted,
        ContrabandSeizedFined,
        BribeRejectedArrestTriggered
    }

    [Serializable]
    public class ContrabandDefinition
    {
        public string ItemId = "item_narcotics";
        public string DisplayName = "Red Nebula Dust";
        public ContrabandCategory Category = ContrabandCategory.Narcotics;
        [Range(1, 5)] public int IllegalityTier = 2;
        [Range(0.1f, 1f)] public float BaseDetectionDifficulty = 0.5f;
        [Min(1f)] public float BlackMarketPriceMultiplier = 3.5f;
        public float ConfiscationFineCredits = 5000f;
    }

    // =========================================================================
    // 2. ZERO-GC EVENT BUS STRUCTS
    // =========================================================================

    public readonly struct ContrabandDetectedEvent : IGameEvent
    {
        public readonly int CheckpointId;
        public readonly int SmugglerId;
        public readonly string ContrabandItemId;
        public readonly float DetectionProbability;

        public ContrabandDetectedEvent(int cpId, int smugglerId, string itemId, float prob)
        {
            CheckpointId = cpId;
            SmugglerId = smugglerId;
            ContrabandItemId = itemId;
            DetectionProbability = prob;
        }
    }

    public readonly struct CargoInspectionPassedEvent : IGameEvent
    {
        public readonly int CheckpointId;
        public readonly int VehicleId;
        public readonly bool WasBribeAccepted;

        public CargoInspectionPassedEvent(int cpId, int vehicleId, bool bribe)
        {
            CheckpointId = cpId;
            VehicleId = vehicleId;
            WasBribeAccepted = bribe;
        }
    }

    public readonly struct ContrabandConfiscatedEvent : IGameEvent
    {
        public readonly int CheckpointId;
        public readonly string ItemId;
        public readonly int Quantity;
        public readonly float FineLevied;

        public ContrabandConfiscatedEvent(int cpId, string itemId, int qty, float fine)
        {
            CheckpointId = cpId;
            ItemId = itemId;
            Quantity = qty;
            FineLevied = fine;
        }
    }

    // =========================================================================
    // 3. SMUGGLER CONCEALED COMPARTMENT COMPONENT
    // =========================================================================

    /// <summary>
    /// Attach to vehicles, shipping containers, or player inventory to add concealed,
    /// lead/EM-shielded cargo compartments and forged shipping manifests.
    /// </summary>
    [DisallowMultipleComponent]
    public class ConcealedCargoCompartment : MonoBehaviour
    {
        [Header("Concealment & Shielding")]
        [Tooltip("Weight capacity of the hidden compartment")]
        public float HiddenCapacityWeightKg = 150f;
        [Tooltip("Lead shielding rating (0.0 = none, 1.0 = completely blocks X-rays)")]
        [Range(0f, 1f)] public float LeadShieldingRating = 0.65f;
        [Tooltip("Electromagnetic shielding rating (blocks Radar/EM scanners)")]
        [Range(0f, 1f)] public float EMShieldingRating = 0.50f;

        [Header("Forged Shipping Manifest")]
        public bool HasForgedManifest = true;
        [Range(0.1f, 1f)] public float ManifestForgeryQuality = 0.80f;
        public List<string> DeclaredLegitimateItems = new List<string>();

        [Header("Inventory Link")]
        public UniversalInventoryContainer LinkedInventory;

        private void Awake()
        {
            if (LinkedInventory == null)
                LinkedInventory = GetComponent<UniversalInventoryContainer>();
        }
    }

    // =========================================================================
    // 4. MASTER CUSTOMS & CONTRABAND INSPECTION SIMULATOR (2D & 3D COMPATIBLE)
    // =========================================================================

    /// <summary>
    /// Smuggling, border checkpoint, customs scanner, and bribery simulator.
    /// Models detection probability curves, lead/EM shielding, forged manifests,
    /// inspector suspicion/integrity, cargo confiscation, and black-market pricing.
    /// Interconnects with UniversalInventoryContainer, RadarAndEMSignatureSim, and LogisticsJobSystem.
    /// Compatible with both 2D and 3D border checkpoint gates.
    /// </summary>
    [DisallowMultipleComponent]
    public class ContrabandAndCustomsInspectionSim : MonoBehaviour, ITickable, ISaveableSystem
    {
        public static ContrabandAndCustomsInspectionSim Instance { get; private set; }

        [Header("Checkpoint Hardware & Location")]
        [SerializeField] private int checkpointId = 1;
        [SerializeField] private string checkpointName = "Sector 7 Border Gate";
        [SerializeField] private Vector3 checkpointPosition;
        [SerializeField] private float inspectionRadiusMeters = 8.0f;
        [SerializeField] private ScannerTechnologyLevel scannerTech = ScannerTechnologyLevel.XRayBackscatterScanner;

        [Header("Inspector AI & Integrity")]
        [Range(0f, 100f)] [SerializeField] private float inspectorSuspicionPercent = 25f;
        [Tooltip("Inspector integrity: 0.0 = completely corruptible, 1.0 = incorruptible")]
        [Range(0f, 1f)] [SerializeField] private float inspectorIntegrity = 0.60f;

        [Header("Contraband Database")]
        [SerializeField] private List<ContrabandDefinition> contrabandDatabase = new List<ContrabandDefinition>();

        [Header("Spatial Mode")]
        [SerializeField] private bool is2DMode = false;

        private readonly Dictionary<string, ContrabandDefinition> contrabandLookup = new Dictionary<string, ContrabandDefinition>(StringComparer.Ordinal);
        private readonly List<InventoryItem> tempItemBuffer = new List<InventoryItem>(32);

        public string SaveKey => $"CustomsCheckpoint_{checkpointId}";
        public int CheckpointId => checkpointId;
        public string CheckpointName => checkpointName;
        public float Suspicion => inspectorSuspicionPercent;

        private void Awake()
        {
            if (Instance == null) Instance = this;

            checkpointPosition = transform.position;
            RebuildDatabase();
        }

        private void OnEnable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.RegisterTickable(this, TickChannel.Normal);
        }

        private void OnDisable()
        {
            if (TickSimulationManager.Instance != null)
                TickSimulationManager.Instance.UnregisterTickable(this, TickChannel.Normal);
        }

        public void RebuildDatabase()
        {
            contrabandLookup.Clear();
            for (int i = 0; i < contrabandDatabase.Count; i++)
            {
                var def = contrabandDatabase[i];
                contrabandLookup[def.ItemId] = def;
            }

            // Seed default contrabands if empty
            if (contrabandDatabase.Count == 0)
            {
                AddDefaultContraband("res_spice", "Prohibited Spice", ContrabandCategory.Narcotics, 3, 0.6f, 4f, 8000f);
                AddDefaultContraband("res_plasma_blaster", "Unregistered Plasma Rifle", ContrabandCategory.IllegalWeapons, 4, 0.4f, 5f, 15000f);
                AddDefaultContraband("res_counterfeit_creds", "Forged Credit Chips", ContrabandCategory.CounterfeitCurrency, 2, 0.7f, 2.5f, 5000f);
            }
        }

        private void AddDefaultContraband(string id, string name, ContrabandCategory cat, int tier, float diff, float mult, float fine)
        {
            var def = new ContrabandDefinition
            {
                ItemId = id,
                DisplayName = name,
                Category = cat,
                IllegalityTier = tier,
                BaseDetectionDifficulty = diff,
                BlackMarketPriceMultiplier = mult,
                ConfiscationFineCredits = fine
            };
            contrabandDatabase.Add(def);
            contrabandLookup[id] = def;
        }

        // =========================================================================
        // PUBLIC API: INSPECTION & BRIBERY SOLVER
        // =========================================================================

        public InspectionOutcome InspectVehicle(ConcealedCargoCompartment vehicle, float offeredBribeCredits = 0f)
        {
            if (vehicle == null || vehicle.LinkedInventory == null)
            {
                EventBus<CargoInspectionPassedEvent>.Raise(new CargoInspectionPassedEvent(checkpointId, 0, false));
                return InspectionOutcome.ClearedNoContraband;
            }

            int vehicleId = vehicle.gameObject.GetInstanceID();

            // 1. Evaluate Bribery Attempt
            if (offeredBribeCredits > 0f)
            {
                // Bribe acceptance probability depends on offered credits vs inspector integrity
                float requiredBribe = (1f - inspectorIntegrity) * 20000f + 1000f;
                bool bribeAccepted = (offeredBribeCredits >= requiredBribe) && (inspectorIntegrity < 0.95f);

                if (bribeAccepted)
                {
                    inspectorSuspicionPercent = Mathf.Max(0f, inspectorSuspicionPercent - 20f);
                    EventBus<CargoInspectionPassedEvent>.Raise(new CargoInspectionPassedEvent(checkpointId, vehicleId, true));
                    return InspectionOutcome.ClearedBribeAccepted;
                }
                else
                {
                    // Bribery rejected! Immediate escalation
                    inspectorSuspicionPercent = 100f;
                    EventBus<ContrabandConfiscatedEvent>.Raise(new ContrabandConfiscatedEvent(
                        checkpointId, "BRIBE_ATTEMPT", 1, offeredBribeCredits * 2f));
                    return InspectionOutcome.BribeRejectedArrestTriggered;
                }
            }

            // 2. Scan Vehicle Inventory for Contraband
            tempItemBuffer.Clear();
            var itemsList = vehicle.LinkedInventory.Items;
            for (int slot = 0; slot < itemsList.Count; slot++)
            {
                var item = itemsList[slot];
                if (item != null && item.quantity > 0)
                {
                    tempItemBuffer.Add(item);
                }
            }

            bool contrabandFound = false;

            for (int i = 0; i < tempItemBuffer.Count; i++)
            {
                var item = tempItemBuffer[i];
                if (contrabandLookup.TryGetValue(item.itemId, out var contrabandDef))
                {
                    // Compute physical detection probability curve:
                    // P_detect = (Tech / 4) * (Suspicion / 100) * (1 - Shielding) * (1 - Forgery) / Difficulty
                    float techFactor = (int)scannerTech * 0.25f;
                    float suspicionFactor = inspectorSuspicionPercent / 100f;
                    float shieldingDamping = 1f - Mathf.Clamp01(vehicle.LeadShieldingRating * 0.6f + vehicle.EMShieldingRating * 0.4f);
                    float forgeryDamping = vehicle.HasForgedManifest ? (1f - vehicle.ManifestForgeryQuality * 0.5f) : 1f;

                    float pDetect = techFactor * suspicionFactor * shieldingDamping * forgeryDamping / Mathf.Max(0.1f, contrabandDef.BaseDetectionDifficulty);
                    pDetect = Mathf.Clamp(pDetect, 0.05f, 0.99f);

                    if (UnityEngine.Random.value < pDetect)
                    {
                        // Detected! Confiscate item and levy fine
                        contrabandFound = true;
                        float totalFine = contrabandDef.ConfiscationFineCredits * item.quantity;

                        vehicle.LinkedInventory.RemoveItem(item, item.quantity);

                        EventBus<ContrabandDetectedEvent>.Raise(new ContrabandDetectedEvent(
                            checkpointId, vehicleId, item.itemId, pDetect));
                        EventBus<ContrabandConfiscatedEvent>.Raise(new ContrabandConfiscatedEvent(
                            checkpointId, item.itemId, item.quantity, totalFine));

                        inspectorSuspicionPercent = 100f;
                        return InspectionOutcome.ContrabandSeizedFined;
                    }
                }
            }

            // Inspection passed cleanly
            inspectorSuspicionPercent = Mathf.Max(10f, inspectorSuspicionPercent - 5f);
            EventBus<CargoInspectionPassedEvent>.Raise(new CargoInspectionPassedEvent(checkpointId, vehicleId, false));
            return InspectionOutcome.ClearedNoContraband;
        }

        // =========================================================================
        // SIMULATION TICK LOOP (CHECKPOINT PROXIMITY AUTOSCAN)
        // =========================================================================

        public void OnSimulationTick(int tickIndex, float tickDeltaTime)
        {
            Vector3 center = checkpointPosition;

            // Decay inspector suspicion slowly over time
            inspectorSuspicionPercent = Mathf.MoveTowards(inspectorSuspicionPercent, 20f, 2f * tickDeltaTime);

            // Check for nearby vehicles passing through inspection gate
            var compartments = FindObjectsByType<ConcealedCargoCompartment>(FindObjectsSortMode.None);
            for (int i = 0; i < compartments.Length; i++)
            {
                var vehicle = compartments[i];
                float dist = is2DMode ?
                    Vector2.Distance(center, vehicle.transform.position) :
                    Vector3.Distance(center, vehicle.transform.position);

                if (dist <= inspectionRadiusMeters)
                {
                    // Automatic customs scan
                    InspectVehicle(vehicle, 0f);
                }
            }
        }

        // =========================================================================
        // SAVE & RESTORE (ISaveableSystem)
        // =========================================================================

        [Serializable]
        private class CustomsSaveData
        {
            public int Id;
            public float Suspicion;
            public float Integrity;
        }

        public object CaptureState()
        {
            return new CustomsSaveData
            {
                Id = checkpointId,
                Suspicion = inspectorSuspicionPercent,
                Integrity = inspectorIntegrity
            };
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;
            var data = JsonUtility.FromJson<CustomsSaveData>(stateJson);
            if (data == null) return;

            checkpointId = data.Id;
            inspectorSuspicionPercent = data.Suspicion;
            inspectorIntegrity = data.Integrity;
        }

        // =========================================================================
        // GIZMOS & 2D/3D VISUAL TELEMETRY
        // =========================================================================

        private void OnDrawGizmos()
        {
            Vector3 center = checkpointPosition != Vector3.zero ? checkpointPosition : transform.position;

            Gizmos.color = inspectorSuspicionPercent > 60f ? Color.red : Color.yellow;
            if (is2DMode)
            {
                Gizmos.DrawWireSphere(center, inspectionRadiusMeters);
                Gizmos.DrawWireCube(center, new Vector3(2f, 4f, 0.1f));
            }
            else
            {
                Gizmos.DrawWireSphere(center, inspectionRadiusMeters);
                Gizmos.DrawWireCube(center, new Vector3(4f, 4f, 2f));
            }
        }
    }
}
