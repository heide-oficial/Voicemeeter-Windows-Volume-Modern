using System.Text.Json.Serialization;

namespace VMWV.Core.Settings;

public sealed class AppSettings
{
    private Dictionary<string, ToggleSetting>? _toggleIndex;
    private List<ToggleSetting> _toggles =
    [
        new("restart_audio_engine_on_device_change", false),
        new("restart_audio_engine_on_app_launch", false)
    ];

    [JsonPropertyName("polling_rate")]
    public int PollingRate { get; set; } = 100;

    [JsonPropertyName("gain_min")]
    public double GainMin { get; set; } = -60;

    [JsonPropertyName("gain_max")]
    public double GainMax { get; set; } = 12;

    [JsonPropertyName("start_with_windows")]
    public bool StartWithWindows { get; set; } = true;

    [JsonPropertyName("close_to_tray")]
    public bool CloseToTray { get; set; }

    [JsonPropertyName("logo_variant")]
    public string LogoVariant { get; set; } = "Color";

    [JsonPropertyName("limit_db_gain_to_0")]
    public bool LimitDbGainToZero { get; set; }

    [JsonPropertyName("sync_mute")]
    public bool SyncMute { get; set; } = true;

    [JsonPropertyName("remember_volume")]
    public bool RememberVolume { get; set; }

    [JsonPropertyName("disable_donate")]
    public bool DisableDonate { get; set; }

    [JsonPropertyName("initial_volume")]
    public int? InitialVolume { get; set; }

    [JsonPropertyName("audiodg")]
    public AudiodgSettings Audiodg { get; set; } = new();

    [JsonPropertyName("toggles")]
    public List<ToggleSetting> Toggles
    {
        get => _toggles;
        set
        {
            _toggles = value;
            _toggleIndex = null;
        }
    }

    [JsonPropertyName("device_blacklist")]
    public List<string> DeviceBlacklist { get; set; } =
    [
        "Microsoft Streaming Service Proxy",
        "Volume",
        "Xvd"
    ];

    public bool IsToggleEnabled(string settingId) =>
        ToggleIndex.TryGetValue(settingId, out var toggle) && toggle.Value;

    public void SetToggle(string settingId, bool value)
    {
        var index = ToggleIndex;
        var toggle = index.GetValueOrDefault(settingId);
        if (toggle is null)
        {
            toggle = new ToggleSetting(settingId, value);
            Toggles.Add(toggle);
            index[settingId] = toggle;
            return;
        }

        toggle.Value = value;
    }

    public bool Normalize()
    {
        var changed = false;
        if (Audiodg is null)
        {
            Audiodg = new AudiodgSettings();
            changed = true;
        }

        if (Toggles is null)
        {
            Toggles = [];
            changed = true;
        }

        var normalizedToggles = Toggles
            .Where(toggle => !string.IsNullOrWhiteSpace(toggle.Setting))
            .GroupBy(toggle => toggle.Setting, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        if (normalizedToggles.Count != Toggles.Count)
        {
            Toggles = normalizedToggles;
            changed = true;
        }

        _toggleIndex = null;
        if (DeviceBlacklist is null)
        {
            DeviceBlacklist = [];
            changed = true;
        }

        var normalizedLogoVariant = LogoVariant switch
        {
            "Black" => "Black",
            "White" => "White",
            _ => "Color"
        };

        if (LogoVariant != normalizedLogoVariant)
        {
            LogoVariant = normalizedLogoVariant;
            changed = true;
        }

        var normalizedPollingRate = Math.Clamp(PollingRate, 25, 10_000);
        if (PollingRate != normalizedPollingRate)
        {
            PollingRate = normalizedPollingRate;
            changed = true;
        }

        int? normalizedInitialVolume = InitialVolume is null ? null : Math.Clamp(InitialVolume.Value, 0, 100);
        if (InitialVolume != normalizedInitialVolume)
        {
            InitialVolume = normalizedInitialVolume;
            changed = true;
        }

        return changed;
    }

    private Dictionary<string, ToggleSetting> ToggleIndex =>
        _toggleIndex ??= Toggles
            .Where(toggle => !string.IsNullOrWhiteSpace(toggle.Setting))
            .GroupBy(toggle => toggle.Setting, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
}

public sealed class AudiodgSettings
{
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 128;

    [JsonPropertyName("affinity")]
    public int Affinity { get; set; } = 2;
}

public sealed class ToggleSetting
{
    public ToggleSetting()
    {
    }

    public ToggleSetting(string setting, bool value)
    {
        Setting = setting;
        Value = value;
    }

    [JsonPropertyName("setting")]
    public string Setting { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public bool Value { get; set; }
}
