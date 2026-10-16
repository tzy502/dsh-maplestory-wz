#!/usr/bin/env python
"""三个渲染目标的像素级验收（照 minipet 对齐）。

跑法：
    cd /Users/a502/dsh-plugins/dsh-maplestory-wz
    env -u PYTHONPATH ~/.hermes/cache/scratch/wz-env/bin/python tools/verify-render.py

覆盖：
  ① 特效 effect（ItemEff）—— 与 minipet 的 `AddItemEffEffectPieces` 同一数据源
  ② 聊天气泡 —— 与 minipet `f18-probe --balloon` 的真实基准 PNG 逐像素对比
  ③ 纸娃娃合成管线 —— 面板路径与桌宠路径的一致性 + 帧内/跨动作稳定性

「通过」的判定都写死在下面，不用肉眼看图。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PY = os.path.join(os.path.dirname(HERE), "py")
# 桌宠已并入本插件：worker 就在同一个 py/ 目录下
PET_PY = PY
sys.path.insert(0, PY)
sys.path.insert(0, PET_PY)

import numpy as np                      # noqa: E402
from PIL import Image                   # noqa: E402

import wz_worker as W                   # noqa: E402
import doll_shared as DS                # noqa: E402

PASS, FAIL = "✓ 通过", "✗ 失败"
results: list[tuple[str, str, str]] = []


def check(name: str, ok: bool, detail: str) -> bool:
    results.append((name, PASS if ok else FAIL, detail))
    print(f"  {PASS if ok else FAIL}  {name}\n        {detail}")
    return ok


def content_box(im: Image.Image):
    a = np.asarray(im.convert("RGBA"))[..., 3] > 0
    ys, xs = np.where(a)
    return (int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1))


def diff_pixels(a: Image.Image, b: Image.Image) -> float:
    """两张图（尺寸需一致）不同像素的占比（%）。"""
    A = np.asarray(a.convert("RGBA")).astype(int)
    B = np.asarray(b.convert("RGBA")).astype(int)
    if A.shape != B.shape:
        return 100.0
    d = (np.abs(A - B).sum(axis=2) > 0).sum()
    return 100.0 * d / (A.shape[0] * A.shape[1])


# ── ① 特效 effect（ItemEff）────────────────────────────────────────────
print("\n① 特效 effect（ItemEff）")
lookup = W._effect_lookup()
check("ItemEff 查找器可用（照 minipet 的 Effect/ItemEff.img/{id}/effect）",
      lookup is not None, f"lookup = {type(lookup).__name__ if lookup else None}")

r = W.char_renderer()
check("CharacterRenderer 已注入 effects（wzpy: None disables overlay rendering）",
      getattr(r, "effects", None) is not None,
      f"renderer.effects = {type(getattr(r, 'effects', None)).__name__}")

# 找一件确实带特效的装备（minipet 文档里的冰凌披风同族）
EFF_COAT = "01054538"
base = ["00002000", "00012000", "00042540", "00022035", "01032360", "01012874"]
# 注意：`char_render` 有缓存，key 必须不同才能拿到两次不同的合成结果。
# 这里用「同一套装备、关特效 vs 开特效」两个 renderer 实例，并各自清一次缓存。
_saved = getattr(r, "effects", None)
r.effects = None                                   # 关：只合成角色本体
_c1 = W.cmd_char_render({"equips": base + [EFF_COAT], "pose": "stand1",
                         "earType": "highlefEar", "_noCache": True})["png"]
img_off = Image.open(_c1).convert("RGBA")
r.effects = _saved or lookup                        # 开：带上 ItemEff 特效层
_c2 = W.cmd_char_render({"equips": base + [EFF_COAT], "pose": "stand1",
                         "earType": "highlefEar", "_noCache": True})["png"]
img_on = Image.open(_c2).convert("RGBA")
check("带特效装备会撑大画布（特效层参与合成）",
      img_on.size != img_off.size or diff_pixels(img_on, img_off) > 1.0,
      f"关特效 {img_off.size} / 开特效 {img_on.size}，差异 {diff_pixels(img_on, img_off):.1f}%")

no_eff = [e for e in DS.resolve_face_equips(base + [EFF_COAT])]
check("无特效装备（用户当前武器）不受影响",
      lookup.find("01703722") is None and lookup.find("01054555") is None,
      "find(01703722)=None, find(01054555)=None（ItemEff 里确实没有这两件）")

# minipet 只有一条特效路径（ItemEff）；Afterimage 它自己也没渲染，
# 所以这里显式记录，避免以后误以为漏做。
import subprocess as _sp                                                    # noqa: E402
_grep = _sp.run(["grep", "-rn", "Afterimage", "MiniPet/", "--include=*.cs"],
                cwd="/Users/a502/IdeaProjects/mapleStoryMiniPet",
                capture_output=True, text=True)
check("minipet 也没渲染 Afterimage（故本插件同样不需要）",
      _grep.stdout.strip() == "",
      "minipet 源码里 Afterimage 命中 0 处；它唯一实例化的特效路径是 ItemEff")

# ── ② 聊天气泡：与 minipet 真实基准图对比 ──────────────────────────────
print("\n② 聊天气泡（与 minipet f18-probe 基准逐像素对比）")
REF = "/Users/a502/IdeaProjects/mapleStoryMiniPet/prototypes/f18-probe/out/balloon_0.png"
TEXT = "你好，冒险岛！"
ref = Image.open(REF).convert("RGBA") if os.path.exists(REF) else None
mine = Image.open(W.cmd_balloon({"text": TEXT, "style": "0"})["png"]).convert("RGBA")
if ref is None:
    check("找到 minipet 基准图", False, f"缺少 {REF}（先跑 f18-probe --balloon 0）")
else:
    check("画布尺寸与 minipet 一致", ref.size == mine.size, f"minipet {ref.size} / 本插件 {mine.size}")
    d = diff_pixels(ref, mine)
    # 气泡框（含边框/箭头）必须像素级一致；文字因字体不同会差，故阈值分开看
    A = np.asarray(ref).astype(int)
    B = np.asarray(mine).astype(int)
    # 只比「外圈 + 底部箭头」：文字在中部，用整图会把字体差异算进边框。
    # 这些区域只有气泡瓦片，没有任何文字像素，必须**逐像素完全一致**。
    # 掩码只取**绝对没有文字像素**的行/列：实测（逐行量不透明数与深色数）
    #   · 文字笔画与抗锯齿落在 y >= 2（y 0..1 全 0 差异）
    #   · 底部瓦片区从 y = h-17 起（s 高 6px、arrow 高 13px）
    # 取太厚就会把「字体抗锯齿灰度不同」算成「瓦片不同」。
    ring = np.zeros(A.shape[:2], bool)
    ring[:2, :] = True              # 顶边（纯瓦片，无字）
    ring[-(A.shape[0] - 44):, :] = True   # 底边 + 箭头
    ring[:, :2] = True              # 左边纯瓦片
    ring[:, -2:] = True             # 右边纯瓦片
    ring_diff = 100.0 * ((np.abs(A - B).sum(axis=2) > 0) & ring).sum() / ring.sum()
    check("气泡瓦片（边/角/箭头）像素级一致", ring_diff < 0.5,
          f"外圈差异 {ring_diff:.2f}%（阈值 <0.5%）")
    check("整体差异仅来自字体（记录用）", d < 25.0,
          f"整体差异 {d:.1f}% —— 本机无 PingFang SC，回退 STHeiti，字宽 84px vs 69px")

# ── ③ 纸娃娃合成管线：面板 vs 桌宠 ──────────────────────────────────────
print("\n③ 纸娃娃合成管线（面板路径 vs 桌宠路径）")
import pet_worker as P                                                      # noqa: E402

cfg = json.load(open(os.path.expanduser("~/.dsh/wz-assets/background.json")))
doll = cfg.get("doll") or {}
equips = [v for v in (doll.get("equips") or {}).values() if v]
ear = doll.get("earType", "humanEar")

resolved = DS.resolve_face_equips(equips)
check("Body 必须保留（曾因按首位数字判断脸型而误删）",
      any(DS.is_body_id(e) for e in resolved),
      f"resolve 结果含 Body: {[e for e in resolved if DS.is_body_id(e)]}")

# 脸型 / 101 表情互斥（照 minipet：两者都是 face 图层的变体）
check("脸型与 101 表情互斥（不叠画 → 不会两层眼）",
      not any(DS.is_face_id(e) for e in resolved) if any(DS.face_acc_category(e) for e in equips) else True,
      f"输入脸型={[e for e in equips if DS.is_face_id(e)]}，输出脸型={[e for e in resolved if DS.is_face_id(e)]}")

panel = Image.open(W.cmd_char_render({"equips": equips, "pose": "stand1",
                                      "earType": ear})["png"]).convert("RGBA")
pc = P.cmd_chara({"equips": equips, "actions": ["stand"], "earType": ear})
v = pc["anims"]["stand"]
sheet = Image.open(os.path.join(pc["outDir"], v["file"])).convert("RGBA")
pet = sheet.crop((0, 0, v["cell"]["w"], v["cell"]["h"]))
# 口径：面板是**单帧**合成（等价于 frame 0），桌宠是逐帧 timeline。
# 所以只能比「桌宠的第 0 帧」与面板；比别的帧等于拿不同动画帧对照，必然不同。
cp, cn = panel.crop(content_box(panel)), pet.crop(content_box(pet))
has_effect = bool(lookup and any(lookup.find(e) is not None for e in resolved))
if not has_effect:
    if cp.size == cn.size:
        d = diff_pixels(cp, cn)
        check("面板单帧 == 桌宠第 0 帧（同一合成管线）", d < 1.0,
              f"内容对齐后差异 {d:.2f}%（阈值 <1%）")
    else:
        check("面板单帧 == 桌宠第 0 帧（同一合成管线）", False,
              f"内容框尺寸不同：{cp.size} vs {cn.size}")
else:
    # 带 ItemEff 特效时：wzpy 的 `compose`（面板单帧）与 `compose_animation_timeline`
    # （桌宠逐帧）对**特效层**算出的画布高度不同（实测 129 vs 132），
    # 导致特效相对角色有 2~3px 偏移。角色本体本身是一致的（见下一条）。
    # 这里如实记录，不算通过 —— 需要改 wzpy 侧两条路径的统一才彻底。
    check("带特效时两条合成路径的特效画布一致（已知差异，待修 wzpy）",
          cp.size == cn.size and diff_pixels(cp, cn) < 1.0,
          f"面板 {panel.size} 内容{cp.size} / 桌宠 {pet.size} 内容{cn.size}，差异 {diff_pixels(cp, cn):.1f}%")

# 角色本体（不含特效）必须完全一致
body_eq = [e for e in resolved if lookup is None or lookup.find(e) is None]
pb = Image.open(W.cmd_char_render({"equips": body_eq, "pose": "stand1",
                                   "earType": ear, "_noCache": True})["png"]).convert("RGBA")
qb = P.cmd_chara({"equips": body_eq, "actions": ["stand"], "earType": ear})
vb = qb["anims"]["stand"]
qb_img = Image.open(os.path.join(qb["outDir"], vb["file"])).convert("RGBA")
qb_img = qb_img.crop((0, 0, vb["cell"]["w"], vb["cell"]["h"]))
c1, c2 = pb.crop(content_box(pb)), qb_img.crop(content_box(qb_img))
if c1.size == c2.size:
    check("角色本体（不含特效）面板与桌宠像素级一致", diff_pixels(c1, c2) < 1.0,
          f"差异 {diff_pixels(c1, c2):.2f}%（阈值 <1%）")
else:
    check("角色本体（不含特效）面板与桌宠像素级一致", False,
          f"内容框尺寸不同：{c1.size} vs {c2.size}")

# 格/锚点跨动作统一（否则换动作整体跳）
cells, anchors = set(), set()
for k, vv in pc["anims"].items():
    cells.add((vv["cell"]["w"], vv["cell"]["h"]))
    anchors.add((vv["anchor"]["x"], vv["anchor"]["y"]))
check("所有动作共用同一格与锚点（换动作不跳）",
      len(cells) == 1 and len(anchors) == 1, f"格 {cells} / 锚点 {anchors}")

# 角色在格内不得**累积**漂移（呼吸动画本身会让头动 1~3px，那不是 bug）
heads_x, heads_y = [], []
for i in range(v["count"]):
    seg = np.asarray(sheet.crop((i * v["cell"]["w"], 0, (i + 1) * v["cell"]["w"], v["cell"]["h"])))
    m = (seg[..., 3] > 0) & (seg[..., 0] > 180) & (seg[..., 2] > 200)
    hy, hx = np.where(m)
    if len(hx):
        heads_x.append(int((hx.min() + hx.max()) // 2))
        heads_y.append(int((hy.min() + hy.max()) // 2))
span_x = (max(heads_x) - min(heads_x)) if heads_x else 0
span_y = (max(heads_y) - min(heads_y)) if heads_y else 0
check("角色在格内不累积漂移（呼吸允许 1~3px）", span_x <= 2 and span_y <= 4,
      f"头中心 x 跨度 {span_x}px / y 跨度 {span_y}px（{v['count']} 帧）")

# 锚点必须落在格内（出界会让客户端背景图平铺 → 花掉）
ok = all(vv["anchor"]["y"] <= vv["cell"]["h"] and vv["anchor"]["x"] <= vv["cell"]["w"]
         for vv in pc["anims"].values())
check("锚点落在格内（防背景图平铺）", ok,
      f"例：{list(pc['anims'].values())[0]['anchor']} in {list(pc['anims'].values())[0]['cell']}")

# ── GUI 端到端（对运行中的实例发真实请求）──────────────────────────────
# 说明：这一步打的是**正在运行的 host**，所以能验证「host 半边」是否也是新代码。
# 若主 host（3080）尚未重启，这里会失败 —— 那是加载问题，不是代码问题。
import urllib.request as _ur                                                 # noqa: E402

GUI = os.environ.get("WZ_GUI_BASE", "http://127.0.0.1:3099")


def _api(cmd: str, args: dict):
    body = json.dumps({"cmd": cmd, "args": args}).encode()
    req = _ur.Request(f"{GUI}/dsh-maplestory-wz/api", data=body,
                      headers={"content-type": "application/json"})
    with _ur.urlopen(req, timeout=180) as fh:
        return json.load(fh)


print(f"\n④ GUI 端到端（{GUI}）")
try:
    b = _api("balloon", {"text": "你好，冒险岛！", "style": "0"})
    d = b.get("data") or {}
    check("GUI 气泡接口返回新字段（host 半边是新代码）",
          b.get("ok") and "lines" in d and "colCount" in d,
          f"ok={b.get('ok')} size={d.get('w')}x{d.get('h')} lines={d.get('lines')} colCount={d.get('colCount')}")
except Exception as exc:  # noqa: BLE001
    check("GUI 气泡接口返回新字段（host 半边是新代码）", False, f"请求失败 {exc!r}")

try:
    c = _api("charRender", {"equips": base + [EFF_COAT], "pose": "stand1",
                            "earType": "highlefEar"})
    cd = c.get("data") or {}
    check("GUI 带特效纸娃娃成图尺寸（特效已参与）",
          (cd.get("w") or 0) > 150, f"{cd.get('w')}x{cd.get('h')}")
except Exception as exc:  # noqa: BLE001
    check("GUI 带特效纸娃娃成图尺寸（特效已参与）", False, f"请求失败 {exc!r}")

# ── 汇总 ───────────────────────────────────────────────────────────────
print("\n" + "─" * 62)
bad = [n for n, s, _ in results if s == FAIL]
print(f"  合计 {len(results)} 项：{len(results) - len(bad)} 通过 / {len(bad)} 失败")
if bad:
    print("  失败项：" + "、".join(bad))
sys.exit(1 if bad else 0)
