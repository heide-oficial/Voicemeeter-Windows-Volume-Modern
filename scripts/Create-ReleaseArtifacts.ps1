param(
    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [switch]$SkipSetup,

    [string]$OutputDirectory = "artifacts/release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$rid = "win-$($Platform.ToLowerInvariant())"
$appProject = Join-Path $repoRoot "native\VMWV.App\VMWV.App.csproj"
$portableProject = Join-Path $repoRoot "native\VMWV.Portable\VMWV.Portable.csproj"
$installerProject = Join-Path $repoRoot "native\VMWV.Installer\VMWV.Installer.wixproj"
$installerGeneratedFiles = Join-Path $repoRoot "native\VMWV.Installer\GeneratedFiles.wxs"
$destinationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$artifactRoot = Join-Path $destinationRoot (".staging-" + [Guid]::NewGuid().ToString("N"))
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
    $root = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $full"
    }
}

Assert-InRepo $destinationRoot
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

    if ($id.Length -gt 0) {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
        }
        finally {
            $sha256.Dispose()
        }
        $hash = [System.BitConverter]::ToString($hashBytes, 0, 4).Replace("-", "")
        $id = $id.Substring(0, [Math]::Min(59, $id.Length)) + "_" + $hash
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
    param([System.IO.DirectoryInfo]$Directory, [System.Xml.XmlWriter]$Xml,
        [System.Collections.Generic.List[string]]$ComponentIds)

    foreach ($file in $Directory.GetFiles() | Sort-Object FullName) {
        $relative = Get-RelativePath -BasePath $publishRoot -Path $file.FullName
        $componentId = ConvertTo-WixId "cmp_$relative"
        $ComponentIds.Add($componentId)
        $Xml.WriteStartElement("Component")
        $Xml.WriteAttributeString("Id", $componentId)
        $Xml.WriteAttributeString("Guid", "*")
        $Xml.WriteStartElement("File")
        $Xml.WriteAttributeString("Id", (ConvertTo-WixId "fil_$relative"))
        $Xml.WriteAttributeString("Source", $file.FullName)
        $Xml.WriteAttributeString("KeyPath", "yes")
        $Xml.WriteEndElement()
        $Xml.WriteEndElement()
    }
    foreach ($child in $Directory.GetDirectories() | Sort-Object FullName) {
        $Xml.WriteStartElement("Directory")
        $Xml.WriteAttributeString("Id", (ConvertTo-WixId ("dir_" + (Get-RelativePath $publishRoot $child.FullName))))
        $Xml.WriteAttributeString("Name", $child.Name)
        Write-WixDirectory -Directory $child -Xml $Xml -ComponentIds $ComponentIds
        $Xml.WriteEndElement()
    }
}

function Write-WixGeneratedFiles {
    $components = [System.Collections.Generic.List[string]]::new()
    $options = [System.Xml.XmlWriterSettings]::new()
    $options.Indent = $true
    $xml = [System.Xml.XmlWriter]::Create($installerGeneratedFiles, $options)
    try {
        $xml.WriteStartDocument()
        $xml.WriteStartElement("Wix", "http://wixtoolset.org/schemas/v4/wxs")
        $xml.WriteStartElement("Fragment")
        $xml.WriteStartElement("DirectoryRef")
        $xml.WriteAttributeString("Id", "APPLICATIONFOLDER")
        Write-WixDirectory -Directory (Get-Item -LiteralPath $publishRoot) -Xml $xml -ComponentIds $components
        $xml.WriteEndElement()
        $xml.WriteEndElement()
        $xml.WriteStartElement("Fragment")
        $xml.WriteStartElement("ComponentGroup")
        $xml.WriteAttributeString("Id", "AppFiles")
        foreach ($componentId in $components) {
            $xml.WriteStartElement("ComponentRef")
            $xml.WriteAttributeString("Id", $componentId)
            $xml.WriteEndElement()
        }
        $xml.WriteEndElement()
        $xml.WriteEndElement()
        $xml.WriteEndElement()
        $xml.WriteEndDocument()
    }
    finally { $xml.Dispose() }
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
if ($LASTEXITCODE -ne 0) { throw "Application publish failed with exit code $LASTEXITCODE" }

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
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed with exit code $LASTEXITCODE" }

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
        -p:SuppressValidation=true `
        -o $installerBuildRoot
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE" }

    $publishedSetup = Join-Path $installerBuildRoot "VoicemeeterWindowsVolumeModern-Setup.msi"
    if (-not (Test-Path $publishedSetup)) {
        throw "Installer build did not create $publishedSetup"
    }

    $wixVersion = ([xml](Get-Content -LiteralPath $installerProject -Raw)).Project.Sdk.Split('/')[1]
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget/packages" }
    $wixExecutable = Join-Path $nugetRoot "wixtoolset.sdk/$wixVersion/tools/net472/x64/wix.exe"
    & (Join-Path $PSScriptRoot "Validate-Installer.ps1") -MsiPath $publishedSetup -PublishRoot $publishRoot `
        -GeneratedFiles $installerGeneratedFiles -WixExecutable $wixExecutable

    Copy-Item -LiteralPath $publishedSetup -Destination $setupMsi -Force
    $result.Setup = $setupMsi
}

foreach ($key in @($result.Keys)) {
    $source = $result[$key]
    $destination = Join-Path $destinationRoot ([System.IO.Path]::GetFileName($source))
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $result[$key] = $destination
}
$metadata = [ordered]@{
    BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Version = (Get-Item -LiteralPath $publishedApp).VersionInfo.FileVersion
    Files = @($result.Values | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 | Select-Object Hash,Path })
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destinationRoot "build-manifest.json") -Encoding UTF8
[pscustomobject]$result | ConvertTo-Json
