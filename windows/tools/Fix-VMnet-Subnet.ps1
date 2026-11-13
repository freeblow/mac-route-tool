<#
  SRM 小工具：修复「虚拟机网段和真机网段撞车」
  ------------------------------------------------------------
  适用情况：VMware 的虚拟网卡（VMnet1 / VMnet8）网段，和你的真机网络
  （例如 Wi-Fi 的 192.168.15.0/24）落在同一个网段上。撞车之后，
  本机和虚拟机都会分不清某个 192.168.15.x 的地址该走虚拟机还是真网卡。

  它做的三件事（和 VMware「虚拟网络编辑器」里改子网完全一样）：
    1) 改注册表里该虚拟网段的记录：
       HKLM\SOFTWARE\VMware, Inc.\VMnetLib\VMnetConfig\<vmnet>\IPSubnetAddress
    2) 改 VMware 自带的 DHCP 配置 C:\ProgramData\VMware\vmnetdhcp.conf
       里对应网段的那一段（改前自动备份）
    3) 重启 VMware 的 VMnetDHCP 服务，并把该虚拟网卡禁用/启用一次，
       让它拿到新网段里的地址（一般是 x.x.x.1）

  需要管理员权限，运行时脚本会自己弹 UAC 再开一个窗口。
  运行前请关闭所有正在运行的虚拟机；跑完在虚拟机里重新获取一次 IP。

  用法：
    powershell -ExecutionPolicy Bypass -File .\Fix-VMnet-Subnet.ps1
    powershell -ExecutionPolicy Bypass -File .\Fix-VMnet-Subnet.ps1 -Vmnet vmnet1 -Subnet 192.168.155.0
    加 -Yes 跳过确认（例如放进批处理里）。
#>
param(
    [string]$Vmnet = 'vmnet1',
    [string]$Subnet = '',
    [string]$Mask = '255.255.255.0',
    [switch]$Yes,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Write-Head([string]$text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }
function Write-Step([string]$text) { Write-Host ('  · ' + $text) }
function Write-Warn([string]$text) { Write-Host ('  ! ' + $text) -ForegroundColor Yellow }

function ConvertTo-Number([string]$ip) {
    $bytes = [System.Net.IPAddress]::Parse($ip).GetAddressBytes()
    [uint64]$value = 0
    foreach ($b in $bytes) { $value = ($value * 256) + $b }
    $value
}

function Get-MaskNumber([int]$prefix) {
    if ($prefix -le 0) { [uint64]0 } else { ([uint64]4294967295 -shl (32 - $prefix)) -band 4294967295 }
}

function Get-PrefixLength([string]$mask) {
    $bytes = [System.Net.IPAddress]::Parse($mask).GetAddressBytes()
    $bits = 0
    foreach ($b in $bytes) {
        for ($k = 7; $k -ge 0; $k--) {
            if ($b -band (1 -shl $k)) { $bits++ } else { return $bits }
        }
    }
    $bits
}

function Get-NetworkText([string]$ip, [string]$mask) {
    $value = (ConvertTo-Number $ip) -band (ConvertTo-Number $mask)
    $bytes = New-Object byte[] 4
    for ($k = 3; $k -ge 0; $k--) { $bytes[$k] = [byte]($value % 256); $value = [math]::Floor($value / 256) }
    ([System.Net.IPAddress]::new([byte[]]$bytes)).ToString()
}

function Test-SameSubnet([string]$a, [string]$b, [string]$aMask, [string]$bMask) {
    $aPrefix = Get-PrefixLength $aMask
    $bPrefix = Get-PrefixLength $bMask
    $bits = [Math]::Min($aPrefix, $bPrefix)
    $maskNumber = Get-MaskNumber $bits
    (((ConvertTo-Number $a) -band $maskNumber) -eq ((ConvertTo-Number $b) -band $maskNumber))
}
# ================= 1. 管理员权限 =================
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and -not $DryRun) {
    Write-Host '需要管理员权限（要改 VMware 的虚拟网络配置），正在重新以管理员身份打开…' -ForegroundColor Yellow
    $parts = @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-Vmnet', $Vmnet, '-Mask', $Mask)
    if ($Subnet) { $parts += @('-Subnet', $Subnet) }
    if ($Yes) { $parts += '-Yes' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ($parts -join ' ') | Out-Null
    exit 0
}

# ================= 2. 先看看系统里的网段 =================
Write-Host 'SRM 小工具：修复虚拟机网段撞车' -ForegroundColor Green
Write-Head '第 1 步：检查当前的网段'

if ((Get-PrefixLength $Mask) -ne 24) { throw '这个脚本目前只处理 /24 的虚拟网段（掩码 255.255.255.0）。' }
if (Get-Process 'vmware-vmx', 'vmware-vmx-debug', 'vmware-vmx-stats' -ErrorAction SilentlyContinue) {
    Write-Warn '检测到虚拟机正在运行，请先把虚拟机全部关机，再运行本脚本。'
    exit 1
}

$interfaceRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces'
$deviceRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}'
$nameMap = @{}
foreach ($key in Get-ChildItem $deviceRoot -ErrorAction SilentlyContinue) {
    $friendly = (Get-ItemProperty (Join-Path $key.PSPath 'Connection') -ErrorAction SilentlyContinue).Name
    if ($friendly) { $nameMap[$key.PSChildName] = $friendly }
}

$realSubnets = New-Object System.Collections.Generic.List[object]
$vmSubnets = New-Object System.Collections.Generic.List[object]
foreach ($key in Get-ChildItem $interfaceRoot -ErrorAction SilentlyContinue) {
    $name = $nameMap[$key.PSChildName]
    if (-not $name) { continue }
    $values = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
    if (-not $values) { continue }
    $ip = @($values.IPAddress | Where-Object { $_ }) | Select-Object -First 1
    if (-not $ip) { $ip = $values.DhcpIPAddress }
    $mask = @($values.SubnetMask | Where-Object { $_ }) | Select-Object -First 1
    if (-not $mask) { $mask = $values.DhcpSubnetMask }
    if (-not $ip -or -not $mask) { continue }
    if ($ip -like '127.*' -or $ip -like '169.254.*') { continue }
    $entry = [pscustomobject]@{
        Guid    = $key.PSChildName
        Name    = $name
        Ip      = $ip
        Mask    = $mask
        Network = (Get-NetworkText $ip $mask)
        Prefix  = (Get-PrefixLength $mask)
    }
    if ($name -like 'VMware*') { $vmSubnets.Add($entry) } else { $realSubnets.Add($entry) }
}

foreach ($item in $realSubnets) { Write-Step ('真机网段：{0}/{1}  （{2}，本机 {3}）' -f $item.Network, $item.Prefix, $item.Name, $item.Ip) }
foreach ($item in $vmSubnets) { Write-Step ('虚拟网段：{0}/{1}  （{2}，本机 {3}）' -f $item.Network, $item.Prefix, $item.Name, $item.Ip) }

$segment = [int]($Vmnet -replace '[^0-9]', '')
$vmConfig = "HKLM:\SOFTWARE\VMware, Inc.\VMnetLib\VMnetConfig\$Vmnet"
if (-not (Test-Path $vmConfig)) { throw "没找到 VMware 的虚拟网段配置 $vmConfig，是不是没装 VMware Workstation？" }
$currentSubnet = (Get-ItemProperty $vmConfig).IPSubnetAddress
$vmAdapter = $vmSubnets | Where-Object { $_.Name -match ('VMnet' + $segment + '$') } | Select-Object -First 1
$currentMask = if ($vmAdapter) { $vmAdapter.Mask } else { '255.255.255.0' }

$clashes = @($realSubnets | Where-Object { Test-SameSubnet $currentSubnet $_.Ip $currentMask $_.Mask })
if ($clashes.Count -eq 0 -and -not $Subnet) {
    Write-Head '没有发现撞车'
    Write-Step ("$Vmnet 现在是 $currentSubnet/24，和现有真机网段都不冲突，不需要改。")
    Write-Step '如果一定要换网段，可以加参数指定，例如：-Subnet 192.168.155.0'
    exit 0
}
foreach ($clash in $clashes) { Write-Warn ("撞车：$Vmnet 的 $currentSubnet/24 和「$($clash.Name)」的 $($clash.Network)/$($clash.Prefix) 是同一个网段。") }
# ================= 3. 挑一个不冲突的新网段 =================
if (-not $Subnet) {
    foreach ($candidate in @('192.168.155.0', '192.168.160.0', '192.168.177.0')) {
        $bad = $false
        foreach ($real in $realSubnets) { if (Test-SameSubnet $candidate $real.Ip $Mask $real.Mask) { $bad = $true } }
        foreach ($vm in $vmSubnets) { if ($vm.Name -ne $vmAdapter.Name -and (Test-SameSubnet $candidate $vm.Ip $Mask $vm.Mask)) { $bad = $true } }
        if (-not $bad) { $Subnet = $candidate; break }
    }
    if (-not $Subnet) { throw '没有自动找到空闲网段，请用 -Subnet 指定一个（例如 -Subnet 192.168.155.0）。' }
}
foreach ($real in $realSubnets) {
    if (Test-SameSubnet $Subnet $real.Ip $Mask $real.Mask) { throw "指定的 $Subnet 和「$($real.Name)」的 $($real.Network)/$($real.Prefix) 又是同一个网段，请换一个。" }
}
$newHostIp = ($Subnet -replace '\.0$', '.1')

Write-Head '第 2 步：准备这样改'
Write-Step ("$Vmnet 的网段：$currentSubnet/24  →  $Subnet/24")
Write-Step ("这块虚拟网卡（$($vmAdapter.Name)）会从 " + $vmAdapter.Ip + " 变成 $newHostIp")
Write-Step '会重启 VMware 的 VMnetDHCP 服务，并把这块虚拟网卡禁用/启用一次（几秒钟）'
if ($DryRun) {
    Write-Host ''
    Write-Host '（试运行：上面这些改动不会真的执行）' -ForegroundColor Yellow
    exit 0
}
if (-not $Yes) {
    $answer = Read-Host '回车开始改，输入 n 取消'
    if ($answer -match '^[nN]') { Write-Host '已取消，什么都没改。'; exit 0 }
}

# ================= 4. 动手改 =================
Write-Head '第 3 步：正在改'
$confPath = 'C:\ProgramData\VMware\vmnetdhcp.conf'
if (-not (Test-Path $confPath)) { throw "没找到 VMware 的 DHCP 配置文件：$confPath" }
$oldPrefix = $currentSubnet -replace '\.0$', '.'
$newPrefix = $Subnet -replace '\.0$', '.'
$result = New-Object System.Collections.Generic.List[string]
$inside = $false
$touched = 0
foreach ($line in (Get-Content -LiteralPath $confPath)) {
    if ($line -match '^#\s*Virtual ethernet segment\s+(\d+)\s*$') { $inside = ([int]$Matches[1] -eq $segment); $result.Add($line); continue }
    if ($inside -and $line -match '^#\s*End\s*$') { $inside = $false; $result.Add($line); continue }
    if ($inside -and $line.Contains($oldPrefix)) { $result.Add($line.Replace($oldPrefix, $newPrefix)); $touched++; continue }
    $result.Add($line)
}
if ($touched -eq 0) { throw "没在 vmnetdhcp.conf 里找到 $Vmnet 的网段配置（$oldPrefix），请改用 VMware 的「虚拟网络编辑器」手动改。" }
$backup = $confPath + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $confPath -Destination $backup -Force
Write-Step ("已备份原配置：$backup")
Set-Content -LiteralPath $confPath -Value $result -Encoding ascii
Write-Step ("已更新 $confPath（改了 $touched 行）。")

Set-ItemProperty -Path $vmConfig -Name 'IPSubnetAddress' -Value $Subnet
Write-Step "已把注册表里的虚拟网段改成 $Subnet。"

if (Get-Service -Name 'VMnetDHCP' -ErrorAction SilentlyContinue) {
    Restart-Service -Name 'VMnetDHCP' -Force
    Write-Step 'VMnetDHCP 服务已重启。'
} else {
    Write-Warn '没找到 VMnetDHCP 服务，跳过重启（这块虚拟网可能没开 DHCP）。'
}

$adapterName = $null
if ($vmAdapter) {
    $adapterName = $vmAdapter.Name
    netsh interface set interface name="$adapterName" admin=disabled | Out-Null
    Start-Sleep -Seconds 2
    netsh interface set interface name="$adapterName" admin=enabled | Out-Null
    Write-Step ('已把「' + $adapterName + '」禁用再启用，等它拿新地址…')
    Start-Sleep -Seconds 12
}

# ================= 5. 检查结果 =================
Write-Head '第 4 步：检查结果'
$nowIp = ''
if ($vmAdapter) {
    $values = Get-ItemProperty ("HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + $vmAdapter.Guid) -ErrorAction SilentlyContinue
    if ($values) { $nowIp = $values.DhcpIPAddress }
}
if ($nowIp -and $nowIp.StartsWith($newPrefix)) {
    Write-Host ('  成功：' + $adapterName + ' 现在是 ' + $nowIp + '，已经和新网段对上。') -ForegroundColor Green
    Write-Host ''
    Write-Host '接下来：'
    Write-Host ('  · 虚拟机里如果是自动获取 IP，重启一下虚拟机（或在虚拟机里重连网络）就会拿到 ' + $newPrefix + 'x 的地址；')
    Write-Host ('  · 虚拟机里如果是手动填的 ' + $oldPrefix + 'x，请把它改到新网段里。')
    exit 0
}
Write-Warn ("虚拟网卡现在读到的还是 $nowIp，可能还没刷新完。")
Write-Warn '等十几秒再看「网络连接」里这块网卡的地址；如果仍是老地址，重启一次电脑即可。'
exit 1
