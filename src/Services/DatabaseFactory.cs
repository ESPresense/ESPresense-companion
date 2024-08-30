using SQLite;
using ESPresense.Models;

namespace ESPresense.Services;

public class DatabaseFactory
{
    private readonly SQLiteAsyncConnection _sqliteConnection;
    private readonly Config _config;

    public DatabaseFactory(SQLiteAsyncConnection sqliteConnection, Config config)
    {
        _sqliteConnection = sqliteConnection;
        _config = config;
    }

    public async Task<DeviceHistoryStore> GetDeviceHistory()
    {
        return new DeviceHistoryStore(_sqliteConnection, _config);
    }
}
