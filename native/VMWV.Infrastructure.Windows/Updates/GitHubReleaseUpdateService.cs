using System.Net.Http.Headers;
using System.Text.Json;
using VMWV.Core;
using VMWV.Core.Services;

namespace VMWV.Infrastructure.Windows.Updates;

public sealed class GitHubReleaseUpdateService : IUpdateService
{
    private static readonly Uri LatestReleaseApi = new(
        "https://api.github.com/repos/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases/latest");
    private static readonly Uri LatestReleasePage = new(
        "https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases/latest");
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            LatestReleaseApi,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (!ReleaseVersionParser.TryParseTag(tag, out var latestVersion))
        {
            throw new InvalidDataException($"The GitHub release tag '{tag}' is not a valid version.");
        }

        var releasePage = root.TryGetProperty("html_url", out var htmlUrl)
            && Uri.TryCreate(htmlUrl.GetString(), UriKind.Absolute, out var parsedUri)
                ? parsedUri
                : LatestReleasePage;
        return new UpdateCheckResult(latestVersion > currentVersion, latestVersion, releasePage);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Voicemeeter-Windows-Volume-Modern/{AppInfo.VersionText}");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
