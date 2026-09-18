using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace 以图搜图.Services;

/// <summary>抽帧来源。</summary>
public enum FrameSource
{
    /// <summary>未取到。</summary>
    None,

    /// <summary>外部 ffmpeg 进程。</summary>
    Ffmpeg,

    /// <summary>Windows Shell 缩略图接口。</summary>
    ShellThumbnail
}

/// <summary>抽帧结果。</summary>
/// <param name="Success">是否成功产出文件。</param>
/// <param name="Source">实际使用的来源。</param>
/// <param name="Error">失败原因。</param>
public readonly record struct FrameExtractionResult(bool Success, FrameSource Source, string? Error = null);

/// <summary>
/// 视频抽帧器。
///
/// 采用「ffmpeg 为主、Shell 缩略图为兜底」的混合策略：
///
///   ffmpeg（首选）
///     · 独立进程，天然隔离崩溃——坏文件不会影响主程序
///     · 可并行（多进程），冷启动批量索引显著更快
///     · 格式覆盖最广，不依赖系统已装编解码器
///     · 代价：需要 ffmpeg.exe（内置约 127MB）
///
///   Shell 缩略图（兜底，仅在缺少 ffmpeg 时使用）
///     · 零依赖，走系统已注册的解码器
///     · 命中系统缩略图缓存时极快
///     · 代价：**必须单线程调用**——Shell 的缩略图提供程序不是线程安全的，
///       并发调用会触发访问冲突（0xC0000005）导致进程崩溃（实测复现）
///     · 崩溃无法隔离（在进程内），因此只作为兜底
///
/// 为保证输出的缩略图能被后续索引直接使用，两种实现都统一输出
/// 最长边不超过指定尺寸的 PNG 文件。
/// </summary>
public static class VideoFrameExtractor
{
    /// <summary>默认输出图片的最长边。</summary>
    public const int DefaultSize = 160;

    /// <summary>单次抽帧的超时（毫秒）。微信短视频通常在一秒内完成。</summary>
    private const int TimeoutMs = 30_000;

    /// <summary>
    /// 抽取视频代表帧并写入 <paramref name="outputPath"/>。
    /// </summary>
    /// <param name="videoPath">视频文件路径。</param>
    /// <param name="outputPath">输出图片路径（PNG）。</param>
    /// <param name="size">输出图片最长边。</param>
    public static FrameExtractionResult Extract(string videoPath, string outputPath, int size = DefaultSize)
    {
        if (!File.Exists(videoPath))
        {
            return new FrameExtractionResult(false, FrameSource.None, "文件不存在");
        }

        // 优先 ffmpeg
        if (FfmpegLocator.IsAvailable)
        {
            var result = ExtractWithFfmpeg(videoPath, outputPath, size);
            if (result.Success)
            {
                return result;
            }

            // ffmpeg 失败时继续尝试 Shell 兜底，尽力而为
            var fallback = ExtractWithShell(videoPath, outputPath, size);
            return fallback.Success
                ? fallback
                : new FrameExtractionResult(false, FrameSource.None,
                    $"ffmpeg 失败（{Trim(result.Error)}），Shell 兜底也失败（{Trim(fallback.Error)}）");
        }

        return ExtractWithShell(videoPath, outputPath, size);
    }

    /// <summary>
    /// 用外部 ffmpeg 进程抽帧，按「定位策略」递进尝试。
    ///
    /// 实测发现不同容器对定位方式的兼容性差异很大，单一策略无法覆盖：
    ///   · 微信等来源的短视频极短（中位仅 2 秒、最短 0.7 秒），
    ///     用固定时间点（如 <c>-ss 1</c>）会越过文件末尾导致采不到帧；
    ///   · 因此首选 <c>-sseof -0.5</c>（从末尾回退），无需先探测时长，
    ///     且能避开部分视频开头的黑场；
    ///   · 但 <c>-sseof</c> 对某些格式会失败（实测 rmvb 报
    ///     "Invalid decoder state: B-frame"、asf 报 "mjpeg non full-range YUV"），
    ///     而这些文件**取首帧是成功的**。
    ///
    /// 所以重试必须**换一种定位策略**，而不是重复同一参数——
    /// 若两次尝试的定位方式相同，失败原因也相同，重试毫无意义。
    /// </summary>
    private static FrameExtractionResult ExtractWithFfmpeg(string videoPath, string outputPath, int size)
    {
        var ffmpeg = FfmpegLocator.Path!;
        var scaleFilter = $"scale=w={size}:h={size}:force_original_aspect_ratio=decrease:force_divisible_by=2";
        var lastError = string.Empty;

        // 策略序列：从「更精确的定位」递进到「最保守的取首帧」。
        // 顺序有讲究——优先用能避开黑场的定位方式，实在不行才退回首帧。
        (string Label, string SeekArgs, string PixFmt)[] strategies =
        [
            ("末尾回退", "-sseof -0.5 ", ""),
            ("取首帧",   "",              ""),
            ("取首帧+像素格式", "",       "-pix_fmt yuvj420p "),
        ];

        foreach (var (label, seekArgs, pixFmt) in strategies)
        {
            // -y 覆盖输出；-loglevel error 只保留错误；
            // scale 保证不变形，并强制偶数宽高（部分编码器要求）
            var args = $"-y -loglevel error {seekArgs}-i {Quote(videoPath)} " +
                       $"-frames:v 1 {pixFmt}-vf \"{scaleFilter}\" " +
                       $"{Quote(outputPath)}";

            var result = ProcessRunner.Run(ffmpeg, args, TimeoutMs);

            if (result.Success && IsUsableFile(outputPath))
            {
                return new FrameExtractionResult(true, FrameSource.Ffmpeg);
            }

            // 保留首个策略的错误信息（通常最能说明问题）
            if (lastError.Length == 0)
            {
                lastError = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"ffmpeg 未产出图像（{label}）"
                    : result.StandardError;
            }

            // 失败可能留下残缺文件，清掉再做下一次尝试，
            // 否则 IsUsableFile 会把残留当成功
            TryDelete(outputPath);

            // 超时意味着 ffmpeg 在这个文件上挂住了，换定位策略大概率同样超时，
            // 再等两个完整超时纯属浪费（调用方在此期间一直等结果）。
            // 直接放弃，交由上层（Shell 兜底 / 负缓存）处理。
            if (result.TimedOut)
            {
                return new FrameExtractionResult(false, FrameSource.Ffmpeg,
                    $"{lastError}（已放弃后续策略以避免重复等待超时）");
            }
        }

        return new FrameExtractionResult(false, FrameSource.Ffmpeg, lastError);
    }

    /// <summary>
    /// 用 Shell 缩略图接口抽帧（兜底）。
    /// 经 <see cref="ShellThumbnailWorker"/> 串行化到专用 STA 线程执行。
    /// </summary>
    private static FrameExtractionResult ExtractWithShell(string videoPath, string outputPath, int size)
    {
        try
        {
            var bitmap = ShellThumbnailWorker.GetThumbnail(videoPath, size);
            if (bitmap == null)
            {
                return new FrameExtractionResult(false, FrameSource.ShellThumbnail, "系统未能生成缩略图");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var stream = File.Create(outputPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);

            return IsUsableFile(outputPath)
                ? new FrameExtractionResult(true, FrameSource.ShellThumbnail)
                : new FrameExtractionResult(false, FrameSource.ShellThumbnail, "输出文件为空");
        }
        catch (Exception ex)
        {
            return new FrameExtractionResult(false, FrameSource.ShellThumbnail, ex.Message);
        }
    }

    private static bool IsUsableFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删除可能失败留下的残缺输出文件，避免被后续判定误认为成功。</summary>
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
            // 被占用等情况忽略，下次写入会覆盖
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string Trim(string? text, int max = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "未知原因";
        }

        var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }
}

/// <summary>
/// Shell 缩略图的工作线程。
///
/// **必须单线程**：Shell 的缩略图提供程序不是线程安全的，
/// 多线程并发调用会触发访问冲突（0xC0000005）直接崩掉进程（实测 3/3 复现）。
/// 因此这里用「一个专用 STA 线程 + 阻塞队列」把所有请求串行化，
/// 既保证安全，又不影响调用方（调用方可以来自任意线程）。
///
/// 另外，线程内必须显式调用 CoInitializeEx —— 仅设置 STA 单元不会初始化 COM。
/// </summary>
internal static class ShellThumbnailWorker
{
    private sealed record Request(string Path, int Size, TaskCompletionSource<BitmapSource?> Completion);

    private static readonly BlockingCollection<Request> Queue = new();
    private static readonly Lazy<bool> Started = new(StartWorker, isThreadSafe: true);

    public static BitmapSource? GetThumbnail(string path, int size)
    {
        _ = Started.Value;

        var tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Queue.Add(new Request(path, size, tcs));
        }
        catch (InvalidOperationException)
        {
            // 队列已关闭
            return null;
        }

        // 兜底超时，避免 Shell 卡死时调用方永久阻塞
        return tcs.Task.Wait(TimeSpan.FromSeconds(20)) ? tcs.Task.Result : null;
    }

    private static bool StartWorker()
    {
        var thread = new Thread(() =>
        {
            var comInitialized = false;
            try
            {
                var hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                comInitialized = hr is 0 or 1;   // S_OK / S_FALSE

                foreach (var request in Queue.GetConsumingEnumerable())
                {
                    try
                    {
                        request.Completion.TrySetResult(LoadThumbnail(request.Path, request.Size));
                    }
                    catch (Exception)
                    {
                        request.Completion.TrySetResult(null);
                    }
                }
            }
            catch
            {
                // 消费循环异常退出，后续请求会因超时返回 null
            }
            finally
            {
                if (comInitialized)
                {
                    CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "ShellThumbnail"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    private static BitmapSource? LoadThumbnail(string path, int size)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            // 明确只要缩略图，不要图标回退（图标会让哈希失去意义）
            var hr = factory.GetImage(new SIZE { cx = size, cy = size },
                SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out var hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [Flags]
    private enum SIIGBF
    {
        BiggerSizeOk = 0x01,
        ThumbnailOnly = 0x08
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
