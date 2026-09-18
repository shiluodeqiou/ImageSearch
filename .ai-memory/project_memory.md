# 项目记忆 — 以图搜图（ImageSearch）

> 最后更新：2026-09-18（代码审查会话）
> 本文件承载项目级规则、约束与教训（L3）。当日流水见 `.ai-memory/{YYYYMMDD}/`。

## 项目概况

- WPF 桌面工具（C#，`net10.0-windows7.0`，x64），本地以图搜图：Everything/目录枚举 → SkiaSharp/WIC 解码 → 感知哈希（DctHash32 / DctHash64 / DifferenceHash256）→ 索引落 `index.json`；视频先抽代表帧再走同一套哈希。
- 组成：`以图搜图/`（主程序，含 `WebAPI/` ASP.NET Core 内嵌服务）、`Straper/`（独立的 EXIF 清除工具，与原程序无耦合）。
- 关键外部依赖：`Everything64.dll`（可选，加速枚举）、`tools/ffmpeg.exe`（LGPL 构建，视频抽帧）、SkiaSharp、Masuit.Tools、CommunityToolkit.Mvvm、Scalar/Swashbuckle。

## Project Convention（审查中发现并确认的既有规范）

### C1. 扩展名清单只写一份，其余全部派生
`Models/MediaFormats.cs` 的 `ExtensionsList` 是唯一真相源，`Extensions` / `RegexAlternation` / `EverythingFilter` / `DialogFilter` 全部由它派生。历史上"正则里有、Everything 过滤串里没有"导致 webp 被静默跳过，故新增格式只改一处。

### C2. 注释解释「为什么」，并附实测数据
本项目的注释密度与深度是刻意的：凡是反直觉的实现（如 `UpdateIndexOnHDD` 的 default 分支与 case 1 合并、`VideoThumbnailCache.Extract` 搬移后不再回头校验目标）都在注释里写明了触发过的故障与实测数字。修改这些代码前必须先读注释，否则极易"好心改回旧 bug"。

### C3. 面向用户的结果走状态栏，只有「需要用户介入」才弹模态框
`MainViewModel.NotifyStatus`（写 `StatusMessage`）是非阻塞结果通道；弹窗只保留给必须让用户注意的错误。自动同步（`automatic: true`）全程不许弹窗。

### C4. 数据文件一律新增、不改造既有文件
界面偏好写 `ui_preferences.json`、索引源写 `index_sources.json`，都不写 `config.ini`——因为 `IniFile.Save()` 会抹掉 config.ini 里的全部中文注释。

### C5. 破坏性操作的三道闸
无效索引清理：只删「明确确认不存在」的条目 → 按比例 `WarnRatio(5%)`/`BlockRatio(20%)` 分级 → `Block` 时必须勾选确认框才可执行。

## Decision Record

### D1. 索引文件写入用「临时文件 + File.Replace 原子替换 + 三层备份链」
- **决策**：`temp → File.Replace(index.json, index.json.bak)`，启动时另留 `backups/index_yyyyMMdd.json`；加载按「主文件 → .bak → 最新快照」回退。
- **理由**：旧实现在原文件上 `Seek(0)` 覆盖写，中断即留下残缺库；且写入方 Seek 会把读取方的位置一起挪走，已实测复现"文件被写成无法解析的垃圾"。
- **代价**：每次写盘多一次改名；每日首启多一次整文件复制（1.41GB 索引下代价显著，见 K7）。
- **撤销条件**：若改为增量/分片索引格式，本决策整体作废。

### D2. 备份校验只做结构检查（首字符 `[`、尾字符 `]`），不反序列化
- **理由**：备份校验每次启动都要跑，494 万条 / 1.41GB 完整解析约 8.8 秒、峰值 2GB；启动会解析 3~4 遍。
- **代价**：识别不了「结构完整但内容错乱」的损坏。
- **撤销条件**：索引改为可流式校验的格式，或引入校验和字段。

### D3. 解码走「SkiaSharp 主 + WIC 兜底」，媒体类型按文件头嗅探
- **理由**：SkiaSharp 不支持 HEIC，而微信/iPhone 导出的 `.jpg` 里常是 HEIC；反向也有 `.mts`(TypeScript)/`.hdr`(C 头文件)/`.nef`(NoteExpress) 被扩展名误纳的情况。
- **代价**：视频扩展名与解码失败的图片各多一次文件头读取（已刻意限制为只在这两类路径上做）。
- **撤销条件**：SkiaSharp 支持 HEIC，或改为按解码结果直接分类。

### D4. 视频抽帧「ffmpeg 主 + Shell 缩略图兜底」，Shell 路径强制单线程 STA 串行
- **理由**：ffmpeg 独立进程天然隔离坏文件、可并行；Shell 缩略图提供程序非线程安全，实测并发调用触发 0xC0000005 崩溃。
- **代价**：Shell 兜底吞吐受限（单 worker），故只作兜底。
- **撤销条件**：ffmpeg 改为强制依赖（不再支持无 ffmpeg 运行）。

## Known Issues（2026-09-18 审查发现，**均未修复**）

> 分级：致命 / 严重 / 警告 / 建议。修复前请先读本节结论对应的源码注释（很多"看起来是 bug"的地方是有意为之，见 Convention C2）。

### K1【严重】WebAPI `PATCH /index` 在特定分支弹模态框 → UI 线程阻塞 + HTTP 请求永久挂起
- 位置：`ViewModels/MainViewModel.cs:924`（`EnqueueAndRunAsync`）→ `:711`（`StartQueue`）→ `:744`（`RunQueueAsync(automatic: false)`）→ `:767 / :788 / :806` 三处 `MessageBox.Show`；调用方 `WebAPI/Controllers/HomeController.cs:49`。
- 触发：重复 `PATCH /index?dir=<已完全索引的目录>`（`runnable.Count == 0`），或程序刚启动、索引尚未载入完成时调用。
- 根因：`StartQueueCore` 抑制弹窗只有 `automatic` 一个开关；`AddDirectories(interactive: false)` 只覆盖入队阶段（其注释 :569-572 已声明"弹窗会让 HTTP 请求一直挂到用户点掉对话框为止"，但该保证未延伸到队列启动阶段）。
- 修复方向：给 `RunQueueAsync`/`StartQueueCore` 增加独立的「是否允许弹窗」参数，非交互路径一律走 `NotifyStatus`。

### K2【严重】索引落盘失败被吞，队列仍报「成功 + 新增 N 条」
- 位置：`Services/ImageIndexService.cs:752-755`（`catch (Exception ex) { LogManager.Error(ex); }`）、`:614-619`（`FlushAsync` 不返回结果）、`Services/IndexQueueRunner.cs:171-172`。
- 触发：磁盘满 / `index.json` 被占用 / 权限变化。
- 后果：`FlushAsync` 正常返回 → 队列汇总取内存计数 → 用户以为已保存，重启后新增条目全部丢失。日志有痕但界面无提示。
- 修复方向：`WriteIndexAsync` 返回成功与否；`FlushAsync` 把结果上抛；队列失败时写入状态栏与 `FailedDirectories`。

### K3【严重】HTTP 服务启动失败（端口占用）静默，界面仍显示服务运行中
- 位置：`App.xaml.cs:66`（`WebApiStartup.Run(e.Args);` fire-and-forget）、`WebAPI/WebApiStartup.cs:68`（`ServerRunning = true` 早于 `RunAsync`）。
- 修复方向：改为 `app.Start()` 后再置位，或 `try/catch` 首个失败并把 `ServerRunning=false` + 状态栏提示 + 日志。

### K4【严重】搜索收尾竞态：重入守卫在 800ms 延迟前放开 + `HandleDrop` 无条件清 UI
- 位置：`ViewModels/MainViewModel.cs:1535-1543`（`Interlocked.Exchange(_searchRunning, 0)` 早于 `await Task.Delay(800)`）、`:1400-1405`（`HandleDrop` 的 finally 无条件复位 `IsSearching`/loading/状态文字）。
- 触发：两次搜索在 800ms 内重叠（Ctrl+V 连按、Ctrl+V 后立刻拖放）；或搜索进行中拖入文件/文件夹。
- 后果：第二次搜索全程无加载反馈、状态文字被清空；in-flight 搜索的 loading 被抹掉、按钮提前可用。

### K5【警告】`AddDirectories` 缺 `IsQueueRunning` 守卫
- 位置：`ViewModels/MainViewModel.cs:573`（对比同类入口 `:542 / :668 / :687` 都有守卫）；绕过点 `:1247`（窗口拖放）、`:926`（WebAPI）。
- 后果：队列运行中拖入目录会被写入队列文件但不参与本轮；若拖入的是队列中某目录的父目录，子条目被 `IndexQueue.Remove` 摘掉而 Runner 仍继续处理，完成标记落在已脱离队列的实例上，`SaveQueue()` 又将其丢弃 → 界面与实际不一致。

### K6【警告】DCT 候选桶缓存按「条目数相等」判新鲜度 → 先删后增净变化为 0 时永不失效
- 位置：`Services/ImageSearchService.cs:457-474`（`_candidateIndexSourceCount == index.Count`）。
- 触发：「候选桶加速」开启 + 算法不含 DifferenceHash；搜索 → 删除一条结果 → 索引新文件使总数回到原值 → 后续搜索沿用旧桶，新文件永远进不了候选集（静默漏检）。
- 修复方向：改为按 `ImageIndexService` 的版本号（增删均自增）失效。

### K7【警告】每日快照内容未变也照抄一份 + 盘根归一化丢了尾部分隔符
- 快照：`Services/IndexBackup.cs:48-94` 只按"当天是否已有快照"判断，不比较内容。实测 `bin/Release/net10.0-windows7.0/backups/` 下两份 `index_2026*.json` 大小完全一致（1,517,887,036 字节）、mtime 同为 Sep 16 15:37，占 2.9GB；且每天首启要整份 `File.Copy` 1.41GB。
- 盘根：`Helpers/DirectoryTree.cs:12-15` 的 `Normalize` 去掉尾分隔符，实测 `index_sources.json` 里出现 `"F:"`/`"G:"`/`"H:"`。`"F:"` 在 Windows 语义上是「该盘的当前目录」而非盘根，从带 `=F:` 环境的 shell 启动时会静默缩小枚举范围。

### K8【警告】`_updateIndexTimer` 回调内的 `Dispatcher.Invoke` 无异常保护
- 位置：`ViewModels/MainViewModel.cs:314-327`（对比 `:1887-1913` 的 `UpdatePerformanceMetrics` 全包 try/catch）。关闭窗口瞬间触发 → 线程池线程未处理异常 → 进程崩溃（非干净退出）。

### K9【建议】UI 层
- `Converters/ImagePathToBitmapConverter.cs:48` 只设 `DecodePixelWidth=800`，长截图按比例放大可达数十 MB/张，且每次换选重新解码、在 UI 线程同步完成（绑定点 `MainWindow.xaml:1478 / :1524`）。
- `MainWindow.xaml:1171-1186`：「⚡ 候选桶加速」复选框与「⏱️ 耗时」面板共用同一 `*` 列（列定义 `:1092`），窄窗口下后者压住前者并截获点击。
- `WebAPI/Controllers/HomeController.cs:1652`（`MainViewModel.cs:1652`）`ErrorsDialog` 的内容无长度上限，失败条目上万时构造数 MB 字符串交给 `TextWrapping="Wrap"` 的 TextBox。
- `RemoveInvalidIndexDialog.xaml:139-153` 两个按钮缺 `IsCancel`/`IsDefault`（`ErrorsDialog.xaml:29` 有 → 不一致）。
- `App.xaml.cs:17-19` 的 `#if DEBUG` 早返回在异常处理器注册（`:73-92`）之前 → Debug 构建下未处理异常直接终止进程且无日志。
- `ViewModels/MainViewModel.cs:948 / :814` 的 `IsCheckingInvalidIndex`/`IsQueueRunning` 在 `try` 之外置位，其后至 `try` 之间若抛异常则标志永久为 true（按钮永久禁用，需重启）。
- 死代码（可证无引用）：`Helpers/PathPrefixFinder.cs`（221 行）、`MainWindow.xaml:47-48` 的 `ProgressWidthConverter`/`SpeedHistoryToPathConverter` 与两个实现类、`VideoThumbnailCache.Clear()`、`InvalidIndexScanner.Verify(string)`、`MainWindow.xaml:692` 的 `x:Name="AlwaysOnTopButton"`；`EverythingFileIndex.MinimumPathsForWorth = int.MaxValue` 使 `InvalidIndexScanner` 的 Everything 预筛分支整体不可达（注释承认"保留但不启用"）。
- 构建噪声：`GenerateDocumentationFile=True` → 全量重建 216 警告中约 210 条是 CS1591；真实告警（3 处 CS8618：`SearchResult.匹配算法`、`IndexItem.DifferenceHash`、`IndexProgressEventArgs.Filename`）被淹没。
- `Services/ImageIndexService.cs:53-66` 的静态构造在 UI 线程上做 WMI 遍历（每盘一次 `GetRelated`），启动卡顿风险未实测。

## Glossary

- **索引源 / 队列项**：`IndexSourceItem`，一个待索引目录及其状态（Pending/Running/Completed/Failed）与两个完成标记 `ImagesIndexed` / `VideosIndexed`（视频与图片分开记录，避免"上次没勾索引视频→视频永远补不上"）。
- **快速同步模式**：`IncludeVideosInIndex = false` 时的队列行为——跳过抽帧、且重新扫描全部目录以同步图片变化。
- **候选桶加速**：`HashCandidateIndex`，按 DCT 哈希的 8 位窗口分桶做近似剪枝，默认关闭，会漏掉阈值边缘的少量真命中。
- **代表帧 / 缩略图**：视频抽取的那一帧（PNG，最长边 160），索引与预览共用同一份缓存（`thumbnails/`）。
- **invalid index / 无效索引**：索引里指向已不存在文件的条目；清理走 `InvalidIndexScanner`。
