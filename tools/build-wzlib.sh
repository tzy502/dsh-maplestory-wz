#!/usr/bin/env bash
# 编译 .NET 半边（WzLib worker）。
#
# 为什么需要这一步：WzLib 是 .NET 库，插件用 dotnet <dll> 启动它。
# 仓库里不带编译产物（见 .gitignore），所以装机后要么跑这个脚本，要么用发布好的包。
#
# 用法：
#   bash tools/build-wzlib.sh
# 可用环境变量：
#   DOTNET   指定 dotnet 可执行文件（本机在 /Volumes/SSD/C#/dotnet）
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-}"
if [ -z "$DOTNET" ]; then
  for c in /Volumes/SSD/C#/dotnet /usr/local/share/dotnet/dotnet /opt/homebrew/bin/dotnet "$(command -v dotnet || true)"; do
    [ -n "$c" ] && [ -x "$c" ] && DOTNET="$c" && break
  done
fi
if [ -z "$DOTNET" ]; then
  echo "✗ 找不到 dotnet。装 .NET 9 SDK，或用 DOTNET=/path/to/dotnet 指定。" >&2
  exit 1
fi

echo "==> 用 $DOTNET 编译 net/WzWorker"
"$DOTNET" build net/WzWorker/WzWorker.csproj -c Release --nologo -v q

OUT="net/WzWorker/bin/Release/net9.0/wzworker.dll"
if [ ! -f "$OUT" ]; then
  echo "✗ 编译结束但没有产物：$OUT" >&2
  exit 1
fi

# 同时放一份到 dist/：这份会进仓库与 npm 包，用户只要有 .NET 运行时就能跑，
# 不必再装 SDK。IL 是跨平台的，一次编译各平台通用。
DIST="net/WzWorker/dist"
mkdir -p "$DIST"
cp net/WzWorker/bin/Release/net9.0/wzworker.dll "$DIST/"
cp net/WzWorker/bin/Release/net9.0/wzworker.deps.json "$DIST/" 2>/dev/null || true
cp net/WzWorker/bin/Release/net9.0/wzworker.runtimeconfig.json "$DIST/" 2>/dev/null || true
cp net/WzWorker/bin/Release/net9.0/WzComparerR2.WzLib.dll "$DIST/" 2>/dev/null || true
# SkiaSharp 的托管库与各平台原生库必须一起带走，否则文字渲染会在运行时找不到程序集
for f in net/WzWorker/bin/Release/net9.0/SkiaSharp*.dll; do
  [ -f "$f" ] && cp "$f" "$DIST/" || true
done
for d in net/WzWorker/bin/Release/net9.0/runtimes; do
  [ -d "$d" ] && cp -R "$d" "$DIST/" || true
done

echo "✓ 产物：$OUT"
echo "✓ 分发包：$DIST/（随 npm 包发布，用户只需 .NET 运行时）"
ls -la "$DIST" | tail -5
