using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

const string appName = "Voicemeeter Windows Volume Modern";
const string appExeName = "VMWV.App.exe";
const string payloadResourceName = "VoicemeeterWindowsVolumeModern.Payload.zip";
const string version = "1.1.0";

try
{
    var appDirectory = PortableDirectory();
    var markerPath = Path.Combine(appDirectory, ".payload-version");
    var appPath = Path.Combine(appDirectory, appExeName);
    var payloadVersion = PortablePayloadVersion();

    if (!File.Exists(appPath) || !File.Exists(markerPath) || File.ReadAllText(markerPath) != payloadVersion)
    {
        if (Directory.Exists(appDirectory))
        {
            Directory.Delete(appDirectory, recursive: true);
        }

        Directory.CreateDirectory(appDirectory);
        ExtractPayload(appDirectory);
        File.WriteAllText(markerPath, payloadVersion);
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = appPath,
        WorkingDirectory = appDirectory,
        UseShellExecute = false
    };

    if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
    {
        startInfo.EnvironmentVariables["VMWV_PORTABLE_LAUNCHER"] = Environment.ProcessPath;
    }

    Process.Start(startInfo);
}
catch (Exception ex)
{
    ShowMessage(appName, $"Unable to start {appName}.{Environment.NewLine}{Environment.NewLine}{ex.Message}", 0x00000010);
}

static string PortableDirectory() =>
    Path.Combine(Path.GetTempPath(), "VoicemeeterWindowsVolumeModernPortable", version);

static string PortablePayloadVersion()
{
    var processPath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
    {
        return version;
    }

    var file = new FileInfo(processPath);
    return $"{version}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
}

static void ExtractPayload(string destination)
{
    using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(payloadResourceName)
        ?? throw new InvalidOperationException("Portable payload is missing. Rebuild the portable executable with PayloadZipPath.");
    using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
    archive.ExtractToDirectory(destination, overwriteFiles: true);
}

static void ShowMessage(string title, string message, uint type)
{
    _ = MessageBoxW(nint.Zero, message, title, type);
}

[DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);
