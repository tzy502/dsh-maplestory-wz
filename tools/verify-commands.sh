#!/usr/bin/env bash
# 全命令冒烟：把面板会用到的每个命令都打一遍，确认 .NET 半边都能回 ok。
#
# 用法：bash tools/verify-commands.sh [base_url]
#   缺省 http://127.0.0.1:3099
#
# 为什么要有这个：迁移期间命令是分批接管的，任何一条漏接都会在面板里表现成
# 空白或转圈，肉眼很难定位到是哪条命令。这里一条一条打，失败会指名。
set -uo pipefail

BASE="${1:-http://127.0.0.1:3099}"
API="$BASE/dsh-maplestory-wz/api"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
failed_names=()

# name|json-body
CASES=(
  'packs|{"cmd":"packs","args":{}}'
  'tree|{"cmd":"tree","args":{}}'
  'tree:Mob|{"cmd":"tree","args":{"pack":"Mob","limit":5}}'
  'search|{"cmd":"search","args":{"query":"花蘑菇","limit":3}}'
  'get|{"cmd":"get","args":{"path":"Mob/1210102.img"}}'
  'frames|{"cmd":"frames","args":{"path":"Mob/1210102.img","animation":"stand"}}'
  'sprite|{"cmd":"sprite","args":{"path":"Mob/1210102.img","animation":"move","scale":1}}'
  'sprite:gif|{"cmd":"sprite","args":{"path":"Mob/1210102.img","animation":"move","scale":1,"gif":true}}'
  'image|{"cmd":"image","args":{"path":"Mob/1210102.img","animation":"stand"}}'
  'msPacks|{"cmd":"msPacks","args":{}}'
  'detail:mob|{"cmd":"detail","args":{"id":"1210102","kind":"mob"}}'
  'detail:eqp|{"cmd":"detail","args":{"id":"1302000","kind":"eqp"}}'
  'sources|{"cmd":"sources","args":{"id":"1210102","kind":"mob"}}'
  'stats|{"cmd":"stats","args":{}}'
  'char_poses|{"cmd":"char_poses","args":{}}'
  'char_parts|{"cmd":"char_parts","args":{}}'
  'char_render|{"cmd":"char_render","args":{"equips":{"Body":"00002000","Head":"00012000","Hair":"00042590","Face":"00022038","Longcoat":"01054561","Shoes":"01074237","Weapon":"01703725"},"pose":"stand1","flip":false}}'
  'chair_list|{"cmd":"chair_list","args":{}}'
  'balloon_styles|{"cmd":"balloon_styles","args":{}}'
  'balloon|{"cmd":"balloon","args":{"text":"你好，冒险岛！","style":0}}'
  'map_list|{"cmd":"map_list","args":{"limit":5}}'
  'map_featured|{"cmd":"map_featured","args":{}}'
  'map_detail|{"cmd":"map_detail","args":{"id":"100000000"}}'
  'map_content_bounds|{"cmd":"map_content_bounds","args":{"id":"100000000"}}'
  'cmsdl_status|{"cmd":"cmsdl_status","args":{}}'
  'cmsdl_probe|{"cmd":"cmsdl_probe","args":{}}'
  'cmsdl_plan|{"cmd":"cmsdl_plan","args":{}}'
  'petGet|{"cmd":"petGet","args":{}}'
  'petSearch|{"cmd":"petSearch","args":{"query":"蘑菇","limit":3}}'
  'pet|{"cmd":"pet","args":{"id":"1210102","actions":["stand","move"],"mode":"mob"}}'
)

printf '全命令冒烟：%s\n' "$BASE"
for item in "${CASES[@]}"; do
  name="${item%%|*}"
  body="${item#*|}"
  code=$(curl -s -m 180 -o "$TMP/out.json" -w '%{http_code}' \
    -X POST "$API" -H 'content-type: application/json' --data-binary "$body" 2>/dev/null)
  ok=$(python3 -c "
import json,sys
try:
    d=json.load(open('$TMP/out.json'))
    print('1' if d.get('ok') else '0')
    if not d.get('ok'): print(str(d.get('error'))[:90], file=sys.stderr)
except Exception as e:
    print('0'); print(f'解析失败 {e}', file=sys.stderr)
" 2>"$TMP/err")
  if [ "$code" = "200" ] && [ "$ok" = "1" ]; then
    engine=$(python3 -c "
import json
d=json.load(open('$TMP/out.json')).get('data') or {}
print(d.get('engine') or '')
" 2>/dev/null)
    printf '  ✓ %-18s %s\n' "$name" "${engine:+engine=$engine}"
    pass=$((pass+1))
  else
    reason=$(head -1 "$TMP/err" 2>/dev/null)
    printf '  ✗ %-18s http=%s %s\n' "$name" "$code" "$reason"
    failed_names+=("$name")
    fail=$((fail+1))
  fi
done

echo '──────────────────────────────────────────────────────────────'
printf '结果：%d 通过 / %d 失败\n' "$pass" "$fail"
if [ "$fail" -gt 0 ]; then
  printf '失败项：\n'
  for n in "${failed_names[@]}"; do printf '  - %s\n' "$n"; done
  exit 1
fi
