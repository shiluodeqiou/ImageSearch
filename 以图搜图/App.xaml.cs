using System.Diagnostics;
using System.Windows;
using Masuit.Tools.Files;
using Masuit.Tools.Logging;
using 以图搜图.ViewModels;
using 以图搜图.WebAPI;

namespace 以图搜图;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "ImageSearch_SingleInstance_Mutex";

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        ShowMainWindow();
        return;
#endif
        var isAdmin = new IniFile("config.ini").GetValue("Global", "RunAsAdmin", false);
        if (isAdmin && !IsRunAsAdmin())
        {
            // 以管理员权限重新启动应用程序
            var exeName = Process.GetCurrentProcess().MainModule?.FileName;
            if (exeName != null)
            {
                var startInfo = new ProcessStartInfo(exeName)
                {
                    UseShellExecute = true,
                    Verb = "runas" // 提升权限
                };
                try
                {
                    Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    LogManager.Error(ex);
                    MessageBox.Show("需要管理员权限才能运行此应用程序。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // 本进程只负责拉起提升后的实例，随即退出。
                // 注意这里**不要创建主窗口**（见下方 ShowMainWindow 的说明）。
                Current.Shutdown();
                return;
            }
        }

#if !DEBUG
        // 单实例检查必须早于一切资源初始化（尤其 HTTP 服务）：
        // 否则第二实例会先在同一端口上把 Kestrel 跑起来、
        // 抛端口占用异常（且 Run 是被 fire-and-forget 的 Task，无人感知），
        // 之后才发现已有实例而退出。
        _mutex = new Mutex(true, MutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            // 应用已在运行，激活现有实例并退出（同样不创建主窗口）
            ActivateExistingWindow();
            Current.Shutdown();
            return;
        }
#endif

        WebApiStartup.Run(e.Args);

        base.OnStartup(e);

        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

        // 处理未捕获的异常
        DispatcherUnhandledException += (sender, args) =>
        {
            LogManager.Error(args.Exception);
            var owner = Current.MainWindow;
            if (owner != null)
            {
                MessageBox.Show(owner, args.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(args.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            args.Handled = true;
        };

        // 处理非UI线程异常
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            LogManager.Error((Exception)args.ExceptionObject);
        };

        ShowMainWindow();
    }

    /// <summary>
    /// 创建并显示主窗口。
    ///
    /// 为什么不用 App.xaml 的 <c>StartupUri</c> 自动创建：
    /// 那样的话，**即使前面已经决定退出**（提权重启、单实例激活），
    /// WPF 仍会在 OnStartup 返回后照样把主窗口建出来并显示，
    /// 于是那个注定要退出的进程会短暂闪出一个窗口，
    /// 并触发关闭流程——早期版本因此出现过
    /// 「每次启动都弹『正在加载索引…请稍后再试』且无法关闭」的故障。
    /// 改为在确认要继续运行之后才手动创建，从根上避免多创建一个无用窗口。
    /// </summary>
    private static void ShowMainWindow()
    {
        var window = new MainWindow();
        Current.MainWindow = window;
        window.Show();

        // 以管理员身份运行时，Windows 的权限隔离（UIPI）会阻止普通权限的
        // 资源管理器向提权窗口投递拖放消息，拖放会表现为"毫无反应"。
        // 这是系统级限制、程序内无法绕过，因此主动说明并给出替代输入方式，
        // 避免用户对着提示「可直接拖放图片到窗口进行搜索」反复尝试。
        if (IsRunAsAdmin() && window.DataContext is MainViewModel vm)
        {
            vm.StatusMessage = "⚠️ 正以管理员身份运行：Windows 会阻止从资源管理器拖放文件。"
                + "如需拖放，请把 config.ini 的 RunAsAdmin 改为 false；也可用「选择图片」或 Ctrl+V 粘贴。";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        WebApiStartup.Stop().Wait();
    }

    public static bool IsRunAsAdmin()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void ActivateExistingWindow()
    {
        try
        {
            // 查找现有的应用程序进程
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);

            if (processes.Length > 1)
            {
                // 找到其他实例，激活其主窗口
                var existingProcess = processes.FirstOrDefault(p => p.Id != currentProcess.Id);
                if (existingProcess != null)
                {
                    var mainWindowHandle = existingProcess.MainWindowHandle;
                    if (mainWindowHandle != IntPtr.Zero)
                    {
                        // 显示窗口
                        if (NativeMethods.IsIconic(mainWindowHandle))
                        {
                            NativeMethods.ShowWindow(mainWindowHandle, 9); // 恢复窗口
                        }

                        // 激活窗口
                        NativeMethods.SetForegroundWindow(mainWindowHandle);
                        NativeMethods.BringWindowToTop(mainWindowHandle);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(ex);
        }
    }
}

// Windows API 互操作
public static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);
}