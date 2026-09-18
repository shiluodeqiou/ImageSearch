using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Masuit.Tools.Media;
using SkiaSharp;
using 以图搜图.Models;

namespace 以图搜图.Services;

/// <summary>
/// 统一的图片解码入口。
///
/// 主路径使用 SkiaSharp（快），但它不支持 HEIC；而微信/iPhone 导出的相册里
/// 大量文件是"扩展名为 .jpg、内容实为 HEIC"，直接解码会失败。
/// 因此这里在 SkiaSharp 失败时回退到 Windows 自带的 WIC 解码器，
/// 并把结果统一转换为与主路径一致的 Gray8 位图，保证哈希计算的输入规格相同。
/// </summary>
public static class ImageDecoder
{
    /// <summary>
    /// 解码为最长边不超过 <paramref name="targetSize"/> 的灰度图。
    ///
    /// 图片走正常解码；视频会先抽取一帧缩略图再解码，
    /// 这样视频与图片可以共用同一套哈希与索引结构。
    ///
    /// 类型判定结合扩展名与文件头：扩展名会骗人，两个方向都有实际案例，
    /// 详见 <see cref="MediaTypeDetector"/>。
    /// </summary>
    /// <exception cref="NonMediaFileException">
    /// 文件按扩展名像媒体，但实际不是媒体（例如 .mts 的 TypeScript 文件）。
    /// 调用方应静默跳过，不要计入「解码失败」。
    /// </exception>
    /// <exception cref="InvalidOperationException">是媒体但无法处理。</exception>
    public static SKBitmap DecodeGrayThumb(string path, int targetSize)
    {
        // 扩展名说是视频：先核对真实类型。
        // 视频处理代价高（要起 ffmpeg），且存在同名非视频文件，
        // 所以这里的嗅探是必要的；而视频数量远少于图片，开销可忽略。
        if (VideoFormats.IsVideo(path))
        {
            var kind = MediaTypeDetector.Detect(path);

            if (kind == MediaKind.NotMedia)
            {
                throw new NonMediaFileException(path);
            }

            // 明确是图片（扩展名骗人，如 .mp4 里其实是图片）→ 按图片解码
            if (kind != MediaKind.Image)
            {
                var thumbnail = VideoThumbnailCache.GetOrCreate(path, targetSize)
                    ?? throw new InvalidOperationException("无法从视频中提取预览帧");

                return DecodeGrayThumbFromImage(thumbnail, targetSize);
            }

            return DecodeGrayThumbFromImage(path, targetSize);
        }

        // 图片扩展名：直接按图片解码。
        // 这里刻意不做嗅探——海量正常图片不该为此多付一次文件打开的代价。
        try
        {
            return DecodeGrayThumbFromImage(path, targetSize);
        }
        catch (NonMediaFileException)
        {
            throw;
        }
        catch
        {
            // 解码失败后再嗅探一次：可能是「扩展名写成图片、实为视频」的情况
            // （微信导出中确实存在），也可能是根本不是媒体文件。
            var kind = MediaTypeDetector.Detect(path);

            if (kind == MediaKind.NotMedia)
            {
                throw new NonMediaFileException(path);
            }

            if (kind == MediaKind.Video)
            {
                var thumbnail = VideoThumbnailCache.GetOrCreate(path, targetSize);
                if (thumbnail != null)
                {
                    return DecodeGrayThumbFromImage(thumbnail, targetSize);
                }
            }

            throw;
        }
    }

    /// <summary>按图片路径解码，SkiaSharp 失败时回退到 WIC。</summary>
    private static SKBitmap DecodeGrayThumbFromImage(string path, int targetSize)
    {
        try
        {
            return SkiaImageHelper.DecodeGrayThumb(path, targetSize);
        }
        catch
        {
            // 交由 WIC 重试；仍失败则抛出，由调用方按"无法解码"计入错误列表
            return DecodeGrayThumbViaWic(path, targetSize);
        }
    }

    /// <summary>从流解码为最长边不超过 <paramref name="targetSize"/> 的灰度图。</summary>
    public static SKBitmap DecodeGrayThumb(Stream stream, int targetSize)
    {
        var origin = stream.CanSeek ? stream.Position : 0;
        try
        {
            return SkiaImageHelper.DecodeGrayThumb(stream, targetSize);
        }
        catch
        {
            if (stream.CanSeek)
            {
                stream.Seek(origin, SeekOrigin.Begin);
            }
            else
            {
                throw;
            }
        }

        try
        {
            return DecodeGrayThumbViaWic(stream, targetSize);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法解码图像", ex);
        }
    }

    /// <summary>
    /// 使用 WIC 解码并转换为 Gray8 位图。
    /// 输出规格与 SkiaSharp 路径保持一致：Gray8 / Opaque，最长边等于 targetSize。
    /// </summary>
    private static SKBitmap DecodeGrayThumbViaWic(string path, int targetSize)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return DecodeGrayThumbViaWic(stream, targetSize);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法解码图像", ex);
        }
    }

    private static SKBitmap DecodeGrayThumbViaWic(Stream stream, int targetSize)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException("无法解码图像");
        }

        BitmapSource frame = decoder.Frames[0];

        // 缩放到最长边不超过 targetSize，保持宽高比
        var scale = (double)targetSize / Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (scale < 1)
        {
            var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            transformed.Freeze();
            frame = transformed;
        }

        // 统一转为 8bpp 灰度，与 SkiaSharp 路径的像素格式一致
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var width = gray.PixelWidth;
        var height = gray.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("无法解码图像");
        }

        var stride = width; // Gray8 每像素 1 字节
        var buffer = new byte[stride * height];
        gray.CopyPixels(buffer, stride, 0);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        buffer.AsSpan().CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    /// <summary>
    /// 获取图片尺寸。SkiaSharp 无法识别的格式（如 HEIC）回退到 WIC。
    /// </summary>
    public static bool TryGetImageSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var codec = SKCodec.Create(path);
            if (codec != null)
            {
                width = codec.Info.Width;
                height = codec.Info.Height;
                return width > 0 && height > 0;
            }
        }
        catch
        {
            // 继续尝试 WIC
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
                return width > 0 && height > 0;
            }
        }
        catch
        {
            // 两种方式都失败
        }

        return false;
    }
}
