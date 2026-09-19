# 04 · 高级用户自定义规则

普通用户不需要读本文。目标是让"会写 JSON 的人"能扩展清理范围，同时**不给任何规则文件越过硬否决闸门的能力**。

## 1. 入口与开关

* 设置窗口（`src/WdsShell.App/Views/SettingsWindow.axaml` 的分页 `TabControl`）新增 **"清理"** 页，上游对应 `windirstat/Pages/PageCleanups.*`。
* 页内一个显式开关：`高级模式：允许自定义清理规则`（默认关）。开启后才出现"打开规则文件夹""校验规则""导入/导出"。
* 首次开启时展示一次风险说明（不是一揽子协议，而是一段具体承诺："自定义规则可以删到你自己的文件，我们仍会拦住系统关键位置，但其余后果由规则作者承担"），并写回 `settings.ini`。

## 2. 存放与格式

| 项 | 约定 |
| --- | --- |
| 目录 | `%APPDATA%\WdsShell\Cleanup\Rules\*.json` |
| 命名 | `user.<作者>.<主题>.json`；Group id 必须以 `user.` 开头，否则加载报错（避免冒充内置项） |
| 编码 | UTF-8；允许 `//` 注释与尾随逗号（`JsonReaderOptions.CommentHandling` / `AllowTrailingCommas`） |
| 大小 | 单包 ≤ 1 MB、组 ≤ 64、规则 ≤ 256（防止把求值拖死） |
| 加载时机 | 应用启动 + 该页"重新校验"按钮；**不监听文件变化**（避免编辑中途半加载） |
| 永不加载 | 从资源管理器"打开方式"、网络位置、临时目录拖入的路径 —— 只有落在上面那个目录里的文件才生效 |
| 冲突 | 用户包之间 group id 重复 → 两份都拒绝加载；与内置重复 → 只允许 `enabled:false` 形式的关闭（01 §4 第 1 步） |

## 3. 编辑体验：表单先行，JSON 兜底

1. **表单模式**（默认）：由算子元数据驱动生成 —— `OperatorRegistry` 每个算子带 `DisplayName / 参数说明 / 示例 / 风险注记`，UI 直接读它。这样"新增一个算子"只需要注册时就带上这些元数据，设置页自动出现该项，不需要改第二处代码。
2. **JSON 模式**：显示同一份数据的文本形态，保存时反序列化为模型（源生成），任何解析/校验错误以 **JSON pointer + 中文原因 + 建议**呈现。
3. 两种模式共享同一校验器，**不允许**只有一条路径有校验。

诊断样例（错误码稳定，便于文档检索）：

```
CLEANUP0012  groups[2].rules[0].select      拒绝：knownFolder "LocalAppData" 本身就是根，
                                             必须再往下至少一层（如 "Google\Chrome\User Data\Default\Cache"）
                                             依据：docs/cleanup-rules/02-safety-policy.md §8
CLEANUP0021  groups[0].rules[1].match[0]    拒绝：无根限定的扩展名匹配（extension ".tmp"）
                                             建议：给 walk.root 指定明确子目录后再按扩展名匹配
CLEANUP0031  groups[1].risk                 降级：自填 "safe" 被校验器推导为 "danger"，按 danger 处理
```

错误码前缀即稳定契约：`00xx` 结构/schema、`01xx` 算子、`02xx` 策略与形状、`03xx` 风险推导、`04xx` 求值期。

## 4. 试运行是强制的

保存新规则前必须跑一次"仅预览"（流水线 1~7 步，无任何写入）：

* 命中 0 项 → 提示"这条规则找不到任何文件，可能路径写错了"，允许保存但组上打 `未验证` 标记。
* 命中 > 10 万项 或 遍历超限 → 拒绝保存，提示缩小范围。
* 命中集合里出现任何被 G0 拒绝的根 → 直接展示"这条规则试图触达以下位置：…"，并要求显式确认改写（改不掉，只是让作者看见意图）。
* 通过后把这次预览的 `{命中数, 合计体积, 时间, 规则 hash}` 记为该规则的"最近一次预览"，展示在列表里。

## 5. 外部命令通道（对应上游 UDC，S5 才做）

上游的"用户自定义清理"本质是**执行命令行**：`USERDEFINEDCLEANUP.CommandLine` 经 `%COMSPEC% /C` 交给 `CreateProcess`
（`WinDirStatModel.cpp:791-819`），支持占位符 `%p`（路径）/`%n`（名字）/`%sp`/`%sn`，并且为了躲开文件名里的 `%`
先把占位符替换成含禁用字符 `>` 的中间形式再回填（`WinDirStatModel.cpp:821-835`）；递归时按深度优先、**自底向上**逐目录调用
（`:775-789`），且遍历同样受 `IsFollowingAllowed(reparseTag)` 支配（`:783`）。

本设计把声明式规则与命令通道**明确拆成两个东西**，因为责任模型完全不同：

| | 声明式规则（§2~§4） | 命令通道（本节） |
| --- | --- | --- |
| 能做什么 | 只能删匹配到的文件 | 任意代码执行 |
| 风险 | 受 G0~G4 全量支配 | G0 只能拦"我们传给它的目录"，命令内部行为不可控 |
| UI | 表单 + JSON | 纯文本框，每次执行都完整回显命令行 |
| 默认 | 高级模式可开 | 默认关闭 + `danger` + 需要输入确认词 |
| 字段 | — | 沿用上游语义：`Title` / `CommandLine` / `Enabled` / `WorksForDrives` / `WorksForDirectories` / `WorksForFiles` / `WorksForUncPaths` / `RecurseIntoSubdirectories` / `AskForConfirmation` / `ShowConsoleWindow` / `WaitForCompletion` / `RefreshPolicy`（`Options.h:99-129`） |
| 执行前置 | 同上 | 扫描必须已 settle（`Actions.cpp:727`）、路径必须仍存在（`:716-734`）、退出码与耗时进 Journal |
| 刷新 | Plan 收尾自动 | 由 `RefreshPolicy` 决定：不刷新 / 刷新该项 / 刷新父项（`WinDirStatModel.cpp:748-773`，枚举顺序照抄） |

> UNC 支持（`WorksForUncPaths`）在 shell 侧默认关闭：02 §2 R10 已把 UNC 列入硬否决，命令通道若要覆盖必须另开一个"我清楚这是网络位置"的独立开关。

## 6. 导入 / 导出与责任边界

* 导出：单个 group 或整包，JSON（含 `pack.origin = "user"`）。
* 导入：落到 Rules 目录后**仍走 §3 全套校验 + §4 试运行**，导入动作本身不授予任何权限提升。
* 不做：签名信任、云端订阅、内置"社区规则包"。一旦提供"别人写好的清单"，产品责任就回到本项目身上，
  而清单的维护成本（几千个应用 × 版本漂移）是不可承受的 —— 这正是 02 §3 那张反模式表存在的理由。

## 7. 与引擎/UI 的接缝

* 清理子系统**不改** `wds_core_api.h`：它只需要"读取文件系统事实"和"删除"，两者都在 C# 侧
  （`native/README.md:27-29` 排除 `WinDirStatModel.Actions.cpp` 的理由就是"清理动作全部改在 C# shell 实现"；
  `wds_core_api.h:13-14` 写明"这里没写的能力应在 shell 侧(C#)实现"）。
* 唯一与扫描树的耦合是收尾刷新：删除完成后把受影响子树交给 `IDiskScanEngine` 的现有刷新路径（对齐上游 `RefreshItem`，`WinDirStatModel.cpp:648-658`），
  UI 通过既有 250 ms 节流快照刷新，绝不允许删除线程直接触碰控件（README 设计红线）。
* 主窗挂点：`Views/MainWindow.axaml` 的 `MainGrid`（当前 `ColumnDefinitions="620,Auto,*"`）增加 Row1 放折叠面板，工具栏 `StackPanel` 加切换按钮；
  不改列结构，避免与 code-behind 写死的左列宽（`MainWindow.axaml.cs:92-93`）和"占比"列模板（`:120,206-214`）冲突。
