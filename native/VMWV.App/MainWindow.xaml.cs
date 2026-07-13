using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using VMWV.Core.Settings;
using Windows.Graphics;

namespace VMWV_App;

public sealed partial class MainWindow : Window
{
    private const int GwlWndProc = -4;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const string AppName = "Voicemeeter Windows Volume Modern";
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint MfString = 0x00000000;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint TpmRightButton = 0x00000002;
    private const uint TpmReturnCmd = 0x00000100;
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x0401;
    private const uint TrayMenuShow = 1001;
    private const uint TrayMenuExit = 1002;
    private const uint WmCommand = 0x0111;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmRButtonUp = 0x0205;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private readonly JsonSettingsStore _settingsStore = new(AppSettingsPaths.DefaultSettingsPath);
    private readonly WindowProc _windowProc;
    private readonly nint _hwnd;
    private nint _iconHandle;
    private string _brandIconPath;
    private string _brandVariant;
    private nint _oldWindowProc;
    private uint _taskbarCreatedMessage;
    private bool _closeToTray;
    private bool _exitRequested;
    private bool _trayIconVisible;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIdNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        _windowProc = WndProc;
        _oldWindowProc = SetWindowLongPtrW(_hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_windowProc));
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        var settings = ReadWindowSettings();
        _brandVariant = settings.LogoVariant;
        _closeToTray = settings.CloseToTray;
        _brandIconPath = ResolveBrandIconPath(_brandVariant);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyBrandIcon(_brandVariant);
        AppWindow.Closing += OnAppWindowClosing;
        ResizeWindow();

        RootFrame.Navigate(typeof(MainPage));
    }

    private void ResizeWindow()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(760 * scale)));
    }

    public void ApplyBrandIcon(string variant)
    {
        var normalizedVariant = NormalizeLogoVariant(variant);
        if (_brandVariant == normalizedVariant && AppTitleBar.IconSource is not null)
        {
            return;
        }

        _brandVariant = normalizedVariant;
        _brandIconPath = ResolveBrandIconPath(normalizedVariant);
        AppWindow.SetIcon(_brandIconPath);
        AppTitleBar.IconSource = new ImageIconSource
        {
            ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(ResolveBrandPngUri(normalizedVariant)))
        };

        if (_trayIconVisible)
        {
            RemoveTrayIcon();
            AddTrayIcon();
        }
    }

    public void SetCloseToTray(bool value)
    {
        _closeToTray = value;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            RemoveTrayIcon();
            _ = DisposeSharedServicesAsync();
            RestoreWindowProc();
            return;
        }

        if (!_closeToTray)
        {
            RemoveTrayIcon();
            _ = DisposeSharedServicesAsync();
            RestoreWindowProc();
            return;
        }

        args.Cancel = true;
        AddTrayIcon();
        UnloadShellContent();
        ShowWindow(_hwnd, SwHide);
    }

    private void RestoreFromTray()
    {
        RestoreAndActivate();
    }

    public void RestoreAndActivate()
    {
        EnsureShellContent();
        Activate();
        ShowWindow(_hwnd, SwShow);
        ShowWindow(_hwnd, SwRestore);
        SetForegroundWindow(_hwnd);
    }

    public async Task StartInTrayAsync()
    {
        AddTrayIcon();
        ShowWindow(_hwnd, SwHide);
        await MainPage.InitializeSharedViewModelAsync();
    }

    private void UnloadShellContent()
    {
        if (RootFrame.Content is null)
        {
            return;
        }

        RootFrame.Content = null;
        RootFrame.BackStack.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private void EnsureShellContent()
    {
        if (RootFrame.Content is not null)
        {
            return;
        }

        RootFrame.Navigate(typeof(MainPage));
    }

    private async void ExitApplication()
    {
        _exitRequested = true;
        RemoveTrayIcon();
        await DisposeSharedServicesAsync();
        Close();
    }

    private static async Task DisposeSharedServicesAsync()
    {
        await MainPage.DisposeSharedViewModelAsync();
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == WmLButtonDoubleClick)
            {
                RestoreFromTray();
                return 0;
            }

            if (mouseMessage == WmRButtonUp)
            {
                ShowTrayMenu();
                return 0;
            }
        }

        if (msg == WmPowerBroadcast)
        {
            var powerEvent = wParam.ToInt32();
            if (powerEvent is PbtApmResumeAutomatic or PbtApmResumeSuspend)
            {
                _ = MainPage.NotifySystemResumeAsync();
                return 1;
            }
        }

        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage && _trayIconVisible)
        {
            _trayIconVisible = false;
            AddTrayIcon();
            return 0;
        }

        if (msg == WmCommand)
        {
            var command = unchecked((uint)wParam.ToInt64()) & 0xFFFF;
            if (command == TrayMenuShow)
            {
                RestoreFromTray();
                return 0;
            }

            if (command == TrayMenuExit)
            {
                ExitApplication();
                return 0;
            }
        }

        return CallWindowProcW(_oldWindowProc, hWnd, msg, wParam, lParam);
    }

    private void AddTrayIcon()
    {
        if (_trayIconVisible)
        {
            return;
        }

        _iconHandle = LoadImageW(0, _brandIconPath, ImageIcon, 0, 0, LrLoadFromFile);
        var data = CreateNotifyIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.hIcon = _iconHandle;
        data.szTip = AppName;
        _trayIconVisible = Shell_NotifyIconW(NimAdd, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (_trayIconVisible)
        {
            var data = CreateNotifyIconData();
            Shell_NotifyIconW(NimDelete, ref data);
            _trayIconVisible = false;
        }

        if (_iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }

    private NotifyIconData CreateNotifyIconData() =>
        new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uCallbackMessage = TrayCallbackMessage
        };

    private AppSettings ReadWindowSettings()
    {
        try
        {
            return _settingsStore.LoadOrCreate();
        }
        catch
        {
            return new AppSettings();
        }
    }

    private static string ResolveBrandIconPath(string variant) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Brand", variant switch
        {
            "Black" => "logo-black.ico",
            "White" => "logo-white.ico",
            _ => "logo.ico"
        });

    private static string ResolveBrandPngUri(string variant) =>
        variant switch
        {
            "Black" => "ms-appx:///Assets/Brand/logo-black.png",
            "White" => "ms-appx:///Assets/Brand/logo-white.png",
            _ => "ms-appx:///Assets/Brand/logo.png"
        };

    private static string NormalizeLogoVariant(string variant) =>
        variant switch
        {
            "Black" => "Black",
            "White" => "White",
            _ => "Color"
        };

    private void ShowTrayMenu()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var menu = CreatePopupMenu();
        AppendMenuW(menu, MfString, TrayMenuShow, "Show");
        AppendMenuW(menu, MfString, TrayMenuExit, "Exit");
        SetForegroundWindow(_hwnd);

        var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, 0);
        DestroyMenu(menu);

        if (command == TrayMenuShow)
        {
            RestoreFromTray();
        }
        else if (command == TrayMenuExit)
        {
            ExitApplication();
        }
    }

    private void RestoreWindowProc()
    {
        if (_oldWindowProc != 0)
        {
            SetWindowLongPtrW(_hwnd, GwlWndProc, _oldWindowProc);
            _oldWindowProc = 0;
        }
    }

    private delegate nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }
}
