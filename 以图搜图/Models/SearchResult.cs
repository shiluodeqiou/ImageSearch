namespace 以图搜图.Models;

using System.IO;

public record SearchResult
{
    public string 路径 { get; set; } = string.Empty;
    public float 匹配度 { get; set; }
    public string 匹配算法 { get; set; }

    public string 大小 { get; set; } = string.Empty;
    public string 所属文件夹大小 { get; set; } = string.Empty;
    public int 所属文件夹文件数 { get; set; }

    /// <summary>是否为视频文件（索引的是其代表帧）。</summary>
    public bool 是视频 { get; set; }

    /// <summary>类型标签，列表与预览区共用。</summary>
    public string 类型标签 => 是视频 ? "🎬 视频" : "🖼️ 图片";

    /// <summary>
    /// 文件所在文件夹的完整路径。
    ///
    /// 检索的常见用途是「同一张图散落在哪些文件夹」，因此结果列表按它分组呈现，
    /// 一眼就能看清命中的是几个不同位置，不必逐行比对完整路径。
    /// </summary>
    public string 所在文件夹 => Path.GetDirectoryName(路径) ?? string.Empty;

    /// <summary>文件名（不含文件夹）。列表按文件夹分组后，每行只需显示文件名。</summary>
    public string 文件名 => Path.GetFileName(路径);

    /// <summary>Indicates whether the current object is equal to another object of the same type.</summary>
    /// <param name="other">An object to compare with this object.</param>
    /// <returns>
    /// <see langword="true" /> if the current object is equal to the <paramref name="other"/> parameter; otherwise, <see langword="false" />.</returns>
    public virtual bool Equals(SearchResult? other)
    {
        return 路径 == other?.路径;
    }

    /// <summary>
    /// 与 <see cref="Equals(SearchResult?)"/> 保持一致的哈希（按路径）。
    ///
    /// 必须实现：只重写 Equals 而不重写 GetHashCode 会破坏 .NET 的相等性契约——
    /// 两个「相等」的对象会落到不同的哈希桶，导致 GroupBy / Distinct / Dictionary
    /// 出现元素被静默丢失等难以排查的行为。
    /// 本项目的分组（按所在文件夹）与去重都依赖它。
    /// </summary>
    public override int GetHashCode()
    {
        return 路径?.GetHashCode() ?? 0;
    }
}
