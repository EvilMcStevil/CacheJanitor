<#
.SYNOPSIS
  Draws assets\CacheJanitor.ico: a rounded tile holding a stack of cache layers, the oldest one fading out.
#>
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(2, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, $s)), ([System.Drawing.Color]::FromArgb(255, 20, 136, 140)), ([System.Drawing.Color]::FromArgb(255, 12, 86, 110))
    $g.FillPath($bg, $path)
    $pad = $s * 0.2; $barH = $s * 0.13; $gap = $s * 0.07
    $alphas = 255, 255, 230, 110
    for ($i = 0; $i -lt 4; $i++) {
        $y = $pad + $i * ($barH + $gap)
        $len = ($s - 2 * $pad) * (1 - 0.12 * $i)
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($alphas[$i], 255, 255, 255))
        $g.FillRectangle($brush, [single]$pad, [single]$y, [single]$len, [single][Math]::Max(1, $barH))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\assets\CacheJanitor.ico'), $out.ToArray())
