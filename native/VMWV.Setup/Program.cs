using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

const string appName = "Voicemeeter Windows Volume Modern";
const string appExeName = "VMWV.App.exe";
const string payloadResourceName = "VoicemeeterWindowsVolumeModern.Payload.zip";
const string publisher = "Matheus Heidemann";
const string version = "1.0.0";

var quiet = args.Any(arg => arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
if (args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
{
    Uninstall(quiet);
    return;
}

Install(quiet);

static void Install(bool quiet)
{
    var installDirectory = InstallDirectory();
    if (Directory.Exists(installDirectory))
    {
        Directory.Delete(installDirectory, recursive: true);
    }

    Directory.CreateDirectory(installDirectory);

    var appPath = Path.Combine(installDirectory, appExeName);
    ExtractPayload(installDirectory);

    var uninstallerPath = Path.Combine(installDirectory, "Uninstall.exe");
    var currentProcessPath = Environment.ProcessPath;
    if (!string.IsNullOrWhiteSpace(currentProcessPath))
    {
        File.Copy(currentProcessPath, uninstallerPath, overwrite: true);
    }

    CreateShortcut(StartMenuShortcutPath(), appPath, appName, installDirectory);
    RegisterUninstaller(installDirectory, appPath, uninstallerPath);

    if (!quiet)
    {
        ShowMessage(appName, "Voicemeeter Windows Volume Modern was installed successfully.");
    }
}

static void ExtractPayload(string installDirectory)
{
    using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(payloadResourceName)
        ?? throw new InvalidOperationException("Installer payload is missing. Rebuild the setup with PayloadZipPath.");
    using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
    archive.ExtractToDirectory(installDirectory, overwriteFiles: true);
}

static void Uninstall(bool quiet)
{
    var installDirectory = InstallDirectory();
    DeleteFile(StartMenuShortcutPath());

    using (var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath(), writable: true))
    {
        key?.DeleteSubKeyTree(appName, throwOnMissingSubKey: false);
    }

    DeleteFile(Path.Combine(installDirectory, appExeName));

    if (!quiet)
    {
        ShowMessage(appName, "Voicemeeter Windows Volume Modern was uninstalled.");
    }

    ScheduleDirectoryRemoval(installDirectory);
}

static string InstallDirectory() =>
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        appName);

static string StartMenuShortcutPath()
{
    var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    return Path.Combine(programs, $"{appName}.lnk");
}

static void CreateShortcut(string shortcutPath, string targetPath, string description, string workingDirectory)
{
    Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
    var shellType = Type.GetTypeFromProgID("WScript.Shell")
        ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
    dynamic shell = Activator.CreateInstance(shellType)!;
    dynamic shortcut = shell.CreateShortcut(shortcutPath);
    shortcut.TargetPath = targetPath;
    shortcut.WorkingDirectory = workingDirectory;
    shortcut.Description = description;
    shortcut.IconLocation = targetPath;
    shortcut.Save();
}

static string UninstallRegistryPath() =>
    @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

static void RegisterUninstaller(string installDirectory, string appPath, string uninstallerPath)
{
    using var uninstallRoot = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath(), writable: true);
    using var key = uninstallRoot.CreateSubKey(appName, writable: true);
    key.SetValue("DisplayName", appName);
    key.SetValue("DisplayVersion", version);
    key.SetValue("Publisher", publisher);
    key.SetValue("InstallLocation", installDirectory);
    key.SetValue("DisplayIcon", appPath);
    key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
    key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --quiet");
    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
}

static void DeleteFile(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}

static void ScheduleDirectoryRemoval(string directory)
{
    if (!Directory.Exists(directory))
    {
        return;
    }

    var command = $"/c timeout /t 2 /nobreak > nul & rmdir /s /q \"{directory}\"";
    Process.Start(new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = command,
        CreateNoWindow = true,
        UseShellExecute = false,
        WindowStyle = ProcessWindowStyle.Hidden
    });
}

static void ShowMessage(string title, string message)
{
    _ = MessageBoxW(nint.Zero, message, title, 0);
}

[DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);
