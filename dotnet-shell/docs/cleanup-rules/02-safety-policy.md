# 02 · 安全策略：硬否决、闸门与风险分级

本文定义的边界**优先级高于任何规则数据**。规则包（内置或用户）只能在这道边界内收窄范围，永远不能放宽。
实现位置：`WdsShell.Core/Cleanup/Policy/DenyRoots.cs` + `PolicyGuard.cs`（见 01 文档 §1）。

## 1. 五道闸门

| 闸 | 名称 | 判据 | 未通过的呈现 |
| --- | --- | --- | --- |
| G0 | 硬否决 | 路径命中 deny roots（§2）→ 整条规则作废，连枚举都不开始 | 规则加载诊断（高级模式可见），普通用户完全看不到该项 |
| G1 | 来源合法 | 候选只能来自：`scanSelection`（用户勾选）、`knownFolder`/`env` 白名单、内置分组 rules。**禁止**按扩展名全盘扫描 | `RejectReason.NotApplicable` |
| G2 | 时间与占用 | `minAgeDays`（默认 30，任何 `danger` 组不得低于 7）、体积区间、属性排除、进程占用、卷类型、所有权 | `AgeNotMet` / `InUse` / `ProcessRunning` / … |
| G3 | 预览冻结 + 漂移复核 | 先出 Plan，执行前逐项 stat 全等复核（01 文档 §4 第 7/10 步） | `DriftSkipped` |
| G4 | 可恢复 + 诚实口径 | 默认回收站；永久删需独立动作 + 独立确认；释放量按实测报告 | 完成页文案（§6） |

任何一道闸的失败都**必须留下原因**，不允许静默过滤 —— 见 01 文档 §5。

## 2. 硬否决清单（deny roots）

匹配方式：先规范化（去 `\\?\`、解析大小写不敏感、去尾点/空格），再按**不区分大小写的前缀**比较；
`$`/`*` 类通配只在本清单内使用，规则数据里不引入。

| # | 目标 | 为什么 |
| --- | --- | --- |
| R1 | 任何卷根（`C:\`、`D:\` …） | 一次误选就是整盘 |
| R2 | `%SystemRoot%` 全子树（含 `System32`、`WinSxS`、`Windows\Temp`） | 组件存储是硬链接，删了不释放且破坏 servicing；`Windows\Temp` 交给 cleanmgr/DISM（G1 的委托通道），不自删 |
| R3 | `Program Files`、`Program Files (x86)`、`%ProgramData%\Microsoft\Windows\Installer` | 删 Installer 会让软件无法修复/卸载，是不可逆损害 |
| R4 | 用户配置文件根本身、`%USERPROFILE%`、`NTUSER.DAT*`、`UsrClass.dat*`、`%LocalAppData%\Microsoft\Windows\`（除显式 allow 的 `Explorer\thumbcache_*.db`） | 注册表 hive 与 shell 状态 |
| R5 | 已知文件夹中的 **Desktop / Documents / Pictures / Downloads / Music / Videos**（含重定向到 OneDrive 的解析结果） | 这些是"用户文件"，不是垃圾；Downloads 尤其：里面是用户主动留下的东西 |
| R6 | OneDrive / 其他云同步根及其子树（含占位文件） | 占位文件删除可能触发云端语义；同步冲突不可预期 |
| R7 | `System Volume Information`、`\Boot`、`EFI`、`Recovery`、`pagefile.sys`、`swapfile.sys`、`hiberfil.sys` | 休眠/页面文件属于系统配置变更，不是清理 |
| R8 | `$Recycle.Bin`（**唯一例外**：专用"清空回收站"动作走 `SHEmptyRecycleBin`，对齐上游 `WinDirStatModel.Actions.cpp:313`，不做目录遍历删除） | 手工遍历删会造成回收站元数据不一致 |
| R9 | 任何重解析点（junction / symlink / mount point）作为**目录遍历的穿越点**；以及作为删除目标时的"连带删目标" | Shell 删一个 junction 目录默认会把目标内容一起删掉 —— 这是同类工具最经典的翻车点 |
| R10 | UNC 路径、可移动盘、只读卷、未解锁的 BitLocker 卷、`SUBST` 出来的盘 | 语义不稳定、跨机副作用；上游另有 `IsSUBSTedDrive()`（`HelpersTasks.h:93`）可参考 |
| R11 | 当前进程自己的可执行目录与 `%APPDATA%\WdsShell\`（含 `settings.ini`、规则与 Journal） | 自毁路径 |

> R1/R2/R3/R4/R5/R7/R8/R11 是**清单即代码**：单测里逐条断言"用任意规则包 + 任意输入都无法在这些根下产出候选"（01 文档 §9 的属性测试）。

## 3. 已知"看着像垃圾其实不能删"的反模式

内置规则评审与用户规则校验都要过这张表：

| 目标 | 直觉 | 实际 |
| --- | --- | --- |
| `WinSxS` / 组件存储 | 巨大、可清 | 硬链接密集，手删不释放且破坏更新能力 → 只能 `DISM /StartComponentCleanup`（上游 `windirstat.rc:159-164`） |
| Prefetch | "老工具说可清" | 清了短期内启动更慢，无收益 → 本项目**不做** |
| `ProgramData\Microsoft\Windows\Installer` | 几百 MB 的"缓存" | 删了软件无法修复/卸载 → R3 硬否决 |
| `.nuget\packages` / npm `_cacache` | 包缓存，能重建 | 离线/内网机器会当场失去构建能力 → 只能是 `caution` + 明示后果 + 默认不勾（面向普通用户时干脆归到"开发者分组"，默认关闭） |
| 浏览器 `User Data\` 根、`Login Data`、`Cookies`、`Local Storage` | "缓存" | 清掉的是登录态/本地数据 → 只能命中 `Cache`/`Code Cache`/`GPUCache` 这类明确可重建目录 |
| `Windows.edb`（搜索索引） | 很大 | 被系统锁定，删除会触发索引重建甚至损坏 → 不做 |
| 缩略图缓存 `thumbcache_*.db` | 可重建 | 被 Explorer 持有句柄，强删留脏状态 → 只在 `processGuard` 判 Explorer 空闲时列入，`caution` |
| `*.tmp` 按扩展名全局匹配 | 经典清理 | BleachBit `cleaners/winrar.xml` 就有 `path="%ProgramFiles%\WinRAR\" regex="\.tmp$"` —— 这就是误删形状。本项目**禁止**无根限定的扩展名匹配（`match` 必须有 `walk.root` 且 root 通过 G0/G1） |
| 空目录 | 无价值 | 有些是应用运行期占位（`AppData` 下一堆）→ 只对 `scanSelection` 开放，不自动清 |
| 日志文件 | 可清 | 用户可能正在排障 → 只列 `caution`，且年龄 ≥ 30 天 |

## 4. 风险分级语义（枚举固定，不可由数据自定义）

| `risk` | 含义 | 默认勾选 | 确认强度 | 普通用户可见 |
| --- | --- | --- | --- | --- |
| `safe` | 应用会自动重建；最坏情况是多一次下载/重登录之外的性能损失 | 是 | 一次批量确认（列路径 + 可"不再询问"） | 是 |
| `caution` | 会丢历史、登录态、离线能力、需要联网重建，或影响特定工作流 | 视收益（逐个决定并记入 03 登记表） | 分组行必须显示 `evidence.consequence` 原文；"不再询问"仅在该次会话内有效 | 是，标黄 |
| `danger` | 不在内置包中出现；仅用户规则可能被校验器判为 `danger`（如 `literal` 根、无年龄限制、命中 `caution` 反模式） | 否 | 逐组独立确认 + 需要输入固定词（`清理`）+ 永久删另开 | 否（只在高级模式出现） |

内置包允许的最高级别是 `caution`。若某条内置规则需要 `danger` 才安全，说明它不该内置。

## 5. 提权与权限

* 默认以普通用户令牌运行；**任何**清理动作都不以管理员权限静默执行。
* 需要提权的项走两条不同的路：
  1. 系统级 → 交给 cleanmgr / DISM / Storage Sense（上游用 `isElevationPossible` 作为菜单启用条件，`WinDirStatModel.Actions.cpp:103,107-109`），由那些工具自己弹 UAC；
  2. 文件级 → 只允许 `IFileOperation` 的 `FOFX_SHOWELEVATIONPROMPT`（`WinDirStatModel.cpp:611`）在 shell 内部弹，我们的进程不提权。
* `guards.requireNoElevation` 为真时，遇到 ACL 不可写的项 → `ElevationRequired`，直接跳过。
* 所有权不是当前用户 → `NotOwnedByCurrentUser`，跳过（不尝试改 ACL —— 改 ACL 是比删除更危险的动作）。

## 6. 文案诚实性规范（面向普通用户的硬要求）

1. 完成页必须区分"移到回收站（尚未释放）"与"已释放"。按钮文案用"移到回收站"，**不出现"清理"当删除的同义替换**。
2. 合计面积写实测差值（01 文档 §7），不是候选逻辑大小之和。
3. 每个 `caution` 分组必须显示一行"删了会怎样"，来自 `evidence.consequence`，不得留空。
4. 空结果要写"没有发现可安全清理的文件"，而不是"0 B 可回收"（后者暗示工具该找到东西）。
5. 不展示"加速/优化/深度清理"这类暗示越删越快的措辞。永不建议重启、不改注册表、不动启动项。
6. 失败项必须出现在结果里，带可读原因；不允许"成功清理 998 项"掩盖 2 项失败。

## 7. 最坏情况推演（评审用）

| 假设事故 | 拦截点 |
| --- | --- |
| 内置规则把 root 写成 `%LOCALAPPDATA%`（整个目录） | G0 R4/R5 + 加载器另有"root 深度 ≥ 3 层且不得是 known folder 本身"的形状校验（§8） |
| 用户规则写 `literal: "C:\Users\x\Documents"` | G0 R5 → 整条规则作废并报错 |
| 用户规则写 `glob: "**/*.db"` 且 root 为 `AppData` | 命中 `danger` → 默认不勾 + 需要输入确认；`AppData` 本身是 known folder 根 → §8 形状校验先拒 |
| 目标目录里有个 junction 指向 `D:\work` | G2 `ReparseBlocked`（不遍历）；删除目标本身也不允许穿透（R9） |
| 预览后 3 分钟用户在别处放了新文件 | G3 `DriftSkipped`（mtime/size 快照不符） |
| Chrome 正在跑，缓存里 40% 是被占用 | `processGuard` → 整组跳过并提示"先关 Chrome"；未提示到的占用文件由 `InUse` 逐个跳过 |
| 磁盘是 OneDrive 占位盘 | R6 + 属性 `Offline`/`ReparsePoint` 在 G2 被排除 |
| 规则 JSON 被下载后手工改名放入 Rules 目录 | 用户包权限裁剪（§9）：只能用 `knownFolder`/`env`/`scanSelection` 选择器？否 —— `literal` 允许但完全受 G0 支配；不引入签名信任模型，因为一旦有签名就会诱导"放宽闸门"。边界靠代码，不靠来源 |

## 8. 规则形状校验（加载期，先于求值）

| 校验 | 规则 |
| --- | --- |
| Root 深度 | 解析后的 `walk.root` 相对其 selector 至少 1 层；**禁止** selector 本身即 root（防止"整个 LocalAppData"） |
| Selector 即 known folder 根 | 直接拒绝，错误信息给出建议的更深层 |
| `match` 空 | 视为非法（不允许"隐式全选"） |
| `regex` 未锚定 | 需显式 `anchored:false`，否则非法 |
| `minAgeDays` 缺失 | 默认 30；显式写 `< 7` 时要求 `risk != safe` |
| `onlyFiles:false` 且 `walk.mode == contents` | 允许，但目录级删除一律 `caution` 起步（整目录消失比一堆文件消失更可见） |
| `maxDepth` / 条目上限 | 超出即 `NotApplicable` + 诊断"范围过大，请缩小" |

## 9. 用户包（`origin: user`）的权限裁剪

| 能力 | builtin | user |
| --- | --- | --- |
| `select.literal` | ✗（内置必须可解释） | ✓（仍受 G0） |
| `select.scanSelection` | ✓ | ✓ |
| 声明 `risk` | 由代码侧评审登记 | 由**校验器推导**，用户自填的 risk 仅作下限提示，不能低于推导值 |
| `defaultSelected: true` | ✓ | 仅 `safe`/`caution` 允许，`danger` 强制 false |
| 关闭内置分组（`enabled:false`） | — | ✓（用户永远可以少删） |
| 改写内置分组的 rules | — | ✗（只能整体关闭，避免"改了个 glob 就变成危险规则却仍显示官方标签"） |
| 执行外部命令 | ✗ | ✗（走 S5 独立通道，见 04 文档 §5） |

## 10. 永远不做

注册表清理、启动项/服务调整、驱动更新、"隐私加速器"式批量 Cookie 清除、全盘按扩展名搜垃圾、
自动"深度清理"、静默提权、开机自启清理、跨机器/网络位置清理、修改 ACL、卸载程序（交给 `appwiz.cpl`，上游 `Actions.cpp:700-703`）。
