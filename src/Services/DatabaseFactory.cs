using SQLite;
using ESPresense.Models;

namespace ESPresense.Services;

public class DatabaseFactory(SQLiteAsyncConnection sqliteConnection, ConfigLoader cfg)
{
    public async Task<DeviceHistoryStore> GetDeviceHistory()
    {
        return new DeviceHistoryStore(sqliteConnection, cfg);
    }
}
