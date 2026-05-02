param(
    [string] $Configuration = "Debug",
    [string] $Platform = "x64",
    [switch] $EnableTestSigning
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-Bcdedit {
    $candidates = @(
        (Join-Path $env:SystemRoot 'System32\bcdedit.exe'),
        (Join-Path $env:SystemRoot 'Sysnative\bcdedit.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $whereOutput = cmd /c "where.exe bcdedit 2>nul"
    if ($LASTEXITCODE -eq 0 -and $whereOutput) {
        $first = (@($whereOutput) | Where-Object { $_ } | Select-Object -First 1)
        if ($first) {
            $first = $first.Trim()
            if ($first -and (Test-Path -LiteralPath $first)) {
                return $first
            }
        }
    }

    return $null
}

function Invoke-Bcdedit {
    param(
        [Parameter(Mandatory = $true)] [string] $BcdeditPath,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )

    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $BcdeditPath -ArgumentList $Arguments `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdoutFile `
            -RedirectStandardError $stderrFile

        $stdout = Get-Content -LiteralPath $stdoutFile -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut   = $stdout
            StdErr   = $stderr
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-IsAdmin)) {
    throw "Run this script from an elevated PowerShell window."
}

$driverRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageOut = Join-Path $driverRoot "out\$Platform\$Configuration"
$infPath    = Join-Path $packageOut "SonicFlowVirtualAudio.inf"
$certPath   = Join-Path $packageOut "SonicFlowVirtualAudio.cer"
$devcon     = "C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\devcon.exe"

if ($EnableTestSigning) {
    Write-Host "[1/3] Resolving bcdedit.exe..."
    $bcdedit = Resolve-Bcdedit
    if (-not $bcdedit) {
        Write-Host ""
        Write-Host "ERROR: Could not locate bcdedit.exe." -ForegroundColor Red
        Write-Host "Verify one of the following paths exists from an elevated 64-bit PowerShell window:" -ForegroundColor Red
        Write-Host "  - $($env:SystemRoot)\System32\bcdedit.exe" -ForegroundColor Red
        Write-Host "  - $($env:SystemRoot)\Sysnative\bcdedit.exe   (only visible from a 32-bit host on 64-bit Windows)" -ForegroundColor Red
        Write-Host "You can also run 'where.exe bcdedit' from cmd.exe to confirm Windows can find it." -ForegroundColor Red
        throw "bcdedit.exe was not found."
    }
    Write-Host "      Found: $bcdedit"

    Write-Host "[2/3] Enabling Windows test-signing mode..."
    $result = Invoke-Bcdedit -BcdeditPath $bcdedit -Arguments @('/set', 'testsigning', 'on')

    if ($result.StdOut) { Write-Host $result.StdOut.TrimEnd() }
    if ($result.StdErr) { Write-Host $result.StdErr.TrimEnd() }

    $combined = "$($result.StdOut)`n$($result.StdErr)"
    if ($combined -match 'Secure Boot policy') {
        Write-Host ""
        Write-Host "ERROR: Secure Boot is enabled on this PC." -ForegroundColor Red
        Write-Host "Windows refuses to enable test-signing while Secure Boot is on." -ForegroundColor Red
        Write-Host ""
        Write-Host "To proceed:" -ForegroundColor Yellow
        Write-Host "  1. Reboot into BIOS/UEFI firmware setup." -ForegroundColor Yellow
        Write-Host "     (Windows: Settings -> System -> Recovery -> Advanced startup -> Restart now," -ForegroundColor Yellow
        Write-Host "      then Troubleshoot -> Advanced options -> UEFI Firmware Settings -> Restart.)" -ForegroundColor Yellow
        Write-Host "  2. Find the 'Secure Boot' option (usually under Boot or Security) and set it to Disabled." -ForegroundColor Yellow
        Write-Host "  3. Save changes, boot back into Windows." -ForegroundColor Yellow
        Write-Host "  4. Re-run: .\install-test-driver.ps1 -EnableTestSigning" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "After driver testing is finished you can re-enable Secure Boot from the same BIOS screen." -ForegroundColor Yellow
        throw "Secure Boot blocks 'bcdedit /set testsigning on'. Driver install aborted."
    }

    if ($result.ExitCode -ne 0) {
        throw "bcdedit failed with exit code $($result.ExitCode). See output above."
    }

    Write-Host "[3/3] Test-signing enabled."
    Write-Host ""
    Write-Host "Reboot Windows, then re-run this script WITHOUT -EnableTestSigning to install the driver." -ForegroundColor Green
    exit 0
}

foreach ($path in @($infPath, $certPath, $devcon)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required file not found: $path"
    }
}

Write-Host "[1/3] Importing test certificate into LocalMachine\Root and TrustedPublisher..."
Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\Root            | Out-Null
Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

Write-Host "[2/3] Installing driver via DevCon..."
& $devcon install $infPath "Root\SonicFlowVirtualAudio"
if ($LASTEXITCODE -ne 0) {
    throw "DevCon install failed with exit code $LASTEXITCODE"
}

Write-Host "[3/3] SonicFlow Virtual Speaker install command completed."
