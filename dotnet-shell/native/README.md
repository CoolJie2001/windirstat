# native/ — wdscore.dll（裁剪自上游 C++ core）

本目录是 C++ core 的落点。约定：**上游 fork 仓库不放任何 UI 相关新代码，core 工程只存在于
fork 仓库的 `wdscore/` 目录 + 这里的头文件契约**；构建产物 `wdscore.dll` / `wdscore.lib`
不入库（见 ../.gitignore），拷贝或引用到 .NET 输出目录 `native/x64/` 即可被 shell 加载。

## 构建目标（待实施）

`wdscore.vcxproj`（DLL，x64，C++23）——**显式文件清单**，只有下列文件参与编译；
上游新增文件默认对构建无影响，纳入清单是显式决策：

```
windirstat/Item.cpp                 # 磁盘树模型（含 ScanItems 引擎入口）
windirstat/Item.Extended.cpp
windirstat/FinderBasic.cpp          # 递归枚举 finder
windirstat/FinderNtfs.cpp           # ★ NTFS $MFT 直读 finder（核心竞争力，跟随上游演进）
windirstat/FinderMtp.cpp            # （第二阶段再纳入）
windirstat/Filtering.cpp
windirstat/CsvLoader.cpp
windirstat/Options.cpp              # （接缝补丁 S3：注册表依赖替换为注入式 settings）
windirstat/Localization.cpp         # 仅当 core 内断言/日志需要
contrib/xxhash/xxhash.c
wdscore/ScanEngine.cpp              # ★ 新增文件：从 CWinDirStatModel 提取的引擎 + 队列调度
wdscore/WdsCoreApi.cpp              # ★ 新增文件：wds_core_api.h 的实现（事件翻译/节流）
```

**明确排除**（这些是上游最热文件，排除即零冲突）：
`UiFramework.*`、`MainFrame*`、`Controls/*`、`Views/*`、`Dialogs/*`、`Pages/*`、
`Layout*.cpp`、`WinDirStatModel.Actions.cpp`（清理动作全部改在 C# shell 实现）。

## 接缝补丁（Seam Patches，fork 对上游既有文件的全部改动，目标 <300 行）

| # | 文件 | 改动 | 动机 |
|---|------|------|------|
| S1 | Item.h / Item.cpp | `CItem` 的 `CTreeListItem` 基类与 `Draw*`/`GetIcon` 用 `WDS_CORE_ONLY` 条件编译隔离；4 处 `CMainFrame::Get()->InvokeInMessageThread` 改经 `WdsPostToUiThread` 钩子（core 模式为 no-op） | 切断模型→UI 反向依赖 |
| S2 | WinDirStatModel.cpp/.h | `StartScanningEngine`/`StopScanningEngine`/队列成员移入 `ScanEngine`（代码物理搬移到 wdscore/，Model 保留调用转发） | 引擎与 God-Object 解耦 |
| S3 | Options.h/.cpp | 注册表读写经 `IWdsSettings` 接口（默认实现保持上游行为） | core 无注册表依赖，便于 shell 侧托管配置 |
| S4 | WinDirStat.cpp | 无改动（DLL 不链接启动路径） | — |

补丁以 git patch series 维护（`git format-patch` 导出至 fork 仓库 `patches/`），
每次上游同步后重放，见 ../docs/UPSTREAM_SYNC.md。

## 事件发射点（WdsCoreApi.cpp 的职责）

在 `ScanEngine` 的文件落树处（Item 添加/尺寸聚合）挂钩，把上游内部调用翻译成
`wds_core_api.h` 的 push 事件；PROGRESS / EXTENSION 事件在 core 侧节流（≥100ms），
这是 .NET 侧 UI 流畅的第一道保险。`MODEL_CHANGE` 中的视图类通知（selection/zoom/style）
**不出 core**——它们是 UI 概念，由 shell 自己管理。

## ABI 纪律

- 头文件即合同：`include/wds_core_api.h`，字段只增不改不重排；
- `wds_api_version()` 主版本不匹配时 shell 拒绝加载；
- 回调可能来自任意 worker 线程；字符串仅回调期间有效（shell 侧已按值复制）。
