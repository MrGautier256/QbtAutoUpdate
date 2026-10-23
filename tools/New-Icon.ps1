<#
.SYNOPSIS
    Draws the QbtAutoUpdate icon (a download arrow inside a circular "update" arrow) and writes
    src\QbtAutoUpdate\Resources\QbtAutoUpdate.ico (16 to 256 px) and QbtAutoUpdate.png (256 px).
    Needs Windows PowerShell 5.1 (System.Drawing). Run it again only to change the design.
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\QbtAutoUpdate\Resources'

function Draw([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded blue tile.
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 56 * $s; $m = 8 * $s; $w = $size - 2 * $m
    $tile.AddArc($m, $m, $r, $r, 180, 90)
    $tile.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $tile.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90)
    $tile.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $tile.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 37, 99, 235), [System.Drawing.Color]::FromArgb(255, 29, 78, 216))
    $g.FillPath($brush, $tile)

    $white = [System.Drawing.Color]::White
    # Circular "update" arrow: an open ring with an arrow head.
    $pen = New-Object System.Drawing.Pen($white, [single](18 * $s))
    $pen.StartCap = 'Round'
    $ring = 64 * $s
    $start = -35.0; $sweep = 280.0
    $g.DrawArc($pen, [single]$ring, [single]$ring, [single]($size - 2 * $ring), [single]($size - 2 * $ring), [single]$start, [single]$sweep)
    # Arrow head at the end of the arc, pointing along it (clockwise).
    $end = ($start + $sweep) * [Math]::PI / 180
    $c = 128 * $s; $radius = $c - $ring
    $ex = $c + $radius * [Math]::Cos($end); $ey = $c + $radius * [Math]::Sin($end)
    $tx = -[Math]::Sin($end); $ty = [Math]::Cos($end)      # tangent, clockwise
    $nx = [Math]::Cos($end); $ny = [Math]::Sin($end)       # normal, outwards
    $half = 22 * $s; $length = 30 * $s
    $head = @(
        (New-Object System.Drawing.PointF([single]($ex + $tx * $length), [single]($ey + $ty * $length))),
        (New-Object System.Drawing.PointF([single]($ex + $nx * $half), [single]($ey + $ny * $half))),
        (New-Object System.Drawing.PointF([single]($ex - $nx * $half), [single]($ey - $ny * $half))))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($white)), $head)

    # Download arrow in the middle.
    $shaft = New-Object System.Drawing.Pen($white, [single](16 * $s))
    $shaft.StartCap = 'Round'
    $g.DrawLine($shaft, [single](128 * $s), [single](98 * $s), [single](128 * $s), [single](150 * $s))
    $arrow = @(
        (New-Object System.Drawing.PointF([single](100 * $s), [single](138 * $s))),
        (New-Object System.Drawing.PointF([single](156 * $s), [single](138 * $s))),
        (New-Object System.Drawing.PointF([single](128 * $s), [single](170 * $s))))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($white)), $arrow)

    $g.Dispose()
    return $bitmap
}

function PngBytes([System.Drawing.Bitmap] $bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (PngBytes (Draw $size)) }

# ICO with PNG frames (supported since Windows Vista).
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ico)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]([byte[]]$images[$i]).Length); $writer.Write([uint32]$offset)
    $offset += ([byte[]]$images[$i]).Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $out 'QbtAutoUpdate.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $out 'QbtAutoUpdate.png'), [byte[]]$images[-1])
Write-Host "Wrote QbtAutoUpdate.ico and QbtAutoUpdate.png to $out"
