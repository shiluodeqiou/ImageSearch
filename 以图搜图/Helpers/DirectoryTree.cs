using System.IO;

namespace 以图搜图.Helpers;

/// <summary>
/// 索引目录的规范化与包含关系判断。
/// 用于队列去重：父目录已入队时不再重复加入其子目录，反之亦然。
/// </summary>
public static class DirectoryTree
{
    /// <summary>规范化目录路径：转绝对路径并去掉末尾分隔符。</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim().TrimEnd('\\', '/'));
        return full.TrimEnd('\\', '/');
    }

    /// <summary>
    /// 判断 <paramref name="child"/> 是否等于 <paramref name="parent"/> 或位于其下。
    /// </summary>
    public static bool IsSameOrUnder(string parent, string child)
    {
        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
