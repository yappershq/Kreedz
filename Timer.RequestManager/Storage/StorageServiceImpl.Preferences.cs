using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public Task<string?> GetPreferencesAsync(SteamID steamId)
        => _db.Queryable<PlayerEntity>()
              .Where(x => x.SteamId == steamId)
              .Select(x => x.Preferences)
              .FirstAsync();

    // Update-only: the player row is created on connect (GetPlayerProfile), so it exists by save time.
    public Task SavePreferencesAsync(SteamID steamId, string json)
        => _db.Updateable<PlayerEntity>()
              .SetColumns(x => x.Preferences == json)
              .Where(x => x.SteamId == steamId)
              .ExecuteCommandAsync();
}
