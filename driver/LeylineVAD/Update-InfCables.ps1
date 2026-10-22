# Regenerates the per-cable blocks of src\TabletAudioSample\LeylineVAD.inx from LEYLINE_CABLES in
# src\LeylineShared.h, so the driver's filter names / name GUIDs and the INF can't drift apart.
# Build-Driver.ps1 runs this before building.

$ErrorActionPreference = 'Stop'

$header = Join-Path $PSScriptRoot 'src\LeylineShared.h'
$inx = Join-Path $PSScriptRoot 'src\TabletAudioSample\LeylineVAD.inx'

# X(Id, "DisplayName", l, w1, w2, b1..b8)
$entry = 'X\(\s*(\w+)\s*,\s*"([^"]+)"\s*,\s*0x([0-9a-fA-F]+)\s*,\s*0x([0-9a-fA-F]+)\s*,\s*0x([0-9a-fA-F]+)\s*,' +
         '((?:\s*0x[0-9a-fA-F]+\s*,?){8})\)'
$cables = foreach ($m in [regex]::Matches((Get-Content $header -Raw), $entry)) {
    $bytes = [regex]::Matches($m.Groups[6].Value, '0x([0-9a-fA-F]+)') | ForEach-Object { '{0:x2}' -f [Convert]::ToByte($_.Groups[1].Value, 16) }
    $guid = '{{{0}-{1}-{2}-{3}-{4}}}' -f $m.Groups[3].Value.PadLeft(8, '0'), $m.Groups[4].Value.PadLeft(4, '0'),
        $m.Groups[5].Value.PadLeft(4, '0'), ($bytes[0..1] -join ''), ($bytes[2..7] -join '')
    [pscustomobject]@{ Id = $m.Groups[1].Value; Name = $m.Groups[2].Value; Guid = $guid.ToLower() }
}
if (-not $cables) { throw "No LEYLINE_CABLES entries found in $header." }

$names = foreach ($c in $cables) {
    'HKR,%MEDIA_CATEGORIES%\{0},Name,,"{1}"' -f $c.Guid, $c.Name
}

$interfaces = foreach ($c in $cables) {
    "; $($c.Name)"
    "AddInterface = %KSCATEGORY_AUDIO%,    ""WaveRender$($c.Id)"",  Leyline.I.WaveRender"
    "AddInterface = %KSCATEGORY_RENDER%,   ""WaveRender$($c.Id)"",  Leyline.I.WaveRender"
    "AddInterface = %KSCATEGORY_REALTIME%, ""WaveRender$($c.Id)"",  Leyline.I.WaveRender"
    "AddInterface = %KSCATEGORY_AUDIO%,    ""TopoRender$($c.Id)"",  Leyline.I.TopoRender"
    "AddInterface = %KSCATEGORY_TOPOLOGY%, ""TopoRender$($c.Id)"",  Leyline.I.TopoRender"
    "AddInterface = %KSCATEGORY_AUDIO%,    ""WaveCapture$($c.Id)"", Leyline.I.WaveCapture"
    "AddInterface = %KSCATEGORY_CAPTURE%,  ""WaveCapture$($c.Id)"", Leyline.I.WaveCapture"
    "AddInterface = %KSCATEGORY_REALTIME%, ""WaveCapture$($c.Id)"", Leyline.I.WaveCapture"
    "AddInterface = %KSCATEGORY_AUDIO%,    ""TopoCapture$($c.Id)"", Leyline.I.TopoCapture"
    "AddInterface = %KSCATEGORY_TOPOLOGY%, ""TopoCapture$($c.Id)"", Leyline.I.TopoCapture"
}

function Set-Block([string]$text, [string]$tag, [string[]]$lines) {
    $pattern = "(?ms)(^;@@BEGIN $tag\r?\n).*?(^;@@END $tag)"
    if ($text -notmatch $pattern) { throw "Marker block '$tag' not found in $inx." }
    return [regex]::Replace($text, $pattern, { param($m) $m.Groups[1].Value + (($lines -join "`r`n") + "`r`n") + $m.Groups[2].Value })
}

$text = Get-Content $inx -Raw
$updated = Set-Block $text 'CABLE NAMES' $names
$updated = Set-Block $updated 'CABLE INTERFACES' $interfaces

if ($updated -ne $text) {
    [IO.File]::WriteAllText($inx, $updated, [Text.UTF8Encoding]::new($false))
    "Updated $([IO.Path]::GetFileName($inx)) for $($cables.Count) cables."
}
