using System.Reflection;
using ESPresense.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Services;

public class ConfigLoader : BackgroundService
{
    private readonly IDeserializer _deserializer;
    private Task _toWait;
    private DateTime _lastModified;
    private readonly string _configPath;
    public Config? Config { get; private set; }

    public ConfigLoader(string configDir)
    {
        _configPath = Path.Combine(configDir, "config.yaml");
        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .Build();

        _toWait = Load();
    }

    private async Task Load()
    {
        try
        {
            var fi = new FileInfo(_configPath);

            if (!fi.Exists)
            {
                await using var example = Assembly.GetExecutingAssembly().GetManifestResourceStream("ESPresense.config.example.yaml") ?? throw new Exception("Could not find embedded config.example.yaml");
                await using var newConfig = File.Create(_configPath);
                await example.CopyToAsync(newConfig);
            }

            if (_lastModified == fi.LastWriteTimeUtc)
                return;

            Log.Information("Loading " + _configPath);

            var reader = await File.ReadAllTextAsync(_configPath);
            Config = FixIds(_deserializer.Deserialize<Config>(reader));
            ConfigChanged?.Invoke(this, Config);
            _lastModified = fi.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            Log.Error($"Error reading config, ignoring... {ex}");
        }
    }

    private Config FixIds(Config? c)
    {
        Config config = c ?? new Config();

        foreach (var device in config.Devices ?? Enumerable.Empty<ConfigDevice>())
            device.Id ??= device.GetId();

        foreach (var node in config.Nodes ?? Enumerable.Empty<ConfigNode>())
            node.Id ??= node.GetId();

        foreach (var floor in config.Floors ?? Enumerable.Empty<ConfigFloor>())
            floor.Id ??= floor.GetId();

        return config;
    }

    public event EventHandler<Config>? ConfigChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _toWait;
            _toWait = Load();
            await Task.Delay(1000, stoppingToken);
        }
    }

    public async Task<Config> ConfigAsync(CancellationToken ct = default)
    {
        while (true)
        {
            await _toWait;
            if (Config != null)
                return Config;
            ct.ThrowIfCancellationRequested();
        }
    }
}