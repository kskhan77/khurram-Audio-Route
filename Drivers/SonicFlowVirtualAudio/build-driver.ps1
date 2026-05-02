$ErrorActionPreference = "Stop"

$driverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sysvadSolution = Join-Path $driverRoot "upstream\Windows-driver-samples\audio\sysvad\sysvad.sln"
$checkScript = Join-Path $driverRoot "check-wdk.ps1"

if (Test-Path $checkScript) {
    & $checkScript
    if ($LASTEXITCODE -ne 0) {
        throw "WDK readiness check failed. Install the Windows Driver Kit before building the driver."
    }
}

if (-not (Test-Path $sysvadSolution)) {
    throw "SysVAD solution is missing. Run import-sysvad.ps1 first."
}

$msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\amd64\\MSBuild.exe$" } |
    Select-Object -First 1

if ($null -eq $msbuild) {
    throw "MSBuild x64 was not found. Install Visual Studio with C++ desktop and WDK components."
}

Write-Host "Building SysVAD sample before SonicFlow rename..."
& $msbuild.FullName $sysvadSolution /m /p:Configuration=Debug /p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}
