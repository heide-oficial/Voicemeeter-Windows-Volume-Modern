namespace VMWV.Core.Voicemeeter;

public static class VoicemeeterChannelNames
{
    private static readonly string[] StandardStrips = ["Hardware 1", "Hardware 2", "VAIO"];
    private static readonly string[] StandardBuses = ["A1", "B1"];
    private static readonly string[] BananaStrips = ["Hardware 1", "Hardware 2", "Hardware 3", "VAIO", "AUX"];
    private static readonly string[] BananaBuses = ["A1", "A2", "A3", "B1", "B2"];
    private static readonly string[] PotatoStrips = ["Hardware 1", "Hardware 2", "Hardware 3", "Hardware 4", "Hardware 5", "VAIO", "AUX", "VAIO 3"];
    private static readonly string[] PotatoBuses = ["A1", "A2", "A3", "A4", "A5", "B1", "B2", "B3"];

    public static string GetRoleName(string edition, string kind, int index)
    {
        var names = ResolveNames(edition, kind);
        return index >= 0 && index < names.Length
            ? names[index]
            : GetIndexCaption(kind, index);
    }

    public static string GetIndexCaption(string kind, int index) =>
        kind.Equals("Strip", StringComparison.OrdinalIgnoreCase)
            ? $"Strip {index}"
            : kind.Equals("Bus", StringComparison.OrdinalIgnoreCase)
                ? $"Bus {index}"
                : $"{kind} {index}";

    public static VoicemeeterChannelDisplay FormatDisplay(
        string edition,
        string kind,
        int index,
        string? customLabel,
        string? deviceName)
    {
        var roleName = GetRoleName(edition, kind, index);
        var indexCaption = GetIndexCaption(kind, index);
        var normalizedLabel = customLabel?.Trim();
        var hasCustomLabel = !string.IsNullOrWhiteSpace(normalizedLabel)
            && !normalizedLabel.Equals(roleName, StringComparison.OrdinalIgnoreCase)
            && !normalizedLabel.Equals(indexCaption, StringComparison.OrdinalIgnoreCase);

        var title = hasCustomLabel ? normalizedLabel! : roleName;
        var deviceCaption = string.IsNullOrWhiteSpace(deviceName) ? string.Empty : deviceName.Trim();
        return new VoicemeeterChannelDisplay(title, indexCaption, deviceCaption);
    }

    private static string[] ResolveNames(string edition, string kind)
    {
        var isStrip = kind.Equals("Strip", StringComparison.OrdinalIgnoreCase);
        var isBus = kind.Equals("Bus", StringComparison.OrdinalIgnoreCase);

        if (edition.Contains("Potato", StringComparison.OrdinalIgnoreCase))
        {
            return isStrip ? PotatoStrips : isBus ? PotatoBuses : [];
        }

        if (edition.Contains("Banana", StringComparison.OrdinalIgnoreCase))
        {
            return isStrip ? BananaStrips : isBus ? BananaBuses : [];
        }

        if (edition.Equals("Voicemeeter", StringComparison.OrdinalIgnoreCase))
        {
            return isStrip ? StandardStrips : isBus ? StandardBuses : [];
        }

        return [];
    }
}

public readonly record struct VoicemeeterChannelDisplay(
    string Title,
    string IndexCaption,
    string DeviceCaption);
