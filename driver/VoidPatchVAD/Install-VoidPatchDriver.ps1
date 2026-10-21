param(
    [switch]$Restore
)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    Write-Host "Requesting Administrator privileges to configure VoidPatch Virtual Audio Driver endpoints..." -ForegroundColor Cyan
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Restore) { $argList += " -Restore" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$exePath = "G:\VoidPatch\publish\VoidPatch.exe"
if (-not (Test-Path $exePath)) {
    $exePath = "G:\VoidPatch\src\VoidPatch\bin\Release\net10.0-windows\VoidPatch.exe"
}

if (Test-Path $exePath) {
    if ($Restore) {
        & $exePath --restore-driver
    } else {
        & $exePath --provision-driver
    }
    Write-Host "Done!" -ForegroundColor Green
} else {
    Write-Host "VoidPatch.exe not built yet. Run Install-VoidPatch.ps1 first." -ForegroundColor Yellow
}
