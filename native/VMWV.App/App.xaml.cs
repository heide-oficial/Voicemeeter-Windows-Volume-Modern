using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using VMWV.Infrastructure.Windows.Startup;
using VMWV.Core.Settings;
using VMWV_App.Localization;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VMWV_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private static InstanceActivationService? _instance;
    private static readonly TaskCompletionSource<MainWindow> WindowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        LocalizationService.Current.Initialize("en-us");
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var isBackgroundLaunch = IsBackgroundLaunch(args.Arguments) || IsBackgroundCommandLine();
            _instance = new InstanceActivationService();
            if (!_instance.IsPrimary)
            {
                if (!isBackgroundLaunch)
                {
                    await _instance.RedirectAsync(CancellationToken.None);
                }

                await _instance.DisposeAsync();
                Environment.Exit(0);
                return;
            }

            _instance.Failed += (_, error) => RecordStartupFailure(error);
            _instance.Start(ActivateExistingWindowAsync);
            var settingsStore = new JsonSettingsStore(AppSettingsPaths.DefaultSettingsPath);
            LocalizationService.Current.SetLanguage(settingsStore.LoadOrCreate().Language);
            var createdWindow = new MainWindow(isBackgroundLaunch);
            Window = createdWindow;
            WindowReady.TrySetResult(createdWindow);
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (isBackgroundLaunch && Window is MainWindow mainWindow)
            {
                await mainWindow.StartInTrayAsync();
                return;
            }

            Window.Activate();
        }
        catch (Exception ex)
        {
            RecordStartupFailure(ex);
            Environment.Exit(1);
        }
    }

    private static async Task ActivateExistingWindowAsync()
    {
        var window = await WindowReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try { window.RestoreAndActivate(); activated.TrySetResult(); }
            catch (Exception ex) { activated.TrySetException(ex); }
        })) throw new InvalidOperationException("The application is shutting down.");
        await activated.Task;
    }

    internal static ValueTask StopInstanceAsync() => _instance?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal static void RecordStartupFailure(Exception error)
    {
        try
        {
            Directory.CreateDirectory(AppSettingsPaths.DefaultLogsFolder);
            File.AppendAllText(Path.Combine(AppSettingsPaths.DefaultLogsFolder, "lifecycle.log"),
                $"{DateTimeOffset.Now:O} {error}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsBackgroundLaunch(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments)
        && arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsBackgroundArgument);

    private static bool IsBackgroundCommandLine() =>
        Environment.GetCommandLineArgs().Skip(1).Any(IsBackgroundArgument);

    private static bool IsBackgroundArgument(string argument) =>
        argument.Equals("--background", StringComparison.OrdinalIgnoreCase);

}
