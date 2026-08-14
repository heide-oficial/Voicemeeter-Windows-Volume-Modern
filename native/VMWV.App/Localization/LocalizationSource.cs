using System.ComponentModel;

namespace VMWV_App.Localization;

public sealed class LocalizationSource : INotifyPropertyChanged
{
    public LocalizationSource()
    {
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => LocalizationService.Current.Get(key);

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
