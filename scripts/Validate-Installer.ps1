param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$GeneratedFiles,
    [Parameter(Mandatory)][string]$WixExecutable
)
$ErrorActionPreference = "Stop"

# MSI's legacy language validator cannot represent all WinUI runtime LANGIDs.
# Mark only Microsoft's WinUI resource files language-neutral in the MSI table;
# the shipped file bytes and their actual resources remain unchanged.
[xml]$files = Get-Content -LiteralPath $GeneratedFiles -Raw -Encoding UTF8
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase([System.IO.Path]::GetFullPath($MsiPath), 1)
try {
    foreach ($file in $files.SelectNodes("//*[local-name()='File']")) {
        $name = [System.IO.Path]::GetFileName($file.Source)
        if ($name -notin @("Microsoft.ui.xaml.dll", "Microsoft.UI.Xaml.Phone.dll",
            "Microsoft.ui.xaml.dll.mui", "Microsoft.UI.Xaml.Phone.dll.mui")) { continue }
        $source = [System.IO.Path]::GetFullPath($file.Source)
        $root = [System.IO.Path]::GetFullPath($PublishRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $source.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid MSI source path." }
        $view = $database.OpenView('UPDATE `File` SET `Language` = ? WHERE `File` = ?')
        try {
            $record = $installer.CreateRecord(2)
            $record.StringData(1) = "0"
            $record.StringData(2) = $file.Id
            $view.Execute($record)
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null
        }
        finally { $view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
    }
    $database.Commit()
}
finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}

# Same-version replacement is intentional. All other ICE validators remain enabled.
& $WixExecutable msi validate $MsiPath -sice ICE61
if ($LASTEXITCODE -ne 0) { throw "Final MSI validation failed: $LASTEXITCODE" }
