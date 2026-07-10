using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VMWV_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string MainInstanceKey = "VMWV_App_MainInstance";

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
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var mainInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        if (!mainInstance.IsCurrent)
        {
            mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                .AsTask()
                .GetAwaiter()
                .GetResult();
            Environment.Exit(0);
            return;
        }

        mainInstance.Activated += OnAppInstanceActivated;
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
        if (IsBackgroundLaunch(args.Arguments) && Window is MainWindow mainWindow)
        {
            mainWindow.HideToTrayAfterStartup();
        }
    }

    private static void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Window is MainWindow mainWindow)
            {
                mainWindow.RestoreAndActivate();
            }
            else
            {
                Window.Activate();
            }
        });
    }

    private static bool IsBackgroundLaunch(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments)
        && arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
}
