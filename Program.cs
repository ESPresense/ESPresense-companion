using System.Reflection;
using ESPresense;
using ESPresense.Models;
using Serilog;
using Serilog.Events;
using SQLite;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, cfg) => cfg.ReadFrom.Configuration(context.Configuration));

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espresense");
var configPath = Path.Combine(configDir, "config.yaml");
if (!File.Exists(configPath))
{
    // Create file from embedded file
    using var example = Assembly.GetExecutingAssembly().GetManifestResourceStream("ESPresense.config.example.yaml") ?? throw new Exception("Could not find embedded config.example.yaml");
    using var newConfig = File.Create(configPath);
    example.CopyToAsync(newConfig);
}

var deserializer = new DeserializerBuilder()
    .IgnoreUnmatchedProperties()
    .WithNamingConvention(HyphenatedNamingConvention.Instance)
    .Build();
var config = deserializer.Deserialize<Config>(File.ReadAllText(configPath));
builder.Services.AddSingleton(config);

var storageDir = Path.Combine(configDir, ".storage");
Directory.CreateDirectory(storageDir);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton(a=>{
    var databasePath = Path.Combine(storageDir, "config.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("HOME not found"));
    return new SQLiteConnection(databasePath);
});
builder.Services.AddHostedService<Multilateralizer>();
builder.Services.AddSingleton<State>();

var app = builder.Build();

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