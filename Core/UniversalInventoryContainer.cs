using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Events;
using GameSystems.Persistence;

namespace GameSystems.Inventory
{
    // =========================================================================
    // 1. VÝČTOVÉ TYPY A STRUKTURY POLOŽEK (ITEM ENUMS & STRUCTURES)
    // =========================================================================

    /// <summary>
    /// Kategorie předmětů adaptované z původního inventáře MiskaandKyberkant
    /// s rozšířením pro tovární a logistické suroviny.
    /// </summary>
    public enum ItemCategory
    {
        Item,
        Weapon,
        Hat,
        Chest,
        Boots,
        Ring,
        Resource,
        Tool,
        Consumable,
        Material,
        Quest
    }

    /// <summary>
    /// Vzácnost předmětu (Rarity) převzatá z MiskaandKyberkant.
    /// </summary>
    public enum ItemRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    /// <summary>
    /// Typ bonusové statistiky předmětu převzatý z MiskaandKyberkant.
    /// </summary>
    public enum ItemStatType
    {
        Health,
        Damage,
        Speed,
        LifeSteal,
        AttackSpeed,
        Armor,
        Penetration,
        CriticalChance,
        Durability,
        Efficiency
    }

    /// <summary>
    /// Bonus ke statistice (např. +15 Damage, +5 Speed).
    /// </summary>
    [Serializable]
    public class StatBonus
    {
        public ItemStatType statType;
        public float value;

        public StatBonus() { }
        public StatBonus(ItemStatType type, float val)
        {
            statType = type;
            value = val;
        }
    }

    /// <summary>
    /// Volitelná metadata položky ve formě klíč-hodnota pro uložení enchantů, jména tvůrce apod.
    /// </summary>
    [Serializable]
    public struct ItemMetadataEntry
    {
        public string Key;
        public string Value;

        public ItemMetadataEntry(string key, string value)
        {
            Key = key;
            Value = value;
        }
    }

    // =========================================================================
    // 2. DATOVÁ TŘÍDA PŘEDMĚTU (INVENTORY ITEM MODEL)
    // =========================================================================

    /// <summary>
    /// Reprezentace konkrétního předmětu v inventáři.
    /// Zachovává dynamickou povahu z projektu MiskaandKyberkant (volné statistiky,
    /// munice, efekty) a doplňuje váhu, hodnotu a stohovatelnost.
    /// </summary>
    [Serializable]
    public class InventoryItem
    {
        [Tooltip("Jednoznačný identifikátor typu položky (např. 'res_iron_ore', 'sword_excalibur').")]
        public string itemId = "item_default";

        [Tooltip("Zobrazovaný název předmětu.")]
        public string itemName = "New Item";

        [Tooltip("Podrobný popis předmětu.")]
        public string description = string.Empty;

        [Tooltip("Kategorie předmětu.")]
        public ItemCategory category = ItemCategory.Item;

        [Tooltip("Stupeň vzácnosti.")]
        public ItemRarity rarity = ItemRarity.Common;

        [Min(1), Tooltip("Aktuální počet kusů ve stohu (Stack Quantity).")]
        public int quantity = 1;

        [Min(1), Tooltip("Maximální počet kusů v jednom stohu (1 = nestohovatelné).")]
        public int maxStackSize = 999;

        [Min(0f), Tooltip("Hmotnost jednoho kusu v kilogramech.")]
        public float unitWeight = 0.5f;

        [Min(0f), Tooltip("Základní tržní hodnota jednoho kusu.")]
        public float unitValue = 10f;

        [Tooltip("Aktuální odolnost / trvanlivost předmětu.")]
        public float durability = 100f;

        [Tooltip("Maximální odolnost předmětu.")]
        public float maxDurability = 100f;

        [Tooltip("Bonusy ke statistikám (převzato z MiskaandKyberkant).")]
        public List<StatBonus> stats = new List<StatBonus>();

        [Tooltip("Dodatečná uživatelská metadata.")]
        public List<ItemMetadataEntry> metadata = new List<ItemMetadataEntry>();

        // Unikátní ID konkrétní instance položky
        [NonSerialized]
        public string instanceId;

        public float TotalWeight => unitWeight * quantity;
        public float TotalValue => unitValue * quantity;
        public bool IsStackable => maxStackSize > 1;

        public InventoryItem()
        {
            instanceId = $"{itemId}_{Guid.NewGuid():N}";
        }

        public InventoryItem(string id, string name, ItemCategory cat, int qty = 1, float weight = 0.5f, float val = 10f)
        {
            itemId = string.IsNullOrEmpty(id) ? "item_default" : id;
            itemName = string.IsNullOrEmpty(name) ? itemId : name;
            category = cat;
            quantity = Mathf.Max(1, qty);
            unitWeight = Mathf.Max(0f, weight);
            unitValue = Mathf.Max(0f, val);
            instanceId = $"{itemId}_{Guid.NewGuid():N}";
        }

        public float GetStat(ItemStatType statType)
        {
            if (stats == null) return 0f;
            for (int i = 0; i < stats.Count; i++)
            {
                if (stats[i].statType == statType)
                    return stats[i].value;
            }
            return 0f;
        }

        public void SetStat(ItemStatType statType, float val)
        {
            if (stats == null) stats = new List<StatBonus>();
            for (int i = 0; i < stats.Count; i++)
            {
                if (stats[i].statType == statType)
                {
                    stats[i].value = val;
                    return;
                }
            }
            stats.Add(new StatBonus(statType, val));
        }

        public string GetMetadata(string key, string fallback = "")
        {
            if (metadata == null) return fallback;
            for (int i = 0; i < metadata.Count; i++)
            {
                if (metadata[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return metadata[i].Value;
            }
            return fallback;
        }

        public void SetMetadata(string key, string value)
        {
            if (metadata == null) metadata = new List<ItemMetadataEntry>();
            for (int i = 0; i < metadata.Count; i++)
            {
                if (metadata[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    metadata[i] = new ItemMetadataEntry(key, value);
                    return;
                }
            }
            metadata.Add(new ItemMetadataEntry(key, value));
        }

        public InventoryItem Clone(int newQuantity = -1)
        {
            var copy = new InventoryItem
            {
                itemId = this.itemId,
                itemName = this.itemName,
                description = this.description,
                category = this.category,
                rarity = this.rarity,
                quantity = newQuantity > 0 ? newQuantity : this.quantity,
                maxStackSize = this.maxStackSize,
                unitWeight = this.unitWeight,
                unitValue = this.unitValue,
                durability = this.durability,
                maxDurability = this.maxDurability,
                instanceId = $"{this.itemId}_{Guid.NewGuid():N}",
                stats = new List<StatBonus>(this.stats.Count),
                metadata = new List<ItemMetadataEntry>(this.metadata.Count)
            };

            for (int i = 0; i < this.stats.Count; i++)
            {
                copy.stats.Add(new StatBonus(this.stats[i].statType, this.stats[i].value));
            }
            for (int i = 0; i < this.metadata.Count; i++)
            {
                copy.metadata.Add(new ItemMetadataEntry(this.metadata[i].Key, this.metadata[i].Value));
            }

            return copy;
        }
    }

    // =========================================================================
    // 3. UDÁLOST SBĚRNICE ZEROGCEVENTBUS (INVENTORY EVENT)
    // =========================================================================

    public enum InventoryChangeType
    {
        Added,
        Removed,
        Transferred,
        Cleared
    }

    /// <summary>
    /// Struct událost publikovaná přes ZeroGCEventBus při každé změně obsahu inventáře.
    /// Umožňuje reaktivní překreslení UI Toolkit panelů bez vazby na logiku.
    /// </summary>
    public readonly struct InventoryChangedEvent : IGameEvent
    {
        public readonly UniversalInventoryContainer Container;
        public readonly string ItemId;
        public readonly int DeltaQuantity;
        public readonly InventoryChangeType ChangeType;

        public InventoryChangedEvent(UniversalInventoryContainer container, string itemId, int delta, InventoryChangeType type)
        {
            Container = container;
            ItemId = itemId;
            DeltaQuantity = delta;
            ChangeType = type;
        }
    }

    // =========================================================================
    // 4. HLAVNÍ KOMPONENTA KONTEJNERU (UNIVERSAL INVENTORY CONTAINER)
    // =========================================================================

    /// <summary>
    /// Univerzální, na UI nezávislý inventární a skladovací kontejner založený na List&lt;InventoryItem&gt;.
    /// Převzato a zdokonaleno z projektu MiskaandKyberkant.
    /// Plně integrovaný s SaveLoadSerializer (ISaveableSystem), ProductionChainNode a LogisticsJobSystem.
    /// </summary>
    [SelectionBase]
    [DisallowMultipleComponent]
    public class UniversalInventoryContainer : MonoBehaviour, ISaveableSystem
    {
        // =====================================================================
        // INSPEKTOR A NASTAVENÍ
        // =====================================================================

        [Header("Identifikace Skladu (Container Identity)")]
        [Tooltip("Unikátní ID kontejneru pro ukládání do save souboru.")]
        [SerializeField] private string containerId = "Inventory_Chest_01";

        [Tooltip("Uživatelsky přívětivé jméno skladu/batohu.")]
        [SerializeField] private string containerDisplayName = "Warehouse Storage";

        [Header("Kapacitní Limity (Container Limits)")]
        [Min(0), Tooltip("Maximální počet položek v listu (0 = neomezeno).")]
        [SerializeField] private int maxItemCount = 32;

        [Min(0f), Tooltip("Maximální celková nosnost skladu v kg (0 = neomezeno).")]
        [SerializeField] private float maxTotalWeight = 250f;

        [Tooltip("Povolit automatické slučování položek do stohů (Stacking).")]
        [SerializeField] private bool allowStacking = true;

        [Header("Položky Inventáře (Item Storage)")]
        [SerializeField] private List<InventoryItem> items = new List<InventoryItem>(32);

        // =====================================================================
        // VEŘEJNÉ VLASTNOSTI (PUBLIC PROPERTIES)
        // =====================================================================

        public string ContainerId { get => containerId; set => containerId = value; }
        public string ContainerDisplayName { get => containerDisplayName; set => containerDisplayName = value; }
        public int MaxItemCount { get => maxItemCount; set => maxItemCount = Mathf.Max(0, value); }
        public float MaxTotalWeight { get => maxTotalWeight; set => maxTotalWeight = Mathf.Max(0f, value); }
        public bool AllowStacking { get => allowStacking; set => allowStacking = value; }
        public IReadOnlyList<InventoryItem> Items => items;
        public int ItemCount => items.Count;

        // ISaveableSystem klíč
        public string SaveKey => string.IsNullOrEmpty(containerId) ? $"Inventory_{gameObject.name}_{gameObject.GetInstanceID()}" : containerId;

        private void Awake()
        {
            if (items == null) items = new List<InventoryItem>(maxItemCount > 0 ? maxItemCount : 16);
            EnsureInstanceIds();
        }

        private void EnsureInstanceIds()
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && string.IsNullOrEmpty(items[i].instanceId))
                {
                    items[i].instanceId = $"{items[i].itemId}_{Guid.NewGuid():N}";
                }
            }
        }

        // =====================================================================
        // MUTACE A MANIPULACE S POLOŽKAMI (MUTATION API - ZERO-GC)
        // =====================================================================

        /// <summary>
        /// Přidá předmět do inventáře.
        /// Pokud je povoleno stohování, přednostně naplní existující neplné stohy stejného itemId.
        /// Zbytek vytvoří jako nový záznam v seznamu.
        /// </summary>
        public bool AddItem(InventoryItem item, out int remainingUnadded)
        {
            remainingUnadded = item != null ? item.quantity : 0;
            if (item == null || item.quantity <= 0) return false;

            // Kontrola limitu váhy
            if (maxTotalWeight > 0f)
            {
                float currentWeight = GetTotalWeight();
                float freeWeight = maxTotalWeight - currentWeight;
                if (freeWeight <= 0.0001f)
                    return false;

                if (item.unitWeight > 0f)
                {
                    int maxAffordableQty = Mathf.FloorToInt(freeWeight / item.unitWeight);
                    if (maxAffordableQty <= 0)
                        return false;

                    if (item.quantity > maxAffordableQty)
                    {
                        // Přidáme jen tolik, kolik unese nosnost
                        int toAdd = maxAffordableQty;
                        var partialItem = item.Clone(toAdd);
                        bool res = AddItemInternal(partialItem, out int unaddedPartial);
                        int addedCount = toAdd - unaddedPartial;
                        item.quantity -= addedCount;
                        remainingUnadded = item.quantity;
                        return res;
                    }
                }
            }

            return AddItemInternal(item, out remainingUnadded);
        }

        private bool AddItemInternal(InventoryItem item, out int remainingUnadded)
        {
            int originalQty = item.quantity;
            remainingUnadded = item.quantity;

            // 1. Pokus o stohování do stávajících položek
            if (allowStacking && item.IsStackable)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var existing = items[i];
                    if (existing != null && existing.itemId.Equals(item.itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        int freeStackSpace = existing.maxStackSize - existing.quantity;
                        if (freeStackSpace > 0)
                        {
                            int transfer = Mathf.Min(freeStackSpace, remainingUnadded);
                            existing.quantity += transfer;
                            remainingUnadded -= transfer;

                            if (remainingUnadded <= 0)
                            {
                                item.quantity = 0;
                                ZeroGCEventBus.Raise(new InventoryChangedEvent(this, item.itemId, originalQty, InventoryChangeType.Added));
                                return true;
                            }
                        }
                    }
                }
            }

            // 2. Vytvoření nových slotů pro zbývající počet kusů
            while (remainingUnadded > 0)
            {
                if (maxItemCount > 0 && items.Count >= maxItemCount)
                {
                    // Dosažen limit počtu položek v listu
                    int actuallyAdded = originalQty - remainingUnadded;
                    if (actuallyAdded > 0)
                    {
                        ZeroGCEventBus.Raise(new InventoryChangedEvent(this, item.itemId, actuallyAdded, InventoryChangeType.Added));
                    }
                    return actuallyAdded > 0;
                }

                int chunkSize = item.IsStackable ? Mathf.Min(item.maxStackSize, remainingUnadded) : 1;
                var newItemChunk = item.Clone(chunkSize);
                items.Add(newItemChunk);
                remainingUnadded -= chunkSize;
            }

            item.quantity = 0;
            ZeroGCEventBus.Raise(new InventoryChangedEvent(this, item.itemId, originalQty, InventoryChangeType.Added));
            return true;
        }

        /// <summary>
        /// Pohodlné vytvoření a přidání suroviny dle ID (např. tovární výstup 'res_steel').
        /// </summary>
        public bool AddItem(string itemId, int quantity = 1, ItemCategory category = ItemCategory.Resource, float unitWeight = 0.5f, float unitVal = 10f)
        {
            var newItem = new InventoryItem(itemId, itemId, category, quantity, unitWeight, unitVal);
            return AddItem(newItem, out _);
        }

        /// <summary>
        /// Odebere zadané množství dané položky z inventáře dle reference.
        /// </summary>
        public bool RemoveItem(InventoryItem item, int quantity = 1)
        {
            if (item == null || quantity <= 0) return false;

            int idx = items.IndexOf(item);
            if (idx < 0) return false;

            if (item.quantity <= quantity)
            {
                int removedQty = item.quantity;
                string removedId = item.itemId;
                items.RemoveAt(idx);
                ZeroGCEventBus.Raise(new InventoryChangedEvent(this, removedId, -removedQty, InventoryChangeType.Removed));
                return true;
            }

            item.quantity -= quantity;
            ZeroGCEventBus.Raise(new InventoryChangedEvent(this, item.itemId, -quantity, InventoryChangeType.Removed));
            return true;
        }

        /// <summary>
        /// Odebere celkové množství položek dle itemId napříč všemi stohy v listu.
        /// </summary>
        public bool RemoveItem(string itemId, int quantity = 1)
        {
            if (string.IsNullOrEmpty(itemId) || quantity <= 0) return false;
            if (CountByItemId(itemId) < quantity) return false;

            int remainingToRemove = quantity;
            for (int i = items.Count - 1; i >= 0 && remainingToRemove > 0; i--)
            {
                var it = items[i];
                if (it != null && it.itemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                {
                    if (it.quantity <= remainingToRemove)
                    {
                        remainingToRemove -= it.quantity;
                        items.RemoveAt(i);
                    }
                    else
                    {
                        it.quantity -= remainingToRemove;
                        remainingToRemove = 0;
                    }
                }
            }

            ZeroGCEventBus.Raise(new InventoryChangedEvent(this, itemId, -quantity, InventoryChangeType.Removed));
            return true;
        }

        /// <summary>
        /// Odstraní položku na konkrétním indexu v listu.
        /// </summary>
        public bool RemoveAt(int index)
        {
            if (index < 0 || index >= items.Count) return false;
            var it = items[index];
            string id = it?.itemId ?? string.Empty;
            int qty = it?.quantity ?? 0;
            items.RemoveAt(index);
            ZeroGCEventBus.Raise(new InventoryChangedEvent(this, id, -qty, InventoryChangeType.Removed));
            return true;
        }

        /// <summary>
        /// Vyprázdní celý obsah skladu.
        /// </summary>
        public void Clear()
        {
            items.Clear();
            ZeroGCEventBus.Raise(new InventoryChangedEvent(this, string.Empty, 0, InventoryChangeType.Cleared));
        }

        // =====================================================================
        // DOTAZOVÁNÍ (ZERO-GC QUERIES)
        // =====================================================================

        /// <summary>
        /// Ověří přítomnost minimálního počtu kusů daného itemId v inventáři.
        /// </summary>
        public bool ContainsItem(string itemId, int minQuantity = 1)
        {
            return CountByItemId(itemId) >= minQuantity;
        }

        /// <summary>
        /// Sečte všechny kusy daného itemId napříč celým listem (Zero-GC).
        /// </summary>
        public int CountByItemId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;
            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null && it.itemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                {
                    total += it.quantity;
                }
            }
            return total;
        }

        /// <summary>
        /// Filtruje položky dané kategorie do předem alokovaného bufferu bez alokace na haldě.
        /// </summary>
        public void GetItemsByCategory(ItemCategory category, List<InventoryItem> resultsBuffer)
        {
            if (resultsBuffer == null) return;
            resultsBuffer.Clear();

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null && it.category == category)
                {
                    resultsBuffer.Add(it);
                }
            }
        }

        /// <summary>
        /// Spočítá celkovou hmotnost všech položek v kontejneru.
        /// </summary>
        public float GetTotalWeight()
        {
            float total = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null)
                {
                    total += it.TotalWeight;
                }
            }
            return total;
        }

        /// <summary>
        /// Spočítá celkovou tržní hodnotu všech položek v kontejneru.
        /// </summary>
        public float GetTotalValue()
        {
            float total = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null)
                {
                    total += it.TotalValue;
                }
            }
            return total;
        }

        public bool HasCapacityFor(string itemId, int quantity = 1)
        {
            if (maxItemCount <= 0) return true;
            if (items.Count < maxItemCount) return true;

            if (allowStacking)
            {
                int remaining = quantity;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it != null && it.itemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                    {
                        int free = it.maxStackSize - it.quantity;
                        if (free > 0)
                        {
                            remaining -= free;
                            if (remaining <= 0) return true;
                        }
                    }
                }
            }
            return false;
        }

        // =====================================================================
        // INTEGRACE S REPOZITÁŘEM: LOGISTIKA A VÝROBA (ECOSYSTEM ADAPTERS)
        // =====================================================================

        /// <summary>
        /// Přemístí zadané množství suroviny z tohoto kontejneru do cílového kontejneru.
        /// Ideální pro výrobní linky (ProductionChainNode) a logistické dělníky (LogisticsJobSystem).
        /// </summary>
        public bool TryTransferTo(UniversalInventoryContainer target, string itemId, int quantity)
        {
            if (target == null || string.IsNullOrEmpty(itemId) || quantity <= 0)
                return false;

            if (CountByItemId(itemId) < quantity)
                return false;

            if (!target.HasCapacityFor(itemId, quantity))
                return false;

            // Vyhledání vzorové položky pro zachování jejích atributů
            InventoryItem template = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].itemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                {
                    template = items[i];
                    break;
                }
            }

            if (template == null) return false;

            var transferItem = template.Clone(quantity);
            if (target.AddItem(transferItem, out int remainingUnadded))
            {
                int transferred = quantity - remainingUnadded;
                if (transferred > 0)
                {
                    RemoveItem(itemId, transferred);
                    ZeroGCEventBus.Raise(new ResourceTransferredEvent(itemId, transferred, transform.position, target.transform.position));
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Metoda požadovaná ProductionChainNode a LogisticsJobSystem pro dotaz na dostupnou zásobu suroviny.
        /// </summary>
        public int GetAvailableSupply(string resourceId)
        {
            return CountByItemId(resourceId);
        }

        /// <summary>
        /// Vrátí počet zbývajících volných slotů v kontejneru (nebo int.MaxValue pokud není limit).
        /// </summary>
        public int GetRemainingCapacity()
        {
            if (maxItemCount <= 0) return int.MaxValue;
            return Mathf.Max(0, maxItemCount - items.Count);
        }

        /// <summary>
        /// Vrátí zbývající volnou nosnost v kg (nebo float.MaxValue pokud není limit).
        /// </summary>
        public float GetRemainingWeightCapacity()
        {
            if (maxTotalWeight <= 0f) return float.MaxValue;
            return Mathf.Max(0f, maxTotalWeight - GetTotalWeight());
        }

        // =====================================================================
        // UKLÁDÁNÍ A NAČÍTÁNÍ (SAVELOADSERIALIZER / ISAVEABLESYSTEM)
        // =====================================================================

        [Serializable]
        private class InventorySaveData
        {
            public string ContainerId;
            public string ContainerDisplayName;
            public int MaxItemCount;
            public float MaxTotalWeight;
            public List<InventoryItem> Items = new List<InventoryItem>();
        }

        public object CaptureState()
        {
            var data = new InventorySaveData
            {
                ContainerId = this.containerId,
                ContainerDisplayName = this.containerDisplayName,
                MaxItemCount = this.maxItemCount,
                MaxTotalWeight = this.maxTotalWeight,
                Items = new List<InventoryItem>(this.items.Count)
            };

            for (int i = 0; i < this.items.Count; i++)
            {
                if (this.items[i] != null)
                {
                    data.Items.Add(this.items[i].Clone());
                }
            }

            return data;
        }

        public void RestoreState(string stateJson)
        {
            if (string.IsNullOrEmpty(stateJson)) return;

            try
            {
                var data = JsonUtility.FromJson<InventorySaveData>(stateJson);
                if (data != null)
                {
                    this.containerId = data.ContainerId;
                    this.containerDisplayName = data.ContainerDisplayName;
                    this.maxItemCount = data.MaxItemCount;
                    this.maxTotalWeight = data.MaxTotalWeight;

                    this.items.Clear();
                    if (data.Items != null)
                    {
                        for (int i = 0; i < data.Items.Count; i++)
                        {
                            var it = data.Items[i];
                            if (it != null)
                            {
                                it.instanceId = $"{it.itemId}_{Guid.NewGuid():N}";
                                this.items.Add(it);
                            }
                        }
                    }

                    ZeroGCEventBus.Raise(new InventoryChangedEvent(this, string.Empty, 0, InventoryChangeType.Added));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UniversalInventoryContainer] Selhalo obnovení stavu skladu {SaveKey}: {ex.Message}");
            }
        }
    }
}
