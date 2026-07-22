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

    /// <summary>Player entered a teleport-family trigger (Teleport / Multi/Single/Sequential bhop).</summary>
    void EnterTeleport(PlayerSlot slot, uint triggerHandle, in KzMapTeleportData data, Vector triggerOrigin);

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

    // Teleport-family triggers currently touched (cs2kz triggerTrackers): handle → data + touch time.
    private readonly record struct TeleportState(KzMapTeleportData Data, float StartTouchTime, Vector TriggerOrigin);

    private const int SequentialBhopMemory = 64; // cs2kz CSequentialBhopBuffer size

    private readonly Dictionary<uint, TeleportState>[] _teleports      = NewDicts<TeleportState>();
    private readonly uint[]                            _lastSingleBhop = new uint[PlayerSlot.MaxPlayerCount];
    private readonly Queue<uint>[]                     _seqBhops       = NewQueues();

    private static Queue<uint>[] NewQueues()
    {
        var a = new Queue<uint>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new Queue<uint>(SequentialBhopMemory);
        return a;
    }

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

    private readonly IMapApiSource _mapApi;

    public TriggerModifierModule(InterfaceBridge bridge, IMapApiSource mapApiSource, ILogger<TriggerModifierModule> logger)
    {
        _bridge = bridge;
        _mapApi = mapApiSource;
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

    public void EnterTeleport(PlayerSlot slot, uint triggerHandle, in KzMapTeleportData data, Vector triggerOrigin)
        => _teleports[slot][triggerHandle] = new TeleportState(data, _bridge.GlobalVars.CurTime, triggerOrigin);

    public void Exit(PlayerSlot slot, uint triggerHandle)
    {
        _antibhops[slot].Remove(triggerHandle);
        _modifiers[slot].Remove(triggerHandle);
        _teleports[slot].Remove(triggerHandle);
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
        var tookOff  = !onGround && _wasGround[slot];

        if (onGround && !_wasGround[slot])
            _landTime[slot] = _bridge.GlobalVars.CurTime;

        _onGround[slot]  = onGround;
        _wasGround[slot] = onGround;

        // cs2kz OnStopTouchGround — leaving the ground while on bhop triggers records them for the
        // single/sequential "can't repeat" rules. Any bhop type updates lastSingleBhop (jumping between
        // a multi and a single must work); only sequential types enter the sequential memory.
        if (tookOff)
        {
            foreach (var (handle, st) in _teleports[slot])
            {
                if (!KzTrigger.IsBhop(st.Data.Type))
                    continue;

                if (st.Data.Type == KzTriggerType.SequentialBhop)
                {
                    if (_seqBhops[slot].Count >= SequentialBhopMemory)
                        _seqBhops[slot].Dequeue();
                    _seqBhops[slot].Enqueue(handle);
                }

                _lastSingleBhop[slot] = handle;
            }
        }

        // cs2kz ResetBhopState — grounded/laddered with no bhop trigger under you clears the memory.
        if ((onGround || arg.Pawn.ActualMoveType == MoveType.Ladder) && !AnyBhopTouching(slot))
        {
            _lastSingleBhop[slot] = 0;
            _seqBhops[slot].Clear();
        }

        EvaluateTeleports(slot, arg.Pawn, onGround);

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

    private bool AnyBhopTouching(PlayerSlot slot)
    {
        foreach (var st in _teleports[slot].Values)
            if (KzTrigger.IsBhop(st.Data.Type))
                return true;

        return false;
    }

    // cs2kz TouchTeleportTrigger — per-tick teleport decision for the teleport family. Bhop triggers only
    // fire on the ground after outstaying the grace delay (or on single/sequential repeat); plain teleports
    // fire immediately (delay <= 0) or after the delay.
    private void EvaluateTeleports(PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn, bool onGround)
    {
        if (_teleports[slot].Count == 0)
            return;

        var now = _bridge.GlobalVars.CurTime;

        foreach (var (handle, st) in _teleports[slot])
        {
            var isBhop         = KzTrigger.IsBhop(st.Data.Type);
            var shouldTeleport = false;

            if (isBhop)
            {
                if (!onGround)
                    continue;

                var effectiveStart = MathF.Max(_landTime[slot], st.StartTouchTime);
                if (now - effectiveStart > st.Data.Delay)
                    shouldTeleport = true;
                else if (st.Data.Type == KzTriggerType.SingleBhop)
                    shouldTeleport = _lastSingleBhop[slot] == handle;
                else if (st.Data.Type == KzTriggerType.SequentialBhop)
                    shouldTeleport = _seqBhops[slot].Contains(handle);
            }
            else
            {
                shouldTeleport = st.Data.Delay <= 0f || now - st.StartTouchTime > st.Data.Delay;
            }

            if (shouldTeleport && ExecuteTeleport(pawn, st))
                break; // one teleport per tick; the rest re-evaluate next tick
        }
    }

    private bool ExecuteTeleport(Sharp.Shared.GameEntities.IPlayerPawn pawn, in TeleportState st)
    {
        if (!_mapApi.TryResolveDestination(st.Data.Destination, out var destOrigin, out var destAngles))
        {
            _logger.LogWarning("[KZ.Trigger] invalid teleport destination \"{dest}\"", st.Data.Destination);
            return false;
        }

        var reorient    = st.Data.Reorient && destAngles.Y != 0f;
        var finalOrigin = destOrigin;

        if (st.Data.Relative)
        {
            var offset = pawn.GetAbsOrigin() - st.TriggerOrigin;
            if (reorient)
                offset = RotateYaw(offset, destAngles.Y);
            finalOrigin = destOrigin + offset;
        }

        var angles   = pawn.GetEyeAngles();
        var velocity = pawn.GetAbsVelocity();

        if (reorient)
        {
            velocity  = RotateYaw(velocity, destAngles.Y);
            angles.Y -= destAngles.Y; // cs2kz does exactly this (known quirk noted upstream)
        }
        else if (st.Data.UseDestAngles)
        {
            angles = destAngles;
        }

        pawn.Teleport(finalOrigin, angles, st.Data.ResetSpeed ? new Vector() : velocity);
        return true;
    }

    private static Vector RotateYaw(Vector v, float yawDeg)
    {
        var (sin, cos) = MathF.SinCos(yawDeg * MathF.PI / 180f);
        return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);
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
        _teleports[slot].Clear();
        _seqBhops[slot].Clear();
        _lastSingleBhop[slot] = 0;
        _gravityApplied[slot] = false;
        _duckApplied[slot]    = false;
    }
}
