using System.Net;
using System.Globalization;
using Microsoft.Win32;
using VMWV.Core.Services;
using VMWV.Infrastructure.Windows.Voicemeeter;
using VMWV.Infrastructure.Windows.Updates;
using VMWV.Infrastructure.Windows.Audio;
using VMWV.Infrastructure.Windows.Startup;
using VMWV.Infrastructure.Windows.Globalization;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("regional formats follow Windows rather than the app language", () =>
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using var regionalSettings = Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
        var originalCulture = CultureInfo.CurrentCulture;
        var originalLanguage = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var language in new[] { "en-US", "pt-BR", "it-IT", "zh-CN" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                WindowsRegionalFormats.Refresh();
                var formats = WindowsRegionalFormats.Culture;
                Equal((string)regionalSettings!.GetValue("LocaleName")!, formats.Name);
                Equal(true, formats.UseUserOverride);
                Equal(true, formats.IsReadOnly);
                Equal((string)regionalSettings.GetValue("sShortDate")!, formats.DateTimeFormat.ShortDatePattern);
                Equal((string)regionalSettings.GetValue("sShortTime")!, formats.DateTimeFormat.ShortTimePattern);
                Equal((string)regionalSettings.GetValue("sTimeFormat")!, formats.DateTimeFormat.LongTimePattern);
                var expected = new CultureInfo(formats.Name, useUserOverride: true);
                var date = new DateTime(2026, 9, 8, 17, 43, 21);
                Equal(date.ToString("g", expected), date.ToString("g", formats));
                Equal(date.ToString("T", expected), date.ToString("T", formats));
                var timestamp = new DateTimeOffset(date);
                Equal(date.ToString("g", expected), WindowsRegionalFormats.FormatDateTime(timestamp));
                Equal(date.ToString("G", expected), WindowsRegionalFormats.FormatDateTime(timestamp, includeSeconds: true));
                Equal(date.ToString("T", expected), WindowsRegionalFormats.FormatTime(timestamp));
                Equal(date.ToString("t", expected), WindowsRegionalFormats.FormatTime(timestamp, includeSeconds: false));
                Equal(language, CultureInfo.CurrentUICulture.Name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalLanguage;
        }
        return Task.CompletedTask;
    }),
    ("displayed timestamps convert offsets to local time before regional formatting", () =>
    {
        var timestamp = new DateTimeOffset(2026, 9, 8, 0, 10, 42, TimeSpan.FromHours(14));
        var local = timestamp.ToLocalTime();
        Equal(WindowsRegionalFormats.FormatDateTime(local), WindowsRegionalFormats.FormatDateTime(timestamp));
        Equal(WindowsRegionalFormats.FormatTime(local), WindowsRegionalFormats.FormatTime(timestamp));
        Equal(local.ToString("d", WindowsRegionalFormats.Culture) + " " + local.ToString("t", WindowsRegionalFormats.Culture),
            WindowsRegionalFormats.FormatDateTime(timestamp));
        return Task.CompletedTask;
    }),
    ("existing diagnostic entries can refresh their regional date and time text", () =>
    {
        var timestamp = new DateTimeOffset(2026, 9, 8, 17, 43, 21, TimeSpan.Zero);
        var entry = new VMWV_App.Models.DiagnosticLogEntry(timestamp, "Audio", "Test event");
        var changed = new List<string?>();
        entry.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        entry.RefreshTimeText();
        Equal(2, changed.Count);
        Equal(nameof(entry.TimeText), changed[0]);
        Equal(nameof(entry.DateTimeText), changed[1]);
        Equal(WindowsRegionalFormats.FormatTime(timestamp), entry.TimeText);
        Equal(WindowsRegionalFormats.FormatDateTime(timestamp, includeSeconds: true), entry.DateTimeText);
        return Task.CompletedTask;
    }),
    ("stable instance activation redirects concurrent callers and releases ownership on exit", async () =>
    {
        var name = $"VMWV.Test.{Guid.NewGuid():N}";
        await using var first = new InstanceActivationService(name);
        Equal(true, first.IsPrimary);
        var requests = 0;
        first.Start(() => { Interlocked.Increment(ref requests); return Task.CompletedTask; });
        await using (var second = new InstanceActivationService(name))
        {
            Equal(false, second.IsPrimary);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => second.RedirectAsync(CancellationToken.None)));
            Equal(8, requests);
        }
        await first.DisposeAsync();
        await first.DisposeAsync();
        await using var replacement = new InstanceActivationService(name);
        Equal(true, replacement.IsPrimary);
    }),
    ("failed window activation does not stop later activation requests", async () =>
    {
        var name = $"VMWV.Test.{Guid.NewGuid():N}";
        await using var first = new InstanceActivationService(name);
        await using var second = new InstanceActivationService(name);
        var attempts = 0;
        var failures = 0;
        first.Failed += (_, _) => Interlocked.Increment(ref failures);
        first.Start(() => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new InvalidOperationException("Window activation failed."))
            : Task.CompletedTask);
        await ExpectFailure(() => second.RedirectAsync(CancellationToken.None));
        await second.RedirectAsync(CancellationToken.None);
        Equal(2, attempts);
        Equal(1, failures);
    }),
    ("native registration survives unavailable engine and recovery", async () =>
    {
        var library = new FakeLibrary { DirtyResult = -1 };
        await using (var client = new VoicemeeterRemoteClient(library))
        {
            await ExpectFailure(() => client.ConnectAsync(CancellationToken.None));
            Equal(1, library.Logins);
            Equal(0, library.Logouts);
            library.DirtyResult = 1;
            await client.ConnectAsync(CancellationToken.None);
            Equal("Voicemeeter Banana", client.Edition);
            Equal(VoicemeeterConnectionState.Connected, client.State);
            Equal(1, library.Logins);
            library.DirtyResult = -1;
            await ExpectFailure(() => client.RefreshAsync(CancellationToken.None));
            Equal(VoicemeeterConnectionState.Error, client.State);
            library.DirtyResult = 0;
            library.Type = 3;
            await client.ConnectAsync(CancellationToken.None);
            Equal("Voicemeeter Potato", client.Edition);
            Equal(1, library.Logins);
        }
        Equal(1, library.Logouts);
    }),
    ("manual disconnect pauses commands without duplicate login", async () =>
    {
        var library = new FakeLibrary();
        await using var client = new VoicemeeterRemoteClient(library);
        await client.ConnectAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);
        await ExpectFailure(() => client.SetMuteAsync(new VoicemeeterBindingTarget("Bus_0", "Bus", 0, "A1", null, true), true, CancellationToken.None));
        Equal(0, library.Writes);
        await client.ConnectAsync(CancellationToken.None);
        Equal(1, library.Logins);
    }),
    ("standard banana and potato have exact target counts", async () =>
    {
        foreach (var (type, count) in new[] { (1, 5), (2, 10), (3, 16) })
        {
            await using var client = new VoicemeeterRemoteClient(new FakeLibrary { Type = type });
            await client.ConnectAsync(CancellationToken.None);
            var targets = await client.GetBindingTargetsAsync(CancellationToken.None);
            Equal(count, targets.Count);
        }
    }),
    ("disposed native client cannot register again", async () =>
    {
        var library = new FakeLibrary();
        var client = new VoicemeeterRemoteClient(library);
        await client.ConnectAsync(CancellationToken.None);
        await client.DisposeAsync();
        await client.DisposeAsync();
        await ExpectFailure(() => client.ConnectAsync(CancellationToken.None));
        Equal(1, library.Logins);
        Equal(1, library.Logouts);
    }),
    ("fractional gain limits are not rounded up again by the native writer", async () =>
    {
        var library = new FakeLibrary();
        await using var client = new VoicemeeterRemoteClient(library);
        await client.ConnectAsync(CancellationToken.None);
        var target = new VoicemeeterBindingTarget("Bus_0", "Bus", 0, "A1", null, true);
        await client.SetGainAsync(target, -12.04, CancellationToken.None);
        Equal("Bus[0].Gain = -12.04;", library.LastScript);
        await ExpectFailure(() => client.SetGainAsync(target, double.NaN, CancellationToken.None));
        Equal(1, library.Writes);
    }),
    ("unknown edition is not connected", async () =>
    {
        await using var client = new VoicemeeterRemoteClient(new FakeLibrary { Type = 0 });
        await ExpectFailure(() => client.ConnectAsync(CancellationToken.None));
        Equal(VoicemeeterConnectionState.Error, client.State);
    }),
    ("HTTP release parsing compares versions and rejects unsafe links", async () =>
    {
        using var http = new HttpClient(new FakeHttp(HttpStatusCode.OK, """{"tag_name":"v1.3.0","html_url":"http://example.com/download"}"""));
        var result = await new GitHubReleaseUpdateService(http).CheckAsync(new Version(1, 2, 1), CancellationToken.None);
        Equal(true, result.IsUpdateAvailable);
        Equal("github.com", result.ReleasePage.Host);
        Equal("https", result.ReleasePage.Scheme);
    }),
    ("HTTP failures and invalid release payloads are not up-to-date results", async () =>
    {
        foreach (var (code, json) in new[] { (HttpStatusCode.Forbidden, "{}"), (HttpStatusCode.OK, """{"tag_name":"invalid"}""") })
        {
            using var http = new HttpClient(new FakeHttp(code, json));
            await ExpectFailure(() => new GitHubReleaseUpdateService(http).CheckAsync(new Version(1, 2, 1), CancellationToken.None));
        }
    }),
    ("cancelled HTTP requests exit promptly", async () =>
    {
        using var http = new HttpClient(new FakeHttp(HttpStatusCode.OK, "{}", 10000));
        using var cancellation = new CancellationTokenSource(30);
        await ExpectFailure(() => new GitHubReleaseUpdateService(http).CheckAsync(new Version(1, 2, 1), cancellation.Token));
    })
};

if (args.Contains("--live-read-only"))
{
    tests.Add(("real Windows endpoint can be refreshed concurrently and stopped", async () =>
    {
        var audio = new WindowsAudioEndpointService();
        await audio.StartAsync(CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => audio.RefreshAsync(CancellationToken.None))));
        Equal(false, string.IsNullOrWhiteSpace(audio.Current.DeviceId));
        await audio.DisposeAsync();
        await ExpectFailure(() => audio.RefreshAsync(CancellationToken.None));
    }));
    tests.Add(("STA caller can start audio and refresh from MTA", async () =>
    {
        WindowsAudioEndpointService? audio = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                audio = new WindowsAudioEndpointService();
                audio.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await audio!.RefreshAsync(CancellationToken.None);
            Equal(false, string.IsNullOrEmpty(audio.Current.DeviceId));
        }
        finally { if (audio is not null) await audio.DisposeAsync(); }
    }));
    tests.Add(("real Voicemeeter connects and reads edition and targets without changing audio", async () =>
    {
        await using var client = new VoicemeeterRemoteClient();
        await client.ConnectAsync(CancellationToken.None);
        await client.RefreshAsync(CancellationToken.None);
        Equal(false, client.Edition == "Unknown");
        var targets = await client.GetBindingTargetsAsync(CancellationToken.None);
        Console.WriteLine($"  Read {client.Edition}, {targets.Count} targets.");
        Equal(true, targets.Count > 0);
    }));
}

var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"Tests: {tests.Count}, failures: {failed}");
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}
static async Task ExpectFailure(Func<Task> action)
{
    try { await action(); } catch (Exception) { return; }
    throw new Exception("Expected failure");
}

sealed class FakeLibrary : IVoicemeeterRemoteLibrary
{
    public int DirtyResult;
    public int Type = 2;
    public int Logins;
    public int Logouts;
    public int Writes;
    public string? LastScript;
    public void Load() { }
    public int Login() { Logins++; return 0; }
    public int Logout() { Logouts++; return 0; }
    public int IsParametersDirty() => DirtyResult;
    public int GetVoicemeeterType() => Type;
    public void SetParameterFloat(string name, float value) { Writes++; }
    public string GetParameterString(string name) => "";
    public void SetParameters(string script) { Writes++; LastScript = script; }
    public void Dispose() { }
}

sealed class FakeHttp(HttpStatusCode status, string json, int delay = 0) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (delay > 0) await Task.Delay(delay, cancellationToken);
        return new HttpResponseMessage(status) { Content = new StringContent(json) };
    }
}
