# 冒险岛 WZ 资源插件（DSH）

把本地冒险岛客户端 **WZ 数据源**接进 DSH：Agent 工具 + Web 面板浏览/搜索/图鉴可视化。

## 它做什么

| 能力 | Agent 工具 | 面板 |
|---|---|---|
| 看数据源有哪些分类与分卷 | `wz_packs` | 分类列表 |
| 浏览节点树（目录 / 图片 / 属性） | `wz_tree` | 左侧树 |
| 中文名 / 英文名 / 编号搜索 | `wz_search` | 搜索页 |
| 动作帧 → 精灵图 PNG + 循环 GIF | `wz_sprite` | 动作切换、GIF 播放、缩放 |
| 编号 → 名字 + 属性 + icon | `wz_item` | — |
| **来源探测：info 主源 vs `_Canvas` 仓库** | `wz_sources` | 图鉴页「数据来源」卡 |
| **图鉴：怪物 / NPC / 装备一页看完** | — | 「图鉴」页：图标 + 信息表 + 属性 + 动作 + 帧网格 |

## 图鉴页（mob / npc / eqp）

「图鉴」页签支持三类实体，输入编号（或在「搜索」里点结果直接进入）：

- **基本区**：图标（按原始像素整数倍放大）+ 中文名、编号、WZ 路径、字符串表、属性来源、尺寸。
- **数据来源卡**：逐个列出该编号的全部候选来源，并标出「有 info / 仅图像 / 不存在」——
  这是本项目最容易踩的坑，见下节。
- **属性表**：把 `info` 树压平（最多 2 层）成 `键 → 值` 展示；`link` 目标的属性会合并进来并标注来源。
- **动作 + 帧网格**：每个动作的帧数/尺寸，选中后拉一张雪碧图，按 `origin` 锚点切出前 24 帧逐个显示。

## info 与 _Canvas 的区别（关键）

参考 `minipet-esp32/Server/MinipetServer/Services/WzService.cs` 与 WzComparerR2 的读法：

| 来源 | 是什么 | 该不该当信息源 |
|---|---|---|
| `{type}/{id}.img` | **信息主源**：动作结构、`origin`/`delay`/`z`/`map`，PNG 只是 `_outlink` 指向 | ✅ 首选 |
| `{type}/_Canvas/{id}.img` | **PNG 仓库**：像素数据；多数条目没有 `info` | ⚠️ 仅兜底 |
| `info/link` | 同型号共用数据，指向另一个编号 | 跟随并标注 |

实测本客户端（傲天 BMS 228）：

- `Mob/Mob.wz` 是 216 B 的**索引壳**，文件模式拿不到本体 img（`images` 为 0）。
- 怪物图**只有** `Mob/_Canvas/_Canvas_000~005.wz`（4394 个 img）；随机抽 300 个，只有 **3%** 带 `info`。
- 所以「这只怪有没有属性」不能假设 —— 跑 `wz_sources` 看探测结果。
- NPC / 装备正常：`Npc/{id}.img`、`Character/<类目>/{8位补零}.img` 都有完整 `info`，图标走 `info/icon` 的 `_outlink`。

## 数据链路

```
Web 面板 ──POST /dsh-maplestory-wz/api──┐
                                        ├─► Host 插件 index.js ─► Python worker（wzpy 读 WZ）
Agent 工具 ─────────────────────────────┘                              │
图片 <img> ──GET /dsh-maplestory-wz/file/<name>── ~/.dsh/wz-assets/ ◄──┘
单帧/图标 ──GET /dsh-maplestory-wz/image?path=…&animation=…&scale=…
```

- `py/wz_worker.py`：JSON-line over stdio 的常驻 worker；缓存已打开的数据根与已解析 img，详情在 host 侧另有 2 分钟短缓存。
- 精灵图按 **origin 锚点**对齐拼接：先算每帧在锚点空间的矩形，取并集做画布，再按格贴；`origin` 缺失时回退几何中心。
- `_outlink` / `_inlink` 自实现解析：傲天客户端把 `Weapon/`、`_Canvas/` 做成了**图片名前缀**而不是子目录，
  wzpy 自带的 `resolve_canvas_link`（走 `subdirs`）会解析失败，因此按「分类 + 图片路径」直接取图再取子树，并兼容 1×1 占位画布。

## 配置

| 键 | 缺省 | 说明 |
|---|---|---|
| `dataDir` | `/Volumes/SSD/mxd/mxd/Data` | WZ 数据源目录（内含 `Mob/` `Character/` … 分类目录） |
| `region` | `BMS` | WZ 区域密钥；傲天端为 BMS |
| `version` | `228` | WZ 版本号 |
| `wzpyPath` | 自动探测 | wz-python 克隆目录（含 `wzpy/`） |
| `python` | 自动探测 | Python 解释器；需装 Pillow、numpy |

Python 探测顺序：`config.python` → `$WZ_PYTHON` → `~/.hermes/cache/scratch/wz-env/bin/python`
→ `/opt/homebrew/bin/python3` → `/usr/local/bin/python3` → `/usr/bin/python3`。
worker 启动时会 **清掉继承的 `PYTHONPATH`**（hermes 会把 3.14 的 site-packages 塞给 3.11 解释器，导致 PIL 导入崩溃）。

## 改完代码怎么生效

| 改了哪半边 | 怎么生效 |
|---|---|
| `client.js`（面板 UI） | 刷新浏览器即可；client 走 `/plugins` bundle 路由，**不**需要重启 |
| `index.js`（Agent 工具 / HTTP 路由） | **必须重启 host**：DSH 用 ESM `import()`，同一路径在进程内只求值一次 |
| `py/wz_worker.py` | 必须重启 host（worker 是 host 的子进程） |

```bash
bash tools/restart-host.sh          # 停旧 host 并按 profile 重新拉起（默认 profile=web，端口 3080）
```

## 路径写法

```
Mob/_Canvas/1210102.img            怪物图（傲天把怪物图并进 _Canvas 分卷）
Npc/0011000.img                    NPC（本体在 Npc_000.wz，图在 Npc/_Canvas）
Character/Weapon/01332020.img      装备（8 位补零）；动作如 stand1 / swingO1
UI/Basic.img                       UI 元件
```

## 产出位置

- 精灵图 / GIF：`~/.dsh/wz-assets/`
- 面板图片经 `/dsh-maplestory-wz/file/<文件名>` 提供（1 天 immutable 缓存）
- 单帧 / 图标直出：`/dsh-maplestory-wz/image?path=…&animation=info/icon&scale=4`

## DSH 地图背景（相机规则复刻 mapleStoryMiniPet）

面板「背景」页签可以把整个 DSH 背景换成一张冒险岛地图，镜头按
`mapleStoryMiniPet/Server/.../Services/MapService.ClampCamera` 的规则在边界内往返漂移：

```
camX ∈ [MinX + halfW, MaxX − halfW]，halfW = viewW/(2*zoom)
地图比视口窄（minX > maxX）时居中；Y 同理
```

实现要点（都是踩过的坑，改之前先读）：

1. **画布用「内容边界」不是 VR 边界**。VR 是给游戏相机用的，往往远大于画面：
   射手村 `VRTop=-1100`，天空只画到约 593——直接用 VR 拼背景，顶上会留一条白带。
   worker 的 `map_content_bounds` 会渲一遍取 alpha 外接框；内容过小时自动回退 VR。
2. **裁剪换算必须用实际渲染尺寸**。`_render_full_map` 会把请求的 scale 压到
   `MAX_MAP_WORLD/长边`，拿请求值换算会裁错区域（实测把整片天空裁掉）。
3. **很扁的地图带撑不满 16:9 视口**。做法是：内容按宽度缩放、底对齐（地平线在下方），
   空档用图两端代表色（顶端＝天空、底端＝地面）做线性渐变延伸——不是拉伸单行
   （会出竖向条纹），也不是放大模糊（太抢戏）。
4. **要能看见背景，得让布局三列半透明**。`.frame` 与 sidebar/center/rightbar 都带不透明
   底色，且画在 `shell.overlay` 之上。client 半边在背景开启时插入一段作用域 CSS
   （`html[data-ms-wz-bg="on"] ...` + `color-mix` 半透明 + `backdrop-filter` 轻模糊），
   并把背景层显式设成 `pointer-events: none`（`overlayLayer > *` 默认是 auto，会吃掉点击）。
5. **缩放有上下限**：上限由纹理尺寸（4096）定，下限由「最长边至少占视口 78%」定——
   否则竖图/小地图（如「冬」278×495）会算出 1×1 的退化结果。
6. **不是所有地图都渲染得出来**。部分地图（如「冬」303090400）在 wzpy 的
   HaCreator 风格合成下几乎是空的；选地图时优先挑射手村（100000000）、
   自由市场（910000000）、魔法密林（101000000）这类。

配置写在 `~/.dsh/wz-assets/background.json`（`enabled` / `mapId` / `speed` / `scrim`），
默认关闭——开背景是显式选择，不静默改任何人的界面。

## DSH 内部桌宠

`/Users/a502/dsh-plugins/dsh-desktop-pet/`（`@local/dsh-desktop-pet`，行 id `desktop-pet`）：
把 C#/Avalonia 桌宠搬进 DSH Web——`shell.overlay` 里渲染一只本地 WZ 怪物，
按 origin 锚点切帧、自己来回走、可拖拽、点击有反应，其余区域 `pointer-events: none`。
接口：`POST /dsh-desktop-pet/api {"cmd":"pet"}`、`GET /dsh-desktop-pet/file/<name>`。

## 改代码后为什么"没生效"——三个坑都已在代码里防住

常驻进程 + 缓存这套结构有三个会让人误判"改了没用"的地方，都已加防护（踩过才写的，别删）：

1. **host 半边（`index.js`）**：DSH 用 ESM `import()`，同一文件路径在一个进程里只求值
   一次 → 必须重启 host。用 `tools/restart-host.sh`。
2. **worker（`py/*.py`）**：host 只是在**第一次调用时** spawn 它，之后一直是同一个子进程。
   改完 `.py` 若不重启 worker，接口会继续按旧逻辑返回。
   → 现在 worker 的 `stats` 会回传 `fingerprint`（各 `.py` 的 sha1 前 12 位），
   host 启动时与磁盘比对，不一致就打 warning 指名哪个文件、并告诉你
   `pkill -f wz_worker.py` 让它按需重启。
3. **地图 PNG 内存缓存**：历史上裁错区域那版渲出来的 1920×929，会按同一个 cache key
   一直命中——**光重启进程也不失效**（重启后第一次调用的结果又被缓存进去）。
   → 现在缓存命中前会校验「缓存的 w/h 是否满足本次请求的 target_w/target_h」，
   不符就重渲；`MAP_CACHE_SCHEMA` 也在 meta 字段变化时让我们主动作废旧缓存。

### 一条命令看出该不该重启

```bash
bash tools/verify.sh        # 20 项全链路实测；背景尺寸/w-h/padded 是最容易回归的三项
```

## 桌宠换宠（已实现）

面板之外的入口：宠物旁边的 ⚙ → 搜中文名/编号（花蘑菇 / 路西德 / 1210102）→ 立刻换。
接口：`POST /dsh-desktop-pet/api {"cmd":"petSet","args":{"id":"210100"}}`；
候选：`cmd:"presets"`（经典怪）与 `cmd:"search"`；当前选择落 `~/.dsh/desktop-pet/pet-config.json`，
刷新页面/重启都记得住。换宠时会先验证目标**真的有可渲染动作**再落盘，
避免把选择存成一个坏编号。
