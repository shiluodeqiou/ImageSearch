using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using 以图搜图.Helpers;
using 以图搜图.Models;

namespace 以图搜图.Services;

/// <summary>
/// 索引源（索引目录队列）的持久化。
/// 只保存"有哪些索引目录、队列顺序、完成情况"，完全不触碰 index.json。
/// </summary>
public static class IndexSourceStore
{
    private const string FileName = "index_sources.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>读取索引源列表；文件不存在或损坏时返回空列表。</summary>
    public static List<IndexSourceItem> Load()
    {
        try
        {
            if (!File.Exists(FileName))
            {
                return new List<IndexSourceItem>();
            }

            var json = File.ReadAllText(FileName);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<IndexSourceItem>();
            }

            var model = JsonSerializer.Deserialize<IndexSourcesFile>(json, Options);
            if (model?.Directories == null)
            {
                return new List<IndexSourceItem>();
            }

            // 旧版本只记「已完成的目录」。为了兼容，把那些目录视为
            // 图片与视频都已索引（旧版本确实是两者一起做的）。
            var imagesDone = new HashSet<string>(model.ImagesIndexed ?? [], StringComparer.OrdinalIgnoreCase);
            var videosDone = new HashSet<string>(model.VideosIndexed ?? [], StringComparer.OrdinalIgnoreCase);
            var legacyCompleted = new HashSet<string>(model.Completed ?? [], StringComparer.OrdinalIgnoreCase);

            var items = new List<IndexSourceItem>();
            foreach (var dir in model.Directories.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                var isLegacyDone = legacyCompleted.Contains(dir);
                var item = new IndexSourceItem(
                    dir,
                    isLegacyDone ? IndexSourceStatus.Completed : IndexSourceStatus.Pending)
                {
                    ImagesIndexed = isLegacyDone || imagesDone.Contains(dir),
                    VideosIndexed = isLegacyDone || videosDone.Contains(dir)
                };

                items.Add(item);
            }

            return items;
        }
        catch
        {
            // 索引源文件损坏不应阻止程序启动，按空队列处理
            return new List<IndexSourceItem>();
        }
    }

    /// <summary>保存索引源列表。</summary>
    public static void Save(IEnumerable<IndexSourceItem> items)
    {
        try
        {
            var list = items.ToList();
            var model = new IndexSourcesFile
            {
                Directories = list.Select(i => i.Directory).ToList(),
                // 仍写出 completed：字面含义是「图片与视频都已完成」，
                // 便于旧版本回读时不至于把已完成的目录当成待办。
                Completed = list
                    .Where(i => i.ImagesIndexed && i.VideosIndexed)
                    .Select(i => i.Directory).ToList(),
                ImagesIndexed = list.Where(i => i.ImagesIndexed).Select(i => i.Directory).ToList(),
                VideosIndexed = list.Where(i => i.VideosIndexed).Select(i => i.Directory).ToList()
            };

            AtomicFile.WriteAllText(FileName, JsonSerializer.Serialize(model, Options));
        }
        catch
        {
            // 保存失败不影响索引本身
        }
    }

    private sealed class IndexSourcesFile
    {
        /// <summary>索引目录，按队列顺序。</summary>
        public List<string> Directories { get; set; } = new();

        /// <summary>
        /// 上次运行已完成的目录（图片与视频都已索引）。
        /// 保留此字段是为了与旧版本互相兼容：旧版本只认它。
        /// </summary>
        public List<string> Completed { get; set; } = new();

        /// <summary>图片已完成索引的目录。</summary>
        public List<string> ImagesIndexed { get; set; } = new();

        /// <summary>视频已完成索引的目录。</summary>
        public List<string> VideosIndexed { get; set; } = new();
    }
}
