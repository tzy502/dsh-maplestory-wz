# 冒险岛 WZ 资源插件 + 桌宠

把**本地冒险岛客户端**（WZ 数据，不联网、不要服务端）接进 DSH：
Agent 能直接查和渲染游戏资源，Web 面板能浏览搜索，页面上还能养一只会走动的桌宠。

素材全部现读 WZ 文件 —— **没有数据库、没有硬编码素材表**。

---

## 这个插件解决什么

冒险岛的素材（怪物、NPC、装备、地图、UI）都封在 `.wz` 里：格式私有、分卷存放、
还有一堆 `_outlink` 间接引用。想看点什么通常得开 WzComparerR2 那类专用工具，
没法在写代码或聊天的地方顺手查。

装上之后：

- **问一句就能查** —— Agent 直接调工具拿怪物属性、装备数值、技能图标、动作帧
- **点一下就能看** —— 面板里翻分类树、搜中文名、看怪物/装备图鉴、播动作 GIF
- **能拼出成品图** —— 纸娃娃（16 部位 + 2500 把椅子）、聊天气泡、地图背景
- **能玩** —— 桌宠浮在页面上自己走、可拖拽、可换宠、能坐上椅子

---

## 能做到什么效果

| 效果 | 说明 |
|---|---|
| **查数据** | 怪物 · NPC · 装备 · 地图 · 技能 · 消耗品，按中文名/英文名/编号搜，带属性表 |
| **看动作** | 任一实体的动作帧 → 按 `origin` 锚点对齐的精灵图 PNG，可选循环 GIF |
| **图鉴** | 怪物 / NPC / 装备一页看完：图标 + 信息表 + 属性 + 全部动作 + 帧网格 |
| **来源探测** | 一个编号的**全部候选来源**，标出「哪个有 info 属性 / 哪个只是图片仓库」 |
| **纸娃娃** | 16 部位自由搭配实时合成；带 2500 把椅子（含花田这类场景椅，会连人一起坐上去） |
| **聊天气泡** | 冒险岛 `UI/ChatBalloon.img` 九宫格气泡，489 种样式 |
| **地图背景** | 游戏地图当网页背景，相机平滑横移，到头自动换图（最近 20 张不重复），换图带交叉淡入 |
| **桌宠** | 页面上一只怪物或你自己的角色：走动、飞行、拖拽、点它有反应；**坐上椅子就不动了** |
| **资源更新** | 面板里跑 cmsdl 更新本机冒险岛客户端（进度 + 日志） |

---

## 界面

| 页签 | 用途 |
|---|---|
| **浏览** | 分类树 → 节点 → 图片/属性，像文件管理器一样翻 WZ |
| **搜索** | 全库按中文名/编号搜，结果直接跳图鉴 |
| **图鉴** | 怪物 / NPC / 装备的完整详情页 |
| **纸娃娃** | 16 部位 + 椅子 + 姿势 + 耳型，实时预览，一键应用到桌宠 |
| **背景** | 地图选择、清晰度、换图过渡、自动轮换 |
| **资源** | cmsdl 更新客户端资源 |

**纸娃娃页细节**

- **部位**：身体 · 头 · 发型 · 脸 · 帽子 · 上衣 · 套服 · 裤子 · 鞋 · 手套 · 披风 · 盾 · 脸饰 · 眼镜 · 耳环 · 武器
- **椅子**：独立区块（2500 把，懒加载），有自己的搜索框 —— 椅子在 `Item/Install`，
  不属于 Character 的 16 类
- **全库搜索**：16 类一起筛；本地每类 400 条的窗口之外的命中由服务端补
- **应用到桌宠**：点完**立刻生效**，不用刷新页面
- 配置存在 `~/.dsh/wz-assets/background.json` 的 `doll`，纸娃娃页与桌宠**共用这一份**

**地图背景细节**

镜头按 `mapleStoryMiniPet` 的 `ClampCamera` 规则在边界内往返：
`camX ∈ [MinX + halfW, MaxX − halfW]`，`halfW = viewW/(2*zoom)`；地图比视口窄时居中。

几个必须知道的实现点（踩过的坑）：

1. **画布用「内容边界」不是 VR 边界** —— VR 是给游戏相机用的，往往远大于画面
   （射手村 `VRTop=-1100`，天空只画到约 593），直接用 VR 会留白带。
2. **裁剪换算用实际渲染尺寸** —— 缩放会被压到上限，拿请求值换算会裁错区域。
3. **要看见背景，布局三列得半透明** —— 面板开启时插入作用域 CSS
   （`html[data-ms-wz-bg="on"]` + `color-mix` + 轻模糊）；背景层必须 `pointer-events: none`，
   否则会吃掉点击。
4. **不是所有地图都渲染得出来** —— 部分地图合成后几乎是空的；推荐射手村 `100000000`、
   自由市场 `910000000`、魔法密林 `101000000`。

---

## 装上以后

**1. 链进 DSH profile**

```bash
ln -s /Users/a502/IdeaProjects/dsh-maplestory-wz \
      ~/.dsh/profiles/web/node_modules/@local/dsh-maplestory-wz
```

在 profile 的 `package.json` 里加这条依赖，然后重启 host（见文末「改代码后为什么不生效」）。

**2. 配置数据源** —— 改 `cordis.patch.yml` 的 `config.dataDir`：

```yaml
- insert:
    - id: maplestory-wz
      name: '@local/dsh-maplestory-wz'
      config:
        dataDir: /Volumes/SSD/mxd/mxd/Data   # ← 改成你的客户端 Data 目录
        region: BMS                          # BMS / CMS / GMS / TMS …
        version: 228
```

**3. 开始用**

- **面板**：左侧栏点「冒险岛资源」
- **Agent 工具**：直接说「查一下花蘑菇的属性」「把 1210102 的走路做成 GIF」
- **桌宠**：默认浮在页面上，点它旁边的 ⚙ 换宠

---

## Agent 工具

| 工具 | 作用 |
|---|---|
| `wz_packs` | 有哪些分类与分卷 |
| `wz_tree` | 浏览节点树（目录 / 图片 / 属性） |
| `wz_search` | 按名字或编号搜 |
| `wz_item` | 编号 → 名字 + 属性 + 图标 |
| `wz_sources` | 一个编号的全部候选来源 + 主源判定 |
| `wz_sprite` | 动作帧 → 精灵图 PNG（可选循环 GIF） |
| `wz_map` | 地图名录 / 详情（VR 边界、层、物件、出生点、相机） |
| `wz_map_sprite` | 地图整图或指定视口 → PNG |
| `wz_char` | 纸娃娃：单帧 / 逐帧雪碧图 / 部件清单 / 姿势 |
| `wz_balloon` | 聊天气泡（489 种样式） |
| `wz_ms` | 读 `Data/Packs/*.ms`（动作→帧结构、是否 Spine） |

---

## 配置项

### `cordis.patch.yml` 的 `config`（改完要重启 host）

| 键 | 默认 | 说明 |
|---|---|---|
| `dataDir` | — | WZ 数据目录（**必填**） |
| `region` | `BMS` | 区域，影响字符串表与解码 |
| `version` | `228` | 版本号 |
| `wzpyPath` | 自动探测 | wz-python 克隆目录（含 `wzpy/`） |
| `python` | 自动探测 | Python 解释器，需 Pillow + numpy |
| `mobId` | `1210102` | 桌宠初始怪物 |
| `originFallback` | `center` | 缺 `origin` 时的兜底锚点 |
| `scale` | `1` | 桌宠缩放 |
| `speed` | `100` | 走动速度 |
| `flySpeed` | `60` | 飞行速度 |
| `flyChance` | `0.35` | 起飞概率 |

Python 探测顺序：`config.python` → `$WZ_PYTHON` → `~/.hermes/cache/scratch/wz-env/bin/python`
→ `/opt/homebrew/bin/python3` → `/usr/local/bin/python3` → `/usr/bin/python3`。
worker 启动时会**清掉继承的 `PYTHONPATH`**（hermes 会把 3.14 的 site-packages 塞给 3.11
解释器，导致 PIL 导入崩溃）。

### `~/.dsh/wz-assets/background.json`（面板里改，即时生效）

| 组 | 键 |
|---|---|
| 地图背景 | `enabled` `mapId` `bgVisibility` `blur` `switchDelayMs` `mapFadeMs` `recent` `scrim` |
| 纸娃娃 | `doll.equips` `doll.chair` `doll.pose` `doll.earType` `doll.flip` |

桌宠自己那份选择落 `~/.dsh/desktop-pet/pet-config.json`（`mode` / `mobId` / `equips`），
刷新和重启都记得住。

---

## 接口

```
POST /dsh-maplestory-wz/api          {"cmd":"<命令>","args":{…}}
GET  /dsh-maplestory-wz/file/<name>  渲染产物（PNG / GIF）
GET  /dsh-maplestory-wz/image?path=…&animation=…&scale=…
```

命令：`packs` `tree` `search` `get` `sprite` `imageUrl` `stats` `detail` `sources`
`charParts` `charPoses` `charAnim` `charRender` `charEars` `chairList`
`balloon` `balloonStyles` `mapList` `mapFeatured` `mapRandom` `mapNext` `mapCanvas`
`pet` `petGet` `petSet` `petSetChara` `petSetMob` `presets` `backgroundGet` `backgroundSet`

数据链路：

```
Web 面板 ──POST /dsh-maplestory-wz/api──┐
                                        ├─► Host 插件 index.js ─► Python worker（wzpy 读 WZ）
Agent 工具 ─────────────────────────────┘                              │
图片 <img> ──GET /dsh-maplestory-wz/file/<name>── ~/.dsh/wz-assets/ ◄──┘
```

---

## info 与 `_Canvas` 的区别（关键）

| 来源 | 是什么 | 该不该当信息源 |
|---|---|---|
| `{type}/{id}.img` | **信息主源**：动作结构、`origin`/`delay`/`z`/`map`，PNG 只是 `_outlink` 指向 | ✅ 首选 |
| `{type}/_Canvas/{id}.img` | **PNG 仓库**：像素数据；多数条目没有 `info` | ⚠️ 仅兜底 |
| `info/link` | 同型号共用数据，指向另一个编号 | 跟随并标注 |

实测本客户端（傲天 BMS 228）：

- `Mob/Mob.wz` 是 216 B 的**索引壳**，文件模式拿不到本体 img
- 怪物图**只有** `Mob/_Canvas/_Canvas_000~005.wz`（4394 个 img）；随机抽 300 个，
  只有 **3%** 带 `info`
- 所以「这只怪有没有属性」不能假设 —— 跑 `wz_sources` 看探测结果
- NPC / 装备正常：`Npc/{id}.img`、`Character/<类目>/{8 位补零}.img` 都有完整 `info`

路径写法：

```
Mob/_Canvas/1210102.img            怪物图（傲天把怪物图并进 _Canvas 分卷）
Npc/0011000.img                    NPC（本体在 Npc_000.wz，图在 Npc/_Canvas）
Character/Weapon/01332020.img      装备（8 位补零）；动作如 stand1 / swingO1
Item/Install/03019.img             椅子（键如 03019632）
UI/Basic.img                       UI 元件
```

---

## 渲染对齐

气泡、纸娃娃、椅子、地图相机全部照 `mapleStoryMiniPet` 的 C# 实现逐条对齐，
不自己发明规则。关键语义都有出处：

| 项 | 依据 |
|---|---|
| 气泡九宫格 | `BalloonService.RenderBalloon`：`n`/`s` 按 `colWidth` 平铺，`c` 平铺 `colWidth×colHeight`，`head` 贴顶行中，`arrow` 在底行开头；`canvas.Translate(-minL,-minT)` |
| 脸型 / 101 表情 | `AddExpressionPieces`：两者是 `face` 图层的**互斥变体**，同时穿要丢掉脸型 |
| ItemEff 特效 | `AddItemEffEffectPieces` → `Effect/ItemEff.img/{id}/effect/{frame}` |
| 椅子层级 | `AddChairEffectPiece`：`z<=0` 在角色后、`z>0` 在角色前；层序 `OrderByDescending(ZIndex)` |
| 椅子偏移 | `ComputeChairEffectOffset`：`锚 = navel − bodyRelMove`（花田椅 `bm=(47,-2)` → `(-47,+2)`） |
| 坐姿动作 | `ResolveChairAction`：`info/sitAction` 优先，缺省 `sit` |
| 地图相机 | `MapService.ClampCamera` |

**不靠肉眼验收**：

```bash
python tools/verify-render.py     # 渲染像素级比对（气泡对 C# 基准图、纸娃娃与桌宠同源一致）
bash tools/verify.sh              # 22 项全链路接口实测
```

---

## 改代码后为什么不生效

三个会让人误判「改了没用」的地方，都已在代码里防住：

1. **`index.js`（host 半边）** —— DSH 用 ESM `import()`，同一路径在进程内只求值一次
   → **必须重启 host**
2. **`client.js`（浏览器半边）** —— bundle 的 rev 在 **host 启动时**算好，
   **刷新页面不够** → 同样要重启 host
3. **`py/*.py`（worker）** —— host 只在首次调用时 spawn，之后复用同一子进程
   → 按需重启：`pkill -f wz_worker.py` / `pkill -f pet_worker.py`

```bash
bash tools/restart-host.sh        # 停旧 host 并按 profile 重新拉起（默认 profile=web，端口 3080）
```

---

## 开发

| 文件 | 作用 |
|---|---|
| `index.js` | host 半边：工具注册、API 路由、worker 池、桌宠配置 |
| `client.js` | 浏览器半边：面板六个页签、地图背景层、桌宠浮层 |
| `lib/client.js` | `client.js` 的镜像副本，**改完必须同步** |
| `py/wz_worker.py` | WZ 读取 / 搜索 / 渲染 worker |
| `py/pet_worker.py` | 桌宠雪碧图 worker |
| `py/chair_support.py` | 椅子层级与偏移（照 `PaperdollService`） |
| `py/doll_shared.py` | 纸娃娃共用语义（脸型互斥、类目判定） |
| `py/effect_support.py` | ItemEff 特效查找 |
| `py/cmsdl_support.py` | cmsdl 客户端更新 |
| `tools/verify.sh` `tools/verify-render.py` | 验收脚本 |
| `tools/restart-host.sh` | 重启 host |

---

## 已知限制

- **椅子 2500 把懒加载**（点「载入椅子列表」或输关键词）—— 一次全拉会拖慢面板
- **桌宠坐姿只有 1 帧** —— 原作 `sit` 就 1 帧，所以坐着时完全静止
- **气泡文字字体** 优先 PingFang SC，本机没有时退到 STHeiti，文字像素有少量差异
  （气泡边框与几何是 0.00% 差异）
- **数据源差异** —— 不同客户端的分卷结构不同（本机实测傲天 BMS 228）；换客户端要重新验证
- `@dsh-external/dsh-super-injector` 与本插件无关，它自己 import 失败会打日志

---

## 许可

私用项目。冒险岛素材版权归 NEXON / 盛大所有；本插件只读取你本机的客户端数据，不分发素材。
