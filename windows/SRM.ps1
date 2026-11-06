# Windows 10/11, Windows PowerShell 5.1. Double-click SRM.cmd to launch.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $arguments = '-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $PSCommandPath
        Start-Process -FilePath "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $arguments -Verb RunAs
    } catch {
        [System.Windows.Forms.MessageBox]::Show('SRM 需要管理员权限才能修改系统路由。', 'SRM') | Out-Null
    }
    exit
}

$configDirectory = Join-Path $env:ProgramData 'SRM'
$configPath = Join-Path $configDirectory 'routes.json'
$script:routes = @()
$script:probeResults = @{}
$script:pendingProbes = @{}
$script:routeErrors = @{}
$script:lastSync = [datetime]::MinValue

function Test-IPv4([string]$address) {
    if ($address -notmatch '^(\d{1,3}\.){3}\d{1,3}$') { return $false }
    foreach ($part in $address.Split('.')) {
        if ([int]$part -gt 255) { return $false }
    }
    return $true
}

function Convert-Destination([string]$inputText) {
    $value = $inputText.Trim()
    if ($value -eq 'default') { return '0.0.0.0/0' }
    $parts = $value.Split('/')
    if ($parts.Count -gt 2 -or -not (Test-IPv4 $parts[0])) { throw '目标地址应为 IPv4 或 CIDR，例如 10.40.85.0/24。' }
    $prefix = 32
    if ($parts.Count -eq 2) {
        if ($parts[1] -notmatch '^\d{1,2}$') { throw '掩码长度应在 0 到 32 之间。' }
        $prefix = [int]$parts[1]
        if ($prefix -gt 32) { throw '掩码长度应在 0 到 32 之间。' }
    }
    $octets = @($parts[0].Split('.') | ForEach-Object { [int]$_ })
    [uint64]$address = 0
    foreach ($octet in $octets) { $address = ($address -shl 8) -bor [uint64]$octet }
    [uint64]$mask = if ($prefix -eq 0) { 0 } else { ([uint64]4294967295 -shl (32 - $prefix)) -band [uint64]4294967295 }
    [uint64]$network = $address -band $mask
    $normalized = '{0}.{1}.{2}.{3}' -f (($network -shr 24) -band 255), (($network -shr 16) -band 255), (($network -shr 8) -band 255), ($network -band 255)
    return "$normalized/$prefix"
}

function Get-RouteParts($route) {
    $parts = $route.destination.Split('/')
    $prefix = [int]$parts[1]
    [uint64]$mask = if ($prefix -eq 0) { 0 } else { ([uint64]4294967295 -shl (32 - $prefix)) -band [uint64]4294967295 }
    $maskText = '{0}.{1}.{2}.{3}' -f (($mask -shr 24) -band 255), (($mask -shr 16) -band 255), (($mask -shr 8) -band 255), ($mask -band 255)
    return @{ destination = $parts[0]; mask = $maskText }
}

function Save-Routes {
    [System.IO.Directory]::CreateDirectory($configDirectory) | Out-Null
    $json = ConvertTo-Json -InputObject @{ version = 1; routes = @($script:routes) } -Depth 5
    $tempPath = "$configPath.tmp"
    [System.IO.File]::WriteAllText($tempPath, $json, [System.Text.UTF8Encoding]::new($false))
    if (Test-Path $configPath) { [System.IO.File]::Delete($configPath) }
    [System.IO.File]::Move($tempPath, $configPath)
}

function Load-Routes {
    if (-not (Test-Path $configPath)) { return }
    $data = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $data.routes) { $script:routes = @($data.routes) }
}

function Get-MatchingRoute($route, [string]$store) {
    $routeMatches = @(Get-NetRoute -AddressFamily IPv4 -PolicyStore $store -DestinationPrefix $route.destination -ErrorAction SilentlyContinue)
    foreach ($item in $routeMatches) {
        if ($item.NextHop -ne $route.gateway) { continue }
        if ($route.interfaceIndex -and [int]$item.InterfaceIndex -ne [int]$route.interfaceIndex) { continue }
        return $item
    }
    return $null
}

function Remove-ManagedRoute($route) {
    foreach ($store in @('ActiveStore', 'PersistentStore')) {
        $match = Get-MatchingRoute $route $store
        if ($null -ne $match) {
            $match | Remove-NetRoute -Confirm:$false -ErrorAction Stop
        }
    }
}

function Ensure-Route($route) {
    if (-not $route.enabled) { return }
    $active = Get-MatchingRoute $route 'ActiveStore'
    $persistent = Get-MatchingRoute $route 'PersistentStore'
    if ($null -ne $active -and $null -ne $persistent) {
        $script:routeErrors.Remove([string]$route.id)
        return
    }
    if ($null -ne $active) {
        New-NetRoute -DestinationPrefix $route.destination -NextHop $route.gateway -InterfaceIndex $active.InterfaceIndex -PolicyStore PersistentStore -ErrorAction Stop | Out-Null
        $script:routeErrors.Remove([string]$route.id)
        return
    }
    $parts = Get-RouteParts $route
    $arguments = if ($null -ne $persistent) { @('add', $parts.destination, 'mask', $parts.mask, $route.gateway) } else { @('-p', 'add', $parts.destination, 'mask', $parts.mask, $route.gateway) }
    if ($route.interfaceIndex) { $arguments += @('IF', [string]$route.interfaceIndex) }
    $output = & "$env:WINDIR\System32\route.exe" @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $script:routeErrors[[string]$route.id] = (($output | Out-String).Trim())
    } else {
        $script:routeErrors.Remove([string]$route.id)
    }
}

function Sync-Routes {
    foreach ($route in $script:routes) {
        try { Ensure-Route $route } catch { $script:routeErrors[[string]$route.id] = $_.Exception.Message }
    }
    $script:lastSync = Get-Date
}

function Get-ProbeTarget($route) {
    if ($route.destination.EndsWith('/32')) { return @{ address = $route.destination.Split('/')[0]; label = '目标' } }
    return @{ address = [string]$route.gateway; label = '网关' }
}

function Start-Probe($route) {
    $id = [string]$route.id
    if ($script:pendingProbes.ContainsKey($id)) { return }
    $target = Get-ProbeTarget $route
    $ping = New-Object System.Net.NetworkInformation.Ping
    try {
        $task = $ping.SendPingAsync([System.Net.IPAddress]::Parse($target.address), 2000)
        $script:pendingProbes[$id] = @{ task = $task; ping = $ping; target = $target }
        $script:probeResults[$id] = "正在测试$($target.label) $($target.address)…"
    } catch {
        $ping.Dispose()
        $script:probeResults[$id] = "测试失败：$($_.Exception.Message)"
    }
}

function Update-Probes {
    foreach ($id in @($script:pendingProbes.Keys)) {
        $pending = $script:pendingProbes[$id]
        if (-not $pending.task.IsCompleted) { continue }
        try {
            $reply = $pending.task.GetAwaiter().GetResult()
            if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
                $script:probeResults[$id] = "$($pending.target.label) $($pending.target.address)：可达 · $($reply.RoundtripTime) ms"
            } else {
                $script:probeResults[$id] = "$($pending.target.label) $($pending.target.address)：未响应（$($reply.Status)，可能禁用 Ping）"
            }
        } catch {
            $script:probeResults[$id] = "$($pending.target.label) $($pending.target.address)：测试失败 · $($_.Exception.Message)"
        } finally {
            $pending.ping.Dispose()
            $script:pendingProbes.Remove($id)
        }
    }
}

function New-Button([string]$text, [int]$x, [int]$width) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $text
    $button.Location = New-Object System.Drawing.Point($x, 12)
    $button.Size = New-Object System.Drawing.Size($width, 30)
    return $button
}

function Show-RouteEditor($existing) {
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = if ($null -eq $existing) { '添加静态路由' } else { '编辑静态路由' }
    $dialog.StartPosition = 'CenterParent'
    $dialog.Size = New-Object System.Drawing.Size(420, 310)
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $fields = @{}
    $labels = @(@('目标（CIDR）', 'destination'), @('网关', 'gateway'), @('接口序号（可留空）', 'interfaceIndex'), @('备注', 'note'))
    for ($i = 0; $i -lt $labels.Count; $i++) {
        $label = New-Object System.Windows.Forms.Label
        $label.Text = $labels[$i][0]
        $label.Location = New-Object System.Drawing.Point(15, (22 + $i * 45))
        $label.Size = New-Object System.Drawing.Size(130, 25)
        $dialog.Controls.Add($label)
        $field = New-Object System.Windows.Forms.TextBox
        $field.Location = New-Object System.Drawing.Point(150, (18 + $i * 45))
        $field.Size = New-Object System.Drawing.Size(245, 25)
        if ($null -ne $existing) { $field.Text = [string]$existing.($labels[$i][1]) }
        $fields[$labels[$i][1]] = $field
        $dialog.Controls.Add($field)
    }
    $enabled = New-Object System.Windows.Forms.CheckBox
    $enabled.Text = '启用（开机后仍有效）'
    $enabled.Location = New-Object System.Drawing.Point(150, 200)
    $enabled.Size = New-Object System.Drawing.Size(220, 25)
    $enabled.Checked = if ($null -eq $existing) { $true } else { [bool]$existing.enabled }
    $dialog.Controls.Add($enabled)
    $save = New-Button '保存' 220 80
    $save.Location = New-Object System.Drawing.Point(220, 230)
    $cancel = New-Button '取消' 310 80
    $cancel.Location = New-Object System.Drawing.Point(310, 230)
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dialog.Controls.Add($save)
    $dialog.Controls.Add($cancel)
    $dialog.CancelButton = $cancel
    $save.Add_Click({
        try {
            $destination = Convert-Destination $fields.destination.Text
            $gateway = $fields.gateway.Text.Trim()
            if (-not (Test-IPv4 $gateway)) { throw '请输入有效的 IPv4 网关。' }
            $index = $null
            if ($fields.interfaceIndex.Text.Trim()) {
                $number = 0
                if (-not [int]::TryParse($fields.interfaceIndex.Text.Trim(), [ref]$number) -or $number -le 0) {
                    throw '接口序号必须是正整数。可在 Windows 网络设置或 route print 中查看。'
                }
                $index = $number
            }
            $dialog.Tag = [pscustomobject]@{
                id = if ($null -eq $existing) { [guid]::NewGuid().ToString() } else { [string]$existing.id }
                destination = $destination
                gateway = $gateway
                interfaceIndex = $index
                enabled = $enabled.Checked
                note = $fields.note.Text.Trim()
            }
            $dialog.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $dialog.Close()
        } catch {
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '输入有误') | Out-Null
        }
    })
    $result = $dialog.ShowDialog($form)
    $value = if ($result -eq [System.Windows.Forms.DialogResult]::OK) { $dialog.Tag } else { $null }
    $dialog.Dispose()
    return $value
}

Load-Routes
$form = New-Object System.Windows.Forms.Form
$form.Text = 'SRM · Windows 静态路由'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(920, 480)
$form.MinimumSize = New-Object System.Drawing.Size(720, 380)

$addButton = New-Button '添加' 12 70
$editButton = New-Button '编辑' 88 70
$toggleButton = New-Button '启用 / 停用' 164 100
$deleteButton = New-Button '删除' 270 70
$syncButton = New-Button '重新同步' 346 100
foreach ($button in @($addButton, $editButton, $toggleButton, $deleteButton, $syncButton)) { $form.Controls.Add($button) }

$grid = New-Object System.Windows.Forms.DataGridView
$grid.Location = New-Object System.Drawing.Point(12, 52)
$grid.Size = New-Object System.Drawing.Size(880, 350)
$grid.Anchor = 'Top,Bottom,Left,Right'
$grid.ReadOnly = $true
$grid.AllowUserToAddRows = $false
$grid.AllowUserToDeleteRows = $false
$grid.MultiSelect = $false
$grid.SelectionMode = 'FullRowSelect'
$grid.RowHeadersVisible = $false
$grid.AutoSizeColumnsMode = 'Fill'
foreach ($item in @(@('状态', 75), @('目标', 145), @('网关', 125), @('接口', 65), @('备注', 120), @('测试结果', 280))) {
    $column = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $column.HeaderText = $item[0]
    $column.FillWeight = [int]$item[1]
    [void]$grid.Columns.Add($column)
}
$testColumn = New-Object System.Windows.Forms.DataGridViewButtonColumn
$testColumn.HeaderText = '连通性'
$testColumn.Text = '测试'
$testColumn.UseColumnTextForButtonValue = $true
$testColumn.FillWeight = 60
[void]$grid.Columns.Add($testColumn)
$form.Controls.Add($grid)

$hint = New-Object System.Windows.Forms.Label
$hint.Text = '绿色表示路由已写入；“测试结果”单独显示 ICMP 响应。网段路由测试网关，主机路由测试目标。'
$hint.Location = New-Object System.Drawing.Point(12, 410)
$hint.Size = New-Object System.Drawing.Size(880, 22)
$hint.Anchor = 'Bottom,Left,Right'
$form.Controls.Add($hint)

function Get-SelectedRoute {
    if ($grid.SelectedRows.Count -eq 0) { return $null }
    return $grid.SelectedRows[0].Tag
}

function Refresh-Grid {
    $selectedID = $null
    $selected = Get-SelectedRoute
    if ($null -ne $selected) { $selectedID = [string]$selected.id }
    $grid.Rows.Clear()
    foreach ($route in $script:routes) {
        $id = [string]$route.id
        $status = if (-not $route.enabled) { '已停用' } elseif ($null -ne (Get-MatchingRoute $route 'ActiveStore')) { '● 生效' } elseif ($script:routeErrors.ContainsKey($id)) { '● 失败' } else { '● 待同步' }
        $response = if ($script:probeResults.ContainsKey($id)) { [string]$script:probeResults[$id] } elseif ($script:routeErrors.ContainsKey($id)) { [string]$script:routeErrors[$id] } else { '尚未测试' }
        $interface = if ($route.interfaceIndex) { [string]$route.interfaceIndex } else { '自动' }
        $rowIndex = $grid.Rows.Add($status, $route.destination, $route.gateway, $interface, $route.note, $response)
        $row = $grid.Rows[$rowIndex]
        $row.Tag = $route
        if ($status -eq '● 生效') { $row.Cells[0].Style.ForeColor = [System.Drawing.Color]::ForestGreen }
        if ($status -eq '● 失败') { $row.Cells[0].Style.ForeColor = [System.Drawing.Color]::Firebrick }
        if ($id -eq $selectedID) { $row.Selected = $true }
    }
}

$addButton.Add_Click({
    $route = Show-RouteEditor $null
    if ($null -eq $route) { return }
    $script:routes += $route
    Save-Routes
    Sync-Routes
    Refresh-Grid
})
$editButton.Add_Click({
    $old = Get-SelectedRoute
    if ($null -eq $old) { return }
    $new = Show-RouteEditor $old
    if ($null -eq $new) { return }
    try {
        if ($old.enabled) { Remove-ManagedRoute $old }
        $script:routes = @($script:routes | ForEach-Object { if ($_.id -eq $old.id) { $new } else { $_ } })
        $script:probeResults.Remove([string]$old.id)
        Save-Routes
        Sync-Routes
        Refresh-Grid
    } catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '编辑失败') | Out-Null }
})
$toggleButton.Add_Click({
    $route = Get-SelectedRoute
    if ($null -eq $route) { return }
    try {
        if ($route.enabled) { Remove-ManagedRoute $route }
        $route.enabled = -not $route.enabled
        Save-Routes
        Sync-Routes
        Refresh-Grid
    } catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '操作失败') | Out-Null }
})
$deleteButton.Add_Click({
    $route = Get-SelectedRoute
    if ($null -eq $route) { return }
    if ([System.Windows.Forms.MessageBox]::Show("删除 $($route.destination)？", '确认删除', 'YesNo') -ne 'Yes') { return }
    try {
        if ($route.enabled) { Remove-ManagedRoute $route }
        $script:routes = @($script:routes | Where-Object { $_.id -ne $route.id })
        $script:probeResults.Remove([string]$route.id)
        Save-Routes
        Refresh-Grid
    } catch { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '删除失败') | Out-Null }
})
$syncButton.Add_Click({ Sync-Routes; Refresh-Grid })
$grid.Add_CellContentClick({
    param($sender, $eventArgs)
    if ($eventArgs.RowIndex -lt 0 -or $eventArgs.ColumnIndex -ne 6) { return }
    $route = $grid.Rows[$eventArgs.RowIndex].Tag
    if (-not $route.enabled) { return }
    Start-Probe $route
    Refresh-Grid
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({
    Update-Probes
    if (((Get-Date) - $script:lastSync).TotalSeconds -ge 10) { Sync-Routes }
    Refresh-Grid
})
$form.Add_Shown({ Sync-Routes; Refresh-Grid; $timer.Start() })
$form.Add_FormClosed({
    $timer.Stop()
    foreach ($pending in $script:pendingProbes.Values) { $pending.ping.Dispose() }
})
[System.Windows.Forms.Application]::Run($form)
