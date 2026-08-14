using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace VMWV_App.Localization;

public sealed class LocalizationService
{
    private const string DefaultLanguage = "en-us";
    private readonly Lock _sync = new();
    private FrozenDictionary<string, string> _fallbackStrings = FrozenDictionary<string, string>.Empty;
    private FrozenDictionary<string, string> _strings = FrozenDictionary<string, string>.Empty;
    private IReadOnlyList<LanguageOption> _availableLanguages = [];

    private LocalizationService()
    {
    }

    public static LocalizationService Current { get; } = new();

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    public IReadOnlyList<LanguageOption> AvailableLanguages => _availableLanguages;

    public void Initialize(string? requestedLanguage)
    {
        lock (_sync)
        {
            var languageFiles = DiscoverLanguageFiles();
            _fallbackStrings = LoadStrings(languageFiles.GetValueOrDefault(DefaultLanguage));
            _availableLanguages = languageFiles
                .Select(pair =>
                {
                    var strings = LoadStrings(pair.Value);
                    var displayName = strings.GetValueOrDefault("Language.DisplayName", pair.Key);
                    return new LanguageOption(pair.Key, displayName);
                })
                .OrderBy(option => option.Code.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            ApplyLanguage(requestedLanguage, languageFiles);
        }
    }

    public bool SetLanguage(string? languageCode)
    {
        bool changed;
        lock (_sync)
        {
            var languageFiles = DiscoverLanguageFiles();
            changed = ApplyLanguage(languageCode, languageFiles);
        }

        if (changed)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    public string Get(string key)
    {
        lock (_sync)
        {
            return _strings.GetValueOrDefault(key)
                ?? _fallbackStrings.GetValueOrDefault(key)
                ?? key;
        }
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    private bool ApplyLanguage(string? requestedLanguage, IReadOnlyDictionary<string, string> languageFiles)
    {
        var normalized = NormalizeLanguageCode(requestedLanguage);
        if (!languageFiles.TryGetValue(normalized, out var path))
        {
            normalized = DefaultLanguage;
            languageFiles.TryGetValue(normalized, out path);
        }

        var nextStrings = LoadStrings(path);
        if (nextStrings.Count == 0)
        {
            nextStrings = _fallbackStrings;
            normalized = DefaultLanguage;
        }

        var changed = !CurrentLanguage.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(_strings, nextStrings);
        CurrentLanguage = normalized;
        _strings = nextStrings;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(normalized);
        return changed;
    }

    private static Dictionary<string, string> DiscoverLanguageFiles()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        if (!Directory.Exists(directory))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .ToDictionary(
                path => NormalizeLanguageCode(Path.GetFileNameWithoutExtension(path)),
                path => path,
                StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, string> LoadStrings(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return FrozenDictionary<string, string>.Empty;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return values.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return FrozenDictionary<string, string>.Empty;
        }
        catch (IOException)
        {
            return FrozenDictionary<string, string>.Empty;
        }
    }

    private static string NormalizeLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? DefaultLanguage
            : languageCode.Trim().Replace('_', '-').ToLowerInvariant();
}
