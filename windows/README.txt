SRM Windows 版（Windows 10/11）

1. 解压整个压缩包。
2. 双击 SRM.cmd。
3. 在 Windows 管理员权限提示中点击“是”。
4. 在图形界面中添加目标网段和网关；每条路由可点“测试”查看响应。

路由会写入 Windows 持久路由表，系统重启后仍保留。
配置文件位于 %ProgramData%\SRM\routes.json。
主机路由（/32）测试目标 IP，网段路由测试网关 IP。
“生效”表示路由已写入；Ping 无响应也可能是目标禁用了 ICMP。

此版本使用 Windows 自带的 Windows PowerShell 5.1 和 WinForms，
无需安装 Swift 或其他运行时，也无需单独维护服务。
