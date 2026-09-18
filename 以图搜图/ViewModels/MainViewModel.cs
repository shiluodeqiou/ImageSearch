using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Masuit.Tools.Files;
using Masuit.Tools.Files.FileDetector;
using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using Masuit.Tools.Systems;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using 以图搜图.Helpers;
using 以图搜图.Models;
using 以图搜图.Services;
using 以图搜图.WebAPI;
using 以图搜图.WebAPI.Controllers;
using ModelsMatchAlgorithm = 以图搜图.Models.MatchAlgorithm;
using Timer = System.Timers.Timer;

namespace 以图搜图.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ImageIndexService _indexService;
    private readonly ImageSearchService _searchService;
    private bool _disposed;

    [ObservableProperty]
    private string destImageInfo = string.Empty;

    [ObservableProperty]
    private string destImagePath = string.Empty;

    [ObservableProperty]
    private string elapsedTime = string.Empty;

    [ObservableProperty]
    private bool findFlipped;

    [ObservableProperty]
    private bool findRotated = true;

    [ObservableProperty]
    private string imagePath = string.Empty;

    [ObservableProperty]
    private string indexCount = "正在加载索引...";

    [ObservableProperty]
    private string indexSpeed = string.Empty;

    [ObservableProperty]
    private string processStatus = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SearchResult> searchResults = new();

    [ObservableProperty]
    private SearchResult? selectedResult;

    [ObservableProperty]
    private Visibility showRemoveInvalidIndex = Visibility.Collapsed;

    [ObservableProperty]
    private bool isCheckingInvalidIndex;

    /// <summary>
    /// 「停止检查」按钮的可见性：仅检查进行中显示。
    /// 由 <see cref="IsCheckingInvalidIndex"/> 派生，避免界面两处状态不一致。
    /// </summary>
    public Visibility CheckingInvalidIndexVisibility =>
        IsCheckingInvalidIndex ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsCheckingInvalidIndexChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckingInvalidIndexVisibility));
    }

    /// <summary>
    /// 清理无效索引的取消源。
    /// 大索引（数百万条）全量检查要跑很久，必须让用户能中途停下——
    /// 否则点错了只有强杀进程一条路。
    /// </summary>
    private CancellationTokenSource? _cleanupCts;

    [ObservableProperty]
    private int similarity = 80;

    public ModelsMatchAlgorithm MatchAlgorithm
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (Similarity < SimilarityMinimum)
                {
                    Similarity = SimilarityMinimum;
                }

                if (value == MatchAlgorithm.DctHash32)
                {
                    Similarity = 90;
                }

                OnPropertyChanged(nameof(SimilarityMinimum));
            }
        }
    } = ModelsMatchAlgorithm.All;

    public IReadOnlyList<ModelsMatchAlgorithm> MatchAlgorithms { get; } = Enum.GetValues<ModelsMatchAlgorithm>();

    public int SimilarityMinimum => MatchAlgorithm.HasFlag(ModelsMatchAlgorithm.DifferenceHash) ? 70 : 85;

    [ObservableProperty]
    private string sourceImageInfo = string.Empty;

    [ObservableProperty]
    private string sourceImagePath = string.Empty;

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private string searchStatusText = string.Empty;

    [ObservableProperty]
    private Visibility searchLoadingVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private double indexProgress;

    [ObservableProperty]
    private string indexProgressText = string.Empty;

    [ObservableProperty]
    private Visibility indexProgressVisibility = Visibility.Collapsed;

    [ObservableProperty]
    private string indexSpeedText = string.Empty;

    [ObservableProperty]
    private string indexThroughputText = string.Empty;

    [ObservableProperty]
    private string maxThroughputText = string.Empty;

    /// <summary>
    /// 进度卡里「速度」那一项的标签。索引时是「速度: 」，
    /// 清理无效索引时换成「检查速度: 」——同一张卡片服务两种任务。
    /// </summary>
    [ObservableProperty]
    private string progressSpeedLabel = "速度: ";

    /// <summary>
    /// 吞吐量相关项（吞吐 / 最大吞吐）是否显示。
    /// 清理索引只是查文件是否存在、不读字节，MB/s 没有意义，
    /// 所以那时隐藏，而不是让用户看到一个恒为 0 的假吞吐。
    /// </summary>
    [ObservableProperty]
    private Visibility progressThroughputVisibility = Visibility.Visible;

    [ObservableProperty]
    private string estimatedRemainingTimeText = string.Empty;

    [ObservableProperty]
    private string processingFilename = string.Empty;

    [ObservableProperty]
    private ObservableCollection<double> speedHistory = new();

    [ObservableProperty]
    private bool isSearchEnabled;

    [ObservableProperty]
    private double cpuUsage;

    [ObservableProperty]
    private double memoryUsage;

    [ObservableProperty]
    private string webApiServer;

    [ObservableProperty]
    private bool webApiServerRunning;

    /// <summary>
    /// 窗口是否置顶显示。绑定到 <see cref="Window.Topmost"/>，由标题栏的图钉按钮切换。
    /// </summary>
    [ObservableProperty]
    private bool isAlwaysOnTop;

    // ---- 索引队列 ----

    /// <summary>索引目录队列，顺序执行。</summary>
    public ObservableCollection<IndexSourceItem> IndexQueue { get; } = new();

    [ObservableProperty]
    private IndexSourceItem? selectedQueueItem;

    [ObservableProperty]
    private bool isQueueRunning;

    /// <summary>
    /// 索引配置区是否展开。收起后只留标题行，把纵向空间让给搜索结果——
    /// 索引配置属于"设置一次就不常动"的内容，搜索才是高频操作。
    /// </summary>
    [ObservableProperty]
    private bool isIndexConfigExpanded = true;

    /// <summary>索引配置内容的可见性，由 <see cref="IsIndexConfigExpanded"/> 派生。</summary>
    public Visibility IndexConfigVisibility =>
        IsIndexConfigExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>折叠按钮上的箭头方向。</summary>
    public string IndexConfigToggleGlyph => IsIndexConfigExpanded ? "▲" : "▼";

    partial void OnIsIndexConfigExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IndexConfigVisibility));
        OnPropertyChanged(nameof(IndexConfigToggleGlyph));
    }

    [RelayCommand]
    private void ToggleIndexConfig() => IsIndexConfigExpanded = !IsIndexConfigExpanded;

    [ObservableProperty]
    private string queueStatusText = string.Empty;

    /// <summary>
    /// 底部状态栏的操作结果提示（非阻塞）。
    ///
    /// 索引完成、清理完成之类的结果都写这里，而不是弹模态对话框——
    /// 弹窗必须手动点「确定」，既打断用户、也让自动化验证无法继续。
    /// 出错这类需要用户注意的情况才用弹窗。
    /// </summary>
    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>状态栏提示的可见性（有内容才占位）。</summary>
    public Visibility StatusMessageVisibility =>
        string.IsNullOrEmpty(StatusMessage) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(StatusMessageVisibility));
    }

    /// <summary>在状态栏显示一条操作结果（非阻塞）。</summary>
    private void NotifyStatus(string message) => StatusMessage = message;

    /// <summary>
    /// 本次索引是否包含视频。
    ///
    /// 关闭时进入「快速同步模式」：跳过最慢的抽帧环节，并重新扫描全部目录，
    /// 把新增/变动的图片同步进来（详见 <see cref="IndexQueueRunner.RunAsync"/>）。
    /// </summary>
    [ObservableProperty]
    private bool includeVideosInIndex = true;

    /// <summary>
    /// 检索时是否用 DCT 候选桶跳过不可能命中的条目。
    ///
    /// 只对「不含 Difference Hash 的算法」生效（候选桶按 DCT 哈希建桶），
    /// 且是近似剪枝：更快，但相似度阈值附近的少量真命中会被漏掉，
    /// 索引很大时还会额外占用数百 MB 内存。默认关闭（全量比对）。
    /// </summary>
    [ObservableProperty]
    private bool useDctCandidateIndex;

    /// <summary>队列执行期间正在处理的目录，用于更新进度文字。</summary>
    private IndexSourceItem? _currentRunningItem;

    private CancellationTokenSource? _queueCts;

    private Process? _currentProcess;
    private PerformanceCounter? _cpuCounter;
    private Timer? _performanceTimer;
    private Timer? _updateIndexTimer;
    private readonly IniFile _config = new IniFile("config.ini");

    public MainViewModel()
    {
        _indexService = ImageIndexService.Instance;
        _searchService = new ImageSearchService();

        _indexService.ProgressChanged += OnIndexProgressChanged;
        _indexService.IndexCompleted += OnIndexCompleted;
        _indexService.IndexUpdated += (sender, args) => Application.Current.Dispatcher.Invoke(UpdateIndexCount);

        LoadQueue();

        // 置顶与「是否索引视频」都是用户偏好，跨次启动保留。
        // 存在独立的 ui_preferences.json，不写 config.ini——
        // 后者的 IniFile.Save() 会抹掉全部注释（见 UiPreferences 的说明）。
        IsAlwaysOnTop = UiPreferences.LoadAlwaysOnTop();
        IncludeVideosInIndex = UiPreferences.LoadIncludeVideos();
        UseDctCandidateIndex = UiPreferences.LoadUseDctCandidateIndex();

        // 异步初始化性能监测，避免阻塞 UI 线程
        _ = Task.Run(InitializePerformanceMonitoring);
        WebApiServerRunning = WebApiStartup.ServerRunning;
        LoadIndexAsync();
        HomeController.MainViewModel = this;
        // 显示实际监听地址；未对外暴露时文档页位于 /api
        WebApiServer = WebApiStartup.ServerRunning
            ? (WebApiStartup.ListenUrl ?? $"http://127.0.0.1:{_config.GetValue("Global", "HttpPort", 5000)}") + "/api"
            : string.Empty;
        if (_config.GetValue("Global", "IndexAutoUpdate", false))
        {
            _updateIndexTimer = new Timer(TimeSpan.FromHours(1));
            _updateIndexTimer.Elapsed += (sender, args) =>
            {
                // 这里刻意不碰 StartQueueCommand：命令的 CanExecute 只接受命令自己的
                // 参数类型（传别的对象会抛 ArgumentException），而定时器的回调抛异常
                // 会直接终止进程。是否该跑交给上面的条件与 RunQueueAsync 内的原子守卫判断。
                if (!IsQueueRunning && IndexProgressVisibility != Visibility.Visible)
                {
                    // 走 automatic:true 的路径：定时任务在用户没操作时自己跑起来，
                    // 任何模态框都会打断用户，因此它全程只写状态栏与日志。
                    Application.Current.Dispatcher.Invoke(() => _ = RunQueueAutomaticallyAsync());
                }
            };
            _updateIndexTimer.Start();
        }
    }

    private void LoadQueue()
    {
        foreach (var item in IndexSourceStore.Load())
        {
            IndexQueue.Add(item);
        }

        // 上次运行失败或未完成的目录在本次启动时保持可执行状态
        foreach (var item in IndexQueue.Where(i => i.Status != IndexSourceStatus.Completed))
        {
            item.Status = IndexSourceStatus.Pending;
            item.Message = string.Empty;
        }

        UpdateQueueStatus();
    }

    private void SaveQueue()
    {
        IndexSourceStore.Save(IndexQueue);
    }

    private void UpdateQueueStatus()
    {
        if (IndexQueue.Count == 0)
        {
            QueueStatusText = "队列为空，请添加要索引的文件夹";
            return;
        }

        if (!IncludeVideosInIndex)
        {
            // 快速同步模式：本次会重新扫描所有目录（不是为了补齐，而是为了同步图片变化），
            // 因此不显示「已完成 N」，那会让用户误以为这些目录会被跳过。
            var imagesDone = IndexQueue.Count(i => i.ImagesIndexed);
            var missingVideo = IndexQueue.Count(i => !i.VideosIndexed);
            QueueStatusText = $"共 {IndexQueue.Count} 个目录　⚡ 快速同步模式（不索引视频）"
                + $"\n图片已索引 {imagesDone}，未索引视频 {missingVideo}"
                + "\n本次将重新扫描全部目录以同步图片变化";
            return;
        }

        var completed = IndexQueue.Count(i => i.Status == IndexSourceStatus.Completed);
        var failed = IndexQueue.Count(i => i.Status == IndexSourceStatus.Failed);
        var pending = IndexQueue.Count - completed - failed;
        var videosPending = IndexQueue.Count(i => !i.VideosIndexed);

        var text = $"共 {IndexQueue.Count} 个目录：已完成 {completed}，等待 {pending}，失败 {failed}";
        if (videosPending > 0)
        {
            // 提示还有目录的视频未索引，避免用户以为一切就绪后再也补不上
            text += $"\n其中 {videosPending} 个目录的视频尚未索引（取消勾选「索引视频」时不会被处理）";
        }

        QueueStatusText = text;
    }

    partial void OnImagePathChanged(string value)
    {
        if (File.Exists(value))
        {
            SourceImagePath = value;
            UpdateSourceImageInfo(value);
        }
    }

    partial void OnSelectedResultChanged(SearchResult? value)
    {
        if (value != null && File.Exists(value.路径))
        {
            DestImagePath = value.路径;
            UpdateDestImageInfo(value.路径);
            EnsurePreviewThumbnail(value);
        }
    }

    /// <summary>
    /// 用户选中视频结果时，若预览帧尚未生成则按需生成，完成后刷新预览。
    ///
    /// 抽帧从检索路径移到了这里（见 ImageSearchService 的说明）：预览是渲染关注点，
    /// 只在用户真的要看这一条时才付出 ffmpeg 代价，检索本身不再被拖慢。
    /// 生成在后台线程进行，期间界面照常可用；失败过的视频在静默期内不再重试。
    /// </summary>
    private void EnsurePreviewThumbnail(SearchResult result)
    {
        if (!VideoFormats.IsVideo(result.路径))
        {
            return;
        }

        var path = result.路径;

        // 已生成则无需动作（GetCached 命中）；近期失败过的则保持静默，不再打扰
        if (VideoThumbnailCache.GetCached(path) != null || VideoThumbnailCache.IsRecentlyFailed(path))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var thumbnail = VideoThumbnailCache.GetOrCreate(path);
            if (thumbnail == null)
            {
                return;   // 抽不出帧或已在静默期：预览保持空白，不再打扰用户
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                // 预览绑定的是 DestImagePath；仅当仍是当前选中项时才通知刷新
                if (SelectedResult?.路径 == path)
                {
                    OnPropertyChanged(nameof(DestImagePath));
                    UpdateDestImageInfo(path);
                }
            });
        });
    }

    partial void OnSimilarityChanged(int value)
    {
        var minimum = SimilarityMinimum;
        if (value < minimum)
        {
            Similarity = minimum;
        }
    }

    /// <summary>置顶状态变化时写入偏好文件，下次启动沿用。</summary>
    partial void OnIsAlwaysOnTopChanged(bool value)
    {
        UiPreferences.SaveAlwaysOnTop(value);
    }

    /// <summary>「是否索引视频」变化时写入偏好文件，下次启动沿用。</summary>
    partial void OnIncludeVideosInIndexChanged(bool value)
    {
        UiPreferences.SaveIncludeVideos(value);
        UpdateQueueStatus();
    }

    /// <summary>「候选桶加速」变化时写入偏好文件，下次启动沿用。</summary>
    partial void OnUseDctCandidateIndexChanged(bool value)
    {
        UiPreferences.SaveUseDctCandidateIndex(value);
    }

    private async void LoadIndexAsync()
    {
        // 立即刷新一次：此时索引尚未载入，界面应显示「正在载入索引…」
        // 而不是「请先创建索引」（后者会让用户误以为索引丢了）。
        UpdateIndexCount();

        await _indexService.LoadIndexAsync();
        UpdateIndexCount();
        ReportLoadIssues();
    }

    /// <summary>
    /// 索引不可用时给搜索操作一个准确的提示：
    /// 区分「还在载入」与「确实是空的」，避免让用户误以为索引丢失。
    /// </summary>
    private void NotifyIndexUnavailableForSearch()
    {
        if (IsIndexLoading)
        {
            NotifyStatus("⏳ 索引正在载入中，请稍候片刻再搜索（索引较大时需要数秒）");
            return;
        }

        NotifyStatus("⚠️ 当前没有任何索引，请先在「索引配置」中添加文件夹并开始队列");
    }

    /// <summary>
    /// 索引加载异常时告知用户（含"已从备份恢复"）。
    /// 加载失败必须让用户知道——否则他会以为索引空了、然后重建一遍。
    /// </summary>
    private void ReportLoadIssues()
    {
        var result = _indexService.LoadResult;

        if (result.Success && result.UsedFallback)
        {
            MessageBox.Show(
                Application.Current.MainWindow!,
                $"原索引文件无法读取，已自动从备份恢复。\r\n\r\n" +
                $"来源：{result.SourcePath}\r\n载入：{result.Count:#,0} 条\r\n\r\n" +
                "建议：确认索引数量无误后，随便触发一次索引写盘即可用恢复正常的主文件；\r\n" +
                "备份目录中保留了最近几天的快照，必要时可手工取用。",
                "索引已从备份恢复",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!result.Success && result.Error != null)
        {
            MessageBox.Show(
                Application.Current.MainWindow!,
                $"读出索引失败：{result.Error.Message}\r\n\r\n" +
                "本次以空索引启动，请勿在有重要索引的情况下直接开始重建。\r\n" +
                "可先检查程序目录下的 index.json 与 backups 文件夹。",
                "索引加载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>添加索引目录，支持一次多选；自动处理父子包含与重复。</summary>
    [RelayCommand]
    private void AddFolders()
    {
        if (IsQueueRunning)
        {
            MessageBox.Show(Application.Current.MainWindow!, "队列正在运行，请先停止后再修改队列", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picked = FolderPicker.PickFolders();
        if (picked.Error != null)
        {
            // 选择器本身出错，必须告知用户，避免"点了没反应"的困惑
            MessageBox.Show(Application.Current.MainWindow!, $"打开文件夹选择器失败：\r\n{picked.Error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (picked.Folders.Count == 0)
        {
            // 用户取消，无需提示
            return;
        }

        AddDirectories(picked.Folders);
    }

    /// <summary>
    /// 把一组目录并入队列，处理重复与父子包含关系。
    /// </summary>
    /// <param name="directories">要加入的目录。</param>
    /// <param name="interactive">
    /// 是否用弹窗汇报结果。WebAPI 调用时必须传 false——
    /// 弹窗是模态的，会让 HTTP 请求一直挂到用户点掉对话框为止。
    /// </param>
    public void AddDirectories(IEnumerable<string> directories, bool interactive = true)
    {
        var added = new List<string>();
        var skippedDuplicate = new List<string>();
        var skippedMissing = new List<string>();
        var replacedChildren = new List<string>();

        foreach (var raw in directories)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string dir;
            try
            {
                dir = DirectoryTree.Normalize(raw);
            }
            catch
            {
                skippedMissing.Add(raw);
                continue;
            }

            if (!Directory.Exists(dir))
            {
                skippedMissing.Add(dir);
                continue;
            }

            // 已存在相同目录，或已存在其父目录 -> 无需重复加入
            if (IndexQueue.Any(q => DirectoryTree.IsSameOrUnder(DirectoryTree.Normalize(q.Directory), dir)))
            {
                skippedDuplicate.Add(dir);
                continue;
            }

            // 新目录是某些已入队目录的父目录 -> 移除那些子目录，避免重复扫描
            var children = IndexQueue.Where(q => DirectoryTree.IsSameOrUnder(dir, DirectoryTree.Normalize(q.Directory))).ToList();
            foreach (var child in children)
            {
                IndexQueue.Remove(child);
                replacedChildren.Add(child.Directory);
            }

            IndexQueue.Add(new IndexSourceItem(dir));
            added.Add(dir);
        }

        if (added.Count > 0)
        {
            SaveQueue();
            UpdateQueueStatus();
        }

        var messages = new List<string>();
        if (added.Count > 0)
        {
            messages.Add($"已添加 {added.Count} 个目录");
        }

        if (replacedChildren.Count > 0)
        {
            messages.Add($"新目录已包含原有 {replacedChildren.Count} 个子目录，已自动合并：\r\n{string.Join("\r\n", replacedChildren.Take(10))}");
        }

        if (skippedDuplicate.Count > 0)
        {
            messages.Add($"{skippedDuplicate.Count} 个目录已被现有队列包含，已跳过：\r\n{string.Join("\r\n", skippedDuplicate.Take(10))}");
        }

        if (skippedMissing.Count > 0)
        {
            messages.Add($"{skippedMissing.Count} 个目录不存在，已跳过：\r\n{string.Join("\r\n", skippedMissing.Take(10))}");
        }

        if (messages.Count == 0)
        {
            return;
        }

        if (!interactive)
        {
            // 无界面调用（WebAPI）：结果只写进状态栏，不用模态弹窗挡住请求
            QueueStatusText = string.Join("；", messages.Select(m => m.Replace("\r\n", "，")));
            return;
        }

        MessageBox.Show(Application.Current.MainWindow!, string.Join("\r\n\r\n", messages), "索引队列", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void RemoveQueueItem()
    {
        if (IsQueueRunning)
        {
            MessageBox.Show(Application.Current.MainWindow!, "队列正在运行，请先停止后再修改队列", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SelectedQueueItem == null)
        {
            return;
        }

        IndexQueue.Remove(SelectedQueueItem);
        SaveQueue();
        UpdateQueueStatus();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (IsQueueRunning)
        {
            MessageBox.Show(Application.Current.MainWindow!, "队列正在运行，请先停止后再修改队列", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IndexQueue.Count == 0)
        {
            return;
        }

        var result = MessageBox.Show(Application.Current.MainWindow!, "确认清空索引队列吗？（不会删除已建立的索引）", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        IndexQueue.Clear();
        SaveQueue();
        UpdateQueueStatus();
    }

    /// <summary>按队列顺序逐个目录建立索引，单个目录失败不影响后续目录。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartQueue()
    {
        await RunQueueAsync(automatic: false);
    }

    /// <summary>
    /// 「自动更新」定时器的执行入口。
    ///
    /// 特意不走 <see cref="StartQueueCommand"/>：命令方法一旦带参数，
    /// 生成的命令就从 RelayCommand 变成 RelayCommand&lt;T&gt;，而按钮不带参数时
    /// 调用的是 CanExecute(null)——null 转不成 T，命令被判为不可执行，
    /// 「开始队列」按钮会直接变成不可点击。（实测确认）
    /// </summary>
    private async Task RunQueueAutomaticallyAsync()
    {
        await RunQueueAsync(automatic: true);
    }

    /// <param name="automatic">
    /// 是否由「自动更新」定时器触发。自动触发时全程不弹模态框：
    /// 它在用户没做任何操作的时候自己跑起来，弹框会打断正在做的事，
    /// 结果一律走状态栏 + 日志（人工触发的运行保留弹框，因为用户在等结果）。
    /// </param>
    private async Task RunQueueAsync(bool automatic)
    {
        // 原子地「检查并置位」：不能写成 `if (IsQueueRunning) return; ... IsQueueRunning = true;`
        // ——两者之间存在窗口，快速双击「开始队列」会让两个执行流都通过检查，
        // 导致同一队列被并发跑两遍（重复索引、进度错乱）。
        if (Interlocked.CompareExchange(ref _queueRunningFlag, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await StartQueueCore(automatic);
        }
        finally
        {
            Interlocked.Exchange(ref _queueRunningFlag, 0);
        }
    }

    /// <summary>队列执行标志，0=空闲 1=运行中（配合 Interlocked 使用）。</summary>
    private int _queueRunningFlag;

    private async Task StartQueueCore(bool automatic)
    {
        if (IndexQueue.Count == 0)
        {
            if (automatic)
            {
                NotifyStatus("⚠️ 自动同步：队列中没有索引目录，已跳过");
                return;
            }

            MessageBox.Show(Application.Current.MainWindow!, "请先添加要索引的文件夹", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 判断「是否还有事可做」必须按本次选择的范围来，不能只看目录的 Completed 状态：
        //   · 不索引视频（快速同步模式）：本次会重新扫描全部目录以同步图片变化，所以永远有事可做；
        //   · 索引视频：目录的 Completed 只代表图片做过，视频若没做过（VideosIndexed=false）
        //     仍需要处理——否则用户先选「不索引视频」、再勾选回来时会什么都做不了，视频永远补不上。
        var includeVideos = IncludeVideosInIndex;
        var runnable = includeVideos
            ? IndexQueue.Where(i => i.Status != IndexSourceStatus.Completed || !i.VideosIndexed).ToList()
            : IndexQueue.ToList();

        if (runnable.Count == 0)
        {
            if (automatic)
            {
                NotifyStatus("自动同步：所有目录的图片与视频都已完成索引，无需处理");
                return;
            }

            MessageBox.Show(
                Application.Current.MainWindow!,
                "队列中所有目录的图片与视频都已完成索引。\r\n如需重新索引，请移除后重新添加。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 索引还没加载完就开始索引，曾经会导致原索引库被覆盖（数据丢失）。
        // 服务层已经会等待加载完成，这里只是把等待显式告知用户，避免界面"卡一下"让人以为没反应。
        if (!_indexService.IsLoaded)
        {
            if (automatic)
            {
                // 自动同步遇到未加载完就放弃本轮，等下个周期；等待会把定时器线程占住
                NotifyStatus("⚠️ 自动同步：索引尚未载入完成，本轮已跳过");
                return;
            }

            MessageBox.Show(
                Application.Current.MainWindow!,
                "索引正在载入中，请稍候再开始队列。\r\n\r\n" +
                "（载入完成前建立索引会与加载过程冲突，可能造成索引库损坏。）",
                "请稍候", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsQueueRunning = true;
        _queueCts = new CancellationTokenSource();
        var ct = _queueCts.Token;

        // 进度卡在索引配置区内部：若该区被收起，用户就看不到索引进度了。
        // 开始索引时自动展开，避免"点了开始却毫无反馈"。
        IsIndexConfigExpanded = true;

        var runner = new IndexQueueRunner(_indexService);

        void OnRunnerStatus(object? _, QueueItemStatusEventArgs args)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _currentRunningItem = args.Item;
            });
        }

        runner.ItemStatusChanged += OnRunnerStatus;

        try
        {
            var result = await runner.RunAsync(IndexQueue, ct, includeVideos);

            // 队列结果改为状态栏一行摘要（非阻塞）。
            // 完整明细写进日志，失败目录另外列出——不为一句「完成」弹窗等用户点确定。
            var head = includeVideos ? "索引队列完成" : "图片快速同步完成";
            NotifyStatus(
                $"✅ {head}：成功 {result.Success}，失败 {result.Failed}，跳过 {result.Skipped}；" +
                $"新增索引 {result.TotalAdded:#,0}，总索引 {result.TotalIndex:#,0}"
                + (result.Stopped ? "（已手动停止，未执行的目录下次继续）" : string.Empty));

            var detail = new System.Text.StringBuilder();
            detail.AppendLine(head);
            if (!includeVideos)
            {
                detail.AppendLine("（本次未索引视频，视频缩略图未生成）");
            }

            detail.AppendLine();
            detail.AppendLine($"成功：{result.Success}");
            detail.AppendLine($"失败：{result.Failed}");
            detail.AppendLine($"跳过：{result.Skipped}");
            detail.AppendLine();
            detail.AppendLine($"新增索引：{result.TotalAdded:#,0}");
            detail.AppendLine($"总索引：{result.TotalIndex:#,0}（本次开始前 {result.StartIndex:#,0}）");

            if (result.Stopped)
            {
                detail.AppendLine();
                detail.AppendLine("队列已被手动停止，未执行的目录将在下次开始时继续。");
            }

            LogManager.Info(detail.ToString());

            // 有目录失败才需要用户介入，这时给弹窗；否则只留状态栏。
            if (result.FailedDirectories.Count > 0)
            {
                detail.AppendLine();
                detail.AppendLine("以下目录处理失败：");
                foreach (var f in result.FailedDirectories.Take(10))
                {
                    detail.AppendLine("  " + f);
                }

                if (automatic)
                {
                    // 自动同步不弹窗：它在用户没操作时自己跑起来，模态框会打断用户。
                    // 失败信息不能就此消失——写日志 + 状态栏点名，且队列项会标记为失败。
                    LogManager.Error(nameof(MainViewModel), $"{head}（自动同步）有 {result.FailedDirectories.Count} 个目录处理失败：\r\n{string.Join("\r\n", result.FailedDirectories)}");
                    NotifyStatus(
                        $"⚠️ {head}（自动同步）：{result.FailedDirectories.Count} 个目录处理失败，"
                        + $"详情见日志。首个：{result.FailedDirectories[0]}");
                }
                else
                {
                    var errorDialog = new ErrorsDialog(detail.ToString());
                    errorDialog.ShowDialog();
                }
            }
        }
        finally
        {
            runner.ItemStatusChanged -= OnRunnerStatus;
            _currentRunningItem = null;
            IsQueueRunning = false;
            SaveQueue();
            UpdateQueueStatus();
            UpdateIndexCount();
        }
    }

    [RelayCommand]
    private void StopQueue()
    {
        if (!IsQueueRunning)
        {
            return;
        }

        _indexService.StopIndexing();
        _queueCts?.Cancel();
        QueueStatusText = "正在停止，请等待当前目录处理结束...";
    }

    /// <summary>供 WebAPI 调用：把目录并入队列并执行。</summary>
    /// <remarks>
    /// 必须整体在 UI 线程上执行（调用方用 Dispatcher.InvokeAsync 切过来）：
    /// AddDirectories 会修改绑定到列表的 IndexQueue，StartQueue 会弹窗。
    /// </remarks>
    public async Task EnqueueAndRunAsync(string directory)
    {
        AddDirectories([directory], interactive: false);
        await StartQueue();
    }

    /// <summary>
    /// 检查并清理无效索引。
    /// 只有明确确认文件不存在的条目才会进入待删除列表，且始终需要用户确认。
    /// </summary>
    [RelayCommand]
    private async Task CleanInvalidIndex()
    {
        if (IsQueueRunning || IsCheckingInvalidIndex)
        {
            return;
        }

        if (_indexService.Index.Count == 0)
        {
            NotifyIndexUnavailableForSearch();
            return;
        }

        IsCheckingInvalidIndex = true;

        // 同理：清理进度也在该区域内，先展开再开始
        IsIndexConfigExpanded = true;

        // 供「停止清理」按钮取消；每次清理独立一个，结束即释放
        _cleanupCts = new CancellationTokenSource();
        var ct = _cleanupCts.Token;

        // 复用索引进度卡：检查百万级索引要跑一阵，没有进度用户无法判断是否卡死。
        // 清理不读文件内容，故吞吐 MB/s 无意义 —— 换成「检查速度」并隐藏吞吐项。
        ResetProgressCard();
        ProgressSpeedLabel = "检查速度: ";
        ProgressThroughputVisibility = Visibility.Collapsed;
        IndexProgressVisibility = Visibility.Visible;

        var sw = Stopwatch.StartNew();
        try
        {
            var paths = _indexService.Index.Keys.ToList();

            // Progress<T> 会把回调编组回创建它的线程（这里是 UI 线程），
            // 因此可直接在回调里更新控件。
            var progress = new Progress<InvalidIndexScanProgress>(p =>
            {
                if (!IsCheckingInvalidIndex)
                {
                    return;
                }

                var elapsed = sw.Elapsed.TotalSeconds;
                var speed = elapsed > 0 ? p.Checked / elapsed : 0;
                UpdateProgressCard(p.Checked, p.Total, p.CurrentPath, speed, throughputMb: 0);
            });

            var report = await InvalidIndexScanner.ScanAsync(paths, ct, progress);

            if (report.WasCancelled)
            {
                // 取消不是错误，用状态栏提示即可，不再弹窗打断
                NotifyStatus($"⏹️ 已停止检查（已检查 {report.TotalCountChecked:#,0} / {paths.Count:#,0} 条，未做任何删除）");
                return;
            }

            var dialog = new RemoveInvalidIndexDialog(report);
            dialog.ShowDialog();

            if (!dialog.Confirmed)
            {
                return;
            }

            var removed = await _indexService.RemoveManyFromIndexAsync(report.ConfirmedMissing);
            UpdateIndexCount();

            // 清理是破坏性操作，但结果本身不含需用户处理的信息，因此只写状态栏。
            // （待删除清单已在执行前的预览对话框里确认过了，这里不必再弹一次。）
            NotifyStatus($"🧽 已清理 {removed:#,0} 条无效索引，当前索引 {_indexService.Index.Count:#,0}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, $"检查无效索引时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            sw.Stop();
            IsCheckingInvalidIndex = false;
            _cleanupCts?.Dispose();
            _cleanupCts = null;
            // 收起进度卡并复原成「索引」的默认外观，
            // 否则下次索引会顶着「检查速度:」和隐藏的吞吐项跑
            IndexProgressVisibility = Visibility.Collapsed;
            ResetProgressCard();
        }
    }

    /// <summary>
    /// 停止「清理无效索引」的检查过程。
    ///
    /// 检查是只读的：停止只是不再继续检查，**不会删除任何索引**
    /// （删除本来就要在预览对话框里另行确认，中止检查时连对话框都不会出现）。
    /// </summary>
    [RelayCommand]
    private void StopCleanup()
    {
        if (!IsCheckingInvalidIndex)
        {
            return;
        }

        _cleanupCts?.Cancel();
        NotifyStatus("正在停止检查，请稍候…");
    }

    [RelayCommand]
    private void SelectImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = MediaFormats.DialogFilter
        };

        if (dialog.ShowDialog() == true)
        {
            ImagePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            MessageBox.Show(Application.Current.MainWindow!, "请先选择图片", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!IsSearchEnabled)
        {
            NotifyIndexUnavailableForSearch();
            return;
        }

        // 图片与视频都可作为查询。
        // 视频会先抽取代表帧再参与比对（与索引时的处理一致），
        // 因此这里不能只按 MIME 类型是否为 image 来判断。
        if (!IsSearchableMedia(ImagePath))
        {
            MessageBox.Show(Application.Current.MainWindow!, "不是受支持的图片或视频文件，无法检索", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await SearchCore(ImagePath);
    }

    [RelayCommand]
    private async Task SearchFromClipboard()
    {
        if (!IsSearchEnabled)
        {
            NotifyIndexUnavailableForSearch();
            return;
        }

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0)
            {
                ImagePath = files[0]!;
                await Search();
            }
            else
            {
                IsSearching = false;
                SearchLoadingVisibility = Visibility.Collapsed;
            }

            return;
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText().Trim();
            if (File.Exists(text))
            {
                ImagePath = text;
                await Search();
            }
            else
            {
                IsSearching = false;
                SearchLoadingVisibility = Visibility.Collapsed;
            }

            return;
        }

        if (Clipboard.ContainsImage())
        {
                    // 在 UI 线程（STA 模式）获取剪贴板图片，然后在后台线程处理编码
                    try
                    {
                        var image = Clipboard.GetImage();
                        if (image != null)
                        {
                            // 立即冻结图片对象，使其可以跨线程访问
                            image.Freeze();

                            // 编码与搜索在后台线程执行，避免 UI 线程阻塞
                            _ = Task.Run(() => RunTempImageSearchAsync(".jpg", stream =>
                            {
                                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                                encoder.Save(stream);
                                return Task.CompletedTask;
                            }));
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(Application.Current.MainWindow!, $"读取剪贴板失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        IsSearching = false;
                        SearchLoadingVisibility = Visibility.Collapsed;
                        SearchStatusText = string.Empty;
                    }
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedResult != null)
        {
            FileExplorerHelper.ExplorerFile(SelectedResult.路径);
        }
    }

    /// <summary>
    /// 打开指定结果所在的文件夹（供列表每行的按钮调用）。
    /// 与 <see cref="OpenFolder"/>（作用于选中项）区分：这里由行内按钮传入具体那一行，
    /// 不必先选中。
    /// </summary>
    [RelayCommand]
    private void OpenResultFolder(SearchResult? result)
    {
        if (result != null)
        {
            FileExplorerHelper.ExplorerFile(result.路径);
        }
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedResult == null) return;

        // 永久删除（Shift+Delete）：明确告知不可恢复。
        // 默认 Delete 走 DeleteToRecycleBin，可从回收站找回。
        var result = MessageBox.Show(Application.Current.MainWindow!,
            "确认永久删除选中项吗？\r\n此操作不可恢复（不会进入回收站）。",
            "永久删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.OK)
        {
            if (File.Exists(SelectedResult.路径))
            {
                // 删除前释放 Image 控件占用的文件
                if (DestImagePath == SelectedResult.路径)
                {
                    DestImagePath = string.Empty;
                    DestImageInfo = string.Empty;
                }

                File.Delete(SelectedResult.路径);
            }
            _indexService.RemoveFromIndex(SelectedResult.路径);
            SearchResults.Remove(SelectedResult);
            UpdateIndexCount();
        }
    }

    [RelayCommand]
    private void DeleteToRecycleBin()
    {
        if (SelectedResult == null) return;

        var result = MessageBox.Show(Application.Current.MainWindow!, "确认删除到回收站吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.OK)
        {
            // 删除前释放 Image 控件占用的文件
            if (DestImagePath == SelectedResult.路径)
            {
                DestImagePath = string.Empty;
                DestImageInfo = string.Empty;
            }

            RecycleBinHelper.Delete(SelectedResult.路径);
            _indexService.RemoveFromIndex(SelectedResult.路径);
            SearchResults.Remove(SelectedResult);
            UpdateIndexCount();
        }
    }

    public async Task HandleDrop(IDataObject dataObject)
    {
        try
        {
            // 1. 检查文件拖放
            if (dataObject.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])dataObject.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0)
                {
                    // 拖入文件夹：意图是「把这个文件夹加入索引队列」，
                    // 与队列列表区域的拖放行为保持一致（原先会走到下面
                    // 报「不是受支持的图片或视频文件」，对文件夹来说很费解）。
                    var dirs = files.Where(Directory.Exists).ToList();
                    if (dirs.Count > 0)
                    {
                        AddDirectories(dirs);
                        return;
                    }

                    // 图片与视频都可作为查询。多选时取第一个可检索的文件，
                    // 并在状态栏说明，避免用户多拖几个文件后以为"没反应"。
                    var media = files.FirstOrDefault(IsSearchableMedia);
                    if (media == null)
                    {
                        MessageBox.Show(Application.Current.MainWindow!,
                            "拖入的文件不是受支持的图片或视频格式。",
                            "无法搜索", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    if (files.Length > 1)
                    {
                        NotifyStatus($"拖入 {files.Length} 个文件，已用第一个可检索的文件搜索：{Path.GetFileName(media)}");
                    }

                    ImagePath = media;
                    await Search();
                    return;
                }
            }

            // 2. 直接获取位图数据（优先处理，避免格式转换问题）
            if (dataObject.GetDataPresent(DataFormats.Bitmap))
            {
                if (TryGetFrozenBitmap(dataObject, DataFormats.Bitmap, out var bitmap))
                {
                    _ = Task.Run(() => RunTempImageSearchAsync(".jpg", stream =>
                    {
                        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        encoder.Save(stream);
                        return Task.CompletedTask;
                    }));
                    return;
                }
            }

            // 3. 处理 DIB (Device Independent Bitmap) 格式
            if (dataObject.GetDataPresent(DataFormats.Dib))
            {
                if (TryGetFrozenBitmap(dataObject, DataFormats.Dib, out var dibBitmap))
                {
                    _ = Task.Run(() => RunTempImageSearchAsync(".jpg", stream =>
                    {
                        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(dibBitmap));
                        encoder.Save(stream);
                        return Task.CompletedTask;
                    }));
                    return;
                }
            }

            // 4. 处理浏览器拖放的图片（FileContents）
            if (dataObject.GetDataPresent("FileContents"))
            {
                try
                {
                    if (dataObject.GetData("FileContents") is Stream stream)
                    {
                        _ = Task.Run(() => RunTempImageSearchAsync(".jpg",
                            target => stream.CopyToAsync(target)));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FileContents 处理失败: {ex.Message}");
                    // 继续尝试其他格式
                }
            }

            // 5. 处理URL或Base64文本
            if (dataObject.GetDataPresent(DataFormats.Text))
            {
                try
                {
                    string text = dataObject.GetData(DataFormats.Text)!.ToString()!;

                    // 检查是否为URL
                    if (Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    {
                        // 下载与搜索在后台线程执行，避免 UI 线程阻塞
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var ext = Path.GetExtension(uri.AbsolutePath);
                                if (string.IsNullOrEmpty(ext))
                                {
                                    ext = ".jpg";
                                }

                                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                                await using var stream = await httpClient.GetStreamAsync(uri);
                                await RunTempImageSearchAsync(ext, async target =>
                                {
                                    await stream.CopyToAsync(target);
                                });
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"URL 处理异常: {ex.Message}");
                            }
                        });
                        return;
                    }

                    // 检查是否为Base64图像数据
                    if (text.StartsWith("data:image/"))
                    {
                        int commaIndex = text.IndexOf(',');
                        if (commaIndex != -1)
                        {
                            string base64Data = text.Substring(commaIndex + 1);
                            byte[] bytes = Convert.FromBase64String(base64Data);
                            _ = Task.Run(() => RunTempImageSearchAsync(".jpg", target =>
                            {
                                target.Write(bytes);
                                return Task.CompletedTask;
                            }));
                            return;
                        }
                    }

                    // 检查是否为本地文件路径
                    if (File.Exists(text))
                    {
                        ImagePath = text;
                        await Search();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"文本数据处理失败: {ex.Message}");
                    // 继续尝试其他格式
                }
            }

            // 如果所有格式都失败，显示提示
            MessageBox.Show(Application.Current.MainWindow!, "无法识别拖放的数据格式，请尝试从剪切板搜索或选择本地文件拖放", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, $"处理拖放数据时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Debug.WriteLine($"HandleDrop 异常: {ex}");
        }
        finally
        {
            IsSearching = false;
            SearchLoadingVisibility = Visibility.Collapsed;
            SearchStatusText = string.Empty;
        }
    }

    public void HandleDataGridKeyUp(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Delete && SelectedResult != null)
        {
            // 与 Windows 资源管理器习惯及右键菜单一致：
            // Delete → 回收站（可恢复，DeleteToRecycleBin 内含确认）；
            // Shift+Delete → 永久删除（Delete 内含确认）。
            if (modifiers == ModifierKeys.Shift)
            {
                Delete();
            }
            else
            {
                DeleteToRecycleBin();
            }
        }

        if (modifiers == ModifierKeys.Control && key == Key.O && SelectedResult != null)
        {
            FileExplorerHelper.ExplorerFile(SelectedResult.路径);
        }
    }

    /// <summary>
    /// 是否允许关闭窗口。
    ///
    /// 只拦「正在进行且会丢数据」的操作：索引中、写盘中、队列运行中。
    ///
    /// **刻意不拦「索引载入中」**：加载是只读的，关掉不会损坏任何数据。
    /// 更重要的是，程序启动时有两条路径会立即退出自身（提权重启、单实例激活），
    /// 它们会触发关闭流程；若此处因"正在加载"而取消关闭，程序就会卡在一个
    /// 无法关闭的窗口上并反复弹「请稍后再试」——加载完成后也不会消失，
    /// 因为退出流程已经被取消掉了。（这是实际发生过的故障。）
    /// </summary>
    public bool CanClose()
    {
        return _indexService is { IsIndexing: false, IsWriting: false }
               && !IsQueueRunning;
    }

    /// <summary>搜索重入守卫：按钮 IsEnabled 只挡住鼠标，Enter/Ctrl+V/拖放仍可并发触发。</summary>
    private int _searchRunning;

    private async Task SearchCore(string filename)
    {
        // 上一次搜索未结束则忽略新请求，避免两个搜索并发、后完成者覆盖结果列表
        if (Interlocked.CompareExchange(ref _searchRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            IsSearching = true;
            SearchLoadingVisibility = Visibility.Visible;
            SearchStatusText = "🔍 正在搜索相似图片...";
            ElapsedTime = string.Empty;

            // 在后台线程执行搜索,避免 UI 线程阻塞
            var (results, elapsed) = await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var sim = Similarity / 100f;

                var resultList = await _searchService.SearchAsync(
                    filename,
                    _indexService.Index,
                    MatchAlgorithm,
                    sim,
                    FindRotated,
                    FindFlipped,
                    UseDctCandidateIndex);

                sw.Stop();
                return (resultList, sw.ElapsedMilliseconds);
            });

            // 切回 UI 线程更新 UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                ElapsedTime = $"{elapsed}ms";

                SearchResults.Clear();
                foreach (var result in results)
                {
                    SearchResults.Add(result);
                }

                if (SearchResults.Count > 0)
                {
                    SelectedResult = SearchResults[0];

                    // 检索目的是「找出同一张图散落在哪些文件夹」，所以直接报出文件夹数，
                    // 不必让用户自己去数列表里有多少个不同路径。
                    var folders = SearchResults.Select(r => r.所在文件夹)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    SearchStatusText = folders > 1
                        ? $"✅ 搜索完成，找到 {SearchResults.Count} 个相似图片，分布在 {folders} 个文件夹"
                        : $"✅ 搜索完成，找到 {SearchResults.Count} 个相似图片（同一文件夹内）";
                }
                else
                {
                    SearchStatusText = "ℹ️ 未找到相似图片";
                }
            });

            // 用视频作查询时，其缩略图是在搜索过程中才生成的（参与哈希前要先抽帧），
            // 而预览绑定在设置路径时就已求值过（那时缩略图还不存在，转换器返回了 null）。
            // 这里同时重算信息文字并通知 SourceImagePath 重新绑定，
            // 否则会出现「信息说已有代表帧、图片区域却空白」的不一致。
            if (VideoFormats.IsVideo(filename))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateSourceImageInfo(filename);
                    OnPropertyChanged(nameof(SourceImagePath));
                });
            }
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                SearchStatusText = $"❌ 搜索失败: {ex.Message}";
                MessageBox.Show(Application.Current.MainWindow!, $"搜索时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            IsSearching = false;
            Interlocked.Exchange(ref _searchRunning, 0);
            // 延迟隐藏 loading，让用户看到完成状态
            await Task.Delay(800);
            SearchLoadingVisibility = Visibility.Collapsed;
            SearchStatusText = string.Empty;
        }
    }

    /// <summary>
    /// 从拖放数据取出位图并冻结（冻结后才能跨线程编码）。
    /// 取数失败时返回 false，让调用方继续尝试后续格式。
    /// </summary>
    private static bool TryGetFrozenBitmap(IDataObject dataObject, string format,
        out System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        try
        {
            if (dataObject.GetData(format) is System.Windows.Media.Imaging.BitmapSource source)
            {
                source.Freeze();
                bitmap = source;
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{format} 位图数据处理失败: {ex.Message}");
        }

        bitmap = null!;
        return false;
    }

    /// <summary>
    /// 位图/文件流/下载字节统一入口：写入临时文件、触发搜索、结束后清理。
    ///
    /// 此前拖放位图、DIB、FileContents、URL、Base64 与剪贴板各复制了一份
    /// 「存临时文件→SearchCore→延迟 1 秒删除」，且删除在主流程之外，
    /// 一旦搜索抛异常临时文件就永久泄漏。这里用 try/finally 保证必删。
    /// （搜索一结束临时文件即可删：结果列表只保留路径字符串，预览按路径实时加载。）
    /// </summary>
    /// <param name="extension">临时文件扩展名（含点）。</param>
    /// <param name="writeAsync">把内容写入给定流的回调。</param>
    private async Task RunTempImageSearchAsync(string extension, Func<Stream, Task> writeAsync)
    {
        var filename = Path.Combine(Path.GetTempPath(), SnowFlake.NewId + extension);
        try
        {
            await using (var stream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await writeAsync(stream);
            }

            Application.Current.Dispatcher.Invoke(() => OnImagePathChanged(filename));
            await SearchCore(filename);
        }
        finally
        {
            try
            {
                if (File.Exists(filename))
                {
                    File.Delete(filename);
                }
            }
            catch
            {
                // 文件被占用时忽略；系统会清理临时目录
            }
        }
    }

    private double _maxThroughput;

    private void OnIndexProgressChanged(object? sender, IndexProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (e.ProcessedFiles > 0)
            {
                IndexSpeed = $"索引速度: {e.Speed:F0} items/s ({e.ThroughputMB:F2}MB/s)";
                UpdateProgressCard(e.ProcessedFiles, e.TotalFiles, e.Filename, e.Speed, e.ThroughputMB);
            }
        });
    }

    private string FormatTimespan(TimeSpan timespan)
    {
        if (timespan.TotalHours >= 1)
        {
            return $"{(int)timespan.TotalHours}h {timespan.Minutes}m {timespan.Seconds}s";
        }
        else if (timespan.TotalMinutes >= 1)
        {
            return $"{(int)timespan.TotalMinutes}m {timespan.Seconds}s";
        }
        else
        {
            return $"{timespan.Seconds}s";
        }
    }

    private void OnIndexCompleted(object? sender, IndexCompletedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // 队列模式下由队列统一汇总，避免每个目录都提示
            if (!IsQueueRunning)
            {
                if (e.Errors.Count > 0)
                {
                    // 有文件失败属于需要用户处理的情况：状态栏提示 + 可查看详情。
                    // 这里仍用弹窗，因为用户必须知道并去检查这些文件。
                    NotifyStatus($"⚠️ 索引创建完成（耗时 {e.ElapsedSeconds:F2}s），{e.Errors.Count} 个文件格式不正确，无法创建索引");
                    var errorDialog = new ErrorsDialog($"索引创建完成，耗时：{e.ElapsedSeconds:F2}s，以下文件格式不正确无法创建索引，请检查：\r\n{string.Join("\r\n", e.Errors)}");
                    errorDialog.ShowDialog();
                }
                else if (e.FilesProcessed > 0)
                {
                    // 单纯的成功提示不再弹窗，改为状态栏显示：
                    // 索引常常是连着做几次的，每次都弹一个「确定」很烦人。
                    NotifyStatus($"✅ 索引创建完成，耗时 {e.ElapsedSeconds:F2}s，处理 {e.FilesProcessed:#,0} 个文件");
                }
            }

            IndexProgressVisibility = Visibility.Collapsed;
            ResetProgressCard();
        });
    }

    /// <summary>
    /// 清空并隐藏进度卡，恢复成「索引」任务的默认外观。
    /// 索引与清理无效索引共用这张卡片，收尾时必须把清理期改动过的部分复原，
    /// 否则下一次索引会顶着「检查速度:」「无吞吐」的错误外观跑。
    /// </summary>
    private void ResetProgressCard()
    {
        IndexProgress = 0;
        IndexProgressText = string.Empty;
        IndexSpeedText = string.Empty;
        IndexThroughputText = string.Empty;
        MaxThroughputText = string.Empty;
        EstimatedRemainingTimeText = string.Empty;
        ProcessingFilename = string.Empty;
        ProgressSpeedLabel = "速度: ";
        ProgressThroughputVisibility = Visibility.Visible;
        _maxThroughput = 0;
        SpeedHistory.Clear();
    }

    /// <summary>
    /// 更新进度卡（索引与清理无效索引共用）。
    /// </summary>
    /// <param name="processed">已完成数量。</param>
    /// <param name="total">总数量。</param>
    /// <param name="current">当前处理的路径。</param>
    /// <param name="speed">每秒处理数量。</param>
    /// <param name="throughputMb">吞吐量 MB/s；清理索引无意义时传 0 并配合隐藏。</param>
    private void UpdateProgressCard(int processed, int total, string current, double speed, double throughputMb)
    {
        if (total <= 0)
        {
            return;
        }

        IndexProgressVisibility = Visibility.Visible;
        ProcessStatus = $"{processed}/{total}";
        IndexProgress = processed * 100.0 / total;
        IndexProgressText = $"{processed:#,0} / {total:#,0}";
        ProcessingFilename = "正在处理：" + current;
        IndexSpeedText = $"{speed:F0} items/s";

        if (ProgressThroughputVisibility == Visibility.Visible)
        {
            IndexThroughputText = $"{throughputMb:F2} MB/s";
            _maxThroughput = Math.Max(throughputMb, _maxThroughput);
            MaxThroughputText = $"{_maxThroughput:F2} MB/s";
        }

        var remaining = total - processed;
        if (remaining > 0 && speed > 0)
        {
            var estimatedSeconds = remaining / speed / 0.9;
            EstimatedRemainingTimeText = FormatTimespan(TimeSpan.FromSeconds(estimatedSeconds));
        }
        else
        {
            EstimatedRemainingTimeText = "--";
        }

        // 队列模式下把百分比同步到当前目录条目
        if (_currentRunningItem != null)
        {
            _currentRunningItem.Message = $"{IndexProgress:F0}%";
        }

        // 速度曲线采样：按总量调整采样密度，避免大索引把点刷爆
        switch (total)
        {
            case <= 1000:
            case <= 10000 when processed % 10 == 0:
            case <= 100000 when processed % 100 == 0:
            case > 100000 when processed % 200 == 0:
                SpeedHistory.Add(ProgressThroughputVisibility == Visibility.Visible ? (float)throughputMb : (float)speed);
                break;
        }
    }

    private void UpdateIndexCount()
    {
        var count = _indexService.Index.Count;

        // 关键区分：**索引正在载入**与**索引确实为空**是两回事。
        //
        // 索引有数百万条时加载要数秒（实测 494 万条 / 1.41GB 需约 9 秒）。
        // 若这期间只按 count==0 显示「请先创建索引」，用户会以为索引丢了，
        // 搜索被拒时也会收到「请先添加文件夹创建索引」这种完全误导的提示——
        // 让人以为数据没了，实际上只是还没读完。
        var loading = !_indexService.IsLoaded;
        IsIndexLoading = loading;

        IndexCount = loading
            ? "正在载入索引…"
            : count > 0 ? $"{count:#,0} 文件" : "请先创建索引";

        // 根据索引总数决定是否显示移除无效索引选项
        ShowRemoveInvalidIndex = count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 根据索引总数决定是否启用搜索配置区域
        IsSearchEnabled = count > 0;
    }

    /// <summary>索引是否仍在载入中（供界面提示与搜索守卫使用）。</summary>
    [ObservableProperty]
    private bool isIndexLoading;

    /// <summary>
    /// 判断文件能否作为搜索查询（图片与视频都支持）。
    ///
    /// 用与索引完全相同的格式清单判定，而不是 MIME 嗅探：
    /// 索引收录的格式（heic/webp/avif 等）中有一部分嗅探不出 image/*，
    /// 只按 MIME 判断会出现"能索引、却搜不了"的不一致。
    /// 视频同样按扩展名清单判断（其 MIME 不是 image，且需先抽帧再比对）。
    /// </summary>
    static bool IsSearchableMedia(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        return MediaFormats.IsImageExtension(path) || VideoFormats.IsVideo(path);
    }

    static bool TryGetImageInfo(string path, out int width, out int height)
    {
        // 视频取缩略图尺寸；缩略图未生成时按「无法加载」处理
        if (VideoFormats.IsVideo(path))
        {
            var thumbnail = VideoThumbnailCache.GetCached(path);
            if (!string.IsNullOrEmpty(thumbnail))
            {
                return ImageDecoder.TryGetImageSize(thumbnail, out width, out height);
            }

            width = 0;
            height = 0;
            return false;
        }

        return ImageDecoder.TryGetImageSize(path, out width, out height);
    }

    /// <summary>构造信息文字；视频会额外标注类型与文件（而非缩略图）大小。</summary>
    private static string BuildMediaInfo(string path)
    {
        var fileInfo = new FileInfo(path);
        var sizeText = fileInfo.Length >= 1048576
            ? $"{fileInfo.Length / 1048576.0:F1}MB"
            : $"{fileInfo.Length / 1024}KB";

        if (VideoFormats.IsVideo(path))
        {
            return TryGetImageInfo(path, out var vw, out var vh)
                ? $"🎬 视频　代表帧：{vw}x{vh}，文件：{sizeText}"
                : $"🎬 视频　文件：{sizeText}（暂无预览帧）";
        }

        return TryGetImageInfo(path, out var w, out var h)
            ? $"分辨率：{w}x{h}，大小：{sizeText}"
            : "无法加载图片信息";
    }

    private void UpdateSourceImageInfo(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            SourceImageInfo = BuildMediaInfo(path);
        }
        catch
        {
            SourceImageInfo = "无法加载图片信息";
        }
    }

    private void UpdateDestImageInfo(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            DestImageInfo = BuildMediaInfo(path);
        }
        catch
        {
            DestImageInfo = "无法加载图片信息";
        }
    }

    private void InitializePerformanceMonitoring()
    {
        try
        {
            _currentProcess = Process.GetCurrentProcess();

            // 初始化当前进程的 CPU 性能计数器
            _cpuCounter = new PerformanceCounter("Process", "% Processor Time", _currentProcess.ProcessName, true);
            _cpuCounter.NextValue(); // 初始化

            // 创建定时器，每秒更新一次
            _performanceTimer = new System.Timers.Timer(1000);
            _performanceTimer.Elapsed += UpdatePerformanceMetrics;
            _performanceTimer.AutoReset = true;
            _performanceTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"性能监测初始化失败: {ex.Message}");
        }
    }

    private void UpdatePerformanceMetrics(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            if (_currentProcess != null)
            {
                // 刷新进程信息
                _currentProcess.Refresh();

                // 获取 CPU 使用率（百分比）
                var cpuUsageValue = _cpuCounter?.NextValue() ?? 0;

                // 获取内存使用量（转换为 MB）
                var memoryUsage = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);

                // 切回 UI 线程更新 UI
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CpuUsage = cpuUsageValue / Environment.ProcessorCount;
                    MemoryUsage = memoryUsage;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新性能指标失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 释放定时器、性能计数器与取消令牌。
    /// 这些字段在构造后于任意线程赋值/使用，故先摘引用再释放，避免释放竞态。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _performanceTimer?.Dispose();
        _cpuCounter?.Dispose();
        _currentProcess?.Dispose();
        _updateIndexTimer?.Dispose();
        _queueCts?.Dispose();
        _cleanupCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
