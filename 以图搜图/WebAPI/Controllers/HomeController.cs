using Masuit.Tools.Logging;
using Masuit.Tools.Systems;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using Masuit.Tools.Files;
using 以图搜图.Models;
using 以图搜图.Services;
using 以图搜图.ViewModels;

namespace 以图搜图.WebAPI.Controllers;

[ApiController]
public class HomeController : Controller
{
    private readonly ImageIndexService _indexService = ImageIndexService.Instance;
    private readonly ImageSearchService _searchService = new ImageSearchService();
    /// <summary>主窗口 ViewModel；由界面在构造时注入，未就绪时为 null。</summary>
    public static MainViewModel? MainViewModel { get; set; }

    /// <summary>
    /// 创建或更新索引（目录会并入索引队列并按顺序执行）
    /// </summary>
    /// <param name="dir">索引目录</param>
    /// <returns></returns>
    [HttpPatch("index")]
    public async Task<ActionResult> UpdateIndex([Required] string dir)
    {
        var viewModel = MainViewModel;
        if (viewModel == null)
        {
            return StatusCode(503, "主程序尚未就绪，请稍后重试");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            return StatusCode(503, "主程序界面不可用");
        }

        // 必须切到 UI 线程执行，不能直接在请求线程上调用：
        //   1) 入队会修改 IndexQueue —— 它是绑定到列表控件的 ObservableCollection，
        //      从非 UI 线程修改会抛 NotSupportedException（请求以 500 结束）；
        //   2) 启动队列过程中会 MessageBox.Show，而弹窗要求 STA/UI 线程。
        // InvokeAsync 只等「UI 线程把这次调用启动起来」（到首个 await 即返回），
        // 因此接口能立刻应答，而队列在 UI 线程上继续跑。
        var queueTask = await dispatcher.InvokeAsync(() => viewModel.EnqueueAndRunAsync(dir));

        // 队列任务的异常不能无人认领，否则会被静默吞掉
        _ = queueTask.ContinueWith(
            task => LogManager.Error(task.Exception!),
            TaskContinuationOptions.OnlyOnFaulted);

        return Ok("已发送指令，请查看主程序窗口");
    }

    /// <summary>
    /// 搜索图像
    /// </summary>
    /// <param name="upload">需要搜索的图片</param>
    /// <param name="similar">相似度</param>
    /// <param name="algorithm">匹配算法，1：DifferenceHash，2：DctHash，4：DctHash64，7：所有</param>
    /// <param name="checkRotated">查找旋转</param>
    /// <param name="checkFlip">查找翻转</param>
    /// <returns></returns>
    [HttpPost("search")]
    public async Task<ActionResult> Search(IFormFile upload, [Range(75, 100)] float similar = 75, MatchAlgorithm algorithm = MatchAlgorithm.All, bool checkRotated = true, bool checkFlip = false)
    {
        // 参数名用 upload 而非 file：后者会遮蔽 ControllerBase.File 方法，
        // 导致本方法内调用 System.IO.File.* 时解析到 MVC 的 File() 重载而编译失败。
        var extension = Path.GetExtension(upload.FileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".jpg";
        }

        var filename = Path.Combine(Path.GetTempPath(), SnowFlake.NewId + extension);

        try
        {
            await upload.OpenReadStream().SaveFileAsync(filename);
            return Ok(await _searchService.SearchAsync(filename, _indexService.Index, algorithm, similar / 100, checkRotated, checkFlip, _indexService.IndexVersion));
        }
        finally
        {
            // 上传的临时文件必须清理：此前从不删除，每调用一次就泄漏一个文件，
            // 长期运行会不断累积（WPF 侧的剪贴板/拖拽路径都有删除，唯独 API 漏了）。
            try
            {
                if (System.IO.File.Exists(filename))
                {
                    System.IO.File.Delete(filename);
                }
            }
            catch
            {
                // 文件被占用时忽略；系统清理临时目录时会回收
            }
        }
    }
}