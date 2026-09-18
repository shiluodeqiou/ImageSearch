using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace 以图搜图.Services;

/// <summary>
/// 视频缩略图的磁盘缓存。
///
/// 为什么需要落盘缓存，而不是每次现取：
///   1. **UI 预览必须用图片文件**。界面用 BitmapImage 加载预览，
///      它无法直接加载视频；结果列表的预览图因此必须指向一个真实的图片文件。
///   2. **避免重复抽帧**。重建索引或重复搜索时可直接复用，无需再次调用 ffmpeg。
///   3. **索引与预览共用一份**，保证「索引的那一帧」和「用户看到的那一帧」一致。
///
/// 缓存键由「视频完整路径 + 最后写入时间 + 文件大小」共同决定，
/// 因此视频被修改或替换后会自动失效，不会用到过期缩略图。
/// </summary>
public static class VideoThumbnailCache
{
    private const string CacheFolderName = "thumbnails";

    private static readonly string CacheRoot =
        Path.Combine(AppContext.BaseDirectory, CacheFolderName);

    /// <summary>
    /// 取得视频缩略图路径；不存在则现场生成。
    /// </summary>
    /// <param name="videoPath">视频文件路径。</param>
    /// <param name="size">缩略图最长边。</param>
    /// <returns>缩略图文件路径；生成失败返回 null。</returns>
    public static string? GetOrCreate(string videoPath, int size = VideoFrameExtractor.DefaultSize)
    {
        var target = GetCachePath(videoPath, size);
        if (target == null)
        {
            return null;
        }

        if (IsCompleteThumbnail(target))
        {
            return target;
        }

        // 已知抽不出帧的视频，在 TTL 内直接放弃，不再重跑 ffmpeg。
        //
        // 没有这道闸门时，抽帧失败的视频**每次调用都要重新尝试**全部策略
        // （实测连续三次均失败并各花数百毫秒；若 ffmpeg 卡住则每次都要等满超时）。
        // 搜索路径已不再抽帧，但索引与预览仍会走到这里，失败重试代价依旧可观。
        if (IsRecentlyFailed(videoPath, size))
        {
            return null;
        }

        // 同一个缓存文件只让一个线程生成，其余线程等它做完后复查缓存。
        // 否则同一视频被并发请求时（索引与搜索兜底可能重叠）会有 N 个 ffmpeg
        // 同时抽帧、同时往同一个目标文件搬，纯属浪费。
        //
        // 注意这只是「省资源」的优化，正确性并不依赖它：即便两个线程拿到不同的
        // 锁对象而同时生成，写入本身也是安全的（见 Extract 的说明）。
        // 因此这里可以在表变大时直接清空，不必担心破坏互斥。
        var gate = PathLocks.GetOrAdd(target, static _ => new object());
        if (PathLocks.Count > PathLocksLimit)
        {
            PathLocks.Clear();
        }

        lock (gate)
        {
            // 双重检查：等锁期间可能已被别的线程生成好
            if (IsCompleteThumbnail(target))
            {
                return target;
            }

            // 等锁期间别的线程可能刚失败过，再查一次负缓存
            if (IsRecentlyFailed(videoPath, size))
            {
                return null;
            }

            // 缓存不可用（不存在或残缺）→ 重新生成，并清掉残缺文件
            TryDelete(target);

            // 顺带清理抽帧中断残留的陈旧临时文件（机会式清理，避免单独开定时任务）
            if (Interlocked.Increment(ref _sinceLastCleanup) >= CleanupInterval)
            {
                Interlocked.Exchange(ref _sinceLastCleanup, 0);
                CleanupStaleTempFiles();
            }

            var ok = Extract(videoPath, target, size);
            if (ok)
            {
                FailedPaths.TryRemove(target, out _);
                return target;
            }

            MarkFailed(target);
            return null;
        }
    }

    /// <summary>
    /// 抽帧失败的缓存路径 → 放弃重试的截止时刻（Ticks）。
    /// 键是缓存文件路径（已含视频路径+时间戳+大小+尺寸），因此视频一旦被
    /// 修改或替换，键就变了，会立刻重新尝试，不会因负缓存而永久跳过。
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> FailedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>失败后的静默期；期间不再为该视频重试抽帧。</summary>
    private static readonly TimeSpan FailureSilence = TimeSpan.FromMinutes(10);

    /// <summary>负缓存条目上限，超限整体清空（清空只是让个别视频重试一次）。</summary>
    private const int FailedPathsLimit = 4096;

    private static void MarkFailed(string target)
    {
        if (FailedPaths.Count >= FailedPathsLimit)
        {
            FailedPaths.Clear();
        }

        FailedPaths[target] = DateTime.UtcNow.Add(FailureSilence).Ticks;
    }

    /// <summary>该视频是否近期抽帧失败（未过期）。供界面避免无谓地反复触发生成。</summary>
    public static bool IsRecentlyFailed(string videoPath, int size = VideoFrameExtractor.DefaultSize)
    {
        var target = GetCachePath(videoPath, size);
        if (target == null)
        {
            return false;
        }

        if (!FailedPaths.TryGetValue(target, out var until))
        {
            return false;
        }

        if (DateTime.UtcNow.Ticks < until)
        {
            return true;
        }

        // 过期即移除，允许重新尝试
        FailedPaths.TryRemove(target, out _);
        return false;
    }

    /// <summary>
    /// 每个缓存文件的生成闸门，避免同一视频被并发重复抽帧。
    /// 键数上界为「见过的不同视频数」，条目很小；超过上限时整体清空
    /// （清空只会让个别调用退化为并发生成，不影响正确性）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>生成闸门表的上限。</summary>
    private const int PathLocksLimit = 8192;

    /// <summary>距上次临时文件清理的调用次数，用于机会式触发清理。</summary>
    private static int _sinceLastCleanup;

    /// <summary>每多少次抽帧尝试清理一次陈旧临时文件。</summary>
    private const int CleanupInterval = 512;

    /// <summary>
    /// 只查询已缓存的缩略图，不触发生成。
    /// 供 UI 绑定使用——绑定必须快速返回，不能阻塞在 ffmpeg 上。
    /// </summary>
    public static string? GetCached(string videoPath, int size = VideoFrameExtractor.DefaultSize)
    {
        var target = GetCachePath(videoPath, size);
        if (target == null)
        {
            return null;
        }

        return IsCompleteThumbnail(target) ? target : null;
    }

    /// <summary>
    /// 判断缓存文件是否为**完整**的 PNG。
    ///
    /// 仅判断「存在且非空」是不够的：ffmpeg 异常退出（如超时被强制结束）
    /// 可能留下只写了一部分的文件，这种文件会被误认为有效缓存长期使用，
    /// 表现为该视频永远显示破损缩略图且不会自愈。
    /// 这里校验 PNG 的文件头与结尾标记（IEND 块），能识别绝大多数截断情形。
    /// </summary>
    private static bool IsCompleteThumbnail(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // PNG 最小体积远大于 32 字节，过小必定是残缺或写坏
            if (!info.Exists || info.Length < 64)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 校验 PNG 签名：89 50 4E 47 0D 0A 1A 0A
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != 8
                || signature[0] != 0x89 || signature[1] != 0x50 || signature[2] != 0x4E || signature[3] != 0x47
                || signature[4] != 0x0D || signature[5] != 0x0A || signature[6] != 0x1A || signature[7] != 0x0A)
            {
                return false;
            }

            // 校验结尾的 IEND 块（PNG 必须以它收尾；截断文件不会有）
            // IEND 块共 12 字节：长度(4)=0 + "IEND"(4) + CRC(4)
            const int tailLength = 12;
            if (info.Length < tailLength)
            {
                return false;
            }

            stream.Seek(-tailLength, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[tailLength];
            if (stream.Read(tail) != tailLength)
            {
                return false;
            }

            // 偏移 4..8 应为 ASCII "IEND"
            return tail[4] == (byte)'I' && tail[5] == (byte)'E'
                && tail[6] == (byte)'N' && tail[7] == (byte)'D';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 抽帧并写入指定位置（供索引流程调用）。
    ///
    /// 采用「先写临时文件、成功后再原子改名」的方式：
    /// ffmpeg 在超时等情况会被强制结束，可能留下**写入一半的图片**；
    /// 若直接写目标路径，这种残缺文件会被当作有效缓存长期使用
    /// （表现为该视频永远显示破损缩略图、且不自愈）。
    /// 改名是原子操作，因此目标路径要么不存在、要么一定是完整文件。
    ///
    /// ⚠️ 临时文件名**必须保留原扩展名**：
    /// ffmpeg 是按输出文件扩展名推断容器格式的，写成 ".png.tmp" 之类
    /// 会直接报 "Unable to choose an output format" 而完全不产出文件。
    /// 这里在扩展名前插入一段随机串以保证唯一性（并发抽帧时互不干扰）。
    /// </summary>
    /// <returns>是否成功。</returns>
    public static bool Extract(string videoPath, string outputPath, int size = VideoFrameExtractor.DefaultSize)
    {
        var tempPath = BuildTempPath(outputPath);

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var result = VideoFrameExtractor.Extract(videoPath, tempPath, size);
            if (!result.Success || !IsCompleteThumbnail(tempPath))
            {
                TryDelete(tempPath);
                return false;
            }

            // 临时文件已校验为完整，改名即发布。
            //
            // 搬过去之后**不能再回头校验并在失败时删掉目标文件**：
            // Windows 上 File.Move(overwrite: true) 走 MoveFileEx(MOVEFILE_REPLACE_EXISTING)，
            // 它会先把目标删掉再改名 —— 也就是说目标路径存在一个「短暂不存在」的窗口。
            // 若另一线程恰好在这个窗口里读取目标，会得到「文件不存在」，
            // 旧写法便据此判定失败并 TryDelete(outputPath)，把**别的线程刚写好的有效文件删掉**，
            // 结果是缓存彻底缺失、而调用方还拿到了一个真实不存在的路径。
            // （实测 8 线程并发抽同一视频：8 次只有 2 次返回成功，且最终缓存文件被删光。）
            // 因此这里以「临时文件已完整」为唯一成功依据。
            File.Move(tempPath, outputPath, overwrite: true);
            return true;
        }
        catch
        {
            TryDelete(tempPath);
            return false;
        }
    }

    /// <summary>
    /// 由目标路径派生临时路径，保留原扩展名（ffmpeg 依赖它判断输出格式）。
    /// 例：thumbnails\ABC.png → thumbnails\ABC.a1b2c3d4.png
    /// </summary>
    private static string BuildTempPath(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        var name = Path.GetFileNameWithoutExtension(outputPath);
        var ext = Path.GetExtension(outputPath);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var fileName = $"{name}.{tag}{ext}";

        return string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
    }

    /// <summary>清理抽帧中断留下的临时文件（形如 name.xxxxxxxx.png 且与目标不同名）。</summary>
    private static void CleanupStaleTempFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(CacheRoot))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var file in System.IO.Directory.EnumerateFiles(CacheRoot, "*.*"))
            {
                // 临时文件特征：名字中含 ".xxxxxxxx." 段（8 位随机串）
                var name = Path.GetFileNameWithoutExtension(file);
                var lastDot = name.LastIndexOf('.');
                if (lastDot < 0 || name.Length - lastDot - 1 != 8)
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 忽略单个文件
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 计算缓存文件路径。
    /// 键包含最后写入时间与文件大小，视频被替换后会自动指向新的缓存文件。
    /// </summary>
    public static string? GetCachePath(string videoPath, int size = VideoFrameExtractor.DefaultSize)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return null;
            }

            // 用文件元数据参与哈希，实现「内容变化即失效」
            long ticks = 0;
            long length = 0;
            try
            {
                var info = new FileInfo(videoPath);
                if (info.Exists)
                {
                    ticks = info.LastWriteTimeUtc.Ticks;
                    length = info.Length;
                }
            }
            catch
            {
                // 元数据读不到时退化为仅按路径做键
            }

            var key = $"{videoPath.ToLowerInvariant()}|{ticks}|{length}|{size}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

            return Path.Combine(CacheRoot, hash + ".png");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>缓存目录（供诊断或清理使用）。</summary>
    public static string CacheDirectory => CacheRoot;

    /// <summary>清空缓存，返回删除的文件数。</summary>
    public static int Clear()
    {
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(CacheRoot))
            {
                return 0;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(CacheRoot, "*.png"))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 被占用则跳过
                }
            }
        }
        catch
        {
            // 忽略
        }

        return removed;
    }
}
