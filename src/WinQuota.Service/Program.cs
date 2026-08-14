using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using Serilog;
using WinQuota.Core.Data;
using WinQuota.Service;
using WinQuota.Service.Api;
using WinQuota.Service.Cli;
using WinQuota.Service.Services;
using WinQuota.Service.Workers;

if (CommandLine.IsCliInvocation(args))
{
    return CommandLine.Run(args);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // 服务方式运行时工作目录是 system32，内容根必须固定为程序所在目录。
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = "WinQuota");

builder.Services.Configure<WinQuotaOptions>(builder.Configuration.GetSection(WinQuotaOptions.SectionName));

var quotaOptions = builder.Configuration
    .GetSection(WinQuotaOptions.SectionName)
    .Get<WinQuotaOptions>() ?? new WinQuotaOptions();
builder.WebHost.UseUrls($"http://127.0.0.1:{quotaOptions.ApiPort}");

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<WinQuotaOptions>>().Value;
    return new QuotaDatabase(options.ResolveDatabasePath());
});

builder.Services.AddSingleton<IProcessScanner, ToolhelpProcessScanner>();
builder.Services.AddSingleton<IProcessTerminator, ProcessTerminator>();
builder.Services.AddSingleton<INotifier, UserSessionNotifier>();
builder.Services.AddSingleton<IComputerUsageMonitor, WtsComputerUsageMonitor>();
builder.Services.AddSingleton<IWorkstationLocker, UserSessionWorkstationLocker>();
builder.Services.AddSingleton<IJobObjectManager, JobObjectManager>();
builder.Services.AddSingleton<LiveStatus>();
builder.Services.AddHostedService<QuotaWorker>();

var runAsService = WindowsServiceHelpers.IsWindowsService();
var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "WinQuota", "logs");

builder.Logging.ClearProviders();
builder.Services.AddSerilog((services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.File(
            Path.Combine(logDirectory, "winquota-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

    if (!runAsService)
    {
        loggerConfiguration.WriteTo.Console();
    }
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapWinQuotaApi();
// SPA 路由回退：非 /api 路径统一回 index.html。
app.MapFallbackToFile("index.html");

app.Run();

return 0;
