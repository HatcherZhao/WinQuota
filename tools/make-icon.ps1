Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 256)
$pngs = @()

function Draw-Png([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(28, 108, 219))
    $margin = [Math]::Max(1, [int]($s / 32))
    $g.FillEllipse($bg, $margin, $margin, $s - 2 * $margin, $s - 2 * $margin)
    $fg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $fontSize = $s * 0.58
    $font = New-Object System.Drawing.Font('Segoe UI', [float]$fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $text = 'W'
    $sz = $g.MeasureString($text, $font)
    $x = ($s - $sz.Width) / 2
    $y = ($s - $sz.Height) / 2 - $s * 0.02
    $g.DrawString($text, $font, $fg, $x, $y)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $font.Dispose(); $bg.Dispose(); $fg.Dispose()
    return ,$ms.ToArray()
}

foreach ($s in $sizes) { $pngs += ,(Draw-Png $s) }

# ICO 容器：ICONDIR + ICONDIRENTRY(每尺寸一个, PNG 编码) + 数据
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)       # reserved
$bw.Write([UInt16]1)       # type = icon
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)  # width
    $bw.Write([Byte]$dim)  # height
    $bw.Write([Byte]0)     # palette
    $bw.Write([Byte]0)     # reserved
    $bw.Write([UInt16]1)   # planes
    $bw.Write([UInt16]32)  # bitcount
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush()

[IO.File]::WriteAllBytes('D:\_workspace\projects\WinQuota\src\WinQuota.Tray\app.ico', $out.ToArray())
[IO.File]::WriteAllBytes('D:\_workspace\projects\WinQuota\src\WinQuota.Service\app.ico', $out.ToArray())
Write-Host ("app.ico written: {0} bytes x2" -f $out.Length)
