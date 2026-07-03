namespace VMWV_App.Models;

public sealed record DiagnosticLogEntry(
    DateTimeOffset Time,
    string Category,
    string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");
}
