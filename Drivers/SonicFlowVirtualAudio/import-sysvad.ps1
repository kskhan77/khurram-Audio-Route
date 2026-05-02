$ErrorActionPreference = "Stop"

$driverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$upstreamRoot = Join-Path $driverRoot "upstream"
$repoPath = Join-Path $upstreamRoot "Windows-driver-samples"
$sysvadPath = Join-Path $repoPath "audio\sysvad"

function Invoke-Git {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    & git -c core.longpaths=true -c gc.writeCommitGraph=false -c "safe.directory=$repoPath" @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

Write-Host "SonicFlow Virtual Audio - SysVAD import"
Write-Host "Driver root: $driverRoot"

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is required to import the Microsoft Windows driver samples."
}

if (-not (Test-Path $upstreamRoot)) {
    New-Item -Path $upstreamRoot -ItemType Directory | Out-Null
}

if (-not (Test-Path $repoPath)) {
    Write-Host "Cloning Microsoft Windows driver samples with sparse checkout..."
    Invoke-Git clone --filter=blob:none --sparse https://github.com/microsoft/Windows-driver-samples.git $repoPath
}

Push-Location $repoPath
try {
    Invoke-Git config core.longpaths true
    Invoke-Git config gc.writeCommitGraph false
    Invoke-Git sparse-checkout set audio/sysvad
    Invoke-Git sparse-checkout reapply
    Invoke-Git checkout
}
finally {
    Pop-Location
}

if (-not (Test-Path $sysvadPath)) {
    throw "SysVAD sample was not found after sparse checkout: $sysvadPath"
}

Write-Host ""
Write-Host "SysVAD imported at:"
Write-Host "  $sysvadPath"
Write-Host ""
Write-Host "Next manual step:"
Write-Host "  Open sysvad.sln, build once unchanged, then rename/reduce it for SonicFlow."
