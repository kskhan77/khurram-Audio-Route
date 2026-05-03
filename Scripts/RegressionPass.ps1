#Requires -Version 5.1
<#
.SYNOPSIS
    v1 regression preflight — clean output build + pointers to manual PLAN.md checklist.

.DESCRIPTION
    Stops any running KhurramAudioRoute, builds the csproj into a fresh temp folder so
    bin\Debug locks don’t interfere, echoes git HEAD, then prints the next manual steps.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Scripts\RegressionPass.ps1
    powershell -ExecutionPolicy Bypass -File .\Scripts\RegressionPass.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Csproj = Join-Path $ProjectRoot 'KhurramAudioRoute.csproj'

if (-not (Test-Path -LiteralPath $Csproj)) {
    Write-Error "KhurramAudioRoute.csproj not found at: $Csproj"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$OutDir = Join-Path ([System.IO.Path]::GetTempPath()) "SonicFlowRegression-$stamp"

Write-Host "--- SonicFlow regression preflight ---"
Push-Location $ProjectRoot
try {
    $head = (& git rev-parse --short HEAD 2>$null)
    if ($head) { Write-Host "Git HEAD: $head" }
}
catch { Write-Host '(git rev-parse unavailable)' }

Write-Host 'Stopping KhurramAudioRoute if running...'
Stop-Process -Name 'KhurramAudioRoute' -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

Write-Host "dotnet build -c $Configuration -o `"$OutDir`" ..."
& dotnet build $Csproj -c $Configuration -o $OutDir --no-restore
if (-not $? -or $LASTEXITCODE -ne 0) {
    Write-Host '(retrying with restore...)'
    & dotnet restore $Csproj
    & dotnet build $Csproj -c $Configuration -o $OutDir
}

if ($LASTEXITCODE -ne 0) {
    Pop-Location
    Write-Error "Build failed ($LASTEXITCODE)."
}

$exe = Join-Path $OutDir 'KhurramAudioRoute.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Pop-Location
    Write-Error "Expected output missing: $exe"
}

Write-Host ""
Write-Host "Build succeeded."
Write-Host "  Output: $OutDir"
Write-Host ""
Write-Host "Next (manual): see PLAN.md Testing checklist."
Write-Host "  1. Launch: $exe"
Write-Host "  2. Other Options -> run Diagnostics; View Output (Debug) for AudioCoreTester lines."
Write-Host "  3. Walk Outputs, VB-CABLE, L2, and power scenarios in PLAN.md; tick checkboxes when done."
Write-Host ""
Pop-Location
