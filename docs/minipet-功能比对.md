# mapleStoryMiniPet ↔ DSH 插件：功能逐条比对

> 2026-10-15 生成。**先落文档，再按表实现。**
> 参照物：`/Users/a502/IdeaProjects/mapleStoryMiniPet/MiniPet/`（C# / Avalonia，V0.2.1）
> 现状：`dsh-maplestory-wz`（资源插件）+ `dsh-desktop-pet`（桌宠）

## A. 参照物能力清单（从源码读出，带出处）

| minipet 能力 | 出处 | 做什么 |
|---|---|---|
| **纸娃娃实时合成** | `Services/PaperdollService.cs` | 按 `action/frame` **逐帧**合成 body/head/hair/face/equip；耳朵按 head 里 `*ear` canvas 名分组（`earType` 索引，人类靠 head 自带圆耳）；表情 `default`/`blink`；**zmap 分层 + 基于 vslot 的遮挡锁**；WZ 读取全缓存，帧循环只做组装 |
| **动画状态机** | `Services/AnimService.cs` | `PlayMode`（Loop 等）、动作 fallback 链、帧延迟、`IEntityFrameProvider` 接入（怪物与纸娃娃同一套播放入口） |
| **聊天气泡** | `Services/BalloonService.cs`、`references/chatballoon-9slice.md` | 9-slice 拼接、缓存、显示控制、`clr` 颜色转换、文本居中 |
| **地图目录三级层级** | `Services/MapCatalogService.cs` | 地图 id 枚举、id→显示名缓存、**区域→街道→地图** 三级 |
| **地图渲染 + 相机** | `Services/MapService.cs` + `MapService.Render.cs` | `RenderViewport` 1:1 对齐 WzComparerR2.MapRender；`ClampCamera`；`GetMapCenter` |
| **地图行走** | `Services/Walk/{MapWalker,WalkGraph,WalkPathPlanner,WalkModeService}.cs` | 沿 foothold 走路、路径规划、梯子/绳索（`LadderRope`）、落地对齐 `PlacePetOnMapGround` |
| **多桌宠** | `Services/PetManager.cs` | 持有多个 `PetWindow`，配置增删、**主宠**概念、主宠绑定（地图背景 + Agent 特效） |
| **Agent 状态反映** | `Services/AgentState.cs` + `AgentStateAggregator.cs` | 对齐 Hermes 官方 Pets **7 态**语义 + 活动级排序 |
| **Agent 特效层** | `Services/EffectLayerService.cs` | `AgentState` → 可视反馈（如 Jump → WZ 庆祝动画；TODO 占位用「搞定!」气泡） |
| **音乐** | `Services/{MusicCatalogService,MusicPlayerService,BassMusicPlayer,MusicDecisions}.cs`、`lib/audio` | 曲目目录（曲→关联地图，每曲最多 5 个有名字的）、按地图/行走状态选曲、播放 |
| **聊天窗** | `Views/{DshChatWindow,HermesChatWindow}.cs` | 与 DSH / Hermes 的对话窗 |
| **素材浏览器** | `Views/MaterialBrowserWindow.cs`(+`.Logic`) | WZ 素材浏览选材 |
| **界面** | `Views/SettingsWindow.{Pets,Paperdoll,Background,Balloon,Music,Agent}.cs`、`Views/Tabs/`、`PetWindow.{Menu,Mode}.cs`、`MusicMiniBarWindow.cs`、`WalkPetVisibility.cs`、`WalkTransition.cs` | 设置分页、宠物右键菜单、模式切换（含「无限之路」`docs/ai/algorithm-infinite-road.md`）、行走时显隐、音乐迷你条 |
| **托盘 / 配置 / 缓存** | `Services/{TrayService,ConfigService,CacheManager}.cs`、`Models/AppConfig.cs` | 托盘、配置持久化、素材缓存 |

## B. 逐条比对（现状 ← 差距）

| # | 能力 | DSH 现状 | 差距 | 优先级 |
|---|---|---|---|---|
| A1 | 纸娃娃实时合成 | 面板「纸娃娃」页：`CharacterRenderer.compose()` 出**单帧静态图**，可挑 12 个部位、姿势、翻转 | ❌ 无逐帧动画、无表情（blink）、无耳朵分组、无 vslot 遮挡锁、无实时（每次改动重合成） | **P0** |
| A2 | 动画状态机 | 桌宠有 stand/move/hit 简化状态机（`dsh-desktop-pet/client.js`） | ❌ 未与纸娃娃统一（`IEntityFrameProvider` 那层没有）、无 Loop 模式表 | P1 |
| A3 | 聊天气泡 9-slice | 无 | ❌ 完全没有 | **P0**（Agent 说话要有气泡） |
| A4 | 地图三级层级 | 面板背景页：平铺列表 + 中文名搜索（经典 17 张 + 名录 21366 条） | ⚠️ 无「区域→街道→地图」树 | P1 |
| A5 | 地图渲染 + 相机 | worker `map_viewport_b64`（整图渲染 + Pillow 裁切）+ `contentBounds`；客户端 `cameraClamp` | ✅ 基本对齐；⚠️ 未走 `RenderViewport` 的逐帧路径（背景用静态图 + CSS 平移） | P2 |
| A6 | 地图行走 | 桌宠在**屏幕底部**来回走 | ❌ 不在地图 foothold 上、无路径规划、无梯绳、无落地对齐 | **P0**（你已明确要求） |
| A7 | 多桌宠 | 单宠 | ❌ 无多宠、无主宠 | P1 |
| A8 | Agent 7 态 | 无 | ❌ 完全没有（DSH 这边可以接到 session/tool 状态） | **P0**（这是 DSH 独有的价值点） |
| A9 | Agent 特效层 | 无 | ❌ 没有 | P1 |
| A10 | 音乐 | 无 | ❌ 没有（WZ `Sound.wz` 可读，5 卷 ~7.5GB） | P2 |
| A11 | 聊天窗 | 无 | 部分重合：DSH 自己有对话流，不需要再造 | — |
| A12 | 素材浏览器 | 面板「浏览」页（分类树 + 图片 + 动作预览） | ⚠️ 默认不在根目录、缺「按名字/编号快速定位」 | P1（你已提到） |
| A13 | 设置分页 | 面板 5 页签（浏览/搜索/图鉴/纸娃娃/背景） | ⚠️ 缺 宠物/气泡/音乐/Agent 分页 | P1 |
| A14 | 托盘/配置/缓存 | 配置落 `~/.dsh/wz-assets/*.json`、`~/.dsh/desktop-pet/pet-config.json` | ✅ 等价 | — |

## C. 本轮先做什么（按你的三条反馈 + 上表 P0）

1. **背景可见性 + 磨砂 + 随机换图 + 全中文名**（你截图红框那条）
   - 红框区域（侧栏/中栏）半透明 + `backdrop-filter: blur` 磨砂，背景在地图之上、内容之下
   - 地图**从左到右移动**（已有相机漂移）→ **自动随机换下一张**（已加 `mapRandom`/`mapFeatured`）
   - 地图名全部中文（已修：`String/Map.img` 21366 条，之前是 9 位补零对不上）
2. **纸娃娃中文名**（你说「纸娃娃也没有中文名」）
   - `list_parts` 只返回 id；要补名字：`String/Eqp.img` 里有装备名（按 8 位编号）
3. **纸娃娃桌宠本体**（A6 + A1）：宠物改成纸娃娃角色，并让它**走在地图 foothold 上**

## D. 待确认

- 「红框」指的是截图里被我圈出的整块内容区（侧栏 + 中栏）：磨砂是指这块半透明+模糊，地图在其后 → 按此实现。
- 自动轮换的默认间隔：先给 关 / 1 / 3 / 5 / 10 / 30 分钟，默认关（不静默改界面）。
