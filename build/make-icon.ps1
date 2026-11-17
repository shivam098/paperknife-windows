# Draws the PaperKnife icon (a page with a paper knife across it) and writes a multi-size .ico.
# Run with Windows PowerShell 5.1, which includes System.Drawing:
#   powershell -NoProfile -File build\make-icon.ps1
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\PaperKnife.App\Assets\PaperKnife.ico'),
    [string]$Preview = (Join-Path $PSScriptRoot '..\docs\images\paperknife-icon.png')
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundedRectangle([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$n) {
    $bitmap = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background tile
    $tile = New-RoundedRectangle 0 0 ($n - 0.5) ($n - 0.5) ($n * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 91, 184))), $tile)

    # Page with a folded corner
    $left = $n * 0.25; $top = $n * 0.16; $right = $n * 0.75; $bottom = $n * 0.84; $fold = $n * 0.15
    $page = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $left, $top),
        (New-Object System.Drawing.PointF ($right - $fold), $top),
        (New-Object System.Drawing.PointF $right, ($top + $fold)),
        (New-Object System.Drawing.PointF $right, $bottom),
        (New-Object System.Drawing.PointF $left, $bottom))
    $g.FillPolygon([System.Drawing.Brushes]::White, $page)
    $corner = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($right - $fold), $top),
        (New-Object System.Drawing.PointF ($right - $fold), ($top + $fold)),
        (New-Object System.Drawing.PointF $right, ($top + $fold)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 196, 214, 240))), $corner)

    # Lines of text (only where they stay legible)
    if ($n -ge 32) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 176, 194, 222)), ([Math]::Max(1.0, $n * 0.035))
        foreach ($y in 0.36, 0.46, 0.56) {
            $g.DrawLine($pen, $n * 0.33, $n * $y, $n * 0.62, $n * $y)
        }
    }

    # Paper knife: a slim orange blade with a darker handle, laid diagonally across the page
    $g.TranslateTransform($n * 0.5, $n * 0.56)
    $g.RotateTransform(-45)
    $blade = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (-$n * 0.02), (-$n * 0.055)),
        (New-Object System.Drawing.PointF ($n * 0.40), (-$n * 0.012)),
        (New-Object System.Drawing.PointF ($n * 0.40), ($n * 0.012)),
        (New-Object System.Drawing.PointF (-$n * 0.02), ($n * 0.055)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 159, 28))), $blade)
    $handle = New-RoundedRectangle (-$n * 0.34) (-$n * 0.07) ($n * 0.33) ($n * 0.14) ($n * 0.05)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 196, 92, 0))), $handle)
    $g.Dispose()
    return $bitmap
}

function Get-PngBytes($bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return $stream.ToArray()
}

# 32-bit DIB entry (BITMAPINFOHEADER, bottom-up BGRA rows, empty AND mask) for small sizes.
function Get-DibBytes($bitmap) {
    $n = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    $writer.Write([int]40); $writer.Write([int]$n); $writer.Write([int]($n * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($n * $n * 4 + $maskRow * $n)); $writer.Write([int]0); $writer.Write([int]0)
    $writer.Write([int]0); $writer.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
        }
    }
    $writer.Write((New-Object byte[] ($maskRow * $n)))
    $writer.Flush()
    return $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $bytes = if ($size -ge 128) { Get-PngBytes $bitmap } else { Get-DibBytes $bitmap }
    if ($size -eq 256) { $bitmap.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$image.Bytes.Length); $writer.Write([int]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $Output)).Path + '\' + (Split-Path $Output -Leaf), $file.ToArray())
Write-Output "Wrote $Output ($($file.Length) bytes, sizes: $($sizes -join ', '))"
