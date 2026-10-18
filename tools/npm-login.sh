#!/usr/bin/env bash
# 登录 npm 官方源（发布用）。
#
# 为什么要独立配置文件：
#   本机 ~/.npmrc 的 registry 指向 registry.npmmirror.com（阿里镜像），
#   那是**只读镜像，不能发布**。而日常装包用镜像是好事，不该动。
#   所以这里用一个专用 npmrc：登录与发布都走官方源，日常配置一行不改。
#
# 用法：
#   bash tools/npm-login.sh
#
# 登录方式：**浏览器授权**（唯一推荐）。
#
# 为什么不推荐 token：npm 自 2026-07-31 起，**创建 token 本身就需要交互式 2FA**
# （见 https://github.blog/changelog/2026-07-31-restricting-npm-bypass-2fa-granular-access-tokens/ ），
# 而 2027-01 起绕过 2FA 的 token 会**失去直接发布权**。
# 所以「临时发布一个包」用浏览器登录最省事，也不需要先开 2FA。
set -euo pipefail

NPMRC="${HOME}/.npmrc-publish"
REG='https://registry.npmjs.org'

echo "==> 使用配置文件：${NPMRC}"
touch "$NPMRC"

# 这个文件只放官方源设置，不影响 ~/.npmrc
if ! grep -q "^registry=${REG}$" "$NPMRC" 2>/dev/null; then
  printf 'registry=%s\n' "$REG" >> "$NPMRC"
  echo "    已写入 registry=${REG}"
else
  echo "    registry 已是 ${REG}"
fi

echo
echo "==> 当前登录状态"
if npm --userconfig "$NPMRC" whoami 2>/dev/null; then
  echo "    已登录，无需重复登录。要换账号就先删掉 ${NPMRC} 里的 //registry.npmjs.org/:_authToken 行。"
  exit 0
fi
echo "    未登录，开始登录流程"

echo
echo "==> 选择登录方式"
echo "    1) 浏览器授权（推荐）——会打印一个网址，浏览器里点确认即可"
echo "    2) 粘贴 Access Token ——**已不推荐**：npm 现在建 token 也要交互式 2FA，"
read -r -p "    输入 1 或 2 [1]: " CHOICE
CHOICE="${CHOICE:-1}"

if [ "$CHOICE" = "2" ]; then
  echo
  read -r -s -p "    粘贴 token（输入时不显示）: " TOKEN
  echo
  if [ -z "${TOKEN}" ]; then echo "    token 为空，退出"; exit 1; fi
  printf '//registry.npmjs.org/:_authToken=%s\n' "$TOKEN" >> "$NPMRC"
  chmod 600 "$NPMRC"
  echo "    已写入 token（文件权限 600）"
else
  echo
  echo "    接下来 npm 会打印一个网址。在浏览器打开、登录并点 Authorize。"
  echo "    如果本机 npm 支持自动开浏览器，它会自己弹。"
  echo
  npm --userconfig "$NPMRC" login --auth-type=web --registry="$REG"
fi

echo
echo "==> 验证登录结果"
npm --userconfig "$NPMRC" whoami --registry="$REG" | sed 's/^/    当前账号: /'
echo
echo "完成。接下来可以发布："
echo "    bash tools/npm-publish.sh"
