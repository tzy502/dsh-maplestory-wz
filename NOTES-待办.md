# 待办 / 用户反馈（2026-10-15，胶水口述）

> 状态：**只记录，未修改**。

## 1. 这边加载逻辑和 R2 不一样

用户原话：「这边加载逻辑和R2不一样」。

已核实的差异：

| | WzComparerR2 | 本插件现状 |
|---|---|---|
| 加载单位 | 整棵 WZ 树一次装进内存（`Wz_Structure.LoadWzFolder`），节点随手可达 | 按分类懒加载（`root_for(pack)`），跨卷靠 `_outlink` 现解析 |
| 数据源 | 单文件模式 `LoadFile` + `DefaultAutoDetectExtFiles`，主树与 `_Canvas` 都在同一棵树里 | 分层包（`WzPackage`）合并 `_xx.wz` 分卷；`Mob.wz` 是空壳 |
| 取图 | `Wz_Png.ExtractPng()` + 自研 `PngEncoder` | `wzpy` 的 `decode_canvas` |
| 链接解析 | `ResolveToCanvas`（跳 `_outlink`）/ `ResolveUolNode`（**不跳**，保住 origin/delay） | 自研 `_resolve_ref`（因为傲天把 `Weapon/`、`_Canvas/` 做成图片名前缀，wzpy 自带的 `resolve_canvas_link` 走 `subdirs` 会失败） |
| 帧序列 | action → frame → 子节点（origin/delay 在 child） | 同左，但帧内多画布时按「面积最大」挑 |

**待办**：把 R2 的加载语义对齐（至少：树结构一次可见、`ResolveUolNode` 式的元数据优先、origin 缺失时的兜底规则），并在面板上体现「当前是哪种加载模式」。

## 2. MS 没加载 —— 【已实现】

用户原话：「MS没加载」。

**现状（2026-10-15 晚）**：已实现。
- worker：`packs_ms`（列 `.ms`）/ `ms_mob`（某怪的动作→帧、是否 Spine）/ `ms_stats`（已打开容器概览）
- host：`/api` 的 `msPacks` / `msMob` / `msStats`；Agent 工具 `wz_ms`；`wz_item`、`wz_sources` 的怪物结果里附 `.ms` 结构
- 面板：图鉴页（怪物）多一张「.ms 结构」卡
- 实测：`Mob_00000.ms` 打开 ~2.4s（之后缓存），含 **4311 个 mob / 121 个 Spine / 447k 条 UOL**；
  花蘑菇 `1210102` → `die1(3) hit1(1) stand(1)`，非 Spine。
- 未做：`Skill_00000.ms`（954MB）的技能属性树（`wzpy.ms_wz.parse_skill_imgs` 可用，但要单独接口）、
  Spine 骨骼的实际渲染。

原文（当时的问题描述）：
指的是 `Data/Packs/*.ms`（`Mob_00000.ms` 394MB、`Skill_00000.ms` 954MB）——**当时完全没读**。

已探明的事实：

- `wzpy.ms_container.MsContainer.open()` 可用，属性含 `mobs` / `imgs` / `skeletons` / `skill_imgs` / `summary`。
- 怪物结构形如 `mobs[mid] = {animation: {frame}}`；canvas 帧是 1×1 占位 + `_outlink` 指向 `Mob/_Canvas/<id>.img/<anim>/<frame>`（与 Character 包同一套 outlink 模型）。
- 像素压缩是**未识别编解码**（非 zlib），所以 `.ms` 里的 canvas 解不出像素——像素仍从 `_Canvas` 取，`.ms` 提供的是**结构/骨骼/meta**。
- 本客户端 `Mob/_Canvas/*.img` 抽样 300 个只有 **3%** 带 `info`；R2/minipet 侧是把 `.ms` 当 origin/delay 的 meta 来源用的。

**待办**：worker 加 `.ms` 支持（至少 `MsContainer.summary/mobs/imgs` 浏览 + Spine 骨骼列表），面板加「Packs(.ms)」入口，并在图鉴里标注「meta 来自 .ms / 像素来自 _Canvas」。

## 3. 桌宠没有更换，更换体系全部迁移

用户原话：「桌宠没有更换 更换体系全部迁移」。

现状：`/Users/a502/dsh-plugins/dsh-desktop-pet/` 只渲染**花蘑菇 1210102**，能力止于「走路 + 拖拽 + 点击」。
`mapleStoryMiniPet` 的完整体系（见其 `MiniPet/Services/` 与 `MiniPet/Views/`）远不止这些。

**待办（全部迁移）**：

- 宠物**更换体系**：可换怪物/NPC 编号、按名字搜索选择、预设列表、记忆上次选择。
- 多宠物（`docs/ai/PLAN-0.1.3-multi-pet.md`）。
- 表情 / 聊天气泡（`docs/ai/chatballoon-algorithm.md`、`ChatBalloon` 九宫格）。
- 行走状态机（`Services/Walk/`）——起伏地形、梯子绳索（`LadderRope`）、落地对齐 `PlacePetOnMapGround`。
- 交互菜单（`Views/PetWindow.Menu.cs`）、音乐/时钟（`docs/ai/clock-display-spec.md`）。
- 与地图背景的**同源坐标**：宠物应站在当前背景地图的地面上，而不是屏幕底部。

## 4. 背景没有加载

用户原话：「图2 背景没有加载」。

### 已定位并修掉的根因

`mapBackground` 返回的图是 **1920×929**（应为 1920×1080），原因有两层：

1. **worker 裁剪换算 bug**：`_render_full_map` 会把请求 scale 压到 `MAX_MAP_WORLD/长边`，但裁剪用**请求值**反算 → 裁错区域（旧图上半截全黑）。已改为按实际渲染尺寸反算。
2. **运行中的 worker 是旧代码**：改完文件后 host 进程内的 worker 仍跑旧模块（ESM 缓存），API 一直回旧图。杀掉 worker 子进程让它 respawn 后才生效。

### 修复后实测

```
后台 API: ok=true  w,h=1920 1080  padded=edges  bytes=1570122
落盘:     /tmp/bg_v2.png → (1920, 1080) ✅
tools/verify.sh: 17 通过 / 0 失败
```

### 仍需人工确认

**面板 UI 我看不到**（浏览器需要 3080 的访问令牌）。如果你刷新后背景还是不显示，请回报「顶栏/侧栏是否还在、有没有报错」，我按下面这份清单逐条排：

1. 背景层挂载：`shell.overlay` occupant `maplestory-wz-background`（`Slots.listSubTree` 已确认 active）
2. 背景开关：`~/.dsh/wz-assets/background.json` 的 `enabled` + `mapId`（当前 `true` / `100000000`）
3. `html[data-ms-wz-bg="on"]` 是否被加上（BackgroundHost 的 useEffect）
4. 三列半透明 CSS 是否命中（`.pI_x6G_frame` / `.centerCol` / `.sidebarCol` / `[data-rightbar-col]`）
5. 图是否 200：`/dsh-maplestory-wz/file/mapbg_*.png`
6. `background-position` 用的实际图宽（`img.w`，不是请求宽）
7. 客户端是否为最新：**改 client.js 必须 `cp client.js lib/client.js` 并刷新页面**

## 附：本次重启方式（供后续复用）

```bash
bash tools/restart-host.sh        # 停旧 host 并按 profile 重新拉起
# worker 是 host 的子进程；若只改了 py/wz_worker.py，杀掉 worker 让它 respawn 即可：
kill $(pgrep -f wz_worker.py)
```
