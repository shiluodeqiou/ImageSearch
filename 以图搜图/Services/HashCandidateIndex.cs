namespace 以图搜图.Services;

/// <summary>
/// DCT 哈希的分桶候选索引。
///
/// 用哈希上若干个 8 位窗口把条目分桶，检索时只比对「与查询图至少共享一个桶」的条目，
/// 以此避免对整个索引做全量比对。
///
/// **这是近似剪枝，不是无损过滤。** 两张图只有在 4 个窗口（DctHash 取 bit 0/7/15/23，
/// DctHash64 取 bit 0/11/22/33）**每一个**都不相同时才会被排除，
/// 因此相似度阈值附近的真命中仍有一定概率被漏掉：差异比特越多越容易被漏，
/// 相似度越高越不容易。需要结果绝对完整时不要启用
/// （开关见 <see cref="UiPreferences.LoadUseDctCandidateIndex"/>）。
///
/// 内存开销与条目数成正比：每个条目要往 8 个桶各挂一个路径引用。
/// 索引规模到数百万条时是数百 MB 量级，故默认关闭。
/// </summary>
internal sealed class HashCandidateIndex
{
    /// <summary>DctHash（32 位精度）的窗口起始位。</summary>
    private static readonly int[] DctHashBucketOffsets = [0, 7, 15, 23];

    /// <summary>DctHash64（64 位精度）的窗口起始位。</summary>
    private static readonly int[] DctHash64BucketOffsets = [0, 11, 22, 33];

    private readonly Dictionary<byte, List<string>>[] _dctHashBuckets;
    private readonly Dictionary<byte, List<string>>[] _dctHash64Buckets;

    private HashCandidateIndex(
        Dictionary<byte, List<string>>[] dctHashBuckets,
        Dictionary<byte, List<string>>[] dctHash64Buckets)
    {
        _dctHashBuckets = dctHashBuckets;
        _dctHash64Buckets = dctHash64Buckets;
    }

    /// <summary>按索引条目建桶。传入的是快照，建好后不再依赖调用方的字典。</summary>
    public static HashCandidateIndex Build(KeyValuePair<string, IndexItem>[] entries)
    {
        var dctHashBuckets = CreateBuckets();
        var dctHash64Buckets = CreateBuckets();

        foreach (var (path, item) in entries)
        {
            for (var table = 0; table < DctHashBucketOffsets.Length; table++)
            {
                Add(dctHashBuckets[table], GetBucket(item.DctHash, table, DctHashBucketOffsets), path);
                Add(dctHash64Buckets[table], GetBucket(item.DctHash64, table, DctHash64BucketOffsets), path);
            }
        }

        return new HashCandidateIndex(dctHashBuckets, dctHash64Buckets);
    }

    /// <summary>取与任意一个查询哈希共享桶的候选路径。</summary>
    public HashSet<string> FindCandidates(ulong[] dctHashes, ulong[] dctHash64s)
    {
        var candidates = new HashSet<string>();
        AddCandidates(_dctHashBuckets, dctHashes, DctHashBucketOffsets, candidates);
        AddCandidates(_dctHash64Buckets, dctHash64s, DctHash64BucketOffsets, candidates);
        return candidates;
    }

    // 两组哈希各建一组表，窗口数量取两者中较大者，保证表数一致
    private static Dictionary<byte, List<string>>[] CreateBuckets()
    {
        return Enumerable.Range(0, DctHash64BucketOffsets.Length)
            .Select(_ => new Dictionary<byte, List<string>>())
            .ToArray();
    }

    private static void Add(Dictionary<byte, List<string>> buckets, byte bucket, string path)
    {
        if (!buckets.TryGetValue(bucket, out var paths))
        {
            paths = [];
            buckets[bucket] = paths;
        }

        paths.Add(path);
    }

    private static void AddCandidates(
        Dictionary<byte, List<string>>[] buckets,
        ulong[] hashes,
        int[] bucketOffsets,
        HashSet<string> candidates)
    {
        foreach (var hash in hashes)
        {
            for (var table = 0; table < bucketOffsets.Length; table++)
            {
                if (buckets[table].TryGetValue(GetBucket(hash, table, bucketOffsets), out var paths))
                {
                    candidates.UnionWith(paths);
                }
            }
        }
    }

    /// <summary>取哈希上某个窗口的 8 位值作为桶键。</summary>
    private static byte GetBucket(ulong hash, int table, int[] bucketOffsets)
    {
        return (byte)(hash >> bucketOffsets[table]);
    }
}
