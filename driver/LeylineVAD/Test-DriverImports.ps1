# Checks that every function the built driver imports is exported by this PC's kernel modules.
# The driver compiles against Windows 11 headers (see Build-Driver.ps1); a missing export here means
# Windows would refuse to load it on this machine.
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.Windows.DriverKit -property installationPath
# CI images may register the WDK without the DriverKit component; fall back to the newest Visual Studio.
if (-not $vs) { $vs = & $vswhere -latest -products * -property installationPath }
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

$sys = Join-Path $PSScriptRoot "src\TabletAudioSample\x64\$Configuration\LeylineVAD.sys"

# Parse "dumpbin /imports": a module line, then indented "<hint> <name>" lines.
$imports = @{}
$module = $null
foreach ($line in dumpbin /nologo /imports $sys) {
    if ($line -match '^\s+Summary\s*$') { break }   # section sizes follow, not imports
    if ($line -match '^\s{4}(\S+\.(sys|exe|dll))\s*$') { $module = $Matches[1].ToLower(); $imports[$module] = @(); continue }
    if ($module -and $line -match '^\s+[0-9A-F]+\s+(\w+)\s*$') { $imports[$module] += $Matches[1] }
}

$missing = 0
foreach ($mod in $imports.Keys) {
    $path = @("$env:SystemRoot\System32\drivers\$mod", "$env:SystemRoot\System32\$mod") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $path) { Write-Host "MISSING MODULE $mod" -ForegroundColor Red; $missing++; continue }

    $exports = @{}
    foreach ($line in dumpbin /nologo /exports $path) {
        if ($line -match '^\s+\d+\s+[0-9A-F]+\s+(?:[0-9A-F]+\s+)?(\w+)') { $exports[$Matches[1]] = $true }
    }

    $absent = $imports[$mod] | Where-Object { -not $exports.ContainsKey($_) }
    "{0,-14} {1,4} imports, {2} missing" -f $mod, $imports[$mod].Count, @($absent).Count
    foreach ($fn in $absent) { Write-Host "  missing: $mod!$fn" -ForegroundColor Red; $missing++ }
}

if ($missing -gt 0) { throw "$missing import(s) are not available on this Windows build." }
"All imports resolve on Windows build $([Environment]::OSVersion.Version.Build)."
