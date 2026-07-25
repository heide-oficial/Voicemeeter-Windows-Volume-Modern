#include "audio_monitor.h"
#include "messages.h"
#include "resource.h"
#include "settings.h"
#include "voicemeeter_client.h"

#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <string>
#include <string_view>

namespace
{
constexpr wchar_t kAppName[] = L"Voicemeeter Windows Volume Modern";
constexpr wchar_t kWindowClass[] = L"VMWV.NativeHost.HiddenWindow";
constexpr wchar_t kMutexName[] = L"Local\\VMWV.NativeHost.SingleInstance";
constexpr wchar_t kSettingsExeName[] = L"VMWV.App.exe";

constexpr UINT WM_VMWV_TRAY = WM_APP + 20;
constexpr UINT kTrayIconId = 1;
constexpr UINT kMenuOpenSettings = 1001;
constexpr UINT kMenuShowVoicemeeter = 1002;
constexpr UINT kMenuRestartAudioEngine = 1003;
constexpr UINT kMenuExit = 1004;

constexpr UINT_PTR kMaintenanceTimer = 1;
constexpr UINT_PTR kAudioRefreshTimer = 2;
constexpr UINT_PTR kDeviceRecoveryTimer = 3;
constexpr UINT_PTR kRememberVolumeTimer = 4;

constexpr UINT kMaintenanceIntervalMs = 2000;
constexpr UINT kAudioRefreshIntervalMs = 5000;
constexpr UINT kDeviceRecoveryDelayMs = 2000;
constexpr UINT kRememberVolumeDelayMs = 500;
constexpr std::chrono::seconds kConnectionRetryInterval{10};

struct CommandLineOptions
{
    bool background = false;
    bool exitExisting = false;
    bool selfTest = false;
};

CommandLineOptions ParseCommandLine()
{
    CommandLineOptions options;
    int argumentCount = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    if (arguments == nullptr)
    {
        return options;
    }

    for (int index = 1; index < argumentCount; ++index)
    {
        const std::wstring_view argument(arguments[index]);
        if (argument == L"--background")
        {
            options.background = true;
        }
        else if (argument == L"--exit")
        {
            options.exitExisting = true;
        }
        else if (argument == L"--self-test")
        {
            options.selfTest = true;
        }
    }

    LocalFree(arguments);
    return options;
}

std::wstring ExecutableDirectory()
{
    std::wstring path(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || static_cast<std::size_t>(length) >= path.size())
    {
        return {};
    }
    path.resize(length);
    const std::size_t separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : path.substr(0, separator);
}

double MapVolumeToGain(const int windowsVolume, const RuntimeSettings& settings)
{
    const int normalizedVolume = std::clamp(windowsVolume, 0, 100);
    const double effectiveGainMax = settings.limitDbGainToZero ? 0.0 : settings.gainMax;

    double gain = settings.gainMin;
    if (settings.linearVolumeScale)
    {
        gain = normalizedVolume * (effectiveGainMax - settings.gainMin) / 100.0 + settings.gainMin;
    }
    else if (normalizedVolume > 0)
    {
        gain = std::max(20.0 * std::log10(normalizedVolume / 100.0) + effectiveGainMax, settings.gainMin);
    }

    return std::round(gain * 10.0) / 10.0;
}

std::pair<int, int> TargetCountsForEdition(const int editionType)
{
    switch (editionType)
    {
    case 1: return {3, 2};
    case 2: return {5, 5};
    case 3: return {8, 8};
    default: return {8, 8};
    }
}

std::string BuildGainScript(const RuntimeSettings& settings, const int editionType, const double gain)
{
    const auto [stripCount, busCount] = TargetCountsForEdition(editionType);
    std::string script;
    script.reserve(16U * 32U);
    std::array<char, 64> command{};

    for (int index = 0; index < stripCount; ++index)
    {
        if (!settings.strips[static_cast<std::size_t>(index)])
        {
            continue;
        }
        const int written = sprintf_s(command.data(), command.size(), "Strip[%d].Gain = %.1f;", index, gain);
        if (written > 0)
        {
            script.append(command.data(), static_cast<std::size_t>(written));
        }
    }

    for (int index = 0; index < busCount; ++index)
    {
        if (!settings.buses[static_cast<std::size_t>(index)])
        {
            continue;
        }
        const int written = sprintf_s(command.data(), command.size(), "Bus[%d].Gain = %.1f;", index, gain);
        if (written > 0)
        {
            script.append(command.data(), static_cast<std::size_t>(written));
        }
    }

    return script;
}

std::string BuildMuteScript(const RuntimeSettings& settings, const int editionType, const bool muted)
{
    const auto [stripCount, busCount] = TargetCountsForEdition(editionType);
    std::string script;
    script.reserve(16U * 28U);
    std::array<char, 64> command{};
    const int muteValue = muted ? 1 : 0;

    for (int index = 0; index < stripCount; ++index)
    {
        if (!settings.strips[static_cast<std::size_t>(index)])
        {
            continue;
        }
        const int written = sprintf_s(command.data(), command.size(), "Strip[%d].Mute = %d;", index, muteValue);
        if (written > 0)
        {
            script.append(command.data(), static_cast<std::size_t>(written));
        }
    }

    for (int index = 0; index < busCount; ++index)
    {
        if (!settings.buses[static_cast<std::size_t>(index)])
        {
            continue;
        }
        const int written = sprintf_s(command.data(), command.size(), "Bus[%d].Mute = %d;", index, muteValue);
        if (written > 0)
        {
            script.append(command.data(), static_cast<std::size_t>(written));
        }
    }

    return script;
}

void DebugLog(const std::wstring_view message)
{
    std::wstring line(message);
    line += L"\r\n";
    OutputDebugStringW(line.c_str());
}

class NativeHost
{
public:
    explicit NativeHost(const HINSTANCE instance)
        : instance_(instance), settings_(settingsStore_.Load())
    {
    }

    ~NativeHost()
    {
        Shutdown();
    }

    NativeHost(const NativeHost&) = delete;
    NativeHost& operator=(const NativeHost&) = delete;

    bool Initialize(const bool openSettings)
    {
        WNDCLASSEXW windowClass{};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = WindowProcedure;
        windowClass.hInstance = instance_;
        windowClass.hIcon = LoadIconW(instance_, MAKEINTRESOURCEW(IDI_APP_ICON));
        windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        windowClass.lpszClassName = kWindowClass;
        if (RegisterClassExW(&windowClass) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        {
            return false;
        }

        window_ = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            kWindowClass,
            kAppName,
            WS_POPUP,
            0,
            0,
            0,
            0,
            nullptr,
            nullptr,
            instance_,
            this);
        if (window_ == nullptr)
        {
            return false;
        }

        taskbarCreatedMessage_ = RegisterWindowMessageW(L"TaskbarCreated");
        icon_ = LoadIconW(instance_, MAKEINTRESOURCEW(IDI_APP_ICON));
        AddTrayIcon();

        audioMonitor_ = new AudioMonitor(window_);
        const HRESULT audioResult = audioMonitor_->Start();
        if (FAILED(audioResult))
        {
            DebugLog(L"Core Audio endpoint is currently unavailable; the host will retry.");
        }
        else
        {
            haveAudio_ = audioMonitor_->HasEndpoint();
            currentVolume_ = audioMonitor_->CurrentVolume();
            currentMute_ = audioMonitor_->CurrentMute();
        }

        SetTimer(window_, kMaintenanceTimer, kMaintenanceIntervalMs, nullptr);
        SetTimer(window_, kAudioRefreshTimer, kAudioRefreshIntervalMs, nullptr);
        TryConnectVoicemeeter(true);
        UpdateTrayTooltip();

        if (openSettings)
        {
            LaunchSettings();
        }
        return true;
    }

    int Run()
    {
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        return static_cast<int>(message.wParam);
    }

private:
    HINSTANCE instance_ = nullptr;
    HWND window_ = nullptr;
    HICON icon_ = nullptr;
    HICON trayIconHandle_ = nullptr;
    bool ownsTrayIconHandle_ = false;
    NOTIFYICONDATAW trayIcon_{};
    UINT taskbarCreatedMessage_ = 0;
    bool trayVisible_ = false;
    bool shuttingDown_ = false;

    SettingsStore settingsStore_;
    RuntimeSettings settings_;
    AudioMonitor* audioMonitor_ = nullptr;
    VoicemeeterClient voicemeeter_;
    bool haveAudio_ = false;
    int currentVolume_ = 0;
    bool currentMute_ = false;
    int pendingRememberedVolume_ = 0;
    bool restartOnLaunchApplied_ = false;
    std::chrono::steady_clock::time_point nextConnectionAttempt_{};

    static LRESULT CALLBACK WindowProcedure(
        const HWND window,
        const UINT message,
        const WPARAM wParam,
        const LPARAM lParam)
    {
        NativeHost* host = reinterpret_cast<NativeHost*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            host = static_cast<NativeHost*>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
        }

        if (host != nullptr)
        {
            return host->HandleMessage(window, message, wParam, lParam);
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    LRESULT HandleMessage(const HWND messageWindow, const UINT message, const WPARAM wParam, const LPARAM lParam)
    {
        if (message == taskbarCreatedMessage_ && taskbarCreatedMessage_ != 0)
        {
            trayVisible_ = false;
            AddTrayIcon();
            return 0;
        }

        switch (message)
        {
        case WM_VMWV_TRAY:
            HandleTrayMessage(static_cast<UINT>(LOWORD(lParam)));
            return 0;

        case WM_VMWV_AUDIO_CHANGED:
            OnAudioChanged(static_cast<int>(wParam), lParam != 0);
            return 0;

        case WM_VMWV_DEFAULT_DEVICE_CHANGED:
            OnDefaultDeviceChanged();
            return 0;

        case WM_VMWV_ANY_DEVICE_CHANGED:
            if (settings_.restartOnAnyDeviceChange)
            {
                ScheduleDeviceRecovery();
            }
            return 0;

        case WM_VMWV_OPEN_SETTINGS:
            LaunchSettings();
            return 0;

        case WM_TIMER:
            HandleTimer(wParam);
            return 0;

        case WM_COMMAND:
            HandleCommand(LOWORD(wParam));
            return 0;

        case WM_POWERBROADCAST:
            if (wParam == PBT_APMRESUMEAUTOMATIC || wParam == PBT_APMRESUMESUSPEND)
            {
                OnSystemResume();
                return TRUE;
            }
            break;

        case WM_QUERYENDSESSION:
            return TRUE;

        case WM_ENDSESSION:
            if (wParam != FALSE)
            {
                DestroyWindow(window_);
            }
            return 0;

        case WM_CLOSE:
            DestroyWindow(window_);
            return 0;

        case WM_DESTROY:
            Shutdown();
            PostQuitMessage(0);
            return 0;

        default:
            break;
        }

        return DefWindowProcW(messageWindow, message, wParam, lParam);
    }

    void HandleTrayMessage(const UINT mouseMessage)
    {
        if (mouseMessage == WM_LBUTTONDBLCLK)
        {
            LaunchSettings();
        }
        else if (mouseMessage == WM_RBUTTONUP || mouseMessage == WM_CONTEXTMENU)
        {
            ShowTrayMenu();
        }
    }

    void HandleCommand(const UINT command)
    {
        switch (command)
        {
        case kMenuOpenSettings:
            LaunchSettings();
            break;
        case kMenuShowVoicemeeter:
            if (!voicemeeter_.Show())
            {
                MarkVoicemeeterDisconnected();
            }
            break;
        case kMenuRestartAudioEngine:
            if (!voicemeeter_.RestartAudioEngine())
            {
                MarkVoicemeeterDisconnected();
            }
            else
            {
                SyncCurrentAudio();
            }
            break;
        case kMenuExit:
            DestroyWindow(window_);
            break;
        default:
            break;
        }
    }

    void HandleTimer(const WPARAM timerId)
    {
        switch (timerId)
        {
        case kMaintenanceTimer:
    {
        const std::string previousLogoVariant = settings_.logoVariant;
        if (settingsStore_.ReloadIfChanged(settings_))
        {
            if (settings_.logoVariant != previousLogoVariant)
            {
                RefreshTrayIcon();
            }
            SyncCurrentAudio();
        }
        TryConnectVoicemeeter(false);
        break;
    }

        case kAudioRefreshTimer:
            RefreshAudioEndpoint();
            break;

        case kDeviceRecoveryTimer:
            KillTimer(window_, kDeviceRecoveryTimer);
            RecoverFromDeviceChange();
            break;

        case kRememberVolumeTimer:
            KillTimer(window_, kRememberVolumeTimer);
            (void)settingsStore_.SaveInitialVolume(pendingRememberedVolume_);
            break;

        default:
            break;
        }
    }

    void OnAudioChanged(const int volume, const bool muted)
    {
        const bool volumeChanged = !haveAudio_ || currentVolume_ != volume;
        const bool muteChanged = !haveAudio_ || currentMute_ != muted;
        haveAudio_ = true;
        currentVolume_ = std::clamp(volume, 0, 100);
        currentMute_ = muted;

        if (volumeChanged && settings_.rememberVolume)
        {
            pendingRememberedVolume_ = currentVolume_;
            SetTimer(window_, kRememberVolumeTimer, kRememberVolumeDelayMs, nullptr);
        }

        if (volumeChanged)
        {
            SyncVolume();
        }
        if (muteChanged && settings_.syncMute)
        {
            SyncMute();
        }
        UpdateTrayTooltip();
    }

    void OnDefaultDeviceChanged()
    {
        if (audioMonitor_ == nullptr)
        {
            return;
        }

        const HRESULT result = audioMonitor_->Reattach();
        if (SUCCEEDED(result))
        {
            haveAudio_ = audioMonitor_->HasEndpoint();
            currentVolume_ = audioMonitor_->CurrentVolume();
            currentMute_ = audioMonitor_->CurrentMute();
            SyncCurrentAudio();
        }
        else
        {
            haveAudio_ = false;
        }

        if (settings_.restartOnDeviceChange || settings_.restartOnAnyDeviceChange)
        {
            ScheduleDeviceRecovery();
        }
    }

    void OnSystemResume()
    {
        if (audioMonitor_ != nullptr)
        {
            (void)audioMonitor_->Reattach();
            haveAudio_ = audioMonitor_->HasEndpoint();
            currentVolume_ = audioMonitor_->CurrentVolume();
            currentMute_ = audioMonitor_->CurrentMute();
        }

        voicemeeter_.Disconnect();
        nextConnectionAttempt_ = std::chrono::steady_clock::time_point{};
        TryConnectVoicemeeter(true);
        if (settings_.restartOnResume && voicemeeter_.IsConnected())
        {
            (void)voicemeeter_.RestartAudioEngine();
        }
        SyncCurrentAudio();
    }

    void RefreshAudioEndpoint()
    {
        if (audioMonitor_ == nullptr)
        {
            return;
        }

        int volume = currentVolume_;
        bool muted = currentMute_;
        bool changed = false;
        HRESULT result = audioMonitor_->Refresh(volume, muted, changed);
        if (FAILED(result))
        {
            result = audioMonitor_->Reattach();
            if (FAILED(result))
            {
                haveAudio_ = false;
                UpdateTrayTooltip();
                return;
            }
            volume = audioMonitor_->CurrentVolume();
            muted = audioMonitor_->CurrentMute();
            changed = true;
        }

        if (changed || !haveAudio_)
        {
            OnAudioChanged(volume, muted);
        }
    }

    void ScheduleDeviceRecovery()
    {
        SetTimer(window_, kDeviceRecoveryTimer, kDeviceRecoveryDelayMs, nullptr);
    }

    void RecoverFromDeviceChange()
    {
        RefreshAudioEndpoint();
        if (voicemeeter_.IsConnected())
        {
            if (!voicemeeter_.RestartAudioEngine())
            {
                MarkVoicemeeterDisconnected();
                return;
            }
            SyncCurrentAudio();
        }
        else
        {
            nextConnectionAttempt_ = std::chrono::steady_clock::time_point{};
            TryConnectVoicemeeter(true);
        }
    }

    void TryConnectVoicemeeter(const bool immediate)
    {
        if (voicemeeter_.IsConnected())
        {
            return;
        }

        const auto now = std::chrono::steady_clock::now();
        if (!immediate && now < nextConnectionAttempt_)
        {
            return;
        }

        nextConnectionAttempt_ = now + kConnectionRetryInterval;
        if (!voicemeeter_.Connect())
        {
            UpdateTrayTooltip();
            return;
        }

        if (!restartOnLaunchApplied_ && settings_.restartOnAppLaunch)
        {
            restartOnLaunchApplied_ = true;
            (void)voicemeeter_.RestartAudioEngine();
        }

        SyncCurrentAudio();
        UpdateTrayTooltip();
    }

    void MarkVoicemeeterDisconnected()
    {
        voicemeeter_.Disconnect();
        nextConnectionAttempt_ = std::chrono::steady_clock::time_point{};
        UpdateTrayTooltip();
    }

    void SyncCurrentAudio()
    {
        if (!haveAudio_ || !voicemeeter_.IsConnected() || !settings_.HasSelectedTargets())
        {
            return;
        }
        SyncVolume();
        if (settings_.syncMute)
        {
            SyncMute();
        }
    }

    void SyncVolume()
    {
        if (!voicemeeter_.IsConnected() || !settings_.HasSelectedTargets())
        {
            return;
        }

        const double gain = MapVolumeToGain(currentVolume_, settings_);
        const std::string script = BuildGainScript(settings_, voicemeeter_.EditionType(), gain);
        if (!script.empty() && !voicemeeter_.SetParameters(script))
        {
            MarkVoicemeeterDisconnected();
        }
    }

    void SyncMute()
    {
        if (!voicemeeter_.IsConnected() || !settings_.HasSelectedTargets())
        {
            return;
        }

        const std::string script = BuildMuteScript(settings_, voicemeeter_.EditionType(), currentMute_);
        if (!script.empty() && !voicemeeter_.SetParameters(script))
        {
            MarkVoicemeeterDisconnected();
        }
    }

    HICON LoadTrayIcon()
    {
        const wchar_t* fileName = L"logo.ico";
        if (settings_.logoVariant == "Black")
        {
            fileName = L"logo-black.ico";
        }
        else if (settings_.logoVariant == "White")
        {
            fileName = L"logo-white.ico";
        }

        const std::filesystem::path iconPath =
            std::filesystem::path(ExecutableDirectory()) / L"Assets" / L"Brand" / fileName;
        const HICON loaded = reinterpret_cast<HICON>(LoadImageW(
            nullptr,
            iconPath.c_str(),
            IMAGE_ICON,
            0,
            0,
            LR_LOADFROMFILE | LR_DEFAULTSIZE));
        if (loaded != nullptr)
        {
            ownsTrayIconHandle_ = true;
            return loaded;
        }

        ownsTrayIconHandle_ = false;
        return icon_;
    }

    void ReleaseTrayIconHandle()
    {
        if (ownsTrayIconHandle_ && trayIconHandle_ != nullptr)
        {
            DestroyIcon(trayIconHandle_);
        }
        trayIconHandle_ = nullptr;
        ownsTrayIconHandle_ = false;
    }

    void RefreshTrayIcon()
    {
        if (!trayVisible_)
        {
            return;
        }
        RemoveTrayIcon();
        AddTrayIcon();
        UpdateTrayTooltip();
    }

    void AddTrayIcon()
    {
        if (trayVisible_ || window_ == nullptr)
        {
            return;
        }

        ReleaseTrayIconHandle();
        trayIconHandle_ = LoadTrayIcon();
        trayIcon_ = {};
        trayIcon_.cbSize = sizeof(trayIcon_);
        trayIcon_.hWnd = window_;
        trayIcon_.uID = kTrayIconId;
        trayIcon_.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        trayIcon_.uCallbackMessage = WM_VMWV_TRAY;
        trayIcon_.hIcon = trayIconHandle_;
        StringCchCopyW(trayIcon_.szTip, ARRAYSIZE(trayIcon_.szTip), kAppName);
        trayVisible_ = Shell_NotifyIconW(NIM_ADD, &trayIcon_) != FALSE;
        if (trayVisible_)
        {
            trayIcon_.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, &trayIcon_);
        }
        else
        {
            ReleaseTrayIconHandle();
        }
    }

    void RemoveTrayIcon()
    {
        if (trayVisible_)
        {
            Shell_NotifyIconW(NIM_DELETE, &trayIcon_);
            trayVisible_ = false;
        }
        ReleaseTrayIconHandle();
    }

    void UpdateTrayTooltip()
    {
        if (!trayVisible_)
        {
            return;
        }

        std::wstring tooltip = L"VMWV - ";
        if (!haveAudio_)
        {
            tooltip += L"waiting for Windows audio";
        }
        else if (!voicemeeter_.IsConnected())
        {
            tooltip += L"waiting for Voicemeeter";
        }
        else
        {
            tooltip += L"connected";
        }

        trayIcon_.uFlags = NIF_TIP;
        StringCchCopyW(trayIcon_.szTip, ARRAYSIZE(trayIcon_.szTip), tooltip.c_str());
        Shell_NotifyIconW(NIM_MODIFY, &trayIcon_);
    }

    void ShowTrayMenu()
    {
        POINT cursor{};
        if (!GetCursorPos(&cursor))
        {
            return;
        }

        const HMENU menu = CreatePopupMenu();
        if (menu == nullptr)
        {
            return;
        }

        AppendMenuW(menu, MF_STRING, kMenuOpenSettings, L"Open settings");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, kMenuShowVoicemeeter, L"Show Voicemeeter");
        AppendMenuW(menu, MF_STRING, kMenuRestartAudioEngine, L"Restart Voicemeeter audio engine");
        if (!voicemeeter_.IsConnected())
        {
            EnableMenuItem(menu, kMenuShowVoicemeeter, MF_BYCOMMAND | MF_GRAYED);
            EnableMenuItem(menu, kMenuRestartAudioEngine, MF_BYCOMMAND | MF_GRAYED);
        }
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, kMenuExit, L"Exit");

        SetForegroundWindow(window_);
        const UINT selected = TrackPopupMenu(
            menu,
            TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
            cursor.x,
            cursor.y,
            0,
            window_,
            nullptr);
        DestroyMenu(menu);

        if (selected != 0)
        {
            HandleCommand(selected);
        }
    }

    void LaunchSettings() const
    {
        const std::filesystem::path executable = std::filesystem::path(ExecutableDirectory()) / kSettingsExeName;
        if (GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES)
        {
            MessageBoxW(window_, L"VMWV.App.exe was not found next to the native host.", kAppName, MB_OK | MB_ICONERROR);
            return;
        }

        std::wstring commandLine = L"\"" + executable.wstring() + L"\" --settings-only";
        STARTUPINFOW startupInfo{};
        startupInfo.cb = sizeof(startupInfo);
        PROCESS_INFORMATION processInfo{};
        if (CreateProcessW(
                executable.c_str(),
                commandLine.data(),
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                ExecutableDirectory().c_str(),
                &startupInfo,
                &processInfo))
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
        else
        {
            MessageBoxW(window_, L"Unable to open the settings interface.", kAppName, MB_OK | MB_ICONERROR);
        }
    }

    void Shutdown()
    {
        if (shuttingDown_)
        {
            return;
        }
        shuttingDown_ = true;

        if (window_ != nullptr)
        {
            KillTimer(window_, kMaintenanceTimer);
            KillTimer(window_, kAudioRefreshTimer);
            KillTimer(window_, kDeviceRecoveryTimer);
            KillTimer(window_, kRememberVolumeTimer);
        }

        RemoveTrayIcon();
        voicemeeter_.Disconnect();
        if (audioMonitor_ != nullptr)
        {
            audioMonitor_->Stop();
            audioMonitor_->Release();
            audioMonitor_ = nullptr;
        }
        window_ = nullptr;
    }
};

int RunSelfTest()
{
    RuntimeSettings logarithmic;
    if (MapVolumeToGain(0, logarithmic) != -60.0)
    {
        return 1;
    }
    if (MapVolumeToGain(100, logarithmic) != 12.0)
    {
        return 2;
    }
    if (MapVolumeToGain(50, logarithmic) != 6.0)
    {
        return 3;
    }

    RuntimeSettings linear;
    linear.linearVolumeScale = true;
    if (MapVolumeToGain(50, linear) != -24.0)
    {
        return 4;
    }

    linear.limitDbGainToZero = true;
    if (MapVolumeToGain(100, linear) != 0.0)
    {
        return 5;
    }
    return 0;
}

void SignalExistingInstance(const bool exitExisting, const bool background)
{
    if (background && !exitExisting)
    {
        return;
    }

    for (int attempt = 0; attempt < 20; ++attempt)
    {
        const HWND existing = FindWindowW(kWindowClass, nullptr);
        if (existing != nullptr)
        {
            PostMessageW(existing, exitExisting ? WM_CLOSE : WM_VMWV_OPEN_SETTINGS, 0, 0);
            return;
        }
        Sleep(50);
    }
}
} // namespace

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    const CommandLineOptions options = ParseCommandLine();
    if (options.selfTest)
    {
        return RunSelfTest();
    }

    const HANDLE mutex = CreateMutexW(nullptr, TRUE, kMutexName);
    if (mutex == nullptr)
    {
        return 1;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        SignalExistingInstance(options.exitExisting, options.background);
        CloseHandle(mutex);
        return 0;
    }

    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult))
    {
        CloseHandle(mutex);
        return 2;
    }

    int result = 0;
    {
        NativeHost host(instance);
        if (!host.Initialize(!options.background))
        {
            result = 3;
        }
        else if (options.exitExisting)
        {
            result = 0;
        }
        else
        {
            result = host.Run();
        }
    }

    CoUninitialize();
    ReleaseMutex(mutex);
    CloseHandle(mutex);
    return result;
}
