$ErrorActionPreference = "Stop"

$driverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sysvadRoot = Join-Path $driverRoot "upstream\Windows-driver-samples\audio\sysvad"
$stageRoot = Join-Path $driverRoot "src\SonicFlowVirtualAudio"

$required = @(
    "EndpointsCommon",
    "TabletAudioSample",
    "A2dpHpDevice.cpp",
    "A2dpHpDevice.h",
    "adapter.cpp",
    "adapter.h",
    "basetopo.cpp",
    "basetopo.h",
    "BthhfpDevice.cpp",
    "BthhfpDevice.h",
    "common.cpp",
    "common.h",
    "ContosoKeywordDetector.h",
    "hw.cpp",
    "hw.h",
    "ihvprivatepropertyset.h",
    "kshelper.cpp",
    "kshelper.h",
    "savedata.cpp",
    "savedata.h",
    "sysvad.h",
    "SysVadShared.h",
    "tonegenerator.cpp",
    "tonegenerator.h",
    "UnittestData.h",
    "UsbHsDevice.cpp",
    "UsbHsDevice.h"
)

if (-not (Test-Path $sysvadRoot)) {
    throw "SysVAD source is missing. Run import-sysvad.ps1 first."
}

if (-not (Test-Path $stageRoot)) {
    New-Item -Path $stageRoot -ItemType Directory | Out-Null
}

foreach ($item in $required) {
    $source = Join-Path $sysvadRoot $item
    $target = Join-Path $stageRoot $item

    if (-not (Test-Path $source)) {
        Write-Warning "Missing expected SysVAD item: $source"
        continue
    }

    if (Test-Path $target) {
        Write-Host "Already staged: $item"
        continue
    }

    Copy-Item -Path $source -Destination $target -Recurse
    Write-Host "Staged: $item"
}

$generatedDirs = @(
    (Join-Path $stageRoot "EndpointsCommon\x64"),
    (Join-Path $stageRoot "TabletAudioSample\x64"),
    (Join-Path $stageRoot "SonicFlowVirtualAudio\x64")
)

foreach ($dir in $generatedDirs) {
    $resolved = Resolve-Path $dir -ErrorAction SilentlyContinue
    if ($resolved -and $resolved.Path.StartsWith($stageRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved.Path -Recurse -Force
        Write-Host "Removed generated output: $($resolved.Path)"
    }
}

Write-Host ""
Write-Host "Staged SonicFlow driver source at:"
Write-Host "  $stageRoot"
Write-Host ""
Write-Host "Next step: rename project/INF/device IDs from TabletAudioSample/SysVAD to SonicFlowVirtualAudio."
