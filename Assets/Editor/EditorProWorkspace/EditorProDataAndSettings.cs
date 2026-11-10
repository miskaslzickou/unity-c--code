using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace EditorProWorkspace
{
    /// <summary>
    /// Lightweight attribute to decorate ScriptableObject classes with a default category, icon, and tag color.
    /// Example: [EditorProCategory("Weapons", "d_CustomTool", "#E74C3C")]
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class EditorProCategoryAttribute : Attribute
    {
        public string CategoryName { get; }
        public string IconName { get; }
        public string ColorHex { get; }

        public EditorProCategoryAttribute(string categoryName, string iconName = "d_ScriptableObject Icon", string colorHex = "#3498DB")
        {
            CategoryName = string.IsNullOrEmpty(categoryName) ? "Uncategorized" : categoryName;
            IconName = string.IsNullOrEmpty(iconName) ? "d_ScriptableObject Icon" : iconName;
            ColorHex = string.IsNullOrEmpty(colorHex) ? "#3498DB" : colorHex;
        }
    }

    /// <summary>
    /// Interface that ScriptableObjects can implement to expose custom subtitle text, status badges, and colors in the workspace browser.
    /// </summary>
    public interface IEditorProBadgeProvider
    {
        /// <summary>
        /// Text displayed inside the badge pill (e.g. "Rare", "v1.2", "WIP"). Return null or empty if no badge.
        /// </summary>
        string GetBadgeText();

        /// <summary>
        /// Color used for the badge background / border.
        /// </summary>
        Color GetBadgeColor();

        /// <summary>
        /// Secondary subtitle text displayed underneath the asset name (e.g. "Damage: 45 | Weight: 3.2").
        /// </summary>
        string GetSubtitle();
    }

    /// <summary>
    /// Browser view presentation mode.
    /// </summary>
    public enum WorkspaceViewMode
    {
        Grid,
        List
    }

    /// <summary>
    /// Asset sorting modes in browser pane.
    /// </summary>
    public enum AssetSortMode
    {
        NameAsc,
        NameDesc,
        TypeAsc,
        TypeDesc,
        SizeAsc,
        SizeDesc,
        PathAsc
    }

    /// <summary>
    /// Data container for user-created custom categories.
    /// </summary>
    [Serializable]
    public class EditorProCategory
    {
        [SerializeField] private string id;
        [SerializeField] private string name;
        [SerializeField] private string iconName;
        [SerializeField] private string colorHex;
        [SerializeField] private List<string> assetGuids = new List<string>();

        public string Id => id;
        public string Name { get => name; set => name = value; }
        public string IconName { get => iconName; set => iconName = value; }
        public string ColorHex { get => colorHex; set => colorHex = value; }
        public IReadOnlyList<string> AssetGuids => assetGuids;

        public EditorProCategory()
        {
            id = Guid.NewGuid().ToString("N");
            name = "New Category";
            iconName = "d_Folder Icon";
            colorHex = "#3498DB";
            assetGuids = new List<string>();
        }

        public EditorProCategory(string name, string iconName = "d_Folder Icon", string colorHex = "#3498DB")
        {
            this.id = Guid.NewGuid().ToString("N");
            this.name = string.IsNullOrEmpty(name) ? "New Category" : name;
            this.iconName = string.IsNullOrEmpty(iconName) ? "d_Folder Icon" : iconName;
            this.colorHex = string.IsNullOrEmpty(colorHex) ? "#3498DB" : colorHex;
            this.assetGuids = new List<string>();
        }

        public bool AddAsset(string guid)
        {
            if (string.IsNullOrEmpty(guid) || assetGuids.Contains(guid))
                return false;
            assetGuids.Add(guid);
            return true;
        }

        public bool RemoveAsset(string guid)
        {
            return assetGuids.Remove(guid);
        }

        public bool ContainsAsset(string guid)
        {
            return !string.IsNullOrEmpty(guid) && assetGuids.Contains(guid);
        }

        public void ClearAssets()
        {
            assetGuids.Clear();
        }

        public void PruneMissingAssets(Func<string, bool> assetExistsPredicate)
        {
            if (assetExistsPredicate == null) return;
            assetGuids.RemoveAll(guid => !assetExistsPredicate(guid));
        }
    }

    /// <summary>
    /// Persistent settings and state for EditorProWorkspace.
    /// Saves to ProjectSettings/EditorProSettings.asset via ScriptableSingleton
    /// and synchronizes with ProjectSettings/EditorProSettings.json for human-readable version control.
    /// </summary>
    [FilePath("ProjectSettings/EditorProSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class EditorProSettings : ScriptableSingleton<EditorProSettings>
    {
        public const string JsonSettingsPath = "ProjectSettings/EditorProSettings.json";

        [SerializeField] private List<string> favorites = new List<string>();
        [SerializeField] private List<string> recentAssets = new List<string>();
        [SerializeField] private List<EditorProCategory> customCategories = new List<EditorProCategory>();
        [SerializeField] private List<string> excludedFolders = new List<string> { "Assets/Plugins", "Assets/ThirdParty" };
        [SerializeField] private List<string> recentCreatedTypeNames = new List<string>();
        [SerializeField] private string defaultCreationPath = "Assets";
        [SerializeField] private WorkspaceViewMode viewMode = WorkspaceViewMode.Grid;
        [SerializeField] private float gridItemSize = 110f;
        [SerializeField] private AssetSortMode sortMode = AssetSortMode.NameAsc;

        [SerializeField] private List<string> openTabGuids = new List<string>();
        [SerializeField] private List<string> pinnedTabGuids = new List<string>();
        [SerializeField] private string activeTabGuid = string.Empty;
        [SerializeField] private bool inspectorSplitView = false;
        [SerializeField] private string secondaryTabGuid = string.Empty;

        [SerializeField] private string activeSection = "All"; // "All", "Favorites", "Recent", "Type:<typeName>", "Category:<catId>"
        [SerializeField] private string searchQuery = string.Empty;

        public event Action OnSettingsChanged;

        public IReadOnlyList<string> Favorites => favorites;
        public IReadOnlyList<string> RecentAssets => recentAssets;
        public IReadOnlyList<EditorProCategory> CustomCategories => customCategories;
        public IReadOnlyList<string> ExcludedFolders => excludedFolders;
        public IReadOnlyList<string> RecentCreatedTypeNames => recentCreatedTypeNames;
        public string DefaultCreationPath { get => defaultCreationPath; set => defaultCreationPath = value; }
        public WorkspaceViewMode ViewMode { get => viewMode; set => viewMode = value; }
        public float GridItemSize { get => gridItemSize; set => gridItemSize = Mathf.Clamp(value, 80f, 180f); }
        public AssetSortMode SortMode { get => sortMode; set => sortMode = value; }
        public IReadOnlyList<string> OpenTabGuids => openTabGuids;
        public IReadOnlyList<string> PinnedTabGuids => pinnedTabGuids;
        public string ActiveTabGuid => activeTabGuid;
        public bool InspectorSplitView => inspectorSplitView;
        public string SecondaryTabGuid => secondaryTabGuid;
        public string ActiveSection { get => activeSection; set => activeSection = value; }
        public string SearchQuery { get => searchQuery; set => searchQuery = value; }

        private void OnEnable()
        {
            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            if (customCategories == null) customCategories = new List<EditorProCategory>();
            if (favorites == null) favorites = new List<string>();
            if (recentAssets == null) recentAssets = new List<string>();
            if (excludedFolders == null) excludedFolders = new List<string> { "Assets/Plugins", "Assets/ThirdParty" };
            if (recentCreatedTypeNames == null) recentCreatedTypeNames = new List<string>();
            if (openTabGuids == null) openTabGuids = new List<string>();
            if (pinnedTabGuids == null) pinnedTabGuids = new List<string>();

            // Attempt to restore from JSON if asset file was fresh or empty
            if (customCategories.Count == 0 && favorites.Count == 0 && File.Exists(JsonSettingsPath))
            {
                LoadFromJson();
            }

            // Create default custom categories if completely new project
            if (customCategories.Count == 0)
            {
                customCategories.Add(new EditorProCategory("Items", "d_PreMatCube", "#3498DB"));
                customCategories.Add(new EditorProCategory("Config", "d_SettingsIcon", "#9B59B6"));
            }
        }

        public void SaveSettings()
        {
            try
            {
                Save(true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EditorProWorkspace] Failed to save ScriptableSingleton: {ex.Message}");
            }

            SyncToJson();
            OnSettingsChanged?.Invoke();
        }

        private void SyncToJson()
        {
            try
            {
                string json = EditorJsonUtility.ToJson(this, true);
                string dir = Path.GetDirectoryName(JsonSettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(JsonSettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EditorProWorkspace] Failed to write {JsonSettingsPath}: {ex.Message}");
            }
        }

        private void LoadFromJson()
        {
            try
            {
                if (File.Exists(JsonSettingsPath))
                {
                    string json = File.ReadAllText(JsonSettingsPath);
                    if (!string.IsNullOrEmpty(json))
                    {
                        EditorJsonUtility.FromJsonOverwrite(json, this);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EditorProWorkspace] Failed to load {JsonSettingsPath}: {ex.Message}");
            }
        }

        // --- Favorites ---
        public bool IsFavorite(string guid)
        {
            return !string.IsNullOrEmpty(guid) && favorites.Contains(guid);
        }

        public void ToggleFavorite(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (favorites.Contains(guid))
                favorites.Remove(guid);
            else
                favorites.Add(guid);

            SaveSettings();
        }

        // --- Recent Assets ---
        public bool IsRecent(string guid)
        {
            return !string.IsNullOrEmpty(guid) && recentAssets.Contains(guid);
        }

        public void RecordRecent(string guid, bool save = true)
        {
            if (string.IsNullOrEmpty(guid)) return;
            recentAssets.Remove(guid);
            recentAssets.Insert(0, guid);
            if (recentAssets.Count > 30)
            {
                recentAssets.RemoveRange(30, recentAssets.Count - 30);
            }
            if (save) SaveSettings();
        }

        public void ClearRecent()
        {
            recentAssets.Clear();
            SaveSettings();
        }

        // --- Custom Categories ---
        public EditorProCategory AddCategory(string name, string iconName = "d_Folder Icon", string colorHex = "#3498DB")
        {
            var cat = new EditorProCategory(name, iconName, colorHex);
            customCategories.Add(cat);
            SaveSettings();
            return cat;
        }

        public bool RemoveCategory(string categoryId)
        {
            int idx = customCategories.FindIndex(c => c.Id == categoryId);
            if (idx >= 0)
            {
                customCategories.RemoveAt(idx);
                if (activeSection == $"Category:{categoryId}")
                {
                    activeSection = "All";
                }
                SaveSettings();
                return true;
            }
            return false;
        }

        public EditorProCategory GetCategory(string categoryId)
        {
            return customCategories.Find(c => c.Id == categoryId);
        }

        public bool AddAssetToCategory(string categoryId, string guid)
        {
            var cat = GetCategory(categoryId);
            if (cat != null && cat.AddAsset(guid))
            {
                SaveSettings();
                return true;
            }
            return false;
        }

        public bool RemoveAssetFromCategory(string categoryId, string guid)
        {
            var cat = GetCategory(categoryId);
            if (cat != null && cat.RemoveAsset(guid))
            {
                SaveSettings();
                return true;
            }
            return false;
        }

        // --- Excluded Folders ---
        public bool IsFolderExcluded(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = assetPath.Replace('\\', '/');
            for (int i = 0; i < excludedFolders.Count; i++)
            {
                string excl = excludedFolders[i].TrimEnd('/').Replace('\\', '/');
                if (normalized.StartsWith(excl + "/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals(excl, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public void AddExcludedFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            string normalized = folderPath.Replace('\\', '/');
            if (!excludedFolders.Contains(normalized))
            {
                excludedFolders.Add(normalized);
                SaveSettings();
            }
        }

        public void RemoveExcludedFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            string normalized = folderPath.Replace('\\', '/');
            if (excludedFolders.Remove(normalized))
            {
                SaveSettings();
            }
        }

        // --- Recent Created Types ---
        public void RecordCreatedType(string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName)) return;
            recentCreatedTypeNames.Remove(typeFullName);
            recentCreatedTypeNames.Insert(0, typeFullName);
            if (recentCreatedTypeNames.Count > 10)
            {
                recentCreatedTypeNames.RemoveRange(10, recentCreatedTypeNames.Count - 10);
            }
            SaveSettings();
        }

        // --- Tab Management ---
        public void OpenTab(string guid, bool pin = false, bool save = true)
        {
            if (string.IsNullOrEmpty(guid)) return;

            if (!openTabGuids.Contains(guid))
            {
                openTabGuids.Add(guid);
            }

            if (pin && !pinnedTabGuids.Contains(guid))
            {
                pinnedTabGuids.Add(guid);
            }

            activeTabGuid = guid;
            if (save) SaveSettings();
        }

        public void RemoveAssetEverywhere(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            favorites.Remove(guid);
            recentAssets.Remove(guid);
            openTabGuids.Remove(guid);
            pinnedTabGuids.Remove(guid);

            if (activeTabGuid == guid) activeTabGuid = string.Empty;
            if (secondaryTabGuid == guid) secondaryTabGuid = string.Empty;

            for (int i = 0; i < customCategories.Count; i++)
            {
                customCategories[i].RemoveAsset(guid);
            }

            SaveSettings();
        }

        public void CloseTab(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            int idx = openTabGuids.IndexOf(guid);
            openTabGuids.Remove(guid);
            pinnedTabGuids.Remove(guid);

            if (secondaryTabGuid == guid)
            {
                secondaryTabGuid = string.Empty;
            }

            if (activeTabGuid == guid)
            {
                if (openTabGuids.Count > 0)
                {
                    int nextIdx = Mathf.Clamp(idx, 0, openTabGuids.Count - 1);
                    activeTabGuid = openTabGuids[nextIdx];
                }
                else
                {
                    activeTabGuid = string.Empty;
                }
            }

            SaveSettings();
        }

        public void CloseOtherTabs(string guidToKeep)
        {
            openTabGuids.RemoveAll(g => g != guidToKeep && !pinnedTabGuids.Contains(g));
            if (!openTabGuids.Contains(activeTabGuid))
            {
                activeTabGuid = guidToKeep;
            }
            SaveSettings();
        }

        public void CloseAllTabs()
        {
            openTabGuids.RemoveAll(g => !pinnedTabGuids.Contains(g));
            if (!openTabGuids.Contains(activeTabGuid))
            {
                activeTabGuid = openTabGuids.Count > 0 ? openTabGuids[0] : string.Empty;
            }
            SaveSettings();
        }

        public void TogglePinTab(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            if (pinnedTabGuids.Contains(guid))
                pinnedTabGuids.Remove(guid);
            else
                pinnedTabGuids.Add(guid);

            SaveSettings();
        }

        public bool IsTabPinned(string guid)
        {
            return !string.IsNullOrEmpty(guid) && pinnedTabGuids.Contains(guid);
        }

        public void SetActiveTab(string guid)
        {
            if (activeTabGuid != guid)
            {
                activeTabGuid = guid;
                SaveSettings();
            }
        }

        public void SetSecondaryTab(string guid)
        {
            if (secondaryTabGuid != guid)
            {
                secondaryTabGuid = guid;
                SaveSettings();
            }
        }

        public void SetInspectorSplit(bool split)
        {
            if (inspectorSplitView != split)
            {
                inspectorSplitView = split;
                SaveSettings();
            }
        }
    }
}
