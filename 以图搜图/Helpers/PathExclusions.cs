namespace 以图搜图.Helpers;

/// <summary>
/// 路径排除规则：识别那些「不该被当作素材」的位置。
///
/// 目前只有一类：回收站。回收站里的照片是**已被用户删除**的东西，
/// 把它们索引进来会造成两种困扰：
///   1. 搜索结果里冒出用户以为早就删掉的文件；
///   2. 这些文件随时可能被系统真正清空，索引很快变成无效条目。
/// </summary>
public static class PathExclusions
{
    /// <summary>
    /// 判断路径是否位于回收站内。
    ///
    /// 识别两种形态（按段比较，不区分子目录层级）：
    ///   · <c>$Recycle.Bin</c>  —— Vista 及以后每个分区下的回收站目录（如 C:\$Recycle.Bin\S-1-5-21-...\）
    ///   · <c>RECYCLER</c>      —— XP/2000 时代的回收站目录，且只在盘符根目录下才算
    ///
    /// 为什么按「路径段」而不是子串匹配：形如 <c>D:\照片\$Recycle.Bin备份\a.jpg</c>
    /// 里的 "Recycle" 只是文件名的一部分，不该被排除；而完整段相等才是真的回收站目录。
    ///
    /// 这个判断会在枚举每个文件时被调用（百万级次数），因此用 span 扫描、不产生分配。
    /// </summary>
    public static bool IsInRecycleBin(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return IsInRecycleBin(path.AsSpan());
    }

    /// <inheritdoc cref="IsInRecycleBin(string)"/>
    public static bool IsInRecycleBin(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            return false;
        }

        var segmentStart = 0;
        var segmentIndex = 0;

        for (var i = 0; i <= path.Length; i++)
        {
            var atEnd = i == path.Length;
            if (!atEnd && path[i] != '\\' && path[i] != '/')
            {
                continue;
            }

            var segment = path[segmentStart..i];

            // $Recycle.Bin：现代回收站，出现在任意层级都算（它总是位于分区根下）
            if (segment.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // RECYCLER：XP 时代回收站。只在「盘符根目录」这一层认定，
            // 避免把用户自己建的普通文件夹（名字恰为 recycler）一并排除。
            // 段序号 1 即为紧跟盘符（如 C:）之后的那一段。
            if (segmentIndex == 1 && segment.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            segmentIndex++;
            segmentStart = i + 1;
        }

        return false;
    }
}
