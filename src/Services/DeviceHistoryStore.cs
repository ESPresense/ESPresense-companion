using SQLite;

namespace ESPresense.Models;

public class DeviceHistoryStore
{
    private readonly SQLiteAsyncConnection _sqliteConnection;

    private readonly Lazy<Task> _create;

    public DeviceHistoryStore(SQLiteAsyncConnection sqliteConnection)
    {
        _sqliteConnection = sqliteConnection;
        _create = new Lazy<Task>(async () =>
        {
            await _sqliteConnection.CreateTableAsync<DeviceHistory>();
            await _sqliteConnection.CreateIndexAsync("IX_DeviceHistory_When", "DeviceHistory", "When");
            await _sqliteConnection.ExecuteAsync("DROP TRIGGER IF EXISTS DeviceHistory_RollingData;");
            await _sqliteConnection.ExecuteAsync(@$"CREATE TRIGGER DeviceHistory_RollingData AFTER INSERT ON DeviceHistory
   BEGIN
     DELETE FROM DeviceHistory WHERE `When` <= (NEW.`When`-{TimeSpan.FromHours(36).Ticks});
   END;");
        });
    }

    public async Task<int> Add(DeviceHistory dh)
    {
        await _create.Value;
        return await _sqliteConnection.InsertAsync(dh);
    }

    public async Task<IList<DeviceHistory>> List(string id)
    {
        await _create.Value;
        return await _sqliteConnection.Table<DeviceHistory>().Where(x => x.Id == id).ToListAsync();
    }
}
