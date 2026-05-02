param(
    [string] $Configuration = "Debug",
    [string] $Platform = "x64"
)

$ErrorActionPreference = "Stop"

$driverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $driverRoot "build-driver.ps1"
$buildOut = Join-Path $driverRoot "src\SonicFlowVirtualAudio\SonicFlowVirtualAudio\$Platform\$Configuration"
$packageOut = Join-Path $driverRoot "out\$Platform\$Configuration"
$kitBin = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0"
$inf2cat = Join-Path $kitBin "x86\Inf2Cat.exe"
$signtool = Join-Path $kitBin "x86\signtool.exe"

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "Build script not found: $buildScript"
}

& $buildScript -Staged
if ($LASTEXITCODE -ne 0) {
    throw "Driver build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $buildOut)) {
    throw "Build output folder not found: $buildOut"
}

if (-not (Test-Path -LiteralPath $inf2cat)) {
    throw "Inf2Cat.exe not found: $inf2cat"
}

if (-not (Test-Path -LiteralPath $signtool)) {
    throw "signtool.exe not found: $signtool"
}

if (-not (Test-Path -LiteralPath $packageOut)) {
    New-Item -Path $packageOut -ItemType Directory | Out-Null
}

foreach ($name in @("SonicFlowVirtualAudio.inf", "SonicFlowVirtualAudio.sys", "SonicFlowVirtualAudio.cer")) {
    $source = Join-Path $buildOut $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Expected build artifact is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $packageOut $name) -Force
}

& $inf2cat /driver:$packageOut /os:10_X64 /uselocaltime
if ($LASTEXITCODE -ne 0) {
    throw "Inf2Cat failed with exit code $LASTEXITCODE"
}

$generatedCat = Join-Path $packageOut "sonicflowvirtualaudio.cat"
$catalog = Join-Path $packageOut "SonicFlowVirtualAudio.cat"
if (Test-Path -LiteralPath $generatedCat) {
    $currentCatalog = Get-Item -LiteralPath $generatedCat
    if ($currentCatalog.Name -cne "SonicFlowVirtualAudio.cat") {
        $temporaryCatalog = Join-Path $packageOut "SonicFlowVirtualAudio.cat.tmp"
        Move-Item -LiteralPath $currentCatalog.FullName -Destination $temporaryCatalog -Force
        Move-Item -LiteralPath $temporaryCatalog -Destination $catalog -Force
    }
}

$sysPath = Join-Path $packageOut "SonicFlowVirtualAudio.sys"
$signature = Get-AuthenticodeSignature -LiteralPath $sysPath
$thumbprint = $signature.SignerCertificate.Thumbprint

if ([string]::IsNullOrWhiteSpace($thumbprint)) {
    throw "Could not read the WDK test-signing certificate thumbprint from $sysPath"
}

& $signtool sign /ph /fd sha256 /sha1 $thumbprint $catalog
if ($LASTEXITCODE -ne 0) {
    throw "Catalog signing failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "SonicFlow driver package is ready:"
Write-Host "  $packageOut"
