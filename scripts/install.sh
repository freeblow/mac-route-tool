#!/bin/bash
# Developer installer. SRM.app can also install or repair its bundled service.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET_USER="${SUDO_USER:-}"

if [[ "$(id -u)" -ne 0 ]]; then
    echo "请使用 sudo 运行：sudo $0" >&2
    exit 1
fi
if [[ -z "$TARGET_USER" ]]; then
    echo "无法确定授权用户（请用 sudo 运行）" >&2
    exit 1
fi
TARGET_HOME="$(dscl . -read "/Users/$TARGET_USER" NFSHomeDirectory | awk '{print $2}')"

echo "==> 编译通用二进制（以 ${TARGET_USER} 身份）"
sudo -u "$TARGET_USER" bash -lc "cd '$ROOT' && bash scripts/build-universal.sh srmd && bash scripts/build-universal.sh srm"

SERVICE_DIR="$ROOT/dist"
if [[ "${1:-}" != "--no-gui" ]]; then
    echo "==> 构建菜单栏应用"
    sudo -u "$TARGET_USER" bash "$ROOT/scripts/build-app.sh"
    SERVICE_DIR="$ROOT/dist/SRM.app/Contents/Resources/Service"
fi

echo "==> 安装服务"
SRM_SERVICE_BIN_DIR="$SERVICE_DIR" "$ROOT/scripts/install-service.sh" "$TARGET_USER"

if [[ "${1:-}" != "--no-gui" ]]; then
    echo "==> 安装菜单栏应用到 ~/Applications"
    install -d -m 755 "${TARGET_HOME}/Applications"
    rm -rf "${TARGET_HOME}/Applications/SRM.app"
    cp -R "$ROOT/dist/SRM.app" "${TARGET_HOME}/Applications/SRM.app"
    chown -R "${TARGET_USER}" "${TARGET_HOME}/Applications/SRM.app"
    sudo -u "$TARGET_USER" open "${TARGET_HOME}/Applications/SRM.app" || true
fi

echo "安装完成。运行 srm status 检查守护进程。"
