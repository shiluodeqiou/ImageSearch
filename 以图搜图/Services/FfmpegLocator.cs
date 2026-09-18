using System.Diagnostics;
using System.IO;
using System.Text;

namespace 以图搜图.Services;

/// <summary>
/// ffmpeg 可执行文件的定位。
///
/// 查找顺序（先近后远）：
///   1. config.ini 中 FFmpegPath 指定的路径；
///   2. 程序目录下的 ffmpeg.exe；
///   3. 程序目录下的 tools\ffmpeg.exe（内置发布时的常见位置）；
///   4. 系统 PATH 中的 ffmpeg（用户自行安装的情况）。
///
/// 都找不到时返回 null——视频索引会退回到内置的 Shell 缩略图方案，
/// 不会因为缺少 ffmpeg 而完全不可用。
/// </summary>
public static class FfmpegLocator
{
    private const string ExeName = "ffmpeg.exe";

    private static readonly object Sync = new();
    private static string? _cached;
    private static bool _detected;

    /// <summary>ffmpeg 完整路径；未找到时为 null。</summary>
    public static string? Path
    {
        get
        {
            lock (Sync)
            {
                if (!_detected)
                {
                    _cached = Detect();
                    _detected = true;
                }

                return _cached;
            }
        }
    }

    /// <summary>是否可用。</summary>
    public static bool IsAvailable => Path != null;

    /// <summary>供界面显示的来源说明。</summary>
    public static string Describe()
    {
        var path = Path;
        return path == null
            ? "未检测到 ffmpeg（视频抽帧将使用系统缩略图方案）"
            : $"ffmpeg: {path}";
    }

    /// <summary>清除缓存，下次访问时重新探测（用户在设置中修改路径后调用）。</summary>
    public static void Invalidate()
    {
        lock (Sync)
        {
            _cached = null;
            _detected = false;
        }
    }

    private static string? Detect()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 路径非法，继续下一个
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidates()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1) 配置指定的路径
        yield return TryReadConfiguredPath();

        // 2) 程序目录
        yield return Combine(baseDir, ExeName);

        // 3) tools 子目录（内置发布惯用位置）
        yield return Combine(baseDir, "tools", ExeName);

        // 4) PATH
        yield return FindInPath();
    }

    private static string? TryReadConfiguredPath()
    {
        try
        {
            var configured = new Masuit.Tools.Files.IniFile("config.ini").GetValue("Global", "FFmpegPath", string.Empty);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            // 允许配置相对路径（相对于程序目录）
            return System.IO.Path.IsPathRooted(configured)
                ? configured
                : Combine(AppContext.BaseDirectory, configured);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInPath()
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
            {
                return null;
            }

            foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Combine(dir.Trim(), ExeName);
                if (full != null && File.Exists(full))
                {
                    return full;
                }
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static string? Combine(string? dir, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine([dir, .. parts]));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>ffmpeg 命令行执行结果。</summary>
/// <param name="Success">是否成功产出结果。</param>
/// <param name="StandardError">stderr 内容（失败时用于诊断）。</param>
/// <param name="TimedOut">是否因超时被强制结束（与普通失败区分：超时说明该文件可能让 ffmpeg 挂住）。</param>
public readonly record struct ProcessResult(bool Success, string StandardError, bool TimedOut = false);

/// <summary>
/// 外部进程执行辅助。
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// 静默执行外部命令，不弹窗、不阻塞界面。
    /// </summary>
    public static ProcessResult Run(string fileName, string arguments, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return new ProcessResult(false, "无法启动进程");
            }

            // 必须异步读取输出，否则子进程写满管道缓冲区后会死锁
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能已退出
                }

                return new ProcessResult(false, $"执行超时（{timeoutMs}ms）", TimedOut: true);
            }

            var stderr = SafeResult(stderrTask);
            _ = SafeResult(stdoutTask);

            return new ProcessResult(process.ExitCode == 0, stderr);
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(false, "已取消");
        }
        catch (Exception ex)
        {
            return new ProcessResult(false, ex.Message);
        }
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
