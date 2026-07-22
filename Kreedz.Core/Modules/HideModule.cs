/*
 * !hide — cs2kz quiet service (src/kz/quiet). Per-viewer hiding of other players via ModSharp's
 * transmit manager: every other player's CONTROLLER is transmit-hooked (ModSharp pairs the pawn to it;
 * hooking pawns directly is rejected) and the per-viewer state cleared, plus their carried weapons'
 * world models. Preference-persisted ("hide"), reapplied on spawn (new pawns/weapons) and pref load.
 *
 * Sound/decal suppression (cs2kz OnPostEvent) rides ModSharp's PostEventAbstract hook: FireBullets
 * (shot sound + bullet decals), WeaponSound (reloads etc.) and SosStartSoundEvent (misc player sounds)
 * from a hidden player are stripped from every hiding viewer's receiver mask — hidden players are
 * both invisible AND inaudible, like cs2kz. Remaining deviation: custom-particle filtering (we have
 * no custom particle systems to filter).
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed class HideModule : IModule
{
    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly IPreferencesModule  _prefs;
    private readonly ILogger<HideModule> _logger;

    private readonly bool[] _hidden = new bool[PlayerSlot.MaxPlayerCount];

    public HideModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<HideModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("hide", (slot, _) =>
        {
            Toggle(slot);
            return ECommandAction.Handled;
        });

        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _prefs.Loaded += OnPreferencesLoaded;

        // cs2kz OnPostEvent — suppress hidden players' sounds/decals for hiding viewers.
        _bridge.ModSharp.HookNetMessage(ProtobufNetMessageType.GE_FireBulletsId);
        _bridge.ModSharp.HookNetMessage(ProtobufNetMessageType.CS_UM_WeaponSound);
        _bridge.ModSharp.HookNetMessage(ProtobufNetMessageType.GE_SosStartSoundEvent);
        _bridge.HookManager.PostEventAbstract.InstallHookPre(OnPostEvent);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _bridge.HookManager.PostEventAbstract.RemoveHookPre(OnPostEvent);
        _prefs.Loaded -= OnPreferencesLoaded;
    }

    private HookReturnValue<NetworkReceiver> OnPostEvent(IPostEventAbstractHookParams param, HookReturnValue<NetworkReceiver> ret)
    {
        var emitterEntIndex = param.MsgId switch
        {
            ProtobufNetMessageType.GE_FireBulletsId      => (int) ((param.Data.ReadUInt32("player") ?? 0) & 0x3FFF),
            ProtobufNetMessageType.CS_UM_WeaponSound     => param.Data.ReadInt32("entidx") ?? 0,
            ProtobufNetMessageType.GE_SosStartSoundEvent => param.Data.ReadInt32("source_entity_index") ?? 0,
            _                                            => 0,
        };

        if (emitterEntIndex <= 0)
            return ret;

        if (_bridge.EntityManager.FindEntityByIndex(new EntityIndex(emitterEntIndex)) is not { IsPlayerPawn: true } emitterEnt
            || emitterEnt.AsPlayerPawn() is not { } emitterPawn
            || emitterPawn.GetController() is not { IsValidEntity: true } emitterController)
            return ret;

        var emitterSlot = emitterController.PlayerSlot.AsPrimitive();
        var bits        = param.Receivers.AsPrimitive();
        var modified    = bits;

        for (var v = 0; v < PlayerSlot.MaxPlayerCount.AsPrimitive(); v++)
        {
            if (_hidden[v] && v != emitterSlot)
                modified &= ~(1UL << v);
        }

        return modified != bits
            ? new HookReturnValue<NetworkReceiver>(EHookAction.SkipCallReturnOverride, new NetworkReceiver(modified))
            : ret;
    }

    private void Toggle(PlayerSlot slot)
    {
        _hidden[slot] = !_hidden[slot];
        _prefs.Set(slot, "hide", _hidden[slot] ? "1" : "0");
        ApplyViewer(slot);

        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, _hidden[slot] ? "Kreedz_Hide_On" : "Kreedz_Hide_Off");
    }

    private void OnPreferencesLoaded(PlayerSlot slot)
    {
        _hidden[slot] = _prefs.Get(slot, "hide") == "1";
        if (_hidden[slot])
            ApplyViewer(slot);
    }

    // A spawn creates a fresh pawn + weapon set — every hiding viewer must re-hide the spawned player.
    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var spawned = @params.Client;
        if (spawned.IsFakeClient)
            return;

        for (var v = 0; v < PlayerSlot.MaxPlayerCount; v++)
        {
            if (!_hidden[v] || v == spawned.Slot)
                continue;

            if (_bridge.ClientManager.GetGameClient(new PlayerSlot((byte) v)) is { IsFakeClient: false } viewer)
                ApplyPair(viewer, spawned);
        }
    }

    /// <summary>(Re)apply the viewer's hide state against every other connected player.</summary>
    private void ApplyViewer(PlayerSlot viewerSlot)
    {
        if (_bridge.ClientManager.GetGameClient(viewerSlot) is not { IsFakeClient: false } viewer)
            return;

        for (var o = 0; o < PlayerSlot.MaxPlayerCount; o++)
        {
            if (o == viewerSlot)
                continue;

            if (_bridge.ClientManager.GetGameClient(new PlayerSlot((byte) o)) is { } other)
                ApplyPair(viewer, other);
        }
    }

    private void ApplyPair(Sharp.Shared.Objects.IGameClient viewer, Sharp.Shared.Objects.IGameClient other)
    {
        if (viewer.GetPlayerController() is not { IsValidEntity: true } viewerController
            || other.GetPlayerController() is not { IsValidEntity: true } otherController)
            return;

        var tm      = _bridge.TransmitManager;
        var visible = !_hidden[viewer.Slot];

        if (!tm.IsEntityHooked(otherController))
            tm.AddEntityHooks(otherController, true);
        tm.SetEntityState(otherController.Index, viewerController.Index, visible, -1);

        // Carried-weapon world models follow the pawn's visibility (cs2kz clears them in CheckTransmit).
        if (otherController.GetPlayerPawn() is { IsValidEntity: true } pawn
            && pawn.GetWeaponService() is { } weapons)
        {
            foreach (var handle in weapons.GetMyWeapons())
            {
                if (_bridge.EntityManager.FindEntityByHandle(handle) is not { IsValidEntity: true } weapon)
                    continue;

                if (!tm.IsEntityHooked(weapon))
                    tm.AddEntityHooks(weapon, true);
                tm.SetEntityState(weapon.Index, viewerController.Index, visible, -1);
            }
        }
    }
}
