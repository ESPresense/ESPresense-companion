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

builder.Host.UseSerilog((context, cfg) => cfg.ReadFrom.Configuration(context.Configuration));

Log.Logger.Information(MathNet.Numerics.Control.Describe().Trim('\r','\n'));

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espresense");
var storageDir = Path.Combine(configDir, ".storage");
Directory.CreateDirectory(storageDir);

var configLoader = new ConfigLoader(configDir);

builder.Services.AddSingleton(a => configLoader);
builder.Services.AddHostedService(a => configLoader);

builder.Services.AddDataProtection()
    .UseEphemeralDataProtectionProvider();

builder.Services.AddSingleton(a =>
{
    SQLitePCL.Batteries.Init();
    var databasePath = Path.Combine(storageDir, "history.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("HOME not found"));
    var sqLiteConnection = new SQLiteAsyncConnection(databasePath)
    {
        Trace = true,
        Tracer = Log.Debug
    };

    return sqLiteConnection;
});

builder.Services.AddAutoMapper(cfg => { }, typeof(MappingProfile).Assembly);

builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<DatabaseFactory>();
builder.Services.AddSingleton<IMqttNetLogger>(a => new MqttNetLogger());
builder.Services.AddSingleton<MqttCoordinator>();
builder.Services.AddSingleton<IMqttCoordinator>(provider => provider.GetRequiredService<MqttCoordinator>());
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
builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceTracker>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceSettingsStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<NodeSettingsStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<NodeTelemetryStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<TelemetryService>());
builder.Services.AddHostedService<DeviceCleanupService>();
builder.Services.AddSingleton<State>();
builder.Services.AddControllersWithViews().AddJsonOptions(opt =>
{
    opt.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    opt.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(15)
});

app.UseSerilogRequestLogging(o =>
{
    o.EnrichDiagnosticContext = (dc, ctx) => dc.Set("UserAgent", ctx?.Request.Headers.UserAgent);
    o.GetLevel = (ctx, ms, ex) => ex != null ? LogEventLevel.Error : ctx.Response.StatusCode > 499 ? LogEventLevel.Error : ms > 500 ? LogEventLevel.Warning : ctx.Request.Path.Value.IndexOf("/state", StringComparison.OrdinalIgnoreCase) > 0 ? LogEventLevel.Verbose : LogEventLevel.Debug;
});

app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.{json|yaml}");
app.UseSwaggerUI(c => c.RoutePrefix = "api/swagger");

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller}/{action=Index}/{id?}");

app.MapFallbackToFile("index.html");

app.Run();
