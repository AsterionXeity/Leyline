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
    Write-Host "Requesting Administrator privileges to configure Leyline Virtual Audio Driver endpoints..." -ForegroundColor Cyan
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Restore) { $argList += " -Restore" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait
    exit
}

$exePath = "G:\Leyline\publish\Leyline.exe"
if (-not (Test-Path $exePath)) {
    $exePath = "G:\Leyline\src\Leyline\bin\Release\net10.0-windows\Leyline.exe"
}

if (Test-Path $exePath) {
    if ($Restore) {
        & $exePath --restore-driver
    } else {
        & $exePath --provision-driver
    }
    Write-Host "Done!" -ForegroundColor Green
} else {
    Write-Host "Leyline.exe not built yet. Run Install-Leyline.ps1 first." -ForegroundColor Yellow
}
