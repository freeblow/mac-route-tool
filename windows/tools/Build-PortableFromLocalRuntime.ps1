# 生成“便携版”：把这台机器上已经装好的 .NET 8 运行时 + SRM 一起打包成一个文件夹，
# 拷到别的电脑上双击「SRM 网络助手.exe」就能用：不用联网、不用装 .NET、不用改系统设置。
#
# 用法：双击 tools\生成便携版.cmd，或者
#       powershell -ExecutionPolicy Bypass -File tools\Build-PortableFromLocalRuntime.ps1
#
# 产物（默认 dist-portable\）：
#   SRM 网络助手.exe   启动器（带图标；把程序指到自带的 runtime 上，目标电脑不用装 .NET）
#   app\               程序本体（和 dist\ 里一样的那几个文件）
#   runtime\           自带的 .NET 8 运行时（约 160 MB，从本机已装的运行时复制，不需要联网）
#   使用说明.txt        给拿到这个文件夹的人看的说明（来自 tools\PortableReadme.txt）
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist-portable'),
    [string]$DotNetRoot = (Join-Path $env:ProgramFiles 'dotnet')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

# 1) 找本机已装的 .NET 8 运行时版本（CoreLib 和 WPF 那一套各要一份）
function Get-NewestFramework($name) {
    $dir = Join-Path $DotNetRoot "shared\$name"
    if (-not (Test-Path $dir)) { throw "这台机器上找不到 $name：$dir" }
    $version = Get-ChildItem $dir -Directory | Where-Object { $_.Name -like '8.*' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $version) { throw "这台机器上没装 .NET 8 的 $name：先装一次 .NET 8 桌面运行时。" }
    return $version.Name
}

$coreVersion = Get-NewestFramework 'Microsoft.NETCore.App'
$desktopVersion = Get-NewestFramework 'Microsoft.WindowsDesktop.App'
Say "本机 .NET 8 运行时：CoreLib $coreVersion / 桌面 $desktopVersion" 'DarkGray'

# 2) 拷运行时：host\fxr（宿主）+ 两个 shared 框架
$runtimeDir = Join-Path $OutDir 'runtime'
New-Item -ItemType Directory -Force -Path (Join-Path $runtimeDir 'host\fxr'),
    (Join-Path $runtimeDir 'shared\Microsoft.NETCore.App'),
    (Join-Path $runtimeDir 'shared\Microsoft.WindowsDesktop.App') | Out-Null
Copy-Item (Join-Path $DotNetRoot 'host\fxr\*') (Join-Path $runtimeDir 'host\fxr') -Recurse -Force
Copy-Item (Join-Path $DotNetRoot "shared\Microsoft.NETCore.App\$coreVersion") (Join-Path $runtimeDir 'shared\Microsoft.NETCore.App') -Recurse -Force
Copy-Item (Join-Path $DotNetRoot "shared\Microsoft.WindowsDesktop.App\$desktopVersion") (Join-Path $runtimeDir 'shared\Microsoft.WindowsDesktop.App') -Recurse -Force
$runtimeMb = [math]::Round((Get-ChildItem $runtimeDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Say "运行时已拷贝：$runtimeMb MB" 'DarkGray'

# 3) 发布程序本体（框架依赖版，约 660 KB，不需要联网）
$appDir = Join-Path $OutDir 'app'
Say '正在生成 app\（程序本体）…' 'Cyan'
& dotnet publish (Join-Path $PSScriptRoot '..\src\SRM.App') -c Release -o $appDir --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 4) 编译启动器：用 Windows 自带的 csc.exe（.NET Framework），不需要 SDK、不需要联网
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 Windows 自带的编译器：$csc" }
$icon = Join-Path $PSScriptRoot '..\src\SRM.App\SRM.ico'
$launcher = Join-Path $OutDir 'SRM 网络助手.exe'
Say '正在生成启动器…' 'Cyan'
& $csc /nologo /target:winexe /optimize+ /out:"$launcher" /win32icon:"$icon" `
    /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'PortableLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw "编译启动器失败（退出码 $LASTEXITCODE）" }

# 5) 使用说明（给拿到这个文件夹的人看）
Copy-Item (Join-Path $PSScriptRoot 'PortableReadme.txt') (Join-Path $OutDir '使用说明.txt') -Force

# 6) 报告
$outFull = (Resolve-Path $OutDir).Path
$totalMb = [math]::Round((Get-ChildItem $OutDir -File -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Say "完成：$outFull（整个文件夹约 $totalMb MB）" 'Green'
Say '入口是里面的「SRM 网络助手.exe」：整个文件夹拷到别的电脑上双击就能用，不用装 .NET。' 'Green'
