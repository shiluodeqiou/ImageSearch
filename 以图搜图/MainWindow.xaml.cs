using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using 以图搜图.ViewModels;

namespace 以图搜图;

public partial class MainWindow
{
    private Polygon? _speedPolygon;

    public MainWindow()
    {
        InitializeComponent();

        // 订阅 ViewModel 的 SpeedHistory 变化
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 创建面积图 Polygon
        _speedPolygon = new Polygon
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x7A, 0xCC))
        };
        SpeedChartCanvas.Children.Add(_speedPolygon);

        if (DataContext is MainViewModel vm)
        {
            vm.SpeedHistory.CollectionChanged += SpeedHistory_CollectionChanged;
        }
    }

    /// <summary>
    /// 结果列表尺寸变化时，把「文件名」列调成吸收剩余宽度。
    ///
    /// 为什么不直接用星号列：WPF 的 DataGrid 在启用分组时无法解析星号宽度，
    /// 会把所有列压到最小列宽 20px（实测：8 列全部 20px，界面不可用）。
    /// 因此列用固定像素宽，再由这里按可用宽度手动分配余量，效果等价且不触发该问题。
    /// </summary>
    private void SearchResultGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not DataGrid { Columns.Count: > 0 } grid)
        {
            return;
        }

        // 用事件里的新宽度，而不是 grid.ActualWidth：
        // 尺寸变化回调触发时 ActualWidth 可能还是旧值，按旧值算出的余量会让
        // 列宽合计超出实际可用宽度，从而冒出不该有的横向滚动条（实测超出 63px）。
        var total = e.NewSize.Width;

        // 其余列的宽度用「声明值」（Width.DisplayValue）而非 ActualWidth：
        // 声明值稳定且就是它们最终占用的像素数，不受布局时序影响。
        // 以列索引 0 跳过第一列，而非按表头名查找——第一列始终是文件名，
        // 后面各列调整顺序（例如「操作」列挪到文件名之后）都无需改这里。
        var others = grid.Columns.Skip(1).Sum(c => c.Width.DisplayValue);
        var available = total - others - SystemParameters.VerticalScrollBarWidth - 4;

        // 给个下限，避免窗口很窄时第一列被压没
        grid.Columns[0].Width = new DataGridLength(Math.Max(160, available));
    }

    private void SpeedHistory_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateSpeedChart();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void UpdateSpeedChart()
    {
        if (_speedPolygon == null || ViewModel.SpeedHistory.Count == 0)
        {
            if (_speedPolygon != null)
            {
                _speedPolygon.Points.Clear();
                _speedPolygon.Points.Add(new Point(0, 80));
            }
            return;
        }

        var speeds = ViewModel.SpeedHistory.ToArray();
        var maxSpeed = speeds.Max();
        if (maxSpeed <= 0) maxSpeed = 1;

        var points = new PointCollection();

        var width = SpeedChartCanvas.ActualWidth;
        var height = SpeedChartCanvas.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            width = 500;
            height = 80;
        }

        // 起始点(左下角)
        points.Add(new Point(0, height));

        // 绘制数据点
        var step = speeds.Length > 1 ? width / (speeds.Length - 1) : 0;

        for (int i = 0; i < speeds.Length; i++)
        {
            var x = i * step;
            var y = height - (speeds[i] / maxSpeed * height * 0.9); // 留10%边距
            points.Add(new Point(x, y));
        }

        // 结束点(右下角)
        points.Add(new Point((speeds.Length - 1) * step, height));

        _speedPolygon.Points = points;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        await ViewModel.HandleDrop(e.Data);
    }

    /// <summary>
    /// 判断拖入的数据是否是我们能处理的类型（用于决定鼠标"可放置"光标）。
    /// </summary>
    private static DragDropEffects GetDropEffect(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop)
            || data.GetDataPresent("FileContents")
            || data.GetDataPresent(DataFormats.Bitmap)
            || data.GetDataPresent(DataFormats.Dib)
            || data.GetDataPresent(DataFormats.Text))
        {
            return DragDropEffects.Copy;
        }

        return DragDropEffects.None;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = GetDropEffect(e.Data);
        e.Handled = true;
    }

    /// <summary>
    /// 拖拽移动过程中必须持续确认效果。
    ///
    /// WPF（OLE）对每一次 DragOver 都以 <c>Effects = None</c> 重新发起询问，
    /// 只在 DragEnter 里设置效果是**无效的**：鼠标一动，DragOver 无人处理就返回
    /// "不允许放置"，光标变成禁止符号，且 <see cref="Window_Drop"/> 永远不会被触发。
    /// 这正是「界面提示可以拖放、实际拖了毫无反应」的原因。
    /// </summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDropEffect(e.Data);
        e.Handled = true;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var items = (string[])e.Data.GetData(DataFormats.FileDrop);
            var dirs = items.Where(Directory.Exists).ToList();
            if (dirs.Count > 0)
            {
                ViewModel.AddDirectories(dirs);
                // 只有真正被本区域消费掉才标记 Handled：
                // 否则拖入图片/视频时会被这里静默吞掉，窗口级的搜索拖放收不到事件
                // （表现为"拖到队列列表中那片区域完全没反应"）。
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// 队列列表只接受文件夹。拖入非文件夹时不处理，让事件冒泡给窗口，
    /// 由窗口按「拖放图片/视频搜索」处理。
    /// </summary>
    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items != null && items.Any(Directory.Exists))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }

        // 不设置 Handled：冒泡到 Window_DragOver 决定效果
    }

    private void TxtPic_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                ViewModel.ImagePath = files[0];
                e.Handled = true;
            }
        }
    }

    private void Txt_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Txt_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void DataGrid_KeyUp(object sender, KeyEventArgs e)
    {
        ViewModel.HandleDataGridKeyUp(e.Key, Keyboard.Modifiers);
    }

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedResult != null)
        {
            if (File.Exists(ViewModel.SelectedResult.路径))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ViewModel.SelectedResult.路径,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show(this, "文件不存在，可能发生了移动", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SourceImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrEmpty(ViewModel.SourceImagePath) && File.Exists(ViewModel.SourceImagePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.SourceImagePath,
                UseShellExecute = true
            });
        }
    }

    private void DestImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedResult != null && File.Exists(ViewModel.SelectedResult.路径))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.SelectedResult.路径,
                UseShellExecute = true
            });
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.SearchFromClipboardCommand.Execute(null);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!ViewModel.CanClose())
        {
            MessageBox.Show(this, "正在加载索引、索引或写入文件，请稍后再试", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // DataContext 在 XAML 中创建，窗口关闭时由这里负责释放
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}