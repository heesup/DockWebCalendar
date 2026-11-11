<#
.SYNOPSIS
    Checks the machine for everything needed to build and install this extension,
    and installs what is missing with winget.

.DESCRIPTION
    Needed to build:    the .NET 10 SDK. The Windows SDK is NOT required: the
                        project targets net10.0-windows10.0.26100.0, and the .NET
                        SDK downloads the matching Windows reference assemblies
                        from NuGet during restore.
    Needed to install:  Developer Mode (build-and-install.ps1 registers an unsigned
                        development package) and PowerToys Command Palette.
    Needed to run:      the Microsoft Edge WebView2 runtime (part of Windows 11).

.PARAMETER CheckOnly
    Report what is missing without installing or changing anything.

.PARAMETER WithWindowsSdk
    Also install the Windows SDK. Only useful if you want makeappx.exe and
    signtool.exe to pack and sign an .msix yourself.

.EXAMPLE
    ./setup.ps1 -CheckOnly
#>
param(
    [switch]$CheckOnly,
    [switch]$WithWindowsSdk
)

$ErrorActionPreference = "Stop"

# global.json pins 10.0.401 with rollForward latestFeature: any 10.0.4xx or later band works.
$minimumSdk = [version]"10.0.401"
$missing = New-Object System.Collections.Generic.List[string]

function Write-Status([string]$name, [bool]$ok, [string]$detail) {
    $mark = if ($ok) { "[ ok ]" } else { "[miss]" }
    $color = if ($ok) { "Green" } else { "Yellow" }
    Write-Host ("{0} {1,-28} {2}" -f $mark, $name, $detail) -ForegroundColor $color
}

function Install-WithWinget([string]$id, [string]$name) {
    if ($CheckOnly) { return $false }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host "       winget is not available; install $name by hand." -ForegroundColor Yellow
        return $false
    }

    Write-Host "       Installing $name ($id)..."
    winget install --id $id --exact --accept-source-agreements --accept-package-agreements
    return $LASTEXITCODE -eq 0
}

function Get-DotNetSdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $null }
    dotnet --list-sdks |
        ForEach-Object { if ($_ -match '^(\d+\.\d+\.\d+)') { [version]$Matches[1] } } |
        Where-Object { $_.Major -eq 10 -and $_ -ge $minimumSdk } |
        Sort-Object -Descending |
        Select-Object -First 1
}

Write-Host "Build"

$sdk = Get-DotNetSdk
if (-not $sdk -and (Install-WithWinget "Microsoft.DotNet.SDK.10" ".NET 10 SDK")) {
    # A fresh install is not on this session's PATH yet.
    $env:Path = "$env:ProgramFiles\dotnet;$env:Path"
    $sdk = Get-DotNetSdk
}
Write-Status ".NET 10 SDK" ([bool]$sdk) $(if ($sdk) { "$sdk" } else { "need $minimumSdk or a later 10.0 feature band" })
if (-not $sdk) { $missing.Add(".NET 10 SDK") }

if ($WithWindowsSdk) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $makeappx = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $makeappx -and (Install-WithWinget "Microsoft.WindowsSDK.10.0.26100" "Windows SDK 10.0.26100")) {
        $makeappx = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    Write-Status "Windows SDK (optional)" ([bool]$makeappx) $(if ($makeappx) { $makeappx.Directory.Parent.Name } else { "makeappx.exe not found" })
    if (-not $makeappx) { $missing.Add("Windows SDK") }
}

Write-Host ""
Write-Host "Install"

$developerMode = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
if (-not $developerMode -and -not $CheckOnly) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        New-Item "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Force | Out-Null
        Set-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
        $developerMode = $true
    }
}
Write-Status "Developer Mode" $developerMode $(if ($developerMode) { "on" } else { "off: turn it on under Settings > System > For developers, or rerun this script as administrator" })
if (-not $developerMode) { $missing.Add("Developer Mode") }

$palette = Get-AppxPackage -Name "Microsoft.CommandPalette" -ErrorAction SilentlyContinue
if (-not $palette -and (Install-WithWinget "Microsoft.PowerToys" "PowerToys")) {
    $palette = Get-AppxPackage -Name "Microsoft.CommandPalette" -ErrorAction SilentlyContinue
}
Write-Status "PowerToys Command Palette" ([bool]$palette) $(if ($palette) { "$($palette.Version)" } else { "install PowerToys, then enable Command Palette and its Dock" })
if (-not $palette) { $missing.Add("PowerToys Command Palette") }

$webView2 = @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
) | ForEach-Object { (Get-ItemProperty $_ -ErrorAction SilentlyContinue).pv } | Where-Object { $_ -and $_ -ne "0.0.0.0" } | Select-Object -First 1
if (-not $webView2 -and (Install-WithWinget "Microsoft.EdgeWebView2Runtime" "WebView2 runtime")) {
    $webView2 = "installed"
}
Write-Status "WebView2 runtime" ([bool]$webView2) $(if ($webView2) { "$webView2" } else { "install the Evergreen WebView2 runtime" })
if (-not $webView2) { $missing.Add("WebView2 runtime") }

Write-Host ""
if ($missing.Count -eq 0) {
    Write-Host "Everything is in place. Run ./build-and-install.ps1 next." -ForegroundColor Green
    exit 0
}

Write-Host ("Still missing: " + ($missing -join ", ")) -ForegroundColor Yellow
exit 1
