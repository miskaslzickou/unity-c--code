using System;
using UnityEngine;

namespace VisualInspectorStudio
{
    /// <summary>
    /// Display mode specifying when an inspector button should be active.
    /// </summary>
    public enum ButtonDisplayMode
    {
        Always,
        PlayModeOnly,
        EditModeOnly
    }

    /// <summary>
    /// Type of message box displayed by InfoBox.
    /// </summary>
    public enum InfoBoxType
    {
        Info,
        Warning,
        Error,
        Success
    }

    /// <summary>
    /// Renders a clickable button in the Inspector that invokes the target method.
    /// Supports Play/Edit mode filtering and optional custom label/height.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class ButtonAttribute : Attribute
    {
        public string Label { get; }
        public float Height { get; }
        public ButtonDisplayMode DisplayMode { get; }
        public string GroupName { get; set; }

        public ButtonAttribute(string label = null, float height = 26f, ButtonDisplayMode displayMode = ButtonDisplayMode.Always)
        {
            Label = label;
            Height = Mathf.Max(18f, height);
            DisplayMode = displayMode;
        }

        public ButtonAttribute(float height, ButtonDisplayMode displayMode = ButtonDisplayMode.Always)
            : this(null, height, displayMode) { }
    }

    /// <summary>
    /// Dynamically displays the field in the Inspector only when the condition is met.
    /// Condition can be a boolean field/property, enum value, or object reference.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class ShowIfAttribute : PropertyAttribute
    {
        public string ConditionMemberName { get; }
        public object CompareValue { get; }

        public ShowIfAttribute(string boolOrFieldName, object compareValue = null)
        {
            ConditionMemberName = boolOrFieldName;
            CompareValue = compareValue;
        }
    }

    /// <summary>
    /// Dynamically hides the field in the Inspector when the condition is met.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class HideIfAttribute : PropertyAttribute
    {
        public string ConditionMemberName { get; }
        public object CompareValue { get; }

        public HideIfAttribute(string boolOrFieldName, object compareValue = null)
        {
            ConditionMemberName = boolOrFieldName;
            CompareValue = compareValue;
        }
    }

    /// <summary>
    /// Renders the field as non-editable / read-only in the Inspector.
    /// Useful for telemetry, runtime state, and calculated properties.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class ReadOnlyInspectorAttribute : PropertyAttribute
    {
    }

    /// <summary>
    /// Renders a visual horizontal progress / telemetry bar for numeric values (e.g. Health, RPM, Fuel, Heat).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class ProgressBarAttribute : PropertyAttribute
    {
        public float Min { get; }
        public float Max { get; }
        public string Label { get; }
        public string HexColor { get; }

        public ProgressBarAttribute(float min, float max, string label = null, string hexColor = "#3A86FF")
        {
            Min = min;
            Max = max;
            Label = label;
            HexColor = string.IsNullOrEmpty(hexColor) ? "#3A86FF" : hexColor;
        }
    }

    /// <summary>
    /// Renders a dual-handle range slider for Vector2 or Vector2Int fields (e.g. Min/Max spawn rates).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class MinMaxSliderAttribute : PropertyAttribute
    {
        public float Min { get; }
        public float Max { get; }

        public MinMaxSliderAttribute(float min, float max)
        {
            Min = min;
            Max = max;
        }
    }

    /// <summary>
    /// Organizes fields into a clean collapsible box group with a styled header.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class BoxGroupAttribute : PropertyAttribute
    {
        public string GroupName { get; }
        public bool Collapsible { get; set; }
        public bool DefaultExpanded { get; set; }

        public BoxGroupAttribute(string groupName, bool collapsible = true, bool defaultExpanded = true)
        {
            GroupName = groupName;
            Collapsible = collapsible;
            DefaultExpanded = defaultExpanded;
        }
    }

    /// <summary>
    /// Organizes fields into horizontal UI Toolkit tabs within the Inspector.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class TabGroupAttribute : PropertyAttribute
    {
        public string TabName { get; }
        public string TabGroupName { get; }

        public TabGroupAttribute(string tabName, string tabGroupName = "DefaultTabs")
        {
            TabName = tabName;
            TabGroupName = tabGroupName;
        }
    }

    /// <summary>
    /// Highlights an error/warning banner in the Inspector if an Object reference is null.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class RequiredFieldAttribute : PropertyAttribute
    {
        public string ErrorMessage { get; }

        public RequiredFieldAttribute(string errorMessage = null)
        {
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// Renders a live visual waveform / graph for float arrays or numeric collections in the Inspector.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class TelemetryGraphAttribute : PropertyAttribute
    {
        public int BufferSize { get; }
        public float Height { get; }
        public string HexColor { get; }

        public TelemetryGraphAttribute(int bufferSize = 100, float height = 65f, string hexColor = "#00F5D4")
        {
            BufferSize = Mathf.Max(10, bufferSize);
            Height = Mathf.Max(30f, height);
            HexColor = string.IsNullOrEmpty(hexColor) ? "#00F5D4" : hexColor;
        }
    }

    /// <summary>
    /// Displays a notification message box above the decorated field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
    public class InfoBoxAttribute : PropertyAttribute
    {
        public string Message { get; }
        public InfoBoxType Type { get; }

        public InfoBoxAttribute(string message, InfoBoxType type = InfoBoxType.Info)
        {
            Message = message;
            Type = type;
        }
    }

    /// <summary>
    /// Renders a prominent section header title and optional subtitle above the decorated field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public class TitleAttribute : PropertyAttribute
    {
        public string Title { get; }
        public string Subtitle { get; }
        public bool Bold { get; }

        public TitleAttribute(string title, string subtitle = null, bool bold = true)
        {
            Title = title;
            Subtitle = subtitle;
            Bold = bold;
        }
    }
}
