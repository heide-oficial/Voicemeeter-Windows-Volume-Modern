using CommunityToolkit.Mvvm.ComponentModel;

namespace VMWV_App.Models;

public sealed partial class BindingTargetItem : ObservableObject
{
    private readonly Action<BindingTargetItem, bool>? _onChanged;

    public BindingTargetItem(
        string id,
        string name,
        string detail,
        string iconGlyph,
        string iconName,
        bool isAvailable,
        bool isEnabled,
        Action<BindingTargetItem, bool>? onChanged)
    {
        Id = id;
        Name = name;
        Detail = detail;
        IconGlyph = iconGlyph;
        IconName = iconName;
        IsAvailable = isAvailable;
        _onChanged = onChanged;
        IsEnabled = isEnabled;
    }

    public string Id { get; }

    public string Name { get; }

    public string Detail { get; }

    public string IconGlyph { get; }

    public string IconName { get; }

    public bool IsAvailable { get; }

    public string AutomationId => $"TglBinding{Id.Replace("_", string.Empty)}";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        _onChanged?.Invoke(this, value);
    }
}
