using DotNet.Globbing;
using DotNet.Globbing.Token;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Models;

/// <summary>Outcome of <see cref="DeviceTrackingRules.ShouldTrack"/>.</summary>
/// <param name="Track">Whether the device should be tracked.</param>
/// <param name="ConfiguredName">
/// The name configured for the device, when it matched a literal <c>devices[].id</c> entry that has one.
/// The caller applies it; the rules never touch the device.
/// </param>
public readonly record struct TrackDecision(bool Track, string? ConfiguredName = null)
{
    public static readonly TrackDecision NotTracked = new(false);
}

/// <summary>
/// The <c>devices</c> / <c>exclude_devices</c> rules from one config load, with every glob compiled once.
/// Immutable: a config reload builds a new instance.
/// </summary>
public sealed class DeviceTrackingRules
{
    // ponytail: device ids/names are matched case-insensitively everywhere else (all the dictionaries are OrdinalIgnoreCase), globs must agree
    private static readonly GlobOptions GlobOpts = new() { Evaluation = { CaseInsensitive = true } };

    public static DeviceTrackingRules Empty { get; } = FromConfig(null, NullLogger.Instance);

    private readonly IReadOnlyDictionary<string, ConfigDevice> _byId;
    private readonly IReadOnlyDictionary<string, ConfigDevice> _byName;
    private readonly Glob[] _idGlobs;
    private readonly Glob[] _nameGlobs;
    private readonly (Glob? Id, Glob? Name)[] _excludes;

    private DeviceTrackingRules(
        IReadOnlyDictionary<string, ConfigDevice> byId, IReadOnlyDictionary<string, ConfigDevice> byName,
        Glob[] idGlobs, Glob[] nameGlobs, (Glob? Id, Glob? Name)[] excludes)
    {
        _byId = byId;
        _byName = byName;
        _idGlobs = idGlobs;
        _nameGlobs = nameGlobs;
        _excludes = excludes;
    }

    [ThreadStatic] private static int _globsCompiled;

    /// <summary>Globs compiled on the calling thread so far. Lets tests prove matching never compiles.</summary>
    internal static int GlobsCompiledOnThisThread => _globsCompiled;

    /// <summary>Compiles one pattern, or returns null (and logs) when DotNet.Glob can't parse it, e.g. "[a-".</summary>
    private static Glob? Compile(string pattern, ILogger logger)
    {
        _globsCompiled++;
        try
        {
            return Glob.Parse(pattern, GlobOpts);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring invalid device pattern {Pattern}", pattern);
            return null;
        }
    }

    public static DeviceTrackingRules FromConfig(Config? c, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var devices = c?.Devices ?? [];

        // Patterns without wildcards are dictionary lookups; first entry for a given id/name wins.
        var byId = new Dictionary<string, ConfigDevice>(StringComparer.OrdinalIgnoreCase);
        var idGlobs = new List<Glob>();
        foreach (var d in devices)
            if (!string.IsNullOrWhiteSpace(d.Id))
            {
                var glob = Compile(d.Id, logger);
                if (glob == null) continue;
                if (glob.Tokens.All(t => t is LiteralToken)) byId.TryAdd(d.Id, d);
                else idGlobs.Add(glob);
            }

        var byName = new Dictionary<string, ConfigDevice>(StringComparer.OrdinalIgnoreCase);
        var nameGlobs = new List<Glob>();
        foreach (var d in devices)
            if (!string.IsNullOrWhiteSpace(d.Name))
            {
                var glob = Compile(d.Name, logger);
                if (glob == null) continue;
                if (glob.Tokens.All(t => t is LiteralToken)) byName.TryAdd(d.Name, d);
                else nameGlobs.Add(glob);
            }

        var excludes = (c?.ExcludeDevices ?? [])
            .Select(d => (
                Id: string.IsNullOrWhiteSpace(d.Id) ? null : Compile(d.Id, logger),
                Name: string.IsNullOrWhiteSpace(d.Name) ? null : Compile(d.Name, logger)))
            .Where(e => e.Id != null || e.Name != null)
            .ToArray();

        return new DeviceTrackingRules(byId, byName, idGlobs.ToArray(), nameGlobs.ToArray(), excludes);
    }

    /// <summary>
    /// Whether exclude_devices matches this id or name. Takes the raw pair rather than a Device so
    /// callers can decide before constructing one -- autodiscovery needs that, or it resurrects an
    /// excluded device from its own leftover discovery message.
    /// </summary>
    public bool IsExcluded(string? id, string? name)
    {
        var hasId = !string.IsNullOrWhiteSpace(id);
        var hasName = !string.IsNullOrWhiteSpace(name);
        if (!hasId && !hasName) return false;
        foreach (var (idGlob, nameGlob) in _excludes)
        {
            if (hasId && idGlob != null && idGlob.IsMatch(id!)) return true;
            if (hasName && nameGlob != null && nameGlob.IsMatch(name!)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a device with this id and advertised name should be tracked. Exclusions win; then a literal id
    /// (which also yields its configured name), a literal name, an id glob, a name glob. No side effects.
    /// </summary>
    public TrackDecision ShouldTrack(string id, string? name)
    {
        if (IsExcluded(id, name)) return TrackDecision.NotTracked;

        if (_byId.TryGetValue(id, out var byId))
            return new TrackDecision(true, string.IsNullOrWhiteSpace(byId.Name) ? null : byId.Name);

        var hasName = !string.IsNullOrWhiteSpace(name);
        if (hasName && _byName.ContainsKey(name!)) return new TrackDecision(true);
        if (!string.IsNullOrWhiteSpace(id) && _idGlobs.Any(g => g.IsMatch(id))) return new TrackDecision(true);
        if (hasName && _nameGlobs.Any(g => g.IsMatch(name!))) return new TrackDecision(true);
        return TrackDecision.NotTracked;
    }
}
