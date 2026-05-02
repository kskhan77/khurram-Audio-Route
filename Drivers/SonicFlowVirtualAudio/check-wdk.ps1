$ErrorActionPreference = "Stop"

$kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10"

function Find-FirstFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Filter
    )

    if (-not (Test-Path $Root)) {
        return $null
    }

    Get-ChildItem -Path $Root -Recurse -Filter $Filter -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Find-PreferredTool {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Filter
    )

    if (-not (Test-Path $Root)) {
        return $null
    }

    $tools = Get-ChildItem -Path $Root -Recurse -Filter $Filter -ErrorAction SilentlyContinue
    $tools |
        Sort-Object `
            @{ Expression = { if ($_.FullName -match "\\x64\\") { 0 } elseif ($_.FullName -match "\\x86\\") { 1 } else { 2 } } }, `
            FullName |
        Select-Object -First 1
}

$driverBuildProps = Find-FirstFile -Root $kitsRoot -Filter "WindowsDriver.Common.props"
$wdkDesignTimeProps = Find-FirstFile -Root $kitsRoot -Filter "WDK.props"
$stampInf = Find-PreferredTool -Root $kitsRoot -Filter "stampinf.exe"
$devCon = Find-PreferredTool -Root $kitsRoot -Filter "devcon.exe"

Write-Host "SonicFlow Virtual Audio - WDK readiness"
Write-Host ""
Write-Host "Windows Kits root: $kitsRoot"
Write-Host "Driver props:     $($driverBuildProps.FullName)"
Write-Host "WDK design props: $($wdkDesignTimeProps.FullName)"
Write-Host "stampinf.exe:     $($stampInf.FullName)"
Write-Host "devcon.exe:       $($devCon.FullName)"
Write-Host ""

$missing = @()
if ($null -eq $driverBuildProps) { $missing += "WindowsDriver.Common.props" }
if ($null -eq $wdkDesignTimeProps) { $missing += "WDK.props" }
if ($null -eq $stampInf) { $missing += "stampinf.exe" }
if ($null -eq $devCon) { $missing += "devcon.exe" }

if ($missing.Count -gt 0) {
    Write-Host "Missing WDK tooling:"
    $missing | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "WDK tooling looks available."
exit 0
