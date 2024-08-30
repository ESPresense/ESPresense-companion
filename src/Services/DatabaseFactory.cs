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
        
        return new DeviceHistoryStore(_sqliteConnection);
    }
}
