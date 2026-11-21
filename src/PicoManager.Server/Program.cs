using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PicoManager.Server.Hubs;
using PicoManager.Server.Services;

// 注册控制台与进程退出生命周期监听，确保窗口关闭或停止时立即释放所有底层 C++ 原生线程与 DLL 句柄
ConsoleLifetimeHelper.Register();

string baseDir = AppDomain.CurrentDomain.BaseDirectory;
string contentRoot = Directory.GetCurrentDirectory();

// 智能解析 wwwroot 静态资源目录 (兼容从根目录运行、开发调试、或是独立部署包)
string[] candidateRoots = [
    Path.Combine(contentRoot, "wwwroot"),
    Path.Combine(baseDir, "wwwroot"),
    Path.Combine(contentRoot, "src", "PicoManager.Server", "wwwroot"),
    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "PicoManager.Server", "wwwroot")),
];

string? resolvedWebRoot = candidateRoots.FirstOrDefault(Directory.Exists);
if (!string.IsNullOrEmpty(resolvedWebRoot))
{
    Console.WriteLine($"[WebRoot] 静态资源挂载成功: {resolvedWebRoot}");
}

var options = new WebApplicationOptions
{
    Args = args,
    WebRootPath = resolvedWebRoot
};
var builder = WebApplication.CreateBuilder(options);

// 预加载配置以获取端口
int port = 8999;
try
{
    string rootConfig = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "config.json"));
    string localConfig = Path.Combine(builder.Environment.ContentRootPath, "config.json");
    string configPath = File.Exists(rootConfig) ? rootConfig : localConfig;
    if (File.Exists(configPath))
    {
        var json = File.ReadAllText(configPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("WebPort", out var p))
        {
            port = p.GetInt32();
        }
    }
}
catch { }

// 配置 Kestrel 监听 0.0.0.0 并解除上传体大小限制
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Listen(IPAddress.Any, port);
    serverOptions.Limits.MaxRequestBodySize = null; // 解除 Kestrel 默认 30MB 限制，允许超大文件与 APK 上传
});

// 解除表单上传限制（允许最大 16GB 大文件及大型 VR 游戏包）
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024L * 1024L * 1024L * 16L; // 16GB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// 优化关机超时时间（2秒迅速完成关闭，杜绝后台顽固驻留30秒）
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(2);
});

// 注册依赖注入服务
builder.Services.AddControllers();
builder.Services.AddSignalR();

builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<ScreenMonitorService>();
builder.Services.AddSingleton<PicoDeviceManager>();
builder.Services.AddSingleton<AdbService>();
builder.Services.AddSingleton<OtaService>();
builder.Services.AddHostedService<PicoSdkService>();
builder.Services.AddHostedService<UdpCommandService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p =>
    {
        p.AllowAnyHeader()
         .AllowAnyMethod()
         .SetIsOriginAllowed(_ => true)
         .AllowCredentials();
    });
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors("AllowAll");
app.UseWebSockets();
app.MapControllers();
app.MapHub<PicoHub>("/hubs/pico");
app.MapFallbackToFile("index.html");

// 打印控制台提示信息与本机内网 IP
Console.WriteLine();
Console.WriteLine("==================================================================");
Console.WriteLine("        PICO VR 门店统一设备管理控制系统 (PicoManager)");
Console.WriteLine("==================================================================");
Console.WriteLine($" 本机访问地址:   http://localhost:{port}");
Console.WriteLine($" UDP 控制监听:   端口 8998 (0.0.0.0:8998 JSON 指令接收)");
try
{
    var host = Dns.GetHostEntry(Dns.GetHostName());
    foreach (var ip in host.AddressList)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
        {
            Console.WriteLine($" 平板/局域网访问: http://{ip}:{port}");
        }
    }
}
catch { }
Console.WriteLine("==================================================================");
Console.WriteLine();

app.Run();

// 主程序运行结束后彻底清理解构
ConsoleLifetimeHelper.ForceCleanExit();

internal static class ConsoleLifetimeHelper
{
    [System.Runtime.InteropServices.DllImport("Kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    private delegate bool HandlerRoutine(int ctrlType);
    private static HandlerRoutine? _routine;
    private static int _exitInvoked = 0;

    public static void Register()
    {
        _routine = CtrlHandler;
        SetConsoleCtrlHandler(_routine, true);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            ForceCleanExit();
        };
    }

    private static bool CtrlHandler(int ctrlType)
    {
        // 0=CTRL_C, 1=CTRL_BREAK, 2=CTRL_CLOSE (用户直接点击控制台右上角 [X]), 5=LOGOFF, 6=SHUTDOWN
        ForceCleanExit();
        return true;
    }

    public static void ForceCleanExit()
    {
        if (System.Threading.Interlocked.Exchange(ref _exitInvoked, 1) != 0) return;

        try
        {
            PicoManager.Server.Native.PXREAOpenApi.Deinit();
        }
        catch { }

        // 强行终结进程及所有底层 C++ 原生线程 (PXREAClientSDK)，瞬间释放被占用的所有文件句柄与 DLL
        try
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        catch { }

        Environment.Exit(0);
    }
}
