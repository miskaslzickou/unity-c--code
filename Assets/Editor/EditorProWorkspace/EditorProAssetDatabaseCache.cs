using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace EditorProWorkspace
{
    /// <summary>
    /// Metadata for a discovered ScriptableObject Type.
    /// </summary>
    public class ScriptableObjectTypeInfo
    {
        public Type Type { get; set; }
        public string FullName { get; set; }
        public string Name { get; set; }
        public string Namespace { get; set; }
        public string AssemblyName { get; set; }
        public bool IsProjectScript { get; set; }
        public string CategoryAttributeName { get; set; }
        public string CategoryAttributeIcon { get; set; }
        public string CategoryAttributeColor { get; set; }
        public Texture2D Icon { get; set; }
        public bool ImplementsBadgeProvider { get; set; }
    }

    /// <summary>
    /// Cached representation of a ScriptableObject asset on disk.
    /// </summary>
    public class ScriptableObjectItemData
    {
        public string Guid { get; set; }
        public string AssetPath { get; set; }
        public string AssetName { get; set; }
        public Type AssetType { get; set; }
        public string TypeName { get; set; }
        public string FolderPath { get; set; }
        public long FileSizeBytes { get; set; }
        public string FormattedFileSize { get; set; }
        public Texture2D Icon { get; set; }
        public string CategoryAttributeName { get; set; }
        public string CategoryAttributeIcon { get; set; }
        public string CategoryAttributeColor { get; set; }
        public string Subtitle { get; set; }
        public string BadgeText { get; set; }
        public Color BadgeColor { get; set; }
        public bool HasBadge { get; set; }
        public DateTime LastModifiedTime { get; set; }
    }

    /// <summary>
    /// High-performance cache and search engine for ScriptableObjects in the project.
    /// Uses TypeCache for instant type discovery and AssetDatabase for metadata caching.
    /// </summary>
    public class EditorProAssetDatabaseCache
    {
        private static EditorProAssetDatabaseCache instance;
        public static EditorProAssetDatabaseCache Instance => instance ?? (instance = new EditorProAssetDatabaseCache());

        public static event Action OnCacheUpdated;

        private readonly Dictionary<string, ScriptableObjectItemData> itemsByGuid = new Dictionary<string, ScriptableObjectItemData>(StringComparer.Ordinal);
        private readonly List<ScriptableObjectItemData> allItems = new List<ScriptableObjectItemData>();
        private readonly List<ScriptableObjectTypeInfo> discoveredTypes = new List<ScriptableObjectTypeInfo>();
        private readonly Dictionary<string, ScriptableObjectTypeInfo> typesByFullName = new Dictionary<string, ScriptableObjectTypeInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptableObjectTypeInfo> typesByName = new Dictionary<string, ScriptableObjectTypeInfo>(StringComparer.OrdinalIgnoreCase);

        private bool isInitialized = false;

        public IReadOnlyList<ScriptableObjectItemData> AllItems => allItems;
        public IReadOnlyList<ScriptableObjectTypeInfo> DiscoveredTypes => discoveredTypes;
        public bool IsInitialized => isInitialized;

        public void InitializeIfNeeded()
        {
            if (!isInitialized)
            {
                Refresh(true);
            }
        }

        /// <summary>
        /// Scans types and assets, rebuilding the internal lookup cache.
        /// </summary>
        public void Refresh(bool forceFullScan = false)
        {
            DiscoverTypes();
            ScanAssets();
            isInitialized = true;
            OnCacheUpdated?.Invoke();
        }

        #region Type Discovery (TypeCache)

        private void DiscoverTypes()
        {
            discoveredTypes.Clear();
            typesByFullName.Clear();
            typesByName.Clear();

            // Discover all types derived from ScriptableObject
            var types = TypeCache.GetTypesDerivedFrom<ScriptableObject>();

            foreach (var type in types)
            {
                if (!IsValidScriptableObjectType(type))
                    continue;

                var typeInfo = CreateTypeInfo(type);
                discoveredTypes.Add(typeInfo);

                if (!string.IsNullOrEmpty(typeInfo.FullName))
                    typesByFullName[typeInfo.FullName] = typeInfo;

                if (!string.IsNullOrEmpty(typeInfo.Name) && !typesByName.ContainsKey(typeInfo.Name))
                    typesByName[typeInfo.Name] = typeInfo;
            }

            // Sort types alphabetically by Name
            discoveredTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsValidScriptableObjectType(Type type)
        {
            if (type == null) return false;
            if (type.IsAbstract || type.IsGenericTypeDefinition || type.ContainsGenericParameters)
                return false;

            // Filter out Unity editor window and internal editor types
            if (typeof(EditorWindow).IsAssignableFrom(type) || typeof(Editor).IsAssignableFrom(type))
                return false;

            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                ns.StartsWith("UnityEditorInternal", StringComparison.Ordinal))
            {
                return false;
            }

            // Filter obsolete types
            if (Attribute.IsDefined(type, typeof(ObsoleteAttribute), false))
                return false;

            // Filter ScriptableSingleton internal types
            Type baseType = type.BaseType;
            while (baseType != null)
            {
                if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(ScriptableSingleton<>))
                    return false;
                baseType = baseType.BaseType;
            }

            return true;
        }

        private ScriptableObjectTypeInfo CreateTypeInfo(Type type)
        {
            var info = new ScriptableObjectTypeInfo
            {
                Type = type,
                FullName = type.FullName,
                Name = type.Name,
                Namespace = string.IsNullOrEmpty(type.Namespace) ? "(Global)" : type.Namespace,
                AssemblyName = type.Assembly.GetName().Name,
                IsProjectScript = IsProjectAssembly(type.Assembly.GetName().Name),
                CategoryAttributeName = string.Empty,
                CategoryAttributeIcon = "d_ScriptableObject Icon",
                CategoryAttributeColor = "#3498DB",
                ImplementsBadgeProvider = typeof(IEditorProBadgeProvider).IsAssignableFrom(type)
            };

            // Check for EditorProCategoryAttribute
            ExtractCategoryAttribute(type, info);

            // Resolve custom icon
            info.Icon = ResolveTypeIcon(info);

            return info;
        }

        private static bool IsProjectAssembly(string asmName)
        {
            if (string.IsNullOrEmpty(asmName)) return false;
            return asmName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
                   asmName.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase) ||
                   !asmName.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) &&
                   !asmName.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase);
        }

        private static void ExtractCategoryAttribute(Type type, ScriptableObjectTypeInfo info)
        {
            try
            {
                // Direct typed check
                var attr = type.GetCustomAttribute<EditorProCategoryAttribute>(true);
                if (attr != null)
                {
                    info.CategoryAttributeName = attr.CategoryName;
                    info.CategoryAttributeIcon = attr.IconName;
                    info.CategoryAttributeColor = attr.ColorHex;
                    return;
                }

                // Fallback reflection check (e.g. if defined in another assembly with same name)
                foreach (var a in type.GetCustomAttributes(true))
                {
                    Type aType = a.GetType();
                    if (aType.Name == nameof(EditorProCategoryAttribute) || aType.Name == "EditorProCategory")
                    {
                        var propCat = aType.GetProperty("CategoryName") ?? aType.GetProperty("categoryName");
                        var propIcon = aType.GetProperty("IconName") ?? aType.GetProperty("iconName");
                        var propColor = aType.GetProperty("ColorHex") ?? aType.GetProperty("colorHex");

                        if (propCat != null) info.CategoryAttributeName = propCat.GetValue(a)?.ToString();
                        if (propIcon != null) info.CategoryAttributeIcon = propIcon.GetValue(a)?.ToString();
                        if (propColor != null) info.CategoryAttributeColor = propColor.GetValue(a)?.ToString();
                        break;
                    }
                }
            }
            catch
            {
                // Ignore reflection errors on dynamically unloaded types
            }
        }

        private static Texture2D ResolveTypeIcon(ScriptableObjectTypeInfo info)
        {
            Texture2D tex = null;
            if (!string.IsNullOrEmpty(info.CategoryAttributeIcon))
            {
                var content = EditorGUIUtility.IconContent(info.CategoryAttributeIcon);
                if (content != null && content.image is Texture2D t2d)
                {
                    tex = t2d;
                }
            }

            if (tex == null && info.Type != null)
            {
                var content = EditorGUIUtility.ObjectContent(null, info.Type);
                if (content != null && content.image is Texture2D t2d)
                {
                    tex = t2d;
                }
            }

            if (tex == null)
            {
                var fallback = EditorGUIUtility.IconContent("d_ScriptableObject Icon");
                if (fallback != null && fallback.image is Texture2D t2d)
                {
                    tex = t2d;
                }
            }

            return tex;
        }

        public ScriptableObjectTypeInfo GetTypeInfo(string typeFullNameOrName)
        {
            if (string.IsNullOrEmpty(typeFullNameOrName)) return null;

            if (typesByFullName.TryGetValue(typeFullNameOrName, out var info))
                return info;

            if (typesByName.TryGetValue(typeFullNameOrName, out info))
                return info;

            return null;
        }

        #endregion

        #region Asset Scanning & Caching

        private void ScanAssets()
        {
            itemsByGuid.Clear();
            allItems.Clear();

            var settings = EditorProSettings.instance;
            string[] guids = AssetDatabase.FindAssets("t:ScriptableObject");

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                // Check folder exclusion
                if (settings.IsFolderExcluded(path))
                    continue;

                // Only evaluate .asset files (or specific asset packages)
                if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Fast check of main asset type without loading entire asset binary
                Type mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (mainType == null || !typeof(ScriptableObject).IsAssignableFrom(mainType))
                    continue;

                if (typeof(EditorWindow).IsAssignableFrom(mainType))
                    continue;

                var item = CreateItemData(guid, path, mainType);
                if (item != null)
                {
                    itemsByGuid[guid] = item;
                    allItems.Add(item);
                }
            }
        }

        private ScriptableObjectItemData CreateItemData(string guid, string path, Type assetType)
        {
            string assetName = Path.GetFileNameWithoutExtension(path);
            string folderPath = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;

            long fileSizeBytes = 0;
            DateTime lastModified = DateTime.MinValue;
            try
            {
                if (File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    fileSizeBytes = fileInfo.Length;
                    lastModified = fileInfo.LastWriteTime;
                }
            }
            catch
            {
                // Fallback on inaccessible files
            }

            var typeInfo = GetTypeInfo(assetType.FullName) ?? GetTypeInfo(assetType.Name);

            var item = new ScriptableObjectItemData
            {
                Guid = guid,
                AssetPath = path,
                AssetName = assetName,
                AssetType = assetType,
                TypeName = assetType.Name,
                FolderPath = folderPath,
                FileSizeBytes = fileSizeBytes,
                FormattedFileSize = FormatBytes(fileSizeBytes),
                LastModifiedTime = lastModified,
                CategoryAttributeName = typeInfo?.CategoryAttributeName ?? string.Empty,
                CategoryAttributeIcon = typeInfo?.CategoryAttributeIcon ?? string.Empty,
                CategoryAttributeColor = typeInfo?.CategoryAttributeColor ?? string.Empty,
                Icon = (AssetDatabase.GetCachedIcon(path) as Texture2D) ?? typeInfo?.Icon
            };

            // Query IEditorProBadgeProvider if applicable
            CheckBadgeProvider(item);

            if (item.Icon == null)
            {
                var fallback = EditorGUIUtility.IconContent("d_ScriptableObject Icon");
                if (fallback != null && fallback.image is Texture2D t2d)
                {
                    item.Icon = t2d;
                }
            }

            return item;
        }

        private void CheckBadgeProvider(ScriptableObjectItemData item)
        {
            if (item.AssetType == null) return;

            bool isProvider = typeof(IEditorProBadgeProvider).IsAssignableFrom(item.AssetType);
            if (!isProvider)
            {
                // Check duck typing if declared in an external assembly
                var method = item.AssetType.GetMethod("GetBadgeText", BindingFlags.Public | BindingFlags.Instance);
                if (method != null) isProvider = true;
            }

            if (isProvider)
            {
                try
                {
                    var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(item.AssetPath);
                    if (asset is IEditorProBadgeProvider provider)
                    {
                        item.BadgeText = provider.GetBadgeText();
                        item.BadgeColor = provider.GetBadgeColor();
                        item.Subtitle = provider.GetSubtitle();
                        item.HasBadge = !string.IsNullOrEmpty(item.BadgeText);
                    }
                    else if (asset != null)
                    {
                        var mText = asset.GetType().GetMethod("GetBadgeText", BindingFlags.Public | BindingFlags.Instance);
                        var mColor = asset.GetType().GetMethod("GetBadgeColor", BindingFlags.Public | BindingFlags.Instance);
                        var mSub = asset.GetType().GetMethod("GetSubtitle", BindingFlags.Public | BindingFlags.Instance);

                        if (mText != null) item.BadgeText = mText.Invoke(asset, null)?.ToString();
                        if (mColor != null && mColor.Invoke(asset, null) is Color c) item.BadgeColor = c;
                        if (mSub != null) item.Subtitle = mSub.Invoke(asset, null)?.ToString();
                        item.HasBadge = !string.IsNullOrEmpty(item.BadgeText);
                    }

                    if (item.HasBadge && item.BadgeColor.a < 0.1f)
                    {
                        item.BadgeColor = new Color(0.24f, 0.52f, 0.95f, 1f);
                    }
                }
                catch
                {
                    // Catch exceptions from user script implementations
                }
            }
        }

        public ScriptableObjectItemData GetItemByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            itemsByGuid.TryGetValue(guid, out var item);
            return item;
        }

        #endregion

        #region Search, Filter & Sort Engine

        /// <summary>
        /// Filters and sorts cached assets based on active section, search query, and sort mode.
        /// </summary>
        public List<ScriptableObjectItemData> FilterAssets(string activeSection, string query, AssetSortMode sortMode)
        {
            InitializeIfNeeded();

            var settings = EditorProSettings.instance;
            var results = new List<ScriptableObjectItemData>(allItems.Count);

            // Step 1: Section Filter
            if (string.IsNullOrEmpty(activeSection) || activeSection.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(allItems);
            }
            else if (activeSection.Equals("Favorites", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in allItems)
                {
                    if (settings.IsFavorite(item.Guid))
                        results.Add(item);
                }
            }
            else if (activeSection.Equals("Recent", StringComparison.OrdinalIgnoreCase))
            {
                var recentGuids = settings.RecentAssets;
                var dict = new Dictionary<string, ScriptableObjectItemData>(recentGuids.Count, StringComparer.Ordinal);
                foreach (var item in allItems)
                {
                    if (settings.IsRecent(item.Guid))
                        dict[item.Guid] = item;
                }

                // Preserve MRU order
                for (int i = 0; i < recentGuids.Count; i++)
                {
                    if (dict.TryGetValue(recentGuids[i], out var item))
                    {
                        results.Add(item);
                    }
                }
            }
            else if (activeSection.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
            {
                string targetType = activeSection.Substring(5).Trim();
                foreach (var item in allItems)
                {
                    if (item.TypeName.Equals(targetType, StringComparison.OrdinalIgnoreCase))
                        results.Add(item);
                }
            }
            else if (activeSection.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
            {
                string catId = activeSection.Substring(9).Trim();
                var cat = settings.GetCategory(catId);
                if (cat != null)
                {
                    foreach (var item in allItems)
                    {
                        if (cat.ContainsAsset(item.Guid))
                            results.Add(item);
                    }
                }
            }
            else
            {
                results.AddRange(allItems);
            }

            // Step 2: Search Query Parser
            if (!string.IsNullOrWhiteSpace(query))
            {
                var tokens = ParseSearchTokens(query);
                results.RemoveAll(item => !MatchesTokens(item, tokens));
            }

            // Step 3: Sorting
            SortAssets(results, sortMode);

            return results;
        }

        private struct SearchToken
        {
            public enum TokenType { General, TypeFilter, CategoryFilter, ExcludePath, ExcludeTerm }
            public TokenType Type;
            public string Value;
        }

        private static List<SearchToken> ParseSearchTokens(string query)
        {
            var tokens = new List<SearchToken>();
            string[] rawParts = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in rawParts)
            {
                string trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("t:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIdx = trimmed.IndexOf(':');
                    tokens.Add(new SearchToken { Type = SearchToken.TokenType.TypeFilter, Value = trimmed.Substring(colonIdx + 1) });
                }
                else if (trimmed.StartsWith("c:", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.StartsWith("cat:", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIdx = trimmed.IndexOf(':');
                    tokens.Add(new SearchToken { Type = SearchToken.TokenType.CategoryFilter, Value = trimmed.Substring(colonIdx + 1) });
                }
                else if (trimmed.StartsWith("-path:", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.StartsWith("-dir:", StringComparison.OrdinalIgnoreCase))
                {
                    int colonIdx = trimmed.IndexOf(':');
                    tokens.Add(new SearchToken { Type = SearchToken.TokenType.ExcludePath, Value = trimmed.Substring(colonIdx + 1) });
                }
                else if (trimmed.StartsWith("-", StringComparison.Ordinal) && trimmed.Length > 1)
                {
                    tokens.Add(new SearchToken { Type = SearchToken.TokenType.ExcludeTerm, Value = trimmed.Substring(1) });
                }
                else
                {
                    tokens.Add(new SearchToken { Type = SearchToken.TokenType.General, Value = trimmed });
                }
            }

            return tokens;
        }

        private static bool MatchesTokens(ScriptableObjectItemData item, List<SearchToken> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var tok = tokens[i];
                switch (tok.Type)
                {
                    case SearchToken.TokenType.TypeFilter:
                        if (item.TypeName.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                        break;

                    case SearchToken.TokenType.CategoryFilter:
                        bool inCatAttr = item.CategoryAttributeName.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!inCatAttr)
                        {
                            // Check if present in custom categories matching name
                            bool inCustomCat = false;
                            foreach (var c in EditorProSettings.instance.CustomCategories)
                            {
                                if (c.Name.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0 && c.ContainsAsset(item.Guid))
                                {
                                    inCustomCat = true;
                                    break;
                                }
                            }
                            if (!inCustomCat) return false;
                        }
                        break;

                    case SearchToken.TokenType.ExcludePath:
                        if (item.FolderPath.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0)
                            return false;
                        break;

                    case SearchToken.TokenType.ExcludeTerm:
                        if (item.AssetName.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            item.TypeName.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return false;
                        }
                        break;

                    case SearchToken.TokenType.General:
                        bool matched = item.AssetName.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       item.TypeName.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       (!string.IsNullOrEmpty(item.Subtitle) && item.Subtitle.IndexOf(tok.Value, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                       FuzzyMatch(item.AssetName, tok.Value);
                        if (!matched) return false;
                        break;
                }
            }

            return true;
        }

        private static bool FuzzyMatch(string target, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(target)) return false;
            int patternIdx = 0;
            for (int i = 0; i < target.Length; i++)
            {
                if (char.ToLowerInvariant(target[i]) == char.ToLowerInvariant(pattern[patternIdx]))
                {
                    patternIdx++;
                    if (patternIdx >= pattern.Length) return true;
                }
            }
            return false;
        }

        private static void SortAssets(List<ScriptableObjectItemData> list, AssetSortMode mode)
        {
            switch (mode)
            {
                case AssetSortMode.NameAsc:
                    list.Sort((a, b) => string.Compare(a.AssetName, b.AssetName, StringComparison.OrdinalIgnoreCase));
                    break;
                case AssetSortMode.NameDesc:
                    list.Sort((a, b) => string.Compare(b.AssetName, a.AssetName, StringComparison.OrdinalIgnoreCase));
                    break;
                case AssetSortMode.TypeAsc:
                    list.Sort((a, b) =>
                    {
                        int c = string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : string.Compare(a.AssetName, b.AssetName, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
                case AssetSortMode.TypeDesc:
                    list.Sort((a, b) =>
                    {
                        int c = string.Compare(b.TypeName, a.TypeName, StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : string.Compare(a.AssetName, b.AssetName, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
                case AssetSortMode.SizeAsc:
                    list.Sort((a, b) => a.FileSizeBytes.CompareTo(b.FileSizeBytes));
                    break;
                case AssetSortMode.SizeDesc:
                    list.Sort((a, b) => b.FileSizeBytes.CompareTo(a.FileSizeBytes));
                    break;
                case AssetSortMode.PathAsc:
                    list.Sort((a, b) => string.Compare(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }

        #endregion

        #region Statistics

        public void GetTotalStats(out int count, out long totalBytes, out string formattedSize)
        {
            InitializeIfNeeded();
            count = allItems.Count;
            totalBytes = 0;
            for (int i = 0; i < allItems.Count; i++)
            {
                totalBytes += allItems[i].FileSizeBytes;
            }
            formattedSize = FormatBytes(totalBytes);
        }

        public void GetCategoryStats(string categoryId, out int count, out long totalBytes, out string formattedSize)
        {
            InitializeIfNeeded();
            var cat = EditorProSettings.instance.GetCategory(categoryId);
            count = 0;
            totalBytes = 0;
            if (cat != null)
            {
                for (int i = 0; i < cat.AssetGuids.Count; i++)
                {
                    if (itemsByGuid.TryGetValue(cat.AssetGuids[i], out var item))
                    {
                        count++;
                        totalBytes += item.FileSizeBytes;
                    }
                }
            }
            formattedSize = FormatBytes(totalBytes);
        }

        public void GetTypeStats(string typeName, out int count, out long totalBytes, out string formattedSize)
        {
            InitializeIfNeeded();
            count = 0;
            totalBytes = 0;
            for (int i = 0; i < allItems.Count; i++)
            {
                if (allItems[i].TypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    totalBytes += allItems[i].FileSizeBytes;
                }
            }
            formattedSize = FormatBytes(totalBytes);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{(bytes / 1024f):F1} KB";
            return $"{(bytes / (1024f * 1024f)):F1} MB";
        }

        #endregion
    }

    /// <summary>
    /// AssetPostprocessor listener that automatically invalidates and refreshes the EditorPro cache
    /// whenever ScriptableObject assets are created, renamed, moved, or deleted in the project.
    /// </summary>
    public class EditorProAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool hasScriptableObjectChanges = false;

            if (CheckAnyAssetPath(importedAssets) ||
                CheckAnyAssetPath(deletedAssets) ||
                CheckAnyAssetPath(movedAssets) ||
                CheckAnyAssetPath(movedFromAssetPaths))
            {
                hasScriptableObjectChanges = true;
            }

            if (hasScriptableObjectChanges)
            {
                // Debounce rapid asset postprocessing callbacks into a single tick
                EditorApplication.delayCall -= RequestCacheRefresh;
                EditorApplication.delayCall += RequestCacheRefresh;
            }
        }

        private static bool CheckAnyAssetPath(string[] paths)
        {
            if (paths == null) return false;
            for (int i = 0; i < paths.Length; i++)
            {
                if (paths[i].EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void RequestCacheRefresh()
        {
            if (EditorProAssetDatabaseCache.Instance.IsInitialized)
            {
                EditorProAssetDatabaseCache.Instance.Refresh(false);
            }
        }
    }
}
