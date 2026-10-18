#!/usr/bin/env bash
# 发布到 npm 官方源（市场要显示「安装」按钮就必须有 npm 包）。
#
# 前置：
#   1) 先跑 bash tools/npm-login.sh 登录官方源
#   2) package.json 里不能有 "private": true（脚本会检查并提示）
#
# 用法：
#   bash tools/npm-publish.sh            # 真发布
#   bash tools/npm-publish.sh --dry-run  # 只打包看清单，不发布
set -euo pipefail

cd "$(dirname "$0")/.."
NPMRC="${HOME}/.npmrc-publish"
REG='https://registry.npmjs.org'
DRY=""
[ "${1:-}" = "--dry-run" ] && DRY="--dry-run"

echo "==> 发布前检查"

# 1) 登录
if ! npm --userconfig "$NPMRC" whoami --registry="$REG" >/dev/null 2>&1; then
  echo "    ✗ 未登录官方源。先跑： bash tools/npm-login.sh"
  exit 1
fi
echo "    ✓ 已登录：$(npm --userconfig "$NPMRC" whoami --registry="$REG")"

# 2) private 标志：npm 会拒绝发布 private 包
if node -e "process.exit(JSON.parse(require('fs').readFileSync('package.json','utf8')).private?0:1)"; then
  echo "    ✗ package.json 里 private=true，npm 会拒绝发布。"
  echo "      改法：删掉 package.json 的 \"private\": true 这一行，然后重跑本脚本。"
  echo "      （本地安装用的是 link:，删掉它不影响本机已装好的 profile）"
  exit 1
fi
echo "    ✓ 没有 private 标志"

# 3) 必填元数据
node -e "
const j=JSON.parse(require('fs').readFileSync('package.json','utf8'));
const need=['name','version','license','repository','description'];
const miss=need.filter(k=>!j[k]);
if(miss.length){console.log('    ✗ 缺字段：'+JSON.stringify(miss));process.exit(1);}
console.log('    ✓ '+j.name+'@'+j.version+'  license='+j.license);
"

# 4) 打包清单（files 白名单是否把该带的都带上）
echo
echo "==> 将要发布的文件（npm pack --dry-run）"
npm --userconfig "$NPMRC" pack --dry-run 2>&1 | tail -30

echo
if [ -n "$DRY" ]; then
  echo "==> --dry-run 模式，未真正发布"
  exit 0
fi

echo "==> 正式发布到 ${REG}"
npm --userconfig "$NPMRC" publish --registry="$REG" --access public

echo
echo "==> 验证（等镜像同步几秒）"
sleep 5
curl -s -m 30 "https://registry.npmjs.org/$(node -e "console.log(JSON.parse(require('fs').readFileSync('package.json','utf8')).name)")" \
  | node -e "
let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{
  try{const j=JSON.parse(s);console.log('    ✓ 已在 npm 上：',j.name,JSON.stringify(j['dist-tags']));}
  catch(e){console.log('    ? 查询失败（可能还没同步）：',e.message);}
});"
echo
echo "市场安装命令："
echo "    dsh plugin --profile web add $(node -e "console.log(JSON.parse(require('fs').readFileSync('package.json','utf8')).name)")"
