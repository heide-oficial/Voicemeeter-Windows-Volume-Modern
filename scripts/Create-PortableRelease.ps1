param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

$script = Join-Path $PSScriptRoot "Create-ReleaseArtifacts.ps1"
& $script -Platform $Platform -SkipSetup
