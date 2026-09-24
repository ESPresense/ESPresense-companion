using ESPresense.Services;
using Serilog;
using SQLite;

namespace ESPresense.Models;

public class DeviceHistoryStore
{
    private readonly SQLiteAsyncConnection _sqliteConnection;
    private volatile bool _initialized;

    public DeviceHistoryStore(SQLiteAsyncConnection sqliteConnection, ConfigLoader cfg)
    {
        _sqliteConnection = sqliteConnection;
        cfg.ConfigChanged += (_, c) => _ = ApplyConfigAsync(c);
        // The initial config load usually completes before this singleton is constructed,
        // so ConfigChanged has already fired; apply the current config explicitly.
        if (cfg.Config != null) _ = ApplyConfigAsync(cfg.Config);
    }

    private async Task ApplyConfigAsync(Config c)
    {
        try
        {
            if (!c.History.Enabled) return;
            await _sqliteConnection.CreateTableAsync<DeviceHistory>();
            await _sqliteConnection.CreateIndexAsync("IX_DeviceHistory_When", "DeviceHistory", "When");
            await _sqliteConnection.CreateIndexAsync("IX_DeviceHistory_Id_When", "DeviceHistory", new[] { "Id", "When" });
            await _sqliteConnection.ExecuteAsync("DROP TRIGGER IF EXISTS DeviceHistory_RollingData;");
            await _sqliteConnection.ExecuteAsync(@$"CREATE TRIGGER DeviceHistory_RollingData AFTER INSERT ON DeviceHistory
   BEGIN
     DELETE FROM DeviceHistory WHERE `When` <= (NEW.`When`-{c.History.ExpireAfterTimeSpan.Ticks});
   END;");
            _initialized = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize device history storage");
        }
    }

    public async Task<int> Add(DeviceHistory dh)
    {
        if (!_initialized) return -1;
        return await _sqliteConnection.InsertAsync(dh);
    }

    public async Task<IList<DeviceHistory>?> List(string id)
    {
        if (!_initialized) return null;
        return await _sqliteConnection.Table<DeviceHistory>().Where(x => x.Id == id).OrderBy(x => x.When).ToListAsync();
    }

    public async Task<IList<DeviceHistory>?> List(string id, DateTime start, DateTime end)
    {
        if (!_initialized) return null;
        // Query for records within the specified time range and order by time
        return await _sqliteConnection.Table<DeviceHistory>().Where(x => x.Id == id && x.When >= start && x.When <= end).OrderBy(x => x.When).ToListAsync();
    }
}
