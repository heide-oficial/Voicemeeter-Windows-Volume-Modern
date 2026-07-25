#pragma once

#include <array>
#include <optional>
#include <string>

struct RuntimeSettings
{
    double gainMin = -60.0;
    double gainMax = 12.0;
    std::string logoVariant = "Color";
    bool limitDbGainToZero = false;
    bool linearVolumeScale = false;
    bool syncMute = true;
    bool rememberVolume = false;
    std::optional<int> initialVolume;
    bool restartOnAppLaunch = false;
    bool restartOnDeviceChange = false;
    bool restartOnAnyDeviceChange = false;
    bool restartOnResume = false;
    std::array<bool, 8> strips{};
    std::array<bool, 8> buses{};

    [[nodiscard]] bool HasSelectedTargets() const noexcept;
};

class SettingsStore
{
public:
    SettingsStore();

    [[nodiscard]] const std::wstring& Path() const noexcept;
    [[nodiscard]] RuntimeSettings Load();
    [[nodiscard]] bool ReloadIfChanged(RuntimeSettings& settings);
    bool SaveInitialVolume(int volume);

private:
    std::wstring path_;
    unsigned long lastWriteLow_ = 0;
    unsigned long lastWriteHigh_ = 0;
    bool hasTimestamp_ = false;

    [[nodiscard]] bool ReadText(std::string& text) const;
    [[nodiscard]] bool WriteTextAtomically(const std::string& text) const;
    void CaptureTimestamp();
    [[nodiscard]] bool HasChangedOnDisk() const;
};
