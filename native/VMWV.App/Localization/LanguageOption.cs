using System.ComponentModel;

namespace VMWV_App.Localization;

public sealed class LanguageOption(string code, string displayName) : INotifyPropertyChanged
{
    private string _displayName = displayName;

    public string Code { get; } = code;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
