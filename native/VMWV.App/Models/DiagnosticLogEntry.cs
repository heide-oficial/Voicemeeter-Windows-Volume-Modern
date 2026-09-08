using System.ComponentModel;
using VMWV.Infrastructure.Windows.Globalization;

namespace VMWV_App.Models;

public sealed record DiagnosticLogEntry(
    DateTimeOffset Time,
    string Category,
    string Message,
    string EventId = "activity") : INotifyPropertyChanged
{
    public string TimeText => WindowsRegionalFormats.FormatTime(Time);

    public string DateTimeText => WindowsRegionalFormats.FormatDateTime(Time, includeSeconds: true);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshTimeText()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DateTimeText)));
    }
}
