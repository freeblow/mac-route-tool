# 把 macOS 的图标（.icns）转成 SRM 用的多尺寸 .ico + 256px 预览图
# 用法：powershell -ExecutionPolicy Bypass -File tools\Import-IcnsIcon.ps1
# 默认读 src\SRM.App\SRM.icns，输出 src\SRM.App\SRM.ico 和 docs\preview-icon.png
param(
    [string]$InIcns = (Join-Path $PSScriptRoot '..\src\SRM.App\SRM.icns'),
    [string]$OutIcon = (Join-Path $PSScriptRoot '..\src\SRM.App\SRM.ico'),
    [string]$OutPreview = (Join-Path $PSScriptRoot '..\docs\preview-icon.png')
)

Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function Read-U32BE([byte[]]$data, [int]$offset) {
    $value = [uint32]0
    for ($i = 0; $i -lt 4; $i++) { $value = $value * 256 + $data[$offset + $i] }
    return $value
}

# 1) 把 .icns 里内嵌的图取出来（ic07 ~ ic14 存的都是 PNG）
$bytes = [System.IO.File]::ReadAllBytes($InIcns)
if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'icns') { throw "$InIcns 不像是 icns 文件。" }
$total = Read-U32BE $bytes 4
$frames = @()
$streams = @()
$position = 8
while ($position -lt $total -and $position + 8 -le $bytes.Length) {
    $length = Read-U32BE $bytes ($position + 4)
    if ($length -lt 8) { break }
    $start = $position + 8
    $end = [Math]::Min($position + $length, $bytes.Length)
    if (($end - $start) -gt 8 -and $bytes[$start] -eq 0x89 -and $bytes[$start + 1] -eq 0x50) {
        $data = New-Object byte[] ($end - $start)
        [Array]::Copy($bytes, $start, $data, 0, $data.Length)
        $stream = [System.IO.MemoryStream]::new($data, $false)
        $streams += $stream
        $image = [System.Drawing.Image]::FromStream($stream)
        $frames += [pscustomobject]@{ Width = $image.Width; Height = $image.Height; Image = $image }
    }
    $position = $end
}
if ($frames.Count -eq 0) { throw "$InIcns 里没找到能用的图。" }
$master = ($frames | Sort-Object Width -Descending | Select-Object -First 1).Image
Write-Host ("读到 {0} 张图，最大 {1}x{1}" -f $frames.Count, $master.Width)

# 每个尺寸优先用 icns 里现成的那张（更清晰），没有就从比它大的里缩
function Get-Source([int]$size) {
    $exact = $frames | Where-Object { $_.Width -eq $size } | Select-Object -First 1
    if ($exact) { return $exact.Image }
    $bigger = $frames | Where-Object { $_.Width -gt $size } | Sort-Object Width | Select-Object -First 1
    if ($bigger) { return $bigger.Image }
    return $master
}

# 2) 逐个尺寸重绘（保住透明背景），存成 PNG
$blobs = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $g.DrawImage((Get-Source $size), $rect)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    if ($size -eq 256) { $bmp.Save($OutPreview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms.Dispose()
    $bmp.Dispose()
}

# 3) 拼成 .ico（和 New-SrmIcon.ps1 一样，每张都存 PNG，256 的宽高写 0）
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

foreach ($stream in $streams) { $stream.Dispose() }
Write-Host "已生成图标：$OutIcon（含 $($blobs.Count) 个尺寸）"
Write-Host "预览图：$OutPreview"