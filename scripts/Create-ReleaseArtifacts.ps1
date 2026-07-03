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
$setupProject = Join-Path $repoRoot "native\VMWV.Setup\VMWV.Setup.csproj"
$artifactRoot = Join-Path $repoRoot "artifacts\release"
$publishRoot = Join-Path $artifactRoot "publish-$rid"
$portablePublishRoot = Join-Path $artifactRoot "portable-publish-$rid"
$setupPublishRoot = Join-Path $artifactRoot "setup-publish-$rid"
$payloadZip = Join-Path $artifactRoot "payload-$rid.zip"
$portableExe = Join-Path $artifactRoot "VoicemeeterWindowsVolumeModern-Portable-$Platform.exe"
$setupExe = Join-Path $artifactRoot "VoicemeeterWindowsVolumeModern-Setup-$Platform.exe"

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
Assert-InRepo $setupPublishRoot
Assert-InRepo $payloadZip
Assert-InRepo $portableExe
Assert-InRepo $setupExe

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $portablePublishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $setupPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $portableExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $setupExe -Force -ErrorAction SilentlyContinue

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
    dotnet publish $setupProject `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:PayloadZipPath=$payloadZip `
        -o $setupPublishRoot

    $publishedSetup = Join-Path $setupPublishRoot "VMWV.Setup.exe"
    if (-not (Test-Path $publishedSetup)) {
        throw "Setup publish did not create $publishedSetup"
    }

    Copy-Item -LiteralPath $publishedSetup -Destination $setupExe -Force
    $result.Setup = $setupExe
}

[pscustomobject]$result | ConvertTo-Json
