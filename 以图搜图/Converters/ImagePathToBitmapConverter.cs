using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using 以图搜图.Models;
using 以图搜图.Services;

namespace 以图搜图.Converters;

/// <summary>
/// 把文件路径转换为可显示的位图。
///
/// 视频无法被 BitmapImage 直接加载，因此这里改指它的缩略图缓存文件。
/// 缩略图不存在时返回 null（界面显示空白而非抛异常）；
/// 注意此处只查缓存、不触发抽帧，避免绑定过程阻塞界面。
/// </summary>
public class ImagePathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (VideoFormats.IsVideo(path))
        {
            var thumbnail = VideoThumbnailCache.GetCached(path);
            if (string.IsNullOrEmpty(thumbnail))
            {
                return null;
            }

            path = thumbnail;
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // 关键:加载后立即释放文件
            bitmap.DecodePixelWidth = 800; // 限制解码宽度以节省内存
            bitmap.EndInit();
            bitmap.Freeze(); // 冻结以提高性能并允许跨线程访问
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
