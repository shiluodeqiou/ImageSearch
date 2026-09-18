namespace 以图搜图.Models;

/// <summary>
/// 受支持的图片格式。
///
/// 设计约定：**扩展名清单只写一份**（<see cref="ExtensionsList"/>），
/// 其余成员全部由它派生。这样新增格式时只需改一处，不会出现
/// 「正则里有、Everything 过滤串里没有」这类不一致——历史上正是这种
/// 不一致导致 webp 长期被静默跳过。
///
/// 注意：静态字段的初始化顺序有意义（后声明的依赖先声明的），
/// 新增派生成员时请追加在 <see cref="Extensions"/> 之后。
/// </summary>
public static class ImageFormats
{
    /// <summary>
    /// 唯一格式清单（分号分隔的小写扩展名，不含点号）。
    ///
    /// 收录标准：能被 SkiaSharp 或 Windows WIC 实际解码。
    ///   jpg/jpeg/jpe/jfif —— JPEG 及其常见别名
    ///   png/bmp/webp       —— 常规位图
    ///   avif/heic/heif     —— 现代压缩格式（WIC 支持，需系统装有对应解码扩展）
    ///   tif/tiff           —— 含部分 RAW 相机格式（NEF/ARW/DNG 同为 TIFF 结构）
    ///   ico                —— 图标，本质是位图
    ///   gif                —— **只索引首帧**
    ///
    /// 关于 GIF：早期版本曾完整支持 GIF 多帧索引（独立的 frame_index.json），
    /// 后因架构复杂度被移除。现在按「只取首帧」重新纳入——
    /// 此时 GIF 就是一张普通图片，直接复用图片解码与哈希流程，
    /// 不需要恢复多帧机制，也不产生额外的索引文件。
    /// 实测 SkiaSharp 对多帧 GIF 的 DecodeGrayThumb 恰好返回首帧，
    /// 因此无需任何特殊处理。
    ///
    /// 未收录（实测无法解码或无检索意义）：
    ///   dds/tga/exr/jxl/psd —— 缺少解码器（多为开发/3D 资产）
    ///   svg                 —— 矢量图，感知哈希对其无意义
    /// </summary>
    public const string ExtensionsList = "jpg;jpeg;jpe;jfif;png;bmp;webp;avif;heic;heif;tif;tiff;ico;gif";

    /// <summary>扩展名数组（小写，不含点号）。</summary>
    public static readonly string[] Extensions = ExtensionsList.Split(';');

    /// <summary>Everything 查询使用的扩展名过滤串。</summary>
    public const string EverythingFilter = ExtensionsList;

    /// <summary>用于正则匹配的扩展名分支（不含锚点，按长度降序有利于匹配效率）。</summary>
    public static readonly string RegexAlternation =
        string.Join("|", Extensions.OrderByDescending(e => e.Length));

    /// <summary>文件选择对话框的过滤器片段。</summary>
    public static readonly string DialogFilterPart =
        string.Join(";", Extensions.Select(e => "*." + e));
}

/// <summary>
/// 受支持的视频格式。
///
/// 视频不直接计算哈希，而是先抽取一帧代表画面（缩略图），
/// 再走与图片完全相同的哈希流程，因此可以复用同一个索引结构。
/// </summary>
public static class VideoFormats
{
    /// <summary>
    /// 唯一格式清单。收录标准：ffmpeg 能解码且能抽帧。
    ///
    ///   mp4/mov/m4v/3gp —— ISO-BMFF 家族
    ///   avi/divx         —— AVI 容器（divx 是 AVI 的常见别名）
    ///   mkv/webm         —— Matroska 家族
    ///   wmv/asf          —— Windows Media
    ///   mpg/mpeg/m1v     —— MPEG-1/2 视频流
    ///   ts/mts/m2ts/m2t  —— MPEG-TS 家族（含 AVCHD 摄像机）
    ///   vob              —— DVD 的 MPEG-PS
    ///   flv/rmvb         —— 网络流媒体
    ///
    /// 未收录：
    ///   swf  —— 实测该 ffmpeg 构建无法抽帧
    ///   gif  —— 已归入图片格式（按「只索引首帧」处理，无需走 ffmpeg 抽帧）
    ///
    /// 关于 rm（.rm）：暂未收录。注意**不要**误以为该 ffmpeg 构建缺少 RealVideo
    /// 解码器——实测它含 rv10~rv60，取首帧可成功。此前"缺解码器"的结论是错判，
    /// 真正原因是定位策略（详见 VideoFrameExtractor 的策略递进说明）。
    /// 如需纳入 rm，加入下面的列表即可，无需更换 ffmpeg 构建。
    /// </summary>
    public const string ExtensionsList =
        "mp4;mov;m4v;3gp;avi;divx;mkv;webm;wmv;asf;mpg;mpeg;m1v;ts;mts;m2ts;m2t;vob;flv;rmvb";

    /// <summary>扩展名数组（小写，不含点号）。</summary>
    public static readonly string[] Extensions = ExtensionsList.Split(';');

    /// <summary>Everything 查询使用的扩展名过滤串。</summary>
    public const string EverythingFilter = ExtensionsList;

    /// <summary>用于正则匹配的扩展名分支（不含锚点）。</summary>
    public static readonly string RegexAlternation =
        string.Join("|", Extensions.OrderByDescending(e => e.Length));

    /// <summary>文件选择对话框的过滤器片段。</summary>
    public static readonly string DialogFilterPart =
        string.Join(";", Extensions.Select(e => "*." + e));

    // 用哈希集合做 O(1) 判断：IsVideo 在每个文件的处理路径上都会被调用
    // （百万级次数），逐个比较扩展名数组会明显拖慢索引。
    private static readonly HashSet<string> ExtensionSet =
        new(Extensions, StringComparer.OrdinalIgnoreCase);

    // 支持 ReadOnlySpan<char> 查询的查找器，避免为取扩展名分配字符串。
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ExtensionLookup =
        ExtensionSet.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>判断扩展名是否为受支持的视频（零分配）。</summary>
    public static bool IsVideo(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var dot = path.LastIndexOf('.');
        return dot >= 0 && dot < path.Length - 1 && ExtensionLookup.Contains(path.AsSpan(dot + 1));
    }
}

/// <summary>
/// 全部受支持媒体格式的统一入口。
/// 索引、搜索、Everything 过滤等处都从这里取，避免各写一份导致不一致。
/// </summary>
public static class MediaFormats
{
    /// <summary>
    /// Everything 查询过滤串（图片 + 视频）。
    /// const 形式，可用于方法的默认参数值。
    /// </summary>
    public const string EverythingFilterConst = ImageFormats.EverythingFilter + ";" + VideoFormats.EverythingFilter;

    /// <summary>匹配所有受支持格式的正则（含行尾锚点）。</summary>
    public static readonly string RegexPattern =
        $"({ImageFormats.RegexAlternation}|{VideoFormats.RegexAlternation})$";

    /// <summary>文件选择对话框的过滤器。</summary>
    public static readonly string DialogFilter =
        $"图片文件|{ImageFormats.DialogFilterPart}" +
        $"|视频文件|{VideoFormats.DialogFilterPart}" +
        "|所有文件|*.*";

    private static readonly HashSet<string> ImageExtensionSet =
        new(ImageFormats.Extensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ImageExtensionLookup =
        ImageExtensionSet.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>判断扩展名是否为受支持的图片（零分配）。</summary>
    public static bool IsImageExtension(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var dot = path.LastIndexOf('.');
        return dot >= 0 && dot < path.Length - 1 && ImageExtensionLookup.Contains(path.AsSpan(dot + 1));
    }
}
