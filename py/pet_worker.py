#!/usr/bin/env python3
"""冒险岛桌宠 worker —— 从本地 WZ 取怪物动作帧，按 origin 锚点合成雪碧图。

协议：stdin/stdout 每行一个 JSON。
  请求  {"id": <int>, "cmd": "<name>", "args": {...}}
  响应  {"id": <int>, "ok": true,  "data": {...}}
        {"id": <int>, "ok": false, "error": {"message": "...", "type": "..."}}

依赖 wzpy（~/.hermes/cache/scratch/wz-python）+ Pillow。数据全部来自本地 WZ。

几何口径**照搬 C# MiniPet**（`Services/WzService.cs: BuildSpriteStrip`）：

  * 逐帧读 `origin`（缺省 (0,0)）、`delay`（缺省 **120**，C# 实体路径的默认值）、
    跳过 null / 1×1 退化占位帧；
  * 联合画布  cw = max(origin.x) + max(w - origin.x)，ch 同理；
    公共 origin = (max(origin.x), max(origin.y))，第 i 帧贴在 (finalOx - ox, finalOy - oy)
    —— 所有帧共用同一锚点，切帧时角色不跳（C# 靠这个做 origin 补偿）；
  * 单行横排（C# 条带也是单行）。

与 C# 的**唯一差异**（有据可查，不是自创）：本客户端 `Mob/_Canvas/{id}.img` 的帧节点
里**根本没有 origin / delay 子节点**（dump 过节点树：stand/0、move/0..2 全是无子节点的
WzCanvasProperty），`Mob.wz` 也只是壳（root.images 为空），`Packs/Mob_00000.ms` 经 wzpy
只能取到动作/帧结构、取不到 origin/delay。此时 C# 的 `origin ?? (0,0)` 会把锚点落到
帧左上角，逐帧裁剪尺寸不同 → 角色上下跳。所以这里 originFallback 默认 `center`
（帧中心，与既有 wz 插件 `compose()` 对无 origin 帧的兜底一致），
置 `topleft` 可 100% 复原 C# 行为。
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
import time
from typing import Any, Dict, Iterable, List, Optional, Tuple

# ── wzpy 定位（可被 WZPY_PATH 覆盖；逐个候选尝试）─────────────────────
WZPY_CANDIDATES = [
    os.environ.get("WZPY_PATH") or "",
    os.path.expanduser("~/.hermes/cache/scratch/wz-python"),
    os.path.expanduser("~/wz-python"),
    "/opt/wz-python",
]

# 帧延迟缺省值：C# WzService.BuildSpriteStrip 的 `GetValueEx<int>(120)`
DEFAULT_FRAME_DELAY = 120
# 帧延迟上限/下限（对齐 C# AnimService 的可用区间，防 WZ 里出现 0/超长）
MIN_FRAME_DELAY = 20
MAX_FRAME_DELAY = 5000


def _load_wzpy() -> str:
    last: Optional[Exception] = None
    for cand in WZPY_CANDIDATES:
        if not cand:
            continue
        if not os.path.isdir(os.path.join(cand, "wzpy")):
            continue
        if cand not in sys.path:
            sys.path.insert(0, cand)
        try:
            from wzpy import open_wz  # noqa: F401
            from wzpy.canvas import decode_canvas  # noqa: F401
            return cand
        except Exception as exc:  # noqa: BLE001
            last = exc
            if cand in sys.path:
                sys.path.remove(cand)
    raise RuntimeError(
        "找不到可用的 wzpy：把 wz-python 放到 ~/.hermes/cache/scratch/wz-python "
        f"或用 WZPY_PATH 指定（最后错误：{last!r}）"
    )


WZPY_DIR = _load_wzpy()

from wzpy import open_wz  # noqa: E402

# 表情覆盖等共用逻辑放在 wz 插件的 py/doll_shared.py。
# 两边合成语义必须一致，否则同一套编号在面板与桌宠上渲染不一样
# （实测：桌宠的脸发暗、皱着 —— 它走 compose_animation，没做表情覆盖）。
_SHARED = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__)))), 'dsh-maplestory-wz', 'py')
if _SHARED not in sys.path:
    sys.path.insert(0, _SHARED)
try:
    import doll_shared  # type: ignore
    import effect_support  # type: ignore
except Exception:  # noqa: BLE001
    doll_shared = None
from wzpy.canvas import decode_canvas  # noqa: E402
from wzpy.properties import (  # noqa: E402
    WzCanvasProperty,
    WzProperty,
    WzStringProperty,
    WzSubProperty,
    WzUolProperty,
    WzVectorProperty,
)
from PIL import Image  # noqa: E402

DATA_DIR = os.environ.get("PET_DATA_DIR") or "/Volumes/SSD/mxd/mxd/Data"
REGION = os.environ.get("PET_REGION") or "BMS"
VERSION = int(os.environ.get("PET_VERSION") or 228)
OUT_DIR = os.environ.get("PET_OUT_DIR") or os.path.expanduser("~/.dsh/desktop-pet")

_roots: Dict[str, Any] = {}
# 合成语义版本：改了合成逻辑就 +1（会让输出文件名变化，从而绕开浏览器缓存）
_COMPOSE_VERSION = 3
_image_cache: Dict[str, Any] = {}
_string_cache: Dict[str, Dict[str, Dict[str, str]]] = {}
_IMAGE_CACHE_MAX = 256
_stats = {"opens": 0, "decodes": 0}


def log(*parts: Any) -> None:
    print("[pet]", *parts, file=sys.stderr, flush=True)


# ── WZ 访问 ───────────────────────────────────────────────────────────
def root_for(pack: str):
    if pack in _roots:
        return _roots[pack]
    base = os.path.join(DATA_DIR, pack)
    if not os.path.exists(base):
        raise FileNotFoundError(f"没有这个分类：{pack}（{base}）")
    t0 = time.time()
    root = open_wz(base, region=REGION, version=VERSION)
    _roots[pack] = root
    _stats["opens"] += 1
    log(f"open {pack}: {time.time() - t0:.2f}s")
    return root


def _parsed_image(root, path: str):
    """取已 parse 的 WzImage（大小写 / 前导零兜底，同既有 wz 插件）。"""
    key = f"{id(root)}::{path}"
    img = _image_cache.get(key)
    if img is None:
        img = root.get(path)
        if img is None:
            parent, _, leaf = path.rpartition("/")
            dirnode = root.get(parent) if parent else root
            images = getattr(dirnode, "images", None)
            if images:
                norm = leaf.lower().lstrip("0")
                for name in images:
                    if name.lower() == leaf.lower() or name.lower().lstrip("0") == norm:
                        img = root.get(f"{parent}/{name}" if parent else name)
                        break
        if img is None:
            return None
        img.parse()
        if len(_image_cache) >= _IMAGE_CACHE_MAX:
            _image_cache.pop(next(iter(_image_cache)))
        _image_cache[key] = img
    return img


def scalar(node: Any) -> Any:
    if isinstance(node, WzVectorProperty):
        return [node.x, node.y]
    if node is None:
        return None
    val = getattr(node, "value", None)
    if isinstance(val, (str, int, float, bool)) or val is None or isinstance(val, list):
        return val
    if isinstance(val, tuple):
        return list(val)
    return None


def _classify(ref: str) -> str:
    first = ref.replace("\\", "/").split("/")[0]
    return first if first in ("Mob", "Npc", "Character", "Item", "Skill", "Effect") else ""


def _resolve_ref(ref: str, pack: str, root, img: Any) -> Optional[Any]:
    """解析 _outlink / _inlink（同既有 wz 插件：本客户端把 _Canvas/ 做成了图片名前缀）。"""
    ref = str(ref).replace("\\", "/").strip("/")
    parts = [p for p in ref.split("/") if p]
    if not parts:
        return None
    if _classify(ref) == "":
        return img.get(ref)
    cands: List[Tuple[str, str]] = [(parts[0], "/".join(parts[1:]))]
    if parts[0] != pack:
        cands.append((pack, ref))
    for pack_name, rel in cands:
        if not rel:
            continue
        try:
            other_root = root_for(pack_name)
        except Exception:  # noqa: BLE001
            continue
        img_path, _, sub = rel.partition(".img")
        if not sub:
            continue
        target_img = _parsed_image(other_root, img_path + ".img")
        if target_img is None:
            continue
        node = target_img.get(sub.lstrip("/"))
        if node is not None:
            return node
    return None


def _canvas_meta(canvas: Any, root, img: Any, pack: str = "") -> Optional[Dict[str, Any]]:
    """解析一个画布节点（含 UOL / _outlink / _inlink 跳转），拿像素与 origin/delay。"""
    cur = canvas
    seen = set()
    for _ in range(8):
        if cur is None or id(cur) in seen:
            return None
        seen.add(id(cur))
        if isinstance(cur, WzUolProperty):
            target = cur.parent.get(str(cur.value)) if cur.parent else None
            if target is None:
                return None
            cur = target
            continue
        if not isinstance(cur, WzCanvasProperty):
            return None
        if not cur.has_pixels() or (cur.width <= 1 and cur.height <= 1):
            link = None
            for key in ("_outlink", "_inlink"):
                holder = cur.child(key)
                if isinstance(holder, WzStringProperty) and holder.value:
                    link = str(holder.value)
                    break
            if link is None:
                return None
            nxt = _resolve_ref(link, pack, root, img)
            if nxt is None:
                return None
            cur = nxt
            continue
        break
    else:
        return None
    if not isinstance(cur, WzCanvasProperty):
        return None
    source = cur
    # C# 口径：origin 先看真实画布节点，再看外层被链接的节点
    origin = source.get("origin") or (canvas.get("origin") if isinstance(canvas, WzCanvasProperty) else None)
    delay = source.get("delay") or (canvas.get("delay") if isinstance(canvas, WzCanvasProperty) else None)
    return {
        "canvas": source,
        "w": int(source.width),
        "h": int(source.height),
        "origin": list(scalar(origin)) if isinstance(origin, WzVectorProperty) else None,
        "delay": scalar(delay),
    }


def _leaves(node: Any, depth: int = 3) -> Iterable[Any]:
    yield node
    if depth <= 0:
        return
    if isinstance(node, (WzSubProperty, WzCanvasProperty)):
        for child in node.children():
            if isinstance(child, (WzCanvasProperty, WzUolProperty, WzSubProperty)):
                yield from _leaves(child, depth - 1)


def _frame_meta(frame: WzProperty, root, pack: str, img: Any) -> Optional[Dict[str, Any]]:
    """一帧的绘制内容：候选里挑面积最大的可解码画布（同既有 wz 插件）。"""
    best = None
    for cand in _leaves(frame, 3):
        if isinstance(cand, WzUolProperty):
            target = cand.parent.get(str(cand.value)) if cand.parent else None
            if target is None:
                continue
            cand = target
        if not isinstance(cand, WzCanvasProperty):
            continue
        meta = _canvas_meta(cand, root, img, pack)
        if meta is None:
            continue
        area = meta["w"] * meta["h"]
        if best is None or area > best["w"] * best["h"]:
            best = meta
    if best is None:
        return None
    best["name"] = frame.name
    return best


def frames_of(node: Any, root, pack: str = "", img: Any = None) -> List[Dict[str, Any]]:
    out: List[Dict[str, Any]] = []
    for child in node.children():
        meta = _frame_meta(child, root, pack, img)
        if meta is not None:
            out.append(meta)
    return out


def animations_of(img: Any, root, pack: str = "") -> List[Dict[str, Any]]:
    out: List[Dict[str, Any]] = []
    for child in img.children():
        if isinstance(child, WzCanvasProperty):
            meta = _canvas_meta(child, root, img, pack)
            if meta is not None:
                out.append({"name": child.name, "frames": 1})
            continue
        frames = frames_of(child, root, pack, img)
        if frames:
            out.append({"name": child.name, "frames": len(frames)})
    return out


# ── Packs/*.ms（Ms V2 / ChaCha20）：怪物的**元数据主源** ────────────────
# 本客户端 `Mob/_Canvas/{id}.img` 只有像素；`origin`/`delay`/`info` 全在
# `Packs/Mob_00000.ms` 的 `Mob/{id}.img` 里（ChaCha20 容器，MsPackageV2 可解）。
# 桌宠的动作帧对齐**依赖 origin**——不读 .ms 就只能靠几何中心兜底，会跳帧。
_ms_packages: Dict[str, Any] = {}


def _open_ms_packages() -> List[Any]:
    if "Mob_00000.ms" in _ms_packages:
        return [_ms_packages[k] for k in sorted(_ms_packages)]
    packs_dir = os.path.join(DATA_DIR, "Packs")
    if not os.path.isdir(packs_dir):
        return []
    try:
        from wzpy.ms_file_v2 import MsPackageV2
    except Exception as exc:  # noqa: BLE001
        log(f"MsPackageV2 不可用：{exc}")
        return []
    for name in sorted(os.listdir(packs_dir)):
        if not (name.lower().endswith(".ms") and name.lower().startswith("mob")):
            continue
        try:
            t0 = time.time()
            _ms_packages[name] = MsPackageV2.open(os.path.join(packs_dir, name), region=REGION)
            log(f"打开 {name}: {time.time() - t0:.1f}s")
        except Exception as exc:  # noqa: BLE001
            log(f"{name} 打开失败：{type(exc).__name__} {exc}")
    return [_ms_packages[k] for k in sorted(_ms_packages)]


def _ms_image(rel: str):
    """在 Mob_*.ms 里按 `Mob/<7位>.img` 取镜像（元数据主源）。"""
    rel = rel.strip("/")
    if rel.startswith("Mob/"):
        rel = rel[4:]
    for pkg in _open_ms_packages():
        try:
            node = pkg.root.get(f"Mob/{rel}")
        except Exception:  # noqa: BLE001
            continue
        if node is not None:
            try:
                node.parse()
            except Exception:  # noqa: BLE001
                pass
            return node
    return None


def _parsed_ms_or_mob(rel: str, root):
    """先 .ms（有 origin/delay/info），再退回 Mob 包（只有像素）。"""
    if rel.endswith(".img") and rel.replace(".img", "").strip("/").isdigit():
        ms = _ms_image(rel)
        if ms is not None:
            return ms, "ms"
    return _parsed_image(root, rel), "wz"


# ── 怪物定位 ──────────────────────────────────────────────────────────
def mob_candidates(id_: str) -> List[str]:
    """候选相对路径：.ms 的元数据主源在前，_Canvas 的像素仓库在后。"""
    id_ = str(id_).strip()
    return [f"{id_.zfill(7)}.img", f"_Canvas/{id_.zfill(7)}.img", f"{id_}.img"]


def find_mob(id_: str):
    """返回 (相对路径, WzImage, 动作表, 用于解析 outlink 的 root)。

    顺序：`Packs/*.ms` 的 `Mob/{id}.img`（含 origin/delay/info）→ 退回
    `Mob/_Canvas/{id}.img`（只有像素）。_ms_image 失败时 root 用 Mob 包，
    因为像素的 `_outlink` 始终指向 `Mob/_Canvas/...`。
    """
    root = root_for("Mob")
    tried = []
    for rel in mob_candidates(id_):
        try:
            img, src = _parsed_ms_or_mob(rel, root)
        except Exception as exc:  # noqa: BLE001
            tried.append(f"{rel}: {type(exc).__name__}")
            continue
        if img is None:
            tried.append(f"{rel}: 不存在")
            continue
        anims = animations_of(img, root, "Mob")
        if anims:
            return rel, img, anims, root
        tried.append(f"{rel}: 无动作")
    raise FileNotFoundError(f"怪物 {id_} 在本地 WZ 里没有可用动作（试过 {'; '.join(tried)}）")


def mob_name(id_: str) -> Optional[str]:
    """String/Mob.img 里的怪物名（编号去前导零）。"""
    try:
        from wzpy.properties import WzSubProperty as _Sub

        if "Mob.img" not in _string_cache:
            root = root_for("String")
            img = _parsed_image(root, "Mob.img")
            table: Dict[str, Dict[str, str]] = {}
            if img is not None:
                for child in img.children():
                    if not isinstance(child, _Sub):
                        continue
                    entry = {c.name: scalar(c) for c in child.children()}
                    table[str(child.name)] = {k: str(v) for k, v in entry.items() if v is not None}
            _string_cache["Mob.img"] = table
        table = _string_cache["Mob.img"]
        entry = table.get(str(id_).lstrip("0")) or table.get(str(id_))
        return entry.get("name") if entry else None
    except Exception as exc:  # noqa: BLE001
        log("名字查询失败:", exc)
        return None


def _mob_table() -> Dict[str, Dict[str, str]]:
    """加载并缓存 String/Mob.img 整表（编号去前导零 → 字段）。"""
    if "Mob.img" not in _string_cache:
        from wzpy.properties import WzSubProperty as _Sub

        root = root_for("String")
        img = _parsed_image(root, "Mob.img")
        table: Dict[str, Dict[str, str]] = {}
        if img is not None:
            for child in img.children():
                if not isinstance(child, _Sub):
                    continue
                entry = {c.name: scalar(c) for c in child.children()}
                table[str(child.name)] = {k: str(v) for k, v in entry.items() if v is not None}
        _string_cache["Mob.img"] = table
    return _string_cache["Mob.img"]


def cmd_search(args: Dict[str, Any]) -> Dict[str, Any]:
    """按中文名/编号搜怪物 —— 换宠用的候选列表。

    只返回**在本地 WZ 里真的有可渲染动作**的候选？不：筛动作要逐个解析 img，太慢。
    这里只给名字与编号，换宠时若目标没动作会由 cmd_pet 如实报错。
    """
    query = str(args.get("query") or "").strip()
    limit = int(args.get("limit") or 40)
    table = _mob_table()
    hits: List[Dict[str, str]] = []
    q = query.lower()
    for mid, fields in table.items():
        name = fields.get("name") or ""
        if not q or q in name.lower() or q == mid or q == mid.lstrip("0"):
            hits.append({"id": mid, "name": name})
            if len(hits) >= limit * 4:
                break
    # 编号短、精确命中优先
    hits.sort(key=lambda h: (h["id"] != query and h["name"] != query, len(h["id"]), h["id"]))
    return {"query": query, "total": len(table), "count": len(hits), "hits": hits[:limit]}


def cmd_presets(_args: Dict[str, Any]) -> Dict[str, Any]:
    """一批「长得像宠物/好看」的默认候选（都要能查到名字，且优先经典怪）。"""
    ids = ["1210102", "1210100", "1210101", "100100", "130100", "210100",
           "300100", "4230101", "5100000", "8140700", "9300018", "9400203",
           "9500390", "9602538", "8880140", "1110101", "2230101", "3210100"]
    table = _mob_table()
    out: List[Dict[str, str]] = []
    for mid in ids:
        key = mid.lstrip("0")
        entry = table.get(key) or table.get(mid)
        if entry and entry.get("name"):
            out.append({"id": key, "name": entry["name"]})
    return {"presets": out, "note": "编号取自经典怪物；查不到名字的会被过滤掉"}


# ── 动作解析与兜底（照搬 AnimService 语义）───────────────────────────
# AnimService.Play 的动作定义：Loop 集合 + fallback 链
LOOP_ACTIONS = ("stand", "move", "fly")
FALLBACKS = {"move": "stand", "fly": "stand", "hit": "stand", "hit1": "stand",
             "say": "stand", "mouse": "stand", "laugh": "stand", "eye": "stand"}
# 名字变体（同 WZ 命名习惯）：语义名 → 候选真实动作名，按序命中
ALIASES = {
    "stand": ["stand", "stand1", "stand0", "stand2"],
    "move": ["move", "walk1", "walk2", "walk"],
    "fly": ["fly", "jump"],
    "hit": ["hit1", "hit", "hit2"],
    "die": ["die1", "die", "die2"],
}


def pick_action(want: str, available: List[str]) -> Optional[str]:
    """把一个语义动作名解析成数据里真实存在的动作名（照搬 AnimService 的兜底顺序）。"""
    names = [a["name"] for a in available]
    lower = {n.lower(): n for n in names}
    for cand in ALIASES.get(want, [want]):
        if cand.lower() in lower:
            return lower[cand.lower()]
    # 前缀命中（例："stand" → "stand3"）
    for n in names:
        if n.lower().startswith(want.lower()):
            return n
    # fallback 链：move/fly/hit → stand；stand 本身再退 move/fly/walk（A9 兜底）
    fb = FALLBACKS.get(want)
    if fb and fb != want:
        got = pick_action(fb, available)
        if got:
            return got
    if want == "stand":
        for alt in ("move", "fly", "walk", "walk1"):
            if alt.lower() in lower:
                return lower[alt.lower()]
    return None


# ── 雪碧图合成（C# BuildSpriteStrip 几何）─────────────────────────────
def compose(frames: List[Dict[str, Any]], origin_fallback: str = "center") -> Tuple[Image.Image, Dict[str, Any]]:
    """按公共 origin 锚点把帧拼成单行雪碧图，返回 (图, 元数据)。"""
    imgs: List[Image.Image] = []
    origins: List[Tuple[int, int]] = []
    delays: List[int] = []
    for fr in frames:
        im = decode_canvas(fr["canvas"], region=REGION).convert("RGBA")
        _stats["decodes"] += 1
        # C#: 跳过 null / 1×1 退化帧
        if im.width <= 1 or im.height <= 1:
            continue
        raw = fr.get("origin")
        if raw:
            ox, oy = int(raw[0]), int(raw[1])
        elif origin_fallback == "topleft":
            ox, oy = 0, 0                     # C# `origin ?? (0,0)`
        else:
            ox, oy = im.width // 2, im.height // 2
        imgs.append(im)
        origins.append((ox, oy))
        delay = fr.get("delay")
        try:
            delay = int(delay)
        except (TypeError, ValueError):
            delay = DEFAULT_FRAME_DELAY
        delays.append(max(MIN_FRAME_DELAY, min(delay, MAX_FRAME_DELAY)))
    if not imgs:
        raise ValueError("没有可解码的帧")

    # 联合画布：cw = maxL + maxR, ch = maxT + maxB；公共 origin = (maxL, maxT)
    max_l = max(o[0] for o in origins)
    max_t = max(o[1] for o in origins)
    max_r = max(im.width - o[0] for im, o in zip(imgs, origins))
    max_b = max(im.height - o[1] for im, o in zip(imgs, origins))
    cw, ch = max(1, max_l + max_r), max(1, max_t + max_b)

    sheet = Image.new("RGBA", (cw * len(imgs), ch), (0, 0, 0, 0))
    for i, (im, (ox, oy)) in enumerate(zip(imgs, origins)):
        sheet.paste(im, (i * cw + (max_l - ox), max_t - oy), im)

    return sheet, {
        "cell": {"w": cw, "h": ch},
        "anchor": {"x": max_l, "y": max_t},
        "cols": len(imgs),
        "rows": 1,
        "count": len(imgs),
        "frames": [{"delay": d} for d in delays],
        "totalMs": sum(delays),
        "sizes": [{"w": im.width, "h": im.height, "origin": list(o)} for im, o in zip(imgs, origins)],
    }


def _cache_key(id_: str, action_name: str, origin_fallback: str) -> str:
    digest = hashlib.sha1(f"{id_}|{action_name}|{origin_fallback}|{DATA_DIR}|{VERSION}".encode()).hexdigest()[:10]
    return f"pet_{str(id_).zfill(7)}_{action_name}_{origin_fallback}_{digest}"


def build_sheet(id_: str, rel: str, img: Any, action_name: str, origin_fallback: str,
                cache: Dict[str, Dict[str, Any]]) -> Dict[str, Any]:
    """合成（或命中磁盘缓存）某动作的雪碧图，返回给 Client 的元数据。"""
    if action_name in cache:
        return cache[action_name]
    stem = _cache_key(id_, action_name, origin_fallback)
    png = os.path.join(OUT_DIR, f"{stem}.png")
    meta_path = os.path.join(OUT_DIR, f"{stem}.json")
    if os.path.exists(png) and os.path.exists(meta_path):
        try:
            with open(meta_path, "r", encoding="utf-8") as fh:
                cached = json.load(fh)
            cached["cached"] = True
            cache[action_name] = cached
            return cached
        except Exception:  # noqa: BLE001
            pass

    node = img.get(action_name)
    if node is None:
        raise FileNotFoundError(f"{rel} 里没有动作 {action_name!r}")
    frames = frames_of(node, root_for("Mob"), "Mob", img) if isinstance(node, WzSubProperty) else (
        [m] if (m := _canvas_meta(node, root_for("Mob"), img, "Mob")) else [])
    if not frames:
        raise FileNotFoundError(f"{rel} 的 {action_name} 没有帧")
    sheet, meta = compose(frames, origin_fallback=origin_fallback)
    os.makedirs(OUT_DIR, exist_ok=True)
    sheet.save(png)
    out = {"file": f"{stem}.png", "action": action_name, "originFallback": origin_fallback, **meta}
    with open(meta_path, "w", encoding="utf-8") as fh:
        json.dump(out, fh, ensure_ascii=False)
    cache[action_name] = out
    return out


# ── 命令 ──────────────────────────────────────────────────────────────
def cmd_info(args: Dict[str, Any]) -> Dict[str, Any]:
    id_ = str(args.get("id") or "1210102")
    rel, img, anims, root = find_mob(id_)
    return {"id": id_, "name": mob_name(id_), "path": f"Mob/{rel}", "actions": anims,
            "source": "packs" if rel.count("/") == 0 else "canvas"}


# ── 纸娃娃桌宠（CharacterRenderer.compose_animation）────────────────────
# 与 minipet 的 PaperdollService 同一套合成语义（zmap 分层 + 逐帧）：
# `compose_animation` 保证所有帧共用画布、肚脐在同一像素，所以每格锚点相同。
# 产物结构与怪物那条完全一致（anims[动作] = {url, cell, anchor, count, delays}），
# 客户端不需要为纸娃娃写第二套渲染。
_char_renderer = None
CHAR_DEFAULT = ["00002000", "00012000", "00030000", "00020000"]
# 语义动作 → 角色真实姿势（对齐 minipet AnimService 的语义名）
CHAR_POSE_ALIASES = {
    "stand": ["stand1", "stand2"],
    "move": ["walk1", "walk2", "run1"],
    "hit1": ["hit1", "hit0"],
    "fly": ["fly", "jump"],
    "die": ["die1"],
    "jump": ["jump"],
    "sit": ["sit"],
    "ladder": ["ladder"],
}


def char_renderer():
    global _char_renderer
    if _char_renderer is None:
        from wzpy.character import CharacterRenderer
        # 第三参 effects：ItemEff 装备特效层（照 minipet 接法；不传则特效不渲染）。
        _char_renderer = CharacterRenderer(root_for("Character"), region=REGION,
                                           effects=_effect_lookup())
        log("桌宠：CharacterRenderer 就绪")
    return _char_renderer


def _effect_lookup():
    """构造 ItemEff 特效查找器；失败或无数据返回 None。

    实现与 wz 插件的 `_effect_lookup` 相同，共用 py/effect_support.py，
    保证桌宠与面板渲染**完全一致**。
    """
    global _EFFECT_LOOKUP
    if _EFFECT_LOOKUP is not None:
        return _EFFECT_LOOKUP if _EFFECT_LOOKUP is not False else None
    try:
        eff_root = root_for("Effect")
        img = _parsed_image(eff_root, "ItemEff.img") if "_parsed_image" in globals() else None
        if img is None:
            _EFFECT_LOOKUP = False
            return None
        from wzpy import open_wz as _open
        wz = _open(os.path.join(DATA_DIR, "Effect"), region=REGION, version=VERSION)
        _EFFECT_LOOKUP = effect_support.LocalEffectLookup(wz, img, region=REGION)
        log("ItemEff 特效层已启用")
    except Exception as exc:  # noqa: BLE001
        log(f"ItemEff 特效层不可用：{exc!r}")
        _EFFECT_LOOKUP = False
        return None
    return _EFFECT_LOOKUP


_EFFECT_LOOKUP: Any = None


def _eq_for(equips):
    return doll_shared.resolve_face_equips(equips) if doll_shared is not None else equips


def _apply_chair_frames(imgs, chair_id: str, eq_, pose: str, flip: bool):
    """把椅子逐帧叠到坐姿上，返回 `(帧列表, 时长列表)`。

    椅子自己有动画：`03019632` 的 `effect` 有 **12 帧、每帧 210ms**（花瓣飘、草摇），
    只画第 0 帧会得到一张死图（用户反馈「椅子会动的 你没实现」）。
    而角色的 `sit` 只有 1 帧（原作如此），所以帧数**取椅子的**，角色帧循环复用。
    """
    try:
        import chair_support
    except Exception:  # noqa: BLE001
        return imgs, []

    try:
        item_root = root_for("Item")

        class _Acc:
            def __call__(self, root, path):
                return _parsed_image(root, path)

            def canvas_meta(self, frame, root):
                return _canvas_meta(frame, root, None, "Item")

        chair = chair_support.render_chair_pieces(
            item_root, _Acc(), decode_canvas, REGION, chair_id)
    except Exception as exc:  # noqa: BLE001
        log(f"椅子 {chair_id} 读取失败：{exc}")
        return imgs, []
    if not chair:
        log(f"椅子 {chair_id} 不存在")
        return imgs, []

    # 动画长度取**所有层里最长的那个**（effect 12 帧、effect2 1 帧 → 12）
    count = max(len(L["frames"]) for L in chair["layers"]) if chair["layers"] else 0
    if count <= 1:
        count = max(1, len(imgs))

    # 每帧时长：取第一层第 i 帧的 delay（缺省 210，与 WZ 里的值一致）
    base_frames = chair["layers"][0]["frames"] if chair["layers"] else []
    delays = [int(base_frames[i]["delay"]) if i < len(base_frames) else 210 for i in range(count)]

    # 角色帧（sit 只有 1 帧）垫到统一大小，再交给 compose_chair_animation 一次出全部帧。
    # **不要逐帧调 compose_with_chair**：那样每帧各算各的并集，画布忽大忽小、
    # 内容在格子里上下跳 —— 就是用户看到的「椅子在抖动」。
    uw = max(im.width for im in imgs)
    uh = max(im.height for im in imgs)
    base = imgs[0].convert("RGBA")
    if base.width != uw or base.height != uh:
        c = Image.new("RGBA", (uw, uh), (0, 0, 0, 0))
        c.alpha_composite(base, (0, 0))
        base = c

    navel = None
    try:
        pls, _a = cr._build_placements(
            eq_, pose=pose, frame=0, ear_type=None,
            hide_hair_full=False, hide_hair_set=frozenset(),
            cap_vslot_tokens=frozenset(), return_anchors=True)
        tops = [p.top_left for p in pls if p.top_left is not None]
        if tops:
            navel = (-min(t[0] for t in tops), -min(t[1] for t in tops))
    except Exception:  # noqa: BLE001
        navel = None
    if navel is None:
        navel = (base.width // 2, base.height)

    try:
        frames, anim_delays = chair_support.compose_chair_animation(
            base, navel, chair, chair_id, has_sit_action=True)
    except Exception as exc:  # noqa: BLE001
        log(f"椅子动画合成失败：{exc}")
        return imgs, delays
    if anim_delays:
        delays = anim_delays
    if frames:
        return frames, delays
    return imgs, delays


def cmd_chara(args: Dict[str, Any]) -> Dict[str, Any]:
    """纸娃娃桌宠：逐帧合成各语义动作的雪碧图，返回与怪物同样的 anims 结构。"""
    import time as _t
    cr = char_renderer()
    equips = [str(e) for e in (args.get("equips") or CHAR_DEFAULT)]
    want = args.get("actions") or ["stand", "move", "hit1"]
    flip = bool(args.get("flip"))
    # 耳型与纸娃娃面板共用同一套取值（humanEar/ear/lefEar/highlefEar）。
    # 头型不支持这个耳型时自动退回 humanEar，避免整只桌宠渲染失败。
    ear_type = str(args.get("earType") or args.get("ear_type") or "").strip() or "humanEar"
    # 椅子（可选）：挂了椅子就自动改坐姿，并给雪碧图叠上椅子层。
    # 坐姿只有 1 帧，所以桌宠会「坐着不动」——正好符合「上了椅子就不用动了」。
    chair_id = str(args.get("chairId") or args.get("chair") or "").strip()
    try:
        head = next((e for e in equips if str(e).lstrip("0").startswith("120")), None)
        if head:
            avail = list(cr.get_ear_types(head))
            if avail and ear_type not in avail:
                log(f"耳型 {ear_type} 不在 {avail}，退回 humanEar")
                ear_type = "humanEar"
    except Exception:  # noqa: BLE001
        ear_type = "humanEar"
    out_anims: Dict[str, Any] = {}
    missing: List[str] = []
    resolved: Dict[str, Any] = {}
    # 第一遍：把每个动作的帧都取出来，之后**统一格宽/格高**。
    # 不统一的话，带披风武器的 move 比 stand 宽 8px，两者 anchor.x 不同，
    # 切换动作时角色会横向跳（用户反馈"抖动"）。
    def _compose(pose_name: str):
        """合成一个姿势：返回 (帧列表, 每帧时长ms)。

        **用 `compose_animation_timeline` 而不是 `compose_animation`。**
        两者差别（wzpy 源码注释原文）：
          - `compose_animation`：只按 body 的帧率出帧，**不渲染 ItemEff 特效**；
          - `compose_animation_timeline`：respects each ItemEff overlay's authored
            frame rate independently of the body's，返回 (images, delays_ms, steps)。
        用户反馈"effect 没加载"就是因为之前用的是前者 —— 特效层根本没参与合成。
        另外 timeline 对 stand 姿势会给 [0,1,2,1] 的呼吸回程，比硬切回第 0 帧更自然。
        """
        eq_ = doll_shared.resolve_face_equips(equips) if doll_shared is not None else equips
        imgs, delays, _steps = cr.compose_animation_timeline(
            eq_, pose=pose_name, flip=flip, ear_type=ear_type)
        # ── 垫齐各帧画布（对齐 wzpy `compose` 单帧的语义）─────────────────
        # 带 ItemEff 特效时，**特效每一帧的尺寸/origin 都不同**
        # （实测帽 01003953：帧0 148×99 origin(30,118)、帧3 148×104 origin(30,120)）。
        # `compose` 单帧会把整个特效动画的并集算进画布，所以面板出图高度恒定（129）；
        # 而 `compose_animation_timeline` 是逐帧独立算的 → 各帧高度在 129~132 之间变，
        # 特效相对角色就会抖 2~3px（实测面板 vs 桌宠差异 17%）。
        # 这里按并集把每帧垫到同一张画布上：每帧内部是同一套「气泡局部坐标」，
        # 所以只要统一左上角基准，相对位置就不会变。
        if imgs:
            uw = max(im.width for im in imgs)
            uh = max(im.height for im in imgs)
            if any(im.width != uw or im.height != uh for im in imgs):
                padded = []
                for im in imgs:
                    if im.width == uw and im.height == uh:
                        padded.append(im)
                        continue
                    canvas = Image.new("RGBA", (uw, uh), (0, 0, 0, 0))
                    canvas.alpha_composite(im, (0, 0))
                    padded.append(canvas)
                imgs = padded
        if not delays:
            try:
                delays = list(cr.pose_frame_delays(eq_, pose_name))
            except Exception:  # noqa: BLE001
                delays = []
        return imgs, delays, _steps

    staged: Dict[str, Any] = {}
    for w in want:
        real = None
        for cand in CHAR_POSE_ALIASES.get(str(w), [str(w)]):
            try:
                imgs, _dl, _st = _compose(cand)
            except Exception:  # noqa: BLE001
                imgs = []
            if imgs and not (len(imgs) == 1 and imgs[0].width <= 1):
                real = cand
                break
        if real is None:
            missing.append(str(w))
            continue
        # 与面板同一套：脸型 / 101 表情互斥（resolve_face_equips），不叠不擦。
        _eq = doll_shared.resolve_face_equips(equips) if doll_shared is not None else equips
        imgs, delays, _steps = _compose(real)
        # 挂了椅子：坐姿 + 叠椅子层（与纸娃娃面板同一个 compose_with_chair）。
        # 「sit」只有 1 帧，所以坐姿雪碧图天然就是静止的。
        if chair_id and imgs and real == "sit":
            chair_imgs, chair_delays = _apply_chair_frames(
                imgs, chair_id, eq_=_eq_for(equips), pose="sit", flip=flip)
            if chair_imgs:
                imgs = chair_imgs
                # 时长也跟着椅子走，否则 12 帧会用角色 sit 的节奏播（错速）
                if chair_delays:
                    delays = chair_delays
        staged[str(w)] = (real, imgs, delays)
    # ── 排版（第二遍）────────────────────────────────────────────────
    # 依据 wzpy `compose_animation` 的**承诺**（源码注释原文）：
    #   「the union of every frame's bbox is taken so all returned images
    #    share canvas dimensions and the navel sits at the same image-space pixel」
    # 即：**同一个动作内各帧尺寸已统一、肚脐在同一像素**。
    # 所以排版只需要「原样摆进等宽格」，不要裁剪、不要重算锚点、**更不要跨动作统一尺寸**。
    # 踩过的坑（两个都导致头身错位）：
    #   ① 按各帧包围盒裁剪再按"下半身中点"居中 → 逐帧偏 1~2px；
    #   ② 取所有动作的最大尺寸当统一格（fly 109×101 vs stand 105×85）→
    #      小动作被垫进大格，锚点按大格算 → 头与身子错位、看着"重合"。
    import numpy as _np

    # ── 统一格（**唯一必要的处理**）────────────────────────────────────
    # 只做一件事：把所有动作的帧放进**同一个尺寸的格**，帧内容**原样摆放不平移**。
    #
    # 为什么不平移：wzpy `compose_animation` 已经保证「同动作内各帧共用画布、
    # 肚脐在同一像素」（源码注释原文），帧内头与身体的相对位置**本来就是对的**。
    # 之前我按「头中心」逐帧平移去"消抖"，结果把头与身体拆开了 ——
    # 实测 walk1 末帧出现「武器与手跑到头顶上面」的错位。
    # 而各帧内容框的差异（walk1 帧0 与帧1/3 不同）是**动画本身的肢体运动**，不是漂移。
    #
    # 为什么必须统一格：客户端 left = snap.x - anchor.x，各动作格/锚点不同的话，
    # 换动作时整体会跳（实测 stand 107×84 anchor(63,84) vs move 109×101 anchor(64,101)
    # → 换动作跳 17~18px）。
    # **挂了椅子就不要跨动作统一格**：
    # 椅子只作用于 `sit`，而带花田的 sit 是 244×207，stand/move 只有 76×90。
    # 统一会把 stand/move 也撑到 244×207 —— 桌宠站/走时套进一个巨大的空画布，
    # 看起来又大又飘（用户反馈「椅子在抖动」的真正来源）。
    # 而「换动作会跳」这个统一格要解决的问题，在有椅子时**不存在**：
    # 客户端坐着时只播 sit（`PET_SITTING` 把动作钉死在 sit），不会跨动作切换。
    if chair_id:
        for w, (real, imgs, delays) in staged.items():
            cw_, ch_ = (imgs[0].width, imgs[0].height) if imgs else (1, 1)
            step = cw_
            sheet = Image.new("RGBA", (len(imgs) * step, ch_), (0, 0, 0, 0))
            frames_meta = []
            for i, im in enumerate(imgs):
                ox = i * step
                oy = 0
                sheet.alpha_composite(im.convert("RGBA"), (ox, oy))
                frames_meta.append({"index": i, "x": ox, "y": oy, "w": im.width,
                                    "h": im.height, "delay": int(delays[i]) if i < len(delays) else 180})
            stem = (f"pet_chara_{'_'.join(e.lstrip('0') or '0' for e in equips)}_{real}"
                    f"{('_chair' + (chair_id.lstrip('0') or '0')) if chair_id else ''}"
                    f"{'_flip' if flip else ''}_v{_COMPOSE_VERSION}")
            png = os.path.join(OUT_DIR, f"{stem}.png")
            sheet.save(png)
            out_anims[str(w)] = {
                # **必须是文件名，不能是路径**：host 用 `encodeURIComponent(file)` 拼
                # `/file/<name>`，塞绝对路径进去会变成 `/file/%2FUsers%2F...` → 404
                # → 桌宠的贴图全部加载失败、页面上一片空白（用户反馈「点击应用以后直接空了」）。
                "file": os.path.basename(png), "action": real, "count": len(imgs),
                "cell": {"w": step, "h": ch_},
                "anchor": {"x": step // 2, "y": ch_},
                "cols": len(imgs), "rows": 1,
                "frames": frames_meta,
                "delays": [int(delays[i]) if i < len(delays) else 180 for i in range(len(imgs))],
                "totalMs": sum(int(delays[i]) if i < len(delays) else 180 for i in range(len(imgs))),
            }
        # URL 由 host 侧统一拼（与正常路径同一处），这里只给 file 路径
        return {"kind": "chara", "id": "chara:" + ",".join(equips), "name": "纸娃娃",
                "earType": ear_type, "path": "Character/<equips>",
                "actions": [{"name": k, "frames": v["count"]} for k, v in out_anims.items()],
                "originFallback": "center", "defaultDelay": 180,
                "anims": {k: {**v, "url": f"/dsh-maplestory-wz/file/{os.path.basename(v['file'])}"}
                          for k, v in out_anims.items()},
                "resolved": resolved, "missing": missing, "outDir": OUT_DIR,
                "equips": equips, "chairId": chair_id}

    sizes = [(v[1][0].width, v[1][0].height) for v in staged.values() if v[1]] or [(1, 1)]
    cell_w = max(w_ for w_, _h in sizes)
    cell_h = max(h_ for _w, h_ in sizes)
    anchor_x = cell_w // 2
    anchor_y = cell_h            # 脚底贴格底

    for w, (real, imgs, delays) in staged.items():
        step = cell_w
        sheet = Image.new("RGBA", (len(imgs) * step, cell_h), (0, 0, 0, 0))
        frames_meta = []
        for i, im in enumerate(imgs):
            ox = i * step + max(0, (cell_w - im.width) // 2)   # 水平居中
            oy = max(0, cell_h - im.height)                    # 脚底贴格底
            # **必须用 alpha_composite，不能用 paste(im, pos, im)**：
            # `paste` 传自身当 mask 时会把 alpha **平方**（半透明像素变暗、透明像素渗色），
            # 排版前后 md5 就不同了（实测 imgs[0].md5=9514667030 → sheet0.md5=6b0be55c62）。
            # 对一张全透明画布做 alpha_composite 等价于「原样复制像素」，
            # 与 wzpy `compose` 单帧的合成语义一致。
            sheet.alpha_composite(im.convert("RGBA"), (ox, oy))
            frames_meta.append({"index": i, "x": i * step, "y": 0,
                                "w": step, "h": cell_h, "ox": ox, "oy": oy})
        os.makedirs(OUT_DIR, exist_ok=True)
        # 文件名必须带**椅子编号**：带椅子时 sit 是 12 帧（椅子自己的动画），
        # 不带时只有 1 帧，两者帧数不同却同名 → 开关椅子会命中同一张旧图。
        stem = (f"pet_chara_{'_'.join(e.lstrip('0') or '0' for e in equips)}_{real}"
                f"{('_chair' + (chair_id.lstrip('0') or '0')) if chair_id else ''}"
                f"{'_flip' if flip else ''}_v{_COMPOSE_VERSION}")
        png = os.path.join(OUT_DIR, f"{stem}.png")
        sheet.save(png)
        out_anims[str(w)] = {
            "file": os.path.basename(png),
            "action": real,
            "count": len(imgs),
            "cell": {"w": cell_w, "h": cell_h},
            "frames": frames_meta,
            "anchor": {"x": anchor_x, "y": anchor_y},
            "cols": len(imgs), "rows": 1,
            "delays": [int(delays[i]) if i < len(delays) else 180 for i in range(len(imgs))],
            "totalMs": sum(int(delays[i]) if i < len(delays) else 180 for i in range(len(imgs))),
        }
        resolved[str(w)] = {**out_anims[str(w)], "requested": str(w), "resolved": real,
                            "exact": real == str(w)}
    return {"kind": "chara", "id": "chara:" + ",".join(equips), "name": "纸娃娃",
            "earType": ear_type,
            "path": "Character/<equips>", "actions": [{"name": k, "frames": v["count"]} for k, v in out_anims.items()],
            "originFallback": "center", "defaultDelay": 180,
            # 合并后与资源插件同一个 file 路由（host 侧 ROUTE_PREFIX = /dsh-maplestory-wz）
            "anims": {k: {**v, "url": f"/dsh-maplestory-wz/file/{v['file']}"} for k, v in out_anims.items()},
            "resolved": resolved, "missing": missing, "outDir": OUT_DIR, "equips": equips}


def cmd_pet(args: Dict[str, Any]) -> Dict[str, Any]:
    """一次拿齐：怪物名 + 动作表 + 客户端要的每个动作的雪碧图元数据。"""
    id_ = str(args.get("id") or "1210102")
    want = args.get("actions") or ["stand", "move", "hit1"]
    origin_fallback = str(args.get("originFallback") or "center")
    if origin_fallback not in ("center", "topleft"):
        origin_fallback = "center"
    rel, img, anims, root = find_mob(id_)
    cache: Dict[str, Dict[str, Any]] = {}
    resolved: Dict[str, Any] = {}
    missing: List[str] = []
    for w in want:
        real = pick_action(str(w), anims)
        if not real:
            missing.append(str(w))
            continue
        try:
            sheet = build_sheet(id_, rel, img, real, origin_fallback, cache)
        except Exception as exc:  # noqa: BLE001
            log(f"合成 {id_}/{w} 失败：{exc}")
            missing.append(str(w))
            continue
        resolved[str(w)] = {**sheet, "requested": str(w), "resolved": real,
                            "exact": real.lower() == str(w).lower()}
    return {
        "id": id_,
        "name": mob_name(id_),
        "path": f"Mob/{rel}",
        "actions": anims,
        "originFallback": origin_fallback,
        "defaultDelay": DEFAULT_FRAME_DELAY,
        "anims": resolved,
        "missing": missing,
        "outDir": OUT_DIR,
    }


def _source_fingerprint() -> Dict[str, str]:
    """本目录 .py 的 sha1 前 12 位——host 用它判断常驻 worker 是否跑旧代码。"""
    import hashlib
    here = os.path.dirname(os.path.abspath(__file__))
    out: Dict[str, str] = {}
    for name in sorted(os.listdir(here)):
        if name.endswith(".py"):
            try:
                with open(os.path.join(here, name), "rb") as fh:
                    out[name] = hashlib.sha1(fh.read()).hexdigest()[:12]
            except OSError:
                continue
    return out


SOURCE_FINGERPRINT = _source_fingerprint()


def cmd_stats(_args: Dict[str, Any]) -> Dict[str, Any]:
    return {"data_dir": DATA_DIR, "region": REGION, "version": VERSION,
            "out_dir": OUT_DIR, "wzpy": WZPY_DIR, "opens": _stats["opens"], "decodes": _stats["decodes"],
            "fingerprint": SOURCE_FINGERPRINT, "pid": os.getpid()}


COMMANDS = {"info": cmd_info, "pet": cmd_pet, "stats": cmd_stats, "chara": cmd_chara,
            "search": cmd_search, "presets": cmd_presets}


def main() -> None:
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        req_id = None
        try:
            req = json.loads(line)
            req_id = req.get("id")
            cmd = req.get("cmd")
            fn = COMMANDS.get(cmd)
            if fn is None:
                raise ValueError(f"未知命令 {cmd!r}；可用：{', '.join(COMMANDS)}")
            data = fn(req.get("args") or {})
            sys.stdout.write(json.dumps({"id": req_id, "ok": True, "data": data}, ensure_ascii=False) + "\n")
        except Exception as exc:  # noqa: BLE001
            sys.stdout.write(json.dumps(
                {"id": req_id, "ok": False, "error": {"message": str(exc), "type": type(exc).__name__}},
                ensure_ascii=False) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
