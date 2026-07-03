using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VMWV.Core.Settings;

internal static class AppSettingsJsonSerializer
{
    private static readonly JsonTypeInfo<AppSettings> TypeInfo = AppSettingsJsonContext.Default.AppSettings;

    public static AppSettings? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, TypeInfo);

    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, TypeInfo);
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
