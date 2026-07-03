using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using VMWV.Core.Services;

namespace VMWV.Infrastructure.Windows.Voicemeeter;

public sealed class VoicemeeterRemoteClient : IVoicemeeterClient
{
    private static readonly TimeSpan ProcessWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] VoicemeeterProcessNames =
    [
        "voicemeeter",
        "voicemeeterpro",
        "voicemeeter8"
    ];

    private readonly VoicemeeterRemoteLibrary _library;
    private VoicemeeterConnectionState _state = VoicemeeterConnectionState.Disconnected;
    private bool _isLoggedIn;

    public VoicemeeterRemoteClient()
        : this(new VoicemeeterRemoteLibrary())
    {
    }

    internal VoicemeeterRemoteClient(VoicemeeterRemoteLibrary library)
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
        State = VoicemeeterConnectionState.WaitingForProcess;
        await WaitForVoicemeeterProcessAsync(cancellationToken).ConfigureAwait(false);

        State = VoicemeeterConnectionState.Connecting;
        _library.Load();

        var loginResult = _library.Login();
        if (loginResult < 0)
        {
            State = VoicemeeterConnectionState.Error;
            throw new InvalidOperationException($"Voicemeeter login failed with code {loginResult}.");
        }

        _isLoggedIn = true;
        Edition = GetEditionName(_library.GetVoicemeeterType());
        State = VoicemeeterConnectionState.Connected;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_isLoggedIn)
        {
            _library.Logout();
            _isLoggedIn = false;
        }

        State = VoicemeeterConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VoicemeeterBindingTarget>> GetBindingTargetsAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();

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

        return Task.FromResult<IReadOnlyList<VoicemeeterBindingTarget>>(targets);
    }

    public Task SetGainAsync(VoicemeeterBindingTarget target, double gain, CancellationToken cancellationToken)
    {
        return SetGainAsync([target], gain, cancellationToken);
    }

    public Task SetGainAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, double gain, CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();

        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        var gainText = gain.ToString("0.0", CultureInfo.InvariantCulture);
        var script = new StringBuilder(targets.Count * 24);
        foreach (var target in targets)
        {
            script.Append(CultureInfo.InvariantCulture, $"{target.Kind}[{target.Index}].Gain = {gainText};");
        }

        _library.SetParameters(script.ToString());
        ParametersChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(VoicemeeterBindingTarget target, bool isMuted, CancellationToken cancellationToken)
    {
        return SetMuteAsync([target], isMuted, cancellationToken);
    }

    public Task SetMuteAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, bool isMuted, CancellationToken cancellationToken)
    {
        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();

        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        var muteText = isMuted ? "1" : "0";
        var script = new StringBuilder(targets.Count * 24);
        foreach (var target in targets)
        {
            script.Append(CultureInfo.InvariantCulture, $"{target.Kind}[{target.Index}].Mute = {muteText};");
        }

        _library.SetParameters(script.ToString());
        ParametersChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task RestartAudioEngineAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        _library.SetParameters("Command.Restart = 1;");
        return Task.CompletedTask;
    }

    public Task ShowAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        _library.SetParameters("Command.Show = 1;");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _library.Dispose();
    }

    private static async Task WaitForVoicemeeterProcessAsync(CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (!IsVoicemeeterProcessRunning())
        {
            if (Stopwatch.GetElapsedTime(start) >= ProcessWaitTimeout)
            {
                throw new TimeoutException("Voicemeeter process was not found. Start Voicemeeter and try again.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsVoicemeeterProcessRunning()
    {
        foreach (var processName in VoicemeeterProcessNames)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return IsVoicemeeterProcessRunningByPrefix();
    }

    private static bool IsVoicemeeterProcessRunningByPrefix()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName.StartsWith("voicemeeter", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return false;
    }

    private VoicemeeterBindingTarget CreateTarget(string kind, int index)
    {
        var label = _library.GetParameterString($"{kind}[{index}].Label");
        var deviceName = _library.GetParameterString($"{kind}[{index}].device.name");
        var fallbackName = $"{kind} {index}";
        var displayName = string.IsNullOrWhiteSpace(label) ? fallbackName : label;

        return new VoicemeeterBindingTarget(
            $"{kind}_{index}",
            kind,
            index,
            displayName,
            string.IsNullOrWhiteSpace(deviceName) ? null : deviceName,
            true);
    }

    private void EnsureConnected()
    {
        if (!_isLoggedIn)
        {
            throw new InvalidOperationException("Voicemeeter is not connected.");
        }
    }

    private static string GetEditionName(int type) =>
        type switch
        {
            1 => "Voicemeeter",
            2 => "Voicemeeter Banana",
            3 => "Voicemeeter Potato",
            _ => "Unknown"
        };
}

internal sealed class VoicemeeterRemoteLibrary : IDisposable
{
    private nint _handle;
    private LoginDelegate? _login;
    private LogoutDelegate? _logout;
    private GetVoicemeeterTypeDelegate? _getVoicemeeterType;
    private SetParameterFloatDelegate? _setParameterFloat;
    private GetParameterStringDelegate? _getParameterString;
    private SetParametersDelegate? _setParameters;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int LoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int LogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int GetVoicemeeterTypeDelegate(ref int voicemeeterType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SetParameterFloatDelegate(string parameterName, float value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetParameterStringDelegate(string parameterName, StringBuilder value);

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
        _login = LoadFunction<LoginDelegate>("VBVMR_Login");
        _logout = LoadFunction<LogoutDelegate>("VBVMR_Logout");
        _getVoicemeeterType = LoadFunction<GetVoicemeeterTypeDelegate>("VBVMR_GetVoicemeeterType");
        _setParameterFloat = LoadFunction<SetParameterFloatDelegate>("VBVMR_SetParameterFloat");
        _getParameterString = LoadFunction<GetParameterStringDelegate>("VBVMR_GetParameterStringW");
        _setParameters = LoadFunction<SetParametersDelegate>("VBVMR_SetParameters");
    }

    public int Login() => (_login ?? throw NotLoaded())();

    public int Logout() => (_logout ?? throw NotLoaded())();

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
        var result = (_getParameterString ?? throw NotLoaded())(parameterName, buffer);
        return result < 0 ? string.Empty : buffer.ToString();
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
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = new[]
        {
            Path.Combine(programFiles, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(programFilesX86, "VB", "Voicemeeter", "VoicemeeterRemote64.dll"),
            Path.Combine(AppContext.BaseDirectory, "VoicemeeterRemote64.dll")
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("VoicemeeterRemote64.dll was not found. Install Voicemeeter or place the DLL next to the app.");
    }

    private static InvalidOperationException NotLoaded() =>
        new("Voicemeeter remote library is not loaded.");
}
