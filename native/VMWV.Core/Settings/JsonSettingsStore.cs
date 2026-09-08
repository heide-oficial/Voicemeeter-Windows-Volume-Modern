using System.Text.Json;

namespace VMWV.Core.Settings;

public sealed class JsonSettingsStore
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonSettingsStore(string settingsPath) => _settingsPath = settingsPath;
    public Exception? LoadError { get; private set; }

    public AppSettings LoadOrCreate()
    {
        LoadError = null;
        try
        {
            var settings = AppSettingsJsonSerializer.Deserialize(File.ReadAllText(_settingsPath))
                ?? throw new JsonException("The settings document is null.");
            settings.Normalize();
            LoadError = null;
            return settings;
        }
        catch (FileNotFoundException) { return new AppSettings(); }
        catch (DirectoryNotFoundException) { return new AppSettings(); }
        catch (JsonException)
        {
            // Preserve the original before allowing a replacement to be saved.
            try
            {
                File.Copy(_settingsPath, $"{_settingsPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
                return new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadError = ex;
                return new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoadError = ex;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = CreateSavePayload(settings);
        _writeLock.Wait();
        try
        {
            PrepareDirectory();
            var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, true);
            }
            finally { TryDelete(tempPath); }
        }
        finally { _writeLock.Release(); }
    }

    public string CreateSavePayload(AppSettings settings) => AppSettingsJsonSerializer.Serialize(settings);

    public async Task SavePayloadAsync(string json, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            PrepareDirectory();
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _settingsPath, true);
        }
        finally
        {
            TryDelete(tempPath);
            _writeLock.Release();
        }
    }

    private void PrepareDirectory()
    {
        if (LoadError is not null)
            throw new IOException("Settings could not be read. The existing file will not be overwritten.", LoadError);
        if (Path.GetDirectoryName(_settingsPath) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
