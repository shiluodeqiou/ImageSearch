using System.IO;
using System.Text.Json;
using 以图搜图.Helpers;

namespace 以图搜图.Services;

/// <summary>
/// 界面偏好（与索引导入无关的轻量设置）的持久化。
///
/// 为什么不写进 config.ini：那个文件的 <c>IniFile.Save()</c> 会把**所有注释抹掉**
/// （实测：中文注释全部丢失，键值本身保留）。config.ini 里的注释正是
/// RunAsAdmin / IndexAutoUpdate / FFmpegPath 这些选项的说明文档，
/// 为了记住一个界面开关而删掉它们不划算。
///
/// 因此这里另存一个文件，完全不碰 config.ini——与 index_sources.json
/// 「新数据一律另存、不动既有文件」的做法一致。
///
/// 注意所有保存都走「读-改-写」：本文件承载多个相互独立的偏好，
/// 若保存时直接写一个只含单个字段的新对象，会把其他偏好抹掉。
/// </summary>
public static class UiPreferences
{
    private const string FileName = "ui_preferences.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object Sync = new();

    /// <summary>窗口是否置顶。默认不置顶。</summary>
    public static bool LoadAlwaysOnTop() => Read().AlwaysOnTop;

    /// <summary>保存窗口置顶偏好。</summary>
    public static void SaveAlwaysOnTop(bool value) => Update(m => m.AlwaysOnTop = value);

    /// <summary>是否索引视频。默认 true（保持原行为：图片与视频都索引）。</summary>
    public static bool LoadIncludeVideos() => Read().IncludeVideos;

    /// <summary>保存「是否索引视频」偏好。</summary>
    public static void SaveIncludeVideos(bool value) => Update(m => m.IncludeVideos = value);

    private static Model Read()
    {
        lock (Sync)
        {
            return ReadUnlocked();
        }
    }

    /// <summary>读出现有偏好、应用改动、再整体写回（保留其他偏好）。</summary>
    private static void Update(Action<Model> mutate)
    {
        try
        {
            lock (Sync)
            {
                var model = ReadUnlocked();
                mutate(model);
                AtomicFile.WriteAllText(FileName, JsonSerializer.Serialize(model, Options));
            }
        }
        catch
        {
            // 写不进去只影响「记住偏好」，功能本身照常生效
        }
    }

    /// <summary>已持有 <see cref="Sync"/> 时读取，避免重入锁。</summary>
    private static Model ReadUnlocked()
    {
        try
        {
            if (!File.Exists(FileName))
            {
                return new Model();
            }

            var json = File.ReadAllText(FileName);
            return string.IsNullOrWhiteSpace(json)
                ? new Model()
                : JsonSerializer.Deserialize<Model>(json, Options) ?? new Model();
        }
        catch
        {
            return new Model();
        }
    }

    private sealed class Model
    {
        public bool AlwaysOnTop { get; set; }

        /// <summary>默认 true：不写该键时按「索引视频」处理。</summary>
        public bool IncludeVideos { get; set; } = true;
    }
}
