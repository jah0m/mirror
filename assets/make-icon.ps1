# Draws the app icon with code and writes a multi-size assets\Mirror.ico.
# Run with: pwsh -File assets\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded dark tile
    $radius = $size * 0.22
    $d = $radius * 2
    $max = $size - 1
    $tile = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($max - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($max - $d, $max - $d, $d, $d, 0, 90)
    $tile.AddArc(0, $max - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $bg = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 28, 30, 36))
    $g.FillPath($bg, $tile)

    # Lens: left half solid, right half outlined, so it reads as a reflection
    $lens = $size * 0.62
    $x = ($size - $lens) / 2
    $y = ($size - $lens) / 2
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $g.FillPie($white, $x, $y, $lens, $lens, 90, 180)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(1.5, $size * 0.055))
    $g.DrawArc($pen, $x, $y, $lens, $lens, 270, 180)

    # Mirror axis
    $axis = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 196, 70), [Math]::Max(1.5, $size * 0.045))
    $g.DrawLine($axis, $size / 2, $size * 0.12, $size / 2, $size * 0.88)

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

$out = Join-Path $PSScriptRoot 'Mirror.ico'
$fs = [System.IO.File]::Create($out)
$w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Dispose()
Write-Output "wrote $out"
