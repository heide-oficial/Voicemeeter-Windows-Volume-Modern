using System.ComponentModel;

namespace VMWV_App.Localization;

public sealed class LocalizationSource : INotifyPropertyChanged
{
    public LocalizationSource()
    {
        Attach();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        OnLanguageChanged(this, EventArgs.Empty);
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
    }

    public string this[string key] => LocalizationService.Current.Get(key);

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
