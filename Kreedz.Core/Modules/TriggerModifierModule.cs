/*
 * Mapping-API anti-bhop + modifier trigger runtime (cs2kz src/kz/trigger — TouchAntibhopTrigger,
 * TouchModifierTrigger, UpdateModifiersInternal).
 *
 * ZoneModule feeds Enter/Exit from the trigger_multiple touch outputs; this module keeps the per-player
 * touching sets and applies the effects per tick:
 *   - Anti-bhop: while active (time==0, or on-ground shorter than the trigger's grace time, or airborne
 *     for prediction), jumping is blocked by stripping IN_JUMP from the usercmd (buttons + subtick jump
 *     presses) and holding OldJumpPressed — the managed equivalent of cs2kz's per-slot
 *     sv_jump_spam_penalty_time=999999.9 + m_nLastActualJumpPressTick trick (ModSharp has no per-slot
 *     server convar values).
 *   - Modifier: gravity scale per touching tick (reset to 1 when clear), force-duck via DuckOverride,
 *     and the disable-checkpoint/teleport/jumpstats/pause flags exposed for the other modules to query.
 *   - NOT portable yet (needs per-slot server convars ModSharp doesn't expose): enable_slide
 *     (sv_standable_normal/sv_walkable_normal/sv_airaccelerate), jump_impulse factor (sv_jump_impulse/
 *     sv_staminajumpcost), force_unduck (m_flLastDuckTime not exposed).
 *
 * State clears on spawn (round restarts respawn triggers with new handles, so stale handles never pin
 * a zone effect across rounds).
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Modules.MappingApi;

namespace Kreedz.Modules;

internal interface ITriggerModifiers
{
    void EnterAntiBhop(PlayerSlot slot, uint triggerHandle, float time);

    void EnterModifier(PlayerSlot slot, uint triggerHandle, in KzMapModifier modifier);

    void Exit(PlayerSlot slot, uint triggerHandle);

    /// <summary>timer_modifier_disable_checkpoints — player is in an anti-cp area.</summary>
    bool CheckpointsDisabled(PlayerSlot slot);

    /// <summary>timer_modifier_disable_teleports — checkpoint teleports blocked here.</summary>
    bool TeleportsDisabled(PlayerSlot slot);

    /// <summary>timer_modifier_disable_jumpstats — jumpstats suppressed here.</summary>
    bool JumpstatsDisabled(PlayerSlot slot);
}

internal sealed unsafe class TriggerModifierModule : IModule, ITriggerModifiers
{
    private readonly InterfaceBridge                _bridge;
    private readonly ILogger<TriggerModifierModule> _logger;

    private readonly Dictionary<uint, float>[]         _antibhops = NewDicts<float>();
    private readonly Dictionary<uint, KzMapModifier>[] _modifiers = NewDicts<KzMapModifier>();

    private readonly float[] _landTime       = new float[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _wasGround      = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _onGround       = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _gravityApplied = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _duckApplied    = new bool[PlayerSlot.MaxPlayerCount];

    private static Dictionary<uint, T>[] NewDicts<T>()
    {
        var a = new Dictionary<uint, T>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new Dictionary<uint, T>();
        return a;
    }

    public TriggerModifierModule(InterfaceBridge bridge, ILogger<TriggerModifierModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnRunCommandPre);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnRunCommandPre);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
    }

    public void EnterAntiBhop(PlayerSlot slot, uint triggerHandle, float time)
        => _antibhops[slot][triggerHandle] = time;

    public void EnterModifier(PlayerSlot slot, uint triggerHandle, in KzMapModifier modifier)
        => _modifiers[slot][triggerHandle] = modifier;

    public void Exit(PlayerSlot slot, uint triggerHandle)
    {
        _antibhops[slot].Remove(triggerHandle);
        _modifiers[slot].Remove(triggerHandle);
    }

    public bool CheckpointsDisabled(PlayerSlot slot) => AnyModifier(slot, static m => m.DisableCheckpoints);

    public bool TeleportsDisabled(PlayerSlot slot) => AnyModifier(slot, static m => m.DisableTeleports);

    public bool JumpstatsDisabled(PlayerSlot slot) => AnyModifier(slot, static m => m.DisableJumpstats);

    private bool AnyModifier(PlayerSlot slot, Func<KzMapModifier, bool> pred)
    {
        foreach (var m in _modifiers[slot].Values)
            if (pred(m))
                return true;

        return false;
    }

    // cs2kz TouchAntibhopTrigger — jump-block is active while: no grace time set, or the player hasn't
    // been grounded past the trigger's grace time, or they're airborne (for prediction).
    private bool AntiBhopActive(PlayerSlot slot)
    {
        if (_antibhops[slot].Count == 0)
            return false;

        var timeOnGround = _bridge.GlobalVars.CurTime - _landTime[slot];
        foreach (var time in _antibhops[slot].Values)
        {
            if (time == 0f || timeOnGround <= time || !_onGround[slot])
                return true;
        }

        return false;
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (onGround && !_wasGround[slot])
            _landTime[slot] = _bridge.GlobalVars.CurTime;

        _onGround[slot]  = onGround;
        _wasGround[slot] = onGround;

        // Anti-bhop: hold the legacy jump latch every active tick (cs2kz ApplyAntiBhop).
        if (AntiBhopActive(slot))
            arg.Service.OldJumpPressed = true;

        // Modifier gravity (cs2kz TouchModifierTrigger): applied per touching tick, last trigger wins;
        // reset to 1 once no gravity modifier is touching.
        var gravity = 1f;
        foreach (var m in _modifiers[slot].Values)
            if (m.Gravity != 1f)
                gravity = m.Gravity;

        if (gravity != 1f)
        {
            arg.Pawn.GravityScale = gravity;
            _gravityApplied[slot] = true;
        }
        else if (_gravityApplied[slot])
        {
            arg.Pawn.GravityScale = 1f;
            _gravityApplied[slot] = false;
        }

        // Forced duck via the movement service's duck override (cs2kz ApplyForcedDuck).
        var forceDuck = AnyModifier(slot, static m => m.ForceDuck);
        if (forceDuck)
        {
            arg.Service.DuckOverride = true;
            _duckApplied[slot]       = true;
        }
        else if (_duckApplied[slot])
        {
            arg.Service.DuckOverride = false;
            _duckApplied[slot]       = false;
        }
    }

    // Strip jump input at the usercmd level while anti-bhop is active — kills both the button path and
    // the subtick jump-press path before movement processing sees them.
    private HookReturnValue<EmptyHookReturn> OnRunCommandPre(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;
        if (client.IsValid && !client.IsFakeClient && AntiBhopActive(client.Slot))
        {
            param.KeyButtons     &= ~UserCommandButtons.Jump;
            param.ChangedButtons &= ~UserCommandButtons.Jump;

            for (var i = 0; i < param.SubtickMoveSize; i++)
            {
                var step = param.GetSubtickMove(i);
                if (step != null && step->Buttons == UserCommandButtons.Jump && step->Pressed)
                    step->Pressed = false; // turn the press into a harmless release
            }
        }

        return ret;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var slot = @params.Client.Slot;
        _antibhops[slot].Clear();
        _modifiers[slot].Clear();
        _gravityApplied[slot] = false;
        _duckApplied[slot]    = false;
    }
}
