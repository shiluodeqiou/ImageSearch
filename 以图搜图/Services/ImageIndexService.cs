using Masuit.Tools;
using Masuit.Tools.Hardware;
using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using Masuit.Tools.Systems;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using 以图搜图.Helpers;
using 以图搜图.Models;

namespace 以图搜图.Services;

public sealed class ImageIndexService : Disposable
{
    private const string IndexFileName = "index.json";
    private const string BackupFileName = "index.json.bak";

    private readonly ConcurrentHashQueue<int> _writeQueue = new();
    private readonly CancellationTokenSource? _cancellationTokenSource;
    private readonly Task? _writeTask;

    /// <summary>
    /// 保护 index.json 的所有读写，让「加载」与「写入」互斥。
    ///
    /// 历史上二者共用一个长期打开的 FileStream 且都不加锁：
    /// 写入方 Seek(0) 会把读取方的位置一起挪走，同时读同时写，
    /// 结果是文件被写成无法解析的垃圾（已实测复现）。
    /// 现在改为「每次操作各自打开文件」+ 本锁串行，双保险。
    /// </summary>
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>
    /// 索引加载任务。任何会修改索引的操作都必须先等它完成，
    /// 否则会把新条目写进空字典、进而覆盖掉磁盘上的原索引（数据丢失）。
    /// </summary>
    private readonly Task _loadTask;

    private static readonly Dictionary<char, DiskMediaInfo> DriveMedia = new();

    /// <summary>盘符到物理磁盘号的映射。</summary>
    private static readonly Dictionary<char, string> DriveDiskIndex = new();

    /// <summary>盘符到磁盘型号的映射（用于诊断）。</summary>
    private static readonly Dictionary<char, string> DriveModel = new();

    public static ImageIndexService Instance { get; }

    static ImageIndexService()
    {
        // 现代存储 API 会一次性给出所有物理磁盘的介质类型，避免逐个盘符做 WMI 遍历
        var mediaByDisk = DiskMediaDetector.DetectAll();
        foreach (var drive in "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Where(drive => Directory.Exists(drive + ":")))
        {
            var (media, model, diskIndex) = DiskMediaDetector.Resolve(mediaByDisk, drive);
            DriveMedia[drive] = media;
            DriveDiskIndex[drive] = diskIndex;
            DriveModel[drive] = model;
        }

        Instance = new ImageIndexService();
    }

    /// <summary>获取盘符所属磁盘的介质类型；未知盘符按机械盘处理（更保守）。</summary>
    private static DiskMediaInfo GetMedia(string path)
    {
        return DriveMedia.TryGetValue(path[0], out var media) ? media : new DiskMediaInfo(DiskMediaType.Unknown, "Unknown");
    }

    /// <summary>是否为机械盘（含无法判定为固态的情况）。</summary>
    private static bool IsHdd(string path)
    {
        return GetMedia(path).Type != DiskMediaType.Ssd;
    }

    /// <summary>
    /// 指定路径所在磁盘是否为旋转介质（机械盘）。
    ///
    /// 供其它需要「按介质决定 IO 策略」的模块使用（例如清理无效索引的并发度）：
    /// 机械盘上高并发只会引发寻道竞争，拖慢一切同时读盘的操作。
    /// 无法判定时返回 true（保守），非盘符开头的路径（UNC 等）也按机械盘处理。
    /// </summary>
    public static bool IsRotationalPath(string path)
    {
        return string.IsNullOrEmpty(path) || IsHdd(path);
    }

    private ImageIndexService()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _writeTask = StartWriteTaskAsync(_cancellationTokenSource.Token);

        // 启动即开始加载，而不是等界面来调用。
        // 这样「任何修改索引的操作」都有一个确定的等待目标，
        // 不会出现「界面还没发起加载，索引操作已经开始写」的空窗。
        _loadTask = Task.Run(LoadIndexCoreAsync);
    }

    private async Task StartWriteTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_writeQueue.TryDequeue(out _))
                {
                    _writeQueue.Clear();
                    await WriteIndexAsync();
                    IndexUpdated?.Invoke(this, EventArgs.Empty);
                }

                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常的取消操作
        }
    }

    public ConcurrentDictionary<string, IndexItem> Index { get; private set; } = new();

    public bool IsIndexing { get; private set; }
    public bool IsWriting { get; private set; }

    public event EventHandler<IndexProgressEventArgs>? ProgressChanged;

    public event EventHandler<IndexCompletedEventArgs>? IndexCompleted;

    public event EventHandler? IndexUpdated;

    /// <summary>索引加载结果。</summary>
    /// <param name="Success">是否成功载入索引。</param>
    /// <param name="Count">载入的条目数。</param>
    /// <param name="SourcePath">实际使用的文件（可能是备份）。</param>
    /// <param name="Error">失败原因；文件本就不存在时为 null。</param>
    /// <param name="UsedFallback">是否用了备份而非主文件。</param>
    public sealed record IndexLoadResult(bool Success, int Count, string? SourcePath, Exception? Error, bool UsedFallback);

    /// <summary>最近一次加载的结果。</summary>
    public IndexLoadResult LoadResult { get; private set; } = new(false, 0, null, null, false);

    /// <summary>
    /// 索引是否已加载完成。失败也算完成 —— 否则调用方会永久等待。
    ///
    /// 直接由加载任务的状态推导，而不是单独维护一个布尔字段：
    /// 该属性会被其它线程读取（如关闭窗口时的校验），
    /// 用 <see cref="Task.IsCompleted"/> 天然线程安全，不会读到陈旧的中间值。
    /// </summary>
    public bool IsLoaded => _loadTask.IsCompleted;

    /// <summary>
    /// 等待索引加载完成。修改索引前必须先等待它（尤其见 <see cref="UpdateIndexAsync"/>）。
    /// </summary>
    public Task LoadIndexAsync() => _loadTask;

    private async Task LoadIndexCoreAsync()
    {
        await _ioLock.WaitAsync();
        try
        {
            // 启动时留一份当日快照，作为「之前还好」的回滚点。
            // 放在加载之前，这样快照记录的是这次会话开始前的状态。
            //
            // 注意：从零建库时此处尚无 index.json，必然留不下快照，
            // 此时返回值是 false，标志保持 false，写入路径会在首次落盘后补上。
            _snapshotEnsured = IndexBackup.CreateDailySnapshotIfNeeded(IndexFileName);

            var (set, source, error, usedFallback) = ReadIndexWithFallback();
            if (set != null)
            {
                Index = set.ToConcurrentDictionary(x => x.FilePath);
                LoadResult = new IndexLoadResult(true, set.Count, source, null, usedFallback);

                if (usedFallback)
                {
                    LogManager.Info($"主索引文件不可用，已从备份载入：{source}（{set.Count} 条）");
                }
            }
            else
            {
                LoadResult = new IndexLoadResult(false, 0, null, error, false);
                if (error != null)
                {
                    LogManager.Error(error);
                }
            }
        }
        catch (Exception ex)
        {
            LoadResult = new IndexLoadResult(false, 0, null, ex, false);
            LogManager.Error(ex);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 按「主文件 → 上一代备份 → 最新每日快照」的顺序尝试载入。
    /// 任一步成功即返回，因此主文件损坏时不会直接变成空库。
    /// </summary>
    private (HashSet<IndexItem>? Set, string? Source, Exception? Error, bool UsedFallback) ReadIndexWithFallback()
    {
        Exception? firstError = null;
        var attempted = 0;

        foreach (var candidate in EnumerateLoadCandidates())
        {
            attempted++;
            try
            {
                var info = new FileInfo(candidate);
                if (!info.Exists || info.Length == 0)
                {
                    continue;
                }

                // 流式解析，不把整个文件读进内存。
                // 加载期间不会有并发写入（由 _ioLock 与「先加载后修改」两道保证），
                // 且文件由 .tmp + 原子替换发布、绝不会处于「写了一半」的状态，
                // 因此无需缓冲整份内容——那样只会让峰值内存凭空多出一个文件大小
                // （实测 43MB 索引多占 44MB，百万级就是数百 MB）。
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                var set = JsonSerializer.Deserialize<HashSet<IndexItem>>(stream);
                if (set != null)
                {
                    return (set, candidate, null, attempted > 1);
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        return (null, null, firstError, false);
    }

    /// <summary>加载候选顺序：主文件 → .bak → 最新的每日快照。</summary>
    private static IEnumerable<string> EnumerateLoadCandidates()
    {
        yield return IndexFileName;
        yield return BackupFileName;

        foreach (var snapshot in IndexBackup.EnumerateSnapshotsNewestFirst())
        {
            yield return snapshot;
        }
    }

    private int _totalCount;
    private long _totalSize;

    /// <summary>
    /// 索引内容过滤：用于把「图片」与「视频」分成两批处理。
    ///
    /// 为什么需要：视频要调用 ffmpeg 抽帧（每个约 150ms），比图片解码（约 1.7ms）慢近百倍。
    /// 若同批混合处理，图片会被视频堵在后面——实测 250 图 + 35 视频的场景中，
    /// 图片全部完成要等到 2.96s（= 总耗时），而分两批处理只需 0.44s（快 6.76 倍），
    /// 而总耗时几乎不变（2.96s → 2.89s）。
    /// </summary>
    public enum IndexContentFilter
    {
        /// <summary>图片与视频一起处理。</summary>
        All,

        /// <summary>只处理图片。</summary>
        ImagesOnly,

        /// <summary>只处理视频。</summary>
        VideosOnly
    }

    /// <summary>按内容过滤判断单个文件是否参与本次索引。</summary>
    private static bool MatchesFilter(string path, IndexContentFilter filter) => filter switch
    {
        IndexContentFilter.ImagesOnly => !VideoFormats.IsVideo(path),
        IndexContentFilter.VideosOnly => VideoFormats.IsVideo(path),
        _ => true
    };

    /// <summary>
    /// 对指定目录建立/更新索引。
    /// 队列模式下按目录逐个调用，因此每次调用都是一个独立的、可统计的执行单元。
    /// 注意：这里只做索引，不做任何删除；无效索引的清理由 InvalidIndexScanner 单独负责。
    /// </summary>
    /// <param name="directories">要索引的目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="filter">只处理某一类内容（图片或视频），默认全部处理。</param>
    /// <param name="preEnumeratedFiles">
    /// 已枚举好的文件列表。传入则跳过目录枚举。
    /// 用于索引队列的两阶段执行——队列先枚举一次，再把图片/视频子集分别交给两个阶段，
    /// 避免对同一目录做两次全树遍历（大目录上代价可观）。
    /// </param>
    /// <returns>本次执行的结果统计。</returns>
    public async Task<IndexCompletedEventArgs> UpdateIndexAsync(
        string[] directories,
        CancellationToken cancellationToken = default,
        IndexContentFilter filter = IndexContentFilter.All,
        string[]? preEnumeratedFiles = null)
    {
        // ★ 必须先等索引加载完成，再碰索引。
        //
        // 否则会出现「启动后立刻点开始队列 → 原索引库丢失」：
        // 加载尚未完成时 Index 还是空字典，新条目写进去之后一落盘，
        // 磁盘上的原索引就被这一批新条目整个覆盖掉了。
        // （实测该场景下原 20 万条索引彻底丢失，且当时读写共用同一个流，
        //   文件还会被写坏到完全无法解析 —— 现在这条等待是硬性前置条件。）
        await _loadTask;

        // 优先复用调用方已枚举的结果；否则自行枚举
        var files = preEnumeratedFiles ?? GetFiles(directories);

        // 过滤下推的顺序很重要：
        //   先按内容类型筛图片/视频（字符串比较，极廉价），
        //   再按扩展名筛媒体文件，最后才与已有索引比对。
        // 这样进入后续阶段的数据量最小。
        var candidates = filter == IndexContentFilter.All
            ? files
            : files.Where(s => MatchesFilter(s, filter)).ToArray();

        Interlocked.Exchange(ref _totalCount, 0);
        Interlocked.Exchange(ref _totalSize, 0);
        IsIndexing = true;

        var filesToIndex = candidates
            .Where(s => Regex.IsMatch(s, MediaFormats.RegexPattern, RegexOptions.IgnoreCase))
            .Except(Index.Keys)
            .Order()
            .ToArray();
        if (filesToIndex.Length == 0)
        {
            IsIndexing = false;
            var empty = new IndexCompletedEventArgs();
            OnIndexCompleted(empty);
            return empty;
        }

        var errors = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();

        try
        {
            // 不吞异常：调用方（索引队列）需要感知单个目录的失败以便隔离处理
            await Task.Run(() =>
            {
                Parallel.Invoke(
                    () => UpdateIndexOnSSD(filesToIndex, sw, errors, cancellationToken),
                    () => UpdateIndexOnHDD(filesToIndex, sw, errors, cancellationToken));
                if (Volatile.Read(ref _totalCount) > 0)
                {
                    _writeQueue.Enqueue(1);
                }
            }, cancellationToken);
        }
        finally
        {
            sw.Stop();
            IsIndexing = false;
        }
        var result = new IndexCompletedEventArgs
        {
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
            FilesProcessed = Volatile.Read(ref _totalCount),
            Errors = errors.ToList()
        };
        OnIndexCompleted(result);
        return result;
    }

    private void UpdateIndexOnSSD(string[] filesToIndex, Stopwatch sw, ConcurrentBag<string> errors, CancellationToken cancellationToken)
    {
        // 解码与哈希是计算密集任务，并发度按物理核而非逻辑核来定。
        // 原先用 ProcessorCount * 4 会严重过度订阅（8 核机上达 64 个线程），
        // 实测反而比并发 8 慢 50% 以上。
        var parallelism = CpuInfo.RecommendedComputeConcurrency;
        filesToIndex.Where(s => !IsHdd(s)).Chunk(parallelism).AsParallel().WithDegreeOfParallelism(parallelism).WithCancellation(cancellationToken).ForAll(g =>
        {
            foreach (var file in g.Where(File.Exists).TakeWhile(_ => IsIndexing && !cancellationToken.IsCancellationRequested))
            {
                try
                {
                    using var image = ImageDecoder.DecodeGrayThumb(file, 160);
                    var indexItem = new IndexItem(file)
                    {
                        DctHash = image.DctHash(),
                        DifferenceHash = image.DifferenceHash256(),
                        DctHash64 = image.DctHash64()
                    };
                    Index[file] = indexItem;

                    var size = new FileInfo(file).Length;
                    Interlocked.Increment(ref _totalCount);
                    Interlocked.Add(ref _totalSize, size);

                    OnProgressChanged(new IndexProgressEventArgs
                    {
                        Filename = file,
                        Message = $"{Volatile.Read(ref _totalCount)}/{filesToIndex.Length}",
                        Speed = Volatile.Read(ref _totalCount) / sw.Elapsed.TotalSeconds,
                        ThroughputMB = Volatile.Read(ref _totalSize) / 1048576.0 / sw.Elapsed.TotalSeconds,
                        ProcessedFiles = Volatile.Read(ref _totalCount),
                        TotalFiles = filesToIndex.Length
                    });
                }
                catch (NonMediaFileException)
                {
                    // 扩展名像媒体但实际不是（如 .mts 的 TypeScript 文件）：
                    // 静默跳过，不视为解码失败——它本来就不是要索引的东西
                }
                catch
                {
                    errors.Add(file);
                }
            }
        });
    }

    private void UpdateIndexOnHDD(string[] filesToIndex, Stopwatch sw, ConcurrentBag<string> errors, CancellationToken cancellationToken)
    {
        // 队列项：Stream 为 null 表示「不预读」，用于视频——
        // 视频由 ffmpeg 自行顺序读取，预读进内存既无必要也浪费内存
        var queue = new ConcurrentQueue<(string Path, MemoryStream? Stream, long Length)>();
        long queuedBytes = 0;
        bool loading = true;

        Task.Run(() =>
        {
            try
            {
                var memoryAvailable = Math.Min(RamInfo.Local.MemoryAvailable / 2, 8589934592d);

                // 把一个文件加入处理队列。
                //   图片：整文件预读进内存，让机械盘保持顺序访问（磁头不回跳）。
                //   视频：不预读，只登记路径——抽帧由 ffmpeg 自己读文件，
                //         预读会白白占用与视频等量的内存。
                void EnqueueFile(string file)
                {
                    if (VideoFormats.IsVideo(file))
                    {
                        queue.Enqueue((file, null, new FileInfo(file).Length));
                        return;
                    }

                    var stream = new MemoryStream(File.ReadAllBytes(file));
                    var length = stream.Length;
                    queue.Enqueue((file, stream, length));
                    Interlocked.Add(ref queuedBytes, length);

                    // 背压：内存占用超过预算时等待消费端消化
                    while (Volatile.Read(ref queuedBytes) > memoryAvailable
                           && !cancellationToken.IsCancellationRequested)
                    {
                        Thread.Sleep(200);
                    }
                }

                var diskCount = DriveMedia.Where(kv => kv.Value.Type == DiskMediaType.Hdd)
                    .Select(kv => DriveDiskIndex.GetValueOrDefault(kv.Key, "Unknown"))
                    .Distinct().Count();
                // default 与 case 1 共用「单盘顺序读取」的实现。
                // 这不是凑数：diskCount 统计的是**介质被判定为 Hdd** 的物理盘数，
                // 而 IsHdd() 对「无法判定」的盘也返回 true（保守起见按机械盘处理）。
                // 当索引目录位于网络盘/映射盘，或盘符在程序启动后才出现时，
                // 介质判不出来 → diskCount 为 0，但这些文件仍然会进入本方法。
                // 若 switch 没有 default，它们既不匹配 case 1 也不匹配 case >1，
                // 会被**静默跳过**、永远不进索引；表现为「索引跑完了但文件数为 0」。
                switch (diskCount)
                {
                    case 1:
                    default:
                        foreach (var file in filesToIndex.Where(IsHdd).Order().TakeWhile(_ => IsIndexing && !cancellationToken.IsCancellationRequested).Where(File.Exists))
                        {
                            try
                            {
                                EnqueueFile(file);
                            }
                            catch
                            {
                                errors.Add(file);
                            }
                        }

                        break;

                    case > 1:
                        filesToIndex.Where(IsHdd).GroupBy(s => DriveDiskIndex.GetValueOrDefault(s[0], "Unknown")).AsParallel().WithDegreeOfParallelism(diskCount).ForAll(grouping =>
                        {
                            foreach (var file in grouping.Order().TakeWhile(_ => IsIndexing && !cancellationToken.IsCancellationRequested).Where(File.Exists))
                            {
                                try
                                {
                                    EnqueueFile(file);
                                }
                                catch
                                {
                                    errors.Add(file);
                                }
                            }
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(ex);
            }
            finally
            {
                // 无论正常结束、取消还是异常，都必须复位，否则下面的消费循环会一直等待
                loading = false;
            }
        }, cancellationToken).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                LogManager.Error(t.Exception);
            }
        }, TaskScheduler.Default);

        while (loading || queue.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 队列暂时为空（生产者还在枚举/顺序读盘）时必须让出 CPU：
            // 否则 Parallel.For(0, 0) 一次迭代都不做，循环立即重入，
            // 生产者存活期间会把一个核 100% 空转（纯耗电，不产出）。
            if (queue.Count == 0)
            {
                Thread.Sleep(15);
                continue;
            }

            Parallel.For(0, Math.Min(CpuInfo.RecommendedComputeConcurrency, queue.Count), _ =>
            {
                if (!queue.TryDequeue(out var item))
                {
                    return;
                }

                // 只回收「本项预读占用的内存」，必须与生产者的记账严格对称。
                // 生产者只为**已预读进内存的图片**累加 queuedBytes；视频项 Stream 为 null，
                // 生产者没有为它加过任何数。若这里按 item.Length（视频=文件大小）一律相减，
                // queuedBytes 会被每个视频扣成负数并不断累积负漂移，
                // 于是 `queuedBytes > memoryAvailable` 这个内存上限判断逐步失效——
                // 视频越多，允许预读进内存的图片就越多，最终可能耗尽内存。
                Interlocked.Add(ref queuedBytes, -(item.Stream?.Length ?? 0));

                try
                {
                    // 视频走路径解码（内部先抽帧再解码）；图片优先用已预读的流
                    using var image = item.Stream != null
                        ? ImageDecoder.DecodeGrayThumb(item.Stream, 160)
                        : ImageDecoder.DecodeGrayThumb(item.Path, 160);

                    var indexItem = new IndexItem(item.Path)
                    {
                        DctHash = image.DctHash(),
                        DifferenceHash = image.DifferenceHash256(),
                        DctHash64 = image.DctHash64()
                    };
                    Index[item.Path] = indexItem;

                    Interlocked.Increment(ref _totalCount);
                    Interlocked.Add(ref _totalSize, item.Length);
                    OnProgressChanged(new IndexProgressEventArgs
                    {
                        Filename = item.Path,
                        Message = $"{Volatile.Read(ref _totalCount)}/{filesToIndex.Length}",
                        Speed = Volatile.Read(ref _totalCount) / sw.Elapsed.TotalSeconds,
                        ThroughputMB = Volatile.Read(ref _totalSize) / 1048576.0 / sw.Elapsed.TotalSeconds,
                        ProcessedFiles = Volatile.Read(ref _totalCount),
                        TotalFiles = filesToIndex.Length
                    });
                }
                catch (NonMediaFileException)
                {
                    // 同 SSD 路径：确认不是媒体，静默跳过
                }
                catch (Exception ex)
                {
                    errors.Add(item.Path);
                    LogManager.Error(ex);
                }
                finally
                {
                    // 视频项没有预读流（Stream 为 null）
                    item.Stream?.Dispose();
                }
            });
        }
    }

    public void StopIndexing()
    {
        IsIndexing = false;
    }

    /// <summary>
    /// 立即将当前索引写入磁盘，并等待写入完成。
    /// 完成后触发 <see cref="IndexUpdated"/>，让界面刷新索引计数——
    /// 索引队列分阶段执行（先图片后视频）时，图片阶段一结束计数就应更新，
    /// 否则用户要等到整个队列跑完才看到变化。
    /// </summary>
    public async Task FlushAsync()
    {
        _writeQueue.Clear();
        await WriteIndexAsync();
        IndexUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveFromIndex(string path)
    {
        Index.TryRemove(path, out _);
        _writeQueue.Enqueue(1);
    }

    /// <summary>
    /// 批量移除索引条目并等待落盘。
    /// 仅用于已确认文件不存在的路径（见 InvalidIndexScanner）。
    /// </summary>
    public async Task<int> RemoveManyFromIndexAsync(IEnumerable<string> paths)
    {
        var removed = 0;
        foreach (var path in paths)
        {
            if (Index.TryRemove(path, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            await FlushAsync();
        }

        return removed;
    }

    public IEnumerable<string> GetIndexedPaths()
    {
        return Index.Keys;
    }

    /// <summary>
    /// 枚举指定目录下的全部文件（不含类型过滤）。
    ///
    /// 供索引队列使用：队列在开始两阶段执行前调用一次，
    /// 把结果分别交给图片阶段与视频阶段，避免重复遍历目录树。
    /// </summary>
    public string[] EnumerateFiles(string[] directories) => GetFiles(directories);

    private static string[] GetFiles(string[] directories)
    {
        var files = EnumerateRaw(directories);

        // 回收站里的文件不索引：那是用户已经删掉的东西，
        // 索引进来会让搜索结果里冒出「以为早就删了」的文件，
        // 而且随时会被系统清空、很快变成无效条目。
        return files.Where(static f => !PathExclusions.IsInRecycleBin(f)).ToArray();
    }

    private static string[] EnumerateRaw(string[] directories)
    {
        if (File.Exists("Everything64.dll") && Process.GetProcessesByName("Everything").Length > 0)
        {
            return directories.SelectMany(s =>
            {
                var array = EverythingHelper.EnumerateFiles(s).ToArray();
                return array.Length == 0 ? Directory.GetFiles(s, "*", SearchOption.AllDirectories) : array;
            }).ToArray();
        }

        return directories.SelectMany(static s =>
        {
            try
            {
                return Directory.GetFiles(s, "*", SearchOption.AllDirectories);
            }
            catch
            {
                return [];
            }
        }).ToArray();
    }

    private async Task WriteIndexAsync()
    {
        await _ioLock.WaitAsync();
        IsWriting = true;
        try
        {
            var target = Path.GetFullPath(IndexFileName);
            var dir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(dir);

            // 先写临时文件：即便写到一半崩溃/断电，损坏的也只是临时文件，
            // index.json 仍是上一次的完整内容。旧实现是在原文件上 Seek(0) 覆盖写，
            // 中途崩溃会直接留下一个残缺的索引库。
            var temp = Path.Combine(dir, IndexFileName + ".tmp");
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, Index.Values);
                await stream.FlushAsync();
            }

            PublishIndexFile(temp, target, Path.Combine(dir, BackupFileName));

            // 首次建立索引的那次会话需要额外照顾：
            // 启动时若 index.json 还不存在（全新库），加载阶段留不下快照；
            // 而第一次写入走 File.Move（目标不存在，File.Replace 用不上），也就没有 .bak。
            // 结果是最需要保护的「从零建库」全程没有任何备份——
            // 一旦此时文件损坏，恢复链是空的。
            // 因此在首次成功写入后补一份快照（每次会话最多检查一次，代价仅一次校验+复制）。
            if (!_snapshotEnsured)
            {
                _snapshotEnsured = IndexBackup.CreateDailySnapshotIfNeeded(IndexFileName);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
        finally
        {
            IsWriting = false;
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 用临时文件原子替换 index.json，并把替换前的版本留作 .bak。
    ///
    /// File.Replace 在同一卷上是原子的：任何时刻 index.json 要么是完整旧内容、
    /// 要么是完整新内容，不存在「写了一半」的中间态；同时旧内容自动成为备份，
    /// 不需要额外复制一遍（那是 2 倍写入量）。
    /// </summary>
    /// <summary>
    /// 本次会话是否已经确保过「当日快照」。用于避免每次都做一次校验+复制，
    /// 同时保证「从零建库」这类加载期留不下快照的场景，在首次写入后能补上一份。
    /// </summary>
    private bool _snapshotEnsured;

    private static void PublishIndexFile(string tempPath, string targetPath, string backupPath)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex)
            {
                // 目标被其它进程占用（杀毒软件扫描、另一个实例等）时退回普通覆盖。
                // 此时不会更新 .bak，备份链会「跳过一代」——
                // 必须记日志，否则事后无法判断备份为何不是上一版内容。
                LogManager.Info($"索引原子替换失败，退回普通覆盖（本次不更新 .bak）：{ex.Message}");
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    private void OnProgressChanged(IndexProgressEventArgs e)
    {
        if (e.ProcessedFiles % 1000 == 0)
        {
            _writeQueue.Enqueue(1);
        }

        ProgressChanged?.Invoke(this, e);
    }

    private void OnIndexCompleted(IndexCompletedEventArgs e)
    {
        IndexCompleted?.Invoke(this, e);
    }

    /// <summary>释放</summary>
    /// <param name="disposing"></param>
    public override void Dispose(bool disposing)
    {
        // 停止后台任务
        _cancellationTokenSource?.Cancel();
        try
        {
            _writeTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // 预期的异常
        }

        // 清理事件订阅
        ProgressChanged = null;
        IndexCompleted = null;
        IndexUpdated = null;

        // 清理数据
        Index?.Clear();

        // 清理令牌源
        _cancellationTokenSource?.Dispose();
        _ioLock.Dispose();
    }
}

public record IndexItem(string FilePath)
{
    public ulong[] DifferenceHash { get; set; }
    public ulong DctHash { get; set; }
    public ulong DctHash64 { get; set; }
}
