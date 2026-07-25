#include "settings.h"

#include <windows.h>
#include <shlobj.h>

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <string_view>
#include <vector>

namespace
{
constexpr std::string_view kWhitespace = " \t\r\n";

std::wstring DefaultSettingsPath()
{
    PWSTR localAppData = nullptr;
    const HRESULT result = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &localAppData);
    if (FAILED(result) || localAppData == nullptr)
    {
        return L"settings.json";
    }

    std::wstring path(localAppData);
    CoTaskMemFree(localAppData);
    path += L"\\Voicemeeter Windows Volume\\settings.json";
    return path;
}

void SkipWhitespace(const std::string_view text, std::size_t& position, const std::size_t end)
{
    while (position < end && kWhitespace.find(text[position]) != std::string_view::npos)
    {
        ++position;
    }
}

std::size_t FindKey(const std::string_view text, const std::string_view key, const std::size_t begin, const std::size_t end)
{
    std::string needle;
    needle.reserve(key.size() + 2);
    needle.push_back('"');
    needle.append(key);
    needle.push_back('"');

    std::size_t position = begin;
    while (position < end)
    {
        position = text.find(needle, position);
        if (position == std::string_view::npos || position >= end)
        {
            return std::string_view::npos;
        }

        std::size_t cursor = position + needle.size();
        SkipWhitespace(text, cursor, end);
        if (cursor < end && text[cursor] == ':')
        {
            return cursor + 1;
        }

        position += needle.size();
    }

    return std::string_view::npos;
}

bool FindValueRange(
    const std::string_view text,
    const std::string_view key,
    std::size_t& valueBegin,
    std::size_t& valueEnd,
    const std::size_t begin = 0,
    const std::size_t requestedEnd = std::string_view::npos)
{
    const std::size_t end = requestedEnd == std::string_view::npos ? text.size() : std::min(requestedEnd, text.size());
    std::size_t cursor = FindKey(text, key, begin, end);
    if (cursor == std::string_view::npos)
    {
        return false;
    }

    SkipWhitespace(text, cursor, end);
    if (cursor >= end)
    {
        return false;
    }

    valueBegin = cursor;
    if (text[cursor] == '"')
    {
        ++cursor;
        bool escaped = false;
        while (cursor < end)
        {
            const char character = text[cursor++];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                valueEnd = cursor;
                return true;
            }
        }

        return false;
    }

    if (text[cursor] == '[' || text[cursor] == '{')
    {
        const char open = text[cursor];
        const char close = open == '[' ? ']' : '}';
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (; cursor < end; ++cursor)
        {
            const char character = text[cursor];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == open)
            {
                ++depth;
            }
            else if (character == close && --depth == 0)
            {
                valueEnd = cursor + 1;
                return true;
            }
        }

        return false;
    }

    while (cursor < end && text[cursor] != ',' && text[cursor] != '}' && text[cursor] != ']'
        && kWhitespace.find(text[cursor]) == std::string_view::npos)
    {
        ++cursor;
    }

    valueEnd = cursor;
    return valueEnd > valueBegin;
}

std::optional<bool> GetBool(
    const std::string_view text,
    const std::string_view key,
    const std::size_t begin = 0,
    const std::size_t end = std::string_view::npos)
{
    std::size_t valueBegin = 0;
    std::size_t valueEnd = 0;
    if (!FindValueRange(text, key, valueBegin, valueEnd, begin, end))
    {
        return std::nullopt;
    }

    const std::string_view value = text.substr(valueBegin, valueEnd - valueBegin);
    if (value == "true")
    {
        return true;
    }
    if (value == "false")
    {
        return false;
    }
    return std::nullopt;
}

std::optional<double> GetNumber(
    const std::string_view text,
    const std::string_view key,
    const std::size_t begin = 0,
    const std::size_t end = std::string_view::npos)
{
    std::size_t valueBegin = 0;
    std::size_t valueEnd = 0;
    if (!FindValueRange(text, key, valueBegin, valueEnd, begin, end))
    {
        return std::nullopt;
    }

    const std::string value(text.substr(valueBegin, valueEnd - valueBegin));
    char* parseEnd = nullptr;
    const double parsed = std::strtod(value.c_str(), &parseEnd);
    if (parseEnd == value.c_str() || !std::isfinite(parsed))
    {
        return std::nullopt;
    }
    return parsed;
}

std::optional<std::string> GetString(
    const std::string_view text,
    const std::string_view key,
    const std::size_t begin,
    const std::size_t end)
{
    std::size_t valueBegin = 0;
    std::size_t valueEnd = 0;
    if (!FindValueRange(text, key, valueBegin, valueEnd, begin, end)
        || valueEnd <= valueBegin + 1
        || text[valueBegin] != '"'
        || text[valueEnd - 1] != '"')
    {
        return std::nullopt;
    }

    std::string value;
    value.reserve(valueEnd - valueBegin - 2);
    bool escaped = false;
    for (std::size_t index = valueBegin + 1; index + 1 < valueEnd; ++index)
    {
        const char character = text[index];
        if (escaped)
        {
            switch (character)
            {
            case '"': value.push_back('"'); break;
            case '\\': value.push_back('\\'); break;
            case '/': value.push_back('/'); break;
            case 'b': value.push_back('\b'); break;
            case 'f': value.push_back('\f'); break;
            case 'n': value.push_back('\n'); break;
            case 'r': value.push_back('\r'); break;
            case 't': value.push_back('\t'); break;
            default: value.push_back(character); break;
            }
            escaped = false;
        }
        else if (character == '\\')
        {
            escaped = true;
        }
        else
        {
            value.push_back(character);
        }
    }

    return value;
}

void ApplyToggle(RuntimeSettings& settings, const std::string& id, const bool value)
{
    constexpr std::string_view stripPrefix = "Strip_";
    constexpr std::string_view busPrefix = "Bus_";

    if (id.starts_with(stripPrefix) && id.size() == stripPrefix.size() + 1)
    {
        const char digit = id.back();
        if (digit >= '0' && digit <= '7')
        {
            settings.strips[static_cast<std::size_t>(digit - '0')] = value;
        }
        return;
    }

    if (id.starts_with(busPrefix) && id.size() == busPrefix.size() + 1)
    {
        const char digit = id.back();
        if (digit >= '0' && digit <= '7')
        {
            settings.buses[static_cast<std::size_t>(digit - '0')] = value;
        }
        return;
    }

    if (id == "linear_volume_scale")
    {
        settings.linearVolumeScale = value;
    }
    else if (id == "restart_audio_engine_on_app_launch")
    {
        settings.restartOnAppLaunch = value;
    }
    else if (id == "restart_audio_engine_on_device_change")
    {
        settings.restartOnDeviceChange = value;
    }
    else if (id == "restart_audio_engine_on_any_device_change")
    {
        settings.restartOnAnyDeviceChange = value;
    }
    else if (id == "restart_audio_engine_on_resume")
    {
        settings.restartOnResume = value;
    }
}

RuntimeSettings ParseSettings(const std::string_view text)
{
    RuntimeSettings settings;

    if (const auto value = GetNumber(text, "gain_min"))
    {
        settings.gainMin = *value;
    }
    if (const auto value = GetNumber(text, "gain_max"))
    {
        settings.gainMax = *value;
    }
    if (const auto value = GetString(text, "logo_variant", 0, text.size()))
    {
        settings.logoVariant = *value == "Black" || *value == "White" ? *value : "Color";
    }
    if (const auto value = GetBool(text, "limit_db_gain_to_0"))
    {
        settings.limitDbGainToZero = *value;
    }
    if (const auto value = GetBool(text, "sync_mute"))
    {
        settings.syncMute = *value;
    }
    if (const auto value = GetBool(text, "remember_volume"))
    {
        settings.rememberVolume = *value;
    }

    std::size_t initialBegin = 0;
    std::size_t initialEnd = 0;
    if (FindValueRange(text, "initial_volume", initialBegin, initialEnd))
    {
        const std::string_view token = text.substr(initialBegin, initialEnd - initialBegin);
        if (token != "null")
        {
            if (const auto value = GetNumber(text, "initial_volume"))
            {
                settings.initialVolume = std::clamp(static_cast<int>(std::lround(*value)), 0, 100);
            }
        }
    }

    std::size_t togglesBegin = 0;
    std::size_t togglesEnd = 0;
    if (!FindValueRange(text, "toggles", togglesBegin, togglesEnd)
        || togglesBegin >= togglesEnd
        || text[togglesBegin] != '[')
    {
        return settings;
    }

    std::size_t cursor = togglesBegin + 1;
    while (cursor < togglesEnd)
    {
        const std::size_t objectBegin = text.find('{', cursor);
        if (objectBegin == std::string_view::npos || objectBegin >= togglesEnd)
        {
            break;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        std::size_t objectEnd = objectBegin;
        for (; objectEnd < togglesEnd; ++objectEnd)
        {
            const char character = text[objectEnd];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                ++depth;
            }
            else if (character == '}' && --depth == 0)
            {
                ++objectEnd;
                break;
            }
        }

        if (objectEnd <= objectBegin || objectEnd > togglesEnd)
        {
            break;
        }

        const auto setting = GetString(text, "setting", objectBegin, objectEnd);
        const auto value = GetBool(text, "value", objectBegin, objectEnd);
        if (setting && value)
        {
            ApplyToggle(settings, *setting, *value);
        }

        cursor = objectEnd;
    }

    return settings;
}

bool GetTimestamp(const std::wstring& path, unsigned long& low, unsigned long& high)
{
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
    {
        return false;
    }

    low = attributes.ftLastWriteTime.dwLowDateTime;
    high = attributes.ftLastWriteTime.dwHighDateTime;
    return true;
}
} // namespace

bool RuntimeSettings::HasSelectedTargets() const noexcept
{
    return std::any_of(strips.begin(), strips.end(), [](const bool selected) { return selected; })
        || std::any_of(buses.begin(), buses.end(), [](const bool selected) { return selected; });
}

SettingsStore::SettingsStore()
    : path_(DefaultSettingsPath())
{
}

const std::wstring& SettingsStore::Path() const noexcept
{
    return path_;
}

RuntimeSettings SettingsStore::Load()
{
    std::string text;
    RuntimeSettings settings;
    if (ReadText(text))
    {
        settings = ParseSettings(text);
    }

    CaptureTimestamp();
    return settings;
}

bool SettingsStore::ReloadIfChanged(RuntimeSettings& settings)
{
    if (!HasChangedOnDisk())
    {
        return false;
    }

    settings = Load();
    return true;
}

bool SettingsStore::SaveInitialVolume(const int volume)
{
    std::string text;
    if (!ReadText(text))
    {
        return false;
    }

    const std::string replacement = std::to_string(std::clamp(volume, 0, 100));
    std::size_t valueBegin = 0;
    std::size_t valueEnd = 0;
    if (FindValueRange(text, "initial_volume", valueBegin, valueEnd))
    {
        text.replace(valueBegin, valueEnd - valueBegin, replacement);
    }
    else
    {
        const std::size_t objectEnd = text.rfind('}');
        if (objectEnd == std::string::npos)
        {
            return false;
        }

        std::size_t previous = objectEnd;
        while (previous > 0 && kWhitespace.find(text[previous - 1]) != std::string_view::npos)
        {
            --previous;
        }
        const bool needsComma = previous > 0 && text[previous - 1] != '{';
        const std::string insertion = std::string(needsComma ? "," : "")
            + "\n  \"initial_volume\": " + replacement + "\n";
        text.insert(objectEnd, insertion);
    }

    if (!WriteTextAtomically(text))
    {
        return false;
    }

    CaptureTimestamp();
    return true;
}

bool SettingsStore::ReadText(std::string& text) const
{
    const HANDLE file = CreateFileW(
        path_.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(file, &fileSize) || fileSize.QuadPart < 0 || fileSize.QuadPart > 1024 * 1024)
    {
        CloseHandle(file);
        return false;
    }

    text.resize(static_cast<std::size_t>(fileSize.QuadPart));
    DWORD bytesRead = 0;
    const BOOL readResult = text.empty()
        || ReadFile(file, text.data(), static_cast<DWORD>(text.size()), &bytesRead, nullptr);
    CloseHandle(file);
    if (!readResult)
    {
        text.clear();
        return false;
    }

    text.resize(bytesRead);
    if (text.size() >= 3
        && static_cast<unsigned char>(text[0]) == 0xEF
        && static_cast<unsigned char>(text[1]) == 0xBB
        && static_cast<unsigned char>(text[2]) == 0xBF)
    {
        text.erase(0, 3);
    }
    return true;
}

bool SettingsStore::WriteTextAtomically(const std::string& text) const
{
    const std::size_t separator = path_.find_last_of(L"\\/");
    if (separator != std::wstring::npos)
    {
        const std::wstring directory = path_.substr(0, separator);
        CreateDirectoryW(directory.c_str(), nullptr);
    }

    const std::wstring temporaryPath = path_ + L".native-host.tmp";
    const HANDLE file = CreateFileW(
        temporaryPath.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    DWORD bytesWritten = 0;
    const BOOL writeResult = text.empty()
        || WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &bytesWritten, nullptr);
    if (writeResult)
    {
        FlushFileBuffers(file);
    }
    CloseHandle(file);

    if (!writeResult || static_cast<std::size_t>(bytesWritten) != text.size())
    {
        DeleteFileW(temporaryPath.c_str());
        return false;
    }

    if (!MoveFileExW(
            temporaryPath.c_str(),
            path_.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        DeleteFileW(temporaryPath.c_str());
        return false;
    }
    return true;
}

void SettingsStore::CaptureTimestamp()
{
    hasTimestamp_ = GetTimestamp(path_, lastWriteLow_, lastWriteHigh_);
}

bool SettingsStore::HasChangedOnDisk() const
{
    unsigned long low = 0;
    unsigned long high = 0;
    const bool exists = GetTimestamp(path_, low, high);
    if (!exists)
    {
        return hasTimestamp_;
    }
    if (!hasTimestamp_)
    {
        return true;
    }
    return low != lastWriteLow_ || high != lastWriteHigh_;
}
