using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet.Diagnostics.Logger;
using Serilog;
using Serilog.Events;
using System.Text.Json.Serialization;
using ESPresense.Optimizers;
using ESPresense.Controllers;
using ESPresense.Utils;
using Microsoft.AspNetCore.DataProtection;
using Flurl.Http.Newtonsoft;
using Flurl.Http;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

FlurlHttp.Clients.UseNewtonsoft();

builder.Host.UseSerilog((context, cfg) => cfg.ReadFrom.Configuration(context.Configuration));

Log.Logger.Information(MathNet.Numerics.Control.Describe().Trim('\r', '\n'));

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espresense");
var storageDir = Path.Combine(configDir, ".storage");
Directory.CreateDirectory(storageDir);

var configLoader = new ConfigLoader(configDir);

builder.Services.AddSingleton(a => configLoader);
builder.Services.AddHostedService(a => configLoader);

builder.Services.AddDataProtection()
    .UseEphemeralDataProtectionProvider();

builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IMqttNetLogger>(a => new MqttNetLogger());
builder.Services.AddSingleton<MqttCoordinator>();
builder.Services.AddSingleton<IMqttCoordinator>(provider => provider.GetRequiredService<MqttCoordinator>());
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<GlobalEventDispatcher>();
builder.Services.AddSingleton<DeviceTracker>();
builder.Services.AddSingleton<SupervisorConfigLoader>();
builder.Services.AddSingleton(a => new DeviceHistoryStore(storageDir, a.GetRequiredService<ConfigLoader>()));
builder.Services.AddSingleton<DeviceSettingsStore>();
builder.Services.AddSingleton<NodeSettingsStore>();
builder.Services.AddSingleton<NodeTelemetryStore>();
builder.Services.AddSingleton<FirmwareTypeStore>();
builder.Services.AddSingleton<IFirmwareTypeStore>(provider => provider.GetRequiredService<FirmwareTypeStore>());
builder.Services.AddSingleton<NodeStateMapper>();
builder.Services.AddSingleton<FirmwareUpdateJobService>();
builder.Services.AddSingleton<FirmwareCatalogService>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<DeviceCaptureService>();
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
builder.Services.AddSingleton<PositionAccuracyService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PositionAccuracyService>());
builder.Services.AddSingleton<State>();
builder.Services.AddControllersWithViews().AddJsonOptions(opt =>
{
    opt.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    opt.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "ESPresense.Companion", Version = "1.0.0" };
})
    .WithResources<McpResources>()
    .WithTools<McpResources>()
    .WithHttpTransport();

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

// Without Cache-Control, browsers (and the HA ingress iframe) heuristically cached index.html off Last-Modified
// and kept running the previous build after an add-on update. Hashed assets never change. Everything else is
// good for a minute, then revalidates (ETag makes that a cheap 304). Not "public": ingress responses are per-user.
var spaFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/_app/immutable")
            ? "public, max-age=31536000, immutable"
            : "max-age=60, must-revalidate"
};
app.UseStaticFiles(spaFiles);
app.UseRouting();
app.MapMcp("/api/mcp");
app.MapHealthChecks("/healthz");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller}/{action=Index}/{id?}");

app.MapFallbackToFile("index.html", spaFiles);

app.Run();
