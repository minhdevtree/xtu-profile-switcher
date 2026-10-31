# Generates app.ico (a CPU chip with a lightning bolt) from code, so the icon is reproducible.
#   powershell -ExecutionPolicy Bypass -File .\make-icon.ps1 [-Preview preview.png]
param([string]$Preview = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sizes = 16, 20, 24, 32, 48, 64, 128, 256

# Lightning bolt in a unit square.
$bolt = @(
    @(0.58, 0.05), @(0.22, 0.56), @(0.46, 0.56), @(0.36, 0.95), @(0.78, 0.40), @(0.54, 0.40), @(0.66, 0.05)
)

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-BoltPath([float]$x, [float]$y, [float]$w, [float]$h) {
    $pts = New-Object 'System.Drawing.PointF[]' $bolt.Count
    for ($i = 0; $i -lt $bolt.Count; $i++) {
        $pts[$i] = New-Object System.Drawing.PointF -ArgumentList ([float]($x + $bolt[$i][0] * $w)), ([float]($y + $bolt[$i][1] * $h))
    }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon($pts)
    return $p
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background, blue gradient.
    $inset = $s * 0.03
    $bg = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s),
        [System.Drawing.Color]::FromArgb(255, 46, 170, 255), [System.Drawing.Color]::FromArgb(255, 24, 52, 140))
    $g.FillPath($grad, $bg)

    $amber = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 205, 60))

    if ($s -ge 32) {
        # Chip body.
        $cx = $s * 0.27; $cw = $s * 0.46
        $body = New-RoundedRect $cx $cx $cw $cw ($s * 0.06)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 14, 28, 70))), $body)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 235, 245, 255)), ([float]([Math]::Max(1.0, $s * 0.03)))
        $g.DrawPath($pen, $body)

        # Pins, three per side.
        $pinLen = $s * 0.08
        $pinPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 235, 245, 255)), ([float]([Math]::Max(1.0, $s * 0.035)))
        foreach ($t in 0.38, 0.50, 0.62) {
            $p = $s * $t
            $g.DrawLine($pinPen, $p, $cx - $pinLen, $p, $cx)                 # top
            $g.DrawLine($pinPen, $p, $cx + $cw, $p, $cx + $cw + $pinLen)     # bottom
            $g.DrawLine($pinPen, $cx - $pinLen, $p, $cx, $p)                 # left
            $g.DrawLine($pinPen, $cx + $cw, $p, $cx + $cw + $pinLen, $p)     # right
        }

        $b = New-BoltPath ($s * 0.355) ($s * 0.32) ($s * 0.29) ($s * 0.36)
        $g.FillPath($amber, $b)
    }
    else {
        # Too small for a chip: a big bolt on the background.
        $b = New-BoltPath ($s * 0.22) ($s * 0.12) ($s * 0.56) ($s * 0.76)
        $g.FillPath($amber, $b)
    }

    $g.Dispose()
    return $bmp
}

# Build the .ico (PNG-compressed entries).
$pngs = @()
$bitmaps = @()
foreach ($s in $sizes) {
    $bmp = Render $s
    $bitmaps += , $bmp
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'app.ico'), $out.ToArray())
Write-Host ("Wrote app.ico ({0} bytes, sizes {1})" -f $out.Length, ($sizes -join ','))

if ($Preview) {
    # Contact sheet: 256, 64, 32 and 16 px on light and dark backgrounds.
    $sheet = New-Object System.Drawing.Bitmap 640, 300
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear([System.Drawing.Color]::White)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))), 320, 0, 320, 300)
    $g.InterpolationMode = 'NearestNeighbor'
    $x = 12
    foreach ($i in 7, 5, 3, 0) {
        $bmp = $bitmaps[$i]
        $g.DrawImage($bmp, $x, 20, $bmp.Width, $bmp.Height)
        $g.DrawImage($bmp, $x + 320, 20, $bmp.Width, $bmp.Height)
        $x += $bmp.Width + 8
        if ($i -eq 7) { $x = 12 + 256 + 8; }
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Preview: $Preview"
}
