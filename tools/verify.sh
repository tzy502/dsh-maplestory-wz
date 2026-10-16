#!/usr/bin/env bash
# 全量验收：把「插件 + 桌宠」的每条链路都实测一遍，结果打在一张表里。
#
# 用法：bash tools/verify.sh [host]
#   默认 host=http://127.0.0.1:3080
#
# 退出码 0 = 全绿；1 = 有失败项（失败项会列在末尾）。
set -u

WZ_PORT_VAL=${WZ_PORT:-3080}
HOST=${1:-http://127.0.0.1:$WZ_PORT_VAL}
WZ_API="$HOST/dsh-maplestory-wz/api"
# 桌宠已并入本插件，与 WZ_API 同一个端点（命令名不同）
PET_API="$WZ_API"
PY="${WZ_PYTHON:-$HOME/.hermes/cache/scratch/wz-env/bin/python}"

pass=0; fail=0; failed=()
ok()   { printf "  \033[32m✓\033[0m %s\n" "$1"; pass=$((pass+1)); }
bad()  { printf "  \033[31m✗\033[0m %s\n" "$1"; fail=$((fail+1)); failed+=("$1"); }
sect() { printf "\n\033[1m%s\033[0m\n" "$1"; }

# ---------- 0. host ----------
sect "0. host 与数据源"
host_pid=$(lsof -nP -iTCP:"$WZ_PORT_VAL" -sTCP:LISTEN -t 2>/dev/null | head -1)
if [ -n "$host_pid" ]; then ok "host listening on $WZ_PORT_VAL (pid $host_pid)"; else bad "host not listening on $WZ_PORT_VAL"; fi

# ---------- 1. 插件 API ----------
sect "1. 冒险岛插件 API（每条的 args 都带必填项）"
api() { # name cmd json-args
  local out; out="$(curl -s -m "${3:-60}" -X POST "$WZ_API" -H 'content-type: application/json' \
      -d "{\"cmd\":\"$2\",\"args\":$4}" 2>/dev/null)"
  if printf '%s' "$out" | grep -q '"ok":true'; then ok "$2 —— $1"; else bad "$2 —— $1（$(printf '%s' "$out" | head -c 120)）"; fi
  printf '%s' "$out"
}
api "数据源分类"        packs       30 '{}'                     >/dev/null
api "目录树"            tree        30 '{"pack":"Mob","path":"_Canvas","limit":5}' >/dev/null
api "中文名搜索"        search      60 '{"query":"花蘑菇","limit":5}' >/dev/null
api "图片信息"          get         30 '{"path":"Mob/_Canvas/1210102.img"}' >/dev/null
api "雪碧图"            sprite      60 '{"path":"Mob/_Canvas/1210102.img","animation":"move","scale":2}' >/dev/null
api "图鉴(mob)"         detail      60 '{"kind":"mob","id":"1210102"}' >/dev/null
api "图鉴(eqp)"         detail      90 '{"kind":"eqp","id":"1332020"}' >/dev/null
api "来源探测"          sources     30 '{"kind":"npc","id":"1033113"}' >/dev/null
api "帧网格"            frameSheet  60 '{"path":"Mob/_Canvas/1210102.img","animation":"move","scale":1}' >/dev/null
api "Packs/*.ms 列表"   msPacks     30 '{}' >/dev/null
api "怪物 .ms 结构"     msMob       90 '{"id":"1210102"}' >/dev/null

# ---------- 2. 地图 ----------
sect "2. 地图与背景（相机规则）"
MAPJSON="$(api "地图名录" mapList 30 '{"query":"射手","limit":3}')"
api "地图详情" mapDetail 30 '{"id":"100000000","view":{"w":1920,"h":1080,"zoom":1}}' >/dev/null
api "内容边界" mapContentBounds 60 '{"id":"100000000"}' >/dev/null

BG="$(curl -s -m 120 -X POST "$WZ_API" -H 'content-type: application/json' -d '{
  "cmd":"mapBackground",
  "args":{"id":"100000000","worldScale":0.7023,
          "contentBounds":{"left":370,"top":-508,"right":6202,"bottom":664,"width":5832,"height":1172},
          "target_w":1920,"target_h":1080}}' 2>/dev/null)"
BGU="${BGU:-}"; BGU="$(printf '%s' "$BG" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("data",{}).get("url",""))' 2>/dev/null)"
if [ -n "$BGU" ]; then
  curl -s -m 60 -o /tmp/wz_verify_bg.png "$HOST$BGU" 2>/dev/null
  SIZE="${SIZE:-}"; SIZE="$("$PY" -c "
from PIL import Image
print('%dx%d' % Image.open('/tmp/wz_verify_bg.png').size)" 2>/dev/null)"
  [ "$SIZE" = "1920x1080" ] && ok "背景图落盘 1920x1080" || bad "背景图尺寸 ${SIZE}（应 1920x1080）"
  # 接口还必须回传 w/h/padded：面板靠 w/h 算 background-position，
  # 缺了它就只能退回请求尺寸，平移量会整体偏小（表现＝背景几乎不动）。
  WH="${WH:-}"; WH="$(printf '%s' "$BG" | "$PY" -c 'import json,sys;d=json.load(sys.stdin).get("data",{});print("%sx%s/%s"%(d.get("w"),d.get("h"),d.get("padded")))' 2>/dev/null)"
  case "${WH}" in
    1920x1080/*) ok "接口回传 w/h/padded（${WH}）" ;;
    *)           bad "接口回传 w/h/padded = ${WH}（应 1920x1080/...）" ;;
  esac
else
  bad "地图背景没返回 url"
fi

BC="$(curl -s -m 30 -X POST "$WZ_API" -H 'content-type: application/json' -d '{"cmd":"backgroundGet","args":{}}' 2>/dev/null)"
printf '%s' "$BC" | grep -q '"ok":true' && ok "背景配置可读写（$(printf '%s' "$BC" | head -c 90)…）" || bad "背景配置读取失败"

# ---------- 3. 桌宠 ----------
sect "3. DSH 内部桌宠"
PET="$(curl -s -m 60 -X POST "$PET_API" -H 'content-type: application/json' -d '{"cmd":"pet","args":{}}' 2>/dev/null)"
if printf '%s' "$PET" | grep -q '"ok":true'; then
  ok "桌宠数据接口"
  PU="${PU:-}"; PU="$(printf '%s' "$PET" | "$PY" -c '
import json,sys
x=json.load(sys.stdin).get("data",{})
a=x.get("anims") or {}
for k in a:
    u=a[k].get("url")
    if u: print(u); break' 2>/dev/null)"
  if [ -n "$PU" ]; then
    curl -s -m 30 -o /tmp/wz_verify_pet.png "$HOST$PU" 2>/dev/null
    PS="${PS:-}"; PS="$("$PY" -c "
from PIL import Image
im=Image.open('/tmp/wz_verify_pet.png').convert('RGBA')
op=sum(1 for p in im.getdata() if p[3]>0)
print('%dx%d:%d%%' % (im.width, im.height, 100*op/(im.width*im.height)))" 2>/dev/null)"
    printf '%s' "$PS" | grep -qE '^[0-9]+x[0-9]+:([1-9][0-9]|[5-9])%' \
      && ok "桌宠雪碧图有内容（${PS}）" || bad "桌宠雪碧图疑似空白（${PS}）"
  else
    bad "桌宠没返回图片 url"
  fi
else
  bad "桌宠接口失败（$(printf '%s' "$PET" | head -c 120)）"
fi

# 换宠：搜候选 → 记录当前 → 切换 → 复读 → 换回（不改变用户的选择）
PS2="$(curl -s -m 60 -X POST "$PET_API" -H 'content-type: application/json' -d '{"cmd":"presets","args":{}}' 2>/dev/null)"
printf '%s' "$PS2" | grep -q '"presets"' && ok "换宠候选列表" || bad "换宠候选列表失败"
BEFORE="$("$PY" -c 'import json,sys;print(json.loads(sys.argv[1]).get("data",{}).get("mobId",""))' "$PS2" 2>/dev/null)"
CUR="$(curl -s -m 30 -X POST "$PET_API" -H 'content-type: application/json' -d '{"cmd":"petGet","args":{}}' 2>/dev/null | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("data",{}).get("mobId",""))' 2>/dev/null)"
SWAP="$(printf '%s' "$PS2" | "$PY" -c '
import json,sys
p=json.load(sys.stdin).get("data",{}).get("presets",[])
cur=str(sys.argv[1])
for c in p:
    if str(c.get("id")) != cur:
        print(c.get("id")); break' "$CUR" 2>/dev/null)"
if [ -n "${SWAP}" ]; then
  curl -s -m 90 -X POST "$PET_API" -H 'content-type: application/json' -d "{\"cmd\":\"petSet\",\"args\":{\"id\":\"${SWAP}\"}}" >/dev/null 2>&1
  AFTER="$(curl -s -m 60 -X POST "$PET_API" -H 'content-type: application/json' -d '{"cmd":"pet","args":{}}' 2>/dev/null | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("data",{}).get("id",""))' 2>/dev/null)"
  [ "${AFTER}" = "${SWAP}" ] && ok "换宠生效（${CUR} → ${AFTER}）" || bad "换宠没生效（切到 ${SWAP}，取回 ${AFTER}）"
  # 换回原宠物，别改用户的选择
  [ -n "${CUR}" ] && curl -s -m 90 -X POST "$PET_API" -H 'content-type: application/json' -d "{\"cmd\":\"petSet\",\"args\":{\"id\":\"${CUR}\"}}" >/dev/null 2>&1
else
  bad "换宠：拿不到可切换的候选"
fi

# ---------- 4. cmsdl ----------
sect "4. cmsdl 资源更新"
UP="$(curl -s -m 60 -X POST "$WZ_API" -H 'content-type: application/json' -d '{"cmd":"updateStatus","args":{}}' 2>/dev/null)"
printf '%s' "$UP" | grep -q '"ok":true' && ok "更新状态接口（$(printf '%s' "$UP" | head -c 130)…）" || bad "更新状态接口失败（$(printf '%s' "$UP" | head -c 120)）"

# ---------- 汇总 ----------
printf "\n\033[1m结果：%d 通过 / %d 失败\033[0m\n" "$pass" "$fail"
if [ "$fail" -gt 0 ]; then
  printf "失败项：\n"; for f in "${failed[@]}"; do printf "  - %s\n" "$f"; done
  exit 1
fi
