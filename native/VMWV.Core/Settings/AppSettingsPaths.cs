namespace VMWV.Core.Settings;

public static class AppSettingsPaths
{
    public const string AppDataFolderName = "Voicemeeter Windows Volume";
    public const string SettingsFileName = "settings.json";

    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolderName,
            SettingsFileName);
}
