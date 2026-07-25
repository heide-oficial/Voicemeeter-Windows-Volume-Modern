#pragma once

#include <windows.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <atomic>

class AudioMonitor final : public IMMNotificationClient, public IAudioEndpointVolumeCallback
{
public:
    explicit AudioMonitor(HWND window);

    AudioMonitor(const AudioMonitor&) = delete;
    AudioMonitor& operator=(const AudioMonitor&) = delete;

    HRESULT Start();
    void Stop() noexcept;
    HRESULT Reattach();
    HRESULT Refresh(int& volume, bool& muted, bool& changed);
    [[nodiscard]] bool HasEndpoint() const noexcept;
    [[nodiscard]] int CurrentVolume() const noexcept;
    [[nodiscard]] bool CurrentMute() const noexcept;

    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID interfaceId, void** object) override;

    HRESULT STDMETHODCALLTYPE OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA notification) override;

    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR deviceId, DWORD newState) override;
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR deviceId) override;
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR deviceId) override;
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR deviceId) override;
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR deviceId, const PROPERTYKEY key) override;

private:
    ~AudioMonitor();

    std::atomic<ULONG> references_{1};
    HWND window_ = nullptr;
    Microsoft::WRL::ComPtr<IMMDeviceEnumerator> enumerator_;
    Microsoft::WRL::ComPtr<IMMDevice> device_;
    Microsoft::WRL::ComPtr<IAudioEndpointVolume> endpointVolume_;
    std::atomic<int> volume_{0};
    std::atomic<bool> muted_{false};
    bool registeredForDeviceNotifications_ = false;
    bool registeredForVolumeNotifications_ = false;

    static int ToVolumePercent(float scalar) noexcept;
};
