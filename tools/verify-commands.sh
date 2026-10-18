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
  'frameSheet|{"cmd":"frameSheet","args":{"path":"Mob/1210102.img","animation":"stand"}}'
  'sprite|{"cmd":"sprite","args":{"path":"Mob/1210102.img","animation":"move","scale":1}}'
  'sprite:gif|{"cmd":"sprite","args":{"path":"Mob/1210102.img","animation":"move","scale":1,"gif":true}}'
  'detail|{"cmd":"detail","args":{"id":"1210102","kind":"mob"}}'
  'sources|{"cmd":"sources","args":{"id":"1210102","kind":"mob"}}'
  'stats|{"cmd":"stats","args":{}}'
  'msPacks|{"cmd":"msPacks","args":{}}'
  'msMob|{"cmd":"msMob","args":{"id":"1210102"}}'
  'charParams|{"cmd":"charParts","args":{}}'
  'charPoses|{"cmd":"charPoses","args":{}}'
  'chairList|{"cmd":"chairList","args":{"limit":5}}'
  'charRender|{"cmd":"charRender","args":{"equips":{"Body":"00002000","Head":"00012000","Hair":"00042590","Face":"00022038","Longcoat":"01054561","Shoes":"01074237","Weapon":"01703725"},"pose":"stand1","flip":false}}'
  'balloonStyles|{"cmd":"balloonStyles","args":{}}'
  'balloon|{"cmd":"balloon","args":{"text":"你好，冒险岛！","style":0}}'
  'mapList|{"cmd":"mapList","args":{"limit":5}}'
  'mapFeatured|{"cmd":"mapFeatured","args":{}}'
  'mapNext|{"cmd":"mapNext","args":{}}'
  'mapRandom|{"cmd":"mapRandom","args":{}}'
  'mapCanvas|{"cmd":"mapCanvas","args":{"id":"100000000","scale":0.25}}'
  'mapMinimap|{"cmd":"mapMinimap","args":{"id":"100000000"}}'
  'updateStatus|{"cmd":"updateStatus","args":{}}'
  'updateProbe|{"cmd":"updateProbe","args":{}}'
  'updatePlan|{"cmd":"updatePlan","args":{}}'
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
  ok=$(node -e "
const fs=require('fs');
try{
  const d=JSON.parse(fs.readFileSync('$TMP/out.json','utf8'));
  console.log(d.ok?'1':'0');
  if(!d.ok) console.error(JSON.stringify(d.error).slice(0,90));
}catch(e){ console.log('0'); console.error('解析失败 '+e.message); }
" 2>"$TMP/err")
  if [ "$code" = "200" ] && [ "$ok" = "1" ]; then
    engine=$(node -e "
const fs=require('fs');
const d=(JSON.parse(fs.readFileSync('$TMP/out.json','utf8')).data)||{};
console.log(d.engine||'');
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
