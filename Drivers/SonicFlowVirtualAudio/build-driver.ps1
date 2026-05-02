param(
    [switch] $Staged,
    [switch] $FullSolution
)

$ErrorActionPreference = "Stop"

if ($Staged -and $FullSolution) {
    throw "Choose either -Staged or -FullSolution, not both."
}

$driverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$checkScript = Join-Path $driverRoot "check-wdk.ps1"

if (Test-Path $checkScript) {
    & $checkScript
    if ($LASTEXITCODE -ne 0) {
        throw "WDK readiness check failed. Install the Windows Driver Kit before building the driver."
    }
}

$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null

if (Test-Path $vswhere) {
    $vs2022Path = & $vswhere -version "[17.0,18.0)" -products * -property installationPath |
        Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($vs2022Path)) {
        $candidate = Join-Path $vs2022Path "MSBuild\Current\Bin\amd64\MSBuild.exe"
        if (Test-Path $candidate) {
            $msbuild = Get-Item $candidate
        }
    }
}

if ($null -eq $msbuild) {
    $msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\2022\\.*\\amd64\\MSBuild.exe$" } |
        Select-Object -First 1
}

if ($null -eq $msbuild) {
    $msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\amd64\\MSBuild.exe$" } |
        Select-Object -First 1
}

if ($null -eq $msbuild) {
    throw "MSBuild x64 was not found. Install Visual Studio with C++ desktop and WDK components."
}

$repoRoot = (Resolve-Path (Join-Path $driverRoot "..\..")).Path
$buildDriverRoot = $driverRoot
$mappedDrive = "S:"
$createdMapping = $false

if (-not (Test-Path $mappedDrive)) {
    & subst $mappedDrive $repoRoot
    if ($LASTEXITCODE -eq 0) {
        $createdMapping = $true
        $buildDriverRoot = Join-Path "$mappedDrive\" "Drivers\SonicFlowVirtualAudio"
    }
}
elseif ((Get-Item $mappedDrive).PSDrive.Root -eq "$mappedDrive\") {
    $buildDriverRoot = Join-Path "$mappedDrive\" "Drivers\SonicFlowVirtualAudio"
}

$sysvadRoot = Join-Path $buildDriverRoot "upstream\Windows-driver-samples\audio\sysvad"
$sysvadSolution = Join-Path $sysvadRoot "sysvad.sln"
$tabletAudioProject = Join-Path $sysvadRoot "TabletAudioSample\TabletAudioSample.vcxproj"
$stagedEndpointsProject = Join-Path $buildDriverRoot "src\SonicFlowVirtualAudio\EndpointsCommon\EndpointsCommon.vcxproj"
$stagedSonicFlowProject = Join-Path $buildDriverRoot "src\SonicFlowVirtualAudio\SonicFlowVirtualAudio\SonicFlowVirtualAudio.vcxproj"

if ($Staged) {
    $buildTarget = $stagedSonicFlowProject
    $label = "staged SonicFlow virtual audio driver"
}
elseif ($FullSolution) {
    $buildTarget = $sysvadSolution
    $label = "full SysVAD solution"
}
else {
    $buildTarget = $tabletAudioProject
    $label = "TabletAudioSample virtual audio driver"
}

if (-not (Test-Path $buildTarget)) {
    throw "Driver build target is missing. Run import-sysvad.ps1 and stage-sonicflow-driver.ps1 first: $buildTarget"
}

try {
    if ($Staged) {
        if (-not (Test-Path $stagedEndpointsProject)) {
            throw "Staged EndpointsCommon project is missing: $stagedEndpointsProject"
        }

        Write-Host "Building staged EndpointsCommon library..."
        & $msbuild.FullName $stagedEndpointsProject /m /p:Configuration=Debug /p:Platform=x64
        if ($LASTEXITCODE -ne 0) {
            throw "EndpointsCommon build failed with exit code $LASTEXITCODE"
        }
    }

    Write-Host "Building $label..."
    Write-Host "MSBuild: $($msbuild.FullName)"
    Write-Host "Target:  $buildTarget"
    & $msbuild.FullName $buildTarget /m /p:Configuration=Debug /p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
}
finally {
    if ($createdMapping) {
        & subst $mappedDrive /D
    }
}
