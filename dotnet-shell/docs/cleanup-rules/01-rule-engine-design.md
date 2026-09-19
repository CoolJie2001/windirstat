# 01 · 规则引擎核心设计

本文是清理功能的主设计。安全边界的完整定义在 [02-safety-policy.md](02-safety-policy.md)，本文只描述机制。

## 1. 分层与代码归属

三条既有红线（`dotnet-shell/README.md` 设计红线一节）在本功能上的具体落点：UI ↔ 扫描引擎仍只经 `IDiskScanEngine`；
清理**不是**扫描引擎的一部分，它是消费扫描结果与文件系统事实的独立子系统；`wds_core_api.h` 不动。

```
src/WdsShell.Core/Cleanup/            # 纯逻辑，零 IO、零 Win32。可 100% 用合成文件系统单测
├── Model/
│   ├── RulePack.cs                   # RulePack / Group / Rule 的源生成可序列化模型
│   ├── Candidate.cs                  # 候选 + 判定 + 拒绝原因
│   ├── CleanupPlan.cs                # 不可变冻结计划（含逐项 stat 快照与 PlanHash）
│   └── Enums.cs                      # Risk / ReparsePolicy / RejectReason / Origin
├── Policy/
│   ├── DenyRoots.cs                  # ★ 硬否决清单，编译进二进制，规则数据无法覆盖
│   └── PolicyGuard.cs                # CheckRoot(path) / CheckItem(path, attrs) → RejectReason?
├── Eval/
│   ├── IFileSystemProbe.cs           # 唯一被求值需要的 IO 抽象（枚举/stat/尺寸/占用）
│   ├── IKnownFolderProvider.cs       # known folder 名 → 绝对路径
│   ├── IProcessProbe.cs              # 进程是否在跑
│   ├── RuleEvaluator.cs              # 流水线 2~6 步（见 §4）
│   ├── GlobMatcher.cs                # 自有 glob/regex 匹配（无第三方依赖）
│   └── OperatorRegistry.cs           # ★ 算子注册表：新算子在此登记，未知算子直接拒绝加载
└── Json/
    └── CleanupJsonContext.cs         # JsonSerializerContext（源生成，AOT 安全）

src/WdsShell.Interop/Cleanup/         # 真实 Win32/COM。现有约定"全部 P/Invoke 锁在 NativeMethods.cs"
├── Win32KnownFolders.cs              # IKnownFolderProvider：SHGetKnownFolderPath + GetTempPath + 环境变量
├── Win32FileSystemProbe.cs           # IFileSystemProbe：\\?\ 前缀、GetCompressedFileSize、簇大小、重解析标签
├── Win32ProcessProbe.cs              # IProcessProbe
└── ShellFileOperation.cs             # IFileOperation：回收站/永久删、进度回调、取消、错误码
    # 注：这是 shell COM，不是 wdscore 的 C ABI，属于对 Interop 层职责的有意扩展

src/WdsShell.App/Cleanup/
├── Services/
│   ├── RulePackLoader.cs             # 读嵌入资源 + 用户目录，校验、合并、降级
│   ├── PlanRunner.cs                 # 预览 → 确认 → 执行 → Journal 的编排（后台线程）
│   └── CleanupJournal.cs             # %APPDATA%\WdsShell\Cleanup\journal\*.jsonl
├── CleanupViewModel.cs               # 简单模式分组视图 + 高级模式逐项视图
└── Views/CleanupPanel.axaml          # 主窗 Row1 的折叠面板
Resources/Cleanup/builtin-rulepack.json   # EmbeddedResource
```

依赖方向：`App → Core`、`App → Interop`、`Interop → Core`（实现接口）。**Core 不依赖任何一层。**
这条决定让 S1 阶段的全部逻辑可以在没有任何真实磁盘操作的条件下验收 —— 这是"绝不在真实用户目录上跑测试"的唯一可行办法。

## 2. 数据模型

### 2.1 规则包（磁盘上的 JSON）

```jsonc
{
  "$schema": "../schema/cleanup-rulepack-v1.schema.json",
  "schemaVersion": 1,
  "pack": {
    "id": "wds.builtin",
    "origin": "builtin",              // builtin | user  —— user 包永远无法提升 risk 之外的权限
    "version": "2026.09.0",
    "minAppVersion": "0.7.0"
  },
  "groups": [
    {
      "id": "browser.chromium.cache",
      "title": "cleanup.group.browser.chromium.cache",     // i18n key，不是字面量
      "summary": "cleanup.group.browser.chromium.cache.hint",
      "risk": "caution",                                     // safe | caution | danger（枚举固定）
      "defaultSelected": true,
      "applies": { "platform": ["windows"], "minBuild": 17763 },
      "guards": { "processAnyOf": ["chrome.exe", "msedge.exe"], "requireNoElevation": true },
      "evidence": {
        "source": "vendor-doc",                              // os-documented | vendor-doc | observed | community
        "url": "https://…",
        "verifiedOn": "2026-09-19",
        "verifiedOnSystems": "Win11 23H2 x64 / Chrome 141",
        "consequence": "缓存会被重建，首次打开相关站点变慢；不影响登录态与书签"
      },
      "rules": [
        {
          "id": "chromium.cache.codecache",
          "select": { "knownFolder": "LocalAppData" },
          "walk":   { "root": "Google\\Chrome\\User Data\\Default\\Code Cache",
                      "mode": "contents", "maxDepth": 8, "reparse": "never" },
          "match":  [ { "glob": "**/*" } ],
          "exclude":[ { "name": "LOCK" }, { "glob": "**/*.log" } ],
          "filter": { "onlyFiles": true, "minAgeDays": 14, "skipAttributes": ["ReadOnly", "System", "Hidden"] }
        }
      ]
    }
  ]
}
```

### 2.2 C# 模型（源生成友好）

```csharp
// Core/Cleanup/Model/RulePack.cs —— 全部 sealed record，属性可空，无多态字段
public sealed record RulePackFile(string SchemaVersion, PackHeader Pack, Group[] Groups);
public sealed record PackHeader(string Id, Origin Origin, string Version, string? MinAppVersion);
public sealed record Group(
    string Id, string Title, string Summary, Risk Risk, bool DefaultSelected,
    Applicability? Applies, Guards? Guards, Evidence Evidence, Rule[] Rules);
public sealed record Rule(
    string Id, Selector Select, Walk Walk, MatchClause[] Match,
    MatchClause[]? Exclude, RuleFilter? Filter);
```

`Risk` / `Origin` / `ReparsePolicy` / `RejectReason` 都是 `enum`，加载时严格校验（越界值 = 文件非法），
**不允许**规则包自定义枚举成员 —— 否则"把一切标成 safe"就成了数据层面的自由。

### 2.3 算子清单（v1 的全部能力，也是扩展面）

| 位置 | 算子 | 语义 | 扩展新算子需要 |
| --- | --- | --- | --- |
| `select` | `knownFolder` | 由 `IKnownFolderProvider` 解析，解析失败 → 整条规则 skip（不猜路径） | 注册 + 单测 |
| | `env` | 仅白名单环境变量：`TEMP`、`LOCALAPPDATA`、`USERPROFILE`、`ProgramData` | 注册 + 白名单 |
| | `literal` | 绝对路径；**`user` 包专用**，`builtin` 包禁用（内置规则必须可解释） | 注册 |
| | `scanSelection` | 用户在目录树/区块图里显式勾选的项 | 注册 |
| `walk.mode` | `contents` / `self` / `children` | 删目录内容 / 目录本身 / 仅直接子项 | 注册 |
| | `maxDepth`、`reparse: never`（唯一合法值，`always` 保留字但加载即拒） | 深度上限；永不跟随重解析点 | — |
| `match` | `glob` / `regex` / `name` / `extension` | `regex` 必须显式带 `^…$` 或声明 `anchored:false`，禁止裸子串 | 注册 |
| `exclude` | 同 `match` 全集 | 后写覆盖前写，顺序无关 | — |
| `filter` | `minAgeDays`、`maxSizeBytes`、`minSizeBytes`、`onlyFiles`、`onlyEmptyDirectories`、`keepNewest`、`skipAttributes` | 时间与体积闸门 | 注册 |
| `guards`（Group 级） | `processAnyOf`、`requireNoElevation`、`volumeTypes`、`requireOwnerIsCurrentUser` | 命中即整组跳过并给出原因 | 注册 |

**扩展即注册**：`OperatorRegistry` 是唯一入口，`RuleEvaluator` 不认识任何算子名。加载器遇到未登记算子直接判文件非法，
错误信息精确到 JSON pointer（`groups[3].rules[0].select.volumeGuid`：未知算子 `volumeGuid`，本版本支持 knownFolder/env/literal/scanSelection）。
这条让"加能力"和"防漂移"共用同一个机制，是本项目对"易于扩展 + 可维护性强"的具体回答。

## 3. 表达式能力的边界（明确不做）

不做：读取文件内容判断、执行外部程序、注册表匹配、条件嵌套、变量、脚本宿主、`include` 别的规则包。
理由：(a) `PublishAot=true` 下无脚本宿主可用；(b) 任何"能算"的能力都会变成"能删错"的能力；(c) 规则的可审计性优先于表达力。
真实需求超出算子清单时，走 S4 的"系统清理委托"或 S5 的显式外部命令通道（该通道自成风险区，见 04 文档 §5）。

## 4. 求值流水线

顺序固定，逐步短路。每一步的产物都是可展示的诊断信息，不做静默过滤。

```
 1 加载    读 builtin（嵌入资源） + user（Rules 目录） → 源生成反序列化 → 结构校验 → 算子校验
           合并规则：Group id 冲突时 user 包只能 *关闭*（enabled:false）或 *覆盖勾选默认值*，
           不能改写 builtin 组的 rules；user 包自带的 rules 只能落在新 group 下
 2 解析    selector → 绝对路径（known folder 缺失即 skip 整条规则）
 3 根否决  PolicyGuard.CheckRoot(root) → deny roots 命中 → 整条规则 RejectReason.RootDeniedByPolicy
           ★ 这一步在任何枚举之前，因此被否决的根连"会列出多少文件"都不会发生
 4 枚举    按 walk 规则遍历（不跟随重解析点、深度/条目上限、路径长度 \\?\ 归一）
 5 匹配    match 命中 → exclude 剔除 → 只保留 onlyFiles/onlyEmptyDirectories 允许的类型
 6 闸门    年龄 / 体积 / 属性 / 进程占用 / 卷类型 / 所有权 / 是否需要提权
           → 每个未通过项产出 RejectReason，进"被跳过"清单而非丢弃
 7 冻结    生成 CleanupPlan：逐项 path + kind + logicalSize + physicalSize + mtime + attrs
           + fileId/volumeSerial + ruleId + groupId + risk + PlanHash（清单内容的稳定 hash）
 8 预览    UI 展示：分组 / 风险 / 合计 / 真实可回收；用户可逐项排除（排除只改本次 Plan 的 selected 位）
 9 确认    照上游 ConfirmOperation 语义：列出受影响路径 + "不再询问"写回**该操作自己的**开关
10 复核    执行前逐项 stat 与 Plan 快照比对（size+mtime+attrs+fileId），不一致 → DriftSkipped
11 执行    并发删除（默认 4 路）→ 回收站或永久 → 逐项结果 + Win32 错误码 → 可取消
12 收尾    Journal 落盘 → 受影响子树交给扫描引擎 refresh（不手改内存树，对齐 WinDirStatModel.cpp:648-658）
```

第 10 步是关键：**预览与执行之间可能隔着几分钟的用户阅读时间**，其间世界会变。上游在 UDC 执行前也只是重新检查路径存在性
（`WinDirStatModel.cpp:716-734`），这里加强为快照全等，成本一次 `stat`，换来"绝不删预览里没见过的东西"。

## 5. 拒绝原因（可观测性的地基）

```csharp
public enum RejectReason
{
    None, RootDeniedByPolicy, ItemDeniedByPolicy, NotApplicable,      // 平台/known folder 缺失
    AgeNotMet, SizeOutOfRange, AttributeExcluded, TypeExcluded,
    ReparseBlocked, InUse, ProcessRunning, VolumeTypeBlocked,
    NotOwnedByCurrentUser, ElevationRequired, DriftSkipped, Cancelled,
    DeleteFailed, UnknownOperator, SchemaInvalid,
}
```

每个 `Candidate` 必须带一个 `RejectReason`。UI 的"查看被跳过的 128 项"与诊断日志都直接来自这份数据。
**这条是"用户为什么信任这个工具"的答案**：任何被排除的东西都能问出"为什么没删它"。

## 6. 执行器要点

* COM 声明优先用 `Microsoft.Windows.CsWin32`（MIT，源生成，AOT 安全）从 `IFileOperation`/`shobjidl.h` 生成，
  避免手写 vtable 顺序 —— `IFileOperation` 的方法顺序写错会得到难以定位的 `E_NOINTERFACE`/内存破坏。
  若引入源生成器成本过高，退化为手写 `[ComImport]` 声明 + 一个"能建对象、能 SetOperationFlags"的冒烟单测锁住 vtable。
* 标志位照抄上游语义并按操作分档（`WinDirStatModel.cpp:611-612`）：
  * 移到回收站：`FOFX_RECYCLEONDELETE | FOFX_ADDUNDORECORD | FOFX_SHOWELEVATIONPROMPT | FOF_NOCONFIRMATION`
  * 永久删除：仅 `FOFX_SHOWELEVATIONPROMPT | FOF_NOCONFIRMATION`，并且**默认不经过 shell**：
    先走并行 `File.Delete`（清 ReadOnly 属性）+ 目录逆序 `Directory.Delete` 快路径，剩余存活项再交给 `IFileOperation`
    （对齐上游两段式：`WinDirStatModel.cpp:547-607` 的快路径 + `:609-638` 的 shell 兜底）。
  * `FOF_NOCONFIRMATION` 只允许在**我们已经弹过自己的确认框之后**使用，两个确认框叠着弹是最差的体验。
* 调用线程必须显式设置套间（上游用 `ComApartmentScope`，`WinDirStatModel.cpp:617`）；`IFileOperation` 不支持并发实例，
  整个批次一个对象、一次 `PerformOperations`。
* 取消：`IFileOperation.SetShowIntegrationDialogs(false)` + 我们的进度框 `Cancel` → `IFileOperation` 无干净中止，
  只能在**排队阶段**停止继续 `DeleteItem`，并说明"已开始的那一批会跑完"。这条要写进 UI 文案，不能假装能秒停。
* 长路径：内部统一 `\\?\` 前缀枚举与删除；对外展示时去掉前缀。日志与快照一律用规范化后的形式。

## 7. 可回收空间的口径

不能拿扫描树里的尺寸直接报"释放 X GB"，因为当前托管引擎的物理尺寸是脚手架估算 ——
`WdsShell.Core/Engine/ManagedWalkEngine.cs:155` 的注释原话是"物理尺寸按 4KiB 簇向上取整。真实值由 native core 提供"；
而 `WdsShell.Core/Models/DiskNode.cs:18-43` 的 `DiskNode` 连 `mtime` 与 `attributes` 都还没采集（对该文件 grep `LastWrite|Attributes` 零命中）。
因此 Plan 阶段自带核算：

* 逻辑大小 = `GetFileSizeEx`；物理占用 = `GetCompressedFileSize` 向上取整到簇（`GetDiskFreeSpaceForPath`）。
* 硬链接：`GetFileInformationByHandle` 的 `nNumberOfLinks > 1` → 标记 `Shared`，UI 上注明"与其他文件共享，未必释放"。
* 删除前后各取一次 `GetDiskFreeSpaceEx`，**报告实测差值**；差值与预期不符时以实测为准。
* 移到回收站不释放空间，完成页必须写成"已移到回收站（尚未释放 N）· 清空回收站后生效"。

## 8. 版本化与兼容

| 维度 | 规则 |
| --- | --- |
| `schemaVersion` | 只增不改。加载器遇到 `> 当前支持` 直接拒绝并提示升级程序；遇到 `< 最低支持` 走内置迁移函数 |
| 未知字段 | **拒绝**（`JsonUnmappedMemberHandling.Disallow`），不静默忽略。防止用户以为写了 `minAgeDays` 实际被丢弃 |
| `pack.version` | 规则内容变更即变（影响勾选态解释与 Journal 追溯）；随应用发布 |
| 字段/枚举 | append-only：新算子、新 `RejectReason`、新 `Risk` 都不重排既有值（与 ABI 同一纪律） |
| 勾选态 | 存在 `settings.ini`，键 `Cleanup.Group.<id>.Selected`；规则包删掉某 group 后残留键忽略不清（与现有设置行为一致） |
| 用户包 | 只信任 `%APPDATA%\WdsShell\Cleanup\Rules\`；**永不**从"打开的规则文件"或网络位置加载 |

## 9. 测试策略

| 层 | 手段 | 关键断言 |
| --- | --- | --- |
| Core 求值 | 合成 `IFileSystemProbe`（内存树，可控 mtime/attrs/尺寸/占用） | 黄金用例：给定树 + 规则包 → 逐字节一致的 Plan |
| 策略 | 属性测试（fuzz 路径） | **不存在**任何规则包/任何输入能让候选落在 deny roots 之下；`reparse` 永不为 `always` |
| 加载 | 手写校验器用例 | 未知算子/未知字段/越界枚举/`schemaVersion` 不符 → 明确错误 + JSON pointer |
| 漂移 | 合成树在第 9 步后改动一项 | 该项 `DriftSkipped`，其余正常执行 |
| Interop | 一次性沙箱目录（`%TEMP%\wds-cleanup-fixture-<guid>`，用后即焚） | 快照/尺寸/长路径/重解析标签读取正确 |
| 执行器 | **只允许沙箱目录**；回收站通道单独一组用例，永久删通道单独一组 | 结果码、取消语义、失败项不被静默 |
| UI | 进程内自检 + `settings.ini` 断言（不用 UIA 点树行，见既有验证限制） | 面板折叠/展开、勾选态往返、预览计数 |

真实用户目录（浏览器配置、`.nuget`、OneDrive）**不作为任何自动化测试的目标**；内置规则的实测登记是人工在受控环境完成并写进 `evidence` 的（见 03 文档）。

## 10. 与上游的逐条对齐

| 本设计 | 上游源码证据 |
| --- | --- |
| 确认框列受影响路径 + 按操作持久化"不再询问" | `WinDirStatModel.cpp:687-708`（`ConfirmOperation`，`IDS_OPERATION_CONFIRMATIONs` + `IDS_DONT_SHOW_AGAIN`） |
| 命令启用条件用声明表 | `WinDirStatModel.Actions.cpp:97-150`（`commandFilter`：`allowNone/allowMany/allowEarly/focus/typesAllow/extra`） |
| 回收站要求本地盘 | 同上 `:76-79`（`hasRecycleBin` → `IsLocalDrive`） |
| 回收站 / 永久删 / 清空回收站 三个独立动作 | `windirstat.rc:130-132`，快捷键 `:343-344` |
| 删除标志位组合 | `WinDirStatModel.cpp:611-612` |
| 两段式删除 + 剩余项 shell 兜底 | `WinDirStatModel.cpp:547-607` + `:609-638` |
| 不跟随重解析点 | `WinDirStatModel.cpp:561`（`ITRP_MASK`）、`:783`（`IsFollowingAllowed`；默认 mount/symlink/junction block，见 `tests/Test-WinDirStat.ps1:11930`） |
| 执行前复核世界状态 | `WinDirStatModel.cpp:716-734` |
| 扫描未结束禁止清理 | `WinDirStatModel.Actions.cpp:727`（`IsScanSettled()`） |
| 删后 refresh 而非手改树 | `WinDirStatModel.cpp:648-658` → `RefreshItem` |
| 系统垃圾交给 cleanmgr / Storage Sense / DISM | `WinDirStatModel.Actions.cpp:691-702`、`windirstat.rc:153-164` |
| 用户自定义清理项的字段面 | `Options.h:99-129`（`USERDEFINEDCLEANUP`）、设置页 `Pages/PageCleanups.cpp` |

与上游的**有意分歧**（评审时请重点看这三条）：

1. 上游 UDC 是"跑任意命令行"（`WinDirStatModel.cpp:791-819` 用 `CreateProcess` + `%COMSPEC% /C`）；本设计的用户规则是声明式数据，任意命令走单独通道。
2. 上游没有内置垃圾清单；本设计有（带 evidence 准入）。这是普通用户诉求带来的新增责任面。
3. 上游删除的候选来自"用户在树里选中"；本设计多了"规则自动发现的候选"，因此必须补 Plan 冻结 + 漂移复核，上游不需要。
