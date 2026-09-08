using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

const string appName = "Voicemeeter Windows Volume Modern";
const string appExeName = "VMWV.App.exe";
const string payloadResourceName = "VoicemeeterWindowsVolumeModern.Payload.zip";
const string version = "1.3.0";

try
{
    using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(payloadResourceName)
        ?? throw new InvalidOperationException("Portable payload is missing.");
    var payloadId = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    payload.Position = 0;
    var root = Path.Combine(Path.GetTempPath(), "VoicemeeterWindowsVolumeModernPortable", version);
    var directory = Path.Combine(root, payloadId);
    using var extractionLock = new Mutex(false, $@"Local\VMWV.Payload.{payloadId}");
    var ownsLock = false;
    try
    {
        try { ownsLock = extractionLock.WaitOne(TimeSpan.FromMinutes(2)); }
        catch (AbandonedMutexException) { ownsLock = true; }
        if (!ownsLock) throw new TimeoutException("Another portable launch is still extracting the application.");
        bool IsComplete(string path) => File.Exists(Path.Combine(path, appExeName))
            && File.Exists(Path.Combine(path, ".complete"));
        if (!IsComplete(directory) && Directory.Exists(root))
        {
            directory = Directory.EnumerateDirectories(root, $"{payloadId}.recovered-*")
                .FirstOrDefault(IsComplete) ?? directory;
        }
        if (!IsComplete(directory))
        {
            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, $"{payloadId}.staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
                archive.ExtractToDirectory(staging);
                if (!File.Exists(Path.Combine(staging, appExeName)))
                    throw new InvalidDataException("The portable payload does not contain the application.");
                File.WriteAllText(Path.Combine(staging, ".complete"), payloadId);
                // Published caches are immutable: never delete files another launch might be using.
                if (Directory.Exists(directory))
                    directory = Path.Combine(root, $"{payloadId}.recovered-{Guid.NewGuid():N}");
                Directory.Move(staging, directory);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
    }
    finally { if (ownsLock) extractionLock.ReleaseMutex(); }

    var startInfo = new ProcessStartInfo
    {
        FileName = Path.Combine(directory, appExeName),
        WorkingDirectory = directory,
        UseShellExecute = false
    };
    foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
        startInfo.ArgumentList.Add(argument);
    if (Environment.ProcessPath is { Length: > 0 } launcher)
        startInfo.EnvironmentVariables["VMWV_PORTABLE_LAUNCHER"] = launcher;
    Process.Start(startInfo);
}
catch (Exception ex)
{
    _ = MessageBoxW(nint.Zero, $"Unable to start {appName}.{Environment.NewLine}{ex.Message}", appName, 0x10);
    Environment.ExitCode = 1;
}

[DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);
