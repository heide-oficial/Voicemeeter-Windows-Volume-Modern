using System.Text.Json;
using VMWV.Core.Services;
using VMWV.Core.Settings;
using VMWV.Core.Volume;

internal static class RegressionTests
{
    public static IEnumerable<(string Name, Action Test)> SyncTests()
    {
        yield return ("incomplete update caches do not become available or crash the UI", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "cache.json");
                File.WriteAllText(path, """{"Status":3,"Result":null}""");
                var updates = new UpdateCoordinator(new FakeUpdates(), new Version(1, 2, 1), path);
                Equal(UpdateStatus.NotChecked, updates.State.Status);
                updates.DisposeAsync().AsTask().GetAwaiter().GetResult();
            });
        });
        yield return ("zero cap preserves negative maximum", () =>
        {
            foreach (var linear in new[] { true, false })
                Equal(-12d, VolumeMapper.ToVoicemeeterGain(100, -60, -12, true, linear));
        });
        yield return ("all mapped values are bounded and monotonic", () =>
        {
            foreach (var range in new[] { (-60d, 12d), (-60d, -12d), (-20d, -20d), (5d, 12d), (-12.04, -12.04), (-12.06, -12.04), (-60d, -12.04) })
            foreach (var cap in new[] { true, false })
            foreach (var linear in new[] { true, false })
            {
                var maximum = cap ? Math.Min(0, range.Item2) : range.Item2;
                var minimum = Math.Min(range.Item1, maximum);
                var previous = minimum;
                for (var volume = 0; volume <= 100; volume++)
                {
                    var gain = VolumeMapper.ToVoicemeeterGain(volume, range.Item1, range.Item2, cap, linear);
                    Check(gain >= minimum && gain <= maximum && gain >= previous, $"Unsafe gain: {gain}");
                    previous = gain;
                }
            }
        });
        yield return ("invalid gain configurations never produce commands", () =>
        {
            foreach (var range in new[] { (12d, -60d), (double.NaN, 0d), (-60d, double.PositiveInfinity), (-61d, 0d), (-60d, 13d) })
            {
                Check(!VolumeMapper.IsValidRange(range.Item1, range.Item2));
                Throws<ArgumentOutOfRangeException>(() => VolumeMapper.ToVoicemeeterGain(0, range.Item1, range.Item2, true, true));
            }
        });
        yield return ("null toggle entries are ignored during normalization", () =>
        {
            var settings = JsonSerializer.Deserialize<AppSettings>("""{"toggles":[null,{"setting":"Bus_0","value":true}]}""")!;
            settings.Normalize();
            Equal(1, settings.Toggles.Count);
            Check(settings.IsToggleEnabled("Bus_0"));
        });
        yield return ("transient file locks preserve settings without corruption backups", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, """{"gain_max":-12}""");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var store = new JsonSettingsStore(path);
                    store.LoadOrCreate();
                    Check(store.LoadError is IOException);
                    Throws<IOException>(() => store.Save(new AppSettings()));
                    Equal(0, Directory.GetFiles(directory, "*.corrupt-*").Length);
                }
                Check(File.ReadAllText(path).Contains("-12"));
            });
        });
        yield return ("settings can be read again after a transient lock clears", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, """{"gain_max":-12}""");
                var store = new JsonSettingsStore(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    store.LoadOrCreate();
                Check(store.LoadError is not null);
                var settings = store.LoadOrCreate();
                Check(store.LoadError is null);
                Equal(-12d, settings.GainMax);
                store.Save(settings);
            });
        });
        yield return ("duplicate resumes are ignored but distinct suspends are not", () =>
        {
            var clock = new Clock();
            var gate = new ResumeGate(clock);
            gate.Suspend();
            Check(gate.TryResume());
            Check(!gate.TryResume());
            gate.Suspend();
            Check(gate.TryResume());
            clock.Advance(TimeSpan.FromSeconds(10));
            Check(gate.TryResume());
        });
        yield return ("missing automatic update preference defaults on", () =>
            Check(JsonSerializer.Deserialize<AppSettings>("{}")!.CheckUpdatesAutomatically));
    }

    public static IEnumerable<(string Name, Func<Task> Test)> AsyncTests()
    {
        yield return ("500 rapid saves preserve the newest snapshot on shutdown", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var path = Path.Combine(directory, "settings.json");
                var store = new JsonSettingsStore(path);
                var writer = new SettingsWriter(store);
                for (var index = 0; index < 500; index++)
                    _ = writer.QueueAsync(store.CreateSavePayload(new AppSettings { InitialVolume = index % 101 }), index % 7 == 0);
                await writer.DisposeAsync();
                Equal(499 % 101, store.LoadOrCreate().InitialVolume);
                Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
            });
        });
        yield return ("direct async store writes never share a temporary file", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
                await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
                    store.SavePayloadAsync(store.CreateSavePayload(new AppSettings { InitialVolume = index }), CancellationToken.None)));
                Check(store.LoadOrCreate().InitialVolume is >= 0 and <= 99);
                Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
            });
        });
        yield return ("save failures are observable and do not prevent shutdown", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var file = Path.Combine(directory, "blocked");
                File.WriteAllText(file, "not a directory");
                var writer = new SettingsWriter(new JsonSettingsStore(Path.Combine(file, "settings.json")));
                var failures = 0;
                writer.SaveFailed += (_, _) => failures++;
                await writer.QueueAsync("{}", true);
                await writer.DisposeAsync();
                Equal(1, failures);
            });
        });
        yield return ("audio initialization retries automatically after failure", async () =>
        {
            var audio = new FakeAudio { FailStarts = 1 };
            await using var monitor = new AudioMonitor(audio, () => 100);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.SnapshotChanged += (_, snapshot) => { if (snapshot.DeviceId.Length > 0) ready.TrySetResult(); };
            monitor.Start();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(audio.StartCalls >= 2);
        });
        yield return ("fallback detects a missed default device change", async () =>
        {
            var audio = new FakeAudio();
            await using var monitor = new AudioMonitor(audio, () => 100);
            var ids = new List<string>();
            monitor.SnapshotChanged += (_, snapshot) => ids.Add(snapshot.DeviceId);
            await monitor.RefreshAsync(CancellationToken.None);
            audio.Current = audio.Current with { DeviceId = "headphones" };
            await monitor.RefreshAsync(CancellationToken.None);
            Check(ids.SequenceEqual(["speakers", "headphones"]));
        });
        yield return ("connection monitor recovers and emits targets without toggling", async () =>
        {
            var client = new FakeVoicemeeter { Available = false };
            await using var monitor = new ConnectionMonitor(client);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.Ready += (_, targets) => { Equal(1, targets.Count); ready.TrySetResult(); };
            monitor.Start();
            client.Available = true;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Check(client.ConnectCalls >= 1);
        });
        yield return ("manual disconnection survives refresh and background checks", async () =>
        {
            var client = new FakeVoicemeeter();
            await using var monitor = new ConnectionMonitor(client);
            await monitor.ConnectAsync(CancellationToken.None);
            await monitor.DisconnectAsync(CancellationToken.None);
            var count = client.ConnectCalls;
            await monitor.RefreshAsync(CancellationToken.None);
            Equal(count, client.ConnectCalls);
            Equal(VoicemeeterConnectionState.Disconnected, client.State);
        });
        yield return ("connection health counts failures and clears them after recovery", async () =>
        {
            var client = new FakeVoicemeeter { Available = false };
            await using var monitor = new ConnectionMonitor(client);
            var changes = new List<MonitorHealth>();
            monitor.HealthChanged += (_, health) => changes.Add(health);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try { await monitor.ConnectAsync(CancellationToken.None); }
                catch (IOException) { }
                Equal(MonitorStatus.Retrying, monitor.Health.Status);
                Equal(attempt, monitor.Health.FailedAttempts);
                Check(!string.IsNullOrWhiteSpace(monitor.Health.Error));
            }
            client.Available = true;
            await monitor.ConnectAsync(CancellationToken.None);
            Equal(new MonitorHealth(MonitorStatus.Ready), monitor.Health);
            var count = changes.Count;
            await monitor.RefreshAsync(CancellationToken.None);
            Equal(count, changes.Count);
            Check(changes.Any(health => health.Status == MonitorStatus.Retrying));
        });
        yield return ("manual pause clears active connection failures without restarting retries", async () =>
        {
            var client = new FakeVoicemeeter { Available = false };
            await using var monitor = new ConnectionMonitor(client);
            try { await monitor.ConnectAsync(CancellationToken.None); }
            catch (IOException) { }
            await monitor.DisconnectAsync(CancellationToken.None);
            Equal(new MonitorHealth(MonitorStatus.Paused), monitor.Health);
            var calls = client.ConnectCalls;
            await monitor.RefreshAsync(CancellationToken.None);
            Equal(calls, client.ConnectCalls);
            Equal(MonitorStatus.Paused, monitor.Health.Status);
            client.Available = true;
            await monitor.ConnectAsync(CancellationToken.None);
            Equal(new MonitorHealth(MonitorStatus.Ready), monitor.Health);
        });
        yield return ("audio health recovers even when the endpoint snapshot is unchanged", async () =>
        {
            var audio = new FakeAudio();
            await using var monitor = new AudioMonitor(audio, () => 100);
            var snapshots = 0;
            var healthChanges = 0;
            monitor.SnapshotChanged += (_, _) => snapshots++;
            monitor.HealthChanged += (_, _) => healthChanges++;
            await monitor.RefreshAsync(CancellationToken.None);
            audio.FailStarts = audio.StartCalls + 1;
            try { await monitor.RefreshAsync(CancellationToken.None); }
            catch (IOException) { }
            Equal(MonitorStatus.Retrying, monitor.Health.Status);
            Equal(1, monitor.Health.FailedAttempts);
            Check(monitor.Health.Error is not null);
            await monitor.RefreshAsync(CancellationToken.None);
            Equal(new MonitorHealth(MonitorStatus.Ready), monitor.Health);
            Equal(1, snapshots);
            Equal(3, healthChanges);
            await monitor.RefreshAsync(CancellationToken.None);
            Equal(3, healthChanges);
        });
        yield return ("cancelled monitor refreshes do not create false diagnostic failures", async () =>
        {
            await using var audio = new AudioMonitor(new FakeAudio(), () => 100);
            await using var connection = new ConnectionMonitor(new FakeVoicemeeter());
            await audio.RefreshAsync(CancellationToken.None);
            await connection.ConnectAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await audio.RefreshAsync(cancellation.Token); }
            catch (OperationCanceledException) { }
            try { await connection.RefreshAsync(cancellation.Token); }
            catch (OperationCanceledException) { }
            Equal(new MonitorHealth(MonitorStatus.Ready), audio.Health);
            Equal(new MonitorHealth(MonitorStatus.Ready), connection.Health);
        });
        yield return ("work group drains existing work and rejects new work", async () =>
        {
            var work = new AsyncWorkGroup();
            var completed = 0;
            work.Run(async () => { await Task.Delay(30); completed++; });
            await work.StopAsync();
            work.Run(() => { completed++; return Task.CompletedTask; });
            Equal(1, completed);
        });
        yield return ("disabled updates make no request but manual checks work", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var service = new FakeUpdates();
                await using var coordinator = new UpdateCoordinator(service, new Version(1, 2, 1), Path.Combine(directory, "cache.json"));
                coordinator.Start(false);
                await coordinator.CheckAsync(false);
                Equal(0, service.Calls);
                await coordinator.CheckAsync(true);
                Equal(1, service.Calls);
                Equal(UpdateStatus.Available, coordinator.State.Status);
            });
        });
        yield return ("daily update cache survives restart and manual checks bypass it", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var service = new FakeUpdates();
                var clock = new Clock();
                var path = Path.Combine(directory, "cache.json");
                await using (var first = new UpdateCoordinator(service, new Version(1, 2, 1), path, clock))
                    await first.CheckAsync(true);
                await using var second = new UpdateCoordinator(service, new Version(1, 2, 1), path, clock);
                second.Start(true);
                await second.CheckAsync(false);
                Equal(1, service.Calls);
                clock.Advance(TimeSpan.FromHours(24));
                await second.CheckAsync(false);
                Equal(2, service.Calls);
                await second.CheckAsync(true);
                Equal(3, service.Calls);
            });
        });
        yield return ("simultaneous update checks share a request and network errors remain errors", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var service = new FakeUpdates { Delay = 50, Fail = true };
                await using var coordinator = new UpdateCoordinator(service, new Version(1, 2, 1), Path.Combine(directory, "cache.json"));
                await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => coordinator.CheckAsync(true)));
                Equal(1, service.Calls);
                Equal(UpdateStatus.Failed, coordinator.State.Status);
                Check(coordinator.State.LastSuccess is null);
            });
        });
        yield return ("interrupted update cache is not stuck checking and remains rate limited", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var clock = new Clock();
                var path = Path.Combine(directory, "updates.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new UpdateState(UpdateStatus.Checking, clock.GetUtcNow())));
                var service = new FakeUpdates();
                await using var updates = new UpdateCoordinator(service, new Version(1, 2, 1), path, clock);
                Equal(UpdateStatus.Failed, updates.State.Status);
                updates.SetAutomatic(true);
                await updates.CheckAsync(false);
                Equal(0, service.Calls);
                await updates.CheckAsync(true);
                Equal(1, service.Calls);
            });
        });
        yield return ("log writer drains batches and expires old logs", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var old = Path.Combine(directory, "old.log");
                File.WriteAllText(old, "expired");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
                var writer = new DiagnosticLogWriter(directory);
                for (var i = 0; i < 250; i++)
                    writer.Enqueue(new(DateTimeOffset.Now, "volume.changed", "音频", $"value {i}"));
                await writer.DisposeAsync();
                Check(!File.Exists(old));
                Equal(250, File.ReadAllLines(Directory.GetFiles(directory, "*.log").Single()).Length);
            });
        });
        yield return ("log storage errors are contained", async () =>
        {
            await WithDirectoryAsync(async directory =>
            {
                var path = Path.Combine(directory, "file");
                File.WriteAllText(path, "blocked");
                var writer = new DiagnosticLogWriter(path);
                writer.Enqueue(new(DateTimeOffset.Now, "error", "Runtime", "test"));
                await writer.DisposeAsync();
                Check(writer.LastError is IOException);
            });
        });
    }

    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static void WithDirectory(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vmwv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { test(directory); } finally { Directory.Delete(directory, true); }
    }
    private static async Task WithDirectoryAsync(Func<string, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vmwv-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try { await test(directory); } finally { Directory.Delete(directory, true); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public int Calls;
        public int Delay;
        public bool Fail;
        public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken token)
        {
            Calls++;
            if (Delay > 0) await Task.Delay(Delay, token);
            if (Fail) throw new HttpRequestException("Simulated network failure");
            return new(true, new Version(2, 0, 0), new Uri("https://github.com/heide-oficial/Voicemeeter-Windows-Volume-Modern/releases/latest"));
        }
    }

    private sealed class FakeAudio : IAudioEndpointService
    {
        public int StartCalls;
        public int FailStarts;
        public AudioEndpointSnapshot Current { get; set; } = new("speakers", "Speakers", 35, false);
        public event EventHandler<AudioVolumeChangedEventArgs>? VolumeChanged { add { } remove { } }
        public event EventHandler<AudioMuteChangedEventArgs>? MuteChanged { add { } remove { } }
        public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged { add { } remove { } }
        public Task StartAsync(CancellationToken token)
        {
            StartCalls++;
            if (StartCalls <= FailStarts) throw new IOException("Audio not ready");
            return Task.CompletedTask;
        }
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken token) => Task.CompletedTask;
        public Task SetMuteAsync(bool mute, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVoicemeeter : IVoicemeeterClient
    {
        public bool Available = true;
        public int ConnectCalls;
        public VoicemeeterConnectionState State { get; private set; }
        public string Edition => State == VoicemeeterConnectionState.Connected ? "Voicemeeter Banana" : "Unknown";
        public event EventHandler? ParametersChanged { add { } remove { } }
        public event EventHandler<VoicemeeterConnectionStateChangedEventArgs>? ConnectionStateChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken token)
        {
            ConnectCalls++;
            if (!Available) throw new IOException("Engine not ready");
            State = VoicemeeterConnectionState.Connected;
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken token) { State = VoicemeeterConnectionState.Disconnected; return Task.CompletedTask; }
        public Task RefreshAsync(CancellationToken token) => Available ? Task.CompletedTask : throw new IOException();
        public Task<IReadOnlyList<VoicemeeterBindingTarget>> GetBindingTargetsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<VoicemeeterBindingTarget>>([new("Strip_0", "Strip", 0, "Mic", null, true)]);
        public Task SetGainAsync(VoicemeeterBindingTarget target, double gain, CancellationToken token) => Task.CompletedTask;
        public Task SetGainAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, double gain, CancellationToken token) => Task.CompletedTask;
        public Task SetMuteAsync(VoicemeeterBindingTarget target, bool mute, CancellationToken token) => Task.CompletedTask;
        public Task SetMuteAsync(IReadOnlyList<VoicemeeterBindingTarget> targets, bool mute, CancellationToken token) => Task.CompletedTask;
        public Task RestartAudioEngineAsync(CancellationToken token) => Task.CompletedTask;
        public Task ShowAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
