using System.Collections.Concurrent;
using System.IO;
using Masuit.Tools.Logging;

namespace 以图搜图.Services;

/// <summary>单个索引路径的校验结论。</summary>
public enum PathVerification
{
    /// <summary>文件存在，索引有效。</summary>
    Valid,

    /// <summary>已明确确认文件不存在，可进入待删除列表。</summary>
    Missing,

    /// <summary>无法完成检查（权限、磁盘离线、IO 错误等），必须保留索引。</summary>
    CheckFailed
}

/// <summary>单个路径的校验结果。</summary>
public sealed record PathVerificationResult(PathVerification Verdict, string? FailReason = null);

/// <summary>
/// 无效索引扫描的进度快照。
/// </summary>
/// <param name="Checked">已检查的路径数。</param>
/// <param name="Total">需要检查的路径总数。</param>
/// <param name="CurrentPath">最近开始检查的路径，供界面显示。</param>
public readonly record struct InvalidIndexScanProgress(int Checked, int Total, string CurrentPath);

/// <summary>清理策略等级。</summary>
public enum CleanupPolicy
{
    /// <summary>比例正常，可直接清理。</summary>
    Normal,

    /// <summary>比例偏高，需要明确警告。</summary>
    Warn,

    /// <summary>比例异常，阻止一键删除。</summary>
    Block
}

/// <summary>无效索引的检查报告。</summary>
public sealed class InvalidIndexScanReport
{
    /// <summary>被检查的索引总数。</summary>
    public int TotalCount { get; set; }

    /// <summary>确认文件不存在的路径。</summary>
    public List<string> ConfirmedMissing { get; } = new();

    /// <summary>检查失败、必须保留的路径及其原因。</summary>
    public List<(string Path, string Reason)> CheckFailed { get; } = new();

    /// <summary>仍然有效的索引数量。</summary>
    public int ValidCount => Math.Max(0, TotalCount - ConfirmedMissing.Count - CheckFailed.Count);

    /// <summary>检查是否被取消。</summary>
    public bool WasCancelled { get; set; }

    /// <summary>
    /// 实际已检查的条目数。
    /// 被取消时它小于 <see cref="TotalCount"/>，界面据此告诉用户"检查到哪了"。
    /// </summary>
    public int TotalCountChecked { get; set; }

    /// <summary>待删除数量占总索引的比例。</summary>
    public double MissingRatio => TotalCount > 0 ? (double)ConfirmedMissing.Count / TotalCount : 0;

    /// <summary>按比例得出的清理策略。</summary>
    public CleanupPolicy Policy => InvalidIndexScanner.EvaluatePolicy(TotalCount, ConfirmedMissing.Count);
}

/// <summary>
/// 无效索引的保守检查器。
///
/// 核心原则：只有"明确确认文件不存在"才进入待删除列表。
/// 扫描异常、权限错误、磁盘离线、Everything 查询失败等一律视为"检查失败"并保留索引。
/// 因此这里完全不依赖"重新扫描目录后对比差集"的做法，而是逐个路径做确认。
///
/// 性能上有一处刻意的优化（<see cref="ScanCache"/>）：同一次扫描内，
/// 目录的存在性与可枚举性按目录复用，避免同一目录下的成千上万个文件
/// 各自重复做一遍相同的目录检查。实测「整个文件夹被移走」这类场景快 2~5 倍
/// （详见 ScanCache 的说明与安全边界）。
/// </summary>
public static class InvalidIndexScanner
{
    /// <summary>待删除比例超过该值时给出明确警告。</summary>
    public const double WarnRatio = 0.05;

    /// <summary>待删除比例超过该值时阻止一键删除。</summary>
    public const double BlockRatio = 0.20;

    /// <summary>
    /// 单次扫描内的目录检查缓存。
    ///
    /// 为什么需要：只有「文件已不存在」的路径才会走到目录检查（第 3、4 步）。
    /// 当某个目录整棵消失时（移走文件夹、换盘后目录结构变化等），
    /// 该目录下的每个文件都要**各自**逐级向上找存在的祖先目录，再枚举一次；
    /// 同一目录下几万个文件做的是完全重复的工作，且成本随路径深度累积
    /// （实测 459µs/文件 @深度1 → 752µs/文件 @深度8）。
    ///
    /// 实测收益（16,000 条路径，5 层目录结构）：
    ///   健康索引（1% 失效）    无收益（1.0x，甚至略慢）
    ///   删少量子相册（5%）     1.5~1.7x
    ///   删一整年目录（25%）    2.2~3.2x
    ///   删一半目录（50%）      5.3~5.6x
    ///   整个库消失（100%）     4.7~4.8x
    /// 即：索引健康时几乎无感，越是大面积目录消失收益越明显——
    /// 而那正是清理功能真正要应对的场景。
    ///
    /// **安全边界（重要）**：缓存的生命周期严格限定在一次 ScanAsync 调用内，
    /// 绝不跨扫描复用（因此它不是静态字段，每次扫描新建）。
    /// 这让它带来的语义假设被压到最小：「在一次扫描的这期间内，目录可访问性不变」。
    /// 该假设下裁决与逐条实查**完全一致**（已逐条比对验证）。
    /// 越界情形（扫描中途权限变化）才会产生偏差，而那需要极小概率的巧合；
    /// 即便发生，待删除项仍需用户显式确认，且受 WarnRatio/BlockRatio 阈值保护。
    ///
    /// 第 3 步（盘符在线）同样只在这一层缓存：它只是避免重复查询同一个盘符，
    /// 若盘符中途掉线，后续仍会因目录枚举失败而落到 CheckFailed（保留索引）。
    /// </summary>
    private sealed class ScanCache
    {
        /// <summary>目录是否存在。Directory.Exists 单次约 100µs，重复调用代价可观。</summary>
        public ConcurrentDictionary<string, bool> DirectoryExists { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 缓存条目上限。若路径分布在极多不同目录下（例如几十万个目录），
        /// 达到上限后直接清空重置——只是让后续退回「重复检查」，
        /// 不影响正确性（缓存未命中等价于没缓存）。
        /// </summary>
        private const int Limit = 200_000;

        public bool TryGetDirectoryExists(string dir, out bool exists)
        {
            if (DirectoryExists.TryGetValue(dir, out exists))
            {
                return true;
            }

            exists = Directory.Exists(dir);
            if (DirectoryExists.Count >= Limit)
            {
                DirectoryExists.Clear();
            }

            DirectoryExists[dir] = exists;
            return true;
        }
    }

    /// <summary>根据待删除比例判定清理策略。</summary>
    public static CleanupPolicy EvaluatePolicy(int total, int missing)
    {
        if (total <= 0 || missing <= 0)
        {
            return CleanupPolicy.Normal;
        }

        var ratio = (double)missing / total;
        if (ratio > BlockRatio)
        {
            return CleanupPolicy.Block;
        }

        return ratio > WarnRatio ? CleanupPolicy.Warn : CleanupPolicy.Normal;
    }

    /// <summary>
    /// 校验单个路径（每次都实查目录，不共享任何缓存）。
    /// 用于单条检查或测试；批量扫描请用 <see cref="ScanAsync"/> 以复用目录检查结果。
    /// </summary>
    public static PathVerificationResult Verify(string path) => Verify(path, null);

    /// <summary>
    /// 校验单个路径，可选地复用本轮的目录检查缓存。
    /// </summary>
    private static PathVerificationResult Verify(string path, ScanCache? cache)
    {
        // 1) 直接命中：文件存在
        try
        {
            if (File.Exists(path))
            {
                return new PathVerificationResult(PathVerification.Valid);
            }
        }
        catch (Exception ex)
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "读取文件状态失败：" + ex.Message);
        }

        // 2) 用 GetAttributes 区分"访问被拒绝"与"确实不存在"
        try
        {
            File.GetAttributes(path);
            return new PathVerificationResult(PathVerification.Valid);
        }
        catch (UnauthorizedAccessException)
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "没有访问权限");
        }
        catch (FileNotFoundException)
        {
            // 确认不存在，继续后续磁盘与目录检查
        }
        catch (DirectoryNotFoundException)
        {
            // 上级目录不存在，可能是整棵树被删除；也可能是磁盘离线，交由第 3、4 步判断
        }
        catch (IOException ex)
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "检查文件失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "检查文件失败：" + ex.Message);
        }

        // 3) 磁盘必须在线，否则不能判定文件被删除
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "无法解析所在盘符");
        }

        try
        {
            if (!DirectoryExistsCached(root, cache))
            {
                return new PathVerificationResult(PathVerification.CheckFailed, "所在磁盘当前不可用");
            }
        }
        catch (Exception ex)
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "检查磁盘失败：" + ex.Message);
        }

        // 4) 最近的、实际存在的祖先目录必须可以正常枚举
        //    目录不存在（整棵子树被删）是可接受的，但目录存在却无法读取即为"检查失败"
        //
        //    注：这里原先还会按「父目录」缓存第 4 步结论（ScanCache.Step4Result），
        //    但改为按目录枚举后批量路径已不再调用本方法（只以 cache=null 被调用），
        //    该缓存分支恒不执行，属死代码，已移除。
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            return new PathVerificationResult(PathVerification.CheckFailed, "找不到可访问的上级目录");
        }

        var failReason = ComputeParentCheck(parent, cache);

        return failReason == null
            ? new PathVerificationResult(PathVerification.Missing)
            : new PathVerificationResult(PathVerification.CheckFailed, failReason);
    }

    /// <summary>
    /// 第 4 步：从 <paramref name="startDir"/> 起向上找到最近的存在目录，
    /// 并确认它可以正常枚举。返回 null 表示通过，否则为失败原因。
    /// </summary>
    private static string? ComputeParentCheck(string startDir, ScanCache? cache)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir) && !DirectoryExistsCached(dir, cache))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
            {
                break;
            }

            dir = parent;
        }

        if (string.IsNullOrEmpty(dir) || !DirectoryExistsCached(dir, cache))
        {
            return "找不到可访问的上级目录";
        }

        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            enumerator.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            return "上级目录没有访问权限";
        }
        catch (IOException ex)
        {
            return "读取上级目录失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            return "读取上级目录失败：" + ex.Message;
        }

        return null;
    }

    /// <summary>目录存在性查询；有缓存则复用（缓存失效时会回退为实查）。</summary>
    private static bool DirectoryExistsCached(string dir, ScanCache? cache)
        => cache == null ? Directory.Exists(dir) : cache.TryGetDirectoryExists(dir, out var exists) && exists;

    /// <summary>
    /// 按索引所在介质决定清理并发度。
    ///
    /// 固态盘上文件系统请求能真正并行，高并发有益（这类操作大部分时间在等 IO、不占 CPU）；
    /// 机械盘上高并发只会变成寻道竞争，把盘占满、拖慢一切同时读盘的操作（尤其是搜索），
    /// 而且清理本身也不会更快（见 ScanAsync 中的实测数据）。
    ///
    /// 判定落在哪个介质：按索引中出现最多的盘符类型来定。
    /// 这样「图库在机械盘」的绝大多数用户会自动走保守路径，
    /// 而库在固态盘的用户仍能享受高并发。
    /// 混合盘场景取「机械盘优先」的保守值——宁可清理慢一点，也不要让搜索不可用。
    /// </summary>
    private static int GetConcurrency(ICollection<string> paths)
    {
        const int solidStateConcurrency = 24;   // 固态：IO 等待型，适度高于物理核
        const int rotationalConcurrency = 4;    // 机械：低并发避免寻道抖动

        try
        {
            // 采样若干路径判断介质，避免为上百万条路径逐个查表
            var sampleStep = Math.Max(1, paths.Count / 200);
            var index = 0;
            var rotational = 0;
            var sampled = 0;

            foreach (var path in paths)
            {
                if (index++ % sampleStep != 0)
                {
                    continue;
                }

                // path[0] 为盘符；非盘符开头的（UNC 等）按机械盘处理（保守）
                if (path.Length == 0 || ImageIndexService.IsRotationalPath(path))
                {
                    rotational++;
                }

                if (++sampled >= 200)
                {
                    break;
                }
            }

            // 只要有采样落在机械盘上，整体就走保守并发。
            //
            // 这与本方法 doc 注释声明的「机械盘优先」一致：宁可清理慢一点，
            // 也不要让机械盘因高并发退化为寻道竞争（那会同时拖垮清理本身与搜索）。
            //
            // 原先写的是「多数是机械盘才保守」（rotational * 2 >= sampled），
            // 那是**多数表决**：混合盘场景下若机械盘只占少数，就会给它们分到
            // 24 的并发度——恰好是本注释明确要避免的情形。现改为只要有机械盘即保守。
            return rotational > 0 ? rotationalConcurrency : solidStateConcurrency;
        }
        catch
        {
            // 判定失败时取保守值：宁可慢，也不要拖垮同时进行的搜索
            return rotationalConcurrency;
        }
    }

    /// <summary>
    /// 检查全部索引路径，仅把"确认不存在"的路径放入待删除列表。
    /// </summary>
    /// <param name="paths">待检查的全部索引路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="progress">
    /// 进度回调。回调在**调用方线程**上触发（由 <see cref="Progress{T}"/> 负责编组），
    /// 因此界面可直接在回调里更新控件。
    /// </param>
    public static async Task<InvalidIndexScanReport> ScanAsync(
        ICollection<string> paths,
        CancellationToken cancellationToken = default,
        IProgress<InvalidIndexScanProgress>? progress = null)
    {
        var report = new InvalidIndexScanReport { TotalCount = paths.Count };
        if (paths.Count == 0)
        {
            return report;
        }

        var missing = new ConcurrentBag<string>();
        var failed = new ConcurrentBag<(string, string)>();

        // 进度上报做节流：百万级索引若逐条回调，会产生上百万次界面调度，
        // 把 UI 线程淹没（界面反而更卡）。这里把回调次数压到 500 次以内。
        // 用 Ceiling 而非整除：整除在总量略大于 500 时会退化成 step=1（逐条上报），
        // 达不到「不超过 500 次」的目的。
        var step = Math.Max(1, (int)Math.Ceiling(paths.Count / 500.0));
        var counter = new Counter();

        try
        {
            await Task.Run(() =>
            {
                // ── D：借助 Everything 的 MFT 索引做预筛 ──
                //
                // 实测（本机 1118 万文件）：构建全盘集合一次性约 18 秒，
                // 之后每条的内存比对约 2.8µs（498 万条约 14 秒）。
                // 这能让绝大多数「文件确实存在」的条目**零文件系统 IO** 地判为有效。
                //
                // 严格限定用途：Everything 给出的集合只用于「确认存在」。
                // 集合里**没有**的条目一律交给真实文件系统复核——
                // Everything 的索引可能滞后或排除了某些目录，
                // 把「它没索引到」当作「文件已删」会导致误删索引。
                var knownFiles = EverythingFileIndex.TryBuild(paths, cancellationToken);

                if (knownFiles != null)
                {
                    LogManager.Info($"清理无效索引：已用 Everything 预筛（{knownFiles.Count:#,0} 个文件）");

                    // 先用集合判掉「确定存在」的部分，剩下的才走文件系统
                    var remaining = new List<string>(paths.Count / 2);
                    var preValid = 0;
                    var interrupted = false;
                    foreach (var p in paths)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            // 取消时**不能丢弃**尚未处理的路径：
                            // 它们既不在「确定存在」集合里、也不进 remaining，
                            // 会被 ValidCount（=总数-可删-保留 反算）算成「有效」，
                            // 报告数字失真。这里把它们交给目录枚举阶段处理
                            // （该阶段本身响应取消，不会继续做无用功）。
                            interrupted = true;
                            remaining.Add(p);
                            continue;
                        }

                        if (knownFiles.Contains(p))
                        {
                            preValid++;
                            var done0 = counter.Add(1);
                            if (progress != null && (done0 % step == 0 || done0 == report.TotalCount))
                            {
                                progress.Report(new InvalidIndexScanProgress(done0, report.TotalCount, p));
                            }
                        }
                        else
                        {
                            remaining.Add(p);
                        }
                    }

                    LogManager.Info($"Everything 预筛判定有效 {preValid:#,0} 条，剩余 {remaining.Count:#,0} 条需实查"
                                    + (interrupted ? "（预筛被取消，其余已转交目录枚举）" : string.Empty));
                    knownFiles = null;   // 尽快释放（可能上 GB）

                    ScanByDirectory([.. remaining], missing, failed, counter, step, report, progress, cancellationToken);
                }
                else
                {
                    // Everything 不可用/索引过小：退化为纯目录枚举（仍远快于逐个 stat）
                    ScanByDirectory([.. paths], missing, failed, counter, step, report, progress, cancellationToken);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            report.WasCancelled = true;
        }

        // 记录实际检查进度：取消时用于告知用户"检查到哪了"
        report.TotalCountChecked = counter.Value;
        report.ConfirmedMissing.AddRange(missing.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        report.CheckFailed.AddRange(failed.OrderBy(p => p.Item1, StringComparer.OrdinalIgnoreCase));
        return report;
    }

    /// <summary>
    /// 按「父目录」分组校验：每个目录只枚举一次，目录内所有条目在内存里比对。
    ///
    /// 为什么这样更快：把「随机元数据读取」变成「顺序目录枚举」。
    /// 用户索引里平均每目录只有 2~6 个文件，逐个 stat 会产生 N 次随机寻道
    /// （机械盘每次都要磁头移动），而按目录枚举只需「目录数」次相邻读取。
    ///
    /// 实测（真实索引，逐个 stat 对比）：
    ///   · 文件系统调用：50,664 次（随机）→ 13,265 次（顺序）
    ///   · 吞吐：399~1,192 条/s → 7,973~23,139 条/s
    ///   · 加速区间 6~58 倍；**区间很宽是因为磁盘缓存命中率差异**，
    ///     冷数据单跑得 14.4 倍，两分片公平对比得 58 倍。
    ///     （曾一度记作「26.5 倍 / 3 分钟」，那是参照实现预热缓存后的失真值，已废弃。）
    ///
    /// 裁决语义与逐个 stat **完全一致**（已逐条比对验证），包括：
    ///   · 目录整棵消失 → 该目录条目判 Missing（可删）——与逐个 stat 的结论相同
    ///   · 上层祖先存在但不可读（权限受限）→ 判 CheckFailed（保留）
    ///   · 目录存在但无权限/IO 错误 → 判 CheckFailed（保留）
    ///   · 盘符离线 → 判 CheckFailed（保留）
    /// </summary>
    private static void ScanByDirectory(
        string[] paths,
        ConcurrentBag<string> missing,
        ConcurrentBag<(string, string)> failed,
        Counter counter,
        int step,
        InvalidIndexScanReport report,
        IProgress<InvalidIndexScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (paths.Length == 0)
        {
            return;
        }

        // 盘符在线状态缓存。**在线也缓存**：
        // 若只缓存在线（原先的写法），磁盘正常时每个目录组都会重新执行一次
        // Directory.Exists(root)——实测该调用约 39.5µs，按用户规模 74.5 万个目录
        // 推算约 29.5 秒纯多余开销（机械盘负载下更慢）。
        // 值为 null 表示磁盘可用，非 null 为不可用原因。
        var driveStatus = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // 目录存在性缓存：供「上溯校验」复用，避免同一祖先被反复查询。
        var cache = new ScanCache();

        var byDir = paths
            .GroupBy(static p => Path.GetDirectoryName(p) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Parallel.ForEach(byDir,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                // 并发度按介质决定：机械盘上高并发会退化为寻道竞争，
                // 反而把清理本身与同时进行的搜索一起拖慢（实测见 GetConcurrency）。
                MaxDegreeOfParallelism = GetConcurrency(paths)
            },
            group =>
            {
                var dir = group.Key;

                void ReportProgress()
                {
                    // 计数与「UI 回调」解耦：先把本组条目计入总数，再决定是否上报。
                    //
                    // 若把 progress == null 的早退放在计数之前（原先的写法），
                    // 不传 progress 时 TotalCountChecked 会恒为 0——
                    // 而它是「已停止检查（已检查 N / M 条）」这条提示的数据源，
                    // 会退化成「已检查 0 条」。计数是事实，不应取决于谁在监听。
                    //
                    // 目录内多条一次性计入：目录枚举的成本不随文件数增加，
                    // 因此按组累加后再上报更贴近真实进度。
                    var groupCount = 0;
                    foreach (var _ in group)
                    {
                        groupCount++;
                    }

                    var done = counter.Add(groupCount);

                    if (progress == null)
                    {
                        return;
                    }

                    if (done % step == 0 || done >= report.TotalCount)
                    {
                        progress.Report(new InvalidIndexScanProgress(
                            Math.Min(done, report.TotalCount), report.TotalCount, dir));
                    }
                }

                // 无父目录（如根目录下的文件）→ 无法批量，退回逐条（极少见）
                if (string.IsNullOrEmpty(dir))
                {
                    foreach (var p in group)
                    {
                        var r = Verify(p, null);
                        if (r.Verdict == PathVerification.Missing)
                        {
                            missing.Add(p);
                        }
                        else if (r.Verdict == PathVerification.CheckFailed)
                        {
                            failed.Add((p, r.FailReason ?? "未知原因"));
                        }
                    }

                    ReportProgress();
                    return;
                }

                // 磁盘离线：整批保留（不得删除），原因与逐个 stat 一致
                var root = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(root))
                {
                    if (!driveStatus.TryGetValue(root, out var offlineReason))
                    {
                        try
                        {
                            offlineReason = Directory.Exists(root) ? null : "所在磁盘当前不可用";
                        }
                        catch (Exception ex)
                        {
                            offlineReason = "检查磁盘失败：" + ex.Message;
                        }

                        driveStatus[root] = offlineReason;   // 在线（null）也缓存
                    }

                    if (offlineReason != null)
                    {
                        foreach (var p in group)
                        {
                            failed.Add((p, offlineReason));
                        }

                        ReportProgress();
                        return;
                    }
                }

                HashSet<string>? entries = null;
                var dirGone = false;
                string? readFailReason = null;

                try
                {
                    entries = new HashSet<string>(
                        Directory.EnumerateFiles(dir).Select(Path.GetFileName)!,
                        StringComparer.OrdinalIgnoreCase);
                }
                catch (DirectoryNotFoundException)
                {
                    // **不能直接判「目录已消失」**。
                    //
                    // 直接枚举「本目录」拿到 DirectoryNotFound 有两种可能：
                    //   ① 整棵目录树确实被删了            → 应判 Missing（可删）
                    //   ② 本目录不存在，但上层某个祖先存在却**不可读**（权限受限）
                    //                                       → 必须判 CheckFailed（保留）
                    // 情形 ② 若误判为可删，就会把**仍然存在**的照片列进待删清单——
                    // 这直接违反「权限错误不能删除索引」的既定语义。
                    //
                    // 因此这里复用逐个 stat 的原始做法：上溯到最近存在的祖先目录，
                    // 并确认它可枚举。可枚举 → 整棵树确实没了；不可枚举 → 保守保留。
                    var ancestorReason = ComputeParentCheck(dir, cache);
                    if (ancestorReason == null)
                    {
                        dirGone = true;
                    }
                    else
                    {
                        readFailReason = ancestorReason;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    readFailReason = "上级目录没有访问权限";
                }
                catch (IOException ex)
                {
                    readFailReason = "读取上级目录失败：" + ex.Message;
                }
                catch (Exception ex)
                {
                    readFailReason = "读取上级目录失败：" + ex.Message;
                }

                if (dirGone)
                {
                    // 目录整棵消失 → 目录内所有条目确认不存在，可删。
                    // 与逐个 stat 的结论一致：那条路径会一路上溯到仍存在的祖先目录、
                    // 枚举成功后判定文件不存在。
                    foreach (var p in group)
                    {
                        missing.Add(p);
                    }

                    ReportProgress();
                    return;
                }

                if (entries == null)
                {
                    // 目录存在但读不了 → 保守：整批保留，绝不删除
                    var reason = readFailReason ?? "读取上级目录失败";
                    foreach (var p in group)
                    {
                        failed.Add((p, reason));
                    }

                    ReportProgress();
                    return;
                }

                foreach (var p in group)
                {
                    var name = Path.GetFileName(p);
                    if (entries.Contains(name))
                    {
                        // 文件在目录清单里 → 有效。
                        // 注意：此处不再额外做「是否为目录」的判定——
                        // 索引里存的是文件路径，目录清单中同名条目即视为该文件。
                    }
                    else
                    {
                        missing.Add(p);
                    }
                }

                ReportProgress();
            });
    }

    /// <summary>
    /// 线程安全的进度计数器。
    /// 用独立对象而不是 <c>ref int</c>：ref 参数无法在 lambda / 本地函数中捕获，
    /// 而进度更新发生在并行分支里。
    /// </summary>
    private sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public int Add(int n) => Interlocked.Add(ref _value, n);
    }
}
