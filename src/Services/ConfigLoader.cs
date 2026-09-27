using System.Reflection;
using System.Text.RegularExpressions;
using ESPresense.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Services;

public class ConfigLoader : BackgroundService
{
    private readonly IDeserializer _deserializer;
    private readonly string _configPath;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly TaskCompletionSource<Config> _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _lastModified;

    /// <summary>
    /// The most recently parsed configuration, or null until the first successful load.
    /// Use <see cref="ConfigAsync"/> to wait for it, or subscribe to <see cref="ConfigChanged"/>.
    /// </summary>
    public Config? Config { get; private set; }

    /// <summary>
    /// Raised after every successful parse of config.yaml (including the first). Every handler is
    /// invoked in its own try/catch, so one throwing subscriber cannot starve the others or cause a reload.
    /// </summary>
    public event EventHandler<Config>? ConfigChanged;

    /// <summary>
    /// Field initialisation only: the constructor does no I/O and raises no events, so services that
    /// resolve a ConfigLoader from DI can subscribe to <see cref="ConfigChanged"/> before the first load.
    /// The initial load happens in <see cref="StartAsync"/> (or <see cref="LoadAsync"/> when called directly).
    /// </summary>
    public ConfigLoader(string configDir)
    {
        _configPath = Path.Combine(configDir, "config.yaml");
        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
    }

    /// <summary>
    /// Loads config.yaml synchronously (with respect to the host) before the polling loop and any later
    /// hosted service starts, so <see cref="Config"/> is populated by the time the rest of the app runs.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync();
        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Reads config.yaml if its mtime differs from the last attempt, creating it from the embedded example
    /// when missing. A successful parse updates <see cref="Config"/>, completes <see cref="ConfigAsync"/> and
    /// raises <see cref="ConfigChanged"/>. A parse failure is logged once and the file is left alone until its
    /// mtime changes. Safe to call concurrently; loads are serialised.
    /// </summary>
    public async Task LoadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            await LoadCore();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task LoadCore()
    {
        Config config;
        try
        {
            var fi = new FileInfo(_configPath);

            if (!fi.Exists)
            {
                await using var example = Assembly.GetExecutingAssembly().GetManifestResourceStream("ESPresense.config.example.yaml") ?? throw new Exception("Could not find embedded config.example.yaml");
                await using (var newConfig = File.Create(_configPath))
                {
                    await example.CopyToAsync(newConfig);
                }
                fi.Refresh(); // FileInfo caches Exists/LastWriteTimeUtc; pick up the file we just wrote
            }

            var lastModified = fi.LastWriteTimeUtc;
            if (_lastModified == lastModified)
                return;

            // Advance before parsing and before notifying subscribers: a broken file is logged once and
            // not re-parsed every poll, and a throwing subscriber does not trigger a reload.
            _lastModified = lastModified;

            Log.Information("Loading " + _configPath);

            var text = await File.ReadAllTextAsync(_configPath);
            config = FixIds(_deserializer.Deserialize<Config>(text));
            // Assign/normalize room colors with adjacency-aware algorithm
            Utils.ColorAssigner.AssignRoomColors(config);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error reading {ConfigPath}; keeping the previous config until the file changes", _configPath);
            return;
        }

        Config = config;
        _firstLoad.TrySetResult(config);
        RaiseConfigChanged(config);
    }

    private void RaiseConfigChanged(Config config)
    {
        var handlers = ConfigChanged;
        if (handlers == null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<Config>)handler)(this, config);
            }
            catch (Exception ex)
            {
                var target = handler.Target?.GetType().FullName ?? handler.Method.DeclaringType?.FullName ?? "<static>";
                Log.Error(ex, "ConfigChanged handler {Handler}.{Method} threw; continuing with remaining handlers", target, handler.Method.Name);
            }
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

        foreach (var room in config.Floors?.SelectMany(a => a.Rooms ?? Enumerable.Empty<ConfigRoom>()) ?? Enumerable.Empty<ConfigRoom>())
        {
            room.Id ??= room.GetId();
        }

        // Colors now handled in AssignRoomColors()

        return config;
    }

    private static readonly HashSet<string> ProtectedSections = new(StringComparer.OrdinalIgnoreCase) { "map" };

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public async Task SaveSectionAsync(string sectionName, object value)
    {
        if (ProtectedSections.Contains(sectionName))
            throw new InvalidOperationException($"Section '{sectionName}' cannot be saved via API");

        var sectionYaml = _serializer.Serialize(
            new Dictionary<string, object> { { sectionName, value } }
        ).TrimEnd('\r', '\n');

        var text = await File.ReadAllTextAsync(_configPath);

        // Match from ^sectionName: through all indented/blank lines until next top-level key or EOF
        var pattern = $@"^{Regex.Escape(sectionName)}:.*(\n([ \t]+.*)|\n\s*)*";
        var match = Regex.Match(text, pattern, RegexOptions.Multiline);

        string replaced;
        if (match.Success)
            replaced = text[..match.Index] + sectionYaml + text[(match.Index + match.Length)..];
        else
            replaced = text.TrimEnd() + "\n\n" + sectionYaml + "\n";

        await File.WriteAllTextAsync(_configPath, replaced);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            await LoadAsync();
        }
    }

    /// <summary>
    /// Completes with the first successfully parsed configuration. Does not poll or spin: while the file is
    /// missing or invalid the returned task stays pending until <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task<Config> ConfigAsync(CancellationToken ct = default) => _firstLoad.Task.WaitAsync(ct);
}
