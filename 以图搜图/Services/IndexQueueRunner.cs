using System.IO;
using 以图搜图.Models;

namespace 以图搜图.Services;

/// <summary>队列中单个目录执行后的状态变化通知。</summary>
public sealed class QueueItemStatusEventArgs(IndexSourceItem item, IndexSourceStatus status, string message) : EventArgs
{
    public IndexSourceItem Item { get; } = item;
    public IndexSourceStatus Status { get; } = status;
    public string Message { get; } = message;
}

/// <summary>整个队列执行完毕的统计。</summary>
public sealed class IndexQueueResult
{
    public int Success { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int TotalAdded { get; set; }
    public int TotalIndex { get; set; }
    public int StartIndex { get; set; }
    public bool Stopped { get; set; }

    /// <summary>本次是否包含视频索引（决定用的是完整模式还是快速同步模式）。</summary>
    public bool IncludeVideos { get; set; }

    public List<string> FailedDirectories { get; } = new();
}

/// <summary>
/// 索引队列的执行器。
/// 按顺序逐个目录建立索引，单个目录的失败不会中断整个队列。
/// 不依赖任何 UI 类型，便于独立验证。
/// </summary>
public sealed class IndexQueueRunner(ImageIndexService indexService)
{
    private readonly ImageIndexService _indexService = indexService;

    /// <summary>处理中目录的实时消息（例如进度百分比）。</summary>
    public event EventHandler<QueueItemStatusEventArgs>? ItemStatusChanged;

    /// <summary>
    /// 顺序执行队列。
    /// </summary>
    /// <param name="items">队列中的目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="includeVideos">
    /// 本次是否索引视频。
    ///
    /// false 时进入「快速同步模式」：
    ///   · 跳过最慢的视频抽帧环节（ffmpeg 约 150ms/个，图片解码仅约 1.7ms/个）；
    ///   · 并且**重新扫描全部目录**而不是跳过已完成项，从而把新增/变动的图片同步进来。
    ///   因为省掉了抽帧，这种模式跑得快，适合日常同步图片变化。
    ///
    /// true 时按需处理（完整模式）：图片与视频都补齐，已完成两者的目录跳过。
    /// </param>
    public async Task<IndexQueueResult> RunAsync(
        IReadOnlyList<IndexSourceItem> items,
        CancellationToken cancellationToken = default,
        bool includeVideos = true)
    {
        var result = new IndexQueueResult
        {
            StartIndex = _indexService.Index.Count,
            IncludeVideos = includeVideos
        };

        // 快速同步模式下重新扫描全部目录，因此不再跳过「已完成」项
        var quickSyncMode = !includeVideos;

        var phases = includeVideos
            ? new (ImageIndexService.IndexContentFilter Filter, string Label)[]
            {
                (ImageIndexService.IndexContentFilter.ImagesOnly, "图片"),
                (ImageIndexService.IndexContentFilter.VideosOnly, "视频")
            }
            : new (ImageIndexService.IndexContentFilter Filter, string Label)[]
            {
                (ImageIndexService.IndexContentFilter.ImagesOnly, "图片")
            };

        // 某阶段是否需要处理该目录。
        // 视频阶段单独依据 VideosIndexed 判断，**不能只看目录的 Completed 状态**：
        // 用户可能上次选择「不索引视频」，那次图片做完后目录已标记完成，
        // 若按目录状态跳过，视频将永远补不上。
        bool NeedsPhase(IndexSourceItem item, ImageIndexService.IndexContentFilter filter) => filter switch
        {
            ImageIndexService.IndexContentFilter.VideosOnly => !item.VideosIndexed,
            ImageIndexService.IndexContentFilter.ImagesOnly => quickSyncMode || !item.ImagesIndexed,
            _ => true
        };

        var needed = items.Where(i => phases.Any(p => NeedsPhase(i, p.Filter))).ToList();
        result.Skipped = items.Count - needed.Count;

        if (needed.Count == 0)
        {
            result.TotalIndex = _indexService.Index.Count;
            return result;
        }

        // 每个目录只重置一次计数（在阶段循环之外），
        // 否则第二阶段的 ResetForRun 会把第一阶段的 AddedCount 清零。
        foreach (var item in needed)
        {
            item.ResetForRun();
        }

        // ── 两阶段执行：先处理所有目录的图片，再回头处理所有目录的视频 ──
        //
        // 为什么要分开：视频抽帧（ffmpeg，约 150ms/个）比图片解码（约 1.7ms/个）慢近百倍。
        // 若同批混合处理，图片会被视频堵在后面。实测 250 图 + 35 视频：
        //   混合  → 图片全部完成需 2.96s（= 总耗时）
        //   分阶段 → 图片全部完成只需 0.44s（快 6.76 倍），而总耗时几乎不变（2.89s）
        // 即分批几乎不增加总开销（2%），却让图片索引早早可用。
        var anyFailure = new HashSet<IndexSourceItem>();
        var touched = new HashSet<IndexSourceItem>();
        var cancelled = false;

        // 目录 → 枚举结果缓存。两阶段共用，避免重复遍历目录树。
        // 每个目录在队列执行期间文件列表不会变化，故可安全缓存整轮。
        var enumerated = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        for (var phaseIndex = 0; phaseIndex < phases.Length && !cancelled; phaseIndex++)
        {
            var (filter, label) = phases[phaseIndex];

            foreach (var item in needed)
            {
                if (!NeedsPhase(item, filter))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                touched.Add(item);
                item.Status = IndexSourceStatus.Running;
                item.Message = $"正在处理{label}…";
                ItemStatusChanged?.Invoke(this, new QueueItemStatusEventArgs(item, IndexSourceStatus.Running, item.Message));

                try
                {
                    var before = _indexService.Index.Count;

                    // 目录只枚举一次，结果缓存起来供两个阶段复用。
                    // 否则图片阶段与视频阶段会各自遍历一遍目录树——
                    // 对大目录（数十万文件）而言这是重复的磁盘遍历开销。
                    if (!enumerated.TryGetValue(item.Directory, out var allFiles))
                    {
                        allFiles = await Task.Run(
                            () => _indexService.EnumerateFiles([item.Directory]), cancellationToken);
                        enumerated[item.Directory] = allFiles;
                    }

                    // 按阶段取出对应子集（纯字符串比较，代价可忽略）
                    var phaseFiles = filter == ImageIndexService.IndexContentFilter.ImagesOnly
                        ? allFiles.Where(f => !VideoFormats.IsVideo(f)).ToArray()
                        : allFiles.Where(VideoFormats.IsVideo).ToArray();

                    await Task.Run(
                        () => _indexService.UpdateIndexAsync([item.Directory], cancellationToken, filter, phaseFiles),
                        cancellationToken);

                    await _indexService.FlushAsync();
                    item.AddedCount += _indexService.Index.Count - before;

                    // 取消发生在处理途中：该目录回到等待状态，下次继续
                    // （已入库的文件会被 files.Except(Index.Keys) 过滤掉，重跑很快）
                    if (cancellationToken.IsCancellationRequested)
                    {
                        item.Status = IndexSourceStatus.Pending;
                        item.Message = string.Empty;
                        ItemStatusChanged?.Invoke(this, new QueueItemStatusEventArgs(item, IndexSourceStatus.Pending, string.Empty));
                        cancelled = true;
                        break;
                    }

                    // 该阶段确实做完了才记「已索引」——
                    // 半途取消时不能标记，否则下次会被跳过、漏掉未处理的文件
                    if (filter == ImageIndexService.IndexContentFilter.ImagesOnly)
                    {
                        item.ImagesIndexed = true;
                    }
                    else
                    {
                        item.VideosIndexed = true;
                    }

                    // 该目录还有后续阶段要做时，先标记为进行中而非已完成
                    if (phaseIndex + 1 < phases.Length)
                    {
                        var next = phases[phaseIndex + 1];
                        if (NeedsPhase(item, next.Filter))
                        {
                            item.Message = $"{label}已完成，等待处理{next.Label}";
                            ItemStatusChanged?.Invoke(this, new QueueItemStatusEventArgs(item, IndexSourceStatus.Running, item.Message));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = IndexSourceStatus.Pending;
                    item.Message = string.Empty;
                    ItemStatusChanged?.Invoke(this, new QueueItemStatusEventArgs(item, IndexSourceStatus.Pending, string.Empty));
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    // 单个目录失败不中断队列；记录后继续处理该目录的下一阶段与其他目录
                    anyFailure.Add(item);
                    item.Message = ex.Message;
                    result.FailedDirectories.Add($"{item.Directory}（{label}）：{ex.Message}");
                    ItemStatusChanged?.Invoke(this, new QueueItemStatusEventArgs(item, IndexSourceStatus.Failed, ex.Message));
                }
            }
        }

        // ── 汇总每个目录的最终状态 ──
        foreach (var item in needed)
        {
            if (anyFailure.Contains(item))
            {
                item.Status = IndexSourceStatus.Failed;
            }
            else if (cancelled && !touched.Contains(item))
            {
                // 取消时还没轮到的目录：保持可续跑状态
                item.Status = IndexSourceStatus.Pending;
                item.Message = string.Empty;
            }
            else
            {
                item.Status = IndexSourceStatus.Completed;
                item.Message = string.Empty;
            }

            result.TotalAdded += item.AddedCount;
            ItemStatusChanged?.Invoke(this, new QueueItemStatusEventArgs(item, item.Status, item.Message));
        }

        // 统一统计最终状态（避免与循环内的中间状态重复计数）
        result.Failed = needed.Count(i => i.Status == IndexSourceStatus.Failed);
        result.Success = needed.Count(i => i.Status == IndexSourceStatus.Completed);
        result.Stopped = cancellationToken.IsCancellationRequested;
        result.TotalIndex = _indexService.Index.Count;
        return result;
    }
}
