#!/bin/bash
# 卸载 SRM 守护进程（需要 root）
set -euo pipefail

LABEL="com.user.srmd"
PLIST="/Library/LaunchDaemons/${LABEL}.plist"

if [[ "$(id -u)" -ne 0 ]]; then
    echo "请使用 sudo 运行：sudo $0" >&2
    exit 1
fi

echo "==> 停止守护进程"
launchctl bootout system "$PLIST" 2>/dev/null || true

echo "==> 删除 launchd 定义"
rm -f "$PLIST"

echo "==> 删除二进制"
rm -f /usr/local/libexec/srmd /usr/local/bin/srm

echo "==> 清理运行时文件"
rm -f /var/run/srm/srmd.sock /var/run/srm/managed.json
rmdir /var/run/srm 2>/dev/null || true

echo "==> 保留配置：/etc/srm/routes.json（如需清理请手动删除）"

if [[ -d "$HOME/Applications/SRM.app" ]]; then
    echo "==> 删除菜单栏应用"
    rm -rf "$HOME/Applications/SRM.app"
fi

echo "卸载完成。"
