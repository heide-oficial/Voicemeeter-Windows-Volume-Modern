using System.Text.Json;
using VMWV.Core.Settings;
using VMWV.Core.Volume;

var tests = new List<(string Name, Action Test)>
{
    ("linear scale maps 0 to min", () =>
    {
        AssertEqual(-60d, VolumeMapper.ToVoicemeeterGain(0, -60, 12, false, true));
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
