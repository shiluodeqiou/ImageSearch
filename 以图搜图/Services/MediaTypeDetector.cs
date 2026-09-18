using System.IO;
using System.Text;

namespace 以图搜图.Services;

/// <summary>文件的真实类型（依据文件头判断）。</summary>
public enum MediaKind
{
    /// <summary>确认不是媒体，例如文本、文档、可执行文件。</summary>
    NotMedia,

    /// <summary>图片。</summary>
    Image,

    /// <summary>视频。</summary>
    Video,

    /// <summary>无法判定（文件头未被识别）。</summary>
    Unknown
}

/// <summary>
/// 该文件按扩展名像是媒体，但实际内容不是媒体。
/// 索引流程遇到它时应静默跳过，而不是计入「解码失败」——
/// 因为它本来就不是要索引的东西，报错只会制造噪声。
/// </summary>
public sealed class NonMediaFileException(string path)
    : Exception($"不是媒体文件：{path}")
{
    public string FilePath { get; } = path;
}

/// <summary>
/// 按文件头（magic bytes）判断文件的真实类型。
///
/// 为什么需要它：**扩展名会骗人**，而且两个方向都有实例——
///   · 「实为媒体、扩展名不像」：微信导出的 `.jpg` 里其实是 HEIC
///   · 「扩展名像媒体、实则不是」：实测本机环境中
///       `.mts` = TypeScript 模块文件（文本）
///       `.hdr` = C 语言头文件
///       `.nef` = NoteExpress 过滤器
///       `.ogm` = Origin 软件的文本文件
///       `.mod` = OLE 复合文档（Nastran 模型）
///     若只按扩展名把这些纳入，会把大量非媒体文件拖进解码流程并产生成片错误。
///
/// 判定顺序：文件头 → 是否文本 → 扩展名兜底。
/// 只读取文件开头的少量字节，代价很低；并且**只对视频扩展名与解码失败的图片调用**，
/// 避免给海量正常图片增加额外 IO。
/// </summary>
public static class MediaTypeDetector
{
    /// <summary>
    /// 读取的头部长度。取 512 字节是为了能校验 MPEG-TS 的周期性同步字节
    /// （包长 188，需要至少跨 3 个包才能可靠识别）。
    /// </summary>
    private const int HeaderSize = 512;

    /// <summary>探测文件的真实类型。</summary>
    public static MediaKind Detect(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var read = stream.Read(header);
            return read <= 0 ? MediaKind.Unknown : Classify(header[..read], path);
        }
        catch
        {
            return MediaKind.Unknown;
        }
    }

    /// <summary>
    /// 对已读取的文件头做分类。
    /// </summary>
    /// <param name="header">文件开头若干字节。</param>
    /// <param name="pathForExtensionFallback">文件头无法判定时用于兜底的路径，可为 null。</param>
    public static MediaKind Classify(ReadOnlySpan<byte> header, string? pathForExtensionFallback = null)
    {
        if (header.Length >= 4)
        {
            // ── 1) 图片容器 ──
            if (IsImageHeader(header))
            {
                return MediaKind.Image;
            }

            // ── 2) 视频容器 ──
            if (IsVideoHeader(header))
            {
                return MediaKind.Video;
            }

            // ── 3) 纯文本一定不是媒体 ──
            // 这一步把 .mts(TypeScript)/.hdr(C头文件)/.ogm(文本) 之类
            // 「扩展名像媒体」的文件干净地排除掉。
            if (LooksLikeText(header))
            {
                return MediaKind.NotMedia;
            }
        }

        // ── 4) 兜底：按扩展名判断 ──
        if (!string.IsNullOrEmpty(pathForExtensionFallback))
        {
            if (Models.VideoFormats.IsVideo(pathForExtensionFallback))
            {
                return MediaKind.Video;
            }

            if (Models.MediaFormats.IsImageExtension(pathForExtensionFallback))
            {
                return MediaKind.Image;
            }
        }

        return MediaKind.Unknown;
    }

    /// <summary>图片容器识别。</summary>
    private static bool IsImageHeader(ReadOnlySpan<byte> h)
    {
        // JPEG（含 jpe/jfif 及「伪装成 .jpg 的 HEIC」以外的真实 JPEG）
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            return true;
        }

        // PNG
        if (h.Length >= 8
            && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47
            && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A)
        {
            return true;
        }

        // GIF（虽已不受支持，但识别出来比当成未知更准确）
        if (h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8')
        {
            return true;
        }

        // BMP
        if (h[0] == 'B' && h[1] == 'M')
        {
            return true;
        }

        // TIFF 家族：II*\0 或 MM\0*
        // 注意 NEF / ARW / DNG 等 RAW 格式同样是 TIFF 结构，
        // 这里统一归为图片，由解码器决定能否处理。
        if ((h[0] == 'I' && h[1] == 'I' && h[2] == 0x2A && h[3] == 0x00)
            || (h[0] == 'M' && h[1] == 'M' && h[2] == 0x00 && h[3] == 0x2A))
        {
            return true;
        }

        // ICO / CUR
        if (h[0] == 0x00 && h[1] == 0x00 && (h[2] == 0x01 || h[2] == 0x02) && h[3] == 0x00)
        {
            return true;
        }

        if (h.Length >= 12)
        {
            // RIFF....WEBP
            if (h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8))
            {
                return true;
            }

            // ISO-BMFF 图片：avif/heic/heif
            if (h.Slice(4, 4).SequenceEqual("ftyp"u8))
            {
                return IsIsoBmffImageBrand(h.Slice(8, 4));
            }
        }

        return false;
    }

    /// <summary>
    /// ISO-BMFF 中属于「静态图片」的品牌；其余品牌按视频处理。
    /// 用字节比较而非转字符串：本方法在图片解码失败的回退路径上会被调用，
    /// 转字符串每次都要分配，属于不必要开销。
    /// </summary>
    private static bool IsIsoBmffImageBrand(ReadOnlySpan<byte> brand)
    {
        if (brand.Length < 4)
        {
            return false;
        }

        // avif/avis = AV1 图片；heic/heix/heim/heis = HEVC 图片；
        // mif1/msf1 = 通用图片文件格式（微信/iPhone 导出常见）
        return brand.SequenceEqual("avif"u8)
            || brand.SequenceEqual("avis"u8)
            || brand.SequenceEqual("heic"u8)
            || brand.SequenceEqual("heix"u8)
            || brand.SequenceEqual("heim"u8)
            || brand.SequenceEqual("heis"u8)
            || brand.SequenceEqual("mif1"u8)
            || brand.SequenceEqual("msf1"u8);
    }

    /// <summary>视频容器识别。</summary>
    private static bool IsVideoHeader(ReadOnlySpan<byte> h)
    {
        // Matroska / WebM
        if (h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3)
        {
            return true;
        }

        // ASF（wmv/wma）
        if (h.Length >= 16
            && h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75
            && h[4] == 0x8E && h[5] == 0x66 && h[6] == 0xCF && h[7] == 0x11)
        {
            return true;
        }

        if (h.Length >= 4)
        {
            // FLV
            if (h[0] == 'F' && h[1] == 'L' && h[2] == 'V' && h[3] == 0x01)
            {
                return true;
            }

            // RealMedia（.rm/.rmvb）
            if (h[0] == '.' && h[1] == 'R' && h[2] == 'M' && h[3] == 'F')
            {
                return true;
            }

            // Ogg（ogv/ogm）
            if (h[0] == 'O' && h[1] == 'g' && h[2] == 'g' && h[3] == 'S')
            {
                return true;
            }

            // MPEG-PS（vob 等）：00 00 01 BA
            if (h[0] == 0x00 && h[1] == 0x00 && h[2] == 0x01 && h[3] == 0xBA)
            {
                return true;
            }
        }

        if (h.Length >= 12)
        {
            // RIFF....AVI
            if (h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("AVI "u8))
            {
                return true;
            }

            // ISO-BMFF 且非图片品牌 → 视频（isom/mp42/qt/M4V/3gp...）
            if (h.Slice(4, 4).SequenceEqual("ftyp"u8) && !IsIsoBmffImageBrand(h.Slice(8, 4)))
            {
                return true;
            }
        }

        // MPEG-TS（ts/mts/m2ts/m2t）：0x47 同步字节每 188 字节出现一次。
        // 必须跨多个包校验，否则任意以 0x47 开头的文件都会被误判。
        return IsMpegTransportStream(h);
    }

    /// <summary>MPEG-TS 识别：校验 188 字节周期上的同步字节。</summary>
    private static bool IsMpegTransportStream(ReadOnlySpan<byte> h)
    {
        const int packetSize = 188;
        if (h.Length < packetSize * 2 || h[0] != 0x47)
        {
            return false;
        }

        // 至少校验 3 个周期，避免偶然命中
        var hits = 0;
        for (var offset = 0; offset + 1 <= h.Length; offset += packetSize)
        {
            if (h[offset] != 0x47)
            {
                return false;
            }

            if (++hits >= 3)
            {
                return true;
            }
        }

        return hits >= 2;
    }

    /// <summary>
    /// 判断内容是否像文本文件。
    /// 媒体都是二进制格式，因此「整段可打印 ASCII」即可安全排除。
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> h)
    {
        if (h.Length == 0)
        {
            return false;
        }

        // 出现 NUL 或大量控制字符即为二进制
        foreach (var b in h)
        {
            if (b == 0)
            {
                return false;
            }

            var printable = b is >= 0x20 and <= 0x7E || b is 0x09 or 0x0A or 0x0D;
            if (!printable)
            {
                // 含非文本字节（可能是 UTF-8 中文等多字节字符）→ 不算纯文本
                return false;
            }
        }

        return true;
    }
}
