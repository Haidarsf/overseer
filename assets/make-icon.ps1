# Draws Overseer's icons (Windows dev tool; the outputs are committed, so other platforms don't need it).
#   overseer.ico / overseer-256.png   app icon: slate tile, ivory ring and dot, clay satellite (Claude palette)
#   tray-idle.png / tray-working.png / tray-attention.png   32px tray variants (status dot colour)
# Run:  powershell -File assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here '..\src\Overseer.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

function C($a, $r, $g, $b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function Draw-Icon([int]$s, $dot) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # slate tile
    $m = [Math]::Max(0.5, $s * 0.03); $w = $s - 2 * $m; $r = $w * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90); $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $slate = C 255 0x14 0x14 0x13; $ivory = C 255 0xFA 0xF9 0xF5; $clay = C 255 0xD9 0x77 0x57
    $g.FillPath((New-Object System.Drawing.SolidBrush $slate), $path)

    # the mark: a ring (the session), a centre dot (you) and a clay satellite (Claude)
    $cx = $s * 0.5; $cy = $s * 0.5; $rr = $s * 0.27
    $pen = New-Object System.Drawing.Pen $ivory, ([float][Math]::Max(1.2, $s * 0.06))
    $g.DrawEllipse($pen, [float]($cx - $rr), [float]($cy - $rr), [float](2 * $rr), [float](2 * $rr))
    $cr = $s * 0.085
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $ivory), [float]($cx - $cr), [float]($cy - $cr), [float](2 * $cr), [float](2 * $cr))
    $a = -45 * [Math]::PI / 180; $sx = $cx + $rr * [Math]::Cos($a); $sy = $cy + $rr * [Math]::Sin($a)
    $sr = $s * 0.1; $halo = $sr + [Math]::Max(1, $s * 0.035)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $slate), [float]($sx - $halo), [float]($sy - $halo), [float](2 * $halo), [float](2 * $halo))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $clay), [float]($sx - $sr), [float]($sy - $sr), [float](2 * $sr), [float](2 * $sr))

    if ($dot) {
        $d = $s * 0.34; $dx = $s - $d - $s * 0.02; $dy = $s - $d - $s * 0.02
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $ivory), [float]($dx - $s * 0.04), [float]($dy - $s * 0.04), [float]($d + $s * 0.08), [float]($d + $s * 0.08))
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $dot), [float]$dx, [float]$dy, [float]$d, [float]$d)
    }
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $file) { $bmp.Save((Join-Path $out $file), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

# multi-size .ico (PNG entries)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $b = Draw-Icon $s $null
    $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray()
    if ($s -eq 256) { $b.Save((Join-Path $out 'overseer-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}
$o = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $o
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b8 = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$b8); $bw.Write([byte]$b8); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $out 'overseer.ico'), $o.ToArray())

Save-Png (Draw-Icon 64 (C 255 0xB0 0xAE 0xA5)) 'tray-idle.png'
Save-Png (Draw-Icon 64 (C 255 0x6A 0x9B 0xCC)) 'tray-working.png'
Save-Png (Draw-Icon 64 (C 255 0xD9 0x77 0x57)) 'tray-attention.png'
Copy-Item (Join-Path $out 'overseer-256.png') (Join-Path $here 'icon.png') -Force
Write-Host "Icons written to $out"
