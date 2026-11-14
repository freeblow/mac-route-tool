# SRM · 静态路由管理器

macOS 版使用菜单栏 App 和守护进程管理静态路由。Windows 版是独立的 .NET 8 WPF 应用「SRM 网络助手」（源码在 `windows/`），功能与 macOS 版对齐。两个平台的免安装产物都可以直接从 [GitHub Releases](https://github.com/freeblow/mac-route-tool/releases) 下载。

```
✓ 10.40.85.0/24
  via 10.40.81.254 · dev en0 · 实验楼网段
```

---

## 为什么不用 plist 直接 `route add`

常见做法是往 `/Library/LaunchDaemons` 丢一个 `RunAtLoad` 的 plist，开机时执行一次 `route add`。这在四个场景下会失效：

| 场景 | 原因 |
|---|---|
| Wi-Fi 重连 / DHCP 续租 / 睡眠唤醒 | 内核会清掉静态路由，plist 只在开机跑一次，不会补 |
| 开机时网卡还没拿到 IP | `route add` 报 "No such interface" 或直接失败，且永不重试 |
| VPN（utun）上线 / 下线 | 路由表随接口出现和消失，plist 无感知 |
| 多网卡同时在线 | 默认路由可能选择了与目标网关不同的出口 |

SRM 的做法：常驻 daemon 每隔几秒比对「配置」与「内核路由表」，缺哪条补哪条；通过网关确定出口接口，并在指定接口与网关的实际出口不一致时提示错误；只删除自己写进去的条目，不碰 DHCP/VPN 下发的路由。

---

## 架构

```
┌──────────────┐   JSON over unix socket   ┌───────────────┐   route add/delete
│  SRM.app     │◄──────────────────────────►│   srmd        │◄─────────────────►  内核路由表
│  (菜单栏)     │   /var/run/srm/srmd.sock   │  (root 常驻)   │
└──────────────┘                           └───────┬───────┘
                                                   │ 读写
                                            /etc/srm/routes.json
```

| 组件 | 说明 |
|---|---|
| `SRM.app` | SwiftUI 菜单栏应用，SwiftPM 编译 + 手工打包成 `.app`，`LSUIElement` 无 Dock 图标 |
| `srmd` | root 常驻守护进程（launchd Daemon），负责对齐路由表并监听 socket |
| `srm` | 命令行工具，与守护进程通信，便于脚本化 |
| `SRMCore` | 共享库：CIDR 解析、路由表读取、接口枚举、`route(8)` 调用封装、IPC |

**权限模型**：改路由需要 root，但 GUI 不需要。守护进程以 root 运行，socket 权限 `0600` 且属主为「授权用户」；每次请求还会用 `getpeereid()` 校验对端 uid，拒绝非授权调用方。配置文件 `/etc/srm/routes.json` 为 `0600` root 拥有——普通用户改不了配置，也就没法借守护进程提权。

---

## 安装

**macOS**（两种方式）：

1. 从 [GitHub Releases](https://github.com/freeblow/mac-route-tool/releases) 下载 `SRM-x.y.z-macos-universal.zip`（x86_64 + arm64 通用二进制），解压打开 `SRM.app`。
2. 从源码构建：运行 `scripts/build-app.sh` 后打开 `dist/SRM.app`。

第一次启动或服务断开时，应用会自动检测守护进程版本，弹出系统授权对话框完成安装/修复——应用包内已包含 `srmd`、`srm` 和服务安装程序，输一次管理员密码即可，修复完成后面板会刷新连接和路由状态。右键菜单也可随时重新安装或修复服务。

也可在开发环境中通过终端安装：

```bash
cd ~/Projects/srm
sudo scripts/install.sh
```

脚本会：

1. 编译并安装 `srmd` → `/usr/local/libexec/srmd`、`srm` → `/usr/local/bin/srm`
2. 生成 `/etc/srm/routes.json`（初始路由列表为空，`authorizedUID` 记为当前用户）
3. 安装 `/Library/LaunchDaemons/com.user.srmd.plist` 并 `launchctl bootstrap system`
4. 打包 `SRM.app` 到 `~/Applications/` 并启动

安装完成后可用 `srm status` 检查连接。

只想装守护进程：`sudo scripts/install.sh --no-gui`。

卸载：`sudo scripts/uninstall.sh`（保留 `/etc/srm/routes.json`）。

---

## 使用

### 菜单栏

点菜单栏图标打开面板：

- **＋ 添加路由** — 填写目标网段（CIDR 或 `default`）、网关、绑定接口
- 每一行右侧的勾选框 = 停用/启用；`⋯` 菜单 = 编辑 / 停用 / 删除
- 每条路由的「测试连通性」会显示独立的 ICMP 响应结果；`/32` 主机路由测试目标，其余网段路由测试网关。绿色路由图标只表示路由已写入，不表示目标一定可访问。目标禁用 Ping 时可能显示未响应。
- **重新同步** — 删除并重写全部受管理的路由（接口切换后手动用）
- 右键菜单 = 重新同步 / 安装或修复守护进程 / 打开配置文件 / 退出
- **网卡管理**：查看网卡状态/网关/速率，调整网络服务顺序（优先级）、启用禁用网络服务
- **IP 检测**：子网扫描（CIDR / 单地址 / 范围），在线链路用 ARP 判定占用，离链路用 ping；支持 CSV 导出
- **局域网发现**：放行 LocalSend 端口（PF）、组播路由绑定网卡、喊一声/听听看诊断工具

### 命令行

```bash
srm list                      # 受管理的路由 + 状态
srm status                    # 守护进程状态
srm interfaces                # 列出接口及 IPv4
srm kernel                    # 打印内核路由表
srm add 10.40.85.0/24 10.40.81.254 --iface en0
srm add 10.40.85.0/24 10.40.81.254 --iface en0 --off   # 只存配置，不生效
srm disable 10.40.85.0/24     # 停用并从内核移除
srm del 10.40.85.0/24
srm apply                     # 强制重写
```

状态含义：`生效` 已在路由表里 / `等待中` 网关或接口还没就绪 / `失败` 写入被拒（消息里有内核原话）/ `已停用` 只存在配置里。

## Windows 版

Windows 版是独立的 .NET 8 WPF 应用「SRM 网络助手」（源码在 `windows/src/`），一个窗口覆盖：静态路由（持久路由，重启保留）、网卡优先级 / 多网卡共存、网卡启停、IP 检测（子网扫描占用）。界面与功能的详细说明见 `windows/README.txt`。

安装方式（按省事程度排序）：

1. **免安装便携版（推荐）**：从 [GitHub Releases](https://github.com/freeblow/mac-route-tool/releases) 下载 `SRM-x.y.z-windows-portable.zip`，整个文件夹解压后双击「SRM 网络助手.exe」——自带 .NET 8 运行时，目标电脑无需安装任何依赖（Windows 10 1903+ / 11，64 位）。
2. **从源码构建**：安装 .NET 8 SDK 后双击 `windows/tools/生成便携版.cmd` 或 `生成免安装版.cmd`，生成对应产物。
3. **旧版脚本**：PowerShell + WinForms 版（`SRM.cmd` / `SRM.ps1`）保留备用，功能基本相同。

使用要点：

- 路由写入 Windows 持久路由表，重启后仍有效；配置保存在 `%ProgramData%\SRM\routes.json`，应用打开期间每 21 秒自动检查补写。
- 每条路由右侧有「测试」按钮和响应结果；主机路由检测目标，网段路由检测网关。
- 「网卡优先级」页可调整跃点数（IPv4/IPv6 同时写入）、恢复自动跃点，多网卡共存开关也在这里。

---

## 配置格式

`/etc/srm/routes.json`：

```json
{
  "version": 1,
  "pollInterval": 5,
  "authorizedUID": 501,
  "routes": [
    {
      "id": "6E5A0B10-0000-4000-8000-000000000001",
      "destination": "10.40.85.0/24",
      "gateway": "10.40.81.254",
      "interfaceName": "en0",
      "direct": false,
      "enabled": true,
      "note": "实验楼网段"
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `destination` | CIDR 或 `default`；`10.40.85/24`、`10.40.85.7` 等简写会自动规范化 |
| `gateway` | 下一跳地址 |
| `interfaceName` | 预期出口接口；必须与网关的实际出口一致；留空则按网关自动选择 |
| `direct` | `true` 表示目标就在本网段，改用 `-interface` 修饰符 |
| `enabled` | `false` 时只保留配置，不写路由表 |
| `authorizedUID` | 允许连接守护进程的 uid；`null` 表示只允许 root |

改完配置不需要重启 daemon——守护进程每轮都会重新读配置。

---

## 已知限制

- **`route(8)` 不支持 metric**。macOS 的 `route` 命令没有 `-metric` 选项，路由优先级无法单独设置；同一目标的普通流量只能按内核选出的路由走。
- **只管 IPv4**。`netstat -f inet` / `route -net` 均为 AF_INET；IPv6 需要另走 `netstat -f inet6` + `route -inet6`。
- **默认路由需谨慎**。加了 `default` 路由会改变全局出口，配置前先用 `srm kernel` 确认现状。
- **无级联删除**：删掉配置里的路由时，守护进程只会删自己写入的那条；同目标的其它条目不动。
- **同一目标不能同时绑定到两个接口**。带网关的路由按「网关所在接口」决定出口，写入时不加 `-ifscope`（内核会拒绝对已存在的目标再加一条同目标路由，报 `File exists`）。若需要同一目标在两张网卡上各有一条路由，只能改用 `route -n add -net -ifscope <if>` 直接写入——这条路径当前未纳入守护进程的管理范围。

---

## 开发

```bash
swift build -c release      # 编译（仅当前架构）
swift test                  # 单元测试（66 个）
scripts/build-app.sh        # 打包 dist/SRM.app（通用二进制 arm64 + x86_64）
swift scripts/generate-icon.swift  # 修改图标绘制代码后重新生成图标
make run                    # 打包并启动
```

### 通用二进制

打包产物是 fat binary，同时支持 Apple Silicon 与 Intel Mac：

```bash
lipo -info dist/SRM.app/Contents/MacOS/SRM
# Architectures in the fat file: ... are: x86_64 arm64
```

`scripts/build-universal.sh` 会分别用 `--arch arm64` 和 `--arch x86_64` 编译，再用 `lipo -create` 合并，产物落在 `dist/`：

```bash
bash scripts/build-universal.sh SRMGUI          # arm64 + x86_64
bash scripts/build-universal.sh srmd x86_64     # 只构建 x86_64
ARCHS="arm64" bash scripts/build-universal.sh srm
```

应用包内的 `srmd`、`srm` 同样是通用二进制，所以同一个 `SRM.app` 可以在两种 Mac 上安装服务。最低系统版本 macOS 13（`LSMinimumSystemVersion`）。

> 注意：SwiftPM 不支持 `swift build --arch arm64 --arch x86_64`（会报 `duplicate output file`），必须分架构编译再 `lipo`。

调试环境变量：

| 变量 | 作用 |
|---|---|
| `SRM_SOCKET` | 指定 socket 路径（默认 `/var/run/srm/srmd.sock`） |
| `SRM_SOCKET_DIR` | 指定运行时目录 |
| `SRM_DEBUG_WINDOW=1` | 启动即弹出面板，便于截图核对 |
| `SRM_DEBUG_EDITOR=1` | 启动即打开「添加路由」表单 |

守护进程 dry-run（只报告不写路由表，不需要 root）：

```bash
srmd --dry-run --config /tmp/routes.json --socket /tmp/srm.sock --once
```
