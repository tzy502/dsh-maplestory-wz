---
description: "在 DSH 里读取本机冒险岛客户端的 WZ 数据：浏览与搜索包树、查怪物/NPC/装备的属性与动作精灵图、合成带椅子的纸娃娃、渲染聊天气泡，并在界面上浮一只桌宠。"
kind: "package-reference"
---

# @local/dsh-maplestory-wz

[English](README.md) | 中文

## 概述

读取本机已安装的冒险岛客户端的 WZ 文件，在 DSH 里直接使用 —— **不用数据库、不用起服务、
仓库里也不含任何游戏素材**。host 半边提供十二个模型可见的工具和一条 HTTP 路由；
一个 Python worker 常驻并预热 WZ 读取器，把 PNG / GIF 渲染到 `~/.dsh/wz-assets/`。
Web 半边加了六个页签的面板（浏览、搜索、图鉴、纸娃娃、背景、资源），把页面背景换成会
平移的游戏地图，并在界面上浮一只桌宠。

渲染语义**照抄 `mapleStoryMiniPet`，不自己发明**：聊天气泡照 `BalloonService.RenderBalloon`，
椅子层级与偏移照 `AddChairEffectPiece` 与 `ComputeChairEffectOffset`，纸娃娃脸型互斥照
`AddExpressionPieces`，背景相机照 `MapService.ClampCamera`。验收是**像素级**而不是肉眼：
`tools/verify-render.py` 拿渲染结果与 C# 基准图逐像素比对，`tools/verify.sh` 跑 22 项
端到端接口实测。

属性是否可读**靠探测而不是假设**，因为各发行版的分卷结构不同 —— 在本插件开发所用的客户端上，
只有 3% 的怪物图带 `info` 节点。

## 目录

- [使用本插件](#使用本插件)
- [实现说明](#实现说明)
- [延伸阅读](#延伸阅读)
- [模型体验](#模型体验)
- [已知限制与待办](#已知限制与待办)
- [开发者注记](#开发者注记)

-----

<a id="使用本插件"></a>
## 使用本插件

把仓库链进 DSH profile，把 `dataDir` 指向客户端的 `Data` 目录，然后重启 host。

### 安装与配置

```bash
ln -s /Users/a502/IdeaProjects/dsh-maplestory-wz \
      ~/.dsh/profiles/web/node_modules/@local/dsh-maplestory-wz
```

在 profile 的 `package.json` 里加上这条依赖，然后在 `cordis.patch.yml` 里配数据源：

```yaml
- insert:
    - id: maplestory-wz
      name: '@local/dsh-maplestory-wz'
      config:
        dataDir: /path/to/MapleStory/Data    # 改成你自己的
        region: BMS                          # 见下方说明
        version: 228
```

| 键 | 默认 | 说明 |
|---|---|---|
| `dataDir` | — | 客户端的 `Data` 目录，**必填**（示例 `/path/to/MapleStory/Data`） |
| `region` | `BMS` | 决定字符串表与解密密钥，取值见下 |
| `version` | `228` | WZ 版本号 |
| `wzpyPath` | 自动探测 | 含 `wzpy/` 的克隆目录 |
| `python` | 自动探测 | 带 Pillow、numpy 的解释器 |
| `mobId` | `1210102` | 桌宠初始怪物 |
| `scale` `speed` `flySpeed` `flyChance` | `1` `100` `60` `0.35` | 桌宠尺寸与运动 |

**关于 `region`（重要）**：底层 `wzpy` 只认三个密钥 ——

```python
WZ_IV = {
    "GMS": bytes([0x4D, 0x23, 0xC7, 0x2B]),
    "EMS": bytes([0xB9, 0x7D, 0x63, 0xE9]),
    "BMS": bytes([0x00, 0x00, 0x00, 0x00]),   # 也用于 CLASSIC / 现代 64 位客户端
}
```

**没有 `CMS` 这个取值**（传进去会 `KeyError: 'CMS'` 直接崩）。而 `BMS` 是**零 IV**，
不走 AES —— 国内客户端（CMS / 冒险岛）的数据正是这一套，所以 **CMS 客户端也用
`region: BMS`**。判断方法：如果字符串表能解出中文（例如 `1210102` → `花蘑菇`），
这个 region 就是对的；解不出来或报 `KeyError` 就是错的。

解释器按这个顺序探测：`config.python` → `$WZ_PYTHON` →
`~/.hermes/cache/scratch/wz-env/bin/python` → `/opt/homebrew/bin/python3` →
`/usr/local/bin/python3` → `/usr/bin/python3`。worker 启动时会**清掉继承的 `PYTHONPATH`**，
因为外来的 `site-packages` 会让 Pillow 导入失败。

### 让模型查数据

十二个工具覆盖读取路径。中文名和编号都能搜，属性表与动作帧拿回来就能渲染。

```text
查一下花蘑菇的属性
把 1210102 的走路做成 GIF
100000000 这张地图的边界和出生点在哪
```

| 工具 | 作用 |
|---|---|
| `wz_packs` | 有哪些分类与分卷 |
| `wz_tree` | 浏览节点树 —— 目录、图片、属性 |
| `wz_search` | 按中文名、英文名或编号搜 |
| `wz_item` | 编号 → 名字、属性、图标 |
| `wz_sources` | 一个编号的全部候选来源，并指出主源 |
| `wz_sprite` | 一个动作的帧 → 精灵图，可选循环 GIF |
| `wz_map` `wz_map_sprite` | 地图名录、地图详情、或渲染出的地图图 |
| `wz_char` | 纸娃娃：单帧、逐帧雪碧图、姿势、部位清单 |
| `wz_balloon` | 聊天气泡，489 种样式 |
| `wz_ms` | 读 `Data/Packs/*.ms` 内部结构 —— 动作帧、是否 Spine |

### 使用面板

侧栏「冒险岛资源」下有六个页签。

| 页签 | 内容 |
|---|---|
| **浏览** | 分类树，一路翻到图片与属性 |
| **搜索** | 全库按名字与编号搜，结果直接跳图鉴 |
| **图鉴** | 怪物 / NPC / 装备各一页：图标、属性、全部动作、帧网格 |
| **纸娃娃** | 十六个装备槽 + 一个椅子区块 + 姿势与耳型；一键应用到桌宠 |
| **背景** | 地图选择、清晰度、换图过渡时长、自动轮换 |
| **资源** | 跑 cmsdl 更新本机客户端，带进度与日志 |

纸娃娃写的是 `~/.dsh/wz-assets/background.json` 的 `doll` 字段，面板与桌宠**读同一份**，
所以改一处就够。应用到桌宠**立刻生效**：面板会通知桌宠重新拉取，不需要刷新页面。

### 使用桌宠

一只怪物或你自己的角色浮在页面上，来回走动、偶尔起飞、可拖拽、点击有反应。
齿轮按钮打开选择器，里面有常用怪物的快速选择 chip、纸娃娃、以及上次用的怪物。

装上椅子后桌宠切成坐姿并**完全停止走动**；清空椅子则两者都恢复。椅子**不是角色部件** ——
它住在 `Item/Install`，在面板里有自己的搜索框，由 `py/chair_support.py` 解析。

## 实现说明

涉及三层，每层的「改动不生效」规则都不一样。

```text
Web 面板 ──POST /dsh-maplestory-wz/api──┐
                                        ├─► index.js ─► Python worker（wzpy 读 WZ）
Agent 工具 ─────────────────────────────┘                    │
图片 ──GET /dsh-maplestory-wz/file/<name>── ~/.dsh/wz-assets/ ◄┘
```

`index.js` 注册工具与路由、管理 worker 池、持有桌宠配置。`client.js` 装面板、背景层、
桌宠浮层；`lib/client.js` 是它的镜像，必须同步。两个 worker 用 stdio 上的 JSON 行通信，
并缓存已打开的数据根与已解析的图片。

### 读 WZ

一个来源要么承载属性，要么纯粹是像素仓库，两者极易混淆：

| 来源 | 内容 | 角色 |
|---|---|---|
| `{type}/{id}.img` | 动作、`origin`、`delay`、`z`、`map`；PNG 走 `_outlink` | 主源 |
| `{type}/_Canvas/{id}.img` | 像素；通常没有 `info` | 仅兜底 |
| `info/link` | 同型号共用数据 | 跟随并标注 |

`_outlink` 与 `_inlink` 是自己解析的，因为本客户端把 `Weapon/`、`_Canvas/` 编码成
**图片名前缀**而不是子目录，这让 `resolve_canvas_link` 失效。精灵图的排版方式是：
先算每帧在锚点空间的矩形，取并集当画布，再逐格粘贴；`origin` 缺失时退回几何中心。

### 渲染语义

下表每条都从 `mapleStoryMiniPet` 移植，并标注了出处方法：

| 行为 | 出处 |
|---|---|
| 气泡九宫格 | `BalloonService.RenderBalloon` —— `n`/`s` 按 `colWidth` 平铺，`c` 平铺 `colWidth×colHeight`，`head` 贴顶行，`arrow` 在底行开头 |
| 脸型与 101 表情 | `AddExpressionPieces` —— `face` 图层的互斥变体 |
| ItemEff 特效 | `AddItemEffEffectPieces` —— `Effect/ItemEff.img/{id}/effect/{frame}` |
| 椅子层级 | `AddChairEffectPiece` —— `z<=0` 在角色后、`z>0` 在角色前，按 `z` 降序绘制 |
| 椅子偏移 | `ComputeChairEffectOffset` —— 锚点 = `navel − bodyRelMove`；`3019632` 是 `(47,−2)` → `(−47,+2)` |
| 坐姿动作 | `ResolveChairAction` —— `info/sitAction`，缺省 `sit` |
| 背景相机 | `MapService.ClampCamera` —— `camX ∈ [MinX + halfW, MaxX − halfW]`，地图比视口窄时居中 |

椅子有两个画布细节很关键：合成画布必须**放大到装得下椅子各层**（椅子锚在 navel 左侧、
且比角色大），以及 navel 的像素位置取自 `_build_placements(..., return_anchors=True)`，
而不是从像素上量。

### 背景层

地图同时画在 `html` 背景和 body 下的一个浮层里，三栏布局靠
`html[data-ms-wz-bg="on"]` 作用域 CSS 变半透明。有五个约束是承重的：画布用**内容边界**
而不是 VR 边界（VR 会超出，留出空白带）；裁剪换算用**实际渲染缩放**；插件页与设置页
强制实底以免列表看不清；浮层必须 `pointer-events: none`；缩放下限要夹住，
否则又小又窄的地图会退化。

## 延伸阅读

- `mapleStoryMiniPet` —— 本插件渲染语义的来源（C# 桌宠）。
- `wzpy` —— 两个 worker 共用的 WZ 读取器。

<a id="模型体验"></a>
## 模型体验

直接通过十二个工具。每个返回文本加结构化数据，而精灵图、地图、纸娃娃、气泡这四个
还会写出 PNG / GIF 并返回路径或 URL，所以模型不用第二次调用就能展示结果。

#### KV 缓存影响

工具定义在 host 生命周期内是静态的。改任何工具 schema 都需要重启 host 才可见。

<a id="已知限制与待办"></a>
## 已知限制与待办

- 2500 把椅子是**按需加载**而不是启动即载，因为全量加载会让面板打开很慢。
- `sit` 动作在源数据里**只有 1 帧**，所以坐着的桌宠是完全静止的（椅子的 12 帧动画照播）。
- 气泡文字优先用 PingFang SC，没有时退到 STHeiti，所以字形像素与 C# 基准略有差异；
  气泡几何与边框像素是精确一致的。
- 分卷结构因发行版而异。`wz_sources` 里的探测正是为了让属性查询诚实，换客户端需要重新验证。
- 目前只有桌宠的坐姿由椅子驱动。C# 原版的多宠、音乐、托盘、Agent 状态等功能均未实现。

<a id="开发者注记"></a>
### 开发者注记

<details>
<summary>给维护者的工作上下文 —— 点击展开</summary>

三层都缓存得很激进，每一层都造成过「我明明改了却没生效」：

1. `index.js` 每个 host 进程只求值一次，因为 DSH 按路径 import。改动需要
   `bash tools/restart-host.sh`。
2. `client.js` 是以 bundle 形式提供的，而 bundle 的 rev 在 **host 启动时**算好。
   刷新浏览器不够，必须重启 host。
3. worker 在首次调用时 spawn，之后一直复用。用
   `pkill -f wz_worker.py` 或 `pkill -f pet_worker.py` 重启它。

声称渲染或算法改动完成之前，先跑 `bash tools/verify.sh` 与
`python tools/verify-render.py`。渲染检查器会拿 C# 基准图比对、对像素漂移判失败；
它存在的原因是**肉眼检查反复漏掉真实缺陷**。

</details>

**运行时不变式**：给定配置与 WZ 数据源，渲染是确定性的。缓存 key 里带**合成语义版本**，
所以改了渲染必须同时升 `_COMPOSE_VERSION`，否则会一直吐旧图。
