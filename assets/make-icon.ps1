# Draws the Claude Monitor icon (orange gradient tile + white sparkle + live dot) and writes app.ico and icon.png.
# Re-run after tweaking:  .\assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # rounded tile
    $m = [Math]::Max(0.5, $s * 0.03); $w = $s - 2 * $m; $r = $w * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $m, $m, $w, $w
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0xF5, 0xB0, 0x62)), ([System.Drawing.Color]::FromArgb(255, 0xD4, 0x5F, 0x3A)), 45.0
    $g.FillPath($grad, $path)

    # soft top highlight
    if ($s -ge 32) {
        $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
        $g.FillPath($hl, $path)
    }

    # 4-point sparkle
    $cx = $s * 0.47; $cy = $s * 0.50; $ro = $s * 0.31; $ri = $s * 0.085
    $pts = New-Object 'System.Drawing.PointF[]' 8
    for ($i = 0; $i -lt 8; $i++) {
        $a = -[Math]::PI / 2 + $i * [Math]::PI / 4
        $rad = if ($i % 2 -eq 0) { $ro } else { $ri }
        $pts[$i] = New-Object System.Drawing.PointF ([float]($cx + $rad * [Math]::Cos($a))), ([float]($cy + $rad * [Math]::Sin($a)))
    }
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)

    # "live" dot, bottom-right
    if ($s -ge 24) {
        $d = $s * 0.17; $dx = $s * 0.70; $dy = $s * 0.68
        $ring = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xD4, 0x5F, 0x3A))
        $g.FillEllipse($ring, $dx - $s * 0.025, $dy - $s * 0.025, $d + $s * 0.05, $d + $s * 0.05)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x7E, 0xE0, 0xA4))), $dx, $dy, $d, $d)
    }
    $g.Dispose()
    return $bmp
}

$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($s -eq 256) { $bmp.Save((Join-Path $here 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# ICO container with PNG-compressed entries
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $here 'app.ico'), $out.ToArray())
Write-Host "Wrote $(Join-Path $here 'app.ico') and icon.png"
