# Installs (or removes) the test-signed Leyline Virtual Audio driver on this PC. Run as Administrator.
#
# Prerequisite (once): Windows test signing must be on, then reboot:
#   bcdedit /set testsigning on
#
# Install:  .\Install-TestDriver.ps1
# Remove:   .\Install-TestDriver.ps1 -Remove
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an Administrator PowerShell.'
}

$hardwareId = 'Root\LeylineVAD'
$devcon = Get-ChildItem (Join-Path $PSScriptRoot 'packages') -Recurse -Filter devcon.exe |
    Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName
if (-not $devcon) { throw 'devcon.exe not found; restore the WDK NuGet packages first.' }

if ($Remove) {
    & $devcon remove $hardwareId
    # Remove the driver package from the driver store as well.
    $published = pnputil /enum-drivers | Select-String -Context 1, 0 'Original Name:\s+leylinevad\.inf' |
        ForEach-Object { ($_.Context.PreContext[0] -split ':\s+')[1] }
    foreach ($inf in $published) { pnputil /delete-driver $inf /uninstall /force }
    return
}

$testSigning = (bcdedit /enum '{current}' | Select-String 'testsigning\s+Yes') -ne $null
if (-not $testSigning) {
    throw "Test signing is off. Run 'bcdedit /set testsigning on' as Administrator, reboot, then run this again."
}

$out = Join-Path $PSScriptRoot "out\$Configuration"
$cer = Join-Path $out 'LeylineTestCert.cer'
$inf = Join-Path $out 'LeylineVAD.inf'
if (-not (Test-Path $inf)) { throw "No package in $out; run Build-Driver.ps1 and Package-Driver.ps1 first." }

# Trust the test publisher so Windows installs the package without a prompt.
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

# Root-enumerated device: create the device node and install the driver on it.
$existing = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $hardwareId }
if ($existing) {
    & $devcon update $inf $hardwareId
} else {
    & $devcon install $inf $hardwareId
}
if ($LASTEXITCODE -ne 0) { throw "devcon failed with exit code $LASTEXITCODE." }

Get-PnpDevice | Where-Object { $_.HardwareID -contains $hardwareId } | Select-Object Status, FriendlyName, InstanceId
