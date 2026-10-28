using ESPresense.Services;
using Serilog;
using SQLite;

namespace ESPresense.Models;

public class DeviceHistoryStore
{
    private readonly string? _storageDir;
    private readonly object _openLock = new();
    private SQLiteAsyncConnection? _sqliteConnection;
    private string? _openedPath;
    private volatile Task _initialized = Task.CompletedTask;
    private volatile bool _enabled;

    /// <summary>Opens <c>history.path</c> (relative to <paramref name="storageDir"/>) the first time history is enabled.</summary>
    public DeviceHistoryStore(string storageDir, ConfigLoader cfg)
    {
        _storageDir = storageDir;
        Subscribe(cfg);
    }

    public DeviceHistoryStore(SQLiteAsyncConnection sqliteConnection, ConfigLoader cfg)
    {
        _sqliteConnection = sqliteConnection;
        Subscribe(cfg);
    }

    private void Subscribe(ConfigLoader cfg)
    {
        cfg.ConfigChanged += (_, c) => _initialized = ApplyConfigAsync(c);
        // The initial config load usually completes before this singleton is constructed,
        // so ConfigChanged has already fired; apply the current config explicitly.
        if (cfg.Config != null) _initialized = ApplyConfigAsync(cfg.Config);
    }

    // The connection stays open for the process lifetime, so a later history.path change needs a restart.
    private SQLiteAsyncConnection Connection(Config c)
    {
        lock (_openLock)
        {
            if (_storageDir == null) return _sqliteConnection!;
            var path = Path.GetFullPath(Path.Combine(_storageDir, string.IsNullOrWhiteSpace(c.History.Path) ? "history.db" : c.History.Path));
            if (_sqliteConnection == null)
            {
                SQLitePCL.Batteries.Init();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Log.Information("Device history stored in {Path}", path);
                _sqliteConnection = new SQLiteAsyncConnection(path) { Trace = true, Tracer = Log.Debug };
                _openedPath = path;
            }
            else if (path != _openedPath)
                Log.Warning("history.path is now {Path}, but history keeps using {OpenedPath} until restart", path, _openedPath);
            return _sqliteConnection;
        }
    }

    // Awaited by every caller so requests wait for table creation, and a failed
    // init surfaces as an error instead of an empty result.
    private async Task<bool> Ready()
    {
        await _initialized;
        return _enabled;
    }

    private async Task ApplyConfigAsync(Config c)
    {
        try
        {
            _enabled = c.History.Enabled;
            if (!_enabled) return;
            var db = Connection(c);
            await db.CreateTableAsync<DeviceHistory>();
            await db.CreateIndexAsync("IX_DeviceHistory_When", "DeviceHistory", "When");
            await db.CreateIndexAsync("IX_DeviceHistory_Id_When", "DeviceHistory", new[] { "Id", "When" });
            await db.ExecuteAsync("DROP TRIGGER IF EXISTS DeviceHistory_RollingData;");
            await db.ExecuteAsync(@$"CREATE TRIGGER DeviceHistory_RollingData AFTER INSERT ON DeviceHistory
   BEGIN
     DELETE FROM DeviceHistory WHERE `When` <= (NEW.`When`-{c.History.ExpireAfterTimeSpan.Ticks});
   END;");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize device history storage");
            throw;
        }
    }

    public async Task<int> Add(DeviceHistory dh)
    {
        // Callers are the locator loop, whose catch-all would drop its lease and restart
        // on every write if init failed. The failure is already logged; reads surface it.
        try
        {
            if (!await Ready()) return -1;
        }
        catch (Exception)
        {
            return -1;
        }
        return await _sqliteConnection!.InsertAsync(dh);
    }

    public async Task<IList<DeviceHistory>?> List(string id)
    {
        if (!await Ready()) return null;
        return await _sqliteConnection!.Table<DeviceHistory>().Where(x => x.Id == id).OrderBy(x => x.When).ToListAsync();
    }

    public async Task<IList<DeviceHistory>?> List(string id, DateTime start, DateTime end)
    {
        if (!await Ready()) return null;
        // Query for records within the specified time range and order by time
        return await _sqliteConnection!.Table<DeviceHistory>().Where(x => x.Id == id && x.When >= start && x.When <= end).OrderBy(x => x.When).ToListAsync();
    }
}
