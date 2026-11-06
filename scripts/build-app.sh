#!/bin/bash
# 构建 SRM.app（菜单栏应用，通用二进制 arm64 + x86_64）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${CONFIG:-release}"
VERSION="$(git -C "$ROOT" describe --tags --always 2>/dev/null || echo 0.1.0)"
APP="$ROOT/dist/SRM.app"

echo "==> 编译通用二进制（arm64 + x86_64）"
bash "$ROOT/scripts/build-universal.sh" SRMGUI
bash "$ROOT/scripts/build-universal.sh" srmd
bash "$ROOT/scripts/build-universal.sh" srm

echo "==> 打包 $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/Service"
cp "$ROOT/dist/SRMGUI" "$APP/Contents/MacOS/SRM"

# 守护进程与命令行工具随包分发：应用内可自助安装/修复服务，无需再编译
cp "$ROOT/dist/srmd" "$APP/Contents/Resources/Service/srmd"
cp "$ROOT/dist/srm" "$APP/Contents/Resources/Service/srm"
cp "$ROOT/scripts/install-service.sh" "$APP/Contents/Resources/Service/install-service.sh"
chmod 755 "$APP/Contents/Resources/Service/"*

if [[ -f "$ROOT/Resources/SRM.icns" ]]; then
    cp "$ROOT/Resources/SRM.icns" "$APP/Contents/Resources/SRM.icns"
else
    echo "   (缺少 Resources/SRM.icns，跳过图标)"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
    <key>CFBundleExecutable</key><string>SRM</string>
    <key>CFBundleIdentifier</key><string>com.user.srm</string>
    <key>CFBundleIconFile</key><string>SRM</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>SRM</string>
    <key>CFBundleDisplayName</key><string>静态路由</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSSupportsAutomaticTermination</key><false/>
    <key>NSSupportsSuddenTermination</key><false/>
</dict>
</plist>
PLIST

echo "==> 签名"
for binary in "$APP/Contents/MacOS/SRM" "$APP/Contents/Resources/Service/srmd" "$APP/Contents/Resources/Service/srm"; do
    codesign --force --sign - "$binary" >/dev/null 2>&1 || echo "   (签名失败：$binary)"
done
codesign --force --sign - "$APP" >/dev/null 2>&1 || echo "   (签名失败，仍可运行)"

echo "==> 架构信息"
lipo -info "$APP/Contents/MacOS/SRM"
lipo -info "$APP/Contents/Resources/Service/srmd"
file "$APP/Contents/MacOS/SRM"

echo "==> 完成：$APP"
echo "   运行： open \"$APP\""
