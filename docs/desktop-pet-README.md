# @local/dsh-desktop-pet —— 冒险岛桌宠（DSH Web 插件）

把 C# / Avalonia 桌宠（`/Users/a502/IdeaProjects/mapleStoryMiniPet`）搬进 DSH Web：
`shell.overlay` 透明浮层里渲染一只本地 WZ 怪物，按 **origin 锚点**切帧、自己来回走、
拖拽换位置、点击有反应。浮层其余部分 `pointer-events: none`，不挡 DSH 交互。

## 装了以后

- 行 id：`desktop-pet`，包名 `@local/dsh-desktop-pet`
- Client 半边挂在 `shell.overlay`（list 槽，`id: desktop-pet`，`order: 30`）
- Host 半边提供 HTTP：
  - `POST /dsh-desktop-pet/api` `{"cmd":"pet","args":{...}}` → 怪物名 + 动作表 + 雪碧图元数据
    （也支持 `GET /dsh-desktop-pet/api?cmd=pet&id=1210102`）
  - `GET /dsh-desktop-pet/file/<name>` → 雪碧图 PNG
  - cmd：`pet`（默认） / `info` / `stats`

## 数据流

```
本地 WZ (/Volumes/SSD/mxd/mxd/Data)
   ↓  py/pet_worker.py（wzpy + Pillow，JSON-line over stdio）
雪碧图 PNG（~/.dsh/desktop-pet/pet_<id>_<action>_<fallback>_<hash>.png + .json 旁车缓存）
   ↓  HTTP /dsh-desktop-pet/file|api
client.js（React，shell.overlay）逐帧切 background-position
```

## 与 C# 实现的对应关系（照搬，不是自创）

| Web 侧 | C# 源 | 口径 |
|---|---|---|
| `advanceFrame()` in `client.js` | `Services/AnimService.cs: Tick()` | 真实 delta 累积推进；单 tick 最多追 **8** 帧；Loop 到头回第 0 帧；OneShot 播完切 `pending ?? "stand"` |
| `FALLBACK_DELTA=33` / `MAX_DELTA=500` | `Views/PetWindow.axaml.cs: OnTimerTick()` | delta ≤ 0 按 33ms；delta 封顶 500ms |
| 帧延迟（WZ 无 `delay` 时） | `Services/WzService.cs: BuildSpriteStrip()` | **120ms**（`GetValueEx<int>(120)`）；帧数据缺失兜底 240ms（`SpriteStrip.DefaultDelay`） |
| `compose()` in `py/pet_worker.py` | `WzService.BuildSpriteStrip()` | 联合画布 `cw = max(ox)+max(w−ox)`、`ch = max(oy)+max(h−oy)`，公共 origin `(maxL, maxT)`，第 i 帧贴 `(maxL−ox, maxT−oy)`，单行横排；跳过 null / 1×1 退化帧 |
| `left = x − anchor.x`，`top = y − anchor.y` | `PetWindow.axaml.cs: OnTimerTick()` | 「窗口 = 锚点 − 当前帧 origin」：切动作时**锚点不动**（origin 补偿）→ 脚底不跳 |
| `transformOrigin: anchor` + `scale(±k, k)` | `Services/MapService.Render.cs: DrawSpriteScreen()` | `translate(anchor)→scale(flipX?−zoom:zoom, zoom)→drawBitmap(−ox,−oy)` = **绕放置点镜像**，所以朝向翻转不产生位移 |
| `flipped = facing >= 0` | `PetWindow.axaml.cs: TickWalkMode()` | 「原始帧朝左，向右走（Facing ≥ 0）才翻转」 |
| 拖拽阈值 `|dx|+|dy| < 4` → 点击 | `PetWindow.axaml.cs: SetupDragEvents()` | 点击不进入拖拽；拖拽动作 `fly → move → stand`（`actions.Contains` 顺序） |
| `ACTION_DEFS` + `resolveAction()` | `AnimService` 动作定义 / `LoadStripWithFallback()` | `stand/move/fly` Loop，`hit`/`die` OneShot；fallback 链 `move/fly/hit→stand`，stand 再退 `move/fly/walk` |

### 与 C# 的唯一差异（有据可查）

C# 在 `origin` 缺失时用 `origin ?? (0,0)`（帧左上角当锚点）。但**本客户端的数据里
根本没有 origin / delay**：

- `Mob/_Canvas/1210102.img` 的帧节点（`stand/1`、`move/0..2`、`hit1/0`、`die1/0..2`）
  都是**无子节点**的 `WzCanvasProperty` —— dump 过节点树确认；
- `Mob/1210102.img` 不存在，`Mob.wz` 打开后 `root.images` 为空（纯目录壳）；
- `Packs/Mob_00000.ms` 经 wzpy 只能取到动作/帧结构（`MsContainer`），取不到 origin/delay。

于是 `originFallback` 默认 **`center`**（帧中心，与既有 `dsh-maplestory-wz` 的 `compose()`
对无 origin 帧的兜底一致）：逐帧裁剪尺寸不同时角色不会上下跳。
配 `originFallback: topleft` 可 100% 复原 C# 行为。

## 配置（`cordis.patch.yml` 的 `config`）

| 键 | 默认 | 说明 |
|---|---|---|
| `dataDir` | `/Volumes/SSD/mxd/mxd/Data` | WZ 数据源 |
| `region` / `version` | `BMS` / `228` | WZ 区域与版本 |
| `mobId` | `1210102`（花蘑菇） | 换怪物只改这一项 |
| `originFallback` | `center` | `center` / `topleft`（见上） |
| `scale` | `2` | 1 WZ px = 几 CSS px（`image-rendering: pixelated`） |
| `speed` | `100` | 走动速度（WZ px/s，C# `MapWalker.HSpeedMax=125` 同口径，×`scale` 后是 CSS px/s） |
| `groundMargin` | `6` | 站立时外框底边离视口底部多少 CSS px |
| `delayScale` | `1` | 帧延迟整体倍率（嫌跳太快就调大） |
| `idleMinMs` / `idleMaxMs` | `900` / `2800` | 走完一段后的站立时长 |
| `minLeg` / `maxLeg` | `140` / `420` | 一次「走一段」的距离（CSS px） |
| `edgeInset` | `8` | 撞墙掉头时离视口边缘的留白 |

`python` / `wzpyPath` 可显式指定解释器与 wzpy 目录（默认 `~/.hermes/cache/scratch/wz-env/bin/python`
与 `~/.hermes/cache/scratch/wz-python`）。spawn 时会删掉 `PYTHONPATH`。

## 行为

- **自己走**：随机方向走 `minLeg~maxLeg`，播 `move`；停下播 `stand`（`idleMinMs~idleMaxMs`）；
  撞到视口边缘立刻掉头（朝向 = 行进方向，按上面的翻转规则镜像）。
- **拖拽**：按下后位移 ≥ 4px 才算拖拽（跟手，播 `fly`/`move`）；松手落点即新位置，
  在原高度继续来回走（y 不重置）。
- **点击**：`hit1`（无则 `hit`）播一次 OneShot，播完回 `stand`，停一下再继续走。
- **不挡交互**：浮层 `pointer-events: none`，只有宠物那一块 `auto`。

## 换怪物

改 `cordis.patch.yml` 里的 `mobId`，重启 host。编号要能在 `Mob/_Canvas/<7位补零>.img`
或 `Mob/<7位补零>.img` 找到且带动作（用 `wz_sources` / `wz_search` 查）。
动作缺失时按规定 fallback 链降级（例如没有 `fly` 就用 `move`，没有 `stand` 就用 `move`）。
