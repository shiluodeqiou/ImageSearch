using CommunityToolkit.Mvvm.ComponentModel;

namespace 以图搜图.Models;

/// <summary>
/// 索引队列中单个目录的状态。
/// </summary>
public enum IndexSourceStatus
{
    /// <summary>等待索引。</summary>
    Pending,

    /// <summary>正在索引。</summary>
    Running,

    /// <summary>已完成，重启队列时跳过。</summary>
    Completed,

    /// <summary>本次失败，重启队列时重试。</summary>
    Failed
}

/// <summary>
/// 索引队列中的一个目录条目。
/// </summary>
public partial class IndexSourceItem : ObservableObject
{
    public IndexSourceItem(string directory, IndexSourceStatus status = IndexSourceStatus.Pending)
    {
        Directory = directory;
        this.status = status;
    }

    /// <summary>目录绝对路径。</summary>
    public string Directory { get; }

    [ObservableProperty]
    private IndexSourceStatus status;

    /// <summary>附加信息，例如进度百分比或错误原因。</summary>
    [ObservableProperty]
    private string message = string.Empty;

    /// <summary>本次索引新增的条目数。</summary>
    [ObservableProperty]
    private int addedCount;

    /// <summary>
    /// 该目录的**视频**是否已建立索引。
    ///
    /// 必须与「目录已完成」分开记录：用户可以选「本次不索引视频」，
    /// 此时图片索引完成、目录状态标为 Completed，但视频并没有做。
    /// 若只凭 Completed 判断，日后再勾选「索引视频」时这些目录会被直接跳过，
    /// **视频将永远补不上**。
    /// </summary>
    [ObservableProperty]
    private bool videosIndexed;

    /// <summary>
    /// 该目录的**图片**是否已建立索引。
    /// 与 <see cref="VideosIndexed"/> 同理：只按「是否要处理视频」这一项判断完成度，
    /// 不把它绑死在目录的 Completed 状态上。
    /// </summary>
    [ObservableProperty]
    private bool imagesIndexed;

    public string StatusText => Status switch
    {
        IndexSourceStatus.Pending => "等待",
        IndexSourceStatus.Running => "正在索引",
        IndexSourceStatus.Completed => "已完成",
        IndexSourceStatus.Failed => "失败",
        _ => string.Empty
    };

    /// <summary>供列表右侧显示的状态摘要。</summary>
    public string DisplayStatus => Status switch
    {
        IndexSourceStatus.Running when !string.IsNullOrEmpty(Message) => $"{StatusText} {Message}",
        IndexSourceStatus.Completed when AddedCount > 0 => $"{StatusText}（+{AddedCount:#,0}）",
        IndexSourceStatus.Failed when !string.IsNullOrEmpty(Message) => $"{StatusText}：{Message}",
        _ => StatusText
    };

    partial void OnStatusChanged(IndexSourceStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DisplayStatus));
    }

    partial void OnMessageChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayStatus));
    }

    partial void OnAddedCountChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayStatus));
    }

    /// <summary>重置为可重新执行的状态。</summary>
    public void ResetForRun()
    {
        Message = string.Empty;
        AddedCount = 0;
    }
}
