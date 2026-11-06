#!/bin/bash
# Installs or repairs the privileged service from binaries bundled in SRM.app.
set -euo pipefail

TARGET_USER="${1:-}"
BIN_DIR="${SRM_SERVICE_BIN_DIR:-$(cd "$(dirname "$0")" && pwd)}"
LABEL="com.user.srmd"
PLIST="/Library/LaunchDaemons/${LABEL}.plist"
DAEMON="/usr/local/libexec/srmd"
CLI="/usr/local/bin/srm"
CONFIG_DIR="/etc/srm"
CONFIG="${CONFIG_DIR}/routes.json"
SOCKET="/var/run/srm/srmd.sock"
LOG="/var/log/srmd.log"

if [[ "$(id -u)" -ne 0 ]]; then
    echo "安装守护进程需要管理员授权" >&2
    exit 1
fi
if [[ -z "$TARGET_USER" ]] || ! id "$TARGET_USER" >/dev/null 2>&1; then
    echo "无法确定授权用户" >&2
    exit 1
fi
TARGET_UID="$(id -u "$TARGET_USER")"
if [[ ! -x "$BIN_DIR/srmd" || ! -x "$BIN_DIR/srm" ]]; then
    echo "应用包中缺少守护进程或命令行程序" >&2
    exit 1
fi

echo "==> 安装服务程序"
install -d -m 755 /usr/local/libexec /usr/local/bin "$CONFIG_DIR"
install -m 755 "$BIN_DIR/srmd" "${DAEMON}.new"
install -m 755 "$BIN_DIR/srm" "${CLI}.new"
mv -f "${DAEMON}.new" "$DAEMON"
mv -f "${CLI}.new" "$CLI"

if [[ ! -f "$CONFIG" ]]; then
    printf '{"version":1,"pollInterval":5,"authorizedUID":%s,"routes":[]}\n' "$TARGET_UID" > "$CONFIG"
    chmod 600 "$CONFIG"
else
    echo "==> 保留现有配置：$CONFIG"
fi

cat > "$PLIST" <<PLIST_EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>${LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>${DAEMON}</string>
        <string>--config</string>
        <string>${CONFIG}</string>
        <string>--socket</string>
        <string>${SOCKET}</string>
    </array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key>
    <dict><key>SuccessfulExit</key><false/></dict>
    <key>ProcessType</key><string>Background</string>
    <key>StandardOutPath</key><string>${LOG}</string>
    <key>StandardErrorPath</key><string>${LOG}</string>
</dict>
</plist>
PLIST_EOF
chmod 644 "$PLIST"

echo "==> 启动服务"
launchctl bootout system "$PLIST" 2>/dev/null || true
launchctl bootstrap system "$PLIST"

for _ in 1 2 3 4 5; do
    if sudo -u "$TARGET_USER" "$CLI" status >/dev/null 2>&1; then
        echo "守护进程已连接"
        exit 0
    fi
    sleep 1
done

echo "守护进程未就绪：" >&2
sudo -u "$TARGET_USER" "$CLI" status >&2 || true
launchctl print "system/${LABEL}" 2>&1 | tail -n 25 >&2 || true
tail -n 20 "$LOG" 2>/dev/null >&2 || true
exit 1
