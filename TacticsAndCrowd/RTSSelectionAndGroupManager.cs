using System;
using System.Collections.Generic;
using UnityEngine;
using GameSystems.Vision;

namespace GameSystems.RTS
{
    // =========================================================================
    // 1. ZÁKLADNÍ ROZHRANÍ PRO OZNAČITELNÉ OBJEKTY (SELECTABLES)
    // =========================================================================

    /// <summary>
    /// Rozhraní pro jakoukoliv jednotku nebo budovu, kterou lze v RTS vybrat kurzorem,
    /// tažením myši (marquee box) nebo přiřadit do kontrolní skupiny (Ctrl + 0..9).
    /// </summary>
    public interface ISelectable
    {
        Transform Transform { get; }
        Bounds SelectionBounds { get; }
        string TypeId { get; } // Identifikátor typu jednotky (např. "unit_marine", "unit_tank", "bld_barracks")
        bool IsSelected { get; }

        void OnSelected();
        void OnDeselected();
    }

    // =========================================================================
    // 2. HLAVNÍ MANAŽER VÝBĚRU A KONTROLNÍCH SKUPIN (RTS SELECTION MANAGER)
    // =========================================================================

    /// <summary>
    /// Kompletní RTS systém výběru jednotek a budov.
    /// Podporuje jednoduchý klik, obdélníkový výběr tažením (screen-space marquee box),
    /// Shift-přidávání, Ctrl-invertování, dvojklik na stejný typ jednotek na obrazovce,
    /// kontrolní skupiny (Ctrl + 0..9, dvojité stisknutí pro vycentrování kamery)
    /// a dispečink rozkazů k pohybu a útoku propojitelný s FormationBrain a FlowFieldPathfinder.
    /// </summary>
    [DisallowMultipleComponent]
    public class RTSSelectionAndGroupManager : MonoBehaviour
    {
        public static RTSSelectionAndGroupManager Instance { get; private set; }

        // =========================================================================
        // VEŘEJNÉ UDÁLOSTI (PUBLIC EVENTS)
        // =========================================================================

        /// <summary>Vyvoláno při jakékoliv změně aktuálního výběru jednotek.</summary>
        public static event Action<IReadOnlyList<ISelectable>> OnSelectionChanged;

        /// <summary>Vyvoláno při vydání rozkazu k přesunu (vybrané jednotky, cílová pozice).</summary>
        public static event Action<IReadOnlyList<ISelectable>, Vector3> OnIssueMoveCommand;

        /// <summary>Vyvoláno při vydání rozkazu k útoku / interakci (vybrané jednotky, cíl).</summary>
        public static event Action<IReadOnlyList<ISelectable>, ISelectable> OnIssueAttackCommand;

        /// <summary>Vyvoláno při uložení kontrolní skupiny (číslo 0..9, jednotky).</summary>
        public static event Action<int, IReadOnlyList<ISelectable>> OnControlGroupAssigned;

        // =========================================================================
        // INSPEKTOR KONFIGURACE
        // =========================================================================

        [Header("Kamera a vrstvy")]
        [SerializeField] private Camera mainCamera;
        [SerializeField] private LayerMask selectableLayerMask = ~0;
        [SerializeField] private LayerMask groundLayerMask = ~0;

        [Header("Vizuál výběrového obdélníku (Marquee Box)")]
        [SerializeField] private Color boxBorderColor = new Color(0.2f, 1f, 0.2f, 0.9f);
        [SerializeField] private Color boxFillColor = new Color(0.2f, 1f, 0.2f, 0.15f);
        [SerializeField, Range(1, 4)] private int borderWidth = 2;
        [SerializeField, Range(2f, 20f)] private float dragThresholdPixels = 5f;

        [Header("Časové limity")]
        [SerializeField, Range(0.15f, 0.5f)] private float doubleClickThreshold = 0.3f;
        [SerializeField, Range(0.15f, 0.5f)] private float doubleTapGroupThreshold = 0.35f;

        // =========================================================================
        // VNITŘNÍ REGISTRY A STAVOVÁ DATA (ZERO-GC)
        // =========================================================================

        private readonly List<ISelectable> allRegisteredSelectables = new List<ISelectable>(256);
        private readonly List<ISelectable> currentSelection = new List<ISelectable>(128);

        // 10 kontrolních skupin (0 až 9)
        private readonly List<ISelectable>[] controlGroups = new List<ISelectable>[10];

        // Sledování tažení myší
        private bool isDraggingMarquee = false;
        private Vector3 mouseDragStartScreenPos;

        // Sledování dvojkliku myši
        private float lastLeftClickTime = 0f;
        private ISelectable lastClickedSelectable = null;

        // Sledování dvojitého stisku číselných kláves pro centrování kamery
        private int lastPressedGroupIndex = -1;
        private float lastGroupPressTime = 0f;

        // Pre-alokovaná textura pro OnGUI vykreslení obdélníku
        private static Texture2D whiteTexture;

        // =========================================================================
        // VEŘEJNÉ VLASTNOSTI
        // =========================================================================

        public IReadOnlyList<ISelectable> CurrentSelection => currentSelection;
        public int SelectionCount => currentSelection.Count;

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

            if (mainCamera == null)
            {
                mainCamera = Camera.main;
            }

            for (int i = 0; i < 10; i++)
            {
                controlGroups[i] = new List<ISelectable>(32);
            }

            if (whiteTexture == null)
            {
                whiteTexture = new Texture2D(1, 1);
                whiteTexture.SetPixel(0, 0, Color.white);
                whiteTexture.Apply();
            }
        }

        private void Update()
        {
            HandleSelectionInput();
            HandleCommandInput();
            HandleControlGroupsInput();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // =========================================================================
        // VÝBĚR JEDNOTEK: KLIK, DVOJKLIK A MARQUEE BOX
        // =========================================================================

        private void HandleSelectionInput()
        {
            // 1. Stisknutí levého tlačítka myši (začátek výběru)
            if (Input.GetMouseButtonDown(0))
            {
                mouseDragStartScreenPos = Input.mousePosition;
                isDraggingMarquee = false;
            }

            // 2. Tažení myší (detekce překročení prahu)
            if (Input.GetMouseButton(0))
            {
                if (!isDraggingMarquee && Vector3.Distance(Input.mousePosition, mouseDragStartScreenPos) > dragThresholdPixels)
                {
                    isDraggingMarquee = true;
                }
            }

            // 3. Uvolnění levého tlačítka myši
            if (Input.GetMouseButtonUp(0))
            {
                bool isShift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool isCtrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

                if (isDraggingMarquee)
                {
                    // Dokončení obdélníkového výběru
                    ExecuteMarqueeBoxSelection(mouseDragStartScreenPos, Input.mousePosition, isShift, isCtrl);
                    isDraggingMarquee = false;
                }
                else
                {
                    // Jednoduchý klik nebo dvojklik
                    ExecuteClickSelection(Input.mousePosition, isShift, isCtrl);
                }
            }
        }

        private void ExecuteClickSelection(Vector3 screenPos, bool isShift, bool isCtrl)
        {
            Ray ray = mainCamera.ScreenPointToRay(screenPos);
            ISelectable hitSelectable = null;

            if (Physics.Raycast(ray, out RaycastHit hit, 500f, selectableLayerMask))
            {
                hitSelectable = hit.collider.GetComponentInParent<ISelectable>();
            }

            float timeSinceLastClick = Time.time - lastLeftClickTime;
            lastLeftClickTime = Time.time;

            // Kontrola dvojkliku na stejný typ jednotek
            if (hitSelectable != null && hitSelectable == lastClickedSelectable && timeSinceLastClick <= doubleClickThreshold)
            {
                SelectAllVisibleOfType(hitSelectable.TypeId, isShift);
                lastClickedSelectable = null;
                return;
            }

            lastClickedSelectable = hitSelectable;

            if (hitSelectable == null)
            {
                if (!isShift && !isCtrl)
                {
                    ClearSelection();
                }
                return;
            }

            // Aplikace výběru podle modifikátorů
            if (isCtrl)
            {
                // Invertování výběru
                if (currentSelection.Contains(hitSelectable))
                {
                    Deselect(hitSelectable);
                }
                else
                {
                    Select(hitSelectable, true);
                }
            }
            else if (isShift)
            {
                // Přidání do výběru
                Select(hitSelectable, true);
            }
            else
            {
                // Výhradní výběr jediné jednotky
                ClearSelection();
                Select(hitSelectable, true);
            }
        }

        private void ExecuteMarqueeBoxSelection(Vector3 startScreen, Vector3 endScreen, bool isShift, bool isCtrl)
        {
            if (!isShift && !isCtrl)
            {
                ClearSelection();
            }

            Rect screenRect = GetScreenRect(startScreen, endScreen);
            int total = allRegisteredSelectables.Count;

            for (int i = 0; i < total; i++)
            {
                ISelectable candidate = allRegisteredSelectables[i];
                if (candidate == null || candidate.Transform == null) continue;

                Vector3 screenPoint = mainCamera.WorldToScreenPoint(candidate.Transform.position);

                // Kontrola, zda je jednotka před kamerou a uvnitř obdélníku
                if (screenPoint.z > 0f && screenRect.Contains(new Vector2(screenPoint.x, screenPoint.y)))
                {
                    // Volitelná kontrola mlhy války (skryté nepřátelské jednotky v mlze nelze vybrat)
                    if (FogOfWarManager.Instance != null && !FogOfWarManager.Instance.IsWorldPositionVisible(candidate.Transform.position))
                    {
                        continue;
                    }

                    if (isCtrl)
                    {
                        if (currentSelection.Contains(candidate)) Deselect(candidate);
                        else Select(candidate, true);
                    }
                    else
                    {
                        Select(candidate, true);
                    }
                }
            }
        }

        /// <summary>
        /// Vybere všechny viditelné jednotky stejného typu na obrazovce (Dvojklik).
        /// </summary>
        public void SelectAllVisibleOfType(string typeId, bool additive = false)
        {
            if (string.IsNullOrEmpty(typeId)) return;

            if (!additive)
            {
                ClearSelection();
            }

            Rect screenBounds = new Rect(0, 0, Screen.width, Screen.height);
            int total = allRegisteredSelectables.Count;

            for (int i = 0; i < total; i++)
            {
                ISelectable candidate = allRegisteredSelectables[i];
                if (candidate == null || candidate.TypeId != typeId) continue;

                Vector3 screenPoint = mainCamera.WorldToScreenPoint(candidate.Transform.position);
                if (screenPoint.z > 0f && screenBounds.Contains(new Vector2(screenPoint.x, screenPoint.y)))
                {
                    Select(candidate, true);
                }
            }
        }

        // =========================================================================
        // DISPEČINK ROZKAZŮ K POHYBU A ÚTOKU (RIGHT CLICK)
        // =========================================================================

        private void HandleCommandInput()
        {
            if (currentSelection.Count == 0) return;

            if (Input.GetMouseButtonDown(1)) // Pravé tlačítko myši
            {
                Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

                // 1. Kontrola, zda hráč kliknul na cíl (nepřítel, budova, interaktivní objekt)
                if (Physics.Raycast(ray, out RaycastHit targetHit, 500f, selectableLayerMask))
                {
                    ISelectable target = targetHit.collider.GetComponentInParent<ISelectable>();
                    if (target != null && !currentSelection.Contains(target))
                    {
                        OnIssueAttackCommand?.Invoke(currentSelection, target);
                        return;
                    }
                }

                // 2. Kliknutí do země (přesun formace / navigace)
                if (Physics.Raycast(ray, out RaycastHit groundHit, 500f, groundLayerMask))
                {
                    OnIssueMoveCommand?.Invoke(currentSelection, groundHit.point);
                }
            }
        }

        // =========================================================================
        // KONTROLNÍ SKUPINY (CTRL + 0..9 A DVOJITÉ STISKNUTÍ)
        // =========================================================================

        private void HandleControlGroupsInput()
        {
            for (int i = 0; i < 10; i++)
            {
                KeyCode key = KeyCode.Alpha0 + i;
                if (Input.GetKeyDown(key))
                {
                    bool isCtrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

                    if (isCtrl)
                    {
                        // Uložení kontrolní skupiny: Ctrl + 0..9
                        SaveControlGroup(i);
                    }
                    else
                    {
                        // Vyvolání kontrolní skupiny: 0..9
                        RecallControlGroup(i);
                    }
                }
            }
        }

        private void SaveControlGroup(int groupIndex)
        {
            List<ISelectable> group = controlGroups[groupIndex];
            group.Clear();

            int count = currentSelection.Count;
            for (int i = 0; i < count; i++)
            {
                group.Add(currentSelection[i]);
            }

            OnControlGroupAssigned?.Invoke(groupIndex, group);
        }

        private void RecallControlGroup(int groupIndex)
        {
            List<ISelectable> group = controlGroups[groupIndex];
            if (group.Count == 0) return;

            // Vyčištění zničených instancí
            for (int i = group.Count - 1; i >= 0; i--)
            {
                if (group[i] == null || group[i].Transform == null)
                {
                    group.RemoveAt(i);
                }
            }

            ClearSelection();
            for (int i = 0; i < group.Count; i++)
            {
                Select(group[i], true);
            }

            // Dvojité stisknutí stejné skupiny: vycentrování kamery na těžiště skupiny
            float timeSinceLastPress = Time.time - lastGroupPressTime;
            if (lastPressedGroupIndex == groupIndex && timeSinceLastPress <= doubleTapGroupThreshold)
            {
                CenterCameraOnSelection();
            }

            lastPressedGroupIndex = groupIndex;
            lastGroupPressTime = Time.time;
        }

        private void CenterCameraOnSelection()
        {
            if (currentSelection.Count == 0 || mainCamera == null) return;

            Vector3 centroid = Vector3.zero;
            int validCount = 0;

            for (int i = 0; i < currentSelection.Count; i++)
            {
                if (currentSelection[i]?.Transform != null)
                {
                    centroid += currentSelection[i].Transform.position;
                    validCount++;
                }
            }

            if (validCount > 0)
            {
                centroid /= validCount;
                Vector3 cameraOffset = mainCamera.transform.position - (centroid + Vector3.forward * -10f + Vector3.up * 15f);
                mainCamera.transform.position -= cameraOffset * 0.8f; // Plynulý posun směrem k těžišti
            }
        }

        // =========================================================================
        // VEŘEJNÉ API PRO SPRÁVU VÝBĚRU
        // =========================================================================

        public void RegisterSelectable(ISelectable selectable)
        {
            if (selectable != null && !allRegisteredSelectables.Contains(selectable))
            {
                allRegisteredSelectables.Add(selectable);
            }
        }

        public void UnregisterSelectable(ISelectable selectable)
        {
            if (selectable != null)
            {
                Deselect(selectable);
                allRegisteredSelectables.Remove(selectable);

                // Odstranění z kontrolních skupin
                for (int i = 0; i < 10; i++)
                {
                    controlGroups[i].Remove(selectable);
                }
            }
        }

        public void Select(ISelectable selectable, bool additive = false)
        {
            if (selectable == null) return;

            if (!additive)
            {
                ClearSelection();
            }

            if (!currentSelection.Contains(selectable))
            {
                currentSelection.Add(selectable);
                selectable.OnSelected();
                OnSelectionChanged?.Invoke(currentSelection);
            }
        }

        public void Deselect(ISelectable selectable)
        {
            if (selectable != null && currentSelection.Remove(selectable))
            {
                selectable.OnDeselected();
                OnSelectionChanged?.Invoke(currentSelection);
            }
        }

        public void ClearSelection()
        {
            int count = currentSelection.Count;
            for (int i = 0; i < count; i++)
            {
                currentSelection[i]?.OnDeselected();
            }

            currentSelection.Clear();
            OnSelectionChanged?.Invoke(currentSelection);
        }

        // =========================================================================
        // ONGUI VYKRESLOVÁNÍ VÝBĚROVÉHO OBDÉLNÍKU (MARQUEE BOX)
        // =========================================================================

        private void OnGUI()
        {
            if (!isDraggingMarquee) return;

            Rect screenRect = GetScreenRect(mouseDragStartScreenPos, Input.mousePosition);
            Rect guiRect = new Rect(screenRect.x, Screen.height - screenRect.y - screenRect.height, screenRect.width, screenRect.height);

            // Výplň obdélníku
            GUI.color = boxFillColor;
            GUI.DrawTexture(guiRect, whiteTexture);

            // Rámeček
            GUI.color = boxBorderColor;
            GUI.DrawTexture(new Rect(guiRect.x, guiRect.y, guiRect.width, borderWidth), whiteTexture);
            GUI.DrawTexture(new Rect(guiRect.x, guiRect.yMax - borderWidth, guiRect.width, borderWidth), whiteTexture);
            GUI.DrawTexture(new Rect(guiRect.x, guiRect.y, borderWidth, guiRect.height), whiteTexture);
            GUI.DrawTexture(new Rect(guiRect.xMax - borderWidth, guiRect.y, borderWidth, guiRect.height), whiteTexture);

            GUI.color = Color.white;
        }

        private static Rect GetScreenRect(Vector3 screenPos1, Vector3 screenPos2)
        {
            float xMin = Mathf.Min(screenPos1.x, screenPos2.x);
            float xMax = Mathf.Max(screenPos1.x, screenPos2.x);
            float yMin = Mathf.Min(screenPos1.y, screenPos2.y);
            float yMax = Mathf.Max(screenPos1.y, screenPos2.y);

            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }
    }
}
