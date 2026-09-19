# DiskScope — Windows Disk Space Analyzer

DiskScope 是一个用 C# / Avalonia 构建的 Windows 磁盘空间分析器，提供目录树、区块图可视化和清理候选分析。
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
│   │   ├── Engine/ScanOptions.cs            # 扫描排除项（字段与默认值对齐上游 Options.h）
│   │   ├── Engine/ManagedWalkEngine.cs      # 托管递归扫描（占位引擎 / 跨平台回退）
│   │   ├── Layout/TreeMapLayout.cs          # squarified treemap 布局（纯函数）
│   │   └── Layout/ExtensionPalette.cs       # 扩展名配色 + 尺寸格式化
│   ├── WdsShell.Interop/          # wdscore.dll 的 C ABI 绑定层（骨架，待 native 落地）
│   │   ├── NativeMethods.cs       #   全部 P/Invoke 锁在这一个文件
│   │   └── NativeWdsEngine.cs     #   事件流 → DiskNode 树的翻译器（逻辑已完成）
│   └── WdsShell.App/              # Avalonia 应用（MVVM, CommunityToolkit.Mvvm）
│       ├── ViewModels/MainViewModel.cs      # 250ms 节流快照刷新 + 目录树行管理
│       ├── ViewModels/RowModels.cs          # TreeNodeRow：一个 DiskNode 对应一个行实例，懒展开
│       ├── Services/AppSettings.cs          # 设置单例 + key=value 落盘（%APPDATA%\DiskScope\settings.ini）
│       ├── Views/MainWindow.axaml           # 左：TreeDataGrid 目录树 / 右：区块图
│       ├── Views/SettingsWindow.axaml       # 工具栏"⚙ 设置…"打开的分页设置表
│       └── Controls/                        # TreemapControl / ExtensionBarControl / TreeProportionBar 自绘
├── native/
│   ├── include/wds_core_api.h     # ★ core↔shell 的完整契约（C ABI，push 事件模型）
│   └── README.md                  # wdscore.dll 的构建规划与接缝补丁清单
└── docs/
    ├── UPSTREAM_SYNC.md           # 上游同步操作手册
    └── cleanup-rules/             # 清理规则引擎设计（内置规则包 + 硬否决闸门 + 用户自定义规则）
```

## 依赖约束：TreeDataGrid 必须锁在 11.1.1

`Avalonia.Controls.TreeDataGrid` 自 **11.2.0** 起转为商业授权，构建期强制校验
`<AvaloniaUILicenseKey />`，没有密钥就直接失败（错误码 `AVLIC0001`）。
**11.1.1 是最后一个免密钥版本**，与 Avalonia 11.3.x 运行时兼容，故 `WdsShell.App.csproj` 钉死在该版本。
日后若拿到社区/商业密钥，把版本号改回 11.3.x 即可，代码无需改动。
11.1.1 的两个已知缺口会影响实现选择：没有 `ItemsSource`（列与 `Source` 只能在 code-behind 建）、
没有公开的 `ScrollIntoView`（见下方 ⏳）。

## 设置：有哪些、存哪里、材质是怎么验的

工具栏"⚙ 设置…"打开分页设置表（外观 / 目录树 / 区块图 / 扫描 / 系统集成），对应上游
`CSettingsSheet`（`windirstat/Pages/`，8 页）。这里只做基础子集，页名与项名尽量对齐上游，
每项在界面上都注明了它对应上游的哪个 setting。

- **存储**：`%APPDATA%\DiskScope\settings.ini`，`key=value` 纯 ASCII 文本，键名对齐上游
  `HKCU\Software\WinDirStat\WinDirStat\Options`。首次启动新版本时会兼容读取旧的
  `%APPDATA%\WdsShell\settings.ini`。刻意不用反射式 JSON：项目开了 `PublishAot`，
  反射序列化器在裁剪后拿不到属性，会**静默**丢设置。上游另有便携模式（exe 旁 `WinDirStat.ini`），
  本 shell 暂未实现。
- **生效时机**：外观 / 列可见性 / 面板宽度 / 区块图钳制 = 即时；扫描排除项与 `<Free Space>`
  = 下次扫描生效（扫描中途改设置不影响正在跑的这趟）。
- **与上游的取舍差异**：上游是 OK / Cancel / Apply 三键的模态属性表、点 Apply 才落盘；
  这里改成实时应用 + 关闭即存。上游还有 过滤 / 权限配色 / 清理命令 / 提示 / 语言 五页，未做。

窗口材质一项是 shell 自有（上游是 Win32 原生标题栏，没有这一层），实测结论记在这里免得再踩：

- Avalonia 11.3.5 的 Win32 后端两条路都**不是** Win11 的官方入口：Mica 走未公开的
  `DWMWA_MICA_EFFECT`(1029)，AcrylicBlur 走 Win10 的 accent policy
  （反查 `Avalonia.Win32.dll` 符号可证：`SetTransparencyAcrylicBlur` / `AccentPolicy` /
  `WCA_ACCENT_POLICY` / `ACCENT_ENABLE_ACRYLICBLURBEHIND`）。它从不碰公开的
  `DWMWA_SYSTEMBACKDROP_TYPE`(38)，所以 `DwmGetWindowAttribute(38)` 恒读回 `0`（AUTO）——
  这条只能证明"没走官方入口"，**不能**当作"没材质"的证据，1029 又读不回来（`E_INVALIDARG`）。
- 本项目在 `Services/SystemBackdrop.cs` 里自己补设 38：Win11 22621+ 按设置写
  `DWMSBT_MAINWINDOW`(2)=Mica / `DWMSBT_TRANSIENTWINDOW`(3)=Desktop Acrylic（文档称"最亮档"）/
  `DWMSBT_NONE`(1)，Avalonia 的 hint 退为旧系统上的兜底。判据变成读回值：`CurrentType()==3` 即已接管。
- 材质是**逐窗口**的属性，绑到设置单例不会自己装到新窗口上：主窗和设置窗口都走
  `SystemBackdrop.ApplyTo(window, mode)`（构造期没 HWND，`Opened` 之后还要再走一次），
  且三档都要下发 —— 切回"无"时只清 hint 会把 DWM 上已装的亚克力留在窗上（实测读回仍是 3）。
  探针实测两窗一致：Mica 2/2、Acrylic 3/3、无 1/1。
- **量的判据用离屏 alpha 图，不用截图**：`PrintWindow(2)` 拿不到 DWM 合成结果，屏幕截图又要动前台窗口。
  把 `Window.Content` 渲进 `RenderTargetBitmap` 读每像素 alpha —— 离屏图里没有材质，
  alpha 就是"这一像素把自己的底色挡掉多少"。改之前（工具栏 `#80FFFFFF`、卡片 `#B3FFFFFF`、
  区块图底色不透明 `#EDEDF0`）整窗平均挡掉 76.4%、36.9% 的像素完全不透明，区块图区域 100% 焊死；
  换成现在这套预算（工具栏 25% / 卡片 40% / 区块图底色 40%）后整窗 46.2%、全不透明像素 0.5%。
  注意 alpha 是逐层相乘的，铺满整块的层最致命；卡片感靠描边+圆角，不靠底色浓度。
- 语义画刷定义在 `App.axaml` 的 `ThemeDictionaries`（Light/Dark 各一套），
  不引用 FluentTheme 里的 WinUI 资源键：Avalonia 并不保证 `LayerFillColorDefault` 这类键存在，
  `DynamicResource` 取不到时只会静默得到 null（画刷变透明），出问题没有任何提示。
- 有数据时右半屏仍是几乎不透明的色块（上游也一样：区块图的颜色就是数据本身），
  能透材质的是工具栏、状态栏、目录树与卡片四周的留白。

## 当前状态（脚手架可运行）

```cmd
cd dotnet-shell
dotnet build WdsShell.slnx
dotnet run --project src\WdsShell.App
```

发布版首次启动时会为当前 Windows 用户注册“使用 DiskScope 分析空间”文件夹右键菜单。
右键点击文件夹后，应用会打开并直接扫描该文件夹，同时识别 0 字节文件和典型垃圾扩展名。
Windows 10 直接显示在文件夹菜单中；Windows 11 的传统 Shell 菜单通常位于“显示更多选项”中。
也可以手动注册或移除菜单：

```powershell
powershell -ExecutionPolicy Bypass -File tools\install-context-menu.ps1 `
  -ExecutablePath "C:\Path\To\DiskScope.exe"
powershell -ExecutionPolicy Bypass -File tools\install-context-menu.ps1 -Uninstall
```

命令行也支持直接指定扫描目录：

```cmd
DiskScope.exe --scan-folder "D:\Projects\My Folder"
```

- ✅ 选驱动器 → 递归扫描（托管引擎，真实文件系统），扫描期区块图 / 目录树 / 扩展名条实时刷新
- ✅ 左侧是**真目录树**（`Avalonia.Controls.TreeDataGrid`）：根恒为扫描根并默认展开，
  子级按需懒展开，行对象按 `DiskNode` 身份全局复用，所以扫描期刷新不会把展开/选中状态刷没
- ✅ 列顺序与宽度照抄上游 `CFileTreeView::InitializeColumns` 的 96dpi 基准值：名称 250 /
  占比 105+30 / 百分比·大小（物理）·大小（逻辑）·文件·文件夹 各 90；面板不够宽就出横向滚动条
  （上游同理）。默认显隐照抄上游 `FileTreeColumnVisibility`（名称/占比/百分比/物理/逻辑/文件 显示，
  项目数/文件夹/时间/属性/所有者 隐藏），可选列在设置里开关。
  "占比"列是自绘 `TreeProportionBar`，几何照抄上游 `Item.Extended.cpp`
- ✅ 设置窗口：分页（外观 / 目录树 / 区块图 / 扫描）+ 实时生效 + 关闭落盘，
  见上一节。自动化回归实测：`TabItem` 走 `SelectionItemPattern.Select()`、复选框走
  `TogglePattern`、窗口走 `WindowPattern.Close()` 都可用，勾掉"文件夹"列会重建
  `TreeDataGrid.Source` 且进程存活（结果直接读 `settings.ini` 断言）。
  ⚠ 两处 UIA 拿不到，别指望它们：**ComboBox**（`ExpandCollapse` 展开后枚举不到 `ListItem`，
  `Current.Name` 恒空，读不回也选不中）和 **TreeDataGrid 的列头**（`HeaderItem`/`Header` 均返回空）。
  下拉这条链路改用"恢复默认"按钮间接验证（它走同一个 `Select(combo, key)`）。
  树行同样不进 UIA —— 验证"区块图 ↔ 目录树"双向选中用的是**进程内自检**：临时往 VM 塞一个假引擎
  （合成目录树），程序化改 `SelectedNode`，把 `Reveal` 命中的行、`Rows.Count`、`Scroll.Offset`
  落到日志文件再断言，窗口不必被人点。
- ✅ 单击区块图或树行 = 更新选中项，两侧共享同一个 `SelectedNode` 互相同步高亮；树行对应瓦片未进入当前布局时，区块图会自动切到该目录完成定位；
  双击 = **zoom**（切换基准子树，目标即点到的那块，文件取其父目录），工具栏"上一级"= zoom out。
  要点：11.1.1 的表格**不会**把模型行的 `IsExpanded` 变化同步进行索引（实测设完 true 后 `Rows.Count`
  纹丝不动），所以 VM 的 `Reveal` 只负责物化行，真正的展开由视图自顶向下逐级调公开的
  `HierarchicalTreeDataGridSource.Expand(IndexPath)`。少了这一步，深层行压根不在索引里，
  `RowSelection.Select(path)` 就静默落空 —— 表现正是"点区块图选不中左边的树"
- ✅ 选中项的路径回显 + 选中态强调。路径分两处：**状态栏**按上游 `CMainWindow::UpdatePaneText` 的
  `PaneId{Idle,Size,Ram}` 三格扩成四格 —— 第 1 格"悬停项优先、否则单选选中项"的完整路径（上游就是这条分支顺序），
  第 2 格 `物理大小: Σ …`（上游 `IDS_PANE_SIZE` 最小宽 175px，这里定宽 190 防抖动），后两格空闲提示 / 扫描状态；
  区块图卡片左上角再叠一枚**浮层胶囊**（标题 + 中段省略的完整路径），扫到深层时状态栏那一格会截断，靠它兜底。
  中段省略按整段切，绝不切半截目录名（实测 117 字符 → 69 字符，两头都是完整段名）。
  选中态做成**外发光**：白芯 + 黑底环定边界，5 圈递减透明度的主题色描边摊成光晕（最外 12px），块内压一层
  44/255 白罩当"被点亮"。上游对应物是 `CTreeMapView::RenderHighlightRectangle` 的三重同色描边
  （色值可配：`Options.h:247 TreeMapHighlightColor`），其中 **<7×7px 的块整块填高亮色**这条照抄。
  浅色主题高亮色取 `#0F6CBD`、深色取 `#63B9FF`（白光晕铺在白瓷砖上会糊成一片）；悬停共用同一套描边但降一档强度、
  不加光晕，避免扫鼠标时满图冒光。
  离屏位图量化：选中块外缘 1px 平均通道差 383、12px 处 4.7、13px 起归 0，距边界 >22px 的变化点为 0（不得波及邻块）；
  仅悬停的变化点 500 ≪ 选中态 16510；真实布局下 156×308 大块重绘 43889 点、18×4 细条重绘 76 点（整块填色生效）。
  ⚠ 真实指针悬停仍未验 —— 合成点击/移动在本机被拦，这一段只能手测。
- ✅ `<Free Space>` 伪节点（灰色块）
- ⏳ 区块图仍受 `TreemapMaxDepth` / `TreemapChildrenCap` 限制（默认 3 / 24，已进设置面板可调），
  未做到上游那样一次性递归到全部文件叶子
- ⏳ 树侧已知偏差（相对上游，均因 TreeDataGrid 11.1.1 能力或当前取舍）：只支持单选（上游支持
  Ctrl/Shift 多选）；从区块图定位到树里的深层项时会展开祖先 + 选中 + 滚到可见，但 11.1.1 没有
  公开的 `ScrollIntoView`，偏移是按"扁平行号 × 平均行高（extent/行数）"估的，行高不均时会有偏差；
  表头点击排序关闭；无网格线；双击文件不启动外部程序；
  收起的分支其行对象仍驻留内存（上游会删除）
- ⏳ 缺 项目数(ITEMS) / Last Change / Attributes / Owner 四列：需要托管引擎补采集，并给
  `wds_core_api.h` 追加字段
- ⏳ 扫描排除项目前只有 `ManagedWalkEngine` 读取；native core 落地时要往
  `wds_core_api.h` 追加一份扫描选项（append-only），`NativeWdsEngine` 目前忽略这些选项
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
