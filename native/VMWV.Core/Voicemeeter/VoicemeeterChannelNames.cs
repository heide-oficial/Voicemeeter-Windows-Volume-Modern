namespace VMWV.Core.Voicemeeter;

/// <summary>
/// Maps Voicemeeter strip/bus indexes to the labels used in the Voicemeeter UI
/// (A1, B1, Hardware 1, VAIO, …) for each edition.
/// </summary>
public static class VoicemeeterChannelNames
{
    // Basic (type 1): 3 strips, 2 buses
    private static readonly string[] BasicStrips =
    [
        "Hardware 1",
        "Hardware 2",
        "VAIO"
    ];

    private static readonly string[] BasicBuses =
    [
        "A1",
        "A2"
    ];

    // Banana (type 2): 5 strips, 5 buses
    private static readonly string[] BananaStrips =
    [
        "Hardware 1",
        "Hardware 2",
        "Hardware 3",
        "VAIO",
        "AUX"
    ];

    private static readonly string[] BananaBuses =
    [
        "A1",
        "A2",
        "A3",
        "B1",
        "B2"
    ];

    // Potato (type 3): 8 strips, 8 buses
    private static readonly string[] PotatoStrips =
    [
        "Hardware 1",
        "Hardware 2",
        "Hardware 3",
        "Hardware 4",
        "Hardware 5",
        "VAIO",
        "AUX",
        "VAIO 3"
    ];

    private static readonly string[] PotatoBuses =
    [
        "A1",
        "A2",
        "A3",
        "A4",
        "A5",
        "B1",
        "B2",
        "B3"
    ];

    public static string GetRoleName(string edition, string kind, int index)
    {
        var names = ResolveNames(edition, kind);
        if (index >= 0 && index < names.Length)
        {
            return names[index];
        }

        return FallbackRoleName(kind, index);
    }

    public static string GetIndexCaption(string kind, int index)
    {
        if (kind.Equals("Strip", StringComparison.OrdinalIgnoreCase))
        {
            return $"Strip {index}";
        }

        if (kind.Equals("Bus", StringComparison.OrdinalIgnoreCase))
        {
            return $"Bus {index}";
        }

        return $"{kind} {index}";
    }

    /// <summary>
    /// Primary line: user label if set, otherwise the Voicemeeter role name (e.g. A1).
    /// Secondary line: role + index (+ optional device), e.g. "A1 · Bus 0".
    /// </summary>
    public static (string Title, string Detail) FormatDisplay(
        string edition,
        string kind,
        int index,
        string? customLabel,
        string? deviceName)
    {
        var roleName = GetRoleName(edition, kind, index);
        var indexCaption = GetIndexCaption(kind, index);
        var hasCustomLabel = !string.IsNullOrWhiteSpace(customLabel)
            && !string.Equals(customLabel.Trim(), roleName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(customLabel.Trim(), indexCaption, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(customLabel.Trim(), $"{kind} {index}", StringComparison.OrdinalIgnoreCase);

        var title = hasCustomLabel ? customLabel!.Trim() : roleName;

        var detailParts = new List<string>();
        if (hasCustomLabel)
        {
            detailParts.Add(roleName);
        }

        detailParts.Add(indexCaption);

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            detailParts.Add(deviceName.Trim());
        }

        return (title, string.Join(" · ", detailParts));
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

        // Basic / "Voicemeeter"
        if (isStrip)
        {
            return BasicStrips;
        }

        if (isBus)
        {
            return BasicBuses;
        }

        return [];
    }

    private static string FallbackRoleName(string kind, int index)
    {
        if (kind.Equals("Bus", StringComparison.OrdinalIgnoreCase))
        {
            // Generic A/B fallback when edition is unknown: A1..A5 then B1..
            return index < 5 ? $"A{index + 1}" : $"B{index - 4}";
        }

        if (kind.Equals("Strip", StringComparison.OrdinalIgnoreCase))
        {
            return $"Strip {index + 1}";
        }

        return $"{kind} {index}";
    }
}
