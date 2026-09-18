using Masuit.Tools;
using Masuit.Tools.Media;
using SkiaSharp;
using System.Collections.Concurrent;
using System.IO;
using 以图搜图.Models;

namespace 以图搜图.Services;

public class ImageSearchService
{
    /// <summary>
    /// 目录统计缓存：目录 → (文件数, 总MB, 记录时间)。
    ///
    /// 检索结果会附带「所属文件夹的文件数与大小」，用于判断重复图该保留哪一份。
    /// 该统计需要对每个命中目录做一次**递归枚举**；同一目录在连续多次搜索中
    /// 会被反复扫描，而目录内容变化很慢。
    /// 用短 TTL 缓存把「每次搜索都遍历」降为「每个目录每 5 分钟遍历一次」。
    /// </summary>
    private static readonly ConcurrentDictionary<string, (int Count, float SizeMb, DateTime At)> DirectoryStatsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>目录统计的缓存有效期。</summary>
    private static readonly TimeSpan DirectoryStatsTtl = TimeSpan.FromMinutes(5);

    /// <summary>缓存条目上限，防止长期运行无限增长。</summary>
    private const int DirectoryStatsCacheLimit = 4096;

    /// <summary>取目录统计（带 TTL 缓存）。</summary>
    private static (int Count, float SizeMb) GetDirectoryStats(string directory)
    {
        if (DirectoryStatsCache.TryGetValue(directory, out var cached)
            && DateTime.UtcNow - cached.At < DirectoryStatsTtl)
        {
            return (cached.Count, cached.SizeMb);
        }

        int count;
        float sizeMb;

        // 目录枚举必须容错：该统计只是给结果附带的参考信息，
        // 若目录在搜索过程中消失、或权限被改（网络盘断连等），
        // DirectoryInfo.GetFiles 会抛 DirectoryNotFound / UnauthorizedAccess，
        // 而它被 ToDictionary 直接调用 —— 异常会让**整个搜索失败**。
        // 一条目录的统计拿不到就退化为 0，不该拖垮整次搜索。
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("*.*", SearchOption.AllDirectories);
            count = files.Length;
            sizeMb = files.Sum(s => s.Length) / 1048576f;
        }
        catch
        {
            count = 0;
            sizeMb = 0;
        }

        // 超限时整体清空：比逐个淘汰简单，且缓存本身就是「可重建的加速器」，
        // 清空只影响一次性能，不影响正确性。
        if (DirectoryStatsCache.Count >= DirectoryStatsCacheLimit)
        {
            DirectoryStatsCache.Clear();
        }

        DirectoryStatsCache[directory] = (count, sizeMb, DateTime.UtcNow);
        return (count, sizeMb);
    }

    public async Task<List<SearchResult>> SearchAsync(string filename, ConcurrentDictionary<string, IndexItem> index, MatchAlgorithm algorithm, float similarity, bool checkRotated, bool checkFlipped)
    {
        // 哈希比对是纯 CPU/内存带宽密集型循环，并发度按物理核来定，
        // 避免高并发下的内存带宽争抢与缓存失效（与索引路径同理）。
        var parallelism = CpuInfo.RecommendedComputeConcurrency;
        return await Task.Run(() =>
        {
            var defHashs = new ConcurrentBag<ulong[]>();
            var dctHashs = new ConcurrentBag<ulong>();
            var pHashs = new ConcurrentBag<ulong>();
            var actions = new List<Action>();

            using (var image = ImageDecoder.DecodeGrayThumb(filename, 160))
            {
                if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                {
                    actions.Add(() => defHashs.Add(image.DifferenceHash256()));
                }

                if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                {
                    actions.Add(() => dctHashs.Add(image.DctHash()));
                }

                if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                {
                    actions.Add(() => pHashs.Add(image.DctHash64()));
                }

                if (checkRotated)
                {
                    actions.Add(() =>
                    {
                        using var clone = image.Rotate(90);
                        if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                        {
                            defHashs.Add(clone.DifferenceHash256());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                        {
                            dctHashs.Add(clone.DctHash());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                        {
                            pHashs.Add(clone.DctHash64());
                        }
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Rotate(180);
                        if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                        {
                            defHashs.Add(clone.DifferenceHash256());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                        {
                            dctHashs.Add(clone.DctHash());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                        {
                            pHashs.Add(clone.DctHash64());
                        }
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Rotate(270);
                        if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                        {
                            defHashs.Add(clone.DifferenceHash256());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                        {
                            dctHashs.Add(clone.DctHash());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                        {
                            pHashs.Add(clone.DctHash64());
                        }
                    });
                }

                if (checkFlipped)
                {
                    actions.Add(() =>
                    {
                        using var clone = image.FlipHorizontal();
                        if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                        {
                            defHashs.Add(clone.DifferenceHash256());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                        {
                            dctHashs.Add(clone.DctHash());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                        {
                            pHashs.Add(clone.DctHash64());
                        }
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.FlipVertical();
                        if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                        {
                            defHashs.Add(clone.DifferenceHash256());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                        {
                            dctHashs.Add(clone.DctHash());
                        }

                        if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                        {
                            pHashs.Add(clone.DctHash64());
                        }
                    });
                }

                Parallel.Invoke(actions.ToArray());
            }

            var list = new List<SearchResult>();
            var sim = Math.Max(0.85, similarity);


            list.AddRange(index.Chunk(parallelism).AsParallel().WithDegreeOfParallelism(parallelism).SelectMany(grouping =>
            {
                var items = new List<SearchResult>();
                foreach (var (key, value) in grouping)
                {
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash64))
                    {
                        var match = pHashs.Max(h => ImageHasher.Compare(value.DctHash64, h));
                        if (match > sim)
                        {
                            items.Add(new SearchResult
                            {
                                路径 = key,
                                匹配度 = match,
                                匹配算法 = "DCT Hash 64"
                            });
                        }
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DifferenceHash))
                    {
                        var match = defHashs.Max(h => ImageHasher.Compare(value.DifferenceHash, h));
                        if (match > similarity)
                        {
                            items.Add(new SearchResult
                            {
                                路径 = key,
                                匹配度 = match,
                                匹配算法 = "Difference Hash"
                            });
                        }
                    }
                    if (algorithm.HasFlag(MatchAlgorithm.DctHash32))
                    {
                        var match = dctHashs.Max(h => ImageHasher.Compare(value.DctHash, h));
                        if (match > sim)
                        {
                            items.Add(new SearchResult
                            {
                                路径 = key,
                                匹配度 = match,
                                匹配算法 = "DCT Hash 32"
                            });
                        }
                    }
                }
                return items;
            }));

            list = list.OrderByDescending(a => a.匹配度).DistinctBy(e => e.路径).ToList();

            // 排除查询图自身。
            // 用「自己搜自己」必然 100% 命中，把它混在结果里没有意义——
            // 用户要的是「这张图还出现在哪些**别的**地方」。
            // 注意：只在查询图确实来自被索引的位置时才排除，否则（例如从剪贴板、
            // 临时目录搜索）不会有同名条目，这一步自然无副作用。
            try
            {
                var queryFull = Path.GetFullPath(filename);
                list.RemoveAll(r => string.Equals(
                    Path.GetFullPath(r.路径), queryFull, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // 路径不规范时跳过排除，不影响其它结果
            }

            // 排序：先按文件夹分组、组内按匹配度降序。
            // 这样同一文件夹的结果连成一段，用户一眼就能看出「命中的是几个不同位置」，
            // 而不是让同一目录的几十条结果散落在整个列表里。
            // 组间按「该组最高匹配度」降序，保证最像的仍在最前面。
            var ordered = list
                .GroupBy(r => Path.GetDirectoryName(r.路径) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Max(r => r.匹配度))
                .SelectMany(g => g.OrderByDescending(r => r.匹配度))
                .ToList();
            list = ordered;

            // 一次性完成「确认存在 + 读取大小」，不要分成两步。
            //
            // 为什么必须合成一步：这里的条目可能对应已被删除/移动的文件
            // （用户删了图、磁盘整理过目录、外接盘没插等）。它们哈希还在、
            // 能被比对命中，但点开是空的、预览加载不出来，属纯噪声。
            //
            // 而「先 File.Exists 过滤、再 FileInfo.Length 读大小」这种两段式写法
            // 存在竞态窗口：文件若在两步之间被删除，FileInfo.Length 会抛
            // FileNotFoundException，异常冒泡出去会让**整个搜索失败**
            // （用户看到「搜索时发生错误」且拿不到任何结果）。
            // 现实中这个窗口并不罕见：边搜边清理磁盘、网络盘瞬时断连、
            // 另一个程序在整理相册，都可能命中。
            //
            // 一次 try 里取大小，失败（文件不存在/被占用/权限变化）就跳过该条，
            // 既消除了窗口，也不会因为一条坏数据毁掉整次搜索。
            var sized = new List<(SearchResult Result, long Length)>(list.Count);
            foreach (var result in list)
            {
                try
                {
                    sized.Add((result, new FileInfo(result.路径).Length));
                }
                catch
                {
                    // 文件已不可读：从结果里剔除（它本来也不该展示）
                }
            }

            list = sized.Select(x => x.Result).ToList();

            // 目录统计（文件数/大小）走带 TTL 的缓存，避免每次搜索都重复递归遍历。
            // 注意：这里刻意不再用 AsParallel 包一层——GetDirectoryStats 内部会访问磁盘，
            // 而缓存命中时是纯内存读取；由外层并发去重后的目录数通常很少。
            var dic = list
                .GroupBy(r => Path.GetDirectoryName(r.路径) ?? string.Empty)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToDictionary(g => g.Key, g => GetDirectoryStats(g.Key),
                    StringComparer.OrdinalIgnoreCase);

            // 大小已在上一步随「确认存在」一起取好，这里不再重复访问文件系统。
            for (var i = 0; i < list.Count; i++)
            {
                var result = list[i];
                result.大小 = FormatFileSize(sized[i].Length);

                // 标记视频即可，**不在这里抽帧**。
                //
                // 这里曾经同步调用 VideoThumbnailCache.GetOrCreate，为每个命中的视频
                // 生成预览帧——后果是搜索被 ffmpeg 拖垮：
                //   · 耗时随「未缓存缩略图的命中视频数」线性增长
                //     （实测 40 个视频全部冷缓存：6.5 秒，缓存命中仅 0.25 秒）；
                //   · 单个视频最坏要跑 3 个定位策略 × 超时 + Shell 兜底（可达上百秒），
                //     而失败又不缓存，于是**每次搜索都重跑一遍**（实测连续三次搜索
                //     818ms→413ms→323ms 反复失败重试）。
                // 用户表现为"搜个视频就卡死"：其实界面线程没锁，只是 IsSearching
                // 一直为真、搜索按钮禁用且毫无进度反馈。
                //
                // 缩略图只服务于预览显示，属于渲染关注点，不该由检索承担：
                // 索引进来的视频在索引阶段就已生成；旧索引/缓存被清的情况由
                // 界面在用户真正选中该结果时按需生成（见 MainViewModel）。
                if (VideoFormats.IsVideo(result.路径))
                {
                    result.是视频 = true;
                }

                var dirName = Path.GetDirectoryName(result.路径);
                if (dirName != null && dic.TryGetValue(dirName, out var stats))
                {
                    result.所属文件夹文件数 = stats.Count;
                    result.所属文件夹大小 = $"{stats.SizeMb:F2}MB";
                }
            }

            return list;
        });
    }

    /// <summary>按量级格式化文件大小；旧实现恒以 KB 显示，MB 级文件会出现 1843KB 这种数字。</summary>
    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1048576)
        {
            return $"{bytes / 1048576.0:F1}MB";
        }

        return $"{bytes / 1024}KB";
    }
}
