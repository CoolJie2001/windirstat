# 03 · 内置规则包

内置包 `wds.builtin` 是普通用户看到的全部东西。本文给出准入流程、首批分组与实测登记表。

## 1. 三条取舍

1. **宁缺毋滥**：一条规则若无法在受控环境实测并登记 `evidence`，就不进内置包。空列表比错列表好。
2. **普通用户一屏看懂**：简单模式首屏分组数 ≤ 5，每个分组一行标题 + 一行"删了会怎样"。超出的能力进高级模式或委托按钮。
3. **不背第三方清单**：不从 BleachBit / CCleaner 移植任何路径列表（许可与责任双重原因，见 README §许可）。内置规则全部由本项目自己实测登记。

## 2. 准入流程（每条内置规则都要走完）

```
① 提出：填 rule id + 中文标题 + 预期后果 + 依据来源（vendor-doc / os-documented / observed）
② 复现：在干净 VM（Win11 23H2 + 目标应用版本）里跑"仅预览"，记录命中数量与体积
③ 删除验证：应用重启后确认功能未受损（登录态、书签、离线包、已安装依赖未丢）
④ 登记：把结果写进 evidence.verifiedOnSystems；把快照存 docs/cleanup-rules/verified/<rule-id>.md
⑤ 评审：闸门是否够（年龄、processGuard、exclude 模板）、risk 定级是否诚实
⑥ 打包：随应用版本发布；任何路径/年龄变更递增 pack.version
```

未完成 ②③④ 的规则在加载期被强制 `enabled:false`，只在高级模式可见、不可勾选、不参与求值。

## 3. 首批分组目录

状态含义：**已准入** = 走完 §2；**待实测** = 设计就位但未走 VM 验证，代码里 `enabled:false`。

| # | Group id | 普通用户看到的标题 | selector / 根 | risk | 默认勾选 | 立即释放 | 年龄 | 状态 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `user.temp.files` | 临时文件 | `env:TEMP`，`walk.mode=contents`，`onlyFiles` | safe | ✓ | ✓（永久删需另开） | ≥30 天 | 待实测 |
| 2 | `browser.cache.chromium` | Chrome / Edge 网页缓存 | `knownFolder:LocalAppData` → `Google\Chrome\User Data\*\Cache`、`Microsoft\Edge\User Data\*\Cache`，`walk.mode=children` 于 `User Data` 的每个 profile | caution | ✓ | ✓ | ≥14 天 | 待实测 |
| 3 | `browser.cache.firefox` | Firefox 网页缓存 | `knownFolder:LocalAppData` → `Mozilla\Firefox\Profiles\*\cache2` | caution | ✓ | ✓ | ≥14 天 | 待实测 |
| 4 | `app.crash-reports` | 程序崩溃报告与错误日志 | `knownFolder:LocalAppData` → `Microsoft\Windows\WER\ReportQueue`、`ReportArchive` | caution | ✓ | ✓ | ≥30 天 | 待实测 |
| 5 | `explorer.thumbnail-cache` | 缩略图缓存 | `knownFolder:LocalAppData` → `Microsoft\Windows\Explorer\thumbcache_*.db` | caution | ✗（默认关，因为需 Explorer 空闲） | ✓ | 无（文件级） | 待实测 + `processGuard` |
| 6 | `dev.package-caches` | 开发工具缓存（NuGet HTTP cache、npm、pip、pnpm、Gradle） | `knownFolder:UserProfile` 下各自子路径 | caution | ✗ 整组默认关闭 | ✓ | ≥30 天 | 待实测；离线机器风险已在 02 §3 记录 |
| 7 | `recycle-bin` | 回收站 | **不是规则**：独立动作 + `SHEmptyRecycleBin`（对齐上游 `Actions.cpp:313`） | caution | ✗ | ✓ | — | 设计上已定，属 S4 |

首版上线建议：**1、2、3、4 四个分组 + 回收站独立动作 + §4 的委托按钮**。5/6 完成 §2 后再开。

### 强制排除模板（所有浏览器分组必须带）

```jsonc
"exclude": [
  { "name": "LOCK" }, { "name": "LOG" }, { "name": "*.log" },
  { "glob": "**/Login Data*" }, { "glob": "**/Cookies*" }, { "glob": "**/Local Storage/**" },
  { "glob": "**/Session Storage/**" }, { "glob": "**/Network/*" }, { "glob": "**/Preferences" },
  { "glob": "**/Bookmarks*" }, { "glob": "**/Passwords*" }, { "glob": "**/Extensions/**" },
  { "glob": "**/*History*" }
]
```

原因：`User Data` 之下混着"缓存"和"用户数据/凭据/扩展"，只用 include 白名单不够 —— 命中范围一旦写宽（`walk.mode=contents` 在 profile 根）就会连带删掉登录态与扩展。
更稳的写法是**只 allowlist 明确的缓存子目录**（`Cache`、`Code Cache`、`GPUCache`、`Service Worker\CacheStorage`），上面的 exclude 作为第二层。

## 4. 委托通道（不是规则，是按钮）

这些项**永远不进规则引擎**，因为它们的正确性由操作系统/微软负责（与上游选择完全一致）：

| 按钮 | 动作 | 上游对应 |
| --- | --- | --- |
| 打开系统磁盘清理 | `cleanmgr.exe` | `WinDirStatModel.Actions.cpp:691-693` |
| 打开存储感知设置 | `ms-settings:storagesense`（存在性探测：`HKCR\ms-settings`，`HelpersTasks.cpp:312-318`） | `:695-698` |
| 组件存储清理（DISM） | `dism.exe /Online /Cleanup-Image /StartComponentCleanup`（可选 `/ResetBase`、`/AnalyzeComponentStore`），需提权 | `windirstat.rc:159-164` + `commandFilter:107-109` |
| 应用和功能 | `appwiz.cpl` | `:700-703` |
| 影子副本存储 | 打开系统属性 / `vssadmin`，需提权，独立确认 | `ID_CLEANUP_REMOVE_SHADOW`（`commandFilter:124` 要求 `isElevated`） |

UI 上这一组放进"交给系统处理"卡片，明确写"这些由 Windows 自己决定能删什么，我们只帮你打开它"。

## 5. i18n 与文案规范

* key 命名：`cleanup.group.<id>` 标题、`cleanup.group.<id>.hint` 一行后果、`cleanup.rule.<id>.note` 高级模式说明、`cleanup.reason.<RejectReason>` 拒绝原因。
* 标题写"是什么"，不写"能省多少"：用「临时文件」而不是「释放 3 GB 空间」。
* 后果行必须包含"是否会丢东西"，例：`只删缓存，书签、密码、登录状态不受影响；下次打开这些网站会重新下载。`
* 上游语言文件为 `windirstat/lang_*.ts`（本项目 i18n 自管，不共用），新增分组时中英两份同步，缺译文视为构建警告。

## 6. 更新节奏与责任

| 事项 | 约定 |
| --- | --- |
| 内置包发布 | 跟随应用版本；不做在线订阅/热更清单（一旦有自动下发，误删责任落到"云端"，回滚与审计都难） |
| 出问题回滚 | 支持通过 `settings.ini` 的 `Cleanup.Group.<id>.Selected=false` 关掉单组；极端情况发版把该组 `enabled:false`（无需卸载） |
| 规则数量上限 | 内置包 ≤ 12 组 / ≤ 60 条规则；超出即评审"是否该走委托通道" |
| owner | 每条内置规则的 §2 登记表需要一个具名验证人（学 BleachBit 的 `@testedby` 字段） |
