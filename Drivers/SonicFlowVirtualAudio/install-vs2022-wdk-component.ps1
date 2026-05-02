$ErrorActionPreference = "Stop"

$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
$installer = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vs_installer.exe"

if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio Installer first."
}

if (-not (Test-Path $installer)) {
    throw "vs_installer.exe was not found. Install Visual Studio Installer first."
}

$vs2022Path = & $vswhere -version "[17.0,18.0)" -products * -property installationPath |
    Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($vs2022Path)) {
    throw "Visual Studio 2022 was not found."
}

$blockingSetup = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match "vs_installer|setup|VSIXInstaller" }

if ($blockingSetup) {
    Write-Host "Visual Studio Installer/setup process is already running:"
    $blockingSetup | Select-Object ProcessName, Id | Format-Table -AutoSize
    throw "Close the Visual Studio Installer/setup window, then run this script again."
}

Write-Host "Adding VS 2022 Windows Driver Kit component..."
Write-Host "VS 2022 path: $vs2022Path"

$arguments = 'modify --installPath "{0}" --add Component.Microsoft.Windows.DriverKit --includeRecommended --passive --norestart' -f $vs2022Path

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
}
else {
    Write-Host "Requesting administrator elevation for Visual Studio Installer..."
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Verb RunAs -Wait -PassThru
}

if ($process.ExitCode -ne 0) {
    throw "Visual Studio Installer failed with exit code $($process.ExitCode)"
}

Write-Host "VS 2022 WDK component install completed."
