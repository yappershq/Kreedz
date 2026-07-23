using System;
using Sharp.Shared.Units;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// Race control published by Kreedz.Core for an external event/gate plugin (e.g. an EventManager adapter).
/// Core stays event-agnostic: it exposes "start a race" and "who finished" and knows nothing about any event
/// system. A race = teleport the live field to the map Start zone and reset their timers; each player's clock
/// then auto-starts on the normal start-zone exit (standard cs2kz semantics — arming the field, not
/// force-starting every clock in the same tick). The first <see cref="PlayerFinished"/> after a
/// <see cref="StartRace"/> is the winner. Resolve via <see cref="Identity"/> in OnAllModulesLoaded.
/// </summary>
public interface IKzRaceControl
{
    static readonly string Identity = typeof(IKzRaceControl).FullName!;

    /// <summary>True while a race is armed/running.</summary>
    bool IsRaceActive { get; }

    /// <summary>Teleport every live, non-fake player to <paramref name="track"/>'s Start zone and reset their
    /// run (stop + change track), then mark the race active so finishes are reported.</summary>
    void StartRace(int track = 0);

    /// <summary>Arm one player mid-race (a latecomer, or re-arm after a DQ): teleport to Start + reset run.
    /// Returns false if the slot isn't a live player.</summary>
    bool ArmPlayer(PlayerSlot slot, int track = 0);

    /// <summary>Force-stop one player's timer (DQ) without teleporting them.</summary>
    void StopPlayer(PlayerSlot slot);

    /// <summary>Stop every player's timer and end the race — no further finishes are reported.</summary>
    void StopRace();

    /// <summary>Fires once per player the moment they finish the map while a race is active — (slot, run info,
    /// finish order starting at 1). The first invocation after <see cref="StartRace"/> is the winner.</summary>
    event Action<PlayerSlot, ITimerInfo, int>? PlayerFinished;
}
