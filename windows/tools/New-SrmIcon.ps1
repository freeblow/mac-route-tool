# 生成 SRM 的程序图标（多尺寸 .ico + 256px 预览图）
# 用法：powershell -ExecutionPolicy Bypass -File tools\New-SrmIcon.ps1
param(
    [string]$OutIcon = (Join-Path $PSScriptRoot '..\src\SRM.App\SRM.ico'),
    [string]$OutPreview = (Join-Path $PSScriptRoot '..\docs\preview-icon.png')
)

Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function New-GlyphBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size
    $radius = 0.225 * $s
    $d = [single](2 * $radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]0, [single]0, $d, $d, 180, 90)
    $path.AddArc([single]($s - $d), [single]0, $d, $d, 270, 90)
    $path.AddArc([single]($s - $d), [single]($s - $d), $d, $d, 0, 90)
    $path.AddArc([single]0, [single]($s - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF(0, 0, [single]$s, [single]$s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 76, 141, 255),
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216), 55.0)
    $g.FillPath($grad, $path)

    $glow = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(64, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255), 90.0)
    $g.FillPath($glow, $path)

    $w = [single](0.077 * $s)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ax = [single](0.255 * $s)
    $ay = [single](0.295 * $s)
    $bx = [single](0.255 * $s)
    $by = [single](0.705 * $s)
    $jx = [single](0.53 * $s)
    $cx = [single](0.755 * $s)
    $cy = [single](0.5 * $s)
    $g.DrawLine($pen, $ax, $ay, $jx, $ay)
    $g.DrawLine($pen, $bx, $by, $jx, $by)
    $g.DrawLine($pen, $jx, $ay, $jx, $by)
    $g.DrawLine($pen, $jx, $cy, $cx, $cy)

    $ra = [single](0.086 * $s)
    $rc = [single](0.1 * $s)
    $g.FillEllipse([System.Drawing.Brushes]::White, $ax - $ra, $ay - $ra, 2 * $ra, 2 * $ra)
    $g.FillEllipse([System.Drawing.Brushes]::White, $bx - $ra, $by - $ra, 2 * $ra, 2 * $ra)
    $g.FillEllipse([System.Drawing.Brushes]::White, $cx - $rc, $cy - $rc, 2 * $rc, 2 * $rc)

    $pen.Dispose()
    $grad.Dispose()
    $glow.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}
$blobs = @()
foreach ($size in $sizes) {
    $bmp = New-GlyphBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    if ($size -eq 256) { $bmp.Save($OutPreview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms.Dispose()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($OutIcon)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$blobs.Count)
$offset = 6 + 16 * $blobs.Count
foreach ($blob in $blobs) {
    $dim = [byte]$(if ($blob.Size -ge 256) { 0 } else { $blob.Size })
    $bw.Write($dim)
    $bw.Write($dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$blob.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $blob.Bytes.Length
}
foreach ($blob in $blobs) { $bw.Write($blob.Bytes) }
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host "已生成图标：$OutIcon（含 $($blobs.Count) 个尺寸）"
Write-Host "预览图：$OutPreview"
