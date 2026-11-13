# 生成“不依赖 .NET”的单文件版 SRM.exe：拷到别的电脑上双击就能用，不用装 .NET 运行时
# 用法：双击 tools\生成免安装版.cmd（推荐），或者：
#       powershell -ExecutionPolicy Bypass -File tools\Publish-Standalone.ps1
#
# 说明：
#   · 第一次运行需要联网（要从 NuGet 下载 .NET 8 的运行时包，约 90 MB；下载后会缓存，之后不用再下）。
#   · 脚本会自己找可用的下载源：先试 nuget.org，连不上就换国内镜像（华为云 / 腾讯云 / 中科大），
#     也可以自己指定：-Source https://公司内网镜像/v3/index.json
#   · 生成结果在 dist-standalone\SRM.exe，约 70 MB，双击即用；生成过程不需要管理员权限。
#   · 默认的 dist\SRM.exe 是“框架依赖”版（只有 600 KB），目标电脑要先装 .NET 8 桌面运行时。
#   · 加 -KeepFolder 可以生成“一堆文件的文件夹”版本（不打包成单文件），排查启动问题时用。
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist-standalone'),
    [string]$Source = '',
    [switch]$KeepFolder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

# 1) 有些环境会留下指向 127.0.0.1 的代理变量（本机根本没有代理在跑），它会让 NuGet 完全上不了网
foreach ($name in 'HTTP_PROXY', 'HTTPS_PROXY', 'http_proxy', 'https_proxy', 'ALL_PROXY', 'all_proxy') {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value -and $value -match '127\.0\.0\.1|localhost') {
        Remove-Item "Env:\$name" -ErrorAction SilentlyContinue
        Say "已忽略无效的代理设置：$name=$value" 'Yellow'
    }
}

# 2) 项目检查
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\src\SRM.App')).Path
if (-not (Test-Path (Join-Path $project 'SRM.App.csproj'))) { throw "找不到项目：$project" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '没有找到 dotnet 命令：先装一次 .NET 8 SDK。' }

# 3) 挑一个能连上的下载源（本机 NuGet 源常被公司策略清空，所以这里自己指定）
$candidates = @()
if ($Source.Length -gt 0) { $candidates += [pscustomobject]@{ Name = '指定源'; Url = $Source } }
$candidates += [pscustomobject]@{ Name = 'nuget.org'; Url = 'https://api.nuget.org/v3/index.json' }
$candidates += [pscustomobject]@{ Name = '华为云镜像'; Url = 'https://mirrors.huaweicloud.com/repository/nuget/v3/index.json' }
$candidates += [pscustomobject]@{ Name = '腾讯云镜像'; Url = 'https://mirrors.cloud.tencent.com/nuget/' }
$candidates += [pscustomobject]@{ Name = '中科大镜像'; Url = 'https://mirrors.ustc.edu.cn/nuget/' }

$usable = @()
foreach ($c in $candidates) {
    try {
        $null = Invoke-WebRequest -Uri $c.Url -TimeoutSec 10 -UseBasicParsing
        Say "可以连上：$($c.Name)（$($c.Url)）" 'DarkGray'
        $usable += $c.Url
    } catch {
        Say "连不上：$($c.Name)（$($c.Url)）" 'DarkGray'
    }
}
if ($usable.Count -eq 0) {
    $usable = $candidates | ForEach-Object { $_.Url }
    Say '所有下载源都没探测通，还是挨个试一遍（也可能是需要代理）…' 'Yellow'
}

# 4) 先还原（下载运行时包），再用已经还原好的东西发布，不重复走网络
$restored = $false
for ($i = 0; $i -lt $usable.Count; $i++) {
    $url = $usable[$i]
    Say "[$($i + 1)/$($usable.Count)] 正在下载运行时包（源：$url）…" 'Cyan'
    & dotnet restore $project -r win-x64 --source $url --nologo -v:m
    if ($LASTEXITCODE -eq 0) { $restored = $true; break }
    Say "这个源不行，换下一个。" 'Yellow'
}
if (-not $restored) {
    throw '运行时包没能下载下来。两条路：① 连得上外网或内网镜像时，指定源重试：生成免安装版.cmd -Source https://你的镜像/v3/index.json；② 不想折腾网络：双击 tools\生成便携版.cmd，它直接用本机已经装好的 .NET 8 打包，全程不用联网。'
}

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '-o', $OutDir,
    '--nologo', '-v:m'
)
if (-not $KeepFolder) {
    $publishArgs += '-p:PublishSingleFile=true'
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
}

Say '正在打包免安装版…' 'Cyan'
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）：如果提示找不到包，多半是没联网，改用 tools\生成便携版.cmd 就可以（不用联网）。" }

$exe = Join-Path $OutDir 'SRM.exe'
if (Test-Path $exe) {
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Say "完成：$exe（$mb MB）—— 拷贝到别的电脑就能用，不用装 .NET。" 'Green'
} else {
    Say "完成，输出目录：$OutDir" 'Green'
}
