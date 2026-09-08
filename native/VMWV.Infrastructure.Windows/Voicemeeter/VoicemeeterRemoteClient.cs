using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using System.Reflection.PortableExecutable;
using VMWV.Core.Services;

namespace VMWV.Infrastructure.Windows.Voicemeeter;

public sealed class VoicemeeterRemoteClient : IVoicemeeterClient
{
    private readonly IVoicemeeterRemoteLibrary _library;
    private readonly SemaphoreSlim _apiLock = new(1, 1);
    private VoicemeeterConnectionState _state = VoicemeeterConnectionState.Disconnected;
    private bool _isLoggedIn;
    private bool _disposed;

    public VoicemeeterRemoteClient()
        : this(new VoicemeeterRemoteLibrary())
    {
    }

    internal VoicemeeterRemoteClient(IVoicemeeterRemoteLibrary library)
    {
        _library = library;
    }

    public event EventHandler? ParametersChanged;

    public event EventHandler<VoicemeeterConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public VoicemeeterConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            var oldState = _state;
            _state = value;
            ConnectionStateChanged?.Invoke(this, new VoicemeeterConnectionStateChangedEventArgs(oldState, value, null));
        }
    }

    public string Edition { get; private set; } = "Unknown";

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            State = VoicemeeterConnectionState.Connecting;
            _library.Load();
            if (!_isLoggedIn)
            {
                var loginResult = _library.Login();
                if (loginResult < 0)
                    throw new InvalidOperationException($"Voicemeeter login failed with code {loginResult}.");
                _isLoggedIn = true;
            }
            await WaitForParametersReadyAsync(cancellationToken).ConfigureAwait(false);
            Edition = await ResolveEditionAsync(cancellationToken).ConfigureAwait(false);
            State = VoicemeeterConnectionState.Connected;
        }
        catch
        {
            State = VoicemeeterConnectionState.Error;
            Edition = "Unknown";
            throw;
        }
        finally
        {
            _apiLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            State = VoicemeeterConnectionState.Disconnected;
            Edition = "Unknown";
        }
        finally
        {
            _apiLock.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await WaitForParametersReadyAsync(cancellationToken).ConfigureAwait(false);
            var edition = GetEditionName(_library.GetVoicemeeterType());
            if (edition == "Unknown" || edition != Edition)
                throw new InvalidOperationException("Voicemeeter availability or edition changed.");
        }
        catch { MarkConnectionLost(); throw; }
        finally { _apiLock.Release(); }
    }

    public async Task<IReadOnlyList<VoicemeeterBindingTarget>> GetBindingTargetsAsync(CancellationToken cancellationToken)
    {
        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await WaitForParametersReadyAsync(cancellationToken).ConfigureAwait(false);

            var targetCount = Edition switch
            {
                "Voicemeeter Potato" => (Strips: 8, Buses: 8),
                "Voicemeeter Banana" => (Strips: 5, Buses: 5),
                "Voicemeeter" => (Strips: 3, Buses: 2),
                _ => (Strips: 8, Buses: 8)
            };

            var targets = new List<VoicemeeterBindingTarget>(targetCount.Strips + targetCount.Buses);
            for (var index = 0; index < targetCount.Strips; index++)
            {
                targets.Add(CreateTarget("Strip", index));
            }

            for (var index = 0; index < targetCount.Buses; index++)
            {
                targets.Add(CreateTarget("Bus", index));
            }

            return targets;
        }
        catch
        {
            MarkConnectionLost();
            throw;
        }
        finally
        {
            _apiLock.Release();
        }
    }

    public Task SetGainAsync(VoicemeeterBindingTarget target, double gain, CancellationToken cancellationToken)
    {
        return SetGainAsync([target], gain, cancellationToken);
    }

    public async Task SetGainAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, double gain, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(gain) || gain < -60 || gain > 12)
            throw new ArgumentOutOfRangeException(nameof(gain));
        if (targets.Count == 0)
        {
            return;
        }

        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();

            var gainText = gain.ToString("R", CultureInfo.InvariantCulture);
            var script = new StringBuilder(targets.Count * 24);
            foreach (var target in targets)
            {
                script.Append(CultureInfo.InvariantCulture, $"{target.Kind}[{target.Index}].Gain = {gainText};");
            }

            _library.SetParameters(script.ToString());
            ParametersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            MarkConnectionLost();
            throw;
        }
        finally
        {
            _apiLock.Release();
        }
    }

    public Task SetMuteAsync(VoicemeeterBindingTarget target, bool isMuted, CancellationToken cancellationToken)
    {
        return SetMuteAsync([target], isMuted, cancellationToken);
    }

    public async Task SetMuteAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, bool isMuted, CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return;
        }

        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();

            var muteText = isMuted ? "1" : "0";
            var script = new StringBuilder(targets.Count * 24);
            foreach (var target in targets)
            {
                script.Append(CultureInfo.InvariantCulture, $"{target.Kind}[{target.Index}].Mute = {muteText};");
            }

            _library.SetParameters(script.ToString());
            ParametersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            MarkConnectionLost();
            throw;
        }
        finally
        {
            _apiLock.Release();
        }
    }

    public async Task RestartAudioEngineAsync(CancellationToken cancellationToken)
    {
        await RunCommandAsync("Command.Restart = 1;", cancellationToken).ConfigureAwait(false);
    }

    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        await RunCommandAsync("Command.Show = 1;", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _apiLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_isLoggedIn) { _library.Logout(); _isLoggedIn = false; }
            State = VoicemeeterConnectionState.Disconnected;
            _library.Dispose();
        }
        finally { _apiLock.Release(); }
    }

    private VoicemeeterBindingTarget CreateTarget(string kind, int index)
    {
        var label = _library.GetParameterString($"{kind}[{index}].Label");
        var deviceName = _library.GetParameterString($"{kind}[{index}].device.name");
        var fallbackName = $"{kind} {index}";
        var displayName = string.IsNullOrWhiteSpace(label) ? fallbackName : label.Trim();

        return new VoicemeeterBindingTarget(
            $"{kind}_{index}",
            kind,
            index,
            displayName,
            string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim(),
            true);
    }

    private Task WaitForParametersReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _library.IsParametersDirty();
        if (result < 0)
            throw new InvalidOperationException($"Unable to read Voicemeeter parameters. Code: {result}.");
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isLoggedIn || State != VoicemeeterConnectionState.Connected)
        {
            throw new InvalidOperationException("Voicemeeter is not connected.");
        }
    }

    private async Task RunCommandAsync(string script, CancellationToken cancellationToken)
    {
        await _apiLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            cancellationToken.ThrowIfCancellationRequested();
            _library.SetParameters(script);
            ParametersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            MarkConnectionLost();
            throw;
        }
        finally
        {
            _apiLock.Release();
        }
    }

    private void MarkConnectionLost()
    {
        if (!_isLoggedIn)
        {
            return;
        }

        Edition = "Unknown";
        State = VoicemeeterConnectionState.Error;
    }

    private static string GetEditionName(int type) =>
        type switch
        {
            1 => "Voicemeeter",
            2 => "Voicemeeter Banana",
            3 => "Voicemeeter Potato",
            _ => "Unknown"
        };

    private async Task<string> ResolveEditionAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edition = GetEditionName(_library.GetVoicemeeterType());
            if (edition != "Unknown")
            {
                return edition;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Voicemeeter connected but the edition could not be detected.");
    }
}

internal interface IVoicemeeterRemoteLibrary : IDisposable
{
    void Load();
    int Login();
    int Logout();
    int IsParametersDirty();
    int GetVoicemeeterType();
    void SetParameterFloat(string parameterName, float value);
    string GetParameterString(string parameterName);
    void SetParameters(string script);
}

internal sealed class VoicemeeterRemoteLibrary : IVoicemeeterRemoteLibrary
{
    private nint _handle;
    private LoginDelegate? _login;
    private LogoutDelegate? _logout;
    private GetVoicemeeterTypeDelegate? _getVoicemeeterType;
    private IsParametersDirtyDelegate? _isParametersDirty;
    private SetParameterFloatDelegate? _setParameterFloat;
    private GetParameterStringWDelegate? _getParameterStringW;
    private SetParametersDelegate? _setParameters;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int LoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int LogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int GetVoicemeeterTypeDelegate(ref int voicemeeterType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsParametersDirtyDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SetParameterFloatDelegate(string parameterName, float value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetParameterStringWDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string parameterName,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SetParametersDelegate(string script);

    public void Load()
    {
        if (_handle != 0)
        {
            return;
        }

        var libraryPath = ResolveLibraryPath();
        _handle = NativeLibrary.Load(libraryPath);
        try
        {
            _login = LoadFunction<LoginDelegate>("VBVMR_Login");
            _logout = LoadFunction<LogoutDelegate>("VBVMR_Logout");
            _getVoicemeeterType = LoadFunction<GetVoicemeeterTypeDelegate>("VBVMR_GetVoicemeeterType");
            _isParametersDirty = LoadFunction<IsParametersDirtyDelegate>("VBVMR_IsParametersDirty");
            _setParameterFloat = LoadFunction<SetParameterFloatDelegate>("VBVMR_SetParameterFloat");
            _getParameterStringW = LoadFunction<GetParameterStringWDelegate>("VBVMR_GetParameterStringW");
            _setParameters = LoadFunction<SetParametersDelegate>("VBVMR_SetParameters");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Login() => (_login ?? throw NotLoaded())();

    public int Logout() => (_logout ?? throw NotLoaded())();

    public int IsParametersDirty() => (_isParametersDirty ?? throw NotLoaded())();

    public int GetVoicemeeterType()
    {
        var type = 0;
        var result = (_getVoicemeeterType ?? throw NotLoaded())(ref type);
        return result < 0 ? 0 : type;
    }

    public void SetParameterFloat(string parameterName, float value)
    {
        var result = (_setParameterFloat ?? throw NotLoaded())(parameterName, value);
        if (result < 0)
        {
            throw new InvalidOperationException($"Unable to set Voicemeeter parameter {parameterName}. Code: {result}.");
        }
    }

    public string GetParameterString(string parameterName)
    {
        var buffer = new StringBuilder(512);
        var result = (_getParameterStringW ?? throw NotLoaded())(parameterName, buffer);
        return result < 0 ? string.Empty : buffer.ToString().TrimEnd('\0').Trim();
    }

    public void SetParameters(string script)
    {
        var result = (_setParameters ?? throw NotLoaded())(script);
        if (result < 0)
        {
            throw new InvalidOperationException($"Unable to run Voicemeeter command. Code: {result}.");
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeLibrary.Free(_handle);
            _handle = 0;
        }
    }

    private T LoadFunction<T>(string name)
        where T : Delegate
    {
        var address = NativeLibrary.GetExport(_handle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string ResolveLibraryPath()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Voicemeeter integration currently requires the x64 application.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = RegisteredInstallDirectories().Select(directory => Path.Combine(directory, "VoicemeeterRemote64.dll")).Concat(new[]
        {
            Path.Combine(programFiles, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(programFilesX86, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(AppContext.BaseDirectory, "VoicemeeterRemote64.dll")
        });

        return candidates.FirstOrDefault(path => File.Exists(path) && IsX64Library(path))
            ?? throw new FileNotFoundException("VoicemeeterRemote64.dll was not found. Install Voicemeeter or place the DLL next to the app.");
    }

    internal static bool IsX64Library(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.PEHeaders.CoffHeader.Machine == Machine.Amd64;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> RegisteredInstallDirectories()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames().Where(name => name.StartsWith("VB:Voicemeeter", StringComparison.OrdinalIgnoreCase)))
            {
                using var product = uninstall.OpenSubKey(name);
                if (product?.GetValue("InstallLocation") is string directory && Directory.Exists(directory))
                    yield return directory;
                if (product?.GetValue("UninstallString") is string command)
                {
                    var executable = command.Trim();
                    if (executable.StartsWith('"'))
                    {
                        var end = executable.IndexOf('"', 1);
                        if (end > 1) executable = executable[1..end];
                    }
                    if (Path.IsPathFullyQualified(executable) && File.Exists(executable)
                        && Path.GetDirectoryName(executable) is { } path)
                        yield return path;
                }
            }
        }
    }

    private static InvalidOperationException NotLoaded() =>
        new("Voicemeeter remote library is not loaded.");
}
