# vendor/WzComparerR2.WzLib.dll

第三方 WZ 读取库的**编译产物**，随本插件分发。

- 上游：https://github.com/Kagamia/WzComparerR2
- 源码对照：`/Users/a502/IdeaProjects/maplestory-wiki-backend/WzComparerR2C#/`
- 许可证：**MIT**，Copyright © 2015 Kagamia Studio

MIT 允许随本插件再分发，前提是保留版权声明与本许可声明 —— 见 `WZCOMPARERR2-LICENSE`。

## 为什么要内置这个 DLL

它是 WZ 读取的统一标准（另见全局约定）。插件里 `.NET` 半边（`net/WzWorker`）
通过它读 WZ 与 `.ms` 结构容器，不再依赖 旧实现的第三方克隆。
