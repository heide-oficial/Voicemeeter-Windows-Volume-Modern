#include "voicemeeter_client.h"

#include <windows.h>

#include <array>
#include <cstring>
#include <filesystem>
#include <vector>

namespace
{
std::wstring EnvironmentPath(const wchar_t* name)
{
    const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
    if (required == 0)
    {
        return {};
    }

    std::wstring value(required, L'\0');
    const DWORD written = GetEnvironmentVariableW(name, value.data(), required);
    if (written == 0 || written >= required)
    {
        return {};
    }
    value.resize(written);
    return value;
}

template <typename T>
T LoadFunction(const HMODULE module, const char* name)
{
    const FARPROC address = GetProcAddress(module, name);
    T function = nullptr;
    static_assert(sizeof(function) == sizeof(address));
    std::memcpy(&function, &address, sizeof(function));
    return function;
}

std::wstring ExecutableDirectory()
{
    std::wstring buffer(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || static_cast<std::size_t>(length) >= buffer.size())
    {
        return {};
    }
    buffer.resize(length);
    const std::size_t separator = buffer.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : buffer.substr(0, separator);
}
} // namespace

VoicemeeterClient::~VoicemeeterClient()
{
    Disconnect();
    if (module_ != nullptr)
    {
        FreeLibrary(static_cast<HMODULE>(module_));
        module_ = nullptr;
    }
}

bool VoicemeeterClient::Connect()
{
    if (connected_)
    {
        return true;
    }
    if (!LoadLibraryIfNeeded())
    {
        return false;
    }

    const long result = login_();
    if (result < 0)
    {
        SetError(L"Voicemeeter login failed", result);
        return false;
    }

    connected_ = true;
    editionType_ = 0;
    long type = 0;
    if (getVoicemeeterType_(&type) >= 0)
    {
        editionType_ = static_cast<int>(type);
    }
    lastError_.clear();
    return true;
}

void VoicemeeterClient::Disconnect() noexcept
{
    if (connected_ && logout_ != nullptr)
    {
        logout_();
    }
    connected_ = false;
    editionType_ = 0;
}

bool VoicemeeterClient::IsConnected() const noexcept
{
    return connected_;
}

int VoicemeeterClient::EditionType() const noexcept
{
    return editionType_;
}

bool VoicemeeterClient::SetParameters(const std::string_view script)
{
    if (!connected_ || setParameters_ == nullptr || script.empty())
    {
        return false;
    }

    std::vector<char> mutableScript(script.begin(), script.end());
    mutableScript.push_back('\0');
    const long result = setParameters_(mutableScript.data());
    if (result < 0)
    {
        SetError(L"Voicemeeter command failed", result);
        Disconnect();
        return false;
    }
    return true;
}

bool VoicemeeterClient::RestartAudioEngine()
{
    return SetParameters("Command.Restart = 1;");
}

bool VoicemeeterClient::Show()
{
    return SetParameters("Command.Show = 1;");
}

const std::wstring& VoicemeeterClient::LastError() const noexcept
{
    return lastError_;
}

bool VoicemeeterClient::LoadLibraryIfNeeded()
{
    if (module_ != nullptr)
    {
        return true;
    }

    const std::wstring path = ResolveLibraryPath();
    if (path.empty())
    {
        SetError(L"VoicemeeterRemote64.dll was not found");
        return false;
    }

    const HMODULE module = LoadLibraryW(path.c_str());
    if (module == nullptr)
    {
        SetError(L"Unable to load VoicemeeterRemote64.dll", static_cast<long>(GetLastError()));
        return false;
    }

    module_ = module;
    login_ = LoadFunction<LoginFunction>(module, "VBVMR_Login");
    logout_ = LoadFunction<LogoutFunction>(module, "VBVMR_Logout");
    getVoicemeeterType_ = LoadFunction<GetVoicemeeterTypeFunction>(module, "VBVMR_GetVoicemeeterType");
    setParameters_ = LoadFunction<SetParametersFunction>(module, "VBVMR_SetParameters");

    if (login_ == nullptr || logout_ == nullptr || getVoicemeeterType_ == nullptr || setParameters_ == nullptr)
    {
        SetError(L"Voicemeeter remote library is missing required exports");
        FreeLibrary(module);
        module_ = nullptr;
        login_ = nullptr;
        logout_ = nullptr;
        getVoicemeeterType_ = nullptr;
        setParameters_ = nullptr;
        return false;
    }

    return true;
}

std::wstring VoicemeeterClient::ResolveLibraryPath() const
{
    const std::wstring executableDirectory = ExecutableDirectory();
    const std::wstring programFiles = EnvironmentPath(L"ProgramW6432");
    const std::wstring programFilesFallback = EnvironmentPath(L"ProgramFiles");
    const std::wstring programFilesX86 = EnvironmentPath(L"ProgramFiles(x86)");

    const std::array<std::wstring, 4> roots =
    {
        executableDirectory,
        programFiles,
        programFilesFallback,
        programFilesX86
    };

    for (const std::wstring& root : roots)
    {
        if (root.empty())
        {
            continue;
        }

        const std::filesystem::path candidate = root == executableDirectory
            ? std::filesystem::path(root) / L"VoicemeeterRemote64.dll"
            : std::filesystem::path(root) / L"VB" / L"Voicemeeter" / L"VoicemeeterRemote64.dll";
        if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return candidate.wstring();
        }
    }

    return {};
}

void VoicemeeterClient::SetError(const wchar_t* message, const long code)
{
    lastError_ = message;
    if (code != 0)
    {
        lastError_ += L" (";
        lastError_ += std::to_wstring(code);
        lastError_ += L")";
    }
}
