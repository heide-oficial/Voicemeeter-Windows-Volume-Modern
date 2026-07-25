#pragma once

#include <string>
#include <string_view>

class VoicemeeterClient
{
public:
    VoicemeeterClient() = default;
    ~VoicemeeterClient();

    VoicemeeterClient(const VoicemeeterClient&) = delete;
    VoicemeeterClient& operator=(const VoicemeeterClient&) = delete;

    [[nodiscard]] bool Connect();
    void Disconnect() noexcept;
    [[nodiscard]] bool IsConnected() const noexcept;
    [[nodiscard]] int EditionType() const noexcept;
    [[nodiscard]] bool SetParameters(std::string_view script);
    [[nodiscard]] bool RestartAudioEngine();
    [[nodiscard]] bool Show();
    [[nodiscard]] const std::wstring& LastError() const noexcept;

private:
    using LoginFunction = long(__stdcall*)();
    using LogoutFunction = long(__stdcall*)();
    using GetVoicemeeterTypeFunction = long(__stdcall*)(long* type);
    using SetParametersFunction = long(__stdcall*)(char* script);

    void* module_ = nullptr;
    LoginFunction login_ = nullptr;
    LogoutFunction logout_ = nullptr;
    GetVoicemeeterTypeFunction getVoicemeeterType_ = nullptr;
    SetParametersFunction setParameters_ = nullptr;
    bool connected_ = false;
    int editionType_ = 0;
    std::wstring lastError_;

    [[nodiscard]] bool LoadLibraryIfNeeded();
    [[nodiscard]] std::wstring ResolveLibraryPath() const;
    void SetError(const wchar_t* message, long code = 0);
};
