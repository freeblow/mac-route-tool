#!/bin/bash
# 构建通用二进制（arm64 + x86_64）
#
#   scripts/build-universal.sh <product> [arch ...]
#
# 环境变量：
#   CONFIG  编译模式，默认 release
#   ARCHS   目标架构，空格分隔，默认 "arm64 x86_64"
#   命令行参数中的架构会覆盖环境变量
#
# 产物：dist/<product>（fat binary）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${CONFIG:-release}"
REQUESTED_ARCHS="${ARCHS:-arm64 x86_64}"

if [[ $# -lt 1 ]]; then
    echo "用法: $0 <product> [arch ...]" >&2
    exit 1
fi
PRODUCT="$1"
shift

ARCHS=("$@")
if [[ ${#ARCHS[@]} -eq 0 ]]; then
    read -r -a ARCHS <<< "$REQUESTED_ARCHS"
fi

cd "$ROOT"

mkdir -p "$ROOT/dist"
OUT="$ROOT/dist/$PRODUCT"
BINS=()

for arch in "${ARCHS[@]}"; do
    echo "==> 编译 $PRODUCT ($arch)"
    swift build -c "$CONFIG" --arch "$arch" --product "$PRODUCT"
    BIN="$ROOT/.build/$arch-apple-macosx/$CONFIG/$PRODUCT"
    if [[ ! -f "$BIN" ]]; then
        echo "找不到产物：$BIN" >&2
        exit 1
    fi
    BINS+=("$BIN")
done

if [[ ${#BINS[@]} -eq 1 ]]; then
    cp "${BINS[0]}" "$OUT"
    echo "==> 单架构产物：$OUT"
else
    echo "==> 合并 ${#BINS[@]} 个架构"
    lipo -create "${BINS[@]}" -output "$OUT"
fi

lipo -info "$OUT"
echo "==> $OUT"
