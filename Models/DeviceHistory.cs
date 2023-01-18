using SQLite;

namespace ESPresense.Models;

public class DatabaseFactory
{
    private readonly SQLiteAsyncConnection _sqliteConnection;

    public DatabaseFactory(SQLiteAsyncConnection sqliteConnection)
    {
        _sqliteConnection = sqliteConnection;
    }


    public async Task<DeviceHistoryStore> GetDeviceHistory()
    {
        await _sqliteConnection.CreateTableAsync<DeviceHistory>();
        await _sqliteConnection.CreateIndexAsync("IX_DeviceHistory_When", "DeviceHistory", "When");
        await _sqliteConnection.ExecuteAsync("DROP TRIGGER IF EXISTS DeviceHistory_RollingData;");
        await _sqliteConnection.ExecuteAsync(@$"CREATE TRIGGER DeviceHistory_RollingData AFTER INSERT ON DeviceHistory
   BEGIN
     DELETE FROM DeviceHistory WHERE `When` <= (NEW.`When`-{TimeSpan.FromHours(36).Ticks});
   END;");
        return new DeviceHistoryStore(_sqliteConnection);
    }
}

public class DeviceHistoryStore
{
    private readonly SQLiteAsyncConnection _sqliteConnection;

    public DeviceHistoryStore(SQLiteAsyncConnection sqliteConnection)
    {
        _sqliteConnection = sqliteConnection;
    }

    public async Task<int> Add(DeviceHistory dh)
    {
        return await _sqliteConnection.InsertAsync(dh);
    }

    public async Task<DeviceHistory[]> List(string id)
    {
        return await _sqliteConnection.Table<DeviceHistory>().Where(x => x.Id == id).ToArrayAsync();
    }
}

public class DeviceHistory
{
    public string? Id { get; set; }
    public string? Scenario { get; set; }
    public int Confidence { get; set; }
    public int Fixes { get; set; }
    public DateTime? When { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public bool Best { get; set; }
}