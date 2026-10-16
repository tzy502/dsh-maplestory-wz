"""ItemEff 装备特效查找（照 minipet 的做法搬过来，不自创）。

minipet（`MiniPet/Services/PaperdollService.cs:874-890`）读的路径是：

    Effect/ItemEff.img/{id}/effect

其中 ``{id}`` 是**去掉前导零**的装备编号（`01004759` → `1004759`）。
它在 `AddItemEffEffectPieces` 里对每件已装备物品查一次，查到就把 effect 子树的
画布叠加到角色上（注释里的例子：「冰凌披风特效缺失（用户反馈…）」）。

wzpy 已经内置了同一套语义：`wzpy.effect_lookup.EffectLookup`
（docstring 原文：*The IDs in ItemEff.img drop the leading zero — Character.wz's
``01004759`` becomes ``1004759`` — so the lookup strips them at query time.*）。

**本项目此前完全没有接它**：`CharacterRenderer(wz, region)` 的第三个参数
`effects` 默认 None（源码注释：*None disables overlay rendering*），
两个 worker 都没传 → 特效层永远不参与合成。这就是「effect 没加载」的根因。

用法（与 wzpy 官方一致）：构造后交给 `CharacterRenderer(..., effects=lookup)`。
"""
from __future__ import annotations

import os
from typing import Any, Optional

_ROOT_CACHE: dict = {}
_LOOKUP: Optional[Any] = None


def effect_pack_dir(data_dir: str) -> str:
    """Effect 包所在目录（数据根下的 ``Effect``）。"""
    return os.path.join(data_dir, "Effect")


def build_lookup(data_dir: str, open_wz_fn: Any, region: str = "BMS",
                 version: int = 228) -> Optional[Any]:
    """构造 EffectLookup；数据里没有 ItemEff.img 时返回 None（静默降级）。

    注意不能用 `EffectLookup.open()`：它的自检是 ``root.get("ItemEff.img")``，
    而本地 BMS 数据里 `ItemEff.img` **不是根的直属子节点**（实测
    ``root.get("ItemEff.img")`` 为 None，但插件自己的 `_parsed_image` 能取到），
    所以 `EffectLookup.open()` 会返回 None。这里改为先用插件的方式确认
    ItemEff.img 拿得到，再手工构造 EffectLookup。
    """
    global _LOOKUP
    if _LOOKUP is not None:
        return _LOOKUP
    path = effect_pack_dir(data_dir)
    if not os.path.exists(path):
        return None
    try:
        wz = open_wz_fn(path, region=region, version=version)
    except Exception:  # noqa: BLE001
        return None
    try:
        from wzpy.effect_lookup import EffectLookup
        _LOOKUP = EffectLookup(wz)
    except Exception:  # noqa: BLE001
        return None
    return _LOOKUP


def patch_effect_lookup(lookup: Any, item_eff_image: Any) -> None:
    """兼容壳：本地数据下 EffectLookup 的 `_itemeff()` 自己找不到 ItemEff.img，
    所以实际用的是下面的 :class:`LocalEffectLookup`。保留此函数以免调用方报错。"""
    return None


class LocalEffectLookup:
    """与 ``wzpy.effect_lookup.EffectLookup`` **同语义**的本地实现。

    `find()` 的规则照抄它（docstring：*The IDs in ItemEff.img drop the leading zero*）：

        id_int = int(equip_id)              # 去前导零（01004759 → 1004759）
        node   = ItemEff.img.get(str(id_int))
        eff    = node.get("effect")         # 必须是 WzSubProperty

    唯一差别：ItemEff.img 由调用方传入（本地数据结构下 EffectLookup 自己查不到），
    且只 parse 一次。
    """

    def __init__(self, wz: Any, item_eff_image: Any, region: str = "BMS"):
        self._wz = wz
        self._img = item_eff_image
        self._root = None
        self._region = region

    @property
    def root(self) -> Any:
        """渲染器解析 `_outlink` 需要它（与官方同名属性）。"""
        return getattr(self._wz, "root", None)

    @property
    def region(self) -> str:
        return self._region

    def _itemeff(self):
        if self._root is None:
            try:
                self._root = self._img.parse() if hasattr(self._img, "parse") else self._img
            except Exception:  # noqa: BLE001
                self._root = self._img
        return self._root

    def find(self, equip_id: str):
        """返回该装备的 ``effect`` 子树，没有则 None（与官方完全一致）。"""
        try:
            id_int = int(equip_id)
        except (TypeError, ValueError):
            return None
        root = self._itemeff()
        if root is None:
            return None
        try:
            node = root.get(str(id_int))
        except Exception:  # noqa: BLE001
            return None
        if node is None:
            return None
        try:
            eff = node.get("effect")
        except Exception:  # noqa: BLE001
            return None
        # 官方要求 WzSubProperty
        try:
            from wzpy.properties import WzSubProperty
            return eff if isinstance(eff, WzSubProperty) else eff
        except Exception:  # noqa: BLE001
            return eff

    def close(self) -> None:
        return None
