param(
    [Parameter(Mandatory)]
    [int]$AppPid
)

$ErrorActionPreference = "Continue"
$pass = 0
$fail = 0
$results = @()

function Test-UI {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [scriptblock]$Script
    )

    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += @{ name = $Name; status = "PASS" }
        }
        else {
            $script:fail++
            $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" }
        }
    }
    catch {
        $script:fail++
        $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
    }
}

Test-UI "Root navigation exists" { winapp ui wait-for "RootNavigation" -a $AppPid -t 5000 }
Test-UI "Dashboard navigation exists" { winapp ui wait-for "NavDashboard" -a $AppPid -t 5000 }
Test-UI "Navigate to bindings" { winapp ui invoke "NavBindings" -a $AppPid }
Test-UI "Strip bindings list exists" { winapp ui wait-for "StripBindingTargetsList" -a $AppPid -t 5000 }
Test-UI "Bus bindings list exists" { winapp ui wait-for "BusBindingTargetsList" -a $AppPid -t 5000 }
Test-UI "Navigate to settings" { winapp ui invoke "NavSettings" -a $AppPid }
Test-UI "Settings control group exists" { winapp ui wait-for "SettingsControlCommandBar" -a $AppPid -t 5000 }
Test-UI "Connect command exists in settings" { winapp ui wait-for "BtnConnectVoicemeeter" -a $AppPid -t 5000 }
Test-UI "Refresh command works from settings" { winapp ui invoke "BtnRefreshStatus" -a $AppPid }
Test-UI "Logo variant selector exists" { winapp ui wait-for "CmbLogoVariant" -a $AppPid -t 5000 }
Test-UI "Close to tray toggle exists" { winapp ui wait-for "TglCloseToTray" -a $AppPid -t 5000 }
Test-UI "Settings sync mute toggle exists" { winapp ui wait-for "TglSyncMute" -a $AppPid -t 5000 }
Test-UI "Navigate to about" { winapp ui invoke "NavAbout" -a $AppPid }
Test-UI "Modern repository button exists" { winapp ui wait-for "BtnModernRepository" -a $AppPid -t 5000 }
Test-UI "Original repository button exists" { winapp ui wait-for "BtnLegacyRepository" -a $AppPid -t 5000 }

New-Item -ItemType Directory -Force -Path "screenshots" | Out-Null
winapp ui screenshot -a $AppPid -o "screenshots/ui-smoke-final.png" 2>$null | Out-Null

$results | ConvertTo-Json -Depth 4 | Out-File "ui-smoke-results.json" -Encoding utf8
Write-Host "Passed: $pass | Failed: $fail"

if ($fail -gt 0) {
    $results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
        Write-Host "FAIL: $($_.name) - $($_.detail)"
    }

    exit 1
}

exit 0
