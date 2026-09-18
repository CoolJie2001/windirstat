# WDS Shell — WinDirStat 现代化 .NET 前端（Avalonia）

用 C# / Avalonia 重写的 WinDirStat 类磁盘可视化工具的外壳（Shell）脚手架。
核心战略：**扫描引擎跟随上游 [windirstat/windirstat](https://github.com/windirstat/windirstat) 持续演进，
本目录只维护 UI 与一层极薄的接口适配**（同步纪律见 [docs/UPSTREAM_SYNC.md](docs/UPSTREAM_SYNC.md)）。

## 目录结构

```
dotnet-shell/
├── WdsShell.slnx                  # .NET 10 解决方案（新 XML 格式）
├── src/
│   ├── WdsShell.Core/             # 领域层：无任何 UI / interop 依赖
│   │   ├── Models/DiskNode.cs     #   磁盘树（对应上游 CItem，但纯数据、无绘制）
│   │   ├── Engine/IDiskScanEngine.cs        # 引擎抽象（UI 与引擎的唯一接缝）
│   │   ├── Engine/ManagedWalkEngine.cs      # 托管递归扫描（占位引擎 / 跨平台回退）
│   │   ├── Layout/TreeMapLayout.cs          # squarified treemap 布局（纯函数）
│   │   └── Layout/ExtensionPalette.cs       # 扩展名配色 + 尺寸格式化
│   ├── WdsShell.Interop/          # wdscore.dll 的 C ABI 绑定层（骨架，待 native 落地）
│   │   ├── NativeMethods.cs       #   全部 P/Invoke 锁在这一个文件
│   │   └── NativeWdsEngine.cs     #   事件流 → DiskNode 树的翻译器（逻辑已完成）
│   └── WdsShell.App/              # Avalonia 应用（MVVM, CommunityToolkit.Mvvm）
│       ├── ViewModels/MainViewModel.cs      # 250ms 节流快照刷新
│       ├── Views/MainWindow.axaml
│       └── Controls/                        # TreemapControl / ExtensionBarControl 自绘
├── native/
│   ├── include/wds_core_api.h     # ★ core↔shell 的完整契约（C ABI，push 事件模型）
│   └── README.md                  # wdscore.dll 的构建规划与接缝补丁清单
└── docs/UPSTREAM_SYNC.md          # 上游同步操作手册
```

## 当前状态（脚手架可运行）

```cmd
cd dotnet-shell
dotnet build WdsShell.slnx
dotnet run --project src\WdsShell.App
```

- ✅ 选驱动器 → 递归扫描（托管引擎，真实文件系统），扫描期 treemap / 列表 / 扩展名条实时刷新
- ✅ 双击列表行或点击 treemap 目录块进入，工具栏"上一级"返回，面包屑导航
- ✅ `<Free Space>` 伪节点（灰色块）
- ⏳ `wdscore.dll`（NTFS MFT 秒扫，来自上游 C++ core）尚未构建；
  `NativeWdsEngine.IsAvailable()` 检测到 dll 后会自动接管，UI 层零改动

## 引擎接入路线（两步走）

1. **现在**：`ManagedWalkEngine`（纯 C#）让全部 UI 特性先行开发、验证交互模型；
2. **之后**：在 fork 的上游仓库里按 `native/README.md` 的接缝补丁裁剪出 `wdscore.dll`，
   导出 `native/include/wds_core_api.h` 定义的事件协议；把 dll 放进
   `WdsShell.App` 输出目录的 `native/x64/` 即完成切换（协议翻译逻辑在 `NativeWdsEngine` 中已就绪）。

## 设计红线（防耦合回潮）

- UI ↔ 引擎只经由 `IDiskScanEngine` + `DiskNode` 快照，**没有第二条数据通路**；
- 所有刷新走节流定时器，引擎事件绝不允许直接触达控件；
- 新功能的默认归属是 C# 侧；只有"扫描性能/正确性"类需求才允许进 native core；
- `wds_core_api.h` 只增不改（append-only），字段与枚举值永不重排。

## 许可

上游 WinDirStat 为 GPL-2.0-or-later；本 shell 与其链接的 wdscore.dll 构成结合作品，
同样必须以 GPL-2.0-or-later（或更高版本）分发。
