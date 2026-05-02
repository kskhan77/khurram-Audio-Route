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

$driverKitProps = Find-FirstFile -Root $kitsRoot -Filter "Microsoft.DriverKit.props"
$stampInf = Find-FirstFile -Root $kitsRoot -Filter "stampinf.exe"
$devCon = Find-FirstFile -Root $kitsRoot -Filter "devcon.exe"

Write-Host "SonicFlow Virtual Audio - WDK readiness"
Write-Host ""
Write-Host "Windows Kits root: $kitsRoot"
Write-Host "DriverKit props:  $($driverKitProps.FullName)"
Write-Host "stampinf.exe:     $($stampInf.FullName)"
Write-Host "devcon.exe:       $($devCon.FullName)"
Write-Host ""

$missing = @()
if ($null -eq $driverKitProps) { $missing += "Microsoft.DriverKit.props" }
if ($null -eq $stampInf) { $missing += "stampinf.exe" }
if ($null -eq $devCon) { $missing += "devcon.exe" }

if ($missing.Count -gt 0) {
    Write-Host "Missing WDK tooling:"
    $missing | ForEach-Object { Write-Host " - $_" }
    exit 1
}

Write-Host "WDK tooling looks available."
