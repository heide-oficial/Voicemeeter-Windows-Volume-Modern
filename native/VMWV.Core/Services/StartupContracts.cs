namespace VMWV.Core.Services;

public interface IStartupService
{
    Task<StartupRegistrationState> GetStateAsync(CancellationToken cancellationToken);
    Task SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken);
}

public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    RequiresElevation,
    Unsupported,
    Error
}
