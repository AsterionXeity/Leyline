# Packages the built Leyline driver for installation: copies the .sys/.inf, creates the catalog (.cat)
# and test-signs it with the WDK test certificate the build already used for the .sys.
#
# Output: driver\LeylineVAD\out\<Configuration>\ (LeylineVAD.sys, .inf, .cat, LeylineTestCert.cer)
#
# Test-signed drivers only load with Windows test signing on (bcdedit /set testsigning on + reboot).
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$build = Join-Path $PSScriptRoot "src\TabletAudioSample\x64\$Configuration"
$out = Join-Path $PSScriptRoot "out\$Configuration"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $build 'LeylineVAD.sys'), (Join-Path $build 'LeylineVAD.inf') $out -Force

$sysSigner = (Get-AuthenticodeSignature (Join-Path $out 'LeylineVAD.sys')).SignerCertificate
if (-not $sysSigner) { throw 'LeylineVAD.sys is not signed; build it with Build-Driver.ps1 first.' }

$tools = Get-ChildItem (Join-Path $PSScriptRoot 'packages') -Recurse -Directory -Filter x64 |
    Where-Object { Test-Path (Join-Path $_.FullName 'inf2cat.exe') } | Select-Object -First 1 -ExpandProperty FullName
if (-not $tools) {
    $tools = Get-ChildItem (Join-Path $PSScriptRoot 'packages') -Recurse -Filter inf2cat.exe |
        Select-Object -First 1 | ForEach-Object { $_.DirectoryName }
}
$signtool = Get-ChildItem (Join-Path $PSScriptRoot 'packages') -Recurse -Filter signtool.exe |
    Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName

# Windows 10 2004 (19041) and later, x64; matches the INF's models section.
& (Join-Path $tools 'inf2cat.exe') /driver:$out /os:10_VB_X64 /uselocaltime
if ($LASTEXITCODE -ne 0) { throw 'inf2cat failed.' }

& $signtool sign /fd SHA256 /sha1 $sysSigner.Thumbprint /s My (Join-Path $out 'LeylineVAD.cat')
if ($LASTEXITCODE -ne 0) { throw 'Signing the catalog failed.' }

# Public certificate, for trusting the test publisher on the target PC.
Export-Certificate -Cert "Cert:\CurrentUser\My\$($sysSigner.Thumbprint)" -FilePath (Join-Path $out 'LeylineTestCert.cer') | Out-Null

Get-ChildItem $out | Select-Object Name, Length
