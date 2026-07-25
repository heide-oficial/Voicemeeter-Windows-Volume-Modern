#include "audio_monitor.h"
#include "messages.h"

#include <algorithm>
#include <cmath>

AudioMonitor::AudioMonitor(const HWND window)
    : window_(window)
{
}

AudioMonitor::~AudioMonitor()
{
    Stop();
}

HRESULT AudioMonitor::Start()
{
    if (enumerator_ == nullptr)
    {
        const HRESULT createResult = CoCreateInstance(
            __uuidof(MMDeviceEnumerator),
            nullptr,
            CLSCTX_ALL,
            IID_PPV_ARGS(&enumerator_));
        if (FAILED(createResult))
        {
            return createResult;
        }
    }

    if (!registeredForDeviceNotifications_)
    {
        const HRESULT registerResult = enumerator_->RegisterEndpointNotificationCallback(this);
        if (FAILED(registerResult))
        {
            return registerResult;
        }
        registeredForDeviceNotifications_ = true;
    }

    return Reattach();
}

void AudioMonitor::Stop() noexcept
{
    if (endpointVolume_ != nullptr && registeredForVolumeNotifications_)
    {
        endpointVolume_->UnregisterControlChangeNotify(this);
        registeredForVolumeNotifications_ = false;
    }

    endpointVolume_.Reset();
    device_.Reset();

    if (enumerator_ != nullptr && registeredForDeviceNotifications_)
    {
        enumerator_->UnregisterEndpointNotificationCallback(this);
        registeredForDeviceNotifications_ = false;
    }

    enumerator_.Reset();
}

HRESULT AudioMonitor::Reattach()
{
    if (enumerator_ == nullptr)
    {
        const HRESULT startResult = Start();
        if (FAILED(startResult))
        {
            return startResult;
        }
        return S_OK;
    }

    if (endpointVolume_ != nullptr && registeredForVolumeNotifications_)
    {
        endpointVolume_->UnregisterControlChangeNotify(this);
        registeredForVolumeNotifications_ = false;
    }
    endpointVolume_.Reset();
    device_.Reset();

    const HRESULT deviceResult = enumerator_->GetDefaultAudioEndpoint(eRender, eMultimedia, &device_);
    if (FAILED(deviceResult))
    {
        return deviceResult;
    }

    const HRESULT activateResult = device_->Activate(
        __uuidof(IAudioEndpointVolume),
        CLSCTX_ALL,
        nullptr,
        reinterpret_cast<void**>(endpointVolume_.ReleaseAndGetAddressOf()));
    if (FAILED(activateResult))
    {
        device_.Reset();
        return activateResult;
    }

    const HRESULT registerResult = endpointVolume_->RegisterControlChangeNotify(this);
    if (FAILED(registerResult))
    {
        endpointVolume_.Reset();
        device_.Reset();
        return registerResult;
    }
    registeredForVolumeNotifications_ = true;

    int volume = volume_.load(std::memory_order_relaxed);
    bool muted = muted_.load(std::memory_order_relaxed);
    bool changed = false;
    return Refresh(volume, muted, changed);
}

HRESULT AudioMonitor::Refresh(int& volume, bool& muted, bool& changed)
{
    changed = false;
    if (endpointVolume_ == nullptr)
    {
        return AUDCLNT_E_DEVICE_INVALIDATED;
    }

    float scalar = 0.0F;
    BOOL muteValue = FALSE;
    HRESULT result = endpointVolume_->GetMasterVolumeLevelScalar(&scalar);
    if (FAILED(result))
    {
        return result;
    }
    result = endpointVolume_->GetMute(&muteValue);
    if (FAILED(result))
    {
        return result;
    }

    const int newVolume = ToVolumePercent(scalar);
    const bool newMute = muteValue != FALSE;
    changed = newVolume != volume_.load(std::memory_order_relaxed)
        || newMute != muted_.load(std::memory_order_relaxed);
    volume_.store(newVolume, std::memory_order_relaxed);
    muted_.store(newMute, std::memory_order_relaxed);
    volume = newVolume;
    muted = newMute;
    return S_OK;
}

bool AudioMonitor::HasEndpoint() const noexcept
{
    return endpointVolume_ != nullptr;
}

int AudioMonitor::CurrentVolume() const noexcept
{
    return volume_.load(std::memory_order_relaxed);
}

bool AudioMonitor::CurrentMute() const noexcept
{
    return muted_.load(std::memory_order_relaxed);
}

ULONG AudioMonitor::AddRef()
{
    return ++references_;
}

ULONG AudioMonitor::Release()
{
    const ULONG remaining = --references_;
    if (remaining == 0)
    {
        delete this;
    }
    return remaining;
}

HRESULT AudioMonitor::QueryInterface(REFIID interfaceId, void** object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;
    if (interfaceId == __uuidof(IUnknown) || interfaceId == __uuidof(IMMNotificationClient))
    {
        *object = static_cast<IMMNotificationClient*>(this);
    }
    else if (interfaceId == __uuidof(IAudioEndpointVolumeCallback))
    {
        *object = static_cast<IAudioEndpointVolumeCallback*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

HRESULT AudioMonitor::OnNotify(const PAUDIO_VOLUME_NOTIFICATION_DATA notification)
{
    if (notification == nullptr)
    {
        return E_POINTER;
    }

    const int newVolume = ToVolumePercent(notification->fMasterVolume);
    const bool newMute = notification->bMuted != FALSE;
    volume_.store(newVolume, std::memory_order_relaxed);
    muted_.store(newMute, std::memory_order_relaxed);
    PostMessageW(window_, WM_VMWV_AUDIO_CHANGED, static_cast<WPARAM>(newVolume), static_cast<LPARAM>(newMute));
    return S_OK;
}

HRESULT AudioMonitor::OnDeviceStateChanged(LPCWSTR, const DWORD)
{
    PostMessageW(window_, WM_VMWV_ANY_DEVICE_CHANGED, 0, 0);
    return S_OK;
}

HRESULT AudioMonitor::OnDeviceAdded(LPCWSTR)
{
    PostMessageW(window_, WM_VMWV_ANY_DEVICE_CHANGED, 0, 0);
    return S_OK;
}

HRESULT AudioMonitor::OnDeviceRemoved(LPCWSTR)
{
    PostMessageW(window_, WM_VMWV_ANY_DEVICE_CHANGED, 0, 0);
    return S_OK;
}

HRESULT AudioMonitor::OnDefaultDeviceChanged(
    const EDataFlow flow,
    const ERole role,
    LPCWSTR)
{
    if (flow == eRender && role == eMultimedia)
    {
        PostMessageW(window_, WM_VMWV_DEFAULT_DEVICE_CHANGED, 0, 0);
    }
    return S_OK;
}

HRESULT AudioMonitor::OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY)
{
    return S_OK;
}

int AudioMonitor::ToVolumePercent(const float scalar) noexcept
{
    return std::clamp(static_cast<int>(std::floor(scalar * 100.0F + 0.5F)), 0, 100);
}
