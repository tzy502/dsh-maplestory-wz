# vendor/wzpy

第三方 WZ 读取库的**内置副本**，来自 [Leonana69/wz-python](https://github.com/Leonana69/wz-python)。

## 为什么要内置

本插件原先从 `~/.hermes/cache/scratch/wz-python` 读取 wzpy。那是临时缓存目录，
**会被清理掉了**（2026-10-17 12:00 的一次 prune 删掉了它），插件随即完全失效：

```
POST /dsh-maplestory-wz/api  →  {"ok":false,"error":"WZ worker 退出（code=1）"}
```

内置之后插件自带读取器，不再受任何外部目录清理影响，npm 包也才是自包含的。

## 来源

- 上游：https://github.com/Leonana69/wz-python
- 许可证：MIT（见 `WZPY-LICENSE`），允许随本插件分发
- 同步方式：`bash tools/sync-wzpy.sh`（拉上游最新版覆盖本目录）

## 注意

- `wzpy/` 下的代码**不要本地修改**；要改行为请在上游或在本插件自己的 `py/` 里包一层，
  否则下次同步会被覆盖。
- 本插件实际用到的子模块：`canvas` `character` `effect_lookup` `map` `ms_container`
  `ms_file_v2` `properties` `wz_package`。
