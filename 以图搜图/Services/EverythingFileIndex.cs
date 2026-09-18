using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Masuit.Tools.Logging;
using 以图搜图.Helpers;

namespace 以图搜图.Services;

/// <summary>
/// 借助 Everything 的 MFT 索引，快速得到「磁盘上确实存在哪些文件」。
///
/// 用途：清理无效索引时把大量条目判定为「存在」而**完全不需要文件系统 IO**。
/// Everything 直接读 NTFS 主文件表，一次查询即得全盘文件清单（本机实测
/// 1118 万条约 18 秒），远快于逐个 stat。
///
/// **安全边界（关键）**：本类的结果只能用于「确认存在」这一个方向。
/// 即：集合里有的 → 可以判为有效（跳过 IO）；集合里没有的 → **不能**判为已删除，
/// 必须交给真实文件系统复核。原因：Everything 的索引可能滞后、可能排除了某些目录、
/// 服务可能没在运行或用了旧快照。把「Everything 没索引到」当成「文件已删除」，
/// 会导致把仍然存在的索引误删——对清理这种破坏性操作不可接受。
///
/// 线程安全：Everything 的 API 是进程级全局状态（SetSearch/Query 会互相覆盖），
/// 因此构建必须在**单线程**内一次完成，之后只读使用返回的集合。
/// </summary>
internal static class EverythingFileIndex
{
    private const string DllName = "Everything64.dll";

    /// <summary>
    /// 需要达到的最小条目数才值得启用。
    ///
    /// **实测结论：默认不启用（阈值设为 int.MaxValue）。**
    ///
    /// 本机实测成本与收益：
    ///   · 构建全盘集合：4 个盘约 994 万文件，耗时约 16~18 秒，内存约 950 MB
    ///   · 它能省的只是「文件确实存在」那部分条目的目录枚举
    ///   · 而按目录枚举的成本只与**目录数**有关（实测 19,222 个目录约 560ms），
    ///     5 万条索引里存在的那部分也只覆盖几千个目录 → 只省下约 0.2~5 秒
    ///
    /// 即：16 秒固定开销 > 节省，属于净亏损；还会多占近 1GB 内存。
    /// 按目录枚举本身已经消除了随机 I/O（冷数据实测 14.4 倍加速），
    /// 因此 Everything 预筛**没有存在的必要**。
    ///
    /// 代码保留但不启用：若将来遇到「索引极大（千万级）且文件大量存在」的场景，
    /// 可把此值调低重新评估。当前设为大值以确保默认走纯目录枚举。
    /// </summary>
    public const int MinimumPathsForWorth = int.MaxValue;

    /// <summary>全盘文件集合的内存预算（约 1.5GB 上限，超出则放弃）。</summary>
    private const long MemoryBudgetBytes = 1_500L * 1024 * 1024;

    /// <summary>是否可用（DLL 存在且 Everything 在运行）。</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return File.Exists(DllName)
                       && Process.GetProcessesByName("Everything").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 尝试构建全盘文件集合。
    /// </summary>
    /// <param name="paths">索引路径（用于确定需要查询哪些盘符）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>小写规范化后的全盘文件路径集合；不可用时返回 null（调用方退化为纯目录枚举）。</returns>
    public static HashSet<string>? TryBuild(ICollection<string> paths, CancellationToken cancellationToken)
    {
        try
        {
            if (paths.Count < MinimumPathsForWorth || !IsAvailable)
            {
                return null;
            }

            // 只查询索引实际涉及的盘符，避免为无关磁盘白搬数据
            var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (p.Length >= 2 && p[1] == ':')
                {
                    drives.Add(p[..2]);
                }
            }

            if (drives.Count == 0)
            {
                return null;
            }

            var result = new HashSet<string>(paths.Count * 2, StringComparer.OrdinalIgnoreCase);
            long estimatedBytes = 0;
            // 缓冲区要足够大：路径被截断会导致 Contains 失配，进而把存在的文件误判为不存在
            var buffer = new StringBuilder(4096);

            // 与 EverythingHelper 共用同一把锁：两套代码操作的是同一个全局查询上下文，
            // 否则并发时 SetSearch/Query 会互相覆盖。目前本路径被 int.MaxValue 阈值禁用，
            // 锁主要防止将来重新启用时踩坑。
            lock (EverythingHelper.SyncRoot)
            {
                foreach (var drive in drives)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    Everything_SetMax(uint.MaxValue);
                    Everything_SetSearch($"file: {drive}\\");

                    if (!Everything_Query(true))
                    {
                        // 任一磁盘查询失败 → 整体放弃（避免部分盘缺失导致误判）
                        return null;
                    }

                    var count = Everything_GetNumResults();

                    // 内存预算检查：按平均 96 字节/路径估算
                    estimatedBytes += (long)count * 96;
                    if (estimatedBytes > MemoryBudgetBytes)
                    {
                        LogManager.Info($"Everything 索引过大（{drives.Count} 盘约 {estimatedBytes / 1048576} MB），跳过预筛");
                        return null;
                    }

                    for (uint i = 0; i < count; i++)
                    {
                        Everything_GetResultFullPathName(i, buffer, (uint)buffer.Capacity);
                        if (buffer.Length > 0)
                        {
                            result.Add(buffer.ToString());
                        }
                    }
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
            return null;
        }
    }

    // ── Everything SDK（与 EverythingHelper 使用同一套导出函数）──
    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern uint Everything_SetSearch(string lpSearchString);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern void Everything_GetResultFullPathName(uint index, StringBuilder path, uint length);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern bool Everything_Query(bool wait);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetNumResults();

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    private static extern void Everything_SetMax(uint dwMaxResults);
}
