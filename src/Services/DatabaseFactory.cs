using SQLite;
using ESPresense.Models;

namespace ESPresense.Services;

public class DatabaseFactory(SQLiteAsyncConnection sqliteConnection, ConfigLoader cfg)
{
    public Task<DeviceHistoryStore> GetDeviceHistory()
    {
        return Task.FromResult(new DeviceHistoryStore(sqliteConnection, cfg));
    }
}
