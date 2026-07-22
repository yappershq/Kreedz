using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// Anticheat evidence hook published by Core's replay recorder: snapshot the tail of a player's
/// recorded movement to an evidence replay file so an AC infraction carries reviewable proof
/// (cs2kz attaches replay evidence to its infractions).
/// </summary>
public interface IKzAcEvidence
{
    static readonly string Identity = typeof(IKzAcEvidence).FullName!;

    /// <summary>
    /// Write the last <paramref name="seconds"/> of the player's recorded frames to an evidence clip.
    /// Returns the clip file name, or null when nothing is being recorded for the player.
    /// The write is asynchronous — the returned name refers to a file that lands shortly after.
    /// </summary>
    string? SaveEvidenceClip(PlayerSlot slot, float seconds);
}
