using System.Text.Json.Serialization;
using ESPresense.Extensions;
using ESPresense.Middleware;
using ESPresense.Models;
using ESPresense.Services;
using Flurl.Http;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions.ManagedClient;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Hosting;
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
builder.Services.AddSingleton<Task<IManagedMqttClient>>(async a =>
{
    var cfg = a.GetRequiredService<ConfigLoader>();
    var c = await cfg.ConfigAsync();

    c.Mqtt ??= new ConfigMqtt();

    var supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
    if (string.IsNullOrEmpty(c.Mqtt.Host) && !string.IsNullOrEmpty(supervisorToken))
    {
        try
        {
            try
            {
                var (_, _, data) = await "http://supervisor/services/mqtt"
                    .WithOAuthBearerToken(supervisorToken)
                    .GetJsonAsync<HassIoResult>();

                c.Mqtt.Host = string.IsNullOrEmpty(data.Host) ? "localhost" : data.Host;
                c.Mqtt.Port = int.TryParse(data.Port, out var i) ? i : null;
                c.Mqtt.Username = data.Username;
                c.Mqtt.Password = data.Password;
                c.Mqtt.Ssl = data.Ssl;
            }
            catch (FlurlHttpException ex) {
                var error = await ex.GetResponseJsonAsync<HassIoResult>();
                Log.Warning($"Failed to get MQTT config from Hass.io: {error.Message}");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to get MQTT config from Hass.io");
        }
    }

    var mqttFactory = new MqttFactory(a.GetRequiredService<IMqttNetLogger>());

    var mc = mqttFactory.CreateManagedMqttClient();
    var mqttClientOptions = new MqttClientOptionsBuilder()
        .WithConfig(c.Mqtt)
        .WithWillTopic("espresense/companion/status")
        .WithWillRetain()
        .WithWillPayload("offline")
        .Build();

    var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
        .WithClientOptions(mqttClientOptions)
        .Build();

    await mc.StartAsync(managedMqttClientOptions);
    return mc;
});
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