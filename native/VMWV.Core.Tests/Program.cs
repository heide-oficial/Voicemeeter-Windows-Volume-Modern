using System.Text.Json;
using VMWV.Core.Settings;
using VMWV.Core.Services;
using VMWV.Core.Voicemeeter;
using VMWV.Core.Volume;

var tests = new List<(string Name, Action Test)>
{
    ("linear scale maps 0 to min", () =>
    {
        AssertEqual(-60d, VolumeMapper.ToVoicemeeterGain(0, -60, 12, false, true));
    }),
    ("standard buses use A1 and B1 roles", () =>
    {
        AssertEqual("A1", VoicemeeterChannelNames.GetRoleName("Voicemeeter", "Bus", 0));
        AssertEqual("B1", VoicemeeterChannelNames.GetRoleName("Voicemeeter", "Bus", 1));
    }),
    ("banana and potato buses use edition roles", () =>
    {
        AssertEqual("B1", VoicemeeterChannelNames.GetRoleName("Voicemeeter Banana", "Bus", 3));
        AssertEqual("B1", VoicemeeterChannelNames.GetRoleName("Voicemeeter Potato", "Bus", 5));
    }),
    ("release tags support v prefix and prerelease suffix", () =>
    {
        AssertTrue(ReleaseVersionParser.TryParseTag("v1.2.0-preview.1", out var version));
        AssertEqual(new Version(1, 2, 0), version);
    }),
    ("custom channel label follows binding hierarchy", () =>
    {
        var display = VoicemeeterChannelNames.FormatDisplay(
            "Voicemeeter Banana",
            "Bus",
            0,
            "Headphones",
            "Speakers");
        AssertEqual("Headphones", display.Title);
        AssertEqual("Bus 0", display.IndexCaption);
        AssertEqual("Speakers", display.DeviceCaption);
    }),
    ("technical fallback is preserved for unknown editions", () =>
    {
        var display = VoicemeeterChannelNames.FormatDisplay(
            "Unknown",
            "Bus",
            0,
            "Bus 0",
            null);
        AssertEqual("Bus 0", display.Title);
        AssertEqual("Bus 0", display.IndexCaption);
        AssertEqual(string.Empty, display.DeviceCaption);
    }),
    ("quiet 100 percent jump restores the stable volume", () =>
    {
        var time = new ManualTimeProvider();
        var recovery = new VolumeRecoveryCoordinator(time);
        recovery.Seed(35, null);
        time.Advance(TimeSpan.FromSeconds(2));

        var decision = recovery.ObserveVolumeChange(35, 100, preventVolumeSpikes: true);

        AssertEqual(35, decision.RestoreVolume);
        AssertTrue(!decision.ShouldRememberVolume);
    }),
    ("gradual move to 100 percent remains allowed", () =>
    {
        var time = new ManualTimeProvider();
        var recovery = new VolumeRecoveryCoordinator(time);
        recovery.Seed(80, null);
        recovery.ObserveVolumeChange(80, 95, preventVolumeSpikes: true);
        time.Advance(TimeSpan.FromMilliseconds(500));

        var decision = recovery.ObserveVolumeChange(95, 100, preventVolumeSpikes: true);

        AssertEqual<int?>(null, decision.RestoreVolume);
        AssertTrue(decision.ShouldRememberVolume);
    }),
    ("engine restart guard restores volume even when spike protection is off", () =>
    {
        var recovery = new VolumeRecoveryCoordinator(new ManualTimeProvider());
        recovery.Seed(42, null);
        AssertEqual(42, recovery.BeginEngineRestart(42, null));

        var decision = recovery.ObserveVolumeChange(42, 100, preventVolumeSpikes: false);

        AssertEqual(42, decision.RestoreVolume);
    }),
    ("remembered volume is only a restart fallback", () =>
    {
        var recovery = new VolumeRecoveryCoordinator(new ManualTimeProvider());
        recovery.Seed(100, 30);

        AssertEqual(30, recovery.BeginEngineRestart(100, 30));
    }),
    ("intentional 100 percent remains the restart volume", () =>
    {
        var recovery = new VolumeRecoveryCoordinator(new ManualTimeProvider());
        recovery.Seed(80, 80);
        recovery.ObserveVolumeChange(80, 95, preventVolumeSpikes: true);
        recovery.ObserveVolumeChange(95, 100, preventVolumeSpikes: true);

        AssertEqual(100, recovery.BeginEngineRestart(100, 80));
    }),
    ("linear scale respects zero dB limit", () =>
    {
        AssertEqual(0d, VolumeMapper.ToVoicemeeterGain(100, -60, 12, true, true));
    }),
    ("log scale clamps zero to min", () =>
    {
        AssertEqual(-60d, VolumeMapper.ToVoicemeeterGain(0, -60, 12, false, false));
    }),
    ("log scale maps 100 to configured max", () =>
    {
        AssertEqual(12d, VolumeMapper.ToVoicemeeterGain(100, -60, 12, false, false));
    }),
    ("settings preserve initial volume zero", () =>
    {
        var settings = new AppSettings { InitialVolume = 0 };
        settings.Normalize();
        AssertEqual(0, settings.InitialVolume);
    }),
    ("settings normalize polling range", () =>
    {
        var settings = new AppSettings { PollingRate = 1 };
        settings.Normalize();
        AssertEqual(25, settings.PollingRate);
    }),
    ("settings normalize language codes", () =>
    {
        var settings = new AppSettings { Language = " pt_BR " };
        settings.Normalize();
        AssertEqual("pt-br", settings.Language);
    }),
    ("settings persist support page visibility", () =>
    {
        var json = JsonSerializer.Serialize(new AppSettings { HideSupportPage = true });
        AssertTrue(json.Contains("\"hide_support_page\":true", StringComparison.Ordinal));
    }),
    ("settings deduplicate toggles using last value", () =>
    {
        var settings = new AppSettings
        {
            Toggles =
            [
                new("sync_target", false),
                new("sync_target", true)
            ]
        };

        settings.Normalize();
        AssertEqual(1, settings.Toggles.Count);
        AssertTrue(settings.IsToggleEnabled("sync_target"));
    }),
    ("settings store backs up corrupt json", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vmwv-core-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, "{ broken json");

        var store = new JsonSettingsStore(settingsPath);
        var settings = store.LoadOrCreate();

        AssertEqual(100, settings.PollingRate);
        AssertTrue(Directory.GetFiles(directory, "settings.json.corrupt-*").Length == 1);
        JsonDocument.Parse(File.ReadAllText(settingsPath));

        Directory.Delete(directory, true);
    })
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Test();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

if (failed > 0)
{
    Environment.Exit(1);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }
}

static void AssertTrue(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true");
    }
}

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}
