using System.IO;

namespace 以图搜图.Helpers;

/// <summary>
/// 文件原子写入：先写同目录临时文件，写完再改名覆盖目标。
///
/// 直接 File.WriteAllText 在写到一半时进程被杀/断电，会留下截断的文件；
/// 本项目的小配置（索引源、界面偏好）加载侧虽按「损坏即空」容错，
/// 但那会让用户的配置静默丢失。改名在同卷上是原子操作，
/// 目标要么是完整旧内容、要么是完整新内容。
/// </summary>
public static class AtomicFile
{
    /// <summary>原子写入文本（UTF-8）。</summary>
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 随机后缀避免并发写同一目标时临时文件互相覆盖
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
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
            // 临时文件残留不影响主流程
        }
    }
}
