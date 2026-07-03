param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [switch]$SkipSetup
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$rid = "win-$($Platform.ToLowerInvariant())"
$appProject = Join-Path $repoRoot "native\VMWV.App\VMWV.App.csproj"
$portableProject = Join-Path $repoRoot "native\VMWV.Portable\VMWV.Portable.csproj"
$installerProject = Join-Path $repoRoot "native\VMWV.Installer\VMWV.Installer.wixproj"
$installerGeneratedFiles = Join-Path $repoRoot "native\VMWV.Installer\GeneratedFiles.wxs"
$artifactRoot = Join-Path $repoRoot "artifacts\release"
$publishRoot = Join-Path $artifactRoot "publish-$rid"
$portablePublishRoot = Join-Path $artifactRoot "portable-publish-$rid"
$installerBuildRoot = Join-Path $artifactRoot "installer-build-$rid"
$payloadZip = Join-Path $artifactRoot "payload-$rid.zip"
$portableExe = Join-Path $artifactRoot "VoicemeeterWindowsVolumeModern-Portable-$Platform.exe"
$setupMsi = Join-Path $artifactRoot "VoicemeeterWindowsVolumeModern-Setup-$Platform.msi"
$legacySetupExe = Join-Path $artifactRoot "VoicemeeterWindowsVolumeModern-Setup-$Platform.exe"

function Assert-InRepo {
    param([Parameter(Mandatory)][string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($repoRoot)
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $full"
    }
}

Assert-InRepo $artifactRoot
Assert-InRepo $publishRoot
Assert-InRepo $portablePublishRoot
Assert-InRepo $installerBuildRoot
Assert-InRepo $installerGeneratedFiles
Assert-InRepo $payloadZip
Assert-InRepo $portableExe
Assert-InRepo $setupMsi
Assert-InRepo $legacySetupExe

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $portablePublishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installerBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installerGeneratedFiles -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $portableExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $setupMsi -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacySetupExe -Force -ErrorAction SilentlyContinue

function ConvertTo-WixId {
    param([Parameter(Mandatory)][string]$Value)

    $id = [System.Text.RegularExpressions.Regex]::Replace($Value, "[^A-Za-z0-9_]", "_")
    if ($id.Length -eq 0 -or -not [char]::IsLetter($id[0])) {
        $id = "Id_$id"
    }

    if ($id.Length -gt 68) {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
        }
        finally {
            $sha256.Dispose()
        }
        $hash = [System.BitConverter]::ToString($hashBytes, 0, 4).Replace("-", "")
        $id = $id.Substring(0, 59) + "_" + $hash
    }

    return $id
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][string]$Path
    )

    $baseUri = [System.Uri](([System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'))
    $pathUri = [System.Uri]([System.IO.Path]::GetFullPath($Path))
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Write-WixDirectory {
    param(
        [Parameter(Mandatory)][System.IO.DirectoryInfo]$Directory,
        [Parameter(Mandatory)][System.Text.StringBuilder]$Xml,
        [System.Collections.Generic.List[string]]$ComponentIds
    )

    foreach ($file in $Directory.GetFiles() | Sort-Object FullName) {
        $relative = Get-RelativePath -BasePath $publishRoot -Path $file.FullName
        $fileId = ConvertTo-WixId "fil_$relative"
        $componentId = ConvertTo-WixId "cmp_$relative"
        [void]$ComponentIds.Add($componentId)
        [void]$Xml.AppendLine("      <Component Id=`"$componentId`" Guid=`"*`">")
        [void]$Xml.AppendLine("        <File Id=`"$fileId`" Source=`"$($file.FullName)`" KeyPath=`"yes`" />")
        [void]$Xml.AppendLine("      </Component>")
    }

    foreach ($child in $Directory.GetDirectories() | Sort-Object FullName) {
        $relative = Get-RelativePath -BasePath $publishRoot -Path $child.FullName
        $directoryId = ConvertTo-WixId "dir_$relative"
        [void]$Xml.AppendLine("      <Directory Id=`"$directoryId`" Name=`"$($child.Name)`">")
        Write-WixDirectory -Directory $child -Xml $Xml -ComponentIds $ComponentIds
        [void]$Xml.AppendLine("      </Directory>")
    }
}

function Write-WixGeneratedFiles {
    $components = [System.Collections.Generic.List[string]]::new()
    $xml = [System.Text.StringBuilder]::new()

    [void]$xml.AppendLine("<?xml version=`"1.0`" encoding=`"utf-8`"?>")
    [void]$xml.AppendLine("<Wix xmlns=`"http://wixtoolset.org/schemas/v4/wxs`">")
    [void]$xml.AppendLine("  <Fragment>")
    [void]$xml.AppendLine("    <DirectoryRef Id=`"APPLICATIONFOLDER`">")
    Write-WixDirectory -Directory (Get-Item -LiteralPath $publishRoot) -Xml $xml -ComponentIds $components
    [void]$xml.AppendLine("    </DirectoryRef>")
    [void]$xml.AppendLine("  </Fragment>")
    [void]$xml.AppendLine("  <Fragment>")
    [void]$xml.AppendLine("    <ComponentGroup Id=`"AppFiles`">")
    foreach ($componentId in $components) {
        [void]$xml.AppendLine("      <ComponentRef Id=`"$componentId`" />")
    }
    [void]$xml.AppendLine("    </ComponentGroup>")
    [void]$xml.AppendLine("  </Fragment>")
    [void]$xml.AppendLine("</Wix>")

    Set-Content -LiteralPath $installerGeneratedFiles -Value $xml.ToString() -Encoding UTF8
}

dotnet publish $appProject `
    -c Release `
    -r $rid `
    -p:Platform=$Platform `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:SelfContained=true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $publishRoot

$publishedApp = Join-Path $publishRoot "VMWV.App.exe"
if (-not (Test-Path $publishedApp)) {
    throw "App publish did not create $publishedApp"
}

Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $publishRoot "LICENSE.txt") -Force
Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $payloadZip -Force

dotnet publish $portableProject `
    -c Release `
    -r $rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PayloadZipPath=$payloadZip `
    -o $portablePublishRoot

$publishedPortable = Join-Path $portablePublishRoot "VMWV.Portable.exe"
if (-not (Test-Path $publishedPortable)) {
    throw "Portable publish did not create $publishedPortable"
}

Copy-Item -LiteralPath $publishedPortable -Destination $portableExe -Force

$result = [ordered]@{
    Portable = $portableExe
}

if (-not $SkipSetup) {
    Write-WixGeneratedFiles

    dotnet build $installerProject `
        -c Release `
        -p:Platform=$Platform `
        -p:PublishRoot=$publishRoot `
        -o $installerBuildRoot

    $publishedSetup = Join-Path $installerBuildRoot "VoicemeeterWindowsVolumeModern-Setup.msi"
    if (-not (Test-Path $publishedSetup)) {
        throw "Installer build did not create $publishedSetup"
    }

    Copy-Item -LiteralPath $publishedSetup -Destination $setupMsi -Force
    $result.Setup = $setupMsi
}

[pscustomobject]$result | ConvertTo-Json
