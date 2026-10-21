param(
    [switch]$ProvisionDriver,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   VoidPatch Audio Mixer, Router, Soundboard & VST Host   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$publishDir = "G:\VoidPatch\publish"
$exePath = Join-Path $publishDir "VoidPatch.exe"
$iconPath = Join-Path $publishDir "VoidPatch.ico"

if (-not (Test-Path $exePath)) {
    Write-Host "Building VoidPatch Release binaries..." -ForegroundColor Yellow
    dotnet publish "G:\VoidPatch\src\VoidPatch\VoidPatch.csproj" -c Release -o $publishDir
}

# Generate VoidPatch.ico using System.Drawing
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(256, 256)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(255, 14, 16, 26))
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0, 229, 255), 18)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
$pts = @(
    (New-Object System.Drawing.PointF(48, 108)),
    (New-Object System.Drawing.PointF(85, 62)),
    (New-Object System.Drawing.PointF(128, 194)),
    (New-Object System.Drawing.PointF(172, 62)),
    (New-Object System.Drawing.PointF(208, 134))
)
$g.DrawLines($pen, $pts)
$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $ms.ToArray()
$bmp.Dispose()

$fs = [System.IO.File]::Create($iconPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]1)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([uint16]1)
$bw.Write([uint16]32)
$bw.Write([uint32]$pngBytes.Length)
$bw.Write([uint32]22)
$bw.Write($pngBytes)
$bw.Close()
$fs.Close()

# Create Desktop & Start Menu Shortcuts
$wsh = New-Object -ComObject WScript.Shell
$desktopPath = [Environment]::GetFolderPath("Desktop")
$startMenuPath = Join-Path ([Environment]::GetFolderPath("Programs")) "VoidPatch.lnk"
$desktopShortcutPath = Join-Path $desktopPath "VoidPatch.lnk"

foreach ($lnkPath in @($desktopShortcutPath, $startMenuPath)) {
    $sc = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath = $exePath
    $sc.WorkingDirectory = $publishDir
    $sc.IconLocation = "$iconPath,0"
    $sc.Description = "VoidPatch Virtual Audio Mixer, App Router, Soundboard & VST Host"
    $sc.Save()
}
Write-Host "[OK] Installed Desktop and Start Menu shortcuts for VoidPatch." -ForegroundColor Green

# Register Auto-Start with Windows in HKCU Run
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "VoidPatch" -Value "`"$exePath`""
Write-Host "[OK] Enabled Auto-Start with Windows (HKCU\...\Run\VoidPatch)." -ForegroundColor Green

if ($ProvisionDriver) {
    & "G:\VoidPatch\driver\VoidPatchVAD\Install-VoidPatchDriver.ps1"
}

if (-not $NoLaunch) {
    Write-Host "[OK] Launching VoidPatch..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath -WorkingDirectory $publishDir
}
