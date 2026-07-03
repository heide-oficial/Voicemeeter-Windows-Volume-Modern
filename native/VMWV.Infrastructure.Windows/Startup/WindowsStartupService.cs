using Microsoft.Win32;
using System.Runtime.Versioning;
using VMWV.Core.Services;

namespace VMWV.Infrastructure.Windows.Startup;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Voicemeeter Windows Volume Modern";
    private const string AppExeName = "VMWV.App.exe";

    public Task<StartupRegistrationState> GetStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(RunValueName) as string;
            return Task.FromResult(string.IsNullOrWhiteSpace(value)
                ? StartupRegistrationState.Disabled
                : StartupRegistrationState.Enabled);
        }
        catch
        {
            return Task.FromResult(StartupRegistrationState.Error);
        }
    }

    public Task SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current user startup registry key.");

        if (isEnabled)
        {
            key.SetValue(RunValueName, $"\"{ResolveAppExecutablePath()}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    private static string ResolveAppExecutablePath()
    {
        var portableLauncher = Environment.GetEnvironmentVariable("VMWV_PORTABLE_LAUNCHER");
        if (!string.IsNullOrWhiteSpace(portableLauncher) && File.Exists(portableLauncher))
        {
            return portableLauncher;
        }

        var appPath = Path.Combine(AppContext.BaseDirectory, AppExeName);
        if (File.Exists(appPath))
        {
            return appPath;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            return processPath;
        }

        throw new FileNotFoundException("Unable to locate the application executable for startup registration.", appPath);
    }
}
