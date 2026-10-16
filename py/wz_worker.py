#!/usr/bin/env python3
"""冒险岛 WZ 资源读取 worker（DSH 插件后端）。

协议：stdin/stdout 每行一个 JSON。
  请求  {"id": <int>, "cmd": "<name>", "args": {...}}
  响应  {"id": <int>, "ok": true, "data": {...}}
        {"id": <int>, "ok": false, "error": {"message": "...", "type": "..."}}

依赖 wzpy（Leonana69/wz-python）+ Pillow。所有数据来自本地 WZ，无数据库、无硬编码。
"""
from __future__ import annotations

import base64
import io
import json
import math
import os
import sys
import time
import traceback
from typing import Any, Dict, Iterable, List, Optional, Tuple

# ── wzpy 定位（可被 WZPY_PATH 覆盖；逐个候选尝试）─────────────────────
WZPY_CANDIDATES = [
    os.environ.get("WZPY_PATH") or "",
    os.path.expanduser("~/.hermes/cache/scratch/wz-python"),
    os.path.expanduser("~/wz-python"),
    "/opt/wz-python",
]


def _load_wzpy():
    last = None
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
            from wzpy.wz_package import resolve_canvas_link  # noqa: F401
            return cand
        except Exception as exc:  # noqa: BLE001
            last = exc
            if cand in sys.path:
                sys.path.remove(cand)
    raise RuntimeError(
        "找不到可用的 wzpy：把 wz-python 克隆放到 ~/.hermes/cache/scratch/wz-python "
        f"或用 WZPY_PATH 指定（最后错误：{last!r}）"
    )


WZPY_DIR = _load_wzpy()


def _source_fingerprint() -> Dict[str, str]:
    """本文件（+ sibling 模块）的 sha1 前 12 位。

    为什么需要：常驻 worker 一旦被 host 复用了旧进程，改完 .py 也不会生效，
    表现为「代码明明改了、接口还返回旧结果」。把指纹回传给 host 做比对，
    不一致就是需要重启 worker，别再靠猜。
    """
    import hashlib
    here = os.path.dirname(os.path.abspath(__file__))
    out: Dict[str, str] = {}
    for name in sorted(os.listdir(here)):
        if not name.endswith(".py"):
            continue
        try:
            with open(os.path.join(here, name), "rb") as fh:
                out[name] = hashlib.sha1(fh.read()).hexdigest()[:12]
        except OSError:
            continue
    return out


SOURCE_FINGERPRINT = _source_fingerprint()

from wzpy import open_wz  # noqa: E402
from wzpy.canvas import decode_canvas  # noqa: E402
from wzpy.properties import (  # noqa: E402
    WzCanvasProperty,
    WzProperty,
    WzStringProperty,
    WzSubProperty,
    WzUolProperty,
    WzVectorProperty,
)
from wzpy.wz_package import resolve_canvas_link  # noqa: E402
from PIL import Image, ImageFilter  # noqa: E402

DATA_DIR = os.environ.get("WZ_DATA_DIR") or "/Volumes/SSD/mxd/mxd/Data"
REGION = os.environ.get("WZ_REGION") or "BMS"
VERSION = int(os.environ.get("WZ_VERSION") or 228)
OUT_DIR = os.environ.get("WZ_OUT_DIR") or os.path.expanduser("~/.dsh/wz-assets")
# 地图「世界单位 → 背景图像素」的目标比例上限：一张图最多这么多像素宽，避免超大纹理
MAX_MAP_WORLD = float(os.environ.get("WZ_MAP_MAX_PX") or 4096)

PACKS = [
    "Base", "Character", "Effect", "Etc", "Item", "Map", "Mob", "Morph",
    "Npc", "Quest", "Reactor", "Skill", "Sound", "String", "TamingMob", "UI",
]
STRING_TABLES = ["Mob.img", "Npc.img", "Eqp.img", "Map.img", "Skill.img", "Consume.img", "Ins.img", "Etc.img", "Cash.img"]
IMAGE_ACTIONS = ("stand", "stand0", "stand1", "move", "walk1", "jump", "alert", "hit1", "die1")

# ── cmsdl 资源更新（sibling 模块；只做「探测二进制 / 解析 --help 开关面 / 拼 argv」，
#    不联网、不 spawn 长任务——真正的下载执行与进度在 host 侧 lib/cmsdl.js）──────
_WORKER_DIR = os.path.dirname(os.path.abspath(__file__))
if _WORKER_DIR not in sys.path:
    sys.path.insert(0, _WORKER_DIR)
import cmsdl_support  # noqa: E402
import chair_support
import doll_shared  # noqa: E402
import effect_support  # noqa: E402

cmd_cmsdl_probe = cmsdl_support.cmd_cmsdl_probe
cmd_cmsdl_plan = cmsdl_support.cmd_cmsdl_plan

# ── 缓存 ──────────────────────────────────────────────────────────────
_roots: Dict[str, Any] = {}
_string_root: Optional[Any] = None
_string_cache: Dict[str, Dict[str, Dict[str, str]]] = {}
_image_cache: Dict[str, Any] = {}
_IMAGE_CACHE_MAX = 256
_stats = {"opens": 0, "images": 0, "decodes": 0}


def log(*parts: Any) -> None:
    print("[wz]", *parts, file=sys.stderr, flush=True)


def root_for(pack: str):
    """按分类打开数据根（分层包 / 单文件），结果缓存。"""
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


def string_root():
    global _string_root
    if _string_root is None:
        _string_root = root_for("String")
    return _string_root


def split_path(path: str) -> Tuple[str, str]:
    parts = [p for p in str(path).replace("\\", "/").split("/") if p]
    if not parts:
        raise ValueError("空路径")
    pack = parts[0]
    rest = "/".join(parts[1:])
    if pack not in PACKS:
        raise ValueError(f"未知分类 {pack!r}；可用：{', '.join(PACKS)}")
    return pack, rest


def _parsed_image(root, path: str):
    key = f"{id(root)}::{path}"
    img = _image_cache.get(key)
    if img is None:
        # 怪物优先取 Packs/*.ms 的 `Mob/<id>.img`：那里才有 info/origin/delay。
        # 放在这里统一兜住，cmd_get / cmd_frames / cmd_sprite / cmd_image 就都通用了。
        if path.endswith(".img") and path.replace(".img", "").strip("/").isdigit():
            ms = _ms_image(path)
            if ms is not None:
                if len(_image_cache) >= _IMAGE_CACHE_MAX:
                    _image_cache.pop(next(iter(_image_cache)))
                _image_cache[key] = ms
                _stats["images"] += 1
                return ms
        img = root.get(path)
        if img is None:
            # 大小写 / 补零兜底：在父目录里找同名（忽略大小写或去前导零）
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
        _stats["images"] += 1
        if len(_image_cache) >= _IMAGE_CACHE_MAX:
            _image_cache.pop(next(iter(_image_cache)))
        _image_cache[key] = img
    return img


def node_kind(node: Any) -> str:
    if isinstance(node, WzCanvasProperty):
        return "canvas"
    if isinstance(node, WzUolProperty):
        return "uol"
    if isinstance(node, WzSubProperty):
        return "sub"
    if node is None:
        return "none"
    return "value"


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


# ── 帧/画布解析 ───────────────────────────────────────────────────────
def _classify(ref: str) -> str:
    first = ref.replace("\\", "/").split("/")[0]
    return first if first in PACKS else ""


def _resolve_ref(ref: str, pack: str, root, img: Any) -> Optional[Any]:
    """解析 _outlink / _inlink。

    不能直接用 wzpy.wz_package.resolve_canvas_link：它按 subdirs 逐级下走，而本
    客户端（傲天 BMS 228）把 ``Weapon/``、``_Canvas/`` 做成了**图片名前缀**而不是
    子目录，目录树上根本没有这些节点 → 跨卷链接会解析失败。
    这里改成「优先按图片路径取」，两级兜底。
    """
    ref = str(ref).replace("\\", "/").strip("/")
    if not ref:
        return None
    parts = [p for p in ref.split("/") if p]
    if not parts:
        return None
    # _inlink 是 img 内相对路径
    if _classify(ref) == "":
        node = img.get(ref)
        return node
    cands: List[Tuple[str, str]] = []
    if parts[0] == pack:
        cands.append((parts[0], "/".join(parts[1:])))
    else:
        cands.append((parts[0], "/".join(parts[1:])))
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
    """解析一个画布节点（含 _outlink/_inlink/UOL 跳转），拿尺寸+锚点。"""
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
            # 占位（1×1）或空 → 跟着链接走
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
    origin = source.get("origin") or (canvas.get("origin") if isinstance(canvas, WzCanvasProperty) else None)
    delay = source.get("delay") or (canvas.get("delay") if isinstance(canvas, WzCanvasProperty) else None)
    z = source.get("z")
    link = None
    for holder in (canvas, source):
        if isinstance(holder, WzCanvasProperty):
            o = holder.child("_outlink")
            if isinstance(o, WzStringProperty) and o.value:
                link = str(o.value)
                break
    return {
        "canvas": source,
        "w": int(source.width),
        "h": int(source.height),
        "origin": list(scalar(origin)) if isinstance(origin, WzVectorProperty) else None,
        "delay": scalar(delay),
        "z": str(z.value) if isinstance(z, WzStringProperty) else None,
        "link": link,
    }


def _leaves(node: Any, depth: int = 3) -> Iterable[Any]:
    """往下最多 depth 层找可绘制的候选节点。"""
    yield node
    if depth <= 0:
        return
    if isinstance(node, (WzSubProperty, WzCanvasProperty)):
        for child in node.children():
            if isinstance(child, (WzCanvasProperty, WzUolProperty, WzSubProperty)):
                yield from _leaves(child, depth - 1)


def _frame_meta(frame: WzProperty, root, pack: str, img: Any) -> Optional[Dict[str, Any]]:
    """取一帧的绘制内容：候选里挑面积最大的可解码画布。"""
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


def frames_of(node: WzProperty, root, pack: str = "", img: Any = None) -> List[Dict[str, Any]]:
    out: List[Dict[str, Any]] = []
    for child in node.children():
        meta = _frame_meta(child, root, pack, img)
        if meta is not None:
            out.append(meta)
    return out


def animations_of(img: Any, root, pack: str = "") -> List[Dict[str, Any]]:
    """列出 img 的动作及帧数。"""
    out: List[Dict[str, Any]] = []
    direct = [c for c in img.children() if isinstance(c, (WzSubProperty, WzCanvasProperty))]
    for child in direct:
        if isinstance(child, WzCanvasProperty):
            meta = _canvas_meta(child, root, img, pack)
            if meta is not None:
                out.append({"name": child.name, "frames": 1, "kind": "canvas", "w": meta["w"], "h": meta["h"]})
            continue
        frames = frames_of(child, root, pack, img)
        if frames:
            out.append({
                "name": child.name, "frames": len(frames), "kind": "anim",
                "w": max(f["w"] for f in frames), "h": max(f["h"] for f in frames),
            })
    return out


# ── 雪碧图 / GIF 合成 ─────────────────────────────────────────────────
def compose(frames: List[Dict[str, Any]], scale: int = 1, padding: int = 2) -> Tuple[Image.Image, List[Dict[str, Any]]]:
    """按 origin 锚点把帧对齐拼成一张雪碧图，返回 (图, 帧矩形元数据)。"""
    scale = max(1, min(int(scale), 8))
    images: List[Image.Image] = []
    boxes: List[Tuple[int, int, int, int]] = []
    for fr in frames:
        im = decode_canvas(fr["canvas"], region=REGION).convert("RGBA")
        _stats["decodes"] += 1
        base_w, base_h = im.width, im.height
        ox, oy = (fr["origin"] or [base_w // 2, base_h // 2])
        ox, oy = int(ox) * scale, int(oy) * scale
        if scale != 1:
            im = im.resize((base_w * scale, base_h * scale), Image.NEAREST)
        images.append(im)
        boxes.append((ox, oy, im.width, im.height))
    if not images:
        raise ValueError("没有可解码的帧")
    # 锚点空间：图片占据 [-ox, w-ox) × [-oy, h-oy)
    spans = [(-b[0], -b[1], b[2], b[3]) for b in boxes]
    x0 = min(s[0] for s in spans)
    y0 = min(s[1] for s in spans)
    x1 = max(s[0] + s[2] for s in spans)
    y1 = max(s[1] + s[3] for s in spans)
    canvas_w = max(1, x1 - x0)
    canvas_h = max(1, y1 - y0)
    # 单行优先；整图过大才折行，避免超宽 PNG
    cols = len(images)
    while cols > 1 and cols * (canvas_w + padding) > 4096:
        cols = (cols + 1) // 2
    rows = (len(images) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * (canvas_w + padding) + padding, rows * (canvas_h + padding) + padding), (0, 0, 0, 0))
    out_frames: List[Dict[str, Any]] = []
    for idx, (im, span) in enumerate(zip(images, spans)):
        col, row = idx % cols, idx // cols
        px = padding + col * (canvas_w + padding) + span[0] - x0
        py = padding + row * (canvas_h + padding) + span[1] - y0
        # 全透明画布 + alpha_composite = 原样复制像素；paste(im,pos,im) 会把 alpha 平方
        sheet.alpha_composite(im.convert("RGBA"), (px, py))
        out_frames.append({
            "index": idx,
            "x": px, "y": py, "w": span[2], "h": span[3],
            "origin": [boxes[idx][0] // scale, boxes[idx][1] // scale],
            "delay": int(frames[idx].get("delay") or 100),
        })
    meta = {
        "sheet": {"w": sheet.width, "h": sheet.height},
        "frame": {"w": canvas_w, "h": canvas_h},
        "padding": padding,
        "scale": scale,
        "frames": out_frames,
    }
    return sheet, meta


def _gif_frame(fr: Dict[str, Any], scale: int) -> Tuple[Image.Image, int, int]:
    im = decode_canvas(fr["canvas"], region=REGION).convert("RGBA")
    ox, oy = (fr["origin"] or [im.width // 2, im.height // 2])
    return im, int(ox) * scale, int(oy) * scale  # origin 取自原始尺寸，再按 scale 放缩


def write_gif(frames: List[Dict[str, Any]], path: str, scale: int = 1, max_side: int = 480) -> Dict[str, Any]:
    scale = max(1, min(int(scale), 8))
    raw = []
    for fr in frames:
        im, ox, oy = _gif_frame(fr, scale)
        if scale != 1:
            im = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
        raw.append((im, ox, oy, int(fr.get("delay") or 100)))
    # 与 compose 同一套锚点几何
    spans = [(-ox, -oy, im.width, im.height) for im, ox, oy, _ in raw]
    gx0 = min(s[0] for s in spans)
    gy0 = min(s[1] for s in spans)
    gx1 = max(s[0] + s[2] for s in spans)
    gy1 = max(s[1] + s[3] for s in spans)
    w, h = max(1, gx1 - gx0), max(1, gy1 - gy0)
    if max(w, h) > max_side:
        k = max_side / max(w, h)
        w, h = max(1, int(w * k)), max(1, int(h * k))
    zoom = (w / (gx1 - gx0)) if gx1 > gx0 else 1.0
    gif_frames = []
    for (im, ox, oy, delay), span in zip(raw, spans):
        layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        if abs(zoom - 1.0) > 1e-6:
            im = im.resize((max(1, int(im.width * zoom)), max(1, int(im.height * zoom))), Image.LANCZOS)
        layer.alpha_composite(im.convert("RGBA"),
                              (max(0, int((span[0] - gx0) * zoom)), max(0, int((span[1] - gy0) * zoom))))
        alpha = layer.getchannel("A")
        p = layer.convert("RGB").convert("P", palette=Image.ADAPTIVE, colors=255)
        mask = alpha.point(lambda a: 255 if a <= 128 else 0)
        p.paste(255, mask)
        gif_frames.append((p, max(20, min(delay, 5000))))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    gif_frames[0][0].save(
        path, save_all=True, append_images=[f[0] for f in gif_frames[1:]],
        duration=[f[1] for f in gif_frames], loop=0, transparency=255, disposal=2, optimize=False,
    )
    return {"gif": path, "w": w, "h": h, "frames": len(gif_frames)}


# ── 命令实现 ──────────────────────────────────────────────────────────
def cmd_packs(_args: Dict[str, Any]) -> Dict[str, Any]:
    out = []
    for pack in PACKS:
        folder = os.path.join(DATA_DIR, pack)
        if not os.path.isdir(folder):
            continue
        vols = []
        for name in sorted(os.listdir(folder)):
            full = os.path.join(folder, name)
            if os.path.isdir(full):
                vols.append({"name": name + "/", "dir": True, "mb": None})
            elif name.lower().endswith(".wz"):
                vols.append({"name": name, "mb": round(os.path.getsize(full) / 1e6, 1)})
        out.append({"pack": pack, "volumes": vols})
    return {"data_dir": DATA_DIR, "region": REGION, "version": VERSION, "wzpy": WZPY_DIR, "packs": out}


def cmd_tree(args: Dict[str, Any]) -> Dict[str, Any]:
    pack = str(args.get("pack") or "").strip()
    if not pack:
        # 空 pack = **数据源根目录**：列出全部分类。
        # 面板默认停在这里（用户口径：切换浏览默认不该直接钻进 Mob/）。
        base = DATA_DIR
        rows = []
        for name in sorted(os.listdir(base)):
            full = os.path.join(base, name)
            if name.startswith("."):
                continue
            if os.path.isdir(full):
                rows.append({"name": name, "kind": "dir"})
            elif name.endswith(".wz"):
                rows.append({"name": name, "kind": "dir"})
        return {"pack": "", "path": "", "kind": "root", "children": rows,
                "data_dir": base, "note": "数据源根目录：挑一个分类进入"}
    root = root_for(pack)
    path = (args.get("path") or "").strip("/")
    node = root.root if not path else root.root.get(path)
    if node is None:
        raise FileNotFoundError(f"{pack}/{path} 不存在")
    kind = "dir" if hasattr(node, "subdirs") else "image"
    children: List[Dict[str, Any]] = []
    if kind == "dir":
        for name in list(getattr(node, "subdirs", []))[:400]:
            children.append({"name": name, "kind": "dir"})
        images = list(getattr(node, "images", []))
        for name in images[: int(args.get("limit") or 400)]:
            children.append({"name": name, "kind": "image"})
        total = len(images)
    else:
        node.parse()
        for child in node.children():
            children.append({
                "name": child.name,
                "kind": node_kind(child),
                "value": scalar(child) if node_kind(child) == "value" else None,
                "size": [child.width, child.height] if isinstance(child, WzCanvasProperty) else None,
            })
        total = len(children)
    return {"pack": pack, "path": path, "kind": kind, "total": total, "children": children}


def _string_map(table: str) -> Dict[str, Dict[str, str]]:
    if table in _string_cache:
        return _string_cache[table]
    img = string_root().root.get(table)
    result: Dict[str, Dict[str, str]] = {}
    if img is not None:
        img.parse()
        kids = img.children()
        top = kids[0] if len(kids) == 1 and isinstance(kids[0], WzSubProperty) and kids[0].name in (
            "Eqp", "Consume", "Ins", "Etc", "Cash", "Pet", "Map", "Skill") else None
        groups = top.children() if top is not None else kids
        for group in groups:
            if not isinstance(group, WzSubProperty):
                continue
            if top is None:
                entry = group
                fields = {c.name: c.value for c in entry.children()}
                result[entry.name] = {"group": table.split(".")[0], **{k: str(v) for k, v in fields.items() if v is not None}}
            else:
                for entry in group.children():
                    fields = {c.name: c.value for c in entry.children()}
                    result[entry.name] = {"group": group.name, **{k: str(v) for k, v in fields.items() if v is not None}}
    _string_cache[table] = result
    return result


def _hit(id_: str, fields: Dict[str, str], query: str) -> Optional[str]:
    q = query.strip()
    if not q:
        return None
    if id_ == q or id_.lstrip("0") == q.lstrip("0"):
        return "id"
    for key, val in fields.items():
        if key.startswith("_"):
            continue
        if q.lower() in str(val).lower():
            return key
    return None


def cmd_search(args: Dict[str, Any]) -> Dict[str, Any]:
    query = str(args["query"])
    limit = int(args.get("limit") or 30)
    wanted = args.get("kind") or "all"
    table_for = {
        "mob": "Mob.img", "npc": "Npc.img", "eqp": "Eqp.img", "map": "Map.img",
        "skill": "Skill.img", "consume": "Consume.img", "etc": "Etc.img", "cash": "Cash.img", "ins": "Ins.img",
    }
    kinds = list(table_for) if wanted in ("all", None) else [wanted]
    hits: List[Dict[str, Any]] = []
    t0 = time.time()
    for kind in kinds:
        table = table_for.get(kind)
        if table is None:
            continue
        try:
            entries = _string_map(table)
        except Exception as exc:  # noqa: BLE001
            log(f"string {table} 失败: {exc}")
            continue
        for id_, fields in entries.items():
            why = _hit(id_, fields, query)
            if why is None:
                continue
            name = fields.get("name") or fields.get("mapName") or fields.get("streetName") or ""
            hits.append({
                "id": id_, "kind": kind, "table": table, "name": name,
                "group": fields.get("group"), "matched": why,
                "info": {k: v for k, v in fields.items() if k != "group"},
            })
            if len(hits) >= limit * 3:
                break
    # 精确命中优先，其次 id 升序
    hits.sort(key=lambda h: (h["matched"] != "id", len(h["id"]), h["id"]))
    return {"query": query, "count": len(hits), "hits": hits[:limit], "elapsed": round(time.time() - t0, 2)}


def _split_anchor(spec: str) -> Tuple[str, Optional[str]]:
    """"stand" / "stand#2" / "2" → (动作, 帧号或 None)。"""
    if "#" in spec:
        a, _, f = spec.partition("#")
        return a, f
    return spec, None


def cmd_get(args: Dict[str, Any]) -> Dict[str, Any]:
    pack, rest = split_path(args["path"])
    root = root_for(pack)
    img = _parsed_image(root, rest)
    if img is None:
        raise FileNotFoundError(f"{args['path']} 不存在")
    anims = animations_of(img, root, pack)
    info: Dict[str, Any] = {}
    info_node = img.get("info")
    if isinstance(info_node, WzSubProperty):
        for child in info_node.children():
            if node_kind(child) == "value":
                info[child.name] = scalar(child)
    name = None
    id_ = os.path.basename(rest).replace(".img", "")
    for table in ("Mob.img", "Npc.img"):
        try:
            entry = _string_map(table).get(id_.lstrip("0")) or _string_map(table).get(id_)
        except Exception:  # noqa: BLE001
            entry = None
        if entry:
            name = entry.get("name")
            break
    return {"path": f"{pack}/{rest}", "id": id_, "name": name, "animations": anims, "info": info}


def cmd_frames(args: Dict[str, Any]) -> Dict[str, Any]:
    pack, rest = split_path(args["path"])
    root = root_for(pack)
    anim, want = _split_anchor(str(args.get("animation") or "stand"))
    entry = _parsed_image(root, rest)
    if entry is None:
        raise FileNotFoundError(f"{args['path']} 不存在")
    node = entry.get(anim) if anim else None
    if node is None:
        for cand in animations_of(entry, root, pack):
            anim = cand["name"]
            node = entry.get(anim)
            break
    if node is None:
        raise FileNotFoundError(f"{args['path']} 里没有动作 {anim!r}；可用：{[a['name'] for a in animations_of(entry, root, pack)]}")
    frames = frames_of(node, root, pack, entry) if isinstance(node, WzSubProperty) else ([m] if (m := _canvas_meta(node, root, entry, pack)) else [])
    if want is not None:
        frames = [f for f in frames if f["name"] == want or f["name"].lstrip("0") == want.lstrip("0")]
    if not frames:
        raise FileNotFoundError(f"{args['path']} 的 {anim} 没有帧")
    return {
        "path": f"{pack}/{rest}", "animation": anim, "count": len(frames),
        "frames": [{"name": f["name"], "w": f["w"], "h": f["h"], "origin": f["origin"], "delay": f["delay"], "link": f["link"]} for f in frames],
        "_raw": frames,
    }


def cmd_sprite(args: Dict[str, Any]) -> Dict[str, Any]:
    args = dict(args)
    frames = cmd_frames(args)
    raw = frames.pop("_raw")
    scale = int(args.get("scale") or 1)
    sheet, meta = compose(raw, scale=scale)
    key = f"{args['path']}|{frames['animation']}|{scale}|{len(raw)}"
    digest = str(abs(hash(key)) % (10 ** 12))
    os.makedirs(OUT_DIR, exist_ok=True)
    png_path = os.path.join(OUT_DIR, f"sprite_{digest}.png")
    sheet.save(png_path)
    out = {"path": frames["path"], "animation": frames["animation"], "scale": scale, **meta, "png": png_path}
    if args.get("gif"):
        gif_path = os.path.join(OUT_DIR, f"anim_{digest}.gif")
        out["gif"] = write_gif(raw, gif_path, scale=scale, max_side=int(args.get("max_side") or 480))["gif"]
    if args.get("save_dir"):
        save_dir = os.path.expanduser(str(args["save_dir"]))
        os.makedirs(save_dir, exist_ok=True)
        stem = f"{os.path.basename(frames['path']).replace('.img','')}_{frames['animation']}"
        png_copy = os.path.join(save_dir, f"{stem}.sheet.png")
        sheet.save(png_copy)
        out["saved_png"] = png_copy
        if out.get("gif"):
            gif_copy = os.path.join(save_dir, f"{stem}.gif")
            with open(out["gif"], "rb") as src, open(gif_copy, "wb") as dst:
                dst.write(src.read())
            out["saved_gif"] = gif_copy
    return out


def cmd_image(args: Dict[str, Any]) -> Dict[str, Any]:
    """单帧原图直出 PNG 字节（供 HTTP 路由 / <img> 使用）。"""
    pack, rest = split_path(args["path"])
    root = root_for(pack)
    node = _parsed_image(root, rest)
    if node is None:
        raise FileNotFoundError(f"{args['path']} 不存在")
    anim, want = _split_anchor(str(args.get("animation") or ""))
    scale = max(1, min(int(args.get("scale") or 1), 8))
    if anim:
        target = node.get(anim)
        if target is None:
            raise FileNotFoundError(f"没有动作 {anim!r}")
        if isinstance(target, WzCanvasProperty) or node_kind(target) == "canvas":
            meta = _canvas_meta(target, root, node, pack)
            im = decode_canvas(meta["canvas"], region=REGION) if meta else None
        else:
            frames = frames_of(target, root, pack, node)
            if want is not None:
                frames = [f for f in frames if f["name"] == want or f["name"].lstrip("0") == want.lstrip("0")]
            if not frames:
                raise FileNotFoundError("该动作没有可解码帧")
            im = decode_canvas(frames[0]["canvas"], region=REGION)
    elif isinstance(node, WzCanvasProperty):
        meta = _canvas_meta(node, root, node, pack)
        im = decode_canvas(meta["canvas"], region=REGION) if meta else None
    else:
        frames = None
        for anim_entry in animations_of(node, root, pack):
            sub = node.get(anim_entry["name"])
            if isinstance(sub, WzSubProperty):
                got = frames_of(sub, root, pack, node)
                if got:
                    frames = got
                    break
            elif isinstance(sub, WzCanvasProperty):
                meta = _canvas_meta(sub, root, node, pack)
                if meta:
                    frames = [meta]
                    break
        if not frames:
            raise FileNotFoundError("没有可解码的内容")
        im = decode_canvas(frames[0]["canvas"], region=REGION)
    if im is None:
        raise FileNotFoundError("没有可解码的像素")
    _stats["decodes"] += 1
    if scale != 1:
        im = im.resize((im.width * scale, im.height * scale), Image.NEAREST)
    buf = io.BytesIO()
    im.convert("RGBA").save(buf, "PNG")
    return {"png_base64": base64.b64encode(buf.getvalue()).decode("ascii"), "w": im.width, "h": im.height}


# ── 纸娃娃（CharacterRenderer：按装备编号合成角色帧）─────────────────────
# 与 mapleStoryMiniPet 的 CharaSim / CharacterRenderer 同一套语义：给一串装备编号
# （Body/Head/Hair/Face/Coat/Pants/Shoes/Weapon/Cap…），按 zmap 顺序合成一张 RGBA。
# 部位编号是 8 位补零，CharacterRenderer 也接受去零写法。
_char_renderer = None
_char_parts_cache: Dict[str, List[Dict[str, Any]]] = {}
# 合成语义版本：改了合成逻辑就 +1，旧缓存自动失效
_COMPOSE_VERSION = 2
_char_image_cache: Dict[str, str] = {}


def char_renderer():
    global _char_renderer
    if _char_renderer is None:
        from wzpy.character import CharacterRenderer
        root = root_for("Character")
        # Character 是分层包；CharacterRenderer 会尝试读 Base/zmap.img，拿不到用内置 zmap
        # 第三参 effects：ItemEff 装备特效层。**必须传**，否则特效永远不渲染
        # （wzpy 注释：None disables overlay rendering）。做法照 minipet
        # （PaperdollService.AddItemEffEffectPieces 读 Effect/ItemEff.img/{id}/effect）。
        _char_renderer = CharacterRenderer(root, region=REGION, effects=_effect_lookup())
        log("CharacterRenderer 就绪")
    return _char_renderer


_eqp_name_table_cache: Optional[Dict[str, Dict[str, str]]] = None


def _eqp_name_table() -> Dict[str, Dict[str, str]]:
    """`String/Eqp.img` 的「编号(去前导零) → {name, group}」表。"""
    global _eqp_name_table_cache
    if _eqp_name_table_cache is None:
        try:
            _eqp_name_table_cache = _string_map("Eqp.img")
        except Exception as exc:  # noqa: BLE001
            log(f"Eqp 名表加载失败：{exc}")
            _eqp_name_table_cache = {}
    return _eqp_name_table_cache


def cmd_char_parts(args: Dict[str, Any]) -> Dict[str, Any]:
    """纸娃娃可选部位清单（默认给常用几类，按需取别的）。"""
    cr = char_renderer()
    # wzpy 的 `CATEGORIES` 一共 16 类，全给：
    # 之前只放 12 类，漏了 Longcoat（套服）/FaceAcc（脸饰）/Glass（眼镜）/Earring（耳环）；
    # 而且名字当时写成中文习惯（帽子/披风/手套），wzpy 要的是 Cap/Cape/Glove。
    wanted = args.get("categories") or [
        "Body", "Head", "Hair", "Face",
        "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove",
        "Cape", "Shield", "FaceAcc", "Glass", "Earring", "Weapon",
    ]
    limit = int(args.get("limit") or 40)
    out: Dict[str, Any] = {}
    for cat in wanted:
        if cat not in _char_parts_cache:
            try:
                _char_parts_cache[cat] = cr.list_parts(str(cat))
            except Exception as exc:  # noqa: BLE001
                log(f"list_parts({cat}) 失败：{exc}")
                _char_parts_cache[cat] = []
        items = _char_parts_cache[cat]
        # 中文名从 String/Eqp.img 取（list_parts 只给编号）。
        # 装备表按「去前导零」存键（2000 / 30000 / 1332020），所以两种写法都试。
        table = _eqp_name_table()
        named = []
        for i in items:
            raw = str(i.get("id"))
            key = raw.lstrip("0") or "0"
            entry = table.get(key) or table.get(raw) or {}
            named.append({"id": raw, "short": key,
                          "name": i.get("name") or entry.get("name") or "",
                          "group": entry.get("group") or cat})
        # 关键词搜索必须在**全量**上做：之前前端只过滤返回的前 120 条，
        # 「芙莉莲」这种排在 4000+ 条帽子里的永远轮不到（用户实测搜不到）。
        q = str(args.get("q") or "").strip().lower()
        if q:
            named = [r for r in named
                     if q in r["name"].lower() or q in r["id"] or q in (r["short"] or "")]
        out[cat] = {"total": len(items), "matched": len(named), "items": named[:limit]}
    return {"categories": out,
            "defaults": {"Body": "00002000", "Head": "00012000",
                         "Hair": "00030000", "Face": "00020000"},
            "note": "name 取自 String/Eqp.img（有的部位如 Body 没有名字）"}


def _effect_lookup():
    """构造 ItemEff 特效查找器（失败/无数据返回 None，静默降级）。

    见 py/effect_support.py：本地数据结构下 `EffectLookup.open()` 的自检会失败，
    所以用插件自己的 `_parsed_image` 取 ItemEff.img，再交给 LocalEffectLookup。
    """
    global _EFFECT_LOOKUP
    if _EFFECT_LOOKUP is not None:
        return _EFFECT_LOOKUP if _EFFECT_LOOKUP is not False else None
    try:
        eff_root = root_for("Effect")
        img = _parsed_image(eff_root, "ItemEff.img")
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


def cmd_chair_list(args: Dict[str, Any]) -> Dict[str, Any]:
    """椅子清单（纸娃娃「椅子」槽位的候选）。

    椅子不是 Character 部件（在 `Item/Install/*.img`），wzpy 的类目表里没有它，
    所以这里自己扫：按 `Item/Install/{id//1000:05d}.img` 分卷找 0301xxxx，名字取
    `String/Ins.img`（键不补零）。
    """
    import chair_support
    root = root_for("Item")
    sroot = root_for("String")
    parsed = _parsed_image

    class _Acc:
        def canvas_meta(self, frame, r):
            return None

    # String/Ins.img 的每个条目是 **WzSubProperty**（不是字符串节点），
    # 名字在它的 `name` 子节点里（形如 {name: "变出花田的魔法椅子", desc: ...}）。
    # 第一版按 `value` 读 → 全 None → 2500 把椅子全显示「（无名字）」。
    names = {}
    try:
        ins = parsed(sroot, "Ins.img")
        if ins is not None:
            for c in ins.children():
                key = str(getattr(c, "name", c))
                val = getattr(c, "value", None)
                if isinstance(val, str) and val:
                    names[key] = val
                    continue
                try:
                    sub = c.get("name")
                except Exception:  # noqa: BLE001
                    sub = None
                v2 = getattr(sub, "value", None) if sub is not None else None
                if isinstance(v2, str) and v2:
                    names[key] = v2
    except Exception:  # noqa: BLE001
        pass

    # 椅子分卷名 = id // 1000 补 5 位（实测 03019.img 装了 696 把）。
    # 只有 4 位/6 位两种旧版格式，一并试一下。
    out = []
    seen = set()
    for gi in range(3000, 3050):
        for path in (f"Install/{gi:05d}.img", f"Install/{gi // 10:04d}.img", f"Install/{gi * 10:06d}.img"):
            img = parsed(root, path)
            if img is None:
                continue
            try:
                kids = [str(getattr(c, "name", c)) for c in img.children()]
            except Exception:  # noqa: BLE001
                continue
            for k in kids:
                n = k.lstrip("0")
                if not n.startswith("301") or k in seen:
                    continue
                seen.add(k)
                out.append({"id": k, "name": names.get(k) or names.get(n) or "（无名字）"})
    out.sort(key=lambda r: r["id"])
    q = str(args.get("query") or "").strip()
    if q:
        out = [r for r in out if q in r["name"] or q in r["id"]]
    limit = int(args.get("limit") or 200)
    return {"chairs": out[:limit], "total": len(out)}


class _ChairAccess:
    """把 worker 的画布解码能力包成 chair_support 期望的接口。

    chair_support 刻意不 import worker（避免循环依赖），所以由这里注入：
      - `__call__(root, path)` → 取 WzImage（与 `_parsed_image` 同源）
      - `canvas_meta(frame, root)` → 画布宽高/origin/link（与 `_canvas_meta` 同源）
    """

    def __call__(self, root, path):
        return _parsed_image(root, path)

    def canvas_meta(self, frame, root):
        return _canvas_meta(frame, root, None, "Item")


def _chair_piece_cache():
    global _CHAIR_CACHE
    try:
        return _CHAIR_CACHE
    except NameError:
        _CHAIR_CACHE = {}
        return _CHAIR_CACHE


def _apply_chair(img, equips, pose: str, flip: bool, chair_id: str):
    """把椅子按 minipet 的层级/偏移叠到角色帧上，返回**扩展过画布**的新图。

    navel 的像素位置取自渲染器自己的 `_build_placements(..., return_anchors=True)`：
    navel 是世界原点 (0,0)，所以它在合成图里就是 `(-min_x, -min_y)`。
    这比从图上找肚脐可靠得多（实测红十字正好落在腰部）。
    """
    try:
        chair = chair_support.render_chair_pieces(
            root_for("Item"), _ChairAccess(), decode_canvas, REGION, chair_id)
    except Exception as exc:  # noqa: BLE001
        logger.warning("椅子 %s 读取失败：%s", chair_id, exc)
        return img
    if not chair:
        logger.warning("椅子 %s 不存在（Item/Install 下没找到）", chair_id)
        return img

    cr = char_renderer()
    navel = None
    try:
        pls, _anchors = cr._build_placements(
            equips, pose=pose, frame=0, ear_type=None,
            hide_hair_full=False, hide_hair_set=frozenset(),
            cap_vslot_tokens=frozenset(), return_anchors=True)
        tops = [p.top_left for p in pls if p.top_left is not None]
        if tops:
            navel = (-min(t[0] for t in tops), -min(t[1] for t in tops))
    except Exception as exc:  # noqa: BLE001
        logger.warning("取 navel 失败（椅子将按画布中心兜底）：%s", exc)
    if navel is None:
        navel = (img.width // 2, img.height)

    try:
        return chair_support.compose_with_chair(
            # frame 也驱动椅子帧：椅子的 effect 有 12 帧，面板按 frame 取单帧时
            # 应该拿到对应那一帧（否则永远第 0 帧）
            img, navel, chair, chair_id, frame_index=int(frame or 0),
            has_sit_action=_has_action(equips, "sit"))
    except Exception as exc:  # noqa: BLE001
        logger.warning("椅子合成失败：%s", exc)
        return img


def _apply_chair_anim(imgs, equips, chair_id: str, flip: bool, count_only: bool = False):
    """逐帧把椅子叠上去，返回 `(帧列表, 时长列表)`。

    椅子自己有动画（`03019632` 花田椅 `effect` 12 帧 × 210ms），只画第 0 帧会得到死图。
    角色 `sit` 只有 1 帧，所以帧数取椅子的，角色帧循环复用。
    `count_only=True` 时只算时长、不合成图（给 delays 用，省一次渲染）。
    """
    import chair_support
    try:
        chair = chair_support.render_chair_pieces(
            root_for("Item"), _ChairAccess(), decode_canvas, REGION, chair_id)
    except Exception as exc:  # noqa: BLE001
        logger.warning("椅子 %s 读取失败：%s", chair_id, exc)
        return ([] if count_only else imgs), []
    if not chair or not chair.get("layers"):
        return ([] if count_only else imgs), []

    count = max(len(L["frames"]) for L in chair["layers"])
    base = chair["layers"][0]["frames"]
    delays = [int(base[i]["delay"]) if i < len(base) else 210 for i in range(count)]
    if count_only:
        return [], delays
    if count <= 1:
        count = max(1, len(imgs))

    cr = char_renderer()
    navel = None
    try:
        pls, _a = cr._build_placements(
            equips, pose="sit", frame=0, ear_type=None,
            hide_hair_full=False, hide_hair_set=frozenset(),
            cap_vslot_tokens=frozenset(), return_anchors=True)
        tops = [p.top_left for p in pls if p.top_left is not None]
        if tops:
            navel = (-min(t[0] for t in tops), -min(t[1] for t in tops))
    except Exception:  # noqa: BLE001
        navel = None

    uw = max(im.width for im in imgs)
    uh = max(im.height for im in imgs)
    base = imgs[0].convert("RGBA")
    if base.width != uw or base.height != uh:
        c = Image.new("RGBA", (uw, uh), (0, 0, 0, 0))
        c.alpha_composite(base, (0, 0))
        base = c
    nav = navel or (base.width // 2, base.height)
    try:
        frames, anim_delays = chair_support.compose_chair_animation(
            base, nav, chair, chair_id, has_sit_action=True)
    except Exception as exc:  # noqa: BLE001
        logger.warning("椅子动画合成失败：%s", exc)
        return imgs, delays
    if anim_delays:
        delays = anim_delays
    return (frames or imgs), delays


def _has_action(equips, action: str) -> bool:
    """角色是否有该动作（minipet `ResolveChairAction` 用它决定偏移分支）。"""
    try:
        # wzpy 只有 pose_frame_delays（没有 pose_frame_count）：非空即代表有该动作
        return len(char_renderer().pose_frame_delays(action, equips[0] if equips else None) or []) > 0
    except Exception:  # noqa: BLE001
        return True


def cmd_char_render(args: Dict[str, Any]) -> Dict[str, Any]:
    """按装备编号合成角色帧（纸娃娃）。equips 给几个部位就画几个。"""
    cr = char_renderer()
    equips = [str(e) for e in (args.get("equips") or [])]
    if not equips:
        equips = ["00002000", "00012000", "00030000", "00020000"]
    pose = str(args.get("pose") or "stand1")
    flip = bool(args.get("flip"))
    frame = int(args.get("frame") or 0)
    # 椅子（可选）：纸娃娃可挂一把椅子，角色自动改坐姿并叠上椅子层。
    # 层级与偏移照 minipet `PaperdollService`（见 chair_support 模块注释）。
    chair_id = str(args.get("chairId") or args.get("chair") or "").strip()
    if chair_id:
        pose = "sit"
    # 耳型：Head 里可选的耳朵 canvas 名（humanEar/ear/lefEar/highlefEar）。
    # 人类头型自带圆耳，所以默认 humanEar；想要精灵耳等就显式传。
    ear_type = str(args.get("earType") or args.get("ear_type") or "").strip() or None
    # 先按 face 图层的互斥语义整理（有 101 表情就丢掉脸型），见 doll_shared 注释
    resolved = doll_shared.resolve_face_equips(equips)
    # 缓存 key 必须带上**合成语义版本**：之前改了合成逻辑但 key 没变，
    # 旧缓存被直接命中 → 改了等于没改（用户反馈"重启了但是没用"）。
    key = json.dumps({"v": _COMPOSE_VERSION, "e": resolved, "p": pose, "f": flip,
                      "fr": frame, "ear": ear_type, "chair": chair_id}, sort_keys=True)
    os.makedirs(OUT_DIR, exist_ok=True)
    # `_noCache`：验收脚本要「同输入、不同渲染器状态」各出一张图，
    # 而缓存 key 只含输入参数，会命中同一张 → 必须能显式跳过缓存。
    if args.get("_noCache"):
        cached = None
    else:
        cached = _char_image_cache.get(key)
    if cached and os.path.exists(cached):
        with Image.open(cached) as im:
            return {"png": cached, "w": im.width, "h": im.height, "cached": True,
                    "equips": resolved, "equipsInput": equips,
                    "pose": pose, "flip": flip, "frame": frame,
                    "earType": ear_type}
    compose_kw = {"pose": pose, "flip": flip, "frame": frame}
    if ear_type:
        compose_kw["ear_type"] = ear_type
    # 注意：要先把**不含表情**的帧合成出来，再让 overlay 把表情「区域替换」上去。
    # 之前先 compose(equips)（已含表情）再 overlay，等于把相同像素贴回原处 →
    # 覆盖没生效，底脸的暗瞳继续透出来（用户反馈"脸配置有问题"）。
    # 脸型与 101 表情是 face 图层的**互斥变体**（依据见 doll_shared 注释），
    # 所以这里只用 resolve_face_equips 整理一次，不再做叠加/擦除补丁。
    # 面板也走 `compose_animation_timeline` 取对应帧 —— 与桌宠**完全同源**。
    # 为什么不用 `compose` 单帧：带 ItemEff 特效时，`compose` 只把「当前帧」的特效
    # 范围算进画布（实测帽 01003953：compose 得 184×129，而 timeline 各帧并集是 184×132），
    # 特效动画播到高帧时会被裁掉 3px，且与桌宠的出图对不上（实测差异 17%）。
    # timeline 用整个特效周期的并集，两条路径就一致了。
    _imgs, _delays, _steps = cr.compose_animation_timeline(
        resolved, pose=pose, flip=flip, ear_type=ear_type)
    if not _imgs:
        img = cr.compose(resolved, **compose_kw)
    else:
        _want = max(0, min(int(frame), len(_imgs) - 1))
        # 各帧垫齐到并集画布（与 pet_worker 同一处理，保证两边像素一致）
        _uw = max(im.width for im in _imgs)
        _uh = max(im.height for im in _imgs)
        img = Image.new("RGBA", (_uw, _uh), (0, 0, 0, 0))
        img.alpha_composite(_imgs[_want].convert("RGBA"), (0, 0))
    if chair_id:
        img = _apply_chair(img, resolved, pose, flip, chair_id)
    if img.width <= 1 and img.height <= 1:
        raise ValueError(
            f"合成结果为空（{len(equips)} 个编号都没落上）。检查编号是否存在于 "
            "Character/<类目>/<8位补零>.img，或用 char_parts 取合法编号。"
        )
    digest = str(abs(hash(key)) % (10 ** 12))
    path = os.path.join(OUT_DIR, f"chara_{digest}.png")
    img.convert("RGBA").save(path)
    if len(_char_image_cache) > 120:
        _char_image_cache.pop(next(iter(_char_image_cache)))
    _char_image_cache[key] = path
    return {"png": path, "w": img.width, "h": img.height, "cached": False,
            "equips": resolved, "equipsInput": equips,
            "pose": pose, "flip": flip, "frame": frame,
            "earType": ear_type}


def cmd_char_anim(args: Dict[str, Any]) -> Dict[str, Any]:
    """纸娃娃**逐帧**合成一张横向雪碧图（桌宠直接吃这个格式）。

    `compose_animation` 保证所有帧同一个画布尺寸、肚脐在同一像素——
    所以每格的锚点都相同，等于桌宠眼里的 origin。
    产物字段与 cmd_sprite 对齐（sheet/frame/frames[].x/y/w/h/origin/delay），
    这样桌宠客户端不需要为纸娃娃写第二套渲染。
    """
    cr = char_renderer()
    equips = [str(e) for e in (args.get("equips") or [])]
    if not equips:
        equips = ["00002000", "00012000", "00030000", "00020000"]
    pose = str(args.get("pose") or "stand1")
    flip = bool(args.get("flip"))
    frames = args.get("frames")
    frame_tuple = tuple(int(f) for f in frames) if frames else None
    chair_id = str(args.get("chairId") or args.get("chair") or "").strip()
    if chair_id:
        pose = "sit"
    t0 = time.time()
    resolved_anim = doll_shared.resolve_face_equips(equips)
    imgs = cr.compose_animation(resolved_anim, pose=pose, flip=flip, frames=frame_tuple)
    if not imgs:
        raise ValueError("纸娃娃逐帧合成返回空列表（编号可能都不存在）")
    # 挂了椅子：椅子自己有动画（花田椅 12 帧），逐帧叠上去再出图，
    # 否则面板预览是一张死图（用户反馈「椅子会动的 你没实现」）。
    if chair_id and pose == "sit":
        chair_imgs, chair_delays = _apply_chair_anim(imgs, resolved_anim, chair_id, flip)
        if chair_imgs:
            imgs = chair_imgs
        if chair_delays:
            # 时长跟着椅子走，否则 12 帧会按 sit 的节奏播
            pass
    delays = []
    try:
        delays = list(cr.pose_frame_delays(resolved_anim, pose))
    except Exception:  # noqa: BLE001
        delays = []
    if chair_id and pose == "sit":
        try:
            _ci, _cd = _apply_chair_anim(imgs, resolved_anim, chair_id, flip, count_only=True)
            if _cd:
                delays = _cd
        except Exception:  # noqa: BLE001
            pass
    cw, ch = imgs[0].size
    pad = 2
    sheet = Image.new("RGBA", (len(imgs) * (cw + pad) + pad, ch + pad * 2), (0, 0, 0, 0))
    out_frames: List[Dict[str, Any]] = []
    for i, im in enumerate(imgs):
        x = pad + i * (cw + pad)
        # 同上：必须 alpha_composite，paste 自掩码会平方 alpha
        sheet.alpha_composite(im.convert("RGBA"), (x, pad))
        out_frames.append({
            "index": i, "x": x, "y": pad, "w": im.width, "h": im.height,
            # 锚点：compose_animation 保证肚脐在同一像素，取画布中心即可（客户端按此镜像/落地）
            "origin": [im.width // 2, im.height], "delay": int(delays[i]) if i < len(delays) else 180,
        })
    os.makedirs(OUT_DIR, exist_ok=True)
    digest = str(abs(hash(json.dumps({"e": equips, "p": pose, "f": flip}, sort_keys=True))) % (10 ** 12))
    path = os.path.join(OUT_DIR, f"chara_anim_{digest}.png")
    sheet.save(path)
    return {"png": path, "sheet": {"w": sheet.width, "h": sheet.height},
            "frame": {"w": cw, "h": ch}, "frames": out_frames, "delay": 180,
            "equips": equips, "pose": pose, "flip": flip,
            "elapsed": round(time.time() - t0, 2)}


def cmd_char_ears(args: Dict[str, Any]) -> Dict[str, Any]:
    """列当前头型可用的耳型（Head/<id>.img/front 下的耳朵 canvas 名）。

    人类头型自带圆耳（humanEar），所以默认不画额外耳朵；
    想要精灵耳/长耳就换这里的其它值。带 show 参数时直接出对比图。
    """
    cr = char_renderer()
    base = [str(e) for e in (args.get("equips") or [])]
    if not base:
        base = ["00002000", "00012000", "00030000", "00020000"]
    head = next((e for e in base if str(e).lstrip("0").startswith("120") or e == "00012000"), None)
    ears: List[str] = []
    if head:
        try:
            ears = list(cr.get_ear_types(head))
        except Exception:  # noqa: BLE001
            ears = []
    if not ears:
        ears = ["humanEar"]
    return {"head": head, "earTypes": ears, "default": "humanEar",
            "note": "humanEar=人类自带圆耳；其余为额外耳型（精灵耳等）"}


def cmd_char_poses(_args: Dict[str, Any]) -> Dict[str, Any]:
    """角色支持的姿势。"""
    cr = char_renderer()
    try:
        poses = [p for p in cr.get_weapon_poses("00000000")]
    except Exception:  # noqa: BLE001
        poses = []
    return {"poses": poses or ["stand1", "stand2"]}


# ── 聊天气泡（UI/ChatBalloon.img 九宫格）───────────────────────────────
# 算法照 docs：四角固定、n/s 水平平铺、w/e 垂直平铺、c 双向平铺，箭头**替换**底行中间。
#   内容尺寸 = 文本 + 内边距；n_cols = max(3, ceil(content_w / C_W))；n_rows = max(2, ceil(content_h / C_H))
#   画布 W = nw_w + n_cols*n_w + ne_w；H = nw_h + n_rows*c_h + max(sw_h, s_h, arrow_h)
# clr 是 int32 ARGB → 文字颜色（clr=-1 → 不透明白）。
# minipet（WzService.ExtractBalloonTiles）的瓦片清单：
#   nw / n / head / ne / w / c / e / sw / s / arrow / se
# 之前漏了 **head**，所以顶行中间该画头像的位置画成了普通 n 瓦片。
BALLOON_PARTS = ("nw", "n", "head", "ne", "w", "c", "e", "sw", "s", "arrow", "se")
_balloon_cache: Dict[str, Any] = {}
# 字体候选：与 minipet `CreateBalloonTypeface` 的 macOS 分支对齐
# （它优先 PingFang SC，其次 SimSun/MingLiU；本机装的是 PingFang SC）。
# 注意：不同字体在同一字号下的字宽不同（实测「你好，冒险岛！」STHeiti 84px vs
# PingFang 系 ~69px），所以字体选谁会影响文字的像素级结果 —— 布局已对齐，
# 剩余差异只来自字体本身。
_FONT_CANDIDATES = (
    "/System/Library/Fonts/PingFang.ttc",
    "/System/Library/Fonts/PingFang SC.ttc",
    "/System/Library/Fonts/Supplemental/Songti.ttc",
    "/System/Library/Fonts/STHeiti Medium.ttc",
    "/System/Library/Fonts/Hiragino Sans GB.ttc",
    "/Library/Fonts/Arial Unicode.ttf",
)


def _balloon_font(size: int):
    from PIL import ImageFont
    for path in _FONT_CANDIDATES:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:  # noqa: BLE001
                continue
    return ImageFont.load_default()


def _balloon_style(style_id: str) -> Optional[Dict[str, Any]]:
    """取一个气球样式的九片（解 _outlink 后的真图）。"""
    if style_id in _balloon_cache:
        return _balloon_cache[style_id]
    img = _parsed_image(root_for("UI"), "ChatBalloon.img")
    if img is None:
        return None
    node = img.get(style_id)
    if node is None:
        return None
    parts: Dict[str, Any] = {}
    for name in BALLOON_PARTS:
        child = node.get(name)
        if child is None:
            continue
        # **无条件**走 _canvas_meta：它是本插件读画布元数据（尺寸/origin/outlink）的正规入口。
        # 之前写成 `if hasattr(child, "width")` 才调用 —— 而 `WzCanvasProperty` 没有
        # `width` 属性，于是每片都走 else → meta=None → 解到了错误的图
        # （实测 nw 解成 30×31，而正确的解是 6×6；minipet 的瓦片是 6×6，
        #  且 6 + 8×12 + 6 = 108 正好等于气泡宽度，可交叉验证）。
        meta = _canvas_meta(child, root_for("UI"), img, "UI")
        if meta is None:
            continue
        try:
            parts[name] = decode_canvas(meta["canvas"], region=REGION).convert("RGBA")
            _o = meta.get("origin") or [0, 0]
            parts[name + "_origin"] = (int(_o[0]), int(_o[1]))
        except Exception:  # noqa: BLE001
            continue
    clr = node.get("clr")
    color = None
    clr_raw = None
    if clr is not None and isinstance(getattr(clr, "value", None), int):
        clr_raw = clr.value
        u = clr.value & 0xFFFFFFFF
        color = ((u >> 16) & 0xFF, (u >> 8) & 0xFF, u & 0xFF, (u >> 24) & 0xFF)
    out = {"parts": parts, "clr": color, "clrRaw": clr_raw}
    _balloon_cache[style_id] = out
    return out


def cmd_balloon_styles(_args: Dict[str, Any]) -> Dict[str, Any]:
    """列出 UI/ChatBalloon.img 里哪些样式真的能拼（必须同时有九片里的关键几片）。"""
    img = _parsed_image(root_for("UI"), "ChatBalloon.img")
    if img is None:
        raise FileNotFoundError("UI/ChatBalloon.img 读不到")
    rows = []
    for child in img.children():
        names = set()
        try:
            names = {c.name for c in child.children()}
        except Exception:  # noqa: BLE001
            pass
        need = {"nw", "n", "ne", "w", "c", "e", "sw", "s", "se"}
        rows.append({"id": child.name, "has": sorted(names & set(BALLOON_PARTS)),
                     "usable": need.issubset(names)})
    usable = [r for r in rows if r["usable"]]
    return {"total": len(rows), "usable": len(usable),
            "styles": usable[:40], "broken": [r for r in rows if not r["usable"]][:20]}


def _balloon_wrap(text: str, wrap_w: float, font) -> list:
    """中文友好的逐字符断行（照抄 minipet BalloonService.WrapText：
    wordWrap=true + breakWords=true，宽超 wrapW 处断行）。"""
    lines, cur = [], ""
    for ch in str(text):
        if ch == "\n":
            lines.append(cur)
            cur = ""
            continue
        trial = cur + ch
        if cur and font.getlength(trial) > wrap_w:
            lines.append(cur)
            cur = ch
        else:
            cur = trial
    lines.append(cur)
    return lines or [""]


def use_head(t) -> bool:
    """该瓦片是否可用（minipet：width>1 且 height>1）。"""
    return t is not None and t.width > 1 and t.height > 1


def cmd_balloon(args: Dict[str, Any]) -> Dict[str, Any]:
    """把一句话渲染成冒险岛聊天气泡 PNG。

    **算法照抄 minipet** `MiniPet/Services/BalloonService.cs:RenderBalloon`
    （它自己注释说拼装参考 MapleSalon2 `renderer/chatBalloon/chatBalloonBackground.ts`）：

      * origin-aware 定位：每片的左上角 = 位置 − origin；
      * 顶行/底行用 **n / s 瓦片按 colWidth 平铺**（不是拉伸）；
      * **中央区用 c 瓦片按 colWidth × colHeight 网格重复**（TilingSprite 语义，
        拉伸会把图案拉长，与游戏观感不同）；
      * head 画在顶行正中；arrow 画在底行开头（s 比 arrow 宽时回退对齐）；
      * 文字：12px、居中、wrapW=90、lineHeight=16（MapleSalon2 原值）；
      * 颜色：clr 为 0 / -1 时用深灰 #222（白字在浅色气泡上看不见），
        否则按 MapleSalon2 的 `0x1000000 + clr` 解析。
    """
    from PIL import Image as _Im

    text = str(args.get("text") or "你好，冒险岛！")
    want_style = str(args.get("style") or "")

    # ── 选样式：指定的优先；否则取第一个可用 ──
    if want_style:
        cand = [want_style]
    else:
        st = cmd_balloon_styles({})
        cand = [r["id"] for r in st["styles"]] or ["5"]
    style_id, st = None, None
    for sid in cand:
        s_ = _balloon_style(sid)
        if s_ and "nw" in s_["parts"]:
            style_id, st = sid, s_
            break
    if st is None:
        raise ValueError(f"没有可用的气泡样式（试过 {cand[:5]}）")

    P = st["parts"]

    def tile(name: str):
        return P.get(name)

    def org(name: str):
        return P.get(name + "_origin") or (0, 0)

    nw, n, ne = tile("nw"), tile("n"), tile("ne")
    w_, c, e_ = tile("w"), tile("c"), tile("e")
    sw, s_, se = tile("sw"), tile("s"), tile("se")
    arrow, head = tile("arrow"), tile("head")

    # 缺失瓦片：1×1 透明占位（画了看不见），布局尺寸用同类兜底
    # （minipet 注释：原实现要求九宫格齐全，导致 id=1 无 NW/NE/SW/SE、id=520 无 C 的样式永远渲染失败）
    blank = _Im.new("RGBA", (1, 1), (0, 0, 0, 0))

    def usable(t):
        return t is not None and t.width > 1 and t.height > 1

    nw = nw if usable(nw) else blank
    n = n if usable(n) else blank
    ne = ne if usable(ne) else blank
    w_ = w_ if usable(w_) else blank
    e_ = e_ if usable(e_) else blank
    sw = sw if usable(sw) else blank
    s_ = s_ if usable(s_) else blank
    se = se if usable(se) else blank

    # 中央瓦片退化时：列宽高用左右/上下边兜底（保证还能排版出文字）
    col_w = c.width if (c is not None and c.width > 1) else max(12, w_.width if w_.width > 1 else (n.height if n.height > 1 else 16))
    col_h = c.height if (c is not None and c.height > 1) else max(12, n.height if n.height > 1 else 16)
    tiled_center = c is not None and c.width > 1
    if not tiled_center:
        c = blank

    # ── 文字颜色（照抄 minipet 的规则）──
    clr = st.get("clrRaw")
    if clr is None or clr in (0, -1):
        text_color = (0x22, 0x22, 0x22, 255)
    else:
        rgb = (0x1000000 + int(clr)) & 0xFFFFFF
        text_color = ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255)

    font_size = int(args.get("fontSize") or 12)
    font = _balloon_font(font_size)
    wrap_w = 90.0
    line_h = 16.0
    lines = _balloon_wrap(text, wrap_w, font)

    min_w = max(80, int(wrap_w))
    min_h = max(40, int(len(lines) * line_h))
    if not tiled_center:
        col_w = max(80, int(wrap_w))
        col_h = max(20, int(len(lines) * line_h + 10))

    col_count = max(1, int(math.ceil(min_w / float(col_w))))
    row_count = max(1, int(math.ceil(min_h / float(col_h))))

    # ── origin-aware 拼装 ──
    top_offset = max(org("nw")[1], org("n")[1])
    x_off_arrow = (s_.width - arrow.width) if (arrow is not None and s_.width > arrow.width) else 0

    def pivot_x(ox: int) -> float:
        # MapleSalon2：origin.x == -1 是「未定义」哨兵 → pivot.x = 0（仅当无 arrow 偏移）
        return 0.0 if (ox == -1 and x_off_arrow == 0) else float(ox)

    rects = []   # (tile, L, T)

    # top 行：nw → n×colCount（正中画 head）→ ne
    ox, oy = org("nw")
    rects.append((nw, -pivot_x(ox), -oy))
    x = nw.width - pivot_x(ox)
    half = col_count // 2
    nox, noy = org("n")
    hox, hoy = org("head")
    for i in range(col_count):
        if i == half and use_head(head):
            rects.append((head, x - pivot_x(hox), -hoy))
        else:
            rects.append((n, x - pivot_x(nox), -noy))
        x += n.width
    x -= x_off_arrow
    eox, eoy = org("ne")
    rects.append((ne, x - pivot_x(eox), -eoy))

    # middle：w×rowCount + 中央平铺 + e×rowCount
    y = nw.height - top_offset
    wox, woy = org("w")
    left_off = w_.width - pivot_x(wox)
    right_x = left_off + col_w * col_count - x_off_arrow
    center_top = y
    eox2, eoy2 = org("e")
    for _i in range(row_count):
        rects.append((w_, -pivot_x(wox), y - woy))
        rects.append((e_, right_x - pivot_x(eox2), y - eoy2))
        y += col_h
    if tiled_center:
        for ty in range(row_count):
            for tx in range(col_count):
                rects.append((c, left_off + tx * col_w, center_top + ty * col_h))
    else:
        # 无 c：用透明占位撑出中央尺寸，否则文字会溢出气泡（minipet 实测塌成 70×66）
        placeholder = _Im.new("RGBA", (max(1, col_w * col_count), max(1, col_h * row_count)), (0, 0, 0, 0))
        rects.append((placeholder, left_off, center_top))

    # bottom：sw → s×colCount（i==0 画 arrow）→ se
    sox, soy = org("sw")
    rects.append((sw, -pivot_x(sox), y - soy))
    x = sw.width - pivot_x(sox)
    sox2, soy2 = org("s")
    aox, aoy = org("arrow")
    for i in range(col_count):
        if i == 0 and use_head(arrow):
            rects.append((arrow, x - pivot_x(aox), y - aoy))
            if s_.width > arrow.width:
                x -= (s_.width - arrow.width)
        else:
            rects.append((s_, x - pivot_x(sox2), y - soy2))
        x += col_w       # MapleSalon2：每轮都前进 colWidth（否则 arrow 与下一块 s 重叠）
    seox, seoy = org("se")
    rects.append((se, x - pivot_x(seox), y - seoy))

    # ── 求边界并平移（origin 会产生负坐标）──
    min_l = min(L for _t, L, _T in rects)
    min_t = min(T for _t, _L, T in rects)
    max_r = max(L + t.width for t, L, _T in rects)
    max_b = max(T + t.height for t, _L, T in rects)
    total_w = int(math.ceil(max_r - min_l))
    total_h = int(math.ceil(max_b - min_t))
    if total_w <= 0 or total_h <= 0:
        raise ValueError("气泡尺寸算出来是空的")

    out = _Im.new("RGBA", (total_w, total_h), (0, 0, 0, 0))
    for t, L, T in rects:
        if t is None:
            continue
        out.alpha_composite(t, (int(round(L - min_l)), int(round(T - min_t))))

    # ── 文字：文字块起点 = topLeftPadding；每行在 wrapW 内居中；首行基线 = 起点 + ascent ──
    from PIL import ImageDraw
    d = ImageDraw.Draw(out)
    # 文字定位（照 minipet 的两段式，注意**第二段容易被漏掉**）：
    #   ① minipet 画文字用的是「气泡局部坐标」：textBlockX = nw.width - NWOrigin.x，
    #      textBlockY = nw.height - topOffset；
    #   ② 最后整张画布会 `canvas.Translate(-minL, -minT)` —— 因为 origin 会把
    #      顶行推到负坐标（实测 minT = nw_origin.y - nw.height = 6-6 = 0… 对样式 0 是 0，
    #      但对顶边更高的样式就是负数）。漏掉这个平移，文字就会偏上。
    # 平移量 = -minT；而 minT = nw_origin.y - nw.height（顶行那一片的左上角）。
    # 所以文字左上角 = textBlockY + (nw.height - nw_origin.y)。
    nw_ox, nw_oy = org("nw")
    min_t = nw_oy - nw.height                       # 顶行的最小 y（气泡局部坐标）
    # 水平：minipet 把文字节点放在中央区里，故以**中央平铺区**中心为准
    # （之前用 wrapW/2，wrapW=90 而中央区更窄时文字会偏右不够居中）。
    text_x = left_off + (col_w * col_count) / 2.0
    text_y0 = (nw.height - top_offset) + (-min_t)
    try:
        asc, _desc = font.getmetrics()
    except Exception:  # noqa: BLE001
        asc = int(font_size * 0.8)
    # 坐标口径：minipet 画的是**基线**（firstBaseline = textBlockY - ascent，
    # Skia 的 Ascent 为负 → 减负=加，等价于 textBlockY）；PIL 的 text() 收的是
    # **左上角**。两者在这里恰好等价（左上角 = 基线 - ascent = textBlockY），
    # 所以直接用 textBlockY + i*lineHeight 即可（实测文字落在气泡内且居中）。
    for i, ln in enumerate(lines):
        tw = d.textlength(ln, font=font)
        d.text((text_x - tw / 2.0, text_y0 + i * line_h), ln, font=font, fill=text_color)

    os.makedirs(OUT_DIR, exist_ok=True)
    # 文件名必须**确定性**：Python 的 `hash(str)` 每进程随机化（PYTHONHASHSEED），
    # 用它会生成一堆同名不同内容的残留文件，验收脚本可能读到旧图（实测踩过）。
    import hashlib as _hl
    _digest = _hl.sha1(f"{style_id}|{text}|{font_size}".encode("utf-8")).hexdigest()[:10]
    name = f"balloon_{style_id}_{_digest}.png"
    png = os.path.join(OUT_DIR, name)
    out.save(png)
    return {"png": png, "url": f"/file/{name}", "style": style_id,
            "w": out.width, "h": out.height,
            "lines": lines, "colCount": col_count, "rowCount": row_count,
            "colW": col_w, "colH": col_h, "tiledCenter": tiled_center,
            "textColor": list(text_color)}


def cmd_stats(_args: Dict[str, Any]) -> Dict[str, Any]:
    return {"wzpy": WZPY_DIR, "data_dir": DATA_DIR, "region": REGION, "version": VERSION,
            "roots": list(_roots), "cached_images": len(_image_cache),
            "fingerprint": SOURCE_FINGERPRINT, "pid": os.getpid(), **_stats}


# ── 详情（mob / npc / eqp 三类，供页面可视化）─────────────────────────
LINK_KEYS = ("link", "info/link")


def _candidate_paths(kind: str, id_: str) -> List[str]:
    padded8 = id_.zfill(8)
    padded7 = id_.zfill(7)
    if kind == "eqp":
        out = []
        for group in _eqp_groups(id_):
            out.append(f"Character/{group}/{padded8}.img")
            out.append(f"Character/{group}/_Canvas/{padded8}.img")
        return out
    if kind == "npc":
        return [f"Npc/{padded7}.img", f"Npc/_Canvas/{padded7}.img", f"Npc/{id_}.img"]
    if kind == "mob":
        # 傲天把怪物图并进 _Canvas，但旧结构是 Mob/{id}.img —— 两种都试
        return [f"Mob/{padded7}.img", f"Mob/_Canvas/{padded7}.img", f"Mob/{id_}.img"]
    return []


def _eqp_groups(id_: str) -> List[str]:
    """装备编号 → Character 子目录名（优先用 String/Eqp.img 的分组）。"""
    try:
        entry = _string_map("Eqp.img").get(id_) or _string_map("Eqp.img").get(id_.lstrip("0"))
    except Exception:  # noqa: BLE001
        entry = None
    if entry and entry.get("group"):
        return [str(entry["group"])]
    return ["Weapon", "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove", "Shield",
            "Cape", "Accessory", "Ring", "Face", "Hair", "PetEquip", "Android", "Bits",
            "ArcaneForce", "AuthenticForce", "Totem", "Jewel", "Mechanic", "Taming"]


def _flatten(node: Any, out: Dict[str, Any], prefix: str = "", depth: int = 0, max_depth: int = 2) -> None:
    """把 WzSubProperty 树压成 "a/b" → 标量 的扁平表（限制层数，避免 info 爆炸）。"""
    if not isinstance(node, WzSubProperty):
        return
    for child in node.children():
        key = f"{prefix}/{child.name}" if prefix else child.name
        if isinstance(child, WzSubProperty):
            if depth < max_depth:
                _flatten(child, out, key, depth + 1, max_depth)
            else:
                out[key] = f"<{child.child_count()} 项>"
        elif isinstance(child, WzVectorProperty):
            out[key] = [child.x, child.y]
        else:
            val = scalar(child)
            if val is not None:
                out[key] = val


def _find_img(kind: str, id_: str) -> Tuple[Optional[str], Any, Optional[str]]:
    """返回 (可用路径, img, 错误原因)。

    顺序很重要：**怪物先查 Packs/*.ms**——那里是 `Mob/<id>.img` 的完整数据
    （info + origin/delay + 全部动作），而 `Mob/_Canvas` 只是像素仓库
    （抽样 300 只怪只有 3% 带 info）。先查 _Canvas 就是数据遗失。
    """
    if kind == "mob":
        ms = _ms_image(f"{id_.zfill(7)}.img")
        if ms is not None:
            return f"Mob/{id_.zfill(7)}.img", ms, None
    for path in _candidate_paths(kind, id_):
        parts = path.split("/", 1)
        if len(parts) != 2:
            continue
        try:
            root = root_for(parts[0])
        except Exception:  # noqa: BLE001
            continue
        img = _parsed_image(root, parts[1])
        if img is not None:
            return path, img, None
    return None, None, f"{kind} {id_}：候选路径都没命中（{', '.join(_candidate_paths(kind, id_))[:160]}）"


def icon_of(kind: str, id_: str, path: str) -> Optional[Dict[str, Any]]:
    """取代表图：装备用 info/icon（解 outlink），怪物/NPC 用首个动作的首帧。"""
    parts = path.split("/", 1)
    root = root_for(parts[0])
    img = _parsed_image(root, parts[1])
    if img is None:
        return None
    if kind == "eqp":
        icon = img.get("info/icon") or img.get("info/iconRaw")
        if isinstance(icon, WzCanvasProperty):
            meta = _canvas_meta(icon, root, img, parts[0])
            if meta:
                return {"canvas": meta["canvas"], "w": meta["w"], "h": meta["h"],
                        "source": f"{path}/info/icon"}
    for anim in ("stand", "move", "stand1", "walk1", "alert", "hit1", "die1"):
        node = img.get(anim)
        if node is None:
            continue
        if isinstance(node, WzSubProperty):
            frames = frames_of(node, root, parts[0], img)
            if frames:
                f = frames[0]
                return {"canvas": f["canvas"], "w": f["w"], "h": f["h"],
                        "source": f"{path}/{anim}/{f['name']}"}
        elif isinstance(node, WzCanvasProperty):
            meta = _canvas_meta(node, root, img, parts[0])
            if meta:
                return {"canvas": meta["canvas"], "w": meta["w"], "h": meta["h"],
                        "source": f"{path}/{anim}"}
    return None


def _link_target(img: Any) -> Optional[str]:
    """同型号共用数据：`info/link` 指向另一个编号（怪物尤其常见）。"""
    for key in LINK_KEYS:
        node = img.get(key)
        if node is None:
            continue
        val = getattr(node, "value", None)
        if isinstance(val, (str, int)) and str(val).strip() not in ("", "0", "None"):
            return str(val).strip()
    return None


def probe_sources(kind: str, id_: str) -> List[Dict[str, Any]]:
    """枚举这个编号的**全部**可能来源，并逐个说明它到底有什么。

    关键区别（minipet-esp32 / WzComparerR2 的读法）：
      * ``{type}/{id}.img`` 是**信息主源**：动作结构 + origin/delay/z/map，PNG 只是 _outlink 指向。
      * ``_Canvas/{id}.img`` 只是**PNG 仓库**：多数条目只有图，没有 info —— 不该当信息源用。
    所以这里两条都探，并分别标出「有 info / 只有图 / 卷壳为空」。
    """
    pad7 = id_.zfill(7)
    pad8 = id_.zfill(8)
    cands: List[Tuple[str, str, str]] = []  # (标签, pack, 相对路径)
    if kind == "mob":
        cands = [
            ("Packs/*.ms 元数据主源", "__ms__", f"{pad7}.img"),
            ("Mob 本体 img", "Mob", f"{pad7}.img"),
            ("Mob PNG 仓库", "Mob", f"_Canvas/{pad7}.img"),
        ]
    elif kind == "npc":
        cands = [
            ("Npc 信息主源", "Npc", f"{pad7}.img"),
            ("Npc PNG 仓库", "Npc", f"_Canvas/{pad7}.img"),
        ]
    else:
        for group in _eqp_groups(id_):
            cands.append((f"装备本体（{group}）", "Character", f"{group}/{pad8}.img"))
            cands.append((f"装备 PNG 仓库（{group}）", "Character", f"{group}/_Canvas/{pad8}.img"))

    out: List[Dict[str, Any]] = []
    seen = set()
    for label, pack, rel in cands:
        key = f"{pack}/{rel}"
        if key in seen:
            continue
        seen.add(key)
        entry: Dict[str, Any] = {"label": label, "path": key.replace("//", "/"),
                                 "exists": False, "has_info": False, "info_keys": 0,
                                 "animations": 0, "frames": 0, "note": ""}
        if pack == "__ms__":
            img = _ms_image(rel)
            root = None
        else:
            try:
                root = root_for(pack)
            except Exception as exc:  # noqa: BLE001
                entry["note"] = f"打不开分类 {pack}：{exc}"
                out.append(entry)
                continue
            try:
                img = _parsed_image(root, rel)
            except Exception as exc:  # noqa: BLE001
                entry["note"] = f"解析失败：{type(exc).__name__}"
                out.append(entry)
                continue
        if img is None:
            entry["note"] = "不存在（本客户端没有这个条目）"
            out.append(entry)
            continue
        entry["exists"] = True
        top = [c.name for c in img.children()] if hasattr(img, "children") else []
        info_node = img.get("info")
        if isinstance(info_node, WzSubProperty):
            entry["has_info"] = True
            entry["info_keys"] = info_node.child_count()
        try:
            anims = animations_of(img, root, pack if root is not None else "Mob")
        except Exception:  # noqa: BLE001
            anims = []
        entry["animations"] = len(anims)
        entry["frames"] = sum(a["frames"] for a in anims)
        if "info" in top:
            entry["note"] = f"含 info（{info_node.child_count()} 项）+ {len(anims)} 个动作"
        elif anims:
            entry["note"] = f"只有图像数据：{len(anims)} 个动作 / {entry['frames']} 帧，无 info"
        else:
            entry["note"] = f"空壳或非图像节点（顶层：{', '.join(top[:5]) or '无'}）"
        out.append(entry)
    return out


def cmd_sources(args: Dict[str, Any]) -> Dict[str, Any]:
    kind = str(args.get("kind") or "mob")
    id_ = str(args["id"]).strip()
    return {"kind": kind, "id": id_, "sources": probe_sources(kind, id_)}


def cmd_detail(args: Dict[str, Any]) -> Dict[str, Any]:
    id_ = str(args["id"]).strip()
    kind = str(args.get("kind") or "").strip() or ("eqp" if len(id_) >= 7 and not id_.startswith(("1", "9")) else "mob")
    if kind not in ("mob", "npc", "eqp"):
        raise ValueError(f"不支持的类别 {kind!r}（只支持 mob / npc / eqp）")

    path, img, err = _find_img(kind, id_)
    name = None
    entry = None
    table = {"mob": "Mob.img", "npc": "Npc.img", "eqp": "Eqp.img"}[kind]
    try:
        table_map = _string_map(table)
        entry = table_map.get(id_) or table_map.get(id_.lstrip("0")) or table_map.get(id_.zfill(len(id_)))
    except Exception as exc:  # noqa: BLE001
        log(f"string {table} 失败: {exc}")
    if entry:
        name = entry.get("name")

    sources = probe_sources(kind, id_)
    if img is None:
        return {"kind": kind, "id": id_, "name": name, "path": None, "error": err,
                "string": entry or None, "info": {}, "info_source": None,
                "animations": [], "total_frames": 0, "icon": None,
                "link": None, "link_path": None, "link_info": {},
                "sources": sources, "pack": None}

    pack = path.split("/", 1)[0]
    info: Dict[str, Any] = {}
    for key in ("info", "mobInfo", "npcInfo"):
        node = img.get(key)
        if isinstance(node, WzSubProperty):
            _flatten(node, info)
    # link 跟随：同型号共用数据时把目标的 info 也并进来（并如实标注来源）
    link = _link_target(img)
    link_path = None
    link_info: Dict[str, Any] = {}
    if link and link != id_:
        lpath, limg, _ = _find_img(kind, link)
        if limg is not None:
            link_path = lpath
            for key in ("info", "mobInfo", "npcInfo"):
                node = limg.get(key)
                if isinstance(node, WzSubProperty):
                    _flatten(node, link_info)
    anims = animations_of(img, root_for(pack), pack)
    icon = icon_of(kind, id_, path)
    icon_info = None
    if icon is not None:
        im = decode_canvas(icon["canvas"], region=REGION).convert("RGBA")
        _stats["decodes"] += 1
        icon_info = {"w": im.width, "h": im.height, "source": icon["source"],
                     "transparent": im.getchannel("A").getbbox() is None}
    # 帧总数，用于页面判断"有没有得看"
    total_frames = sum(a["frames"] for a in anims)
    return {
        "kind": kind, "id": id_, "name": name, "path": path,
        "string": entry or None,
        "info": info,
        "info_source": f"{path}/info" if info else None,
        "link": link,
        "link_path": link_path,
        "link_info": link_info,
        "sources": sources,
        "pack": pack,
        "animations": anims,
        "total_frames": total_frames,
        "icon": icon_info,
        "error": None,
    }


def cmd_detail_page(args: Dict[str, Any]) -> Dict[str, Any]:
    """详情页一站式数据：详情 + 全部动作的首帧缩略图。"""
    detail = cmd_detail(args)
    out = dict(detail)
    thumbs: List[Dict[str, Any]] = []
    if detail.get("path"):
        parts = detail["path"].split("/", 1)
        root = root_for(parts[0])
        img = _parsed_image(root, parts[1])
        want = int(args.get("thumb_scale") or 2)
        for anim in (detail.get("animations") or [])[: int(args.get("max_anims") or 24)]:
            node = img.get(anim["name"]) if img is not None else None
            frames = frames_of(node, root, parts[0], img) if isinstance(node, WzSubProperty) else []
            if not frames:
                continue
            sheet, meta = compose(frames[: int(args.get("frames_per_anim") or 8)], scale=want)
            thumbs.append({"animation": anim["name"], "frames": len(frames),
                           "sheet": meta["sheet"], "frame": meta["frame"], "frames_meta": meta["frames"]})
            break  # 只渲染首个动作的缩略（完整帧网格由 /frame 路由按需取）
    out["thumb"] = thumbs[0] if thumbs else None
    return out


# ── 地图与背景（相机模型对齐 mapleStoryMiniPet: MapService.ClampCamera）──
_map_renderer = None
MAP_CACHE_SCHEMA = 3  # meta 字段一变就 +1，旧缓存自动失效
_map_png_cache: Dict[str, Dict[str, Any]] = {}


def map_renderer():
    """懒建 MapRenderer：Map 必选，String/Mob/Npc 提供名称与 life 精灵。"""
    global _map_renderer
    if _map_renderer is None:
        from wzpy.map import MapRenderer
        src: Dict[str, Any] = {}
        for name in ("Map", "String", "Mob", "Npc"):
            try:
                src[name] = root_for(name)
            except Exception as exc:  # noqa: BLE001
                log(f"地图源 {name} 打开失败：{exc}")
        if "Map" not in src:
            raise RuntimeError("打不开 Map 分类，无法渲染地图")
        _map_renderer = MapRenderer(
            src["Map"],
            string_source=src.get("String"),
            mob_source=src.get("Mob"),
            npc_source=src.get("Npc"),
            region=REGION,
        )
        log("MapRenderer 就绪")
    return _map_renderer


def clamp_camera(bounds: Dict[str, int], cam_x: float, cam_y: float,
                 zoom: float, view_w: int, view_h: int) -> Dict[str, float]:
    """相机 clamp —— 与 MiniPet Services/MapService.ClampCamera 同一套规则：

    camX ∈ [MinX + halfW, MaxX − halfW]，halfW = viewW/(2*zoom)；
    地图比视口窄（minX > maxX）时直接居中。Y 同理。
    """
    if zoom <= 0 or view_w <= 0 or view_h <= 0:
        return {"x": cam_x, "y": cam_y}
    half_w = view_w / (2.0 * zoom)
    half_h = view_h / (2.0 * zoom)
    min_x, max_x = bounds["left"] + half_w, bounds["right"] - half_w
    cx = (bounds["left"] + bounds["right"]) / 2.0 if min_x > max_x else min(max(cam_x, min_x), max_x)
    min_y, max_y = bounds["top"] + half_h, bounds["bottom"] - half_h
    cy = (bounds["top"] + bounds["bottom"]) / 2.0 if min_y > max_y else min(max(cam_y, min_y), max_y)
    return {"x": cx, "y": cy, "halfW": half_w, "halfH": half_h}


def _bounds_from_args(args: Dict[str, Any]) -> Dict[str, int]:
    if all(k in args for k in ("left", "top", "right", "bottom")):
        return {"left": int(args["left"]), "top": int(args["top"]),
                "right": int(args["right"]), "bottom": int(args["bottom"]),
                "width": int(args["right"]) - int(args["left"]),
                "height": int(args["bottom"]) - int(args["top"])}
    return {}


_map_name_index: Optional[List[Dict[str, str]]] = None


def map_name_index() -> List[Dict[str, str]]:
    """自建地图名录：`String/Map.img` → 左补零 **9 位** 的真实地图编号 + 中文名。

    为什么不直接用 `MapRenderer.search`：它把编号归一化成「去前导零」
    （`Map.img` 的 `10000` → `10000`），而地图编号是 9 位（`100000000`），
    两边对不上 → 面板里一片 `693000046 ` 这种没有中文名的行。
    这里改成 LeftPad(9)，**不做逐条 has_map 校验**（21369 条逐条尝试会跑几分钟），
    改成「按名字筛选后，只对要返回的那几条做校验」——校验失败的剔除并如实标注。
    """
    global _map_name_index
    if _map_name_index is not None:
        return _map_name_index
    rows: List[Dict[str, str]] = []
    img = _parsed_image(string_root().root, "Map.img")
    if img is not None:
        img.parse()
        for cat in img.children():
            if not hasattr(cat, "children"):
                continue
            for e in cat.children():
                if not e.name.isdigit():
                    continue
                name = e.get("mapName")
                name = str(getattr(name, "value", "") or "").strip()
                if not name:
                    continue
                street = e.get("streetName")
                street = str(getattr(street, "value", "") or "").strip()
                # 补零到 9 位后 id 恰好 9 位；不足 9 位的用原名（如 4000002）
                raw = e.name
                rows.append({"id": raw.zfill(9) if len(raw) <= 9 else raw,
                             "name": name, "street": street})
    log(f"地图名录：名称表里带中文名的 {len(rows)} 条")
    _map_name_index = rows
    return rows


def _filter_named_maps(query: str) -> List[Dict[str, str]]:
    idx = map_name_index()
    q = (query or "").strip().lower()
    if not q:
        return idx
    if q.isdigit():
        qq = q.lstrip("0")
        return [r for r in idx if r["id"].lstrip("0").startswith(qq)]
    return [r for r in idx if q in r["name"].lower() or q in (r["street"] or "").lower()]


def cmd_map_list(args: Dict[str, Any]) -> Dict[str, Any]:
    """地图名录：9 位编号 / 中文名 / 街区名。只对返回的条目做可渲染校验。"""
    mr = map_renderer()
    query = str(args.get("query") or "").strip()
    limit = int(args.get("limit") or 60)
    hits = _filter_named_maps(query)
    # 排序：无查询时「名字长度 2~6 字」优先（像城镇/野外地图，适合当背景），
    # 而不是把「光」「冬」这种极短名排在最前。
    if not query:
        hits = sorted(hits, key=lambda r: (
            0 if 2 <= len(r["name"]) <= 6 else 1, len(r["name"]), r["id"]))
    else:
        hits = sorted(hits, key=lambda r: (r["name"] != query, len(r["name"]), r["id"]))
    out: List[Dict[str, str]] = []
    dropped = 0
    # 多取一些候选来兜掉「有名字但渲染不出来」的
    for r in hits[: limit * 4]:
        if len(out) >= limit:
            break
        try:
            if not mr.has_map(r["id"]):
                dropped += 1
                continue
        except Exception:  # noqa: BLE001
            dropped += 1
            continue
        out.append({"id": r["id"], "name": r["name"], "street": r["street"]})
    return {"query": query, "total": len(hits), "count": len(out), "maps": out,
            "dropped": dropped,
            "note": "编号左补零到 9 位；名字取自 String/Map.img；已剔除渲染不出来的"}


# 经典户外地图（含中文名，实测都能渲染）：当背景最合适的一批。
# 这不是「硬编码地图数据」——渲染用的世界数据与名字仍全部从 WZ 读，
# 这里只是给「推荐/随机优先」一个偏好清单，取不到就自动忽略。
FEATURED_MAPS = (
    "100000000",  # 射手村
    "101000000",  # 魔法密林
    "102000000",  # 勇士部落
    "103000000",  # 废弃都市
    "104000000",  # 明珠港
    "120000000",  # 诺特勒斯码头
    "200000000",  # 天空之城
    "211000000",  # 冰峰雪域
    "220000000",  # 玩具城
    "230000000",  # 水下世界
    "240000000",  # 神木村
    "250000000",  # 武陵道场
    "260000000",  # 阿里安特
    "261000000",  # 玛加提亚
    "600000000",  # 新叶城
    "800000000",  # 昭和村
    "910000000",  # 自由市场入口
    "1000000",    # 彩虹村
)


def cmd_map_featured(_args: Dict[str, Any]) -> Dict[str, Any]:
    """经典户外地图（按中文名返回；渲染不出来的自动剔除）。"""
    mr = map_renderer()
    by_id = {r["id"].lstrip("0"): r for r in map_name_index()}
    out: List[Dict[str, str]] = []
    for mid in FEATURED_MAPS:
        r = by_id.get(mid.lstrip("0"))
        if r is None:
            continue
        try:
            if not mr.has_map(r["id"]):
                continue
        except Exception:  # noqa: BLE001
            continue
        out.append({"id": r["id"], "name": r["name"], "street": r["street"]})
    return {"maps": out, "count": len(out)}


# 「城镇」判定（用户口径：优先 town 的地图）。
# 只看 `String/Map.img` 的**街区名**：街区名等于地图名、或以「村/城/镇/港」结尾的，
# 基本都是城镇地图（射手村/魔法密林/勇士部落…）；野外地图的街区名通常是区域名 + 编号。
TOWN_STREET_HINTS = ("村", "城", "镇", "港", "都市", "部落", "密林", "码头", "市场", "广场", "营地", "村庄")
# 这些街区/名字明显不是城镇：隐藏地图、航海中、地下水路、室内、副本…
NOT_TOWN_HINTS = ("隐藏", "航海", "水路", "地下", "内部", "深处", "洞穴", "窟", "塔", "道场",
                  "竞技", "副本", "等候", "大厅", "祭坛", "飞船", "某处")


def _town_rank(row: Dict[str, str]) -> int:
    """城镇匹配质量：0 最好（镇名=地图名），1 城镇后缀，2 只命中街区词，-1 不是城镇。

    光看街区名不够：野外地图的街区名也是城镇名（「神木村」下面一堆龙林1/2/3）。
    所以优先「地图名 == 街区名」——射手村/魔法密林/勇士部落这类真正的城镇。
    """
    name = (row.get("name") or "").strip()
    street = (row.get("street") or "").strip()
    if not street or not name:
        return -1
    if any(h in street for h in NOT_TOWN_HINTS) or any(h in name for h in NOT_TOWN_HINTS):
        return -1
    if name == street and any(h in street for h in TOWN_STREET_HINTS):
        return 0
    if len(name) <= 6 and any(h in name for h in ("村", "城", "镇", "港", "都市", "部落")):
        return 1
    if any(h in street for h in TOWN_STREET_HINTS):
        return 2
    return -1


def _is_town(row: Dict[str, str]) -> bool:
    return _town_rank(row) >= 0


def cmd_map_random(args: Dict[str, Any]) -> Dict[str, Any]:
    """随机挑一张**有中文名且能渲染**的地图（背景「随机换一张」用）。

    **最近用过的必须真排除**：`exclude` 由前端传「最近 N 张」（用户要求最近 20 次不重复）。
    这里有个坑：精选池只有十几张，光在精选池里抽、只排除几项，就会反复出现同样几张
    （用户反馈「随机有很错觉经常是重复几个」）。所以精选池抽不出时**自动放宽到全量名录**
    （实测 21366 张有中文名），保证「最近 20 张不重复」这条硬约束能满足。
    """
    import random as _random
    mr = map_renderer()
    exclude = {str(x) for x in (args.get("exclude") or [])}
    prefer = str(args.get("prefer") or "").strip().lower()

    def build(featured_only: bool):
        if prefer:
            return _filter_named_maps(prefer)
        if featured_only and args.get("featured"):
            return cmd_map_featured({})["maps"]
        return map_name_index()

    def draw(pool):
        cand = [r for r in pool if r["id"] not in exclude]
        _random.shuffle(cand)
        tried = 0
        for r in cand[:200]:
            tried += 1
            try:
                if not mr.has_map(r["id"]):
                    continue
            except Exception:  # noqa: BLE001
                continue
            return {"id": r["id"], "name": r["name"], "street": r["street"], "tried": tried}
        return None

    # 先按用户选择（精选优先），抽不出来就放宽到全量
    hit = draw(build(featured_only=True))
    if hit is None and not prefer and args.get("featured"):
        hit = draw(build(featured_only=False))
        if hit is not None:
            hit["relaxed"] = True
    if hit is None:
        raise RuntimeError(f"没抽到可用地图（已排除最近 {len(exclude)} 张）")
    return hit


def cmd_map_next(args: Dict[str, Any]) -> Dict[str, Any]:
    """下一张背景地图：**优先城镇**，避开最近用过的，尽量不重复。

    用户口径：「从左到右走到头了就换个地图，不要一直用一个，优先使用 town 的地图」。
    分层抽取：先「镇名=地图名」，再「城镇后缀」，最后才退到一般地图。
    """
    import random as _random
    mr = map_renderer()
    exclude = {str(x) for x in (args.get("exclude") or [])}

    shape_cache: Dict[str, bool] = {}

    def shape_ok(mid: str) -> bool:
        """画面比例是否适合当背景：超过 7:1 的细长条（实测 天空之城上空 51149×3650 = 14:1）
        会在屏幕上退化成一窄条，很难看。"""
        if mid in shape_cache:
            return shape_cache[mid]
        ok = False
        try:
            info = mr.describe(mid)
            b = info.get("bounds") or {}
            w, h = max(1, int(b.get("width") or 1)), max(1, int(b.get("height") or 1))
            ok = max(w, h) / min(w, h) <= 7.0
        except Exception:  # noqa: BLE001
            ok = False
        shape_cache[mid] = ok
        return ok

    def usable(row: Dict[str, str]) -> bool:
        try:
            return mr.has_map(row["id"]) and shape_ok(row["id"])
        except Exception:  # noqa: BLE001
            return False

    # 同一城镇常有多个编号（童话村就有 4 个），按名字去重，别连着抽到同名的
    ranked = {0: [], 1: [], 2: []}
    seen_name = set()
    for row in map_name_index():
        r = _town_rank(row)
        if r < 0:
            continue
        key = row["name"]
        if key in seen_name:
            continue
        seen_name.add(key)
        ranked[r].append(row)
    towns_total = sum(len(v) for v in ranked.values())
    tries = 0
    for tier in (0, 1, 2):
        bucket = [r for r in ranked[tier] if r["id"] not in exclude]
        _random.shuffle(bucket)
        for row in bucket[:300]:
            tries += 1
            if usable(row):
                return {"id": row["id"], "name": row["name"], "street": row["street"],
                        "town": True, "tier": tier, "candidates": towns_total, "tried": tries}
    # 兜底：全部名录里挑一张没排除过的
    rest = [r for r in map_name_index() if r["id"] not in exclude]
    _random.shuffle(rest)
    for row in rest[:300]:
        tries += 1
        if usable(row):
            return {"id": row["id"], "name": row["name"], "street": row["street"],
                    "town": False, "tier": 9, "candidates": towns_total, "tried": tries}
    raise RuntimeError(f"没抽到可用地图（试了 {tries} 张）")


def cmd_map_detail(args: Dict[str, Any]) -> Dict[str, Any]:
    """一张地图的完整信息：VR 边界、层/物件统计、出生点、传送门、相机默认位置。"""
    mr = map_renderer()
    mid = str(args["id"])
    d = mr.describe(mid)
    bounds = d.get("bounds") or {}
    view = args.get("view") or {}
    zoom = float(view.get("zoom") or 1.0)
    cam = clamp_camera(bounds, bounds.get("left", 0) + bounds.get("width", 0) / 2.0,
                       bounds.get("top", 0) + bounds.get("height", 0) / 2.0,
                       zoom, int(view.get("w") or 1920), int(view.get("h") or 1080))
    return {
        "id": d.get("id"), "sourceId": d.get("sourceId"), "linked": d.get("linked"),
        "name": d.get("name"), "street": d.get("street"),
        "bounds": bounds, "counts": d.get("counts"),
        "layers": d.get("layers"), "life": (d.get("life") or [])[:200],
        "portals": d.get("portals"),
        # 完整 foothold / 绳梯：桌宠要沿它们走、站到地面（MiniPet WalkGraph 同口径）
        "footholds": d.get("footholds") or [],
        "ropes": d.get("ropes") or [],
        "footholdCount": len(d.get("footholds") or []),
        "camera": cam,
        "note": "相机 clamp 规则与 MiniPet MapService.ClampCamera 一致",
    }


def _render_map_png(args: Dict[str, Any]) -> Tuple[str, Dict[str, Any]]:
    """渲染地图 PNG 落盘：整图渲染 + 可选按世界坐标矩形裁剪。

    裁剪在 Pillow 侧做（不是 compose 的 viewport）：compose 的 viewport 与 VR 原点
    对不齐，实测会把下半截吃掉（世界 y 1223 以下没有像素）。
    """
    mid = str(args["id"])
    img, info, bounds, scale = _render_full_map(mid, max_scale=float(args.get("scale") or 1.0))
    crop = _bounds_from_args(args)
    if crop:
        # 用**实际渲染尺寸**反算缩放：_render_full_map 会把请求的 scale 压到
        # MAX_MAP_WORLD/长边，若拿请求值换算，裁出来的就是错区域（实测把天空裁掉、
        # 上半截变黑）。
        sx = img.width / max(1.0, float(bounds["width"]))
        sy = img.height / max(1.0, float(bounds["height"]))
        box = (
            max(0, int((crop["left"] - bounds["left"]) * sx)),
            max(0, int((crop["top"] - bounds["top"]) * sy)),
            min(img.width, int((crop["right"] - bounds["left"]) * sx)),
            min(img.height, int((crop["bottom"] - bounds["top"]) * sy)),
        )
        if box[2] > box[0] and box[3] > box[1]:
            img = img.crop(box)
        else:
            crop = {}
    # 视口适配：背景是一条很扁的地图带（射手村 5832×1172 ≈ 5:1），16:9 视口里上下必然空。
    # 做法：内容按宽度缩放后**底对齐**（游戏里地平线在下方），空档用图两端的
    # 代表色（顶端＝天空、底端＝地面）做线性渐变延伸——与地图同色系，不会有
    # 拉伸条纹，也不像模糊光晕那样抢戏。
    target_w = int(args.get("target_w") or 0)
    target_h = int(args.get("target_h") or 0)
    filled = None
    if target_w > 0 and target_h > 0:
        import numpy as _np
        img = img.convert("RGBA")
        # 先剥掉上下全透明的行：内容边界是低分辨率探针取的，实测会多要一两行，
        # 直接缩放会在渐变与画面之间留一条透明缝（看起来像一条黑带）。
        _al = _np.asarray(img.getchannel("A"))
        _rows = _np.where(_al.max(axis=1) > 0)[0]
        if len(_rows):
            img = img.crop((0, int(_rows[0]), img.width, int(_rows[-1]) + 1))
        scaled = img.resize((target_w, max(1, round(img.height * target_w / max(1, img.width)))), Image.LANCZOS)
        # 地图片很扁时，纯按宽度缩放会让天空占掉大半个屏幕（实测 1280×577 里天空占 60%）。
        # 把「空档」压到视口高度的 25% 以内：不足就整体放大，宁可横向超出（背景本来就要平移）。
        max_pad = int(target_h * 0.25)
        if target_h - scaled.height > max_pad:
            need = target_h - max_pad
            k = need / max(1, scaled.height)
            scaled = scaled.resize((max(1, round(scaled.width * k)), need), Image.LANCZOS)

        def edge_color(source: Image.Image, rows: int, from_top: bool) -> Tuple[float, float, float]:
            arr = _np.asarray(source.convert("RGB"), dtype=_np.float32)
            band = arr[:rows] if from_top else arr[-rows:]
            # 同时做纵向与横向的稳健平均，避免被单个高饱和像素带偏
            return tuple(float(v) for v in band.reshape(-1, 3).mean(axis=0))

        if scaled.height >= target_h:
            top = max(0, scaled.height - target_h)
            img = scaled.crop((0, top, target_w, top + target_h))
            filled = "crop"
        else:
            rows = min(24, scaled.height)
            sky = edge_color(scaled, rows, True)
            ground = edge_color(scaled, rows, False)
            pad_top = target_h - scaled.height
            filled = Image.new("RGBA", (target_w, target_h), (0, 0, 0, 0))
            if pad_top > 0:
                # 顶端行向「上延伸色」渐隐，衔接处不留硬边
                ramp = _np.linspace(0.0, 1.0, pad_top, dtype=_np.float32)[:, _np.newaxis]
                block = _np.zeros((pad_top, 3), dtype=_np.float32)
                for c in range(3):
                    block[:, c] = sky[c] * (1.0 - ramp[:, 0]) + sky[c] * ramp[:, 0]
                grad = _np.repeat(block[:, _np.newaxis, :], target_w, axis=1)
                alpha = _np.linspace(255.0, 255.0, pad_top, dtype=_np.float32)[:, _np.newaxis]
                rgba = _np.concatenate([grad, _np.repeat(alpha, target_w, axis=1)[:, :, _np.newaxis]], axis=2)
                filled.paste(Image.fromarray(rgba.astype(_np.uint8), "RGBA"), (0, 0))
            filled.paste(scaled, (0, target_h - scaled.height))
            img = filled
            filled = "edges"
    key = hash((mid, scale, crop.get("left"), crop.get("top"), crop.get("right"),
                crop.get("bottom"), target_w, target_h, int(args.get("time_ms") or 0))) % (10 ** 12)
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, f"map_{abs(key)}.png")
    img.convert("RGBA").save(path)
    return path, {"w": img.width, "h": img.height, "viewport": crop or None,
                  "scale": round(scale, 6), "mapId": mid,
                  "worldScale": scale, "bounds": bounds,
                  "padded": filled, "name": info.get("name")}


def cmd_map_image(args: Dict[str, Any]) -> Dict[str, Any]:
    path, meta = _render_map_png(args)
    return {"png": path, **meta}


def cmd_map_canvas(args: Dict[str, Any]) -> Dict[str, Any]:
    """**固定视口相机**用的地图画布：整图渲染一张（按内容边界裁掉透明行），
    返回 bounds 与 scale，前端按 MiniPet 的公式自己做变换：

        sx = (worldX - camX) * zoom + screenW/2
        sy = (worldY - camY) * zoom + screenH/2

    与 `map_viewport_b64` 的区别：那个是「服务端按视口裁一张」，
    这个是「一张大图 + 前端平移」——只有固定视口 + 相机中心这套才和 MiniPet 一致。
    """
    mid = str(args["id"])
    # 前端要「1:1、不放大」，所以这里给一张**原生分辨率**的画布（1 世界单位 = 1 像素）。
    # 之前默认 max_scale=1 会被内部的 MAX_MAP_WORLD 夹到 ~4096px，等于先缩后放，
    # 屏幕上就成了 2~5 倍放大的糊图。现在按最长边给足（上限 4096，BMP 纹理够用）。
    max_scale = float(args.get("scale") or 0) or 1.0
    probe = map_renderer().describe(mid)
    pb = probe.get("bounds") or {}
    span = max(1, int(pb.get("width") or 1), int(pb.get("height") or 1))
    if span > 4096:
        max_scale = min(max_scale, 4096.0 / span)
    img, info, bounds, scale = _render_full_map(mid, max_scale=max_scale)
    img = img.convert("RGBA")
    # 极端长条地图（实测 51149×3659，比 13.98）铺成背景会退化成一条细带。
    # 保留最长 8:1：超出就只取以 mapinfo 中心为中心的一段（长度截断，不再缩到看不见）。
    if img.width > img.height * 8 and img.height > 0:
        keep = int(img.height * 8)
        x0 = max(0, (img.width - keep) // 2)
        img = img.crop((x0, 0, x0 + keep, img.height))
        log(f"地图 {mid} 过长（{img.width}px → 截到 {keep}px，8:1）")

    # 裁到真正有像素的范围（低分辨率探针给的内容边界会多要几行）
    import numpy as _np
    al = _np.asarray(img.getchannel("A"))
    cols = _np.where(al.max(axis=0) > 0)[0]
    rows = _np.where(al.max(axis=1) > 0)[0]
    if len(cols) and len(rows):
        x0, x1 = int(cols[0]), int(cols[-1]) + 1
        y0, y1 = int(rows[0]), int(rows[-1]) + 1
        img = img.crop((x0, y0, x1, y1))
        # 图像像素 → 世界坐标：1 像素 = 1/scale 世界单位
        content = {
            "left": bounds["left"] + int(round(x0 / scale)),
            "top": bounds["top"] + int(round(y0 / scale)),
            "right": bounds["left"] + int(round(x1 / scale)),
            "bottom": bounds["top"] + int(round(y1 / scale)),
        }
    else:
        content = dict(bounds)
    content["width"] = content["right"] - content["left"]
    content["height"] = content["bottom"] - content["top"]
    # 实际像素/世界单位（裁剪后按内容宽重算，避免和请求 scale 有舍入差）
    real_scale = img.width / max(1, content["width"])
    digest = str(abs(hash((mid, round(real_scale, 6), content["left"], content["top"]))) % (10 ** 12))
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, f"mapcanvas_{digest}.png")
    img.save(path)
    return {"png": path, "w": img.width, "h": img.height,
            "scale": round(real_scale, 6), "contentBounds": content,
            "vrBounds": bounds, "mapId": mid, "name": info.get("name")}


def cmd_map_viewport_b64(args: Dict[str, Any]) -> Dict[str, Any]:
    """视口裁切直出 PNG 字节（背景层用；命中缓存就不重复渲染）。"""
    key = json.dumps({"v": MAP_CACHE_SCHEMA,
                      **{k: args.get(k) for k in ("id", "scale", "left", "top", "right", "bottom")}},
                     sort_keys=True, ensure_ascii=False)
    cached = _map_png_cache.get(key)
    # 命中缓存还必须**校验它确实满足这次请求**：历史上裁错区域那版渲染出来的
    # 1920×929 会按同一个 key 一直命中，光重启进程也不会失效（缓存就在进程里，
    # 而重启后第一次调用的结果又被重新缓存）。所以比一次目标尺寸，不符就重渲。
    if cached and os.path.exists(cached["path"]):
        meta = cached["meta"]
        tw, th = int(args.get("target_w") or 0), int(args.get("target_h") or 0)
        want_ok = (not tw or meta.get("w") == tw) and (not th or meta.get("h") == th)
        if want_ok:
            with open(cached["path"], "rb") as fh:
                data = fh.read()
            return {"png_base64": base64.b64encode(data).decode("ascii"), "cached": True,
                    "bytes": len(data), "key": key, **meta}
        log(f"地图缓存与请求不符（缓存 {meta.get('w')}x{meta.get('h')} vs 请求 {tw}x{th}），重渲")
        _map_png_cache.pop(key, None)
    path, meta = _render_map_png(args)
    with open(path, "rb") as fh:
        data = fh.read()
    if len(_map_png_cache) > 60:
        _map_png_cache.pop(next(iter(_map_png_cache)))
    _map_png_cache[key] = {"path": path, "meta": meta}
    return {"png_base64": base64.b64encode(data).decode("ascii"), "cached": False,
            "bytes": len(data), "key": key, **meta}


def _render_full_map(mid: str, max_scale: float = 1.0):
    """整图渲染（不用 compose 的 viewport——它的裁切与 VR 原点有偏差，会吃掉下半截）。"""
    mr = map_renderer()
    info = mr.describe(mid)
    b = info["bounds"]
    scale = min(max_scale, MAX_MAP_WORLD / max(1, max(b["width"], b["height"])))
    scale = max(0.02, min(scale, 4.0))
    img = mr.compose(mid, scale=scale)
    return img, info, b, scale


def cmd_map_content_bounds(args: Dict[str, Any]) -> Dict[str, Any]:
    """地图**实际有像素**的范围（世界坐标）。

    为什么需要它：VR 边界是给游戏相机用的，往往比画面大得多——实测射手村
    VRTop=-1100，而天空背景只画到约 -450，直接用 VR 拼背景顶上会留一条白带。
    做法：低分辨率渲一遍，取 alpha 非透明外接框，再按 scale 反算回世界坐标。
    """
    mid = str(args["id"])
    img, info, bounds, scale = _render_full_map(mid, max_scale=float(args.get("probe_scale") or 0.25))
    box = img.convert("RGBA").getchannel("A").getbbox()
    if box is None:
        raise ValueError(f"{mid} 渲染结果全透明")
    x0, y0, x1, y1 = box
    inv = 1.0 / scale
    left = bounds["left"] + int(x0 * inv)
    top = bounds["top"] + int(y0 * inv)
    right = bounds["left"] + int(x1 * inv)
    bottom = bounds["top"] + int(y1 * inv)
    return {"id": mid, "name": info.get("name"), "street": info.get("street"),
            "contentBounds": {"left": left, "top": top, "right": right, "bottom": bottom,
                              "width": right - left, "height": bottom - top},
            "vrBounds": bounds, "probeScale": scale, "probePixels": [img.width, img.height],
            "note": "VR 边界是相机用的，可能远大于画面；背景请用 contentBounds"}


def cmd_map_minimap(args: Dict[str, Any]) -> Dict[str, Any]:
    mr = map_renderer()
    mid = str(args["id"])
    raw = mr.minimap_png(mid)
    if not raw:
        raise FileNotFoundError(f"{mid} 没有小地图")
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, f"minimap_{mid}.png")
    with open(path, "wb") as fh:
        fh.write(raw)
    return {"png": path, "bytes": len(raw), "mapId": mid}


# ── Packs/*.ms（Ms **V2** / ChaCha20）：元数据主源 ─────────────────────
# 关键事实（踩过，别退回旧写法）：
#   · `data/Packs/Mob_00000.ms` 是 **ChaCha20 加密的 WZ 镜像归档**（Ms V2），
#     用 `wzpy.ms_file_v2.MsPackageV2` 能解出**真实属性树**：
#     `Mob/<7位>.img` → info（level/maxHP/PADamage/exp…）+ 动作 → 帧 → child
#     上的 origin/z/lt/rb/head/delay。
#   · 而 `<分类>/_Canvas/*.img` **只是像素仓库**：抽样 300 只怪只有 3% 带 info。
#   · `.ms` 帧里的 canvas 是 1×1 占位 + `_outlink` 指向 `<分类>/_Canvas/...`，
#     即「元数据在 .ms、像素在 _Canvas」——两边都要读，缺一边就是数据遗失。
#   · `MsContainer`（ms_container.py）只**扫字符串**给出结构清单，拿不到属性；
#     要真数据必须用 `MsPackageV2`。
_ms_packages: Dict[str, Any] = {}
MS_PACKS = ("Mob_00000.ms", "Mob_00001.ms", "Mob_00002.ms", "Mob_00003.ms")


def _open_ms_packages() -> List[Any]:
    """打开 Packs/Mob_*.ms（缓存）。打开一个 ~1s；没装的客户端返回空表。"""
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
            pkg = MsPackageV2.open(os.path.join(packs_dir, name), region=REGION)
            _ms_packages[name] = pkg
            log(f"打开 {name}: {time.time() - t0:.1f}s")
        except Exception as exc:  # noqa: BLE001
            log(f"{name} 打开失败：{type(exc).__name__} {exc}")
    return [_ms_packages[k] for k in sorted(_ms_packages)]


def _ms_image(path: str):
    """在已打开的 Mob_*.ms 里按 `Mob/<id>.img` 取镜像（.ms 里的元数据主源）。"""
    rel = path.strip("/")
    if rel.startswith("Mob/"):
        rel = rel[4:]
    for pkg in _open_ms_packages():
        try:
            node = pkg.root.get(f"Mob/{rel}")
        except Exception:  # noqa: BLE001
            continue
        if node is None:
            # 也试一下分类根直接挂 img 的情况
            try:
                node = pkg.root.get(rel)
            except Exception:  # noqa: BLE001
                node = None
        if node is not None:
            try:
                node.parse()
            except Exception:  # noqa: BLE001
                pass
            return node
    return None


def _ms_mob_ids() -> List[str]:
    """`.ms` 里全部的怪物编号（7 位补零）。"""
    out: List[str] = []
    for pkg in _open_ms_packages():
        mob = pkg.root.subdirs.get("Mob")
        if mob is not None:
            out.extend(n.replace(".img", "") for n in mob.images)
    return out


# ── .ms（Packs/*.ms）：结构 / Spine / meta 来源 ────────────────────────
# 本客户端把怪物的「结构」放在 Data/Packs/Mob_00000.ms（394MB）里：
#   4311 个 mob、121 个 Spine 骨骼、447k 条 UOL 路径。
# 注意：.ms 里的 canvas 是 1×1 占位 + _outlink 指向 Mob/_Canvas/...，
# 而且它的像素压缩是**未识别编解码**（非 zlib）——所以 .ms 只提供结构，
# 像素依旧从 _Canvas 取。这就是「MS 加载」与「图片加载」的分工。
_ms_cache: Dict[str, Any] = {}


def _ms_open(pack_file: str):
    """按名字打开 Packs/*.ms（结果缓存；一个 394MB 的文件要 ~3s 扫完）。"""
    if pack_file in _ms_cache:
        return _ms_cache[pack_file]
    from wzpy.ms_container import MsContainer
    packs_dir = os.path.join(DATA_DIR, "Packs")
    full = pack_file if os.path.isabs(pack_file) else os.path.join(packs_dir, pack_file)
    if not os.path.exists(full):
        raise FileNotFoundError(f"没有这个 .ms：{pack_file}（在 {packs_dir}）")
    t0 = time.time()
    c = MsContainer.open(full)
    _ms_cache[pack_file] = c
    log(f"打开 {os.path.basename(full)}: {time.time() - t0:.1f}s imgs={len(c.imgs)} mobs={len(c.mobs)}")
    return c


def cmd_packs_ms(_args: Dict[str, Any]) -> Dict[str, Any]:
    """列出 Packs/*.ms（结构容器）与各自规模——但不打开（打开很贵）。"""
    packs_dir = os.path.join(DATA_DIR, "Packs")
    out: List[Dict[str, Any]] = []
    if os.path.isdir(packs_dir):
        for name in sorted(os.listdir(packs_dir)):
            if not name.lower().endswith(".ms"):
                continue
            full = os.path.join(packs_dir, name)
            out.append({"file": name, "mb": round(os.path.getsize(full) / 1e6, 1),
                        "opened": name in _ms_cache})
    return {"dir": packs_dir, "packs": out,
            "note": "Packs/*.ms 提供结构与 Spine 骨骼；像素仍在 <分类>/_Canvas"}


def cmd_ms_mob(args: Dict[str, Any]) -> Dict[str, Any]:
    """一个怪物在 .ms 里的结构：动作 → 帧、是否为 Spine、骨骼动画。

    与 `detail` 的分工：detail 说「像素/属性从哪来」，这里说「结构在 .ms 里长什么样」。
    """
    pack_file = str(args.get("file") or "Mob_00000.ms")
    id_ = str(args["id"]).strip()
    c = _ms_open(pack_file)
    key = id_.lstrip("0") if id_.lstrip("0") in c.mobs else id_
    anims = c.mobs.get(key)
    if anims is None:
        # 编号写法可能不同（.ms 里是 7 位补零），兜一下
        for cand in (id_.zfill(7), id_.lstrip("0").zfill(7), id_):
            if cand in c.mobs:
                key, anims = cand, c.mobs[cand]
                break
    if anims is None:
        near = [m for m in c.mobs if id_.lstrip("0") in m][:8]
        return {"file": pack_file, "id": id_, "found": False, "near": near,
                "total_mobs": len(c.mobs)}
    # Spine：skeletons 里有 hash / 尺寸 / 骨骼数 / 动画名
    spine = None
    for sk in c.skeletons:
        h = str(getattr(sk, "hash", "") or "")
        if h and (h.lstrip("0") == key.lstrip("0") or h == key):
            spine = {"hash": h, "animations": list(getattr(sk, "animations", []) or [])[:12],
                     "bones": len(getattr(sk, "bones", []) or [])}
            break
    return {
        "file": pack_file, "id": id_, "key": key, "found": True,
        "animations": {a: sorted(f) for a, f in sorted(anims.items())},
        "frames_total": sum(len(f) for f in anims.values()),
        "spine": spine,
        "total_mobs": len(c.mobs),
        "note": "结构来自 .ms；像素经 _outlink 取自 Mob/_Canvas",
    }


def cmd_ms_stats(_args: Dict[str, Any]) -> Dict[str, Any]:
    """已打开的 .ms 概览（不打开新的）。"""
    out = []
    for name, c in _ms_cache.items():
        out.append({"file": name, "category": getattr(c, "category", None),
                    "imgs": len(getattr(c, "imgs", {}) or {}), "mobs": len(c.mobs),
                    "skeletons": len(c.skeletons), "path_count": getattr(c, "path_count", 0)})
    return {"opened": out, "cache_size": len(_ms_cache)}


COMMANDS = {
    "packs": cmd_packs, "tree": cmd_tree, "search": cmd_search,
    "get": cmd_get, "frames": cmd_frames, "sprite": cmd_sprite,
    "image": cmd_image, "stats": cmd_stats,
    "detail": cmd_detail, "detail_page": cmd_detail_page, "sources": cmd_sources,
    "chair_list": cmd_chair_list,
    "map_list": cmd_map_list, "map_random": cmd_map_random, "map_featured": cmd_map_featured,
    "map_next": cmd_map_next,
    "map_detail": cmd_map_detail, "map_image": cmd_map_image, "map_canvas": cmd_map_canvas,
    "map_viewport_b64": cmd_map_viewport_b64, "map_minimap": cmd_map_minimap,
    "map_content_bounds": cmd_map_content_bounds,
    "packs_ms": cmd_packs_ms, "ms_mob": cmd_ms_mob, "ms_stats": cmd_ms_stats,
    "char_parts": cmd_char_parts, "char_render": cmd_char_render, "char_poses": cmd_char_poses,
    "char_ears": cmd_char_ears,
    "char_anim": cmd_char_anim,
    "balloon_styles": cmd_balloon_styles, "balloon": cmd_balloon,
    # cmsdl 资源更新（见 py/cmsdl_support.py）
    "cmsdl_probe": cmd_cmsdl_probe, "cmsdl_plan": cmd_cmsdl_plan,
}


def main() -> None:
    log(f"worker 启动 wzpy={WZPY_DIR} data={DATA_DIR} region={REGION} v{VERSION}")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        req: Dict[str, Any] = {}
        try:
            req = json.loads(line)
            cmd = COMMANDS.get(str(req.get("cmd")))
            if cmd is None:
                raise ValueError(f"未知命令 {req.get('cmd')!r}")
            data = cmd(req.get("args") or {})
            data.pop("_raw", None)
            resp = {"id": req.get("id"), "ok": True, "data": data}
        except Exception as exc:  # noqa: BLE001
            log("ERROR", traceback.format_exc(limit=3))
            resp = {"id": req.get("id"), "ok": False,
                    "error": {"message": str(exc), "type": type(exc).__name__}}
        sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
