using System.Windows;
using System.Windows.Controls;
using 以图搜图.Services;

namespace 以图搜图;

/// <summary>
/// 清理无效索引的预览对话框。
/// 只有"确认文件不存在"的条目会被删除；检查失败的条目一律保留。
/// </summary>
public partial class RemoveInvalidIndexDialog : Window
{
    private const int MaxDisplayItems = 500;

    private readonly InvalidIndexScanReport _report;

    /// <summary>用户是否确认删除。</summary>
    public bool Confirmed { get; private set; }

    public RemoveInvalidIndexDialog(InvalidIndexScanReport report)
    {
        InitializeComponent();

        _report = report;
        Owner = Application.Current.MainWindow;

        TotalText.Text = report.TotalCount.ToString("#,0");
        MissingText.Text = report.ConfirmedMissing.Count.ToString("#,0");
        FailedText.Text = report.CheckFailed.Count.ToString("#,0");
        ValidText.Text = report.ValidCount.ToString("#,0");

        MissingList.ItemsSource = report.ConfirmedMissing.Take(MaxDisplayItems).ToList();
        if (report.ConfirmedMissing.Count > MaxDisplayItems)
        {
            TruncateHint.Visibility = Visibility.Visible;
            TruncateHint.Text = $"为避免界面卡顿，仅显示前 {MaxDisplayItems:#,0} 条；实际将处理 {report.ConfirmedMissing.Count:#,0} 条。";
        }

        if (report.CheckFailed.Count == 0)
        {
            ViewFailedButton.Visibility = Visibility.Collapsed;
        }

        SetupByPolicy();

        if (report.WasCancelled)
        {
            WarningBox.Visibility = Visibility.Visible;
            WarningTitle.Text = "检查未完成";
            WarningBody.Text = "检查过程被中断，结果不完整，建议重新检查后再决定是否清理。";
        }
    }

    private void SetupByPolicy()
    {
        var missing = _report.ConfirmedMissing.Count;
        var policy = _report.Policy;

        if (missing == 0)
        {
            WarningBox.Visibility = Visibility.Visible;
            WarningBox.Background = System.Windows.Media.Brushes.White;
            WarningBox.BorderBrush = System.Windows.Media.Brushes.LightGray;
            WarningTitle.Text = "没有需要清理的索引";
            WarningTitle.Foreground = System.Windows.Media.Brushes.DimGray;
            WarningBody.Text = "所有索引对应的文件都确认存在（或无法检查而被保留）。";
            ConfirmButton.IsEnabled = false;
            ConfirmButton.Content = "无可删除";
            return;
        }

        switch (policy)
        {
            case CleanupPolicy.Normal:
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Content = $"确认删除 {missing:#,0} 条";
                break;

            case CleanupPolicy.Warn:
                WarningBox.Visibility = Visibility.Visible;
                WarningTitle.Text = $"注意：待删除数量占索引总数的 {_report.MissingRatio:P1}，高于安全比例 {InvalidIndexScanner.WarnRatio:P0}";
                WarningBody.Text = $"本次将删除 {missing:#,0} 条索引（共 {_report.TotalCount:#,0} 条）。请确认这些文件确实已被删除（例如更换了硬盘、整理过目录）。";
                ConfirmButton.IsEnabled = true;
                ConfirmButton.Content = $"确认删除 {missing:#,0} 条";
                break;

            case CleanupPolicy.Block:
                // 比例异常：给出最醒目的警告，并且「确认删除」默认禁用，
                // 必须勾选确认框才可执行（安全阈值要求的「默认阻止一键删除」）。
                WarningBox.Visibility = Visibility.Visible;
                WarningBox.Background = System.Windows.Media.Brushes.MistyRose;
                WarningBox.BorderBrush = System.Windows.Media.Brushes.IndianRed;
                WarningTitle.Foreground = System.Windows.Media.Brushes.DarkRed;
                WarningTitle.Text = $"严重警告：待删除数量占索引总数的 {_report.MissingRatio:P1}，远超安全比例 {InvalidIndexScanner.BlockRatio:P0}";
                WarningBody.Text = $"本次将删除 {missing:#,0} 条索引（共 {_report.TotalCount:#,0} 条）。\r\n" +
                                   "这样大的比例通常意味着磁盘离线、权限变化或扫描异常，而不是文件真的被删除。\r\n" +
                                   "请务必确认这些文件确实已被删除后再继续。";
                ConfirmButton.Content = $"确认删除 {missing:#,0} 条";
                ConfirmButton.IsEnabled = false;
                AcknowledgeCheck.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>勾选状态变化时重新评估「确认删除」是否可用。</summary>
    private void Acknowledge_Changed(object sender, RoutedEventArgs e)
    {
        if (_report.Policy != CleanupPolicy.Block)
        {
            return;
        }

        ConfirmButton.IsEnabled = AcknowledgeCheck.IsChecked == true;
    }

    private void ViewFailed_Click(object sender, RoutedEventArgs e)
    {
        var lines = _report.CheckFailed.Take(1000).Select(f => $"{f.Path}\r\n    原因：{f.Reason}");
        var text = $"以下 {_report.CheckFailed.Count:#,0} 条索引因无法完成检查而将被保留：\r\n\r\n" + string.Join("\r\n", lines);
        if (_report.CheckFailed.Count > 1000)
        {
            text += $"\r\n\r\n（仅显示前 1000 条）";
        }

        new ErrorsDialog(text).ShowDialog();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
