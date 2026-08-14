namespace VMWV.Core.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken);
}

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version LatestVersion,
    Uri ReleasePage);

public static class ReleaseVersionParser
{
    public static bool TryParseTag(string? tag, out Version version)
    {
        var normalized = tag?.Trim().TrimStart('v', 'V');
        var separator = normalized?.IndexOfAny(['-', '+']) ?? -1;
        if (separator >= 0)
        {
            normalized = normalized![..separator];
        }

        return Version.TryParse(normalized, out version!);
    }
}
