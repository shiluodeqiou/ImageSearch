using Masuit.Tools.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.IO;
using System.Reflection;

namespace 以图搜图.WebAPI;

public static class WebApiStartup
{
    private static WebApplication? _application;
    public static bool ServerRunning { get; set; }

    /// <summary>实际监听地址（供界面显示；未启动时为 null）。</summary>
    public static string? ListenUrl { get; private set; }

    /// <summary>接口令牌；为空表示不校验（仅允许本机监听）。</summary>
    private static string? _apiToken;

    // 文档相关路径不参与令牌校验，否则 Scalar 页面无法读取 openapi 描述
    private static readonly string[] DocPathPrefixes = ["/api", "/openapi"];

    /// <summary>
    /// 决定实际监听地址。安全底线：通配地址（局域网可达）必须同时配置令牌，
    /// 否则 /search、/index 这类能触发磁盘枚举与全库计算的接口会被同网段任意设备
    /// 无凭据调用——没配令牌就强制退回仅本机监听。
    /// </summary>
    /// <returns>(实际监听主机名, 是否仅回环)。</returns>
    public static (string Host, bool LoopbackOnly) ResolveListenHost(string? configuredHost, string? apiToken)
    {
        var hasToken = !string.IsNullOrWhiteSpace(apiToken);

        // 空值或通配形式：有令牌才允许全网卡监听，否则回退回环
        if (string.IsNullOrEmpty(configuredHost) || configuredHost is "0.0.0.0" or "*" or "+")
        {
            return hasToken ? ("0.0.0.0", false) : ("127.0.0.1", true);
        }

        var loopback = configuredHost is "127.0.0.1" or "localhost" or "::1";
        if (!loopback && !hasToken)
        {
            // 显式配了具体的局域网地址却没配令牌：同样退回回环
            return ("127.0.0.1", true);
        }

        return (configuredHost, loopback);
    }

    public static Task Run(params string[] args)
    {
        var config = new IniFile("config.ini");
        var runServer = config.GetValue("Global", "RunServer", false);
        if (!runServer)
        {
            return Task.CompletedTask;
        }

        var port = config.GetValue("Global", "HttpPort", 5000);
        var configuredHost = config.GetValue("Global", "HttpHost", "127.0.0.1")?.Trim();
        _apiToken = config.GetValue("Global", "ApiToken", string.Empty)?.Trim();

        var (host, loopbackOnly) = ResolveListenHost(configuredHost, _apiToken);

        ServerRunning = true;
        var builder = WebApplication.CreateBuilder(args);

        // 上传搜索图的体积上限：再大的文件作为相似度查询输入也没有意义，
        // 同时防止超大请求体把内存吃满
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 32L * 1024 * 1024;
        });

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "以图搜图 - 本地图像检索工具WPF版 by 懒得勤快 (评估版本)",
                Version = "v1"
            });
            // 设置 XML 注释文件路径
            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
            c.IncludeXmlComments(xmlPath);
        });

        // CORS 只向本机来源开放：本工具的网页拖拽搜索场景来自本机浏览器/页面，
        // 没有任何正当的跨机浏览器调用需求。令牌本身放在请求头里时，
        // 浏览器跨域请求还会被预检拦截，不能靠 AllowAnyOrigin 放开。
        builder.Services.AddCors(options => options.AddDefaultPolicy(p =>
            p.WithOrigins("http://127.0.0.1:" + port, "http://localhost:" + port,
                          "https://127.0.0.1:" + port, "https://localhost:" + port)
             .AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        _application = app;
        app.UseCors();

        // 接口文档只在本机监听时挂出：对外暴露时不公开完整接口面
        if (loopbackOnly)
        {
            app.UseSwagger(options => options.RouteTemplate = "/openapi/{documentName}.json");
            app.MapScalarApiReference("/api");
        }

        // 令牌校验：配了 ApiToken 后，除文档路径外的所有请求都必须携带。
        // 同时接受请求头 X-Api-Key 与查询参数 api_key（后者方便浏览器地址栏/简单脚本）。
        if (!string.IsNullOrEmpty(_apiToken))
        {
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                var isDocPath = DocPathPrefixes.Any(prefix =>
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (!isDocPath)
                {
                    var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                                   ?? context.Request.Query["api_key"].FirstOrDefault();
                    if (!string.Equals(provided, _apiToken, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new { error = "未授权：X-Api-Key 无效或缺失" });
                        return;
                    }
                }

                await next();
            });
        }

        app.MapControllers();

        ListenUrl = $"http://{(host == "0.0.0.0" ? "0.0.0.0" : host)}:{port}";
        return app.RunAsync(ListenUrl);
    }

    /// <summary>停止 HTTP 服务。</summary>
    public static async Task Stop()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync();
        }
    }
}
