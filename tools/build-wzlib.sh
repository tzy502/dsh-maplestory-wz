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
if [ -f "$OUT" ]; then
  echo "✓ 产物：$OUT"
  echo "  插件会按 engine=auto 自动使用它（找不到 .NET 时回退 Python 半边）。"
else
  echo "✗ 编译结束但没有产物：$OUT" >&2
  exit 1
fi
