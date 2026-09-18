using System.IO;

namespace 以图搜图.Services;

/// <summary>
/// 索引库的备份管理。
///
/// 提供两层保护，都完全不触碰 <c>index.json</c> 本身（格式硬约束）：
///
///   1. <c>index.json.bak</c> —— 上一代内容。由写入时的原子替换自动产生
///      （见 ImageIndexService.PublishIndexFile），无需额外开销。
///   2. <c>backups\index_yyyyMMdd.json</c> —— 每日快照。启动时检查，
///      当天没有就留一份，最多保留 <see cref="RetainedSnapshots"/> 份。
///
/// 为什么需要每日快照（而不只是 .bak）：.bak 只保留「上一次写入之前」的状态，
/// 若索引是被逐步写坏的（或用户几天后才发现），.bak 也已经被污染。
/// 每日快照提供的是「昨天还好」的回滚点。
///
/// **准入原则：备份目录里只允许存在「能解析」的内容。**
/// 这一点必须严格执行，否则机制会在最需要它的时候自我失效——
/// 主文件损坏时，若不加校验地把损坏内容复制成快照，再按日期淘汰旧快照，
/// 就会用不可用的副本顶掉仍然有效的历史快照；连续几次之后备份链彻底失效。
/// 因此：落盘前校验、保留时只认可解析的。
///
/// 加载时会按「主文件 → .bak → 最新快照」的顺序尝试，
/// 因此主文件损坏时能自动恢复，不会直接变成空库。
/// </summary>
public static class IndexBackup
{
    /// <summary>快照目录名（与 index.json 同级）。</summary>
    public const string FolderName = "backups";

    /// <summary>保留的快照份数（最新的若干份**可解析**快照）。</summary>
    public const int RetainedSnapshots = 2;

    /// <summary>快照目录完整路径。</summary>
    public static string Folder => Path.Combine(Directory.GetCurrentDirectory(), FolderName);

    /// <summary>
    /// 确保今天有一份可用的快照（没有就留一份）。
    /// </summary>
    /// <param name="indexPath">当前索引文件路径。</param>
    /// <returns>
    /// 调用结束后「今天是否已有可用快照」。
    /// 源不可用（不存在/空/损坏）时返回 false，调用方据此在稍后重试——
    /// 例如从零建库时首次调用必然没有源，需等首次写入成功后再补。
    /// </returns>
    public static bool CreateDailySnapshotIfNeeded(string indexPath)
    {
        try
        {
            var target = Path.Combine(Folder, $"index_{DateTime.Now:yyyyMMdd}.json");

            // 今天已有可用快照 → 已达成目标
            if (File.Exists(target) && IsCompleteIndexFile(target))
            {
                Prune();
                return true;
            }

            var info = new FileInfo(indexPath);

            // 源本身不可用（不存在/空/内容损坏）时什么都不做：
            // 既没必要留快照，更不能把损坏内容带进备份目录。
            if (!info.Exists || info.Length <= 0 || !IsCompleteIndexFile(indexPath))
            {
                Prune();
                return false;
            }

            Directory.CreateDirectory(Folder);

            // 先写临时文件再改名：避免复制到一半被杀进程，留下残缺快照
            var temp = target + ".tmp";
            File.Copy(indexPath, temp, overwrite: true);

            // 落盘前再校验一次，确保写进备份目录的东西一定是可用的
            if (!IsCompleteIndexFile(temp))
            {
                TryDelete(temp);
                Prune();
                return false;
            }

            File.Move(temp, target, overwrite: true);
            Prune();
            return true;
        }
        catch
        {
            // 备份失败不应影响程序运行
            return false;
        }
    }

    /// <summary>按日期从新到旧枚举快照（文件名日期可保证字典序即时间序）。</summary>
    public static IEnumerable<string> EnumerateSnapshotsNewestFirst()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }

            return Directory.EnumerateFiles(Folder, "index_*.json")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 保留最新的若干份**可解析**快照，删除其余（含损坏的、以及复制中断留下的临时文件）。
    ///
    /// 为什么不能纯按日期淘汰：损坏快照若被当作有效条目占用保留名额，
    /// 会把真正可用的旧快照挤出去。因此这里以「可解析」为保留前提。
    /// </summary>
    private static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            // 清理复制中断残留的临时文件
            foreach (var temp in Directory.EnumerateFiles(Folder, "*.tmp"))
            {
                TryDelete(temp);
            }

            var kept = 0;
            foreach (var file in EnumerateSnapshotsNewestFirst())
            {
                // 还没凑够保留数，且这份确实可用 → 留下
                if (kept < RetainedSnapshots && IsCompleteIndexFile(file))
                {
                    kept++;
                    continue;
                }

                // 超出保留窗口，或内容不可解析 → 删除
                // （损坏的副本留着毫无价值，只会占用保留名额）
                TryDelete(file);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    /// <summary>
    /// 判断一个文件是否是「完整的索引文件」。
    ///
    /// 只做**结构检查**（剥掉空白/BOM 后首字符为 '['、末字符为 ']'），不反序列化。
    ///
    /// 为什么不反序列化：备份校验在**每次启动**都会跑，
    /// 而索引可能有数百万条（实测 494 万条 / 1.41GB 完整解析约 8.8 秒、峰值 2GB 内存）。
    /// 若这里逐次解析，一次启动会解析 3~4 遍（本方法 + Prune 里对每个快照各一遍），
    /// 启动凭空慢几十秒、内存反复冲高，且完全没必要。
    ///
    /// 结构检查足以识别现实中真正会发生的损坏：写入中断导致文件被截断（没有收尾的 ']'）、
    /// 空文件、以及内容根本不是数组（例如被写入了错误数据）。
    /// </summary>
    private static bool IsCompleteIndexFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // 头部：跳过空白与 UTF-8 BOM 后必须是 '['，紧接着必须出现 '{'（第一条记录）。
            // 要求出现 '{' 是为了排除「空数组」([])：空索引没有备份价值，
            // 若把它当成可用快照写进备份目录，会挤掉仍然有效的旧快照。
            Span<byte> head = stackalloc byte[64];
            var headLen = stream.Read(head);
            var index = 0;
            if (headLen >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                index = 3;   // UTF-8 BOM
            }

            while (index < headLen && IsWhiteSpaceByte(head[index]))
            {
                index++;
            }

            if (index >= headLen || head[index] != (byte)'[')
            {
                return false;
            }

            index++;
            while (index < headLen && IsWhiteSpaceByte(head[index]))
            {
                index++;
            }

            if (index >= headLen || head[index] != (byte)'{')
            {
                return false;
            }

            // 尾部：从文件末尾往前找最后一个非空白字节，必须是 ']'
            // （写入中断的文件会缺这个收尾字符，这正是我们要抓的主要损坏形态）
            var tailSize = (int)Math.Min(16, info.Length);
            stream.Seek(-tailSize, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[16];
            var tailLen = stream.Read(tail);
            for (var i = tailLen - 1; i >= 0; i--)
            {
                if (IsWhiteSpaceByte(tail[i]))
                {
                    continue;
                }

                return tail[i] == (byte)']';
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWhiteSpaceByte(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

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
            // 被占用则跳过
        }
    }
}
