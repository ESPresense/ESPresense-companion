using ESPresense.Extensions;
using AutoMapper;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet.Diagnostics.Logger;
using Serilog;
using Serilog.Events;
using SQLite;
using System.Text.Json.Serialization;
using ESPresense.Optimizers;
using ESPresense.Controllers;
using ESPresense.Utils;
using Microsoft.AspNetCore.DataProtection;
using Flurl.Http.Newtonsoft;
using Flurl.Http;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

FlurlHttp.Clients.UseNewtonsoft();

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

Log.Logger.Information(MathNet.Numerics.Control.Describe().Trim('\r', '\n'));

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR")
                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espresense");
var storageDir = Path.Combine(configDir, ".storage");
Directory.CreateDirectory(storageDir);

var configLoader = new ConfigLoader(configDir);
builder.Services.AddSingleton(_ => configLoader);
builder.Services.AddHostedService(_ => configLoader);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(storageDir, "keys")));

builder.Services.AddSingleton(_ =>
{
    SQLitePCL.Batteries.Init();
    var dbPath = Path.Combine(storageDir, "history.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    return new SQLiteAsyncConnection(dbPath)
    {
        Trace = true,
        Tracer = Log.Debug
    };
});

builder.Services.AddAutoMapper(cfg => { }, typeof(MappingProfile).Assembly);

builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<DatabaseFactory>();
builder.Services.AddSingleton<IMqttNetLogger>(_ => new MqttNetLogger());
builder.Services.AddSingleton<MqttCoordinator>();
builder.Services.AddSingleton<IMqttCoordinator>(p => p.GetRequiredService<MqttCoordinator>());
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<GlobalEventDispatcher>();
builder.Services.AddSingleton<DeviceTracker>();
builder.Services.AddSingleton<SupervisorConfigLoader>();
builder.Services.AddSingleton<DeviceHistoryStore>();
builder.Services.AddSingleton<DeviceSettingsStore>();
builder.Services.AddSingleton<NodeSettingsStore>();
builder.Services.AddSingleton<NodeTelemetryStore>();
builder.Services.AddSingleton<FirmwareTypeStore>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<LeaseService>();
builder.Services.AddSingleton<ILeaseService>(provider => provider.GetRequiredService<LeaseService>());

builder.Services.AddHostedService<MultiScenarioLocator>();
builder.Services.AddHostedService<OptimizationRunner>();
builder.Services.AddHostedService(p => p.GetRequiredService<DeviceTracker>());
builder.Services.AddHostedService(p => p.GetRequiredService<DeviceSettingsStore>());
builder.Services.AddHostedService(p => p.GetRequiredService<NodeSettingsStore>());
builder.Services.AddHostedService(p => p.GetRequiredService<NodeTelemetryStore>());
builder.Services.AddHostedService(p => p.GetRequiredService<TelemetryService>());
builder.Services.AddHostedService<DeviceCleanupService>();
builder.Services.AddSingleton<State>();
builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromMinutes(15) });

app.UseSerilogRequestLogging(o =>
{
    o.EnrichDiagnosticContext = (dc, ctx) => dc.Set("UserAgent", ctx.Request.Headers.UserAgent);
    o.GetLevel = (ctx, elapsedMs, ex) =>
        ex != null ? LogEventLevel.Error :
        ctx.Response.StatusCode > 499 ? LogEventLevel.Error :
        elapsedMs > 500 ? LogEventLevel.Warning :
        ctx.Request.Path.Value?.Contains("/state", StringComparison.OrdinalIgnoreCase) == true
            ? LogEventLevel.Verbose
            : LogEventLevel.Debug;
});

app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.{json|yaml}");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/swagger/v1/swagger.json", "v1");
    c.RoutePrefix = "api/swagger";
});

app.MapStaticAssets();
app.MapControllers();

app.MapFallbackToFile("index.html");

app.Run();
