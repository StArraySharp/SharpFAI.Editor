namespace SharpFAI.ModApi;

/// <summary>Opt in on the Mod entry type. The host loads annotated settings before ActivateAsync.</summary>
public interface IModSettings
{
    object Settings => this;

    /// <summary>Called on the editor thread after the user applies settings successfully.</summary>
    void OnSettingsChanged() { }
}

/// <summary>Available during activation for Mods implementing IModSettings.</summary>
public interface IModSettingsStore
{
    string FilePath { get; }

    /// <summary>Atomically saves the current persistable values without invoking OnSettingsChanged.</summary>
    void Save();
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class ModSettingAttribute(string? label = null) : Attribute
{
    public string? Label { get; } = label;
}

public sealed class ModSettingLabelAttribute(string label) : ModSettingAttribute(label);
public sealed class ModSettingRangeAttribute(double min, double max) : ModSettingAttribute
{
    public double Minimum { get; } = min;
    public double Maximum { get; } = max;
}
public sealed class ModSettingHeaderAttribute(string title) : ModSettingAttribute
{
    public string Title { get; } = title;
}
public sealed class ModSettingTooltipAttribute(string text) : ModSettingAttribute
{
    public string Text { get; } = text;
}
public sealed class ModSettingOrderAttribute(int order) : ModSettingAttribute
{
    public int Order { get; } = order;
}
public sealed class ModSettingShowIfAttribute(string member) : ModSettingAttribute
{
    public string Member { get; } = member;
    public bool Invert { get; init; }
}
public sealed class ModSettingJsonAttribute : ModSettingAttribute
{
    public int Lines { get; init; } = 6;
}
public sealed class ModSettingReadOnlyAttribute : ModSettingAttribute;
public sealed class ModSettingNoSaveAttribute : ModSettingAttribute;
/// <summary>Persist this member without drawing a control for it.</summary>
public sealed class ModSettingHiddenAttribute : ModSettingAttribute;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class ModSettingIgnoreAttribute : Attribute;
