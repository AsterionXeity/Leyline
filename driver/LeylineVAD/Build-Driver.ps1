# Builds the Leyline Virtual Audio driver (SysVAD-based) for x64.
#
# Requires Visual Studio 2022/2026 with the "Windows Driver Kit" component and the WDK NuGet packages:
#   nuget restore packages.config -PackagesDirectory packages
#
# Based on the April 2021 SysVAD (Windows 10 era) and compiled for Windows 10 2004 (19041) and later.
# Test-DriverImports.ps1 checks the result against this PC's kernel modules.
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

# NTDDI_WIN10_VB (Windows 10 2004)
$NtTargetVersion = '0xA000008'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.Windows.DriverKit -property installationPath
if (-not $vs) { throw 'No Visual Studio with the Windows Driver Kit component was found.' }

Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

$src = Join-Path $PSScriptRoot 'src'
foreach ($project in 'EndpointsCommon\EndpointsCommon.vcxproj', 'TabletAudioSample\TabletAudioSample.vcxproj') {
    msbuild (Join-Path $src $project) -m -nologo -v:minimal `
        -p:Configuration=$Configuration -p:Platform=x64 `
        -p:TargetVersion=Windows10 -p:_NT_TARGET_VERSION=$NtTargetVersion
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

Get-ChildItem (Join-Path $src "TabletAudioSample\x64\$Configuration") -Include *.sys, *.inf, *.cat -Recurse |
    Select-Object Name, Length, LastWriteTime
