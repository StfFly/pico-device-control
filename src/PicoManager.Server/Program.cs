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

// 配置 Kestrel 监听 0.0.0.0
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Listen(IPAddress.Any, port);
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
