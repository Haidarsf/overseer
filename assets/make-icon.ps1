# Draws Overseer's icons (Windows dev tool; the outputs are committed, so other platforms don't need it).
#   overseer.ico / overseer-256.png   app icon: indigo-violet tile with a white eye and teal iris
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

    $m = [Math]::Max(0.5, $s * 0.03); $w = $s - 2 * $m; $r = $w * 0.26
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90); $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 255 0x6D 0x74 0xF8), (C 255 0x9B 0x5D 0xE5), 45.0), $path)
    if ($s -ge 32) { $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 55 255 255 255), (C 0 255 255 255), 90.0), $path) }

    # eye: two arcs forming an almond, iris, pupil, highlight
    $cx = $s * 0.5; $cy = $s * 0.5; $ew = $s * 0.62; $eh = $s * 0.40
    $eye = New-Object System.Drawing.Drawing2D.GraphicsPath
    $eye.AddBezier([float]($cx - $ew / 2), [float]$cy, [float]($cx - $ew * 0.2), [float]($cy - $eh * 0.95), [float]($cx + $ew * 0.2), [float]($cy - $eh * 0.95), [float]($cx + $ew / 2), [float]$cy)
    $eye.AddBezier([float]($cx + $ew / 2), [float]$cy, [float]($cx + $ew * 0.2), [float]($cy + $eh * 0.95), [float]($cx - $ew * 0.2), [float]($cy + $eh * 0.95), [float]($cx - $ew / 2), [float]$cy)
    $eye.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $eye)
    $ir = $s * 0.145
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 0x14 0xB8 0xA6)), [float]($cx - $ir), [float]($cy - $ir), [float](2 * $ir), [float](2 * $ir))
    $pr = $s * 0.065
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 0x1E 0x1B 0x4B)), [float]($cx - $pr), [float]($cy - $pr), [float](2 * $pr), [float](2 * $pr))
    if ($s -ge 24) {
        $hr = $s * 0.03
        $g.FillEllipse([System.Drawing.Brushes]::White, [float]($cx + $ir * 0.35 - $hr), [float]($cy - $ir * 0.45 - $hr), [float](2 * $hr), [float](2 * $hr))
    }
    if ($dot) {
        $d = $s * 0.34; $dx = $s - $d - $s * 0.02; $dy = $s - $d - $s * 0.02
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 255 0x16 0x15 0x2B)), [float]($dx - $s * 0.04), [float]($dy - $s * 0.04), [float]($d + $s * 0.08), [float]($d + $s * 0.08))
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

Save-Png (Draw-Icon 64 (C 255 0x94 0xA3 0xB8)) 'tray-idle.png'
Save-Png (Draw-Icon 64 (C 255 0x34 0xD3 0x99)) 'tray-working.png'
Save-Png (Draw-Icon 64 (C 255 0xFB 0xBF 0x24)) 'tray-attention.png'
Copy-Item (Join-Path $out 'overseer-256.png') (Join-Path $here 'icon.png') -Force
Write-Host "Icons written to $out"
