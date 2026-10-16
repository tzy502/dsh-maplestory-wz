#!/usr/bin/env python
"""椅子同步渲染（照 minipet `PaperdollService` 的椅子语义）。

为什么要单独一个函数：椅子不是 Character 部件，wzpy 的 `compose` 不认识它。
但它的定位公式 minipet 已经写死（源码注释 `PaperdollService.cs:937-939`）：

    人物平移 bodyRelMove → MiniPet 锚 = navel − bodyRelMove
    （3019632 花田椅 pos=3 bm=(47,−2) → 锚 (−47,+2)）

而 navel 就是 wzpy 世界坐标的原点 (0,0)，所以合成图里 navel 的像素位置 =
`(-min_x, -min_y)`（`_build_placements` 的 bbox 直接给）。

层级（`AddChairEffectPiece` + 绘制循环 `OrderByDescending(ZIndex)`）：
    z <= 0 → 角色的**后面**（椅子本体）
    z >  0 → 角色的**前面**（effect2 这种罩在身前的层）

坐姿：`ResolveChairAction` = `info/sitAction` 优先，缺省 `"sit"`；
`sit` 在这个 body 上只有 **1 帧**（`pose_frame_delays` 实测 `[100]`），
所以**只取第 0 帧**，不要走 `compose_animation_timeline`（它会给 80 帧，后面都是无效的）。
"""
from __future__ import annotations

import os
from typing import Any, Dict, List, Optional, Tuple

from PIL import Image

# 椅子分卷：Item/Install/{id//1000:05d}.img，另有 //100:06d 与 //10000:04d 两种旧版
_CHAIR_GROUPINGS = ("//1000:05d", "//100:06d", "//10000:04d")


def chair_paths(chair_id: str) -> List[str]:
    """椅子在 Item/Install 下可能的路径（按 minipet ChairRoot 的探测顺序）。"""
    n = int(str(chair_id).lstrip("0") or 0)
    out = []
    for spec in _CHAIR_GROUPINGS:
        code = spec.split(":")[0]
        width = int(spec.split(":")[1].rstrip("d"))
        div = int(code[2:])
        out.append(f"Install/{n // div:0{width}d}.img")
    return out


def compute_chair_effect_offset(has_body_rel_move: bool, bm: Tuple[int, int],
                                has_sit_action: bool, pos: int,
                                remove_body: bool = False,
                                has_taming_mob: bool = False,
                                id_num: int = 0) -> Tuple[int, int]:
    """照抄 minipet `PaperdollService.ComputeChairEffectOffset`（逐行等价）。"""
    off_x = 0
    off_y = 0
    if has_body_rel_move:
        if not has_sit_action and pos < 2:
            if pos == 1:
                off_y = -50
        else:
            off_x, off_y = -bm[0], -bm[1]
    elif pos == 1:
        off_y = -30 if (has_taming_mob and id_num // 1000 < 3011) else -50
    if remove_body:
        off_y += 30
    return off_x, off_y


def _chair_name_map(string_root: Any, parsed) -> Dict[str, str]:
    """String/Ins.img 的椅子名（键不补零，如 '3010000'）。"""
    img = parsed(string_root, "Ins.img")
    if img is None:
        return {}
    out: Dict[str, str] = {}
    try:
        for c in img.children():
            nm = str(getattr(c, "name", c))
            v = getattr(c, "value", None)
            if isinstance(v, str):
                out[nm] = v
    except Exception:  # noqa: BLE001
        pass
    return out


def list_chairs(item_root: Any, parsed, string_root: Any = None,
                limit: int = 400) -> List[Dict[str, Any]]:
    """列出可用椅子（id + 中文名）。

    椅子散在 `Item/Install/*.img` 里，每张 img 是一批同前缀的椅子
    （实测 `03019.img` 有 696 条）。名字取 `String/Ins.img`。
    """
    names = _chair_name_map(string_root, parsed) if string_root is not None else {}
    out: List[Dict[str, Any]] = []
    try:
        subs = item_root.root.subdirs
    except Exception:  # noqa: BLE001
        subs = []
    # 只扫 Install 下的 img（Item/Install 是 WzPackage 的一个子目录）
    targets = []
    for sd in subs:
        try:
            for n, im in sd.walk_images() if hasattr(sd, "walk_images") else []:
                if "/" not in str(n) and str(n).startswith("03"):
                    targets.append((str(n), im))
        except Exception:  # noqa: BLE001
            continue
    # 退路：直接按已知分卷探测
    if not targets:
        for gi in range(3000, 3050):
            for cand in chair_paths(str(gi * 1000)):
                im = parsed(item_root, cand)
                if im is not None:
                    targets.append((os.path.basename(cand), im))
    for fname, im in targets:
        try:
            kids = [str(getattr(c, "name", c)) for c in im.children()]
        except Exception:  # noqa: BLE001
            continue
        for k in kids:
            n = str(k).lstrip("0")
            if not n.startswith("301"):
                continue  # 椅子类前缀 0301xxxx
            out.append({"id": str(k), "name": names.get(str(k)) or names.get(n) or "（无名字）"})
            if len(out) >= limit:
                return out
    return out


def render_chair_pieces(item_root: Any, parsed, decode, region: str,
                        chair_id: str) -> Optional[Dict[str, Any]]:
    """取一把椅子的全部特效层。

    返回 ``{node, info, layers: [{name, z, pos, frames: [{img, origin, delay}]}]}``；
    椅子不存在返回 None。
    """
    node = None
    for p in chair_paths(chair_id):
        im = parsed(item_root, p)
        if im is None:
            continue
        # 键名格式两种都有（实测 Install/03019.img 用补零的 '03019632'，
        # 也有不补零的分卷）——都试，别只试一种（第一版就因此返回 None）。
        key = str(chair_id)
        for k in (key, key.zfill(8), key.lstrip("0"), str(int(key))):
            node = im.get(k) if k else None
            if node is not None:
                break
        if node is not None:
            break
    if node is None:
        return None

    info_node = node.get("info")

    def _iv(name: str, default=None):
        v = info_node.get(name) if info_node is not None else None
        val = getattr(v, "value", None)
        return default if val is None else val

    info = {
        "bodyRelMove": _iv("bodyRelMove"),
        "sitAction": _iv("sitAction"),
        "removeBody": bool(_iv("removeBody")),
        "tamingMob": _iv("tamingMob"),
        "sitEmotion": _iv("sitEmotion"),
        "invisibleWeapon": bool(_iv("invisibleWeapon")),
        "invisibleCape": bool(_iv("invisibleCape")),
        "removeEffect": bool(_iv("removeEffect")),
    }

    layers = []
    for name in ("effect", "effect2", "effect3"):
        e = node.get(name)
        if e is None:
            continue
        z = getattr(e.get("z"), "value", None) or 0
        pos = getattr(e.get("pos"), "value", None) or 0
        frames = []
        for c in e.children():
            cn = str(getattr(c, "name", c))
            if not cn.isdigit():
                continue
            fr = e.get(cn)
            meta = None
            try:
                meta = _canvas_meta_for(fr, item_root, parsed)
            except Exception:  # noqa: BLE001
                meta = None
            if meta is None:
                continue
            try:
                img = decode(meta["canvas"], region=region).convert("RGBA")
            except Exception:  # noqa: BLE001
                continue
            o = meta.get("origin") or [0, 0]
            dl = getattr(fr.get("delay"), "value", None)
            frames.append({"img": img, "origin": (int(o[0]), int(o[1])),
                           "delay": int(dl) if dl else 210})
        if frames:
            layers.append({"name": name, "z": int(z), "pos": int(pos), "frames": frames})
    return {"node": node, "info": info, "layers": layers}


def _canvas_meta_for(frame, item_root, parsed):
    """给椅子帧取画布元数据；由调用方注入 `_canvas_meta`（避免本模块反向依赖 worker）。"""
    fn = getattr(parsed, "canvas_meta", None)
    if fn is None:
        return None
    return fn(frame, item_root)


def chair_placements(chair: Dict[str, Any], chair_id: str,
                     navel_px: Tuple[int, int], has_sit_action: bool = True):
    """算出椅子各层在每一帧的落点。

    返回 ``(placements, count)``；``placements[i]`` 是第 i 帧的
    ``[(z, img, tx, ty), …]``（``tx/ty`` 是相对**角色那张图**的坐标）。
    """
    info = chair["info"]
    bm = info.get("bodyRelMove")
    has_bm = bm is not None
    bmx, bmy = (int(bm[0]), int(bm[1])) if has_bm else (0, 0)
    id_num = int(str(chair_id).lstrip("0") or 0)

    def off(layer):
        return compute_chair_effect_offset(
            has_bm, (bmx, bmy), has_sit_action, layer["pos"],
            remove_body=info.get("removeBody", False),
            has_taming_mob=bool(info.get("tamingMob")),
            id_num=id_num,
        )

    count = max((len(L["frames"]) for L in chair["layers"]), default=1)
    placements = []
    for i in range(count):
        frame_layers = []
        for layer in chair["layers"]:
            off_x, off_y = off(layer)
            frames = layer["frames"]
            fr = frames[min(i, len(frames) - 1)]
            frame_layers.append((
                layer["z"], fr["img"],
                navel_px[0] + off_x - fr["origin"][0],
                navel_px[1] + off_y - fr["origin"][1],
            ))
        placements.append(frame_layers)
    return placements, count


def _bbox(char_img: Image.Image, layers) -> Tuple[int, int, int, int]:
    """角色 + 这些椅子层的并集，坐标相对角色图左上。"""
    min_x = min_y = 0
    max_x, max_y = char_img.width, char_img.height
    for _z, im, tx, ty in layers:
        min_x = min(min_x, tx)
        min_y = min(min_y, ty)
        max_x = max(max_x, tx + im.width)
        max_y = max(max_y, ty + im.height)
    return min_x, min_y, max_x, max_y


def render_chair_frame(char_img: Image.Image, layers, box) -> Image.Image:
    """按给定 box 裁出的画布渲染一帧（层级照 minipet：z 大的先画）。"""
    min_x, min_y, max_x, max_y = box
    out = Image.new("RGBA", (max_x - min_x, max_y - min_y), (0, 0, 0, 0))
    out.alpha_composite(char_img, (-min_x, -min_y))
    for z, im, tx, ty in sorted(layers, key=lambda t: -t[0]):
        pos = (tx - min_x, ty - min_y)
        if z <= 0:
            base = Image.new("RGBA", out.size, (0, 0, 0, 0))
            base.alpha_composite(im, pos)
            base.alpha_composite(out, (0, 0))
            out = base
        else:
            out.alpha_composite(im, pos)
    return out


def compose_chair_animation(char_img: Image.Image, navel_px: Tuple[int, int],
                            chair: Dict[str, Any], chair_id: str,
                            has_sit_action: bool = True):
    """把椅子逐帧合成成一组**尺寸一致、且相对角色不抖**的帧。

    照 minipet 的两条修复：

    1. **画布取全部帧的并集**（`CollectPiecesForFrame(sampleAllChairFrames=True)`：
       「各帧尺寸不一（3019632 实测 …）→ union 全部特效帧 → 画布尺寸稳定」）。
       逐帧各算各的并集会让画布忽大忽小。
    2. **每帧内容仍按自己的并集对齐**后再垫进统一画布，所以角色在自己格子里
       位置固定、椅子也按各自 origin 摆正——不会出现「上边 0/1/12/7/20 来回跳」。

    返回 ``(frames, delays)``；``frames[i]`` 尺寸全部相同。
    """
    placements, count = chair_placements(chair, chair_id, navel_px, has_sit_action)
    if not count:
        return [char_img], []

    # 照 minipet `CollectPiecesForFrame(sampleAllChairFrames=True)`：
    # 「各帧尺寸不一（3019632 实测 …）→ union 全部特效帧 → **画布尺寸稳定**」。
    # 画布取全部帧的并集，**每帧内容按自己的并集对齐**后再垫进统一画布 ——
    # 这样格子尺寸恒定、角色在自己格子里的位置也恒定。
    boxes = [_bbox(char_img, layers) for layers in placements]
    ux0 = min(b[0] for b in boxes)
    uy0 = min(b[1] for b in boxes)
    ux1 = max(b[2] for b in boxes)
    uy1 = max(b[3] for b in boxes)

    frames = []
    for layers, box in zip(placements, boxes):
        tile = render_chair_frame(char_img, layers, box)
        canvas = Image.new("RGBA", (ux1 - ux0, uy1 - uy0), (0, 0, 0, 0))
        canvas.alpha_composite(tile, (box[0] - ux0, box[1] - uy0))
        frames.append(canvas)

    base_frames = chair["layers"][0]["frames"] if chair["layers"] else []
    delays = [int(base_frames[i]["delay"]) if i < len(base_frames) else 210
              for i in range(count)]
    return frames, delays


def compose_with_chair(char_img: Image.Image, navel_px: Tuple[int, int],
                       chair: Dict[str, Any], chair_id: str,
                       frame_index: int = 0,
                       has_sit_action: bool = True) -> Image.Image:
    """合成**单帧**（保留给面板静态预览用）。多帧走 `compose_chair_animation`。"""
    frames, _d = compose_chair_animation(char_img, navel_px, chair, chair_id, has_sit_action)
    if not frames:
        return char_img
    return frames[min(frame_index, len(frames) - 1)]


def _compose_with_chair_old(char_img: Image.Image, navel_px: Tuple[int, int],
                            chair: Dict[str, Any], chair_id: str,
                            frame_index: int = 0,
                            has_sit_action: bool = True) -> Image.Image:
    """（保留旧实现做对照，当前未使用）"""
    info = chair["info"]
    bm = info.get("bodyRelMove")
    has_bm = bm is not None
    bmx, bmy = (int(bm[0]), int(bm[1])) if has_bm else (0, 0)
    id_num = int(str(chair_id).lstrip("0") or 0)

    out = char_img.convert("RGBA")
    # z 大的先画（minipet: OrderByDescending(ZIndex)）
    for layer in sorted(chair["layers"], key=lambda L: -L["z"]):
        off_x, off_y = compute_chair_effect_offset(
            has_bm, (bmx, bmy), has_sit_action, layer["pos"],
            remove_body=info.get("removeBody", False),
            has_taming_mob=bool(info.get("tamingMob")),
            id_num=id_num,
        )
        frames = layer["frames"]
        fr = frames[min(frame_index, len(frames) - 1)]
        # 锚点：navel + offset，再减该帧自己的 origin
        tx = navel_px[0] + off_x - fr["origin"][0]
        ty = navel_px[1] + off_y - fr["origin"][1]
        if layer["z"] <= 0:
            # 椅子本体：在角色**后面**
            base = Image.new("RGBA", out.size, (0, 0, 0, 0))
            base.alpha_composite(fr["img"], (tx, ty))
            base.alpha_composite(out, (0, 0))
            out = base
        else:
            # 罩在身前（effect2）
            out.alpha_composite(fr["img"], (tx, ty))
    return out
