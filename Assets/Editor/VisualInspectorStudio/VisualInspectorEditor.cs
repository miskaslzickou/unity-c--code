using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VisualInspectorStudio;

namespace VisualInspectorStudio.Editor
{
    /// <summary>
    /// Base UI Toolkit custom inspector providing Odin-inspired attribute drawing,
    /// BoxGroups, TabGroups, dynamic ShowIf/HideIf condition evaluation, ReadOnly fields,
    /// live ProgressBars, MinMaxSliders, RequiredField banners, Button execution, and Telemetry graphs.
    /// </summary>
    public abstract class VisualInspectorEditorBase : UnityEditor.Editor
    {
        private VisualElement rootVisualElement;

        public override VisualElement CreateInspectorGUI()
        {
            rootVisualElement = new VisualElement
            {
                name = "VisualInspectorRoot"
            };
            rootVisualElement.style.paddingLeft = 2;
            rootVisualElement.style.paddingRight = 2;
            rootVisualElement.style.paddingTop = 4;
            rootVisualElement.style.paddingBottom = 4;

            if (target == null || serializedObject == null)
                return rootVisualElement;

            Type targetType = target.GetType();

            // Cache reflection metadata for fields and methods
            var fieldMap = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
            Type currentType = targetType;
            while (currentType != null && currentType != typeof(MonoBehaviour) && currentType != typeof(ScriptableObject) && currentType != typeof(UnityEngine.Object))
            {
                FieldInfo[] fields = currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!fieldMap.ContainsKey(fields[i].Name))
                    {
                        fieldMap[fields[i].Name] = fields[i];
                    }
                }
                currentType = currentType.BaseType;
            }

            // Organize containers
            var defaultContainer = new VisualElement { name = "DefaultGroup" };
            var boxGroups = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
            var tabGroups = new Dictionary<string, Dictionary<string, VisualElement>>(StringComparer.Ordinal);
            var tabBars = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
            var tabActiveState = new Dictionary<string, string>(StringComparer.Ordinal);

            // Iterate through serialized properties
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (iterator.name == "m_Script")
                {
                    // Render disabled script field at top
                    var scriptField = new PropertyField(iterator);
                    scriptField.SetEnabled(false);
                    scriptField.style.marginBottom = 6;
                    defaultContainer.Add(scriptField);
                    continue;
                }

                SerializedProperty prop = serializedObject.FindProperty(iterator.name);
                if (prop == null) continue;

                fieldMap.TryGetValue(iterator.name, out FieldInfo fieldInfo);

                // Check attributes on field
                BoxGroupAttribute boxAttr = fieldInfo?.GetCustomAttribute<BoxGroupAttribute>();
                TabGroupAttribute tabAttr = fieldInfo?.GetCustomAttribute<TabGroupAttribute>();
                ShowIfAttribute showIfAttr = fieldInfo?.GetCustomAttribute<ShowIfAttribute>();
                HideIfAttribute hideIfAttr = fieldInfo?.GetCustomAttribute<HideIfAttribute>();
                ReadOnlyInspectorAttribute readOnlyAttr = fieldInfo?.GetCustomAttribute<ReadOnlyInspectorAttribute>();
                ProgressBarAttribute progressAttr = fieldInfo?.GetCustomAttribute<ProgressBarAttribute>();
                MinMaxSliderAttribute minMaxAttr = fieldInfo?.GetCustomAttribute<MinMaxSliderAttribute>();
                RequiredFieldAttribute requiredAttr = fieldInfo?.GetCustomAttribute<RequiredFieldAttribute>();
                TelemetryGraphAttribute telemetryAttr = fieldInfo?.GetCustomAttribute<TelemetryGraphAttribute>();
                TitleAttribute titleAttr = fieldInfo?.GetCustomAttribute<TitleAttribute>();
                IEnumerable<InfoBoxAttribute> infoBoxes = fieldInfo?.GetCustomAttributes<InfoBoxAttribute>();

                // Build property wrapper element
                var propWrapper = new VisualElement { name = $"Wrapper_{prop.name}" };
                propWrapper.style.marginBottom = 2;

                // 1. Title Attribute
                if (titleAttr != null)
                {
                    propWrapper.Add(CreateTitleElement(titleAttr));
                }

                // 2. InfoBox Attributes
                if (infoBoxes != null)
                {
                    foreach (var info in infoBoxes)
                    {
                        propWrapper.Add(CreateInfoBoxElement(info));
                    }
                }

                // 3. RequiredField Banner
                if (requiredAttr != null && prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    propWrapper.Add(CreateRequiredFieldBanner(prop, requiredAttr));
                }

                // 4. Custom Property Drawer / Control
                if (progressAttr != null && IsNumericProperty(prop))
                {
                    propWrapper.Add(CreateProgressBarElement(prop, progressAttr));
                }
                else if (minMaxAttr != null && (prop.propertyType == SerializedPropertyType.Vector2 || prop.propertyType == SerializedPropertyType.Vector2Int))
                {
                    propWrapper.Add(CreateMinMaxSliderElement(prop, minMaxAttr));
                }
                else if (telemetryAttr != null)
                {
                    propWrapper.Add(new PropertyField(prop));
                    propWrapper.Add(CreateTelemetryGraphElement(prop, telemetryAttr));
                }
                else
                {
                    var standardField = new PropertyField(prop);
                    propWrapper.Add(standardField);
                }

                // 5. ReadOnly Attribute
                if (readOnlyAttr != null)
                {
                    propWrapper.SetEnabled(false);
                }

                // 6. ShowIf / HideIf Conditions
                if (showIfAttr != null)
                {
                    SetupConditionalVisibility(propWrapper, showIfAttr.ConditionMemberName, showIfAttr.CompareValue, isShowIf: true);
                }
                else if (hideIfAttr != null)
                {
                    SetupConditionalVisibility(propWrapper, hideIfAttr.ConditionMemberName, hideIfAttr.CompareValue, isShowIf: false);
                }

                // 7. Placement into Group or Default container
                if (tabAttr != null)
                {
                    VisualElement tabContent = GetOrCreateTabPane(tabGroups, tabBars, tabActiveState, tabAttr.TabGroupName, tabAttr.TabName);
                    tabContent.Add(propWrapper);
                }
                else if (boxAttr != null)
                {
                    VisualElement boxContent = GetOrCreateBoxGroup(boxGroups, boxAttr.GroupName, boxAttr.Collapsible, boxAttr.DefaultExpanded);
                    boxContent.Add(propWrapper);
                }
                else
                {
                    defaultContainer.Add(propWrapper);
                }
            }

            // Assemble main layout
            rootVisualElement.Add(defaultContainer);

            // Add Tab Groups
            foreach (var kvp in tabBars)
            {
                string groupName = kvp.Key;
                var tabContainer = new VisualElement
                {
                    name = $"TabGroupContainer_{groupName}"
                };
                tabContainer.style.marginTop = 6;
                tabContainer.style.marginBottom = 6;
                tabContainer.style.borderTopWidth = 1;
                tabContainer.style.borderTopColor = new Color(0.22f, 0.22f, 0.22f);
                tabContainer.style.paddingTop = 4;

                tabContainer.Add(kvp.Value); // Tab Bar Buttons

                if (tabGroups.TryGetValue(groupName, out var panes))
                {
                    foreach (var paneKvp in panes)
                    {
                        tabContainer.Add(paneKvp.Value); // Tab Panes
                    }
                }
                rootVisualElement.Add(tabContainer);
            }

            // Add Box Groups
            foreach (var kvp in boxGroups)
            {
                rootVisualElement.Add(kvp.Value);
            }

            // 8. Discover and Render [Button] Methods
            MethodInfo[] methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var buttonsContainer = new VisualElement { name = "InspectorButtonsContainer" };
            buttonsContainer.style.marginTop = 8;
            buttonsContainer.style.borderTopWidth = 1;
            buttonsContainer.style.borderTopColor = new Color(0.24f, 0.24f, 0.24f);
            buttonsContainer.style.paddingTop = 6;

            int buttonCount = 0;
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                ButtonAttribute btnAttr = m.GetCustomAttribute<ButtonAttribute>();
                if (btnAttr == null) continue;

                VisualElement buttonElem = CreateMethodButton(m, btnAttr);
                buttonsContainer.Add(buttonElem);
                buttonCount++;
            }

            if (buttonCount > 0)
            {
                rootVisualElement.Add(buttonsContainer);
            }

            rootVisualElement.Bind(serializedObject);
            return rootVisualElement;
        }

        // =========================================================================
        // GROUP BUILDERS (BOX GROUP & TAB GROUP)
        // =========================================================================

        private VisualElement GetOrCreateBoxGroup(Dictionary<string, VisualElement> boxGroups, string groupName, bool collapsible, bool defaultExpanded)
        {
            if (boxGroups.TryGetValue(groupName, out VisualElement existing))
            {
                return existing.Q<VisualElement>("BoxGroupContent");
            }

            var boxRoot = new VisualElement
            {
                name = $"BoxGroup_{groupName}"
            };
            boxRoot.style.marginTop = 6;
            boxRoot.style.marginBottom = 6;
            boxRoot.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 0.85f);
            boxRoot.style.borderTopWidth = 1;
            boxRoot.style.borderBottomWidth = 1;
            boxRoot.style.borderLeftWidth = 1;
            boxRoot.style.borderRightWidth = 1;
            boxRoot.style.borderTopColor = new Color(0.28f, 0.28f, 0.28f);
            boxRoot.style.borderBottomColor = new Color(0.28f, 0.28f, 0.28f);
            boxRoot.style.borderLeftColor = new Color(0.28f, 0.28f, 0.28f);
            boxRoot.style.borderRightColor = new Color(0.28f, 0.28f, 0.28f);
            boxRoot.style.borderTopLeftRadius = 4;
            boxRoot.style.borderTopRightRadius = 4;
            boxRoot.style.borderBottomLeftRadius = 4;
            boxRoot.style.borderBottomRightRadius = 4;
            boxRoot.style.overflow = Overflow.Hidden;

            var content = new VisualElement { name = "BoxGroupContent" };
            content.style.paddingTop = 6;
            content.style.paddingBottom = 6;
            content.style.paddingLeft = 8;
            content.style.paddingRight = 8;

            if (collapsible)
            {
                var foldout = new Foldout
                {
                    text = $"  {groupName}",
                    value = defaultExpanded
                };
                foldout.style.backgroundColor = new Color(0.24f, 0.24f, 0.24f);
                foldout.style.unityFontStyleAndWeight = FontStyle.Bold;
                foldout.style.paddingTop = 4;
                foldout.style.paddingBottom = 4;
                foldout.style.paddingLeft = 6;
                foldout.contentContainer.Add(content);
                boxRoot.Add(foldout);
            }
            else
            {
                var headerLabel = new Label(groupName)
                {
                    style =
                    {
                        backgroundColor = new Color(0.24f, 0.24f, 0.24f),
                        unityFontStyleAndWeight = FontStyle.Bold,
                        fontSize = 12,
                        paddingTop = 5,
                        paddingBottom = 5,
                        paddingLeft = 8,
                        color = new Color(0.92f, 0.92f, 0.92f)
                    }
                };
                boxRoot.Add(headerLabel);
                boxRoot.Add(content);
            }

            boxGroups[groupName] = boxRoot;
            return content;
        }

        private VisualElement GetOrCreateTabPane(
            Dictionary<string, Dictionary<string, VisualElement>> tabGroups,
            Dictionary<string, VisualElement> tabBars,
            Dictionary<string, string> tabActiveState,
            string tabGroupName,
            string tabName)
        {
            if (!tabGroups.TryGetValue(tabGroupName, out var groupPanes))
            {
                groupPanes = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
                tabGroups[tabGroupName] = groupPanes;

                var tabBar = new VisualElement
                {
                    name = $"TabBar_{tabGroupName}"
                };
                tabBar.style.flexDirection = FlexDirection.Row;
                tabBar.style.flexWrap = Wrap.Wrap;
                tabBar.style.marginBottom = 4;
                tabBars[tabGroupName] = tabBar;

                tabActiveState[tabGroupName] = tabName;
            }

            if (!groupPanes.TryGetValue(tabName, out VisualElement pane))
            {
                pane = new VisualElement
                {
                    name = $"TabPane_{tabName}"
                };
                pane.style.paddingTop = 6;
                pane.style.paddingBottom = 6;
                pane.style.paddingLeft = 4;
                pane.style.paddingRight = 4;

                bool isFirst = groupPanes.Count == 0;
                pane.style.display = isFirst ? DisplayStyle.Flex : DisplayStyle.None;
                groupPanes[tabName] = pane;

                // Add button to tab bar
                VisualElement tabBar = tabBars[tabGroupName];
                var tabBtn = new Button(() =>
                {
                    tabActiveState[tabGroupName] = tabName;
                    foreach (var pKvp in groupPanes)
                    {
                        pKvp.Value.style.display = (pKvp.Key == tabName) ? DisplayStyle.Flex : DisplayStyle.None;
                    }
                    // Highlight active tab button
                    for (int b = 0; b < tabBar.childCount; b++)
                    {
                        if (tabBar[b] is Button bBtn)
                        {
                            bool active = bBtn.text == tabName;
                            bBtn.style.backgroundColor = active ? new Color(0.23f, 0.49f, 0.92f) : new Color(0.25f, 0.25f, 0.25f);
                            bBtn.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
                        }
                    }
                })
                {
                    text = tabName
                };
                tabBtn.style.flexGrow = 1;
                tabBtn.style.paddingTop = 5;
                tabBtn.style.paddingBottom = 5;
                tabBtn.style.marginLeft = 1;
                tabBtn.style.marginRight = 1;
                tabBtn.style.borderTopLeftRadius = 3;
                tabBtn.style.borderTopRightRadius = 3;
                tabBtn.style.borderBottomLeftRadius = 0;
                tabBtn.style.borderBottomRightRadius = 0;
                tabBtn.style.backgroundColor = isFirst ? new Color(0.23f, 0.49f, 0.92f) : new Color(0.25f, 0.25f, 0.25f);
                tabBtn.style.unityFontStyleAndWeight = isFirst ? FontStyle.Bold : FontStyle.Normal;

                tabBar.Add(tabBtn);
            }

            return pane;
        }

        // =========================================================================
        // CONDITIONAL VISIBILITY (SHOWIF / HIDEIF)
        // =========================================================================

        private void SetupConditionalVisibility(VisualElement element, string memberName, object compareValue, bool isShowIf)
        {
            if (string.IsNullOrEmpty(memberName)) return;

            // Try to find as a serialized property first for TrackPropertyValue
            SerializedProperty condProp = serializedObject.FindProperty(memberName);

            void UpdateVisibility()
            {
                bool condResult = EvaluateCondition(memberName, compareValue, condProp);
                bool shouldShow = isShowIf ? condResult : !condResult;
                element.style.display = shouldShow ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (condProp != null)
            {
                element.TrackPropertyValue(condProp, _ => UpdateVisibility());
            }
            else
            {
                // Fallback polling for non-serialized properties/methods
                element.schedule.Execute(UpdateVisibility).Every(100);
            }

            UpdateVisibility();
        }

        private bool EvaluateCondition(string memberName, object compareValue, SerializedProperty condProp)
        {
            if (condProp != null)
            {
                switch (condProp.propertyType)
                {
                    case SerializedPropertyType.Boolean:
                        bool bVal = condProp.boolValue;
                        return compareValue != null ? Equals(bVal, compareValue) : bVal;

                    case SerializedPropertyType.Enum:
                        if (compareValue != null)
                        {
                            string enumStr = condProp.enumNames[condProp.enumValueIndex];
                            return string.Equals(enumStr, compareValue.ToString(), StringComparison.OrdinalIgnoreCase)
                                   || Equals(condProp.intValue, compareValue);
                        }
                        return condProp.enumValueIndex != 0;

                    case SerializedPropertyType.ObjectReference:
                        return compareValue != null ? condProp.objectReferenceValue == (UnityEngine.Object)compareValue : condProp.objectReferenceValue != null;

                    case SerializedPropertyType.Integer:
                        return compareValue != null ? Equals(condProp.intValue, Convert.ToInt32(compareValue)) : condProp.intValue != 0;

                    case SerializedPropertyType.Float:
                        return compareValue != null ? Mathf.Approximately(condProp.floatValue, Convert.ToSingle(compareValue)) : condProp.floatValue != 0f;

                    case SerializedPropertyType.String:
                        return compareValue != null ? string.Equals(condProp.stringValue, compareValue.ToString()) : !string.IsNullOrEmpty(condProp.stringValue);
                }
            }

            // Fallback via reflection on target
            Type t = target.GetType();
            FieldInfo f = t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
            {
                object val = f.GetValue(target);
                if (val is bool b) return compareValue != null ? Equals(b, compareValue) : b;
                return compareValue != null ? Equals(val, compareValue) : val != null;
            }

            PropertyInfo p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                object val = p.GetValue(target);
                if (val is bool b) return compareValue != null ? Equals(b, compareValue) : b;
                return compareValue != null ? Equals(val, compareValue) : val != null;
            }

            MethodInfo m = t.GetMethod(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m != null && m.ReturnType == typeof(bool))
            {
                return (bool)m.Invoke(target, null);
            }

            return true;
        }

        // =========================================================================
        // ATTRIBUTE ELEMENT GENERATORS
        // =========================================================================

        private VisualElement CreateTitleElement(TitleAttribute attr)
        {
            var container = new VisualElement();
            container.style.marginTop = 6;
            container.style.marginBottom = 3;

            var titleLabel = new Label(attr.Title);
            titleLabel.style.fontSize = 13;
            if (attr.Bold) titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new Color(0.95f, 0.95f, 0.95f);
            container.Add(titleLabel);

            if (!string.IsNullOrEmpty(attr.Subtitle))
            {
                var subLabel = new Label(attr.Subtitle);
                subLabel.style.fontSize = 10;
                subLabel.style.color = new Color(0.65f, 0.65f, 0.65f);
                container.Add(subLabel);
            }

            var divider = new VisualElement();
            divider.style.height = 1;
            divider.style.backgroundColor = new Color(0.35f, 0.35f, 0.35f);
            divider.style.marginTop = 2;
            divider.style.marginBottom = 4;
            container.Add(divider);

            return container;
        }

        private VisualElement CreateInfoBoxElement(InfoBoxAttribute attr)
        {
            var box = new VisualElement();
            box.style.paddingTop = 5;
            box.style.paddingBottom = 5;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            box.style.marginTop = 3;
            box.style.marginBottom = 4;
            box.style.borderLeftWidth = 4;
            box.style.borderTopRightRadius = 3;
            box.style.borderBottomRightRadius = 3;

            Color bgColor, borderColor, textColor;
            switch (attr.Type)
            {
                case InfoBoxType.Warning:
                    bgColor = new Color(0.40f, 0.30f, 0.05f, 0.45f);
                    borderColor = new Color(0.95f, 0.70f, 0.15f);
                    textColor = new Color(1.0f, 0.88f, 0.60f);
                    break;
                case InfoBoxType.Error:
                    bgColor = new Color(0.45f, 0.10f, 0.10f, 0.50f);
                    borderColor = new Color(0.95f, 0.25f, 0.25f);
                    textColor = new Color(1.0f, 0.70f, 0.70f);
                    break;
                case InfoBoxType.Success:
                    bgColor = new Color(0.10f, 0.35f, 0.15f, 0.45f);
                    borderColor = new Color(0.20f, 0.85f, 0.35f);
                    textColor = new Color(0.70f, 1.0f, 0.75f);
                    break;
                default:
                    bgColor = new Color(0.12f, 0.25f, 0.42f, 0.45f);
                    borderColor = new Color(0.25f, 0.60f, 0.95f);
                    textColor = new Color(0.75f, 0.90f, 1.0f);
                    break;
            }

            box.style.backgroundColor = bgColor;
            box.style.borderLeftColor = borderColor;

            var label = new Label(attr.Message);
            label.style.color = textColor;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 11;
            box.Add(label);

            return box;
        }

        private VisualElement CreateRequiredFieldBanner(SerializedProperty prop, RequiredFieldAttribute attr)
        {
            var banner = new VisualElement();
            banner.style.paddingTop = 4;
            banner.style.paddingBottom = 4;
            banner.style.paddingLeft = 6;
            banner.style.paddingRight = 6;
            banner.style.marginBottom = 2;
            banner.style.backgroundColor = new Color(0.55f, 0.12f, 0.12f, 0.65f);
            banner.style.borderLeftWidth = 3;
            banner.style.borderLeftColor = Color.red;
            banner.style.borderTopRightRadius = 3;
            banner.style.borderBottomRightRadius = 3;

            string msg = !string.IsNullOrEmpty(attr.ErrorMessage)
                ? attr.ErrorMessage
                : $"[Required] '{prop.displayName}' cannot be null!";

            var label = new Label(msg);
            label.style.color = new Color(1f, 0.8f, 0.8f);
            label.style.fontSize = 11;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            banner.Add(label);

            void UpdateBanner()
            {
                bool isNull = prop.objectReferenceValue == null;
                banner.style.display = isNull ? DisplayStyle.Flex : DisplayStyle.None;
            }

            banner.TrackPropertyValue(prop, _ => UpdateBanner());
            UpdateBanner();

            return banner;
        }

        private VisualElement CreateProgressBarElement(SerializedProperty prop, ProgressBarAttribute attr)
        {
            var container = new VisualElement();
            container.style.marginTop = 2;
            container.style.marginBottom = 3;

            var track = new VisualElement();
            track.style.height = 20;
            track.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
            track.style.borderTopWidth = 1;
            track.style.borderBottomWidth = 1;
            track.style.borderLeftWidth = 1;
            track.style.borderRightWidth = 1;
            track.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f);
            track.style.borderBottomColor = new Color(0.25f, 0.25f, 0.25f);
            track.style.borderLeftColor = new Color(0.25f, 0.25f, 0.25f);
            track.style.borderRightColor = new Color(0.25f, 0.25f, 0.25f);
            track.style.borderTopLeftRadius = 4;
            track.style.borderTopRightRadius = 4;
            track.style.borderBottomLeftRadius = 4;
            track.style.borderBottomRightRadius = 4;
            track.style.overflow = Overflow.Hidden;

            Color barColor = ColorUtility.TryParseHtmlString(attr.HexColor, out Color parsed) ? parsed : new Color(0.23f, 0.52f, 1.0f);

            var fill = new VisualElement();
            fill.style.height = Length.Percent(100);
            fill.style.backgroundColor = barColor;
            fill.style.width = Length.Percent(0);
            track.Add(fill);

            var textLabel = new Label();
            textLabel.style.position = Position.Absolute;
            textLabel.style.left = 0;
            textLabel.style.right = 0;
            textLabel.style.top = 0;
            textLabel.style.bottom = 0;
            textLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            textLabel.style.fontSize = 11;
            textLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            textLabel.style.color = Color.white;
            track.Add(textLabel);

            container.Add(track);

            void UpdateBar()
            {
                float val = (prop.propertyType == SerializedPropertyType.Integer) ? prop.intValue : prop.floatValue;
                float pct = Mathf.Clamp01((val - attr.Min) / Mathf.Max(0.0001f, attr.Max - attr.Min));
                fill.style.width = Length.Percent(pct * 100f);

                string prefix = !string.IsNullOrEmpty(attr.Label) ? attr.Label : prop.displayName;
                textLabel.text = $"{prefix}: {val:F1} / {attr.Max:F1} ({pct * 100f:F0}%)";
            }

            container.TrackPropertyValue(prop, _ => UpdateBar());
            UpdateBar();

            return container;
        }

        private VisualElement CreateMinMaxSliderElement(SerializedProperty prop, MinMaxSliderAttribute attr)
        {
            var container = new VisualElement();
            container.style.marginTop = 2;
            container.style.marginBottom = 3;

            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween } };
            header.Add(new Label(prop.displayName) { style = { unityFontStyleAndWeight = FontStyle.Normal, fontSize = 11 } });
            var valLabel = new Label();
            valLabel.style.fontSize = 10;
            valLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            header.Add(valLabel);
            container.Add(header);

            Vector2 cur = (prop.propertyType == SerializedPropertyType.Vector2)
                ? prop.vector2Value
                : new Vector2(prop.vector2IntValue.x, prop.vector2IntValue.y);

            var slider = new MinMaxSlider(cur.x, cur.y, attr.Min, attr.Max);
            slider.style.flexGrow = 1;

            slider.RegisterValueChangedCallback(evt =>
            {
                if (prop.propertyType == SerializedPropertyType.Vector2)
                {
                    prop.vector2Value = new Vector2(evt.newValue.x, evt.newValue.y);
                }
                else
                {
                    prop.vector2IntValue = new Vector2Int(Mathf.RoundToInt(evt.newValue.x), Mathf.RoundToInt(evt.newValue.y));
                }
                prop.serializedObject.ApplyModifiedProperties();
                valLabel.text = $"[{evt.newValue.x:F1} - {evt.newValue.y:F1}]";
            });

            valLabel.text = $"[{cur.x:F1} - {cur.y:F1}]";
            container.Add(slider);
            return container;
        }

        private VisualElement CreateTelemetryGraphElement(SerializedProperty prop, TelemetryGraphAttribute attr)
        {
            var graphContainer = new VisualElement();
            graphContainer.style.height = attr.Height;
            graphContainer.style.marginTop = 3;
            graphContainer.style.marginBottom = 4;
            graphContainer.style.backgroundColor = new Color(0.10f, 0.10f, 0.10f);
            graphContainer.style.borderTopWidth = 1;
            graphContainer.style.borderBottomWidth = 1;
            graphContainer.style.borderLeftWidth = 1;
            graphContainer.style.borderRightWidth = 1;
            graphContainer.style.borderTopColor = new Color(0.22f, 0.22f, 0.22f);
            graphContainer.style.borderBottomColor = new Color(0.22f, 0.22f, 0.22f);
            graphContainer.style.borderLeftColor = new Color(0.22f, 0.22f, 0.22f);
            graphContainer.style.borderRightColor = new Color(0.22f, 0.22f, 0.22f);
            graphContainer.style.borderTopLeftRadius = 4;
            graphContainer.style.borderTopRightRadius = 4;
            graphContainer.style.borderBottomLeftRadius = 4;
            graphContainer.style.borderBottomRightRadius = 4;

            Color lineColor = ColorUtility.TryParseHtmlString(attr.HexColor, out Color parsed) ? parsed : Color.cyan;

            // Generate graph vector graphics
            graphContainer.generateVisualContent += mgc =>
            {
                var painter = mgc.painter2D;
                float width = mgc.visualElement.contentRect.width;
                float height = mgc.visualElement.contentRect.height;

                if (width <= 10f || height <= 10f) return;

                // Draw background grid lines
                painter.strokeColor = new Color(0.18f, 0.18f, 0.18f);
                painter.lineWidth = 1f;
                painter.BeginPath();
                painter.MoveTo(new Vector2(0, height * 0.5f));
                painter.LineTo(new Vector2(width, height * 0.5f));
                painter.Stroke();

                // Read property float array values
                if (!prop.isArray || prop.arraySize < 2) return;

                int count = Mathf.Min(prop.arraySize, attr.BufferSize);
                float minVal = float.MaxValue;
                float maxVal = float.MinValue;

                for (int i = 0; i < count; i++)
                {
                    SerializedProperty elem = prop.GetArrayElementAtIndex(i);
                    float val = (elem.propertyType == SerializedPropertyType.Float) ? elem.floatValue : elem.intValue;
                    if (val < minVal) minVal = val;
                    if (val > maxVal) maxVal = val;
                }

                float range = Mathf.Max(0.001f, maxVal - minVal);

                painter.strokeColor = lineColor;
                painter.lineWidth = 1.8f;
                painter.BeginPath();

                for (int i = 0; i < count; i++)
                {
                    SerializedProperty elem = prop.GetArrayElementAtIndex(i);
                    float val = (elem.propertyType == SerializedPropertyType.Float) ? elem.floatValue : elem.intValue;
                    float x = (float)i / (count - 1) * width;
                    float y = height - (((val - minVal) / range) * (height * 0.85f) + (height * 0.075f));

                    if (i == 0) painter.MoveTo(new Vector2(x, y));
                    else painter.LineTo(new Vector2(x, y));
                }

                painter.Stroke();
            };

            graphContainer.TrackPropertyValue(prop, _ => graphContainer.MarkDirtyRepaint());
            return graphContainer;
        }

        private VisualElement CreateMethodButton(MethodInfo method, ButtonAttribute attr)
        {
            var btnContainer = new VisualElement();
            btnContainer.style.marginTop = 3;
            btnContainer.style.marginBottom = 3;

            ParameterInfo[] parameters = method.GetParameters();
            string label = !string.IsNullOrEmpty(attr.Label) ? attr.Label : ObjectNames.NicifyVariableName(method.Name);

            // If method has parameters, provide inputs
            object[] paramValues = new object[parameters.Length];
            var paramInputs = new VisualElement();
            paramInputs.style.marginLeft = 4;
            paramInputs.style.marginRight = 4;

            for (int p = 0; p < parameters.Length; p++)
            {
                int pIdx = p;
                ParameterInfo param = parameters[p];
                Type pt = param.ParameterType;

                if (pt == typeof(int))
                {
                    paramValues[pIdx] = 0;
                    var intField = new IntegerField(param.Name) { value = 0 };
                    intField.RegisterValueChangedCallback(e => paramValues[pIdx] = e.newValue);
                    paramInputs.Add(intField);
                }
                else if (pt == typeof(float))
                {
                    paramValues[pIdx] = 0f;
                    var floatField = new FloatField(param.Name) { value = 0f };
                    floatField.RegisterValueChangedCallback(e => paramValues[pIdx] = e.newValue);
                    paramInputs.Add(floatField);
                }
                else if (pt == typeof(string))
                {
                    paramValues[pIdx] = string.Empty;
                    var strField = new TextField(param.Name) { value = string.Empty };
                    strField.RegisterValueChangedCallback(e => paramValues[pIdx] = e.newValue);
                    paramInputs.Add(strField);
                }
                else if (pt == typeof(bool))
                {
                    paramValues[pIdx] = false;
                    var toggle = new Toggle(param.Name) { value = false };
                    toggle.RegisterValueChangedCallback(e => paramValues[pIdx] = e.newValue);
                    paramInputs.Add(toggle);
                }
            }

            if (parameters.Length > 0)
            {
                var foldout = new Foldout { text = $"Parameters: {label}", value = false };
                foldout.contentContainer.Add(paramInputs);
                btnContainer.Add(foldout);
            }

            var button = new Button(() =>
            {
                Undo.RecordObjects(targets, $"Invoke {method.Name}");
                for (int t = 0; t < targets.Length; t++)
                {
                    try
                    {
                        method.Invoke(targets[t], paramValues);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[VisualInspectorStudio] Error executing method '{method.Name}' on {targets[t].name}: {ex.InnerException?.Message ?? ex.Message}");
                    }
                    EditorUtility.SetDirty(targets[t]);
                }
            })
            {
                text = label
            };

            button.style.height = attr.Height;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.backgroundColor = new Color(0.24f, 0.28f, 0.35f);
            button.style.borderTopLeftRadius = 4;
            button.style.borderTopRightRadius = 4;
            button.style.borderBottomLeftRadius = 4;
            button.style.borderBottomRightRadius = 4;

            // DisplayMode Filtering check
            void CheckMode()
            {
                bool isPlaying = Application.isPlaying;
                bool enabled = true;
                if (attr.DisplayMode == ButtonDisplayMode.PlayModeOnly && !isPlaying) enabled = false;
                if (attr.DisplayMode == ButtonDisplayMode.EditModeOnly && isPlaying) enabled = false;
                button.SetEnabled(enabled);
            }

            btnContainer.schedule.Execute(CheckMode).Every(250);
            CheckMode();

            btnContainer.Add(button);
            return btnContainer;
        }

        private static bool IsNumericProperty(SerializedProperty prop)
        {
            return prop.propertyType == SerializedPropertyType.Float || prop.propertyType == SerializedPropertyType.Integer;
        }
    }

    /// <summary>
    /// Custom fallback inspector for all MonoBehaviours using UI Toolkit and VisualInspectorStudio attributes.
    /// </summary>
    [CustomEditor(typeof(MonoBehaviour), true, isFallback = true)]
    [CanEditMultipleObjects]
    public class VisualInspectorMonoBehaviourEditor : VisualInspectorEditorBase
    {
    }

    /// <summary>
    /// Custom fallback inspector for all ScriptableObjects using UI Toolkit and VisualInspectorStudio attributes.
    /// </summary>
    [CustomEditor(typeof(ScriptableObject), true, isFallback = true)]
    [CanEditMultipleObjects]
    public class VisualInspectorScriptableObjectEditor : VisualInspectorEditorBase
    {
    }
}
