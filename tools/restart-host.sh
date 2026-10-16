#!/usr/bin/env bash
# 重启 DSH Web host，让改动过的 host 半边 / 插件包重新加载。
#
# 为什么要重启：DSH 用 ESM `import()` 记住模块缓存，同一个文件路径在进程内只会
# 求值一次。改完 index.js 后，不重启就永远是旧那一代代码（client 半边不用重启，
# 浏览器刷新即可——它走的是 /plugins 的 bundle 路由）。
#
# 用法：bash tools/restart-host.sh [profile]
set -u

PROFILE="${1:-${DSH_PROFILE:-web}}"
PORT="${DSH_PORT:-3080}"
LOG="${DSH_LOG:-$HOME/.dsh/dsh-${PROFILE}.log}"
DSH_BIN="$(command -v dsh || echo "$HOME/.local/bin/dsh")"

old="$(lsof -nP -iTCP:"$PORT" -sTCP:LISTEN -t 2>/dev/null | head -1)"
if [ -n "$old" ]; then
  echo "停止旧的 host (pid $old)…"
  kill "$old" 2>/dev/null
  for _ in $(seq 1 20); do
    sleep 0.5
    lsof -nP -iTCP:"$PORT" -sTCP:LISTEN -t >/dev/null 2>&1 || break
  done
fi

echo "启动：$DSH_BIN --profile $PROFILE --no-open   （日志：$LOG）"
nohup "$DSH_BIN" --profile "$PROFILE" --no-open >>"$LOG" 2>&1 &
new=$!

for _ in $(seq 1 60); do
  sleep 0.5
  pid="$(lsof -nP -iTCP:"$PORT" -sTCP:LISTEN -t 2>/dev/null | head -1)"
  if [ -n "$pid" ]; then
    echo "host 已就绪：pid=$pid  http://127.0.0.1:$PORT"
    exit 0
  fi
done

echo "60 秒内没起来，看日志：$LOG" >&2
exit 1
