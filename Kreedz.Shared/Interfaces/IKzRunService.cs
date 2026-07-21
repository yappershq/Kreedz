using System;
using Sharp.Shared.Units;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// Public read/notify surface over a player's KZ run, published by Kreedz.Core so external plugins
/// (HUD, Global submitter, …) don't need Core-internal timer/checkpoint services. Exposes the live timer
/// info + checkpoint/teleport counts, and a finish event carrying the run result. Mirrors the mode/style
/// registry pattern (resolve via <see cref="Identity"/> in OnAllModulesLoaded).
/// </summary>
public interface IKzRunService
{
    static readonly string Identity = typeof(IKzRunService).FullName!;

    /// <summary>The player's live timer info (status + time), or null if no run state.</summary>
    ITimerInfo? GetTimerInfo(PlayerSlot slot);

    /// <summary>Teleports used this run (0 = Pro attempt).</summary>
    int GetTeleportCount(PlayerSlot slot);

    /// <summary>Checkpoints the player currently has saved.</summary>
    int GetCheckpointCount(PlayerSlot slot);

    /// <summary>Raised when a player finishes a run — (slot, finishInfo, teleports, styled).
    /// Styled runs are unranked; a submitter should skip them.</summary>
    event Action<PlayerSlot, ITimerInfo, int, bool>? RunFinished;
}
