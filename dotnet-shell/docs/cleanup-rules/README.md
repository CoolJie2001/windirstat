# 清理规则引擎（Cleanup Rule Engine）设计文档

> 状态：设计定稿待评审 · 日期 2026-09-19 · 影响范围：`WdsShell.Core` / `WdsShell.Interop` / `WdsShell.App`，**不涉及 `wds_core_api.h` ABI**

## 一句话定位

给用户一套"点一下就能安全腾出空间"的清理功能：内置规则包负责"什么是垃圾"，策略层负责"什么绝对不能删"，
用户只负责在最后确认那份**看得见的清单**。高级用户可以加规则，但**永远不能越过策略层**。

两个使用者画像决定了整个设计的形状：

| 画像 | 他能做什么 | 他看到什么 | 他能配置什么 |
| --- | --- | --- | --- |
| 普通用户（默认） | 勾选分组 → 预览 → 移到回收站 | 中文分组名 + "删了会怎样"一句话 + 合计可回收 | 只有勾选，看不到路径与规则 |
| 高级用户（设置里显式开启） | 编辑/新增自定义规则包 | 逐项路径、命中规则 id、拒绝原因、规则校验器 | `%APPDATA%\WdsShell\Cleanup\Rules\*.json` |

## 核心决策（ADR 摘要）

| # | 决策 | 理由 | 代价 |
| --- | --- | --- | --- |
| D1 | 系统级垃圾**委托** cleanmgr / Storage Sense / DISM，不自研 | 唯一有人长期为"删了不坏"背书的是操作系统厂商。上游 `WinDirStatModel.Actions.cpp:691-698` 正是这么做的 | 少删一部分系统项；UI 要能解释"这部分交给系统" |
| D2 | 规则是**受限声明式数据**（JSON），不是脚本/表达式 | AOT（`WdsShell.App.csproj` 里 `PublishAot=true`）无法加载脚本；更重要的是"能写代码"就等于把删库权限发给规则文件 | 表达力受限，个别怪需求做不了 → 走 D1 或高级用户的"外部命令"（单独危险通道，见 04 文档） |
| D3 | 内置规则**必须带 evidence 字段**才允许默认勾选 | BleachBit 的 `winrar.xml` 里有 `path="%ProgramFiles%\WinRAR\" regex="\.tmp$"` 这种"在 Program Files 按扩展名删"的定义，这就是误删事故的标准形状。无证据的规则一律 `enabled:false` 且不在简单模式出现 | 首批内置规则数量少；每条要人工实测登记 |
| D4 | 硬否决策略（deny roots）写在 **C# 代码里**，不在数据里 | 规则包可被编辑/替换；数据一旦能改安全边界，边界就不存在 | 新增例外要发版；可接受 |
| D5 | 默认只**移到回收站**，永久删除是另一个显式动作 | 与上游一致：`Del`=回收站、`Shift+Del`=永久（`windirstat.rc:343-344`），`FOFX_RECYCLEONDELETE` 仅在 `toTrashBin` 时加（`WinDirStatModel.cpp:611-612`） | 必须诚实告知"回收站不释放空间"，并单独提供"清空回收站" |
| D6 | 规则包与勾选态分离：规则=JSON 文件，勾选态=`settings.ini` | 沿用现有约定（`%APPDATA%\WdsShell\settings.ini`，key=value，见 `dotnet-shell/README.md` 设置一节） | 两套持久化；但各自职责单一 |
| D7 | JSON 一律走 **System.Text.Json 源生成**（`JsonSerializerContext`） | 反射序列化器在本项目是**静默**失效的（README 里设置模块已经为此放弃过反射 JSON） | 模型类必须保持可源生成（无 `object`、无非 sealed 多态） |
| D8 | 引擎保持只读；清理逻辑全部在 C# 侧 | 与既有边界一致：`native/README.md:27-29` 已把 `WinDirStatModel.Actions.cpp` 排除在 wdscore 外（理由写的就是"清理动作全部改在 C# shell 实现"），`wds_core_api.h:13-14` 要求"这里没写的能力应在 shell 侧(C#)实现" | 无 |

## 许可与外部代码

* 上游 WinDirStat = GPL-2.0-or-later；本 shell 同许可分发（见 `dotnet-shell/README.md` 许可一节）。
* **不引入**第三方清理器的代码与规则数据。已核查的候选：

| 项目 | 许可 | 活跃度（2026-09） | 结论 |
| --- | --- | --- | --- |
| BleachBit | GPL-3.0-or-later（`cleaners/winrar.xml` 文件头 + 仓库元数据） | 6.9k★，push 2026-09-17 | 只借鉴"声明式清理器 + 每项自带风险与 provenance"的**结构** |
| BCUninstaller | Apache-2.0，C# | 21.4k★，push 2026-09-07 | 定位是卸载残留，不是通用垃圾引擎；Apache-2.0 → GPL-2.0-only 不兼容，需走"or later 升级到 GPLv3"才可用，收益不值这个复杂度 |
| Czkawka | MIT（`README.md` L197-203），Rust | 33.5k★，push 2026-09-16 | 许可干净、可复用；但它只做"查找候选"（重复/空目录/大文件），不判定安全性 → 未来做"重复文件"面板时可参考 |
| Microsoft PowerToys | MIT | 活跃 | **没有**磁盘清理模块（官方模块清单，learn.microsoft.com/windows/powertoys/，ms.date 2026-09-14），无可引入对象 |

## 文档索引

| 文件 | 内容 |
| --- | --- |
| [01-rule-engine-design.md](01-rule-engine-design.md) | 数据模型、求值流水线、代码归属、版本化、测试策略（**主文档**） |
| [02-safety-policy.md](02-safety-policy.md) | 硬否决清单、五道闸门、风险分级语义、可回收空间口径、日志与故障处理 |
| [03-builtin-ruleset.md](03-builtin-ruleset.md) | 内置规则包目录、准入流程、每条规则的实测登记表 |
| [04-user-defined-rules.md](04-user-defined-rules.md) | 高级用户规则编辑、校验与错误信息规范、与上游 UDC 的关系 |
| [schema/cleanup-rulepack-v1.schema.json](schema/cleanup-rulepack-v1.schema.json) | 规则包 JSON Schema（编辑器补全与离线校验用） |

## 术语

* **RulePack** — 一份 JSON 文件，内含若干 Group。`origin` 取 `builtin` 或 `user`。
* **Group（清理项）** — UI 里那个复选框，是普通用户唯一要理解的对象。一个 Group 由若干 Rule 组成。
* **Rule（规则）** — 一个匹配单元：从哪个根出发、怎么走、命中什么、排除什么、要满足什么年龄。
* **Candidate** — 求值产出的单个文件/目录 + 元数据 + 命中的 ruleId + 判定结果。
* **Plan（预览计划）** — 一次求值冻结出来的不可变候选清单，带 hash 与逐项 stat 快照。执行只针对 Plan。
* **Journal** — 执行后的事实记录（做了什么、成败、错误码），不是"撤销数据库"。

## 里程碑

| 阶段 | 交付 | 验收标准 | 风险 |
| --- | --- | --- | --- |
| S0 | 清理区域骨架：`MainGrid` 加 Row + 折叠面板 + 工具栏入口；右键只读项（打开位置/复制路径/属性） | 构建 + 启动通过；不出现任何删除代码 | 零 |
| S1 | `Core/Cleanup` 纯逻辑：模型、求值、策略、Plan；合成文件系统全单测 | 单测覆盖 deny roots、年龄、reparse、TOCTOU drift；无真实 IO | 零（不碰磁盘） |
| S2 | `Interop/Cleanup` 只读侧：known folder、`GetTempPath`、物理尺寸核算、占用探测 | 在真机上跑"仅预览"，产出 Plan 并展示 | 零（只读） |
| S3 | 执行器：`IFileOperation` 回收站通道 + 确认框 + Journal | 只在一次性沙箱目录上验证；`%TEMP%` 真实清理需单独批准 | 中（首次可删） |
| S4 | 系统清理委托入口（cleanmgr / Storage Sense / DISM，提权闸门）+ 清空回收站 + 删除空目录 | 每项独立确认，照上游 `commandFilter` 的启用条件建模 | 中 |
| S5 | 用户自定义规则 + 校验器 + 导入导出 | 校验失败的规则包必须拒绝加载并给出可定位的错误 | 高（等同执行任意命令的通道，见 04） |

依赖：S2 起需要扫描引擎补 `mtime` / `attributes` 采集 —— `WdsShell.Core/Models/DiskNode.cs:18-43` 的 `DiskNode`
目前只有 Name/Kind/Parent/Extension/IsDirectory/IsReparsePoint，对该文件 grep `LastWrite|Attributes` 零命中（本就在排期内）。
在此之前，清理求值自带一次独立 walk，不依赖扫描结果树。
