using System.Text.Json;

namespace VMWV.Core.Settings;

public sealed class JsonSettingsStore
{
    private readonly string _settingsPath;

    public JsonSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public AppSettings LoadOrCreate()
    {
        if (!File.Exists(_settingsPath))
        {
            var created = new AppSettings();
            Save(created);
            return created;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = AppSettingsJsonSerializer.Deserialize(json) ?? new AppSettings();
            if (settings.Normalize())
            {
                Save(settings);
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            BackupCorruptSettings();
            var created = new AppSettings();
            Save(created);
            return created;
        }
    }

    public void Save(AppSettings settings)
    {
        var json = CreateSavePayload(settings);
        SavePayload(json);
    }

    public string CreateSavePayload(AppSettings settings)
    {
        settings.Normalize();
        return AppSettingsJsonSerializer.Serialize(settings);
    }

    public async Task SavePayloadAsync(string json, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_settingsPath}.tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _settingsPath, true);
    }

    private void SavePayload(string json)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_settingsPath}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settingsPath, true);
    }

    private void BackupCorruptSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        var backupPath = $"{_settingsPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        File.Copy(_settingsPath, backupPath, true);
    }
}
