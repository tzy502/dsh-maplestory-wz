"""纸娃娃合成的共用小工具（两个插件都 import 这个文件）。

为什么单独放一份：桌宠（dsh-desktop-pet）与资源插件（dsh-maplestory-wz）各有自己的
worker，但**必须用同一套合成语义**，否则同一套编号在两处渲染结果不一样。
实测过的问题：桌宠的脸发暗、皱着 —— 因为它走的是 compose_animation 那条路，
没有做表情（FaceAcc）覆盖，基础脸的瞳仁透在表情的透明瞳下面。
"""
from __future__ import annotations

from typing import Any, List, Optional

import numpy as np
from PIL import Image

# ── 表情（101 面饰）与脸型的关系：**互斥变体，不是叠加** ──────────────
# 依据（照抄 minipet `PaperdollService.cs`）：
#   「脸型 + 101 面饰都走 AddExpressionPieces（{root}/{expression}/{frame}/{piece}）」
#   即两者都是 `face` 图层的内容，只是**不同的表情变体**（default/blink/hit…）。
# 实测三组（芙莉莲）：
#   A 只有脸型 00022035 → 睁眼、青绿瞳
#   B 只有表情 01012874 → 半闭眼（该表情本来的样子）
#   C 两者都画          → 半闭眼皮 + 底下透出睁眼的瞳 = 一团糊 ✗
# 还踩过的坑：之前用「区域替换 / 擦除」去补 C，那是在给一个**本就不该存在**的
# 组合打补丁，越补越糊。正确做法就是 A/B 二选一。
_FACE_ACC_CATS = ("FaceAcc",)


def face_acc_category(equip_id: str) -> bool:
    """是不是 101 脸饰（FaceAcc = 脸的另一个表情变体）。

    走 wzpy 的类目表，不再自己按前缀字符串判（手写规则踩过坑：Body/Face 混淆）。
    """
    return _category_of(equip_id) == "FaceAcc"


def has_face_acc(equips: List[str]) -> bool:
    return any(face_acc_category(e) for e in equips)


def _ensure_wzpy_on_path() -> None:
    """把 wzpy 所在目录加进 sys.path（照 wz_worker._load_wzpy 的候选顺序）。

    本模块可能被任一 worker 导入，导入时机可能早于 worker 自己加路径，
    所以这里自己保证一次。
    """
    import os as _os
    import sys as _sys
    cands = [
        _os.environ.get("WZPY_PATH"),
        _os.path.expanduser("~/.hermes/cache/scratch/wz-python"),
        "/Users/a502/.hermes/cache/scratch/wz-python",
    ]
    for c in cands:
        if c and _os.path.isdir(_os.path.join(c, "wzpy")) and c not in _sys.path:
            _sys.path.insert(0, c)
            break


# 类目判断**直接用 wzpy 的现成实现**，不要自己发明规则：
#   wzpy.character.category_for_id(eid) —— 内部是 `int(eid) // 10000` 查
#   `_CATEGORY_BY_ID_PREFIX`（照抄 MapleNecrocer 的 Equip.GetDir/GetPart）。
# 我先前自己写「去前导零后按位数判断」，把 Body(00002000→2000) 和
# Face(00020000→20000) 混在一起，导致**身体被当成脸型删掉**。
def _category_of(equip_id: str) -> Optional[str]:
    """equip 编号 → 类目名（Body/Face/FaceAcc/Coat/…），取不到返回 None。"""
    try:
        _ensure_wzpy_on_path()
        from wzpy.character import category_for_id  # type: ignore
        return category_for_id(str(equip_id))
    except Exception:  # noqa: BLE001
        return None


def is_face_id(equip_id: str) -> bool:
    return _category_of(equip_id) == "Face"


def is_body_id(equip_id: str) -> bool:
    return _category_of(equip_id) == "Body"


def resolve_face_equips(equips: List[str]) -> List[str]:
    """按 face 图层的互斥语义整理装备表：

    - 有 101 面饰（表情）→ **丢掉脸型**，只画表情；
    - 没有 → 原样返回。
    **绝不碰 Body**（身体必须始终保留）。
    """
    acc = [e for e in equips if face_acc_category(e)]
    if not acc:
        return list(equips)
    drop = set(acc) | {e for e in equips if is_face_id(e)}
    # 保留表情本身（acc 在 drop 里，单独挑回来）
    out = [e for e in equips if e not in drop] + acc
    # 安全网：整理结果必须仍含 Body，否则退回原表并只去脸型
    if not any(is_body_id(e) for e in out):
        out = [e for e in equips if not is_face_id(e)]
    return out
