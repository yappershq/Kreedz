using System;
using SqlSugar;

namespace Kreedz.Common.Entities;

[SugarTable("surf_player_map_stats")]
[SugarIndex("idx_player_map_stats_unique", nameof(SteamId), OrderByType.Asc, nameof(MapId), OrderByType.Asc, true)]
internal sealed class PlayerMapStatsEntity : BaseSteamIdSerialEntity
{
    public ulong MapId { get; set; }

    public float PlayTime { get; set; }

    public int PlayCount { get; set; }
}
