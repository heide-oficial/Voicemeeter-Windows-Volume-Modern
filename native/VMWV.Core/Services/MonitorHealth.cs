namespace VMWV.Core.Services;

public enum MonitorStatus { Starting, Ready, Retrying, Paused }

public sealed record MonitorHealth(MonitorStatus Status, int FailedAttempts = 0, string? Error = null);
