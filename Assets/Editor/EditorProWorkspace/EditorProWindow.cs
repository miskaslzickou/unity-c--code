using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace EditorProWorkspace
{
    /// <summary>
    /// EditorProWorkspace — ScriptableObject Asset Manager for Unity 6 UI Toolkit.
    /// Drop-in, zero-dependency, C# procedural styling with 3-pane responsive layout,
    /// TypeCache discovery, MultiColumnListView & Dense Grid view, Multi-Tab Inspector,
    /// Side-by-side Split View, and Searchable Creation modal.
    /// </summary>
    public class EditorProWindow : EditorWindow
    {
        [MenuItem("Tools/EditorPro/ScriptableObject Workspace", false, 100)]
        [MenuItem("Window/EditorPro Workspace", false, 2050)]
        public static void OpenWindow()
        {
            var window = GetWindow<EditorProWindow>();
            window.titleContent = new GUIContent("EditorPro Workspace", EditorGUIUtility.IconContent("d_ScriptableObject Icon").image);
            window.minSize = new Vector2(880, 500);
            window.Show();
        }

        // Layout Containers
        private VisualElement rootContainer;
        private Toolbar topToolbar;
        private TwoPaneSplitView mainSplitView;
        private TwoPaneSplitView contentSplitView;

        // Left Sidebar Elements
        private VisualElement leftSidebar;
        private ScrollView sidebarScrollView;
        private VisualElement customCategoriesContainer;
        private VisualElement byTypeContainer;
        private Label allAssetsBadge;
        private Label favoritesBadge;
        private Label recentBadge;

        // Center Browser Elements
        private VisualElement centerPane;
        private ToolbarSearchField searchField;
        private ToolbarButton gridViewToggleBtn;
        private ToolbarButton listViewToggleBtn;
        private ToolbarMenu sortMenu;
        private Slider gridZoomSlider;
        private Label browserHeaderTitle;
        private Label browserHeaderStats;
        private ScrollView gridScrollView;
        private MultiColumnListView multiColumnListView;

        // Right Inspector Elements
        private VisualElement rightPane;
        private ScrollView tabStripScrollView;
        private VisualElement inspectorBodyContainer;
        private ToolbarButton splitViewToggleBtn;

        // Modal Overlays
        private VisualElement modalOverlay;
        private VisualElement createAssetModal;
        private VisualElement addCategoryModal;
        private VisualElement settingsModal;

        // Data Caches
        private List<ScriptableObjectItemData> currentFilteredAssets = new List<ScriptableObjectItemData>();
        private SerializedObject activeSerializedObject;
        private SerializedObject secondarySerializedObject;

        #region Lifecycle

        private void OnEnable()
        {
            EditorProAssetDatabaseCache.Instance.InitializeIfNeeded();
            EditorProAssetDatabaseCache.OnCacheUpdated += OnCacheUpdated;
            EditorProSettings.instance.OnSettingsChanged += OnSettingsChanged;
        }

        private void OnDisable()
        {
            EditorProAssetDatabaseCache.OnCacheUpdated -= OnCacheUpdated;
            if (EditorProSettings.instance != null)
            {
                EditorProSettings.instance.OnSettingsChanged -= OnSettingsChanged;
            }

            activeSerializedObject?.Dispose();
            activeSerializedObject = null;
            secondarySerializedObject?.Dispose();
            secondarySerializedObject = null;
        }

        public void CreateGUI()
        {
            rootContainer = rootVisualElement;
            rootContainer.Clear();
            rootContainer.style.flexGrow = 1;
            rootContainer.style.flexDirection = FlexDirection.Column;
            rootContainer.style.backgroundColor = GetThemeColor(new Color(0.14f, 0.14f, 0.14f), new Color(0.82f, 0.82f, 0.82f));

            rootContainer.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape && modalOverlay != null && modalOverlay.style.display == DisplayStyle.Flex)
                {
                    HideModals();
                    evt.StopPropagation();
                }
            });

            BuildTopToolbar();
            BuildMainSplitView();
            BuildModals();

            RefreshAll();
        }

        private void OnCacheUpdated()
        {
            RefreshAll();
        }

        private void OnSettingsChanged()
        {
            RefreshAll();
        }

        private void RefreshAll()
        {
            RefreshSidebar();
            RefreshBrowser();
            RefreshInspector();
        }

        #endregion

        #region UI Construction: Top Toolbar

        private void BuildTopToolbar()
        {
            topToolbar = new Toolbar();
            topToolbar.style.height = 30;
            topToolbar.style.paddingLeft = 8;
            topToolbar.style.paddingRight = 8;
            topToolbar.style.borderBottomWidth = 1;
            topToolbar.style.borderBottomColor = GetBorderColor();
            topToolbar.style.alignItems = Align.Center;

            // Brand / Title Label
            var brandLabel = new Label("EditorPro Workspace");
            brandLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            brandLabel.style.fontSize = 12;
            brandLabel.style.marginRight = 12;
            brandLabel.style.color = GetThemeColor(new Color(0.85f, 0.85f, 0.85f), new Color(0.15f, 0.15f, 0.15f));
            topToolbar.Add(brandLabel);

            // Search Bar
            searchField = new ToolbarSearchField();
            searchField.style.width = 240;
            searchField.style.flexShrink = 1;
            searchField.value = EditorProSettings.instance.SearchQuery;
            searchField.RegisterValueChangedCallback(evt =>
            {
                EditorProSettings.instance.SearchQuery = evt.newValue;
                RefreshBrowser();
            });
            topToolbar.Add(searchField);

            var spacer1 = new ToolbarSpacer();
            spacer1.style.width = 10;
            topToolbar.Add(spacer1);

            // View Mode Toggles (Grid / List)
            gridViewToggleBtn = new ToolbarButton(() => SetViewMode(WorkspaceViewMode.Grid))
            {
                text = "Grid",
                tooltip = "Switch to Dense Grid View"
            };
            listViewToggleBtn = new ToolbarButton(() => SetViewMode(WorkspaceViewMode.List))
            {
                text = "List",
                tooltip = "Switch to MultiColumn List View"
            };
            topToolbar.Add(gridViewToggleBtn);
            topToolbar.Add(listViewToggleBtn);

            // Sort Menu
            sortMenu = new ToolbarMenu();
            sortMenu.style.marginLeft = 6;
            UpdateSortMenuText();
            sortMenu.menu.AppendAction("Name (A-Z)", _ => SetSortMode(AssetSortMode.NameAsc), _ => EditorProSettings.instance.SortMode == AssetSortMode.NameAsc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            sortMenu.menu.AppendAction("Name (Z-A)", _ => SetSortMode(AssetSortMode.NameDesc), _ => EditorProSettings.instance.SortMode == AssetSortMode.NameDesc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            sortMenu.menu.AppendAction("Type (A-Z)", _ => SetSortMode(AssetSortMode.TypeAsc), _ => EditorProSettings.instance.SortMode == AssetSortMode.TypeAsc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            sortMenu.menu.AppendAction("Type (Z-A)", _ => SetSortMode(AssetSortMode.TypeDesc), _ => EditorProSettings.instance.SortMode == AssetSortMode.TypeDesc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            sortMenu.menu.AppendAction("Size (Smallest)", _ => SetSortMode(AssetSortMode.SizeAsc), _ => EditorProSettings.instance.SortMode == AssetSortMode.SizeAsc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            sortMenu.menu.AppendAction("Size (Largest)", _ => SetSortMode(AssetSortMode.SizeDesc), _ => EditorProSettings.instance.SortMode == AssetSortMode.SizeDesc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            sortMenu.menu.AppendAction("Folder Path", _ => SetSortMode(AssetSortMode.PathAsc), _ => EditorProSettings.instance.SortMode == AssetSortMode.PathAsc ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            topToolbar.Add(sortMenu);

            // Grid Zoom Slider
            gridZoomSlider = new Slider(85f, 170f)
            {
                value = EditorProSettings.instance.GridItemSize,
                tooltip = "Adjust Grid Card Size",
                style = { width = 90, marginLeft = 10, marginRight = 6 }
            };
            gridZoomSlider.RegisterValueChangedCallback(evt =>
            {
                EditorProSettings.instance.GridItemSize = evt.newValue;
                if (EditorProSettings.instance.ViewMode == WorkspaceViewMode.Grid)
                {
                    RefreshBrowser();
                }
            });
            topToolbar.Add(gridZoomSlider);

            // Spacer
            var flexSpacer = new ToolbarSpacer();
            flexSpacer.style.flexGrow = 1;
            topToolbar.Add(flexSpacer);

            // "+ Create Asset..." Button
            var createBtn = new ToolbarButton(ShowCreateAssetModal)
            {
                text = "+ Create Asset...",
                tooltip = "Search and create a new ScriptableObject asset instance"
            };
            createBtn.style.backgroundColor = new StyleColor(new Color(0.18f, 0.49f, 0.76f));
            createBtn.style.color = Color.white;
            createBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            createBtn.style.paddingLeft = 10;
            createBtn.style.paddingRight = 10;
            createBtn.style.borderTopLeftRadius = 4;
            createBtn.style.borderTopRightRadius = 4;
            createBtn.style.borderBottomLeftRadius = 4;
            createBtn.style.borderBottomRightRadius = 4;
            topToolbar.Add(createBtn);

            rootContainer.Add(topToolbar);
        }

        private void SetViewMode(WorkspaceViewMode mode)
        {
            EditorProSettings.instance.ViewMode = mode;
            EditorProSettings.instance.SaveSettings();
            UpdateViewModeButtons();
            RefreshBrowser();
        }

        private void UpdateViewModeButtons()
        {
            bool isGrid = EditorProSettings.instance.ViewMode == WorkspaceViewMode.Grid;
            ApplyActiveButton(gridViewToggleBtn, isGrid);
            ApplyActiveButton(listViewToggleBtn, !isGrid);
            if (gridZoomSlider != null)
            {
                gridZoomSlider.style.display = isGrid ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void SetSortMode(AssetSortMode mode)
        {
            EditorProSettings.instance.SortMode = mode;
            EditorProSettings.instance.SaveSettings();
            UpdateSortMenuText();
            RefreshBrowser();
        }

        private void UpdateSortMenuText()
        {
            if (sortMenu == null) return;
            string prefix = "Sort: ";
            switch (EditorProSettings.instance.SortMode)
            {
                case AssetSortMode.NameAsc: sortMenu.text = prefix + "Name ↑"; break;
                case AssetSortMode.NameDesc: sortMenu.text = prefix + "Name ↓"; break;
                case AssetSortMode.TypeAsc: sortMenu.text = prefix + "Type ↑"; break;
                case AssetSortMode.TypeDesc: sortMenu.text = prefix + "Type ↓"; break;
                case AssetSortMode.SizeAsc: sortMenu.text = prefix + "Size ↑"; break;
                case AssetSortMode.SizeDesc: sortMenu.text = prefix + "Size ↓"; break;
                case AssetSortMode.PathAsc: sortMenu.text = prefix + "Path"; break;
            }
        }

        #endregion

        #region UI Construction: Three-Pane Responsive Workspace

        private void BuildMainSplitView()
        {
            // Outer Split View: Left Sidebar vs (Center + Right)
            mainSplitView = new TwoPaneSplitView(0, 240, TwoPaneSplitViewOrientation.Horizontal);
            mainSplitView.style.flexGrow = 1;

            // Pane 0: Left Sidebar
            BuildLeftSidebar();
            mainSplitView.Add(leftSidebar);

            // Pane 1: Center Browser + Right Inspector
            contentSplitView = new TwoPaneSplitView(0, 480, TwoPaneSplitViewOrientation.Horizontal);
            contentSplitView.style.flexGrow = 1;

            BuildCenterPane();
            contentSplitView.Add(centerPane);

            BuildRightPane();
            contentSplitView.Add(rightPane);

            mainSplitView.Add(contentSplitView);
            rootContainer.Add(mainSplitView);
        }

        #endregion

        #region Left Sidebar (Categories & Favorites)

        private void BuildLeftSidebar()
        {
            leftSidebar = new VisualElement();
            leftSidebar.style.flexGrow = 1;
            leftSidebar.style.backgroundColor = GetThemeColor(new Color(0.18f, 0.18f, 0.18f), new Color(0.88f, 0.88f, 0.88f));
            leftSidebar.style.borderRightWidth = 1;
            leftSidebar.style.borderRightColor = GetBorderColor();

            // Header bar
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 8;
            header.style.paddingRight = 8;
            header.style.height = 30;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = GetBorderColor();

            var titleLbl = new Label("WORKSPACE");
            titleLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLbl.style.fontSize = 10;
            titleLbl.style.color = GetMutedTextColor();
            titleLbl.style.flexGrow = 1;
            header.Add(titleLbl);

            var addCatBtn = new Button(ShowAddCategoryModal) { text = "+ Category", tooltip = "Create custom category" };
            StyleMiniButton(addCatBtn);
            header.Add(addCatBtn);

            var settingsBtn = new Button(ShowSettingsModal) { text = "⚙", tooltip = "Workspace Settings & Exclusions" };
            StyleMiniButton(settingsBtn);
            header.Add(settingsBtn);

            var refreshBtn = new Button(() => EditorProAssetDatabaseCache.Instance.Refresh(true)) { text = "↻", tooltip = "Rescan Project ScriptableObjects" };
            StyleMiniButton(refreshBtn);
            header.Add(refreshBtn);

            leftSidebar.Add(header);

            // Scrollable Content
            sidebarScrollView = new ScrollView(ScrollViewMode.Vertical);
            sidebarScrollView.style.flexGrow = 1;
            sidebarScrollView.style.paddingTop = 6;
            sidebarScrollView.style.paddingBottom = 12;

            // Section 1: Quick Access
            var quickAccessSection = new VisualElement();
            quickAccessSection.style.marginBottom = 12;

            var quickHeader = CreateSectionHeaderLabel("QUICK ACCESS");
            quickAccessSection.Add(quickHeader);

            var allAssetsRow = CreateSidebarItem("All", "All Assets", "d_Project", out allAssetsBadge);
            var favoritesRow = CreateSidebarItem("Favorites", "Favorites", "Favorite", out favoritesBadge);
            var recentRow = CreateSidebarItem("Recent", "Recent", "History", out recentBadge);

            quickAccessSection.Add(allAssetsRow);
            quickAccessSection.Add(favoritesRow);
            quickAccessSection.Add(recentRow);
            sidebarScrollView.Add(quickAccessSection);

            // Section 2: Custom Categories
            var customCatSection = new VisualElement();
            customCatSection.style.marginBottom = 12;

            var catHeader = CreateSectionHeaderLabel("CUSTOM CATEGORIES");
            customCatSection.Add(catHeader);

            customCategoriesContainer = new VisualElement();
            customCatSection.Add(customCategoriesContainer);
            sidebarScrollView.Add(customCatSection);

            // Section 3: By Type
            var byTypeSection = new VisualElement();
            byTypeSection.style.marginBottom = 12;

            var byTypeHeader = CreateSectionHeaderLabel("BY TYPE");
            byTypeSection.Add(byTypeHeader);

            byTypeContainer = new VisualElement();
            byTypeSection.Add(byTypeContainer);
            sidebarScrollView.Add(byTypeSection);

            leftSidebar.Add(sidebarScrollView);
        }

        private VisualElement CreateSectionHeaderLabel(string text)
        {
            var lbl = new Label(text);
            lbl.style.fontSize = 9;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.color = GetMutedTextColor();
            lbl.style.paddingLeft = 10;
            lbl.style.paddingBottom = 4;
            lbl.style.paddingTop = 4;
            return lbl;
        }

        private VisualElement CreateSidebarItem(string sectionKey, string title, string iconName, out Label badgeLabel)
        {
            var item = new VisualElement();
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.height = 24;
            item.style.paddingLeft = 10;
            item.style.paddingRight = 8;
            item.style.marginBottom = 1;
            item.style.borderTopLeftRadius = 4;
            item.style.borderBottomLeftRadius = 4;
            item.style.borderTopRightRadius = 4;
            item.style.borderBottomRightRadius = 4;

            var iconImg = new Image();
            iconImg.image = EditorGUIUtility.IconContent(iconName)?.image;
            iconImg.style.width = 14;
            iconImg.style.height = 14;
            iconImg.style.marginRight = 6;
            item.Add(iconImg);

            var titleLbl = new Label(title);
            titleLbl.style.fontSize = 11;
            titleLbl.style.flexGrow = 1;
            titleLbl.style.color = GetTextColor();
            item.Add(titleLbl);

            var badge = new Label("0");
            StyleBadge(badge, GetBadgeBgColor());
            item.Add(badge);
            badgeLabel = badge;

            item.RegisterCallback<ClickEvent>(evt =>
            {
                SelectSection(sectionKey);
            });

            return item;
        }

        private void SelectSection(string sectionKey)
        {
            EditorProSettings.instance.ActiveSection = sectionKey;
            EditorProSettings.instance.SaveSettings();
            RefreshSidebar();
            RefreshBrowser();
        }

        private void RefreshSidebar()
        {
            var cache = EditorProAssetDatabaseCache.Instance;
            var settings = EditorProSettings.instance;

            // Update Quick Access Badges
            cache.GetTotalStats(out int totalCount, out _, out _);
            if (allAssetsBadge != null) allAssetsBadge.text = totalCount.ToString();
            if (favoritesBadge != null) favoritesBadge.text = settings.Favorites.Count.ToString();
            if (recentBadge != null) recentBadge.text = settings.RecentAssets.Count.ToString();

            // Refresh Custom Categories
            if (customCategoriesContainer != null)
            {
                customCategoriesContainer.Clear();
                foreach (var cat in settings.CustomCategories)
                {
                    cache.GetCategoryStats(cat.Id, out int catCount, out _, out string formattedSize);
                    var catRow = BuildCategorySidebarRow(cat, catCount, formattedSize);
                    customCategoriesContainer.Add(catRow);
                }
            }

            // Refresh By Type Section
            if (byTypeContainer != null)
            {
                byTypeContainer.Clear();

                // Aggregate types from cached items
                var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in cache.AllItems)
                {
                    if (!typeCounts.ContainsKey(item.TypeName))
                        typeCounts[item.TypeName] = 0;
                    typeCounts[item.TypeName]++;
                }

                foreach (var kvp in typeCounts)
                {
                    string typeName = kvp.Key;
                    int count = kvp.Value;
                    string sectionKey = $"Type:{typeName}";

                    var typeInfo = cache.GetTypeInfo(typeName);
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.height = 22;
                    row.style.paddingLeft = 14;
                    row.style.paddingRight = 8;
                    row.style.marginBottom = 1;
                    row.style.borderTopLeftRadius = 4;
                    row.style.borderBottomLeftRadius = 4;
                    row.style.borderTopRightRadius = 4;
                    row.style.borderBottomRightRadius = 4;

                    if (settings.ActiveSection == sectionKey)
                    {
                        row.style.backgroundColor = GetSelectedRowBgColor();
                    }

                    var iconImg = new Image();
                    iconImg.image = typeInfo?.Icon ?? EditorGUIUtility.IconContent("d_ScriptableObject Icon")?.image;
                    iconImg.style.width = 12;
                    iconImg.style.height = 12;
                    iconImg.style.marginRight = 6;
                    row.Add(iconImg);

                    var nameLbl = new Label(typeName);
                    nameLbl.style.fontSize = 11;
                    nameLbl.style.flexGrow = 1;
                    nameLbl.style.color = GetTextColor();
                    row.Add(nameLbl);

                    var badge = new Label(count.ToString());
                    StyleBadge(badge, GetBadgeBgColor());
                    row.Add(badge);

                    row.RegisterCallback<ClickEvent>(evt => SelectSection(sectionKey));

                    byTypeContainer.Add(row);
                }
            }
        }

        private VisualElement BuildCategorySidebarRow(EditorProCategory cat, int count, string formattedSize)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 24;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 6;
            row.style.marginBottom = 1;
            row.style.borderTopLeftRadius = 4;
            row.style.borderBottomLeftRadius = 4;
            row.style.borderTopRightRadius = 4;
            row.style.borderBottomRightRadius = 4;

            string sectionKey = $"Category:{cat.Id}";
            if (EditorProSettings.instance.ActiveSection == sectionKey)
            {
                row.style.backgroundColor = GetSelectedRowBgColor();
            }

            // Color tag dot
            var colorDot = new VisualElement();
            colorDot.style.width = 8;
            colorDot.style.height = 8;
            colorDot.style.borderTopLeftRadius = 4;
            colorDot.style.borderTopRightRadius = 4;
            colorDot.style.borderBottomLeftRadius = 4;
            colorDot.style.borderBottomRightRadius = 4;
            colorDot.style.marginRight = 6;
            if (ColorUtility.TryParseHtmlString(cat.ColorHex, out var dotColor))
            {
                colorDot.style.backgroundColor = dotColor;
            }
            else
            {
                colorDot.style.backgroundColor = new Color(0.2f, 0.6f, 0.9f);
            }
            row.Add(colorDot);

            // Category Name
            var nameLbl = new Label(cat.Name);
            nameLbl.style.fontSize = 11;
            nameLbl.style.flexGrow = 1;
            nameLbl.style.color = GetTextColor();
            row.Add(nameLbl);

            // Statistics pill badge
            var statBadge = new Label($"{count} ({formattedSize})");
            statBadge.tooltip = $"Category contains {count} assets totaling {formattedSize}";
            StyleBadge(statBadge, GetBadgeBgColor());
            statBadge.style.marginRight = 4;
            row.Add(statBadge);

            // Delete Category Button
            var deleteBtn = new Button(() =>
            {
                if (EditorUtility.DisplayDialog("Delete Category", $"Are you sure you want to delete category '{cat.Name}'?", "Delete", "Cancel"))
                {
                    EditorProSettings.instance.RemoveCategory(cat.Id);
                }
            })
            {
                text = "✕",
                tooltip = "Delete Category"
            };
            StyleMiniCloseButton(deleteBtn);
            row.Add(deleteBtn);

            // Click row to select
            row.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target != deleteBtn)
                {
                    SelectSection(sectionKey);
                }
            });

            // DRAG AND DROP TARGET SUPPORT
            // Users can drag ScriptableObjects from center pane cards or Project window onto this category!
            row.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                row.style.backgroundColor = new StyleColor(new Color(0.18f, 0.45f, 0.75f, 0.35f));
                evt.StopPropagation();
            });

            row.RegisterCallback<DragLeaveEvent>(evt =>
            {
                row.style.backgroundColor = EditorProSettings.instance.ActiveSection == sectionKey
                    ? GetSelectedRowBgColor()
                    : new StyleColor(Color.clear);
            });

            row.RegisterCallback<DragPerformEvent>(evt =>
            {
                DragAndDrop.AcceptDrag();
                row.style.backgroundColor = EditorProSettings.instance.ActiveSection == sectionKey
                    ? GetSelectedRowBgColor()
                    : new StyleColor(Color.clear);

                bool addedAny = false;
                if (DragAndDrop.paths != null)
                {
                    foreach (var path in DragAndDrop.paths)
                    {
                        string guid = AssetDatabase.AssetPathToGUID(path);
                        if (!string.IsNullOrEmpty(guid))
                        {
                            if (EditorProSettings.instance.AddAssetToCategory(cat.Id, guid))
                                addedAny = true;
                        }
                    }
                }

                string directGuid = DragAndDrop.GetGenericData("EditorProGuid") as string;
                if (!string.IsNullOrEmpty(directGuid))
                {
                    if (EditorProSettings.instance.AddAssetToCategory(cat.Id, directGuid))
                        addedAny = true;
                }

                if (addedAny)
                {
                    EditorProSettings.instance.SaveSettings();
                    RefreshSidebar();
                    if (EditorProSettings.instance.ActiveSection == sectionKey)
                    {
                        RefreshBrowser();
                    }
                }

                evt.StopPropagation();
            });

            return row;
        }

        #endregion

        #region Center Pane (Browser & Creation Hub)

        private void BuildCenterPane()
        {
            centerPane = new VisualElement();
            centerPane.style.flexGrow = 1;
            centerPane.style.backgroundColor = GetThemeColor(new Color(0.16f, 0.16f, 0.16f), new Color(0.85f, 0.85f, 0.85f));
            centerPane.style.borderRightWidth = 1;
            centerPane.style.borderRightColor = GetBorderColor();

            // Breadcrumb / Section Header Bar
            var headerBar = new VisualElement();
            headerBar.style.flexDirection = FlexDirection.Row;
            headerBar.style.alignItems = Align.Center;
            headerBar.style.paddingLeft = 10;
            headerBar.style.paddingRight = 10;
            headerBar.style.height = 30;
            headerBar.style.borderBottomWidth = 1;
            headerBar.style.borderBottomColor = GetBorderColor();
            headerBar.style.backgroundColor = GetThemeColor(new Color(0.18f, 0.18f, 0.18f), new Color(0.88f, 0.88f, 0.88f));

            browserHeaderTitle = new Label("All Assets");
            browserHeaderTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            browserHeaderTitle.style.fontSize = 12;
            browserHeaderTitle.style.color = GetTextColor();
            headerBar.Add(browserHeaderTitle);

            browserHeaderStats = new Label("(0 assets • 0 B)");
            browserHeaderStats.style.fontSize = 11;
            browserHeaderStats.style.color = GetMutedTextColor();
            browserHeaderStats.style.marginLeft = 8;
            headerBar.Add(browserHeaderStats);

            var spacer = new ToolbarSpacer();
            spacer.style.flexGrow = 1;
            headerBar.Add(spacer);

            centerPane.Add(headerBar);

            // Container for Grid View
            gridScrollView = new ScrollView(ScrollViewMode.Vertical);
            gridScrollView.style.flexGrow = 1;
            gridScrollView.style.paddingLeft = 6;
            gridScrollView.style.paddingRight = 6;
            gridScrollView.style.paddingTop = 6;
            gridScrollView.style.paddingBottom = 6;
            gridScrollView.contentContainer.style.flexDirection = FlexDirection.Row;
            gridScrollView.contentContainer.style.flexWrap = Wrap.Wrap;
            centerPane.Add(gridScrollView);

            // Container for List View
            BuildMultiColumnListView();
            centerPane.Add(multiColumnListView);

            UpdateViewModeButtons();
        }

        private void BuildMultiColumnListView()
        {
            multiColumnListView = new MultiColumnListView();
            multiColumnListView.style.flexGrow = 1;
            multiColumnListView.fixedItemHeight = 28f;
            multiColumnListView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;

            // Column 0: Star / Favorite Toggle
            var colFav = new Column
            {
                name = "Fav",
                title = "★",
                width = 28f,
                minWidth = 28f,
                maxWidth = 28f,
                resizable = false,
                makeCell = () =>
                {
                    var btn = new Button();
                    btn.style.width = 20;
                    btn.style.height = 20;
                    btn.style.paddingLeft = 0;
                    btn.style.paddingRight = 0;
                    btn.style.paddingTop = 0;
                    btn.style.paddingBottom = 0;
                    btn.style.backgroundColor = StyleKeyword.Null;
                    btn.style.borderTopWidth = 0;
                    btn.style.borderBottomWidth = 0;
                    btn.style.borderLeftWidth = 0;
                    btn.style.borderRightWidth = 0;
                    btn.style.fontSize = 12;
                    return btn;
                },
                bindCell = (visualElement, index) =>
                {
                    var btn = (Button)visualElement;
                    if (index >= 0 && index < currentFilteredAssets.Count)
                    {
                        var item = currentFilteredAssets[index];
                        bool isFav = EditorProSettings.instance.IsFavorite(item.Guid);
                        btn.text = isFav ? "★" : "☆";
                        btn.style.color = isFav ? new Color(0.95f, 0.77f, 0.05f) : GetMutedTextColor();
                        btn.clickable = new Clickable(() =>
                        {
                            EditorProSettings.instance.ToggleFavorite(item.Guid);
                            RefreshBrowser();
                            RefreshSidebar();
                        });
                    }
                }
            };
            multiColumnListView.columns.Add(colFav);

            // Column 1: Asset Name
            var colName = new Column
            {
                name = "Name",
                title = "Name",
                width = 200f,
                resizable = true,
                makeCell = () =>
                {
                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.paddingLeft = 4;

                    var img = new Image();
                    img.name = "icon";
                    img.style.width = 16;
                    img.style.height = 16;
                    img.style.marginRight = 6;
                    row.Add(img);

                    var lbl = new Label();
                    lbl.name = "name";
                    lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
                    lbl.style.fontSize = 11;
                    lbl.style.color = GetTextColor();
                    row.Add(lbl);

                    return row;
                },
                bindCell = (visualElement, index) =>
                {
                    if (index >= 0 && index < currentFilteredAssets.Count)
                    {
                        var item = currentFilteredAssets[index];
                        var img = visualElement.Q<Image>("icon");
                        var lbl = visualElement.Q<Label>("name");
                        if (img != null) img.image = item.Icon;
                        if (lbl != null) lbl.text = item.AssetName;
                    }
                }
            };
            multiColumnListView.columns.Add(colName);

            // Column 2: Type
            var colType = new Column
            {
                name = "Type",
                title = "Type",
                width = 130f,
                resizable = true,
                makeCell = () =>
                {
                    var pill = new Label();
                    pill.style.fontSize = 10;
                    pill.style.unityTextAlign = TextAnchor.MiddleCenter;
                    pill.style.paddingLeft = 6;
                    pill.style.paddingRight = 6;
                    pill.style.paddingTop = 2;
                    pill.style.paddingBottom = 2;
                    pill.style.borderTopLeftRadius = 3;
                    pill.style.borderTopRightRadius = 3;
                    pill.style.borderBottomLeftRadius = 3;
                    pill.style.borderBottomRightRadius = 3;
                    pill.style.backgroundColor = GetBadgeBgColor();
                    pill.style.color = GetMutedTextColor();
                    return pill;
                },
                bindCell = (visualElement, index) =>
                {
                    if (index >= 0 && index < currentFilteredAssets.Count)
                    {
                        var item = currentFilteredAssets[index];
                        var lbl = (Label)visualElement;
                        lbl.text = item.TypeName;
                    }
                }
            };
            multiColumnListView.columns.Add(colType);

            // Column 3: Badge / Subtitle
            var colBadge = new Column
            {
                name = "Badge",
                title = "Status / Subtitle",
                width = 110f,
                resizable = true,
                makeCell = () =>
                {
                    var lbl = new Label();
                    lbl.style.fontSize = 10;
                    lbl.style.paddingLeft = 4;
                    lbl.style.color = GetMutedTextColor();
                    return lbl;
                },
                bindCell = (visualElement, index) =>
                {
                    if (index >= 0 && index < currentFilteredAssets.Count)
                    {
                        var item = currentFilteredAssets[index];
                        var lbl = (Label)visualElement;
                        if (item.HasBadge)
                        {
                            lbl.text = item.BadgeText;
                            lbl.style.color = item.BadgeColor;
                            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
                        }
                        else if (!string.IsNullOrEmpty(item.Subtitle))
                        {
                            lbl.text = item.Subtitle;
                            lbl.style.color = GetMutedTextColor();
                            lbl.style.unityFontStyleAndWeight = FontStyle.Normal;
                        }
                        else if (!string.IsNullOrEmpty(item.CategoryAttributeName))
                        {
                            lbl.text = item.CategoryAttributeName;
                            lbl.style.color = GetMutedTextColor();
                            lbl.style.unityFontStyleAndWeight = FontStyle.Normal;
                        }
                        else
                        {
                            lbl.text = string.Empty;
                        }
                    }
                }
            };
            multiColumnListView.columns.Add(colBadge);

            // Column 4: Folder Path
            var colPath = new Column
            {
                name = "Path",
                title = "Folder",
                width = 170f,
                resizable = true,
                makeCell = () =>
                {
                    var lbl = new Label();
                    lbl.style.fontSize = 10;
                    lbl.style.color = GetMutedTextColor();
                    lbl.style.paddingLeft = 4;
                    return lbl;
                },
                bindCell = (visualElement, index) =>
                {
                    if (index >= 0 && index < currentFilteredAssets.Count)
                    {
                        var item = currentFilteredAssets[index];
                        var lbl = (Label)visualElement;
                        lbl.text = item.FolderPath;
                    }
                }
            };
            multiColumnListView.columns.Add(colPath);

            // Column 5: File Size
            var colSize = new Column
            {
                name = "Size",
                title = "Size",
                width = 65f,
                resizable = true,
                makeCell = () =>
                {
                    var lbl = new Label();
                    lbl.style.fontSize = 10;
                    lbl.style.color = GetMutedTextColor();
                    lbl.style.paddingLeft = 4;
                    return lbl;
                },
                bindCell = (visualElement, index) =>
                {
                    if (index >= 0 && index < currentFilteredAssets.Count)
                    {
                        var item = currentFilteredAssets[index];
                        var lbl = (Label)visualElement;
                        lbl.text = item.FormattedFileSize;
                    }
                }
            };
            multiColumnListView.columns.Add(colSize);

            // Handle selection
            multiColumnListView.selectionChanged += items =>
            {
                foreach (var obj in items)
                {
                    if (obj is ScriptableObjectItemData item)
                    {
                        SelectAsset(item.Guid);
                        break;
                    }
                }
            };

            // Double click or Enter on row reveals/pings in Project
            multiColumnListView.itemsChosen += items =>
            {
                foreach (var obj in items)
                {
                    if (obj is ScriptableObjectItemData item)
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(item.AssetPath);
                        if (asset != null) EditorGUIUtility.PingObject(asset);
                        break;
                    }
                }
            };
        }

        private void RefreshBrowser()
        {
            var settings = EditorProSettings.instance;
            var cache = EditorProAssetDatabaseCache.Instance;

            // Filter & Sort
            currentFilteredAssets = cache.FilterAssets(settings.ActiveSection, settings.SearchQuery, settings.SortMode);

            // Update Header Title & Stats
            string headerTitle = "All Assets";
            if (settings.ActiveSection.Equals("Favorites", StringComparison.OrdinalIgnoreCase))
                headerTitle = "★ Favorites";
            else if (settings.ActiveSection.Equals("Recent", StringComparison.OrdinalIgnoreCase))
                headerTitle = "🕒 Recent";
            else if (settings.ActiveSection.StartsWith("Type:", StringComparison.OrdinalIgnoreCase))
                headerTitle = $"Type: {settings.ActiveSection.Substring(5)}";
            else if (settings.ActiveSection.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
            {
                var cat = settings.GetCategory(settings.ActiveSection.Substring(9));
                headerTitle = cat != null ? $"📁 {cat.Name}" : "Category";
            }

            long totalBytes = 0;
            for (int i = 0; i < currentFilteredAssets.Count; i++)
            {
                totalBytes += currentFilteredAssets[i].FileSizeBytes;
            }

            if (browserHeaderTitle != null) browserHeaderTitle.text = headerTitle;
            if (browserHeaderStats != null) browserHeaderStats.text = $"({currentFilteredAssets.Count} assets • {EditorProAssetDatabaseCache.FormatBytes(totalBytes)})";

            bool isGrid = settings.ViewMode == WorkspaceViewMode.Grid;
            if (gridScrollView != null) gridScrollView.style.display = isGrid ? DisplayStyle.Flex : DisplayStyle.None;
            if (multiColumnListView != null) multiColumnListView.style.display = isGrid ? DisplayStyle.None : DisplayStyle.Flex;

            if (isGrid)
            {
                PopulateDenseGrid();
            }
            else
            {
                multiColumnListView.itemsSource = currentFilteredAssets;
                multiColumnListView.Rebuild();
            }
        }

        private void PopulateDenseGrid()
        {
            if (gridScrollView == null) return;
            Vector2 savedScroll = gridScrollView.scrollOffset;
            gridScrollView.Clear();

            float cardSize = EditorProSettings.instance.GridItemSize;

            foreach (var item in currentFilteredAssets)
            {
                var card = CreateGridCard(item, cardSize);
                gridScrollView.Add(card);
            }

            gridScrollView.schedule.Execute(() => gridScrollView.scrollOffset = savedScroll);
        }

        private void UpdateGridCardSelection(string activeGuid)
        {
            if (gridScrollView == null) return;
            foreach (var child in gridScrollView.Children())
            {
                if (child.userData is string cardGuid)
                {
                    bool isSelected = cardGuid == activeGuid;
                    child.style.backgroundColor = isSelected ? GetSelectedRowBgColor() : GetCardBgColor();
                    var borderCol = isSelected ? new Color(0.24f, 0.52f, 0.95f) : GetBorderColor();
                    child.style.borderTopColor = borderCol;
                    child.style.borderBottomColor = borderCol;
                    child.style.borderLeftColor = borderCol;
                    child.style.borderRightColor = borderCol;
                }
            }
        }

        private VisualElement CreateGridCard(ScriptableObjectItemData item, float cardSize)
        {
            var card = new VisualElement();
            card.userData = item.Guid;
            card.style.width = cardSize;
            card.style.height = cardSize + 32f;
            card.style.marginRight = 6;
            card.style.marginBottom = 6;
            card.style.borderTopLeftRadius = 6;
            card.style.borderTopRightRadius = 6;
            card.style.borderBottomLeftRadius = 6;
            card.style.borderBottomRightRadius = 6;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            card.style.alignItems = Align.Center;
            card.style.justifyContent = Justify.SpaceBetween;
            card.style.paddingTop = 6;
            card.style.paddingBottom = 6;
            card.style.paddingLeft = 4;
            card.style.paddingRight = 4;

            bool isSelected = item.Guid == EditorProSettings.instance.ActiveTabGuid;
            if (isSelected)
            {
                card.style.backgroundColor = GetSelectedRowBgColor();
                card.style.borderTopColor = new Color(0.24f, 0.52f, 0.95f);
                card.style.borderBottomColor = new Color(0.24f, 0.52f, 0.95f);
                card.style.borderLeftColor = new Color(0.24f, 0.52f, 0.95f);
                card.style.borderRightColor = new Color(0.24f, 0.52f, 0.95f);
            }
            else
            {
                card.style.backgroundColor = GetCardBgColor();
                card.style.borderTopColor = GetBorderColor();
                card.style.borderBottomColor = GetBorderColor();
                card.style.borderLeftColor = GetBorderColor();
                card.style.borderRightColor = GetBorderColor();
            }

            // Top Row: Star / Favorite toggle
            var topRow = new VisualElement();
            topRow.style.width = new Length(100, LengthUnit.Percent);
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.justifyContent = Justify.FlexEnd;
            topRow.style.height = 14;

            bool isFav = EditorProSettings.instance.IsFavorite(item.Guid);
            var starBtn = new Button(() =>
            {
                EditorProSettings.instance.ToggleFavorite(item.Guid);
                RefreshBrowser();
                RefreshSidebar();
            })
            {
                text = isFav ? "★" : "☆",
                tooltip = isFav ? "Remove Favorite" : "Add to Favorites"
            };
            starBtn.style.backgroundColor = StyleKeyword.Null;
            starBtn.style.borderTopWidth = 0;
            starBtn.style.borderBottomWidth = 0;
            starBtn.style.borderLeftWidth = 0;
            starBtn.style.borderRightWidth = 0;
            starBtn.style.paddingTop = 0;
            starBtn.style.paddingBottom = 0;
            starBtn.style.paddingLeft = 0;
            starBtn.style.paddingRight = 0;
            starBtn.style.fontSize = 11;
            starBtn.style.color = isFav ? new Color(0.95f, 0.77f, 0.05f) : GetMutedTextColor();
            topRow.Add(starBtn);
            card.Add(topRow);

            // Thumbnail / Icon
            var iconImg = new Image();
            iconImg.image = item.Icon;
            float iconDim = Mathf.Clamp(cardSize * 0.42f, 28f, 54f);
            iconImg.style.width = iconDim;
            iconImg.style.height = iconDim;
            card.Add(iconImg);

            // Center details: Name + Subtitle/Badge
            var detailsContainer = new VisualElement();
            detailsContainer.style.width = new Length(100, LengthUnit.Percent);
            detailsContainer.style.alignItems = Align.Center;

            var nameLbl = new Label(item.AssetName);
            nameLbl.tooltip = $"{item.AssetName} ({item.TypeName})\n{item.AssetPath}";
            nameLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLbl.style.fontSize = 10;
            nameLbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            nameLbl.style.color = GetTextColor();
            nameLbl.style.overflow = Overflow.Hidden;
            nameLbl.style.textOverflow = TextOverflow.Ellipsis;
            nameLbl.style.maxWidth = cardSize - 10;
            detailsContainer.Add(nameLbl);

            // Type Badge Pill
            var typeBadge = new Label(item.TypeName);
            typeBadge.style.fontSize = 9;
            typeBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            typeBadge.style.paddingLeft = 4;
            typeBadge.style.paddingRight = 4;
            typeBadge.style.borderTopLeftRadius = 3;
            typeBadge.style.borderTopRightRadius = 3;
            typeBadge.style.borderBottomLeftRadius = 3;
            typeBadge.style.borderBottomRightRadius = 3;
            typeBadge.style.backgroundColor = GetBadgeBgColor();
            typeBadge.style.color = GetMutedTextColor();
            typeBadge.style.marginTop = 2;
            typeBadge.style.maxWidth = cardSize - 14;
            typeBadge.style.overflow = Overflow.Hidden;
            typeBadge.style.textOverflow = TextOverflow.Ellipsis;
            detailsContainer.Add(typeBadge);

            // Custom Badge (if IEditorProBadgeProvider or Category Attribute)
            if (item.HasBadge)
            {
                var customBadge = new Label(item.BadgeText);
                customBadge.style.fontSize = 8;
                customBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
                customBadge.style.color = item.BadgeColor;
                customBadge.style.marginTop = 1;
                detailsContainer.Add(customBadge);
            }
            else if (!string.IsNullOrEmpty(item.Subtitle))
            {
                var subLbl = new Label(item.Subtitle);
                subLbl.style.fontSize = 8;
                subLbl.style.color = GetMutedTextColor();
                subLbl.style.maxWidth = cardSize - 14;
                subLbl.style.overflow = Overflow.Hidden;
                subLbl.style.textOverflow = TextOverflow.Ellipsis;
                detailsContainer.Add(subLbl);
            }

            card.Add(detailsContainer);

            Vector2 dragStartPos = Vector2.zero;
            bool isDragging = false;

            // Single click selects and opens in inspector tab
            card.RegisterCallback<ClickEvent>(evt =>
            {
                if (isDragging) return;
                if (evt.target != starBtn)
                {
                    SelectAsset(item.Guid);
                }
            });

            // Double click reveals/pings in Project
            card.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2 && evt.button == 0)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(item.AssetPath);
                    if (asset != null)
                    {
                        EditorGUIUtility.PingObject(asset);
                    }
                }
            });

            // Drag and drop source support: drag card to sidebar categories
            card.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 0)
                {
                    dragStartPos = evt.mousePosition;
                    isDragging = false;
                }
            });

            card.RegisterCallback<MouseMoveEvent>(evt =>
            {
                if (evt.pressedButtons == 1 && !isDragging && Vector2.Distance(evt.mousePosition, dragStartPos) > 6f)
                {
                    isDragging = true;
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.paths = new string[] { item.AssetPath };
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.AssetPath);
                    if (obj != null)
                    {
                        DragAndDrop.objectReferences = new UnityEngine.Object[] { obj };
                    }
                    DragAndDrop.SetGenericData("EditorProGuid", item.Guid);
                    DragAndDrop.StartDrag(item.AssetName);
                }
            });

            // Context Menu
            card.RegisterCallback<ContextClickEvent>(evt =>
            {
                ShowAssetContextMenu(item);
                evt.StopPropagation();
            });

            return card;
        }

        private void ShowAssetContextMenu(ScriptableObjectItemData item)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Open in Inspector Tab"), false, () => SelectAsset(item.Guid));
            menu.AddItem(new GUIContent("Ping in Project"), false, () =>
            {
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(item.AssetPath);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            });

            bool isFav = EditorProSettings.instance.IsFavorite(item.Guid);
            menu.AddItem(new GUIContent(isFav ? "Remove Favorite" : "Add to Favorites"), false, () =>
            {
                EditorProSettings.instance.ToggleFavorite(item.Guid);
                RefreshBrowser();
                RefreshSidebar();
            });

            menu.AddSeparator(string.Empty);

            // Custom Categories sub-menu
            foreach (var cat in EditorProSettings.instance.CustomCategories)
            {
                bool inCat = cat.ContainsAsset(item.Guid);
                menu.AddItem(new GUIContent($"Add to Category/{cat.Name}"), inCat, () =>
                {
                    if (inCat)
                        EditorProSettings.instance.RemoveAssetFromCategory(cat.Id, item.Guid);
                    else
                        EditorProSettings.instance.AddAssetToCategory(cat.Id, item.Guid);

                    RefreshSidebar();
                    RefreshBrowser();
                });
            }

            // Remove from current category if viewing one
            if (EditorProSettings.instance.ActiveSection.StartsWith("Category:"))
            {
                string catId = EditorProSettings.instance.ActiveSection.Substring(9);
                menu.AddItem(new GUIContent("Remove from this Category"), false, () =>
                {
                    EditorProSettings.instance.RemoveAssetFromCategory(catId, item.Guid);
                    RefreshSidebar();
                    RefreshBrowser();
                });
            }

            menu.AddSeparator(string.Empty);

            menu.AddItem(new GUIContent("Duplicate Asset"), false, () =>
            {
                string dir = Path.GetDirectoryName(item.AssetPath);
                string newPath = Path.Combine(dir, $"{item.AssetName}_Copy.asset").Replace('\\', '/');
                newPath = AssetDatabase.GenerateUniqueAssetPath(newPath);
                if (AssetDatabase.CopyAsset(item.AssetPath, newPath))
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    string newGuid = AssetDatabase.AssetPathToGUID(newPath);
                    SelectAsset(newGuid);
                }
            });

            menu.AddItem(new GUIContent("Delete Asset..."), false, () =>
            {
                if (EditorUtility.DisplayDialog("Delete Asset", $"Are you sure you want to delete '{item.AssetName}'?", "Delete", "Cancel"))
                {
                    AssetDatabase.DeleteAsset(item.AssetPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    EditorProSettings.instance.RemoveAssetEverywhere(item.Guid);
                    RefreshAll();
                }
            });

            menu.ShowAsContext();
        }

        private void SelectAsset(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            EditorProSettings.instance.OpenTab(guid, false, false);
            EditorProSettings.instance.RecordRecent(guid, false);
            EditorProSettings.instance.SaveSettings();

            RefreshSidebar();
            RefreshInspector();

            if (EditorProSettings.instance.ViewMode == WorkspaceViewMode.Grid)
            {
                UpdateGridCardSelection(guid);
            }
            else if (multiColumnListView != null)
            {
                int idx = currentFilteredAssets.FindIndex(x => x.Guid == guid);
                if (idx >= 0 && multiColumnListView.selectedIndex != idx)
                {
                    multiColumnListView.SetSelection(idx);
                }
            }
        }

        #endregion

        #region Right Pane (Multi-Tab Inspector & Split View)

        private void BuildRightPane()
        {
            rightPane = new VisualElement();
            rightPane.style.flexGrow = 1;
            rightPane.style.backgroundColor = GetThemeColor(new Color(0.18f, 0.18f, 0.18f), new Color(0.88f, 0.88f, 0.88f));

            // Tab Strip Container
            var tabHeaderBar = new VisualElement();
            tabHeaderBar.style.flexDirection = FlexDirection.Row;
            tabHeaderBar.style.alignItems = Align.Center;
            tabHeaderBar.style.height = 30;
            tabHeaderBar.style.borderBottomWidth = 1;
            tabHeaderBar.style.borderBottomColor = GetBorderColor();
            tabHeaderBar.style.backgroundColor = GetThemeColor(new Color(0.15f, 0.15f, 0.15f), new Color(0.82f, 0.82f, 0.82f));

            tabStripScrollView = new ScrollView(ScrollViewMode.Horizontal);
            tabStripScrollView.style.flexGrow = 1;
            tabStripScrollView.style.height = 30;
            tabStripScrollView.contentContainer.style.flexDirection = FlexDirection.Row;
            tabStripScrollView.contentContainer.style.alignItems = Align.Center;
            tabHeaderBar.Add(tabStripScrollView);

            // Split View Button
            splitViewToggleBtn = new ToolbarButton(() =>
            {
                bool next = !EditorProSettings.instance.InspectorSplitView;
                EditorProSettings.instance.SetInspectorSplit(next);
                RefreshInspector();
            })
            {
                text = "Split",
                tooltip = "Toggle Side-by-Side Dual Inspector Split View"
            };
            StyleMiniButton(splitViewToggleBtn);
            tabHeaderBar.Add(splitViewToggleBtn);

            // Tab Options Menu ("...")
            var tabMenuBtn = new ToolbarButton(() =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Close All Tabs"), false, () =>
                {
                    EditorProSettings.instance.CloseAllTabs();
                    RefreshInspector();
                });
                menu.AddItem(new GUIContent("Close Other Tabs"), false, () =>
                {
                    EditorProSettings.instance.CloseOtherTabs(EditorProSettings.instance.ActiveTabGuid);
                    RefreshInspector();
                });
                menu.ShowAsContext();
            })
            {
                text = "...",
                tooltip = "Tab Options"
            };
            StyleMiniButton(tabMenuBtn);
            tabHeaderBar.Add(tabMenuBtn);

            rightPane.Add(tabHeaderBar);

            // Body container for InspectorElement(s)
            inspectorBodyContainer = new VisualElement();
            inspectorBodyContainer.style.flexGrow = 1;
            rightPane.Add(inspectorBodyContainer);
        }

        private void RefreshInspector()
        {
            RefreshTabStrip();
            RefreshInspectorBody();
        }

        private void RefreshTabStrip()
        {
            if (tabStripScrollView == null) return;
            tabStripScrollView.Clear();

            var settings = EditorProSettings.instance;
            var cache = EditorProAssetDatabaseCache.Instance;

            ApplyActiveButton(splitViewToggleBtn, settings.InspectorSplitView);

            foreach (var guid in settings.OpenTabGuids)
            {
                var item = cache.GetItemByGuid(guid);
                string title = item?.AssetName ?? "Asset";
                bool isActive = guid == settings.ActiveTabGuid;
                bool isPinned = settings.IsTabPinned(guid);

                var tab = new VisualElement();
                tab.style.flexDirection = FlexDirection.Row;
                tab.style.alignItems = Align.Center;
                tab.style.height = 24;
                tab.style.paddingLeft = 8;
                tab.style.paddingRight = 4;
                tab.style.marginRight = 2;
                tab.style.borderTopLeftRadius = 4;
                tab.style.borderTopRightRadius = 4;
                tab.style.borderTopWidth = 1;
                tab.style.borderLeftWidth = 1;
                tab.style.borderRightWidth = 1;

                if (isActive)
                {
                    tab.style.backgroundColor = GetThemeColor(new Color(0.22f, 0.22f, 0.22f), new Color(0.92f, 0.92f, 0.92f));
                    tab.style.borderTopColor = new Color(0.24f, 0.52f, 0.95f);
                    tab.style.borderLeftColor = GetBorderColor();
                    tab.style.borderRightColor = GetBorderColor();
                }
                else
                {
                    tab.style.backgroundColor = GetThemeColor(new Color(0.16f, 0.16f, 0.16f), new Color(0.85f, 0.85f, 0.85f));
                    tab.style.borderTopColor = GetBorderColor();
                    tab.style.borderLeftColor = GetBorderColor();
                    tab.style.borderRightColor = GetBorderColor();
                }

                var iconImg = new Image();
                iconImg.image = item?.Icon ?? EditorGUIUtility.IconContent("d_ScriptableObject Icon")?.image;
                iconImg.style.width = 12;
                iconImg.style.height = 12;
                iconImg.style.marginRight = 4;
                tab.Add(iconImg);

                var titleLbl = new Label(title);
                titleLbl.style.fontSize = 11;
                titleLbl.style.color = isActive ? GetTextColor() : GetMutedTextColor();
                titleLbl.style.marginRight = 4;
                tab.Add(titleLbl);

                // Pin Button
                var pinBtn = new Button(() =>
                {
                    settings.TogglePinTab(guid);
                    RefreshTabStrip();
                })
                {
                    text = isPinned ? "📌" : "·",
                    tooltip = isPinned ? "Pinned (will not close on selection)" : "Pin tab"
                };
                pinBtn.style.backgroundColor = StyleKeyword.Null;
                pinBtn.style.borderTopWidth = 0;
                pinBtn.style.borderBottomWidth = 0;
                pinBtn.style.borderLeftWidth = 0;
                pinBtn.style.borderRightWidth = 0;
                pinBtn.style.paddingLeft = 0;
                pinBtn.style.paddingRight = 0;
                pinBtn.style.paddingTop = 0;
                pinBtn.style.paddingBottom = 0;
                pinBtn.style.fontSize = 10;
                pinBtn.style.color = isPinned ? new Color(0.95f, 0.77f, 0.05f) : GetMutedTextColor();
                tab.Add(pinBtn);

                // Close Button
                var closeBtn = new Button(() =>
                {
                    settings.CloseTab(guid);
                    RefreshInspector();
                })
                {
                    text = "✕",
                    tooltip = "Close tab"
                };
                StyleMiniCloseButton(closeBtn);
                tab.Add(closeBtn);

                tab.RegisterCallback<ClickEvent>(evt =>
                {
                    if (evt.target != closeBtn && evt.target != pinBtn)
                    {
                        settings.SetActiveTab(guid);
                        RefreshInspector();
                        RefreshBrowser();
                    }
                });

                tabStripScrollView.Add(tab);
            }
        }

        private void RefreshInspectorBody()
        {
            if (inspectorBodyContainer == null) return;
            inspectorBodyContainer.Clear();

            var settings = EditorProSettings.instance;

            if (string.IsNullOrEmpty(settings.ActiveTabGuid))
            {
                var emptyMsg = new Label("No ScriptableObject Selected\nSelect an asset in the browser to view and edit properties.");
                emptyMsg.style.fontSize = 12;
                emptyMsg.style.unityTextAlign = TextAnchor.MiddleCenter;
                emptyMsg.style.color = GetMutedTextColor();
                emptyMsg.style.flexGrow = 1;
                inspectorBodyContainer.Add(emptyMsg);
                return;
            }

            if (!settings.InspectorSplitView)
            {
                // Single Inspector
                var singleInspector = BuildSingleInspectorView(settings.ActiveTabGuid);
                inspectorBodyContainer.Add(singleInspector);
            }
            else
            {
                // Side-by-Side Dual Split View
                var splitContainer = new VisualElement();
                splitContainer.style.flexDirection = FlexDirection.Row;
                splitContainer.style.flexGrow = 1;

                var leftHalf = BuildSingleInspectorView(settings.ActiveTabGuid);
                leftHalf.style.flexGrow = 1;
                leftHalf.style.width = new Length(50, LengthUnit.Percent);
                leftHalf.style.borderRightWidth = 1;
                leftHalf.style.borderRightColor = GetBorderColor();
                splitContainer.Add(leftHalf);

                var rightHalf = BuildSecondaryInspectorView();
                rightHalf.style.flexGrow = 1;
                rightHalf.style.width = new Length(50, LengthUnit.Percent);
                splitContainer.Add(rightHalf);

                inspectorBodyContainer.Add(splitContainer);
            }
        }

        private VisualElement BuildSingleInspectorView(string guid)
        {
            var container = new VisualElement();
            container.style.flexGrow = 1;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

            if (asset == null)
            {
                var missingLbl = new Label($"Asset could not be loaded at:\n{path}");
                missingLbl.style.fontSize = 11;
                missingLbl.style.color = Color.red;
                missingLbl.style.paddingLeft = 10;
                missingLbl.style.paddingTop = 10;
                container.Add(missingLbl);
                return container;
            }

            // Top Header Bar
            var topBar = new VisualElement();
            topBar.style.flexDirection = FlexDirection.Row;
            topBar.style.alignItems = Align.Center;
            topBar.style.paddingLeft = 10;
            topBar.style.paddingRight = 10;
            topBar.style.paddingTop = 6;
            topBar.style.paddingBottom = 6;
            topBar.style.borderBottomWidth = 1;
            topBar.style.borderBottomColor = GetBorderColor();

            var iconImg = new Image();
            iconImg.image = AssetDatabase.GetCachedIcon(path);
            iconImg.style.width = 20;
            iconImg.style.height = 20;
            iconImg.style.marginRight = 8;
            topBar.Add(iconImg);

            var titleContainer = new VisualElement();
            titleContainer.style.flexGrow = 1;

            var nameLbl = new Label(asset.name);
            nameLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLbl.style.fontSize = 12;
            nameLbl.style.color = GetTextColor();
            titleContainer.Add(nameLbl);

            var pathLbl = new Label(path);
            pathLbl.style.fontSize = 9;
            pathLbl.style.color = GetMutedTextColor();
            titleContainer.Add(pathLbl);

            topBar.Add(titleContainer);

            var pingBtn = new Button(() => EditorGUIUtility.PingObject(asset)) { text = "Ping", tooltip = "Reveal in Project Window" };
            StyleMiniButton(pingBtn);
            topBar.Add(pingBtn);

            container.Add(topBar);

            // InspectorElement in ScrollView
            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1;
            scrollView.style.paddingLeft = 8;
            scrollView.style.paddingRight = 8;
            scrollView.style.paddingTop = 8;
            scrollView.style.paddingBottom = 8;

            activeSerializedObject?.Dispose();
            activeSerializedObject = new SerializedObject(asset);
            var inspectorElement = new InspectorElement(activeSerializedObject);
            scrollView.Add(inspectorElement);

            container.Add(scrollView);
            return container;
        }

        private VisualElement BuildSecondaryInspectorView()
        {
            var container = new VisualElement();
            container.style.flexGrow = 1;

            var settings = EditorProSettings.instance;

            // Secondary selector header
            var topBar = new VisualElement();
            topBar.style.flexDirection = FlexDirection.Row;
            topBar.style.alignItems = Align.Center;
            topBar.style.paddingLeft = 10;
            topBar.style.paddingRight = 10;
            topBar.style.height = 32;
            topBar.style.borderBottomWidth = 1;
            topBar.style.borderBottomColor = GetBorderColor();

            var compareLbl = new Label("Compare with:");
            compareLbl.style.fontSize = 11;
            compareLbl.style.color = GetMutedTextColor();
            compareLbl.style.marginRight = 8;
            topBar.Add(compareLbl);

            var dropdown = new ToolbarMenu();
            dropdown.style.flexGrow = 1;

            // If secondary tab isn't set, default to first other open tab
            if (string.IsNullOrEmpty(settings.SecondaryTabGuid) || settings.SecondaryTabGuid == settings.ActiveTabGuid)
            {
                foreach (var g in settings.OpenTabGuids)
                {
                    if (g != settings.ActiveTabGuid)
                    {
                        settings.SetSecondaryTab(g);
                        break;
                    }
                }
            }

            string currentSecName = "(Select Asset)";
            if (!string.IsNullOrEmpty(settings.SecondaryTabGuid))
            {
                var secItem = EditorProAssetDatabaseCache.Instance.GetItemByGuid(settings.SecondaryTabGuid);
                if (secItem != null) currentSecName = secItem.AssetName;
            }
            dropdown.text = currentSecName;

            foreach (var g in settings.OpenTabGuids)
            {
                if (g == settings.ActiveTabGuid) continue;
                var gItem = EditorProAssetDatabaseCache.Instance.GetItemByGuid(g);
                string gName = gItem?.AssetName ?? g;
                string captureG = g;
                dropdown.menu.AppendAction(gName, _ =>
                {
                    settings.SetSecondaryTab(captureG);
                    RefreshInspector();
                });
            }
            topBar.Add(dropdown);
            container.Add(topBar);

            if (!string.IsNullOrEmpty(settings.SecondaryTabGuid))
            {
                string secPath = AssetDatabase.GUIDToAssetPath(settings.SecondaryTabGuid);
                var secAsset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(secPath);
                if (secAsset != null)
                {
                    var scrollView = new ScrollView(ScrollViewMode.Vertical);
                    scrollView.style.flexGrow = 1;
                    scrollView.style.paddingLeft = 8;
                    scrollView.style.paddingRight = 8;
                    scrollView.style.paddingTop = 8;
                    scrollView.style.paddingBottom = 8;

                    secondarySerializedObject?.Dispose();
                    secondarySerializedObject = new SerializedObject(secAsset);
                    var secInspector = new InspectorElement(secondarySerializedObject);
                    scrollView.Add(secInspector);
                    container.Add(scrollView);
                }
                else
                {
                    var missingLbl = new Label("Secondary asset could not be loaded.");
                    missingLbl.style.fontSize = 11;
                    missingLbl.style.color = Color.red;
                    missingLbl.style.paddingLeft = 10;
                    missingLbl.style.paddingTop = 10;
                    container.Add(missingLbl);
                }
            }
            else
            {
                var selectMsg = new Label("Open another asset tab to compare side-by-side.");
                selectMsg.style.fontSize = 11;
                selectMsg.style.unityTextAlign = TextAnchor.MiddleCenter;
                selectMsg.style.color = GetMutedTextColor();
                selectMsg.style.flexGrow = 1;
                container.Add(selectMsg);
            }

            return container;
        }

        #endregion

        #region Modals (Create Asset, Add Category, Settings)

        private void BuildModals()
        {
            modalOverlay = new VisualElement();
            modalOverlay.style.position = Position.Absolute;
            modalOverlay.style.left = 0;
            modalOverlay.style.top = 0;
            modalOverlay.style.right = 0;
            modalOverlay.style.bottom = 0;
            modalOverlay.style.backgroundColor = new Color(0, 0, 0, 0.72f);
            modalOverlay.style.alignItems = Align.Center;
            modalOverlay.style.justifyContent = Justify.Center;
            modalOverlay.style.display = DisplayStyle.None;

            BuildCreateAssetModal();
            BuildAddCategoryModal();
            BuildSettingsModal();

            rootContainer.Add(modalOverlay);
        }

        private void HideModals()
        {
            if (modalOverlay != null) modalOverlay.style.display = DisplayStyle.None;
            if (createAssetModal != null) createAssetModal.style.display = DisplayStyle.None;
            if (addCategoryModal != null) addCategoryModal.style.display = DisplayStyle.None;
            if (settingsModal != null) settingsModal.style.display = DisplayStyle.None;
        }

        // --- Create Asset Modal ---
        private void BuildCreateAssetModal()
        {
            createAssetModal = new VisualElement();
            createAssetModal.style.width = 520;
            createAssetModal.style.maxHeight = 580;
            createAssetModal.style.backgroundColor = GetThemeColor(new Color(0.2f, 0.2f, 0.2f), new Color(0.92f, 0.92f, 0.92f));
            createAssetModal.style.borderTopLeftRadius = 8;
            createAssetModal.style.borderTopRightRadius = 8;
            createAssetModal.style.borderBottomLeftRadius = 8;
            createAssetModal.style.borderBottomRightRadius = 8;
            createAssetModal.style.borderTopWidth = 1;
            createAssetModal.style.borderBottomWidth = 1;
            createAssetModal.style.borderLeftWidth = 1;
            createAssetModal.style.borderRightWidth = 1;
            createAssetModal.style.borderTopColor = new Color(0.24f, 0.52f, 0.95f);
            createAssetModal.style.borderBottomColor = new Color(0.24f, 0.52f, 0.95f);
            createAssetModal.style.borderLeftColor = new Color(0.24f, 0.52f, 0.95f);
            createAssetModal.style.borderRightColor = new Color(0.24f, 0.52f, 0.95f);
            createAssetModal.style.paddingLeft = 14;
            createAssetModal.style.paddingRight = 14;
            createAssetModal.style.paddingTop = 12;
            createAssetModal.style.paddingBottom = 12;

            modalOverlay.Add(createAssetModal);
        }

        private void ShowCreateAssetModal()
        {
            HideModals();
            modalOverlay.style.display = DisplayStyle.Flex;
            createAssetModal.style.display = DisplayStyle.Flex;
            createAssetModal.Clear();

            // Header
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 10;

            var titleLbl = new Label("Create New ScriptableObject Asset");
            titleLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLbl.style.fontSize = 13;
            titleLbl.style.color = GetTextColor();
            titleLbl.style.flexGrow = 1;
            header.Add(titleLbl);

            var closeBtn = new Button(HideModals) { text = "✕" };
            StyleMiniCloseButton(closeBtn);
            header.Add(closeBtn);
            createAssetModal.Add(header);

            // Search Bar for Types
            var searchTypeField = new ToolbarSearchField();
            searchTypeField.style.width = new Length(100, LengthUnit.Percent);
            searchTypeField.style.marginBottom = 8;
            createAssetModal.Add(searchTypeField);

            // Recent Created Types Chips
            var recentContainer = new VisualElement();
            recentContainer.style.flexDirection = FlexDirection.Row;
            recentContainer.style.flexWrap = Wrap.Wrap;
            recentContainer.style.marginBottom = 8;

            var recentTypes = EditorProSettings.instance.RecentCreatedTypeNames;
            if (recentTypes.Count > 0)
            {
                var rLbl = new Label("Recent:");
                rLbl.style.fontSize = 10;
                rLbl.style.color = GetMutedTextColor();
                rLbl.style.marginRight = 6;
                rLbl.style.alignSelf = Align.Center;
                recentContainer.Add(rLbl);

                foreach (var rTypeName in recentTypes)
                {
                    string shortName = rTypeName;
                    int lastDot = rTypeName.LastIndexOf('.');
                    if (lastDot >= 0) shortName = rTypeName.Substring(lastDot + 1);

                    var chip = new Button { text = shortName, tooltip = rTypeName };
                    StyleChipButton(chip);
                    recentContainer.Add(chip);
                }
            }
            createAssetModal.Add(recentContainer);

            // Type List View
            var typeScrollView = new ScrollView(ScrollViewMode.Vertical);
            typeScrollView.style.height = 200;
            typeScrollView.style.borderTopWidth = 1;
            typeScrollView.style.borderBottomWidth = 1;
            typeScrollView.style.borderLeftWidth = 1;
            typeScrollView.style.borderRightWidth = 1;
            typeScrollView.style.borderTopColor = GetBorderColor();
            typeScrollView.style.borderBottomColor = GetBorderColor();
            typeScrollView.style.borderLeftColor = GetBorderColor();
            typeScrollView.style.borderRightColor = GetBorderColor();
            typeScrollView.style.marginBottom = 10;
            createAssetModal.Add(typeScrollView);

            // Asset Name Field
            var nameField = new TextField("Asset Name:");
            nameField.value = "NewAsset.asset";
            nameField.style.marginBottom = 6;
            createAssetModal.Add(nameField);

            // Target Folder Field
            var folderRow = new VisualElement();
            folderRow.style.flexDirection = FlexDirection.Row;
            folderRow.style.alignItems = Align.Center;
            folderRow.style.marginBottom = 12;

            var folderField = new TextField("Target Folder:")
            {
                value = EditorProSettings.instance.DefaultCreationPath,
                style = { flexGrow = 1 }
            };
            folderRow.Add(folderField);

            var browseFolderBtn = new Button(() =>
            {
                string picked = EditorUtility.OpenFolderPanel("Select Target Folder", folderField.value, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    if (picked.StartsWith(Application.dataPath))
                    {
                        folderField.value = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Invalid Folder", "Target folder must be inside this project's 'Assets' directory.", "OK");
                    }
                }
            })
            {
                text = "Browse..."
            };
            StyleMiniButton(browseFolderBtn);
            folderRow.Add(browseFolderBtn);
            createAssetModal.Add(folderRow);

            // Action Buttons
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.FlexEnd;

            var cancelBtn = new Button(HideModals) { text = "Cancel" };
            StyleMiniButton(cancelBtn);
            btnRow.Add(cancelBtn);

            ScriptableObjectTypeInfo selectedType = null;
            VisualElement selectedRowEl = null;

            Action<ScriptableObjectTypeInfo, VisualElement> onSelectType = (info, el) =>
            {
                if (selectedRowEl != null) selectedRowEl.style.backgroundColor = StyleKeyword.Null;
                selectedType = info;
                selectedRowEl = el;
                if (selectedRowEl != null) selectedRowEl.style.backgroundColor = GetSelectedRowBgColor();
                nameField.value = $"New{info.Name}.asset";
            };

            // Wire up recent chips click
            for (int i = 0; i < recentContainer.childCount; i++)
            {
                if (recentContainer[i] is Button b)
                {
                    string targetFullName = b.tooltip;
                    b.clickable = new Clickable(() =>
                    {
                        var tInfo = EditorProAssetDatabaseCache.Instance.GetTypeInfo(targetFullName);
                        if (tInfo != null)
                        {
                            onSelectType(tInfo, null);
                        }
                    });
                }
            }

            var allTypes = EditorProAssetDatabaseCache.Instance.DiscoveredTypes;

            Action<string> populateTypes = filter =>
            {
                typeScrollView.Clear();
                foreach (var info in allTypes)
                {
                    if (!string.IsNullOrEmpty(filter))
                    {
                        bool match = info.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     info.Namespace.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     info.CategoryAttributeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!match) continue;
                    }

                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.paddingLeft = 6;
                    row.style.paddingRight = 6;
                    row.style.height = 24;

                    var img = new Image();
                    img.image = info.Icon;
                    img.style.width = 14;
                    img.style.height = 14;
                    img.style.marginRight = 6;
                    row.Add(img);

                    var nLbl = new Label(info.Name);
                    nLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
                    nLbl.style.fontSize = 11;
                    nLbl.style.color = GetTextColor();
                    nLbl.style.marginRight = 6;
                    row.Add(nLbl);

                    var nsLbl = new Label($"({info.Namespace})");
                    nsLbl.style.fontSize = 10;
                    nsLbl.style.color = GetMutedTextColor();
                    nsLbl.style.flexGrow = 1;
                    row.Add(nsLbl);

                    if (!string.IsNullOrEmpty(info.CategoryAttributeName))
                    {
                        var catPill = new Label(info.CategoryAttributeName);
                        StyleBadge(catPill, GetBadgeBgColor());
                        row.Add(catPill);
                    }

                    var captureInfo = info;
                    var captureRow = row;
                    row.RegisterCallback<ClickEvent>(evt => onSelectType(captureInfo, captureRow));

                    typeScrollView.Add(row);
                }
            };

            searchTypeField.RegisterValueChangedCallback(evt => populateTypes(evt.newValue));
            populateTypes(string.Empty);

            var createConfirmBtn = new Button(() =>
            {
                if (selectedType == null)
                {
                    EditorUtility.DisplayDialog("Error", "Please select a ScriptableObject type from the list.", "OK");
                    return;
                }

                string folder = folderField.value.Trim().Replace('\\', '/');
                if (string.IsNullOrEmpty(folder)) folder = "Assets";

                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string fileName = nameField.value.Trim();
                if (!fileName.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    fileName += ".asset";
                }

                string assetPath = Path.Combine(folder, fileName).Replace('\\', '/');
                assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

                var instance = ScriptableObject.CreateInstance(selectedType.Type);
                if (instance == null)
                {
                    EditorUtility.DisplayDialog("Error", $"Failed to instantiate ScriptableObject of type {selectedType.Name}.", "OK");
                    return;
                }

                AssetDatabase.CreateAsset(instance, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                EditorProSettings.instance.RecordCreatedType(selectedType.FullName);
                EditorProSettings.instance.DefaultCreationPath = folder;
                EditorProSettings.instance.SaveSettings();

                HideModals();

                // Select newly created asset
                string newGuid = AssetDatabase.AssetPathToGUID(assetPath);
                SelectAsset(newGuid);
            })
            {
                text = "Create Asset"
            };
            createConfirmBtn.style.backgroundColor = new StyleColor(new Color(0.18f, 0.49f, 0.76f));
            createConfirmBtn.style.color = Color.white;
            createConfirmBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            createConfirmBtn.style.marginLeft = 8;
            btnRow.Add(createConfirmBtn);

            createAssetModal.Add(btnRow);
        }

        // --- Add Category Modal ---
        private void BuildAddCategoryModal()
        {
            addCategoryModal = new VisualElement();
            addCategoryModal.style.width = 380;
            addCategoryModal.style.backgroundColor = GetThemeColor(new Color(0.2f, 0.2f, 0.2f), new Color(0.92f, 0.92f, 0.92f));
            addCategoryModal.style.borderTopLeftRadius = 8;
            addCategoryModal.style.borderTopRightRadius = 8;
            addCategoryModal.style.borderBottomLeftRadius = 8;
            addCategoryModal.style.borderBottomRightRadius = 8;
            addCategoryModal.style.borderTopWidth = 1;
            addCategoryModal.style.borderBottomWidth = 1;
            addCategoryModal.style.borderLeftWidth = 1;
            addCategoryModal.style.borderRightWidth = 1;
            addCategoryModal.style.borderTopColor = new Color(0.24f, 0.52f, 0.95f);
            addCategoryModal.style.borderBottomColor = new Color(0.24f, 0.52f, 0.95f);
            addCategoryModal.style.borderLeftColor = new Color(0.24f, 0.52f, 0.95f);
            addCategoryModal.style.borderRightColor = new Color(0.24f, 0.52f, 0.95f);
            addCategoryModal.style.paddingLeft = 14;
            addCategoryModal.style.paddingRight = 14;
            addCategoryModal.style.paddingTop = 12;
            addCategoryModal.style.paddingBottom = 12;

            modalOverlay.Add(addCategoryModal);
        }

        private void ShowAddCategoryModal()
        {
            HideModals();
            modalOverlay.style.display = DisplayStyle.Flex;
            addCategoryModal.style.display = DisplayStyle.Flex;
            addCategoryModal.Clear();

            // Header
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 10;

            var titleLbl = new Label("Create Custom Category");
            titleLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLbl.style.fontSize = 13;
            titleLbl.style.color = GetTextColor();
            titleLbl.style.flexGrow = 1;
            header.Add(titleLbl);

            var closeBtn = new Button(HideModals) { text = "✕" };
            StyleMiniCloseButton(closeBtn);
            header.Add(closeBtn);
            addCategoryModal.Add(header);

            // Name Field
            var nameField = new TextField("Category Name:");
            nameField.value = "New Category";
            nameField.style.marginBottom = 10;
            addCategoryModal.Add(nameField);

            // Color Palette Selector
            var colorLabel = new Label("Tag Color:");
            colorLabel.style.fontSize = 11;
            colorLabel.style.color = GetTextColor();
            colorLabel.style.marginBottom = 4;
            addCategoryModal.Add(colorLabel);

            var colorPaletteRow = new VisualElement();
            colorPaletteRow.style.flexDirection = FlexDirection.Row;
            colorPaletteRow.style.marginBottom = 14;

            string[] palette = { "#3498DB", "#2ECC71", "#E74C3C", "#F39C12", "#9B59B6", "#1ABC9C", "#E67E22", "#E91E63" };
            string selectedHex = palette[0];
            VisualElement selectedColorChip = null;

            foreach (var hex in palette)
            {
                var chip = new VisualElement();
                chip.style.width = 24;
                chip.style.height = 24;
                chip.style.marginRight = 8;
                chip.style.borderTopLeftRadius = 12;
                chip.style.borderTopRightRadius = 12;
                chip.style.borderBottomLeftRadius = 12;
                chip.style.borderBottomRightRadius = 12;
                chip.style.borderTopWidth = 2;
                chip.style.borderBottomWidth = 2;
                chip.style.borderLeftWidth = 2;
                chip.style.borderRightWidth = 2;
                chip.style.borderTopColor = Color.clear;
                chip.style.borderBottomColor = Color.clear;
                chip.style.borderLeftColor = Color.clear;
                chip.style.borderRightColor = Color.clear;

                if (ColorUtility.TryParseHtmlString(hex, out var col))
                {
                    chip.style.backgroundColor = col;
                }

                string captureHex = hex;
                chip.RegisterCallback<ClickEvent>(evt =>
                {
                    if (selectedColorChip != null)
                    {
                        selectedColorChip.style.borderTopColor = Color.clear;
                        selectedColorChip.style.borderBottomColor = Color.clear;
                        selectedColorChip.style.borderLeftColor = Color.clear;
                        selectedColorChip.style.borderRightColor = Color.clear;
                    }
                    selectedHex = captureHex;
                    selectedColorChip = chip;
                    chip.style.borderTopColor = Color.white;
                    chip.style.borderBottomColor = Color.white;
                    chip.style.borderLeftColor = Color.white;
                    chip.style.borderRightColor = Color.white;
                });

                if (hex == selectedHex)
                {
                    selectedColorChip = chip;
                    chip.style.borderTopColor = Color.white;
                    chip.style.borderBottomColor = Color.white;
                    chip.style.borderLeftColor = Color.white;
                    chip.style.borderRightColor = Color.white;
                }

                colorPaletteRow.Add(chip);
            }
            addCategoryModal.Add(colorPaletteRow);

            // Action Buttons
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.FlexEnd;

            var cancelBtn = new Button(HideModals) { text = "Cancel" };
            StyleMiniButton(cancelBtn);
            btnRow.Add(cancelBtn);

            var createConfirmBtn = new Button(() =>
            {
                string catName = nameField.value.Trim();
                if (string.IsNullOrEmpty(catName))
                {
                    EditorUtility.DisplayDialog("Error", "Category name cannot be empty.", "OK");
                    return;
                }

                EditorProSettings.instance.AddCategory(catName, "d_Folder Icon", selectedHex);
                HideModals();
                RefreshSidebar();
            })
            {
                text = "Create Category"
            };
            createConfirmBtn.style.backgroundColor = new StyleColor(new Color(0.18f, 0.49f, 0.76f));
            createConfirmBtn.style.color = Color.white;
            createConfirmBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            createConfirmBtn.style.marginLeft = 8;
            btnRow.Add(createConfirmBtn);

            addCategoryModal.Add(btnRow);
        }

        // --- Settings Modal ---
        private void BuildSettingsModal()
        {
            settingsModal = new VisualElement();
            settingsModal.style.width = 460;
            settingsModal.style.backgroundColor = GetThemeColor(new Color(0.2f, 0.2f, 0.2f), new Color(0.92f, 0.92f, 0.92f));
            settingsModal.style.borderTopLeftRadius = 8;
            settingsModal.style.borderTopRightRadius = 8;
            settingsModal.style.borderBottomLeftRadius = 8;
            settingsModal.style.borderBottomRightRadius = 8;
            settingsModal.style.borderTopWidth = 1;
            settingsModal.style.borderBottomWidth = 1;
            settingsModal.style.borderLeftWidth = 1;
            settingsModal.style.borderRightWidth = 1;
            settingsModal.style.borderTopColor = new Color(0.24f, 0.52f, 0.95f);
            settingsModal.style.borderBottomColor = new Color(0.24f, 0.52f, 0.95f);
            settingsModal.style.borderLeftColor = new Color(0.24f, 0.52f, 0.95f);
            settingsModal.style.borderRightColor = new Color(0.24f, 0.52f, 0.95f);
            settingsModal.style.paddingLeft = 14;
            settingsModal.style.paddingRight = 14;
            settingsModal.style.paddingTop = 12;
            settingsModal.style.paddingBottom = 12;

            modalOverlay.Add(settingsModal);
        }

        private void ShowSettingsModal()
        {
            HideModals();
            modalOverlay.style.display = DisplayStyle.Flex;
            settingsModal.style.display = DisplayStyle.Flex;
            settingsModal.Clear();

            // Header
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 10;

            var titleLbl = new Label("EditorPro Workspace Settings");
            titleLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLbl.style.fontSize = 13;
            titleLbl.style.color = GetTextColor();
            titleLbl.style.flexGrow = 1;
            header.Add(titleLbl);

            var closeBtn = new Button(HideModals) { text = "✕" };
            StyleMiniCloseButton(closeBtn);
            header.Add(closeBtn);
            settingsModal.Add(header);

            // Default Creation Path
            var defPathField = new TextField("Default Creation Path:")
            {
                value = EditorProSettings.instance.DefaultCreationPath,
                style = { marginBottom = 10 }
            };
            defPathField.RegisterValueChangedCallback(evt =>
            {
                EditorProSettings.instance.DefaultCreationPath = evt.newValue;
                EditorProSettings.instance.SaveSettings();
            });
            settingsModal.Add(defPathField);

            // Excluded Folders Section
            var exclHeaderRow = new VisualElement();
            exclHeaderRow.style.flexDirection = FlexDirection.Row;
            exclHeaderRow.style.alignItems = Align.Center;
            exclHeaderRow.style.marginBottom = 6;

            var exclTitle = new Label("Excluded Folders (Hidden from Workspace):");
            exclTitle.style.fontSize = 11;
            exclTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            exclTitle.style.color = GetTextColor();
            exclTitle.style.flexGrow = 1;
            exclHeaderRow.Add(exclTitle);

            var addFolderBtn = new Button(() =>
            {
                string picked = EditorUtility.OpenFolderPanel("Select Folder to Exclude", "Assets", string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    string relPath = picked;
                    if (picked.StartsWith(Application.dataPath))
                    {
                        relPath = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
                    }
                    EditorProSettings.instance.AddExcludedFolder(relPath);
                    EditorProAssetDatabaseCache.Instance.Refresh(true);
                    ShowSettingsModal();
                }
            })
            {
                text = "+ Add Folder..."
            };
            StyleMiniButton(addFolderBtn);
            exclHeaderRow.Add(addFolderBtn);
            settingsModal.Add(exclHeaderRow);

            var exclScrollView = new ScrollView(ScrollViewMode.Vertical);
            exclScrollView.style.height = 160;
            exclScrollView.style.borderTopWidth = 1;
            exclScrollView.style.borderBottomWidth = 1;
            exclScrollView.style.borderLeftWidth = 1;
            exclScrollView.style.borderRightWidth = 1;
            exclScrollView.style.borderTopColor = GetBorderColor();
            exclScrollView.style.borderBottomColor = GetBorderColor();
            exclScrollView.style.borderLeftColor = GetBorderColor();
            exclScrollView.style.borderRightColor = GetBorderColor();
            exclScrollView.style.marginBottom = 12;

            foreach (var folder in EditorProSettings.instance.ExcludedFolders)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.height = 24;
                row.style.paddingLeft = 8;
                row.style.paddingRight = 6;

                var fLbl = new Label(folder);
                fLbl.style.fontSize = 11;
                fLbl.style.color = GetTextColor();
                fLbl.style.flexGrow = 1;
                row.Add(fLbl);

                string captureFolder = folder;
                var remBtn = new Button(() =>
                {
                    EditorProSettings.instance.RemoveExcludedFolder(captureFolder);
                    EditorProAssetDatabaseCache.Instance.Refresh(true);
                    ShowSettingsModal();
                })
                {
                    text = "✕"
                };
                StyleMiniCloseButton(remBtn);
                row.Add(remBtn);

                exclScrollView.Add(row);
            }
            settingsModal.Add(exclScrollView);

            // Close button
            var doneBtn = new Button(HideModals) { text = "Done" };
            StyleMiniButton(doneBtn);
            doneBtn.style.alignSelf = Align.FlexEnd;
            settingsModal.Add(doneBtn);
        }

        #endregion

        #region Procedural Styling Helpers

        private static Color GetThemeColor(Color dark, Color light)
        {
            return EditorGUIUtility.isProSkin ? dark : light;
        }

        private static Color GetBorderColor()
        {
            return GetThemeColor(new Color(0.24f, 0.24f, 0.24f), new Color(0.75f, 0.75f, 0.75f));
        }

        private static Color GetTextColor()
        {
            return GetThemeColor(new Color(0.85f, 0.85f, 0.85f), new Color(0.12f, 0.12f, 0.12f));
        }

        private static Color GetMutedTextColor()
        {
            return GetThemeColor(new Color(0.6f, 0.6f, 0.6f), new Color(0.4f, 0.4f, 0.4f));
        }

        private static Color GetCardBgColor()
        {
            return GetThemeColor(new Color(0.2f, 0.2f, 0.2f), new Color(0.92f, 0.92f, 0.92f));
        }

        private static Color GetBadgeBgColor()
        {
            return GetThemeColor(new Color(0.12f, 0.12f, 0.12f, 0.85f), new Color(0.8f, 0.8f, 0.8f, 0.9f));
        }

        private static Color GetSelectedRowBgColor()
        {
            return GetThemeColor(new Color(0.18f, 0.35f, 0.55f, 0.65f), new Color(0.65f, 0.8f, 0.98f, 0.75f));
        }

        private static void StyleBadge(Label badge, Color bgColor)
        {
            badge.style.fontSize = 9;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.paddingLeft = 6;
            badge.style.paddingRight = 6;
            badge.style.paddingTop = 1;
            badge.style.paddingBottom = 1;
            badge.style.borderTopLeftRadius = 8;
            badge.style.borderTopRightRadius = 8;
            badge.style.borderBottomLeftRadius = 8;
            badge.style.borderBottomRightRadius = 8;
            badge.style.backgroundColor = bgColor;
            badge.style.color = GetMutedTextColor();
        }

        private static void StyleMiniButton(Button btn)
        {
            btn.style.height = 20;
            btn.style.fontSize = 10;
            btn.style.paddingLeft = 6;
            btn.style.paddingRight = 6;
            btn.style.paddingTop = 1;
            btn.style.paddingBottom = 1;
            btn.style.borderTopLeftRadius = 3;
            btn.style.borderTopRightRadius = 3;
            btn.style.borderBottomLeftRadius = 3;
            btn.style.borderBottomRightRadius = 3;
        }

        private static void StyleChipButton(Button btn)
        {
            btn.style.height = 18;
            btn.style.fontSize = 9;
            btn.style.paddingLeft = 6;
            btn.style.paddingRight = 6;
            btn.style.paddingTop = 0;
            btn.style.paddingBottom = 0;
            btn.style.marginRight = 4;
            btn.style.marginBottom = 4;
            btn.style.borderTopLeftRadius = 9;
            btn.style.borderTopRightRadius = 9;
            btn.style.borderBottomLeftRadius = 9;
            btn.style.borderBottomRightRadius = 9;
        }

        private static void StyleMiniCloseButton(Button btn)
        {
            btn.style.width = 16;
            btn.style.height = 16;
            btn.style.paddingLeft = 0;
            btn.style.paddingRight = 0;
            btn.style.paddingTop = 0;
            btn.style.paddingBottom = 0;
            btn.style.fontSize = 9;
            btn.style.backgroundColor = StyleKeyword.Null;
            btn.style.borderTopWidth = 0;
            btn.style.borderBottomWidth = 0;
            btn.style.borderLeftWidth = 0;
            btn.style.borderRightWidth = 0;
            btn.style.color = GetMutedTextColor();
        }

        private static void ApplyActiveButton(ToolbarButton btn, bool isActive)
        {
            if (btn == null) return;
            if (isActive)
            {
                btn.style.backgroundColor = new StyleColor(new Color(0.18f, 0.45f, 0.75f, 0.7f));
                btn.style.color = Color.white;
            }
            else
            {
                btn.style.backgroundColor = StyleKeyword.Null;
                btn.style.color = GetTextColor();
            }
        }

        #endregion
    }
}
