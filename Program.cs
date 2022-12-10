using ESPresense.Middleware;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet.Diagnostics;
using Serilog;
using Serilog.Events;
using SQLite;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

builder.Host.UseSerilog((context, cfg) => cfg.ReadFrom.Configuration(context.Configuration));

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espresense");
var storageDir = Path.Combine(configDir, ".storage");
Directory.CreateDirectory(storageDir);

var configLoader = new ConfigLoader(configDir);

builder.Services.AddSingleton(a => configLoader);
builder.Services.AddHostedService(a => configLoader);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton(a =>
{
    var databasePath = Path.Combine(storageDir, "config.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("HOME not found"));
    return new SQLiteConnection(databasePath);
});
builder.Services.AddSingleton<IMqttNetLogger>(a => new MqttNetLogger());
builder.Services.AddSingleton(MqttConnection.GetClient);
builder.Services.AddHostedService<Multilateralizer>();
builder.Services.AddSingleton<State>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(15)
});

app.UseSerilogRequestLogging(o =>
{
    o.EnrichDiagnosticContext = (dc, ctx) => dc.Set("UserAgent", ctx?.Request.Headers["User-Agent"]);
    o.GetLevel = (ctx, ms, ex) => ex != null ? LogEventLevel.Error : ctx.Response.StatusCode > 499 ? LogEventLevel.Error : ms > 500 ? LogEventLevel.Warning : LogEventLevel.Debug;
});

app.UseMiddleware<FixAbsolutePaths>();
app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller}/{action=Index}/{id?}");

app.MapFallbackToFile("index.html");

var db = app.Services.GetRequiredService<SQLiteConnection>();
db.CreateTable<Node>();

app.Run();