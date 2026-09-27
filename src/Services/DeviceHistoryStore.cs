using ESPresense.Services;
using Serilog;
using SQLite;

namespace ESPresense.Models;

public class DeviceHistoryStore
{
    private readonly SQLiteAsyncConnection _sqliteConnection;
    private volatile Task _initialized = Task.CompletedTask;
    private volatile bool _enabled;

    public DeviceHistoryStore(SQLiteAsyncConnection sqliteConnection, ConfigLoader cfg)
    {
        _sqliteConnection = sqliteConnection;
        cfg.ConfigChanged += (_, c) => _initialized = ApplyConfigAsync(c);
        // The initial config load usually completes before this singleton is constructed,
        // so ConfigChanged has already fired; apply the current config explicitly.
        if (cfg.Config != null) _initialized = ApplyConfigAsync(cfg.Config);
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
            await _sqliteConnection.CreateTableAsync<DeviceHistory>();
            await _sqliteConnection.CreateIndexAsync("IX_DeviceHistory_When", "DeviceHistory", "When");
            await _sqliteConnection.CreateIndexAsync("IX_DeviceHistory_Id_When", "DeviceHistory", new[] { "Id", "When" });
            await _sqliteConnection.ExecuteAsync("DROP TRIGGER IF EXISTS DeviceHistory_RollingData;");
            await _sqliteConnection.ExecuteAsync(@$"CREATE TRIGGER DeviceHistory_RollingData AFTER INSERT ON DeviceHistory
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
        if (!await Ready()) return -1;
        return await _sqliteConnection.InsertAsync(dh);
    }

    public async Task<IList<DeviceHistory>?> List(string id)
    {
        if (!await Ready()) return null;
        return await _sqliteConnection.Table<DeviceHistory>().Where(x => x.Id == id).OrderBy(x => x.When).ToListAsync();
    }

    public async Task<IList<DeviceHistory>?> List(string id, DateTime start, DateTime end)
    {
        if (!await Ready()) return null;
        // Query for records within the specified time range and order by time
        return await _sqliteConnection.Table<DeviceHistory>().Where(x => x.Id == id && x.When >= start && x.When <= end).OrderBy(x => x.When).ToListAsync();
    }
}
