# 上游同步手册（WinDirStat 官方仓库 → fork → wdscore.dll → shell）

> 目标：官方持续维护扫描核心，本 fork 的存量改动**永远只有那一小叠接缝补丁**，
> 同步动作可以机械化为"周检 + 重放"。

## 0. 仓库拓扑

```
github.com/windirstat/windirstat        （上游，只读 remote: upstream）
        │  git fetch upstream
        ▼
fork: windirstat fork 仓库
   ├── master        纯跟随上游，永不提交（git reset --hard upstream/master 即同步）
   └── wdscore       master + patch series（接缝补丁 S1..S3，见 ../native/README.md）
                     构建产物 wdscore.dll 发布为 Release 附件 / NuGet 包
        │
        ▼
本仓库 dotnet-shell/                     （UI 与产品功能，日常开发在这里）
```

## 1. 每周同步（约 30–60 分钟）

```cmd
:: fork 仓库
git fetch upstream
git checkout master && git merge --ff-only upstream/master
git checkout wdscore && git rebase master        :: 重放 patch series

:: 查看上游本周动了什么（决定是否需要人工跟进）
git log master@{1}..master --oneline --name-only -- windirstat/Item.* windirstat/Finder*.* windirstat/WinDirStatModel.cpp
```

**分流规则**：

| 上游改动落点 | 处理 |
|---|---|
| UiFramework / Controls / Views / Dialogs / Pages / MainFrame / Actions | 忽略——不参与 wdscore 构建，合并零冲突 |
| Item.* / Finder*.* / WinDirStatModel.cpp（引擎部分） | rebase 时若与 S1/S2 冲突：人工重放（通常只是行号漂移）；语义变化则检查 `wdscore/WdsCoreApi.cpp` 的事件发射点是否需要跟着调 |
| Options.* / CsvLoader / Filtering | 一般可直接吸收；涉及 S3 时重放 |
| 上游新增 core 级文件（如新 Finder） | 决策是否加入 wdscore.vcxproj 清单（显式 opt-in） |
| `MODEL_CHANGE` 枚举语义变化 | 只要 push 事件语义不变就无影响（事件协议是翻译层，天然隔离） |

## 2. 契约变更流程（唯一需要"设计"的场合）

新需求先问三个问题（顺序不可调换）：

1. 能在 C# shell 实现吗？ → 能：实现在 `WdsShell.*`，core 不动；
2. 能由**现有** `wds_core_api.h` 事件/调用推导出来吗？ → 能：shell 加适配器；
3. 确需 native 支持 → 在 `wds_core_api.h` **末尾**追加字段 / 新事件 kind / 新函数，
   次版本号 +1；shell 侧 `NativeMethods.cs` + `NativeWdsEngine` 同步适配。
   ——**永不**修改/删除/重排既有字段与枚举值。

值得做的额外动作：把 S1–S3 作为 PR 提交给官方（理由："可独立构建的扫描库，便于单测"）。
被合并则 patch series 长度归零，同步模型升级为"纯 UI fork"。

## 3. 大版本重构的应对（如上游 2026-08 的 MFC→Win32 迁移这种级别）

- 特征：`git log --stat` 出现全文件树移动/改名；
- 应对：不要 rebase 重放（会产生海啸级冲突）。改为在**新的 master 基线**上按
  `patches/README` 里逐条记录的"补丁意图"手工重写 S1–S3（总量 <300 行，一次 1–2 天）；
- 保险丝：patch series 的每个补丁必须在 `patches/README.md` 里有一句"意图描述"，
  保证可以脱离 diff 本身重建。

## 4. 发布对齐

- wdscore.dll 与上游 **release tag** 绑定（如 `v4.1.4-core.1`），不追 master 日更；
- shell 的 CI 固定引用某个 core 版本；升级 core 版本是一次显式的、可回滚的依赖 bump。

## 5. 许可证提醒

上游为 GPL-2.0-or-later。wdscore.dll + WdsShell.* 作为单一产品分发时整体受 GPL 约束：
必须开放全部源码（含 C# shell）并随附 LICENSE。若未来要闭源商用，唯一出路是完全不
复用上游代码（自行实现 MFT 引擎），届时重新评估。
