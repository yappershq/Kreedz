using System;
using SqlSugar;

namespace Kreedz.Common.Entities;

[SugarTable("surf_players")]
[SugarIndex("idx_surf_players_steamid", nameof(SteamId), OrderByType.Asc, true)]
internal sealed class PlayerEntity : BaseSteamIdSerialEntity
{
    [SugarColumn(Length = 192)]
    public string Name { get; set; } = string.Empty;

    public uint Points { get; set; }
    public uint Runs   { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>KZ per-player options blob (JSON), owned by the OptionModule preference store. Nullable
    /// so CodeFirst adds it in-place on existing tables; unread until the OptionModule lands (P1+).</summary>
    [SugarColumn(ColumnDataType = "text", IsNullable = true)]
    public string? Preferences { get; set; }
}
