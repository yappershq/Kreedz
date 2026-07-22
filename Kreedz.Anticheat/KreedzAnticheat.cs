/*
 * yappershq/Kreedz (KZ) — Anticheat plugin (1:1 cs2kz src/kz/anticheat)
 *
 * A standalone ModSharp module (split out of Core, like the mode/style plugins) — it depends only on
 * ISharedSystem primitives (hooks, convars, client manager), no Core-internal services, so it's a clean
 * drop-in that a server can install or omit.
 *
 * Detectors implemented:
 *   1. Invalid client-cvar — illegal client convar values that enable cheating (tampered m_yaw,
 *      out-of-range cl_pitchdown/up), checked on spawn.
 *   2. Bhop-hack + hyperscroll — cs2kz's landing-event window (detectors/bhop.cpp): per-landing jump-input
 *      counts from the subtick press stream feed three checks — >=25 perfs in the 30-landing window,
 *      repetitive low jump-pattern at >=18 perfs (macro), and avg pattern >=16 with >0.6 perf ratio
 *      (hyperscroll). Perfs only count when sv_jump_spam_penalty_time >= tick interval (CKZ sets 0 →
 *      desubtick 100% perfs are legit there, exactly like cs2kz).
 *   3. Snaptap + subtick desubticking — same-subtick counter-strafes / zero-`when` subtick command spam
 *      (kick/log-only, never autoban — cs2kz keeps these FP-sensitive classes kick-only).
 *   5. Autostrafe — scripted high strafes/sec over a rolling window of jumps.
 *   6. Strafe-optimizer — scripted yaw-accel pattern.
 * Flags feed the autoban accumulator (`kz_ac_autoban`, default off) and optionally kick (`kz_ac_autokick`).
 * All detection is disabled while `sv_cheats 1` and for fake clients.
 *
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Anticheat;

public sealed class KreedzAnticheat : IModSharpModule
{
    private const float PerfWindow = 0.02f; // cs2kz BH_PERF_WINDOW — <=1 ground tick at 64t = frame-perfect
    private const float TickTime   = 1f / 64f;

    // cs2kz detectors/bhop.cpp landing-event window constants (tick units where cs2kz uses cmd/time units).
    private const int   BhopMinAirTicks     = 4;    // MIN_AIR_TIME_FOR_BHOP
    private const int   BhopIgnoreTicks     = 4;    // BHOP_IGNORE_DURATION (teleport/movetype guard)
    private const int   JumpPurgeTicks      = 16;   // OLD_JUMP_PURGE_THRESHOLD (0.25s * 64)
    private const int   BhopMinSamples      = 20;   // MIN_SAMPLE_COUNT
    private const int   BhopWindowSize      = 30;   // WINDOW_SIZE
    private const int   PerfsForInfraction  = 25;   // NUM_CONSECUTIVE_PERFS_FOR_INFRACTION
    private const int   PerfsForPatternCheck = 18;  // NUM_CONSECUTIVE_PERFS_FOR_PATTERN_CHECK
    private const float HyperscrollPerfRatio = 0.6f; // PERF_RATIO_FOR_HYPERSCROLL_INFRACTION
    private const float RepetitivePatternRatio = 0.9f; // REPETITIVE_PATTERN_THRESHOLD
    private const int   LowPatternThreshold  = 4;   // LOW_PATTERN_THRESHOLD
    private const float HighPatternThreshold = 16f; // HIGH_PATTERN_THRESHOLD

    private struct LandingEvent
    {
        public int  Tick;        // cmdNum
        public bool PendingPerf; // true until a fully-grounded tick passes after the landing
        public bool Perf;        // hasPerfectBhop
        public bool Eligible;    // shouldCountTowardsPerfChains — perf with jump-spam penalty active
        public int  JumpsBefore; // jump presses in the last 0.25s at landing
        public int  JumpsAfter;  // jump presses in the 0.25s after landing
    }

    private readonly ISharedSystem           _shared;
    private readonly IModSharp               _modSharp;
    private readonly IHookManager            _hookManager;
    private readonly IClientManager          _clientManager;
    private readonly ILogger<KreedzAnticheat> _logger;

    private IRequestManager?  _request;  // resolved cross-plugin for infraction persistence
    private IKzStyleRegistry? _styles;   // resolved cross-plugin to skip AC on styled movement
    private IKzAcEvidence?    _evidence; // resolved cross-plugin to attach replay clips to flags
    private readonly IConVar                 _autokick;
    private readonly IConVar                 _autoban;
    private readonly IConVar                 _banThreshold;
    private readonly IConVar                 _banMinutes;
    private readonly IConVar?                _svCheats;
    private readonly IConVar?                _svAutoBhop;      // sv_autobunnyhopping — no bhop detection while on
    private readonly IConVar?                _svJumpPenalty;   // sv_jump_spam_penalty_time — perfs only count when >= tick

    // Autoban accumulation (cs2kz Infraction→Finalize, simplified): confirmed flags within a fixed window;
    // once the count crosses kz_ac_ban_threshold, ban for kz_ac_ban_minutes. ALL per-slot state (including
    // this counter) resets on disconnect, so a reused slot never inherits a prior player's count.
    private const float BanWindow = 600f; // 10 min
    private readonly int[]   _infractions      = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[] _infractionWindow = new float[PlayerSlot.MaxPlayerCount];

    private readonly bool[]  _wasGround  = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[] _groundTime = new float[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _lastPos   = new Vector[PlayerSlot.MaxPlayerCount]; // for the telehop guard
    private readonly int[]    _tpGuard   = new int[PlayerSlot.MaxPlayerCount];    // ticks left ignoring bhops after a teleport
    private readonly int[]    _mtGuard   = new int[PlayerSlot.MaxPlayerCount];    // ticks left ignoring bhops after noclip/ladder
    private readonly int[]    _airTicks  = new int[PlayerSlot.MaxPlayerCount];
    private readonly List<float>[]        _recentJumps   = NewLists<float>();        // jump-press times (tick + subtick when)
    private readonly List<LandingEvent>[] _landingEvents = NewLists<LandingEvent>();

    private static List<T>[] NewLists<T>()
    {
        var a = new List<T>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new List<T>();
        return a;
    }

    // Subtick snaptap detector (cs2kz nulls): perfect same-subtick counter-strafes no human hits.
    private const float SnaptapEpsilon = 0.0078125f; // ~1 subtick — release+press this close = perfect
    private const int   SnaptapChain   = 128;        // cs2kz NUM_CONSECUTIVE_PERFECT_CSTRAFE minimum
    private readonly int[] _snapChain   = new int[PlayerSlot.MaxPlayerCount];

    // Desubticking detector (cs2kz subtick.cpp). A legit KB+M subtick move carries a fractional `When`; a
    // "desubticking" cheat zeroes it. Over a ~20s window, if the vast majority of subtick-carrying commands
    // are all-zero-`When`, flag. NOTE: cs2kz's other subtick checks (VerifyCommand, the "suspicious moves
    // with angles" count that SUBTICK_SUSPICIOUS_MOVES_THRESHOLD actually gates) need the pitch/yaw-delta +
    // full buttonstate protobuf fields ModSharp's MoveData does NOT expose — so only this zero-`When` ratio
    // check is portable. The old raw-subtick-move count was WRONG (every legit strafe carries subtick moves).
    private const int   DesubtickWindowTicks = 1280; // ~20s @ 64t  (SUBTICK_SUBTICK_INPUTS_WINDOW)
    private const int   DesubtickMinCommands = 30;   //             (SUBTICK_SUBTICK_INPUTS_THRESHOLD)
    private const float DesubtickRatio       = 0.9f; //             (SUBTICK_ZERO_WHEN_RATIO_THRESHOLD)
    private const int   DesubtickWarmupTicks = 640;  // ~10s ignore on connect (SUBTICK_INITIAL_IGNORE_TIME)
    private readonly int[] _subtickCmds     = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[] _subtickZeroWhen = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[] _subtickWinTicks = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[] _subtickSeen     = new int[PlayerSlot.MaxPlayerCount];

    // Autostrafe detector (cs2kz jumps.cpp) — a script strafes far more per second than a human. Per jump
    // (airtime >= 0.6s, sync > 0.7): if strafes/sec exceeds thresholds it's suspicious; too many suspicious
    // jumps in a rolling window of 20 flags a strafe-hack.
    private const float AsMinAirtime  = 0.6f;
    private const float AsMinSync     = 0.7f;
    private const float AsBaseSps     = 18.0f;  // REAL_STRAFE_PER_SECOND_THRESHOLD
    private const float AsMaxSps      = 30.0f;  // MAX_STRAFES_PER_SECOND_THRESHOLD
    private const int   AsWindow      = 20;
    private const int   AsBaseSusp    = 15;
    private const int   AsMinSusp     = 5;
    private readonly bool[]  _jumpTracking = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpAir      = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpGain     = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpStrafes  = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[] _jumpLastSpd  = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _jumpLastYaw  = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpYawDir   = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[][] _susWindow   = NewJaggedBool(AsWindow); // rolling suspicious flags
    private readonly bool[][] _veryHighWin = NewJaggedBool(AsWindow);
    private readonly int[]   _susIdx       = new int[PlayerSlot.MaxPlayerCount];

    private static bool[][] NewJaggedBool(int depth)
    {
        var a = new bool[PlayerSlot.MaxPlayerCount][];
        for (var i = 0; i < a.Length; i++) a[i] = new bool[depth];
        return a;
    }

    // Strafe-optimizer detector (cs2kz strafe_optimizer.cpp): a scripted optimizer snaps the yaw at the
    // exact optimal strafe-reversal, producing a yaw-accel spike a human mouse can't. Rolling average of
    // spike occurrences; flag past 0.9. Needs 6 angle frames to compute accel at the 3 sample points.
    private readonly float[][] _yawBuf  = NewJagged(6);
    private readonly float[][] _ftBuf   = NewJagged(6);
    private readonly int[]     _yawLen  = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[]   _soPct   = new float[PlayerSlot.MaxPlayerCount];

    private static float[][] NewJagged(int depth)
    {
        var a = new float[PlayerSlot.MaxPlayerCount][];
        for (var i = 0; i < a.Length; i++) a[i] = new float[depth];
        return a;
    }

    public string DisplayName   => "[Kreedz] Anticheat";
    public string DisplayAuthor => "yappershq";

    public KreedzAnticheat(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _modSharp      = shared.GetModSharp();
        _hookManager   = shared.GetHookManager();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzAnticheat>();

        var cvar = shared.GetConVarManager();
        _autokick = cvar.CreateConVar("kz_ac_autokick", false,
            "Anticheat kicks flagged players instead of warning.")!;
        _autoban = cvar.CreateConVar("kz_ac_autoban", false,
            "Anticheat bans a player after kz_ac_ban_threshold flags within 10 minutes.")!;
        _banThreshold = cvar.CreateConVar("kz_ac_ban_threshold", 3,
            "Number of flags within the window that triggers an autoban (see kz_ac_autoban).")!;
        _banMinutes = cvar.CreateConVar("kz_ac_ban_minutes", 1440,
            "Autoban duration in minutes (default 1440 = 1 day).")!;
        _svCheats      = cvar.FindConVar("sv_cheats");
        _svAutoBhop    = cvar.FindConVar("sv_autobunnyhopping");
        _svJumpPenalty = cvar.FindConVar("sv_jump_spam_penalty_time");
    }

    public bool Init()
    {
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _hookManager.PlayerRunCommand.InstallHookPost(OnRunCommandPost);
        _clientManager.InstallClientListener(_clientListener = new ClientListener(this));
        return true;
    }

    // cs2kz KZAnticheatService::Reset — EVERY per-slot buffer clears when the slot changes hands,
    // so a new player never inherits the previous occupant's chains, counters, or infractions.
    private sealed class ClientListener(KreedzAnticheat owner) : Sharp.Shared.Listeners.IClientListener
    {
        public int ListenerVersion  => Sharp.Shared.Listeners.IClientListener.ApiVersion;
        public int ListenerPriority => 0;

        public void OnClientConnected(IGameClient client)    => owner.ResetSlot(client.Slot);
        public void OnClientDisconnected(IGameClient client) => owner.ResetSlot(client.Slot);
    }

    private ClientListener? _clientListener;

    private void ResetSlot(PlayerSlot slot)
    {
        _landingEvents[slot].Clear();
        _recentJumps[slot].Clear();
        _snapChain[slot]        = 0;
        _subtickCmds[slot]      = 0;
        _subtickZeroWhen[slot]  = 0;
        _subtickWinTicks[slot]  = 0;
        _subtickSeen[slot]      = 0;
        _wasGround[slot]        = false;
        _groundTime[slot]       = 0f;
        _airTicks[slot]         = 0;
        _lastPos[slot]          = default;
        _tpGuard[slot]          = 0;
        _mtGuard[slot]          = 0;
        _soPct[slot]            = 0f;
        _yawLen[slot]           = 0;
        _jumpTracking[slot]     = false;
        _susIdx[slot]           = 0;
        Array.Clear(_susWindow[slot]);
        Array.Clear(_veryHighWin[slot]);
        _infractions[slot]      = 0;
        _infractionWindow[slot] = 0f;
    }

    public void OnAllModulesLoaded()
    {
        var mgr   = _shared.GetSharpModuleManager();
        _request  = mgr.GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity)?.Instance;
        _styles   = mgr.GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;
        _evidence = mgr.GetOptionalSharpModuleInterface<IKzAcEvidence>(IKzAcEvidence.Identity)?.Instance;
    }

    // Per-command stream processing. NOTE: the old tick-resolution "nulls" detector was REMOVED after
    // parity review — cs2kz's real nulls detector needs subtick-exact timing over a 2048-event buffer
    // with FPS scaling; a tick-level 20-chain approximation can false-flag skilled legit strafers.
    private void OnRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;
        if (client.IsValid && !client.IsFakeClient && !(_svCheats?.GetBool() ?? false))
        {
            ParseCommandForJump(param, client.Slot);
        }
    }

    // cs2kz ParseCommandForJump — collect jump-press times from the subtick stream (or newly-pressed button
    // when the command carries no subtick moves), bump JumpsAfter on landings within the last 0.25s, and
    // purge presses older than 0.25s. Presses are taken at face value (faking +jump helps nothing).
    private unsafe void ParseCommandForJump(IPlayerRunCommandHookParams param, PlayerSlot slot)
    {
        var now    = (float) _modSharp.GetGlobals().TickCount;
        var jumps  = _recentJumps[slot];
        var events = _landingEvents[slot];

        void AddJump(float when)
        {
            jumps.Add(when);
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(events);
            foreach (ref var e in span)
                if (e.Tick >= now - JumpPurgeTicks)
                    e.JumpsAfter++;
        }

        var n = param.SubtickMoveSize;
        if (n == 0)
        {
            if ((param.ChangedButtons & param.KeyButtons & UserCommandButtons.Jump) != 0)
                AddJump(now);
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                var step = param.GetSubtickMove(i);
                if (step != null && step->Buttons == UserCommandButtons.Jump && step->Pressed)
                    AddJump(now + step->When);
            }
        }

        for (var i = jumps.Count - 1; i >= 0; i--) // purge presses older than 0.25s (no-alloc RemoveAll)
            if (now - jumps[i] > JumpPurgeTicks)
                jumps.RemoveAt(i);
    }

    private readonly Dictionary<int, int> _patternScratch = new(); // reused per check — hooks are main-thread

    // cs2kz CheckLandingEvents — the 30-landing window, three checks: >=25 perfs = bhop-hack; >=18 perfs with
    // a repetitive low jump-pattern (>=90% same total, < 4) = macro bhop-hack; avg pattern >=16 with >60% perf
    // ratio over >=20 eligible = hyperscroll (scroll spam lands many jump inputs around every landing).
    // Only penalty-eligible perfs count (see LandingEvent.Eligible); patterns count any landing with inputs.
    private void CheckLandingEvents(IGameClient client, PlayerSlot slot)
    {
        var events = _landingEvents[slot];
        while (events.Count > BhopWindowSize) events.RemoveAt(0);
        if (events.Count < BhopMinSamples) return;

        int numPerfs = 0, eligible = 0, maxChain = 0, curChain = 0;
        int mostCommon = 0, mostCommonCount = 0, patternOccurrences = 0, weightedSum = 0;
        _patternScratch.Clear();

        foreach (var e in events)
        {
            if (e.JumpsBefore > 0 || e.JumpsAfter > 0)
            {
                var pattern = e.JumpsBefore + e.JumpsAfter;
                var count   = _patternScratch.GetValueOrDefault(pattern) + 1;
                _patternScratch[pattern] = count;
                patternOccurrences++;
                weightedSum += pattern;
                if (count > mostCommonCount) { mostCommonCount = count; mostCommon = pattern; }
            }

            if (!e.Eligible) continue;
            eligible++;
            if (e.Perf) { numPerfs++; maxChain = Math.Max(maxChain, ++curChain); }
            else          curChain = 0;
        }

        var avgPattern = patternOccurrences > 0 ? (float) weightedSum / patternOccurrences : 0f;

        if (maxChain >= PerfsForInfraction)
        {
            events.Clear(); // reset the window so one burst doesn't re-flag every tick
            Flag(client, $"bhop-hack ({maxChain}/{eligible} perfect bhops in the window)");
            return;
        }

        if (maxChain >= PerfsForPatternCheck && patternOccurrences > 0
            && mostCommonCount >= patternOccurrences * RepetitivePatternRatio && mostCommon < LowPatternThreshold)
        {
            events.Clear();
            Flag(client, $"bhop-hack ({mostCommonCount}/{patternOccurrences} occurrences of jump pattern {mostCommon}, avg {avgPattern:F2})");
            return;
        }

        var perfRatio = eligible > 0 ? (float) numPerfs / eligible : 0f;
        if (avgPattern >= HighPatternThreshold && perfRatio > HyperscrollPerfRatio && eligible >= BhopMinSamples)
        {
            events.Clear();
            Flag(client, $"hyperscroll (avg jump pattern {avgPattern:F2} >= {HighPatternThreshold} with {perfRatio * 100f:F0}% perfect ratio ({numPerfs}/{eligible}))");
        }
    }

    public void Shutdown()
    {
        _hookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _hookManager.PlayerRunCommand.RemoveHookPost(OnRunCommandPost);
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive) return;
        if (_svCheats?.GetBool() ?? false) return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (onGround && !_wasGround[slot]) _groundTime[slot] = 0f; // landed
        if (onGround) _groundTime[slot] += TickTime;

        // cs2kz bhop guards: don't count perfs while noclipping/on a ladder (MoveType != Walk), or within 4
        // ticks of a teleport (BHOP_IGNORE_DURATION) — a large one-tick position jump is treated as a telehop.
        var origin = arg.Pawn.GetAbsOrigin();
        var dx = origin.X - _lastPos[slot].X;
        var dy = origin.Y - _lastPos[slot].Y;
        if (MathF.Sqrt(dx * dx + dy * dy) > 128f) _tpGuard[slot] = 4;
        else if (_tpGuard[slot] > 0)              _tpGuard[slot]--;
        _lastPos[slot] = origin;

        // cs2kz OnChangeMoveType guard — landings within 4 ticks of noclip/ladder don't create events.
        if (arg.Pawn.ActualMoveType != MoveType.Walk) _mtGuard[slot] = BhopIgnoreTicks;
        else if (_mtGuard[slot] > 0)                  _mtGuard[slot]--;

        var events = _landingEvents[slot];

        if (onGround && !_wasGround[slot]) // landed — cs2kz CreateLandEvent
        {
            if (_airTicks[slot] >= BhopMinAirTicks && _mtGuard[slot] == 0 && _tpGuard[slot] == 0
                && !(_svAutoBhop?.GetBool() ?? false))
            {
                events.Add(new LandingEvent
                {
                    Tick        = _modSharp.GetGlobals().TickCount,
                    PendingPerf = true,
                    JumpsBefore = _recentJumps[slot].Count,
                });
            }
        }
        else if (onGround && _wasGround[slot] && events.Count > 0)
        {
            // A fully-grounded tick after the landing — no perf possible anymore (cs2kz pendingPerf clear).
            var last = events[^1];
            last.PendingPerf = false;
            events[^1] = last;
        }
        else if (!onGround && _wasGround[slot] && events.Count > 0) // took off — cs2kz OnJump
        {
            var last = events[^1];
            if (last.PendingPerf && _groundTime[slot] <= PerfWindow)
            {
                last.Perf = true;
                // Perfs only count toward chains when the jump-spam penalty is active (>= 1 tick) — with
                // penalty 0 (CKZ) a desubticking player can legitimately hit 100% perfs (cs2kz shouldCount).
                last.Eligible = (_svJumpPenalty?.GetFloat() ?? 0.0625f) >= TickTime;
                events[^1]    = last;
            }
        }

        if (!onGround) _airTicks[slot]++;
        else if (!_wasGround[slot]) _airTicks[slot] = 0; // reset after the landing checks used it

        CheckLandingEvents(client, slot);

        DetectAutostrafe(client, slot, arg.Pawn, onGround);

        _wasGround[slot] = onGround;

        DetectSnaptap(client, slot, arg);
        DetectStrafeOptimizer(client, slot, arg.Pawn.GetEyeAngles().Y, _modSharp.GetGlobals().FrameTime);
    }

    // cs2kz jumps.cpp autostrafe detector — per-jump strafes/sec over a rolling window of jumps.
    private void DetectAutostrafe(IGameClient client, PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn, bool onGround)
    {
        // cs2kz skips autostrafe detection for styled movement (e.g. ABH) — a style legitimately alters strafing.
        if (_styles?.HasAnyStyle(slot) == true) return;

        var vel   = pawn.GetAbsVelocity();
        var horiz = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
        var yaw   = pawn.GetEyeAngles().Y;

        if (_wasGround[slot] && !onGround) // takeoff
        {
            _jumpTracking[slot] = pawn.ActualMoveType is MoveType.Walk;
            _jumpAir[slot] = _jumpGain[slot] = _jumpStrafes[slot] = 0;
            _jumpLastSpd[slot] = horiz; _jumpLastYaw[slot] = yaw; _jumpYawDir[slot] = 0;
        }
        else if (!onGround && _jumpTracking[slot]) // airborne
        {
            _jumpAir[slot]++;
            if (horiz > _jumpLastSpd[slot] + 0.01f) _jumpGain[slot]++;
            var dy  = NormalizeYaw(yaw - _jumpLastYaw[slot]);
            var dir = dy > 0.05f ? 1 : dy < -0.05f ? -1 : 0;
            if (dir != 0 && _jumpYawDir[slot] != 0 && dir != _jumpYawDir[slot]) _jumpStrafes[slot]++;
            if (dir != 0) _jumpYawDir[slot] = dir;
            _jumpLastSpd[slot] = horiz; _jumpLastYaw[slot] = yaw;
        }
        else if (!_wasGround[slot] && onGround && _jumpTracking[slot]) // landing
        {
            _jumpTracking[slot] = false;
            var airtime = _jumpAir[slot] * TickTime;
            var sync    = _jumpAir[slot] > 0 ? (float) _jumpGain[slot] / _jumpAir[slot] : 0f;

            var suspicious = false; var veryHigh = false;
            if (airtime >= AsMinAirtime && sync > AsMinSync)
            {
                var sps = _jumpStrafes[slot] / airtime;
                if (sps > AsMaxSps)      { suspicious = true; veryHigh = true; }
                else if (sps > AsBaseSps) suspicious = true;
            }

            var i = _susIdx[slot];
            _susWindow[slot][i]   = suspicious;
            _veryHighWin[slot][i] = veryHigh;
            _susIdx[slot] = (i + 1) % AsWindow;

            int susCount = 0, vhCount = 0;
            for (var k = 0; k < AsWindow; k++) { if (_susWindow[slot][k]) susCount++; if (_veryHighWin[slot][k]) vhCount++; }

            if (susCount >= AsBaseSusp || (susCount >= AsMinSusp && vhCount > 0))
            {
                Flag(client, $"autostrafe ({susCount}/{AsWindow} high-strafe jumps)");
                for (var k = 0; k < AsWindow; k++) { _susWindow[slot][k] = false; _veryHighWin[slot][k] = false; }
            }
        }
    }

    // cs2kz KZAnticheatService::DetectOptimization — flags a scripted strafe optimizer by its yaw-accel
    // spike at strafe reversals (a human mouse can't produce it). Buffer of the last 6 (yaw, frametime);
    // yaw speed = Δyaw/ft, yaw accel = Δspeed/ft; a low-avg-accel window with a lone spike at a direction
    // switch bumps a rolling average toward 1; > 0.9 = detected.
    private void DetectStrafeOptimizer(IGameClient client, PlayerSlot slot, float yaw, float ft)
    {
        if (ft <= 0f) return;
        var yb = _yawBuf[slot]; var fb = _ftBuf[slot];
        for (var i = 0; i < 5; i++) { yb[i] = yb[i + 1]; fb[i] = fb[i + 1]; } // shift, newest at [5]
        yb[5] = yaw; fb[5] = ft;
        if (_yawLen[slot] < 6) { _yawLen[slot]++; return; }

        float Speed(int i) => fb[i] > 0f ? NormalizeYaw(yb[i] - yb[i - 1]) / fb[i] : 0f;
        float Accel(int i) => fb[i] > 0f ? (Speed(i) - Speed(i - 1)) / fb[i] : 0f;

        var curSpeed = Speed(5); var lastSpeed = Speed(4);
        var switched = (curSpeed < 0f) != (lastSpeed < 0f);

        var accel2ago = MathF.Abs(Accel(3));
        var lastAccel = MathF.Abs(Accel(4));
        var curAccel  = MathF.Abs(Accel(5));

        if (MathF.Abs(curAccel - accel2ago) < 1.0f)
        {
            var avg = (curAccel + accel2ago) * 0.5f;
            if (avg < 2.0f && (lastAccel - avg) > 2.0f && switched)
                _soPct[slot] = _soPct[slot] * 0.95f + 0.05f;        // spike at a reversal
            else if (switched)
                _soPct[slot] = _soPct[slot] * 0.95f;                // clean reversal
        }

        if (_soPct[slot] > 0.9f)
        {
            Flag(client, "strafe-optimizer (scripted yaw-accel pattern)");
            _soPct[slot] = 0f;
        }
    }

    private static float NormalizeYaw(float a)
    {
        while (a >  180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    // Subtick snaptap/nulls detector (cs2kz src/kz/anticheat/detectors/nulls). A snaptap/SOCD device
    // cancels one strafe key the instant the opposite is pressed — a release + opposite-press at the
    // SAME subtick `When`, perfectly, every counter-strafe. Humans have an underlap gap. Reads the real
    // subtick move data (MoveData.SubTickMoves) and counts consecutive same-subtick counter-strafes.
    private unsafe void DetectSnaptap(IGameClient client, PlayerSlot slot, IPlayerProcessMoveForwardParams arg)
    {
        var moves = arg.Info->SubTickMoves.AsReadOnlySpan();

        // ── Desubticking check (cs2kz subtick.cpp): over ~20s, if ≥90% of subtick-carrying commands have
        // all-zero `When` on their button moves, it's a desubticking cheat. Skip a ~10s warmup on connect.
        if (_subtickSeen[slot] <= DesubtickWarmupTicks) _subtickSeen[slot]++;
        if (_subtickSeen[slot] > DesubtickWarmupTicks)
        {
            var hasButtonMove = false;
            var allZeroWhen   = true;
            foreach (ref readonly var mv in moves)
            {
                if ((int) mv.Button == 0) continue; // no button = analog/mouse — cs2kz excludes these
                hasButtonMove = true;
                if (mv.When != 0f) allZeroWhen = false;
            }

            if (hasButtonMove)
            {
                _subtickCmds[slot]++;
                if (allZeroWhen) _subtickZeroWhen[slot]++;
            }

            if (++_subtickWinTicks[slot] >= DesubtickWindowTicks)
            {
                if (_subtickCmds[slot] >= DesubtickMinCommands
                    && (float) _subtickZeroWhen[slot] / _subtickCmds[slot] >= DesubtickRatio)
                    Flag(client, $"desubticking ({_subtickZeroWhen[slot]}/{_subtickCmds[slot]} zero-when subtick cmds in 20s)", banEligible: false);

                _subtickCmds[slot] = _subtickZeroWhen[slot] = _subtickWinTicks[slot] = 0;
            }
        }

        if (moves.Length == 0) return;

        // Track release/press per axis independently — a MoveLeft-release + Forward-press isn't a counter-strafe.
        // cs2kz checks BOTH the left/right (A/D) and forward/back (W/S) axes for null-alias counter-strafes.
        float relWhenLR = -1f, relWhenFB = -1f;
        UserCommandButtons relKeyLR = 0, relKeyFB = 0;
        var strafed = false;

        foreach (ref readonly var m in moves)
        {
            var lr = m.Button is UserCommandButtons.MoveLeft or UserCommandButtons.MoveRight;
            var fb = m.Button is UserCommandButtons.Forward  or UserCommandButtons.Back;
            if (!lr && !fb) continue;
            strafed = true;

            ref var relWhen = ref lr ? ref relWhenLR : ref relWhenFB;
            ref var relKey  = ref lr ? ref relKeyLR  : ref relKeyFB;

            if (!m.Pressed) { relWhen = m.When; relKey = m.Button; continue; }

            // A press of the opposite key of the same axis right after releasing it = a counter-strafe.
            if (relWhen >= 0f && m.Button != relKey)
            {
                RegisterCounterStrafe(slot, client, m.When, relWhen);
                relWhen = -1f;
            }
        }

        // Parity fix: a command without strafe moves (e.g. a jump press) does NOT reset the chain —
        // cs2kz analyzes a persistent event buffer; only a real underlap gap resets (see RegisterCounterStrafe).
        _ = strafed;
    }

    // A same-subtick (release+opposite-press within ~1 subtick) counter-strafe is inhumanly perfect; a run of
    // them (SnaptapChain) is a null-alias device. A real underlap gap resets the chain (human).
    private void RegisterCounterStrafe(PlayerSlot slot, IGameClient client, float pressWhen, float releaseWhen)
    {
        if (MathF.Abs(pressWhen - releaseWhen) < SnaptapEpsilon)
        {
            if (++_snapChain[slot] >= SnaptapChain)
            {
                Flag(client, $"snaptap ({_snapChain[slot]} perfect same-subtick counter-strafes)", banEligible: false);
                _snapChain[slot] = 0;
            }
        }
        else
        {
            _snapChain[slot] = 0;
        }
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;
        if (client.IsFakeClient) return;

        // Fresh bhop window per spawn — also stops a reused slot inheriting the last player's events
        // (cs2kz clears these in its per-player Reset on connect).
        _landingEvents[client.Slot].Clear();
        _recentJumps[client.Slot].Clear();

        // Client cvars replicate shortly after spawn — check next frame.
        _modSharp.InvokeFrameAction(() => CheckClient(client));
    }

    // sv_cheats replication grace (cs2kz ShouldEnforceCheatCvars): after the server toggles sv_cheats
    // 1→0, clients keep the replicated value for a while — don't flag cheat-class cvars for 30s.
    private const float SvCheatsGrace = 30f;
    private float _svCheatsOnUntil = -1000f;

    private void CheckClient(IGameClient client)
    {
        if (!client.IsValid) return;

        if (_svCheats?.GetBool() ?? false)
        {
            _svCheatsOnUntil = _modSharp.GetGlobals().CurTime;
            return; // no detection while sv_cheats
        }

        // Userinfo-replicated cvars — readable synchronously (cs2kz's own userinfo split: m_yaw, sensitivity).
        // Movement-integrity class: kick-only, never fed to the autoban counter (cs2kz cvars.cpp kicks these).
        if (Value(client, "m_yaw") is { } yaw && yaw > 0.3)                    { Flag(client, "invalid cvar (m_yaw)", banEligible: false); return; }
        if (Value(client, "sensitivity") is { } sv && (sv < 0.0001 || sv > 20.0)) { Flag(client, "invalid cvar (sensitivity)", banEligible: false); return; }

        // Everything else lives client-side only — GetConVarValue reads userinfo and returns null for
        // these (parity-review find: 9 of 11 checks were silently dead). Query the client asynchronously.
        QueryCheck(client, "fps_max",        v => v is > 0.0 and < 64.0,        banEligible: false);
        QueryCheck(client, "cl_pitchdown",   v => Math.Abs(v - 89.0)  > 0.001,  banEligible: false);
        QueryCheck(client, "cl_pitchup",     v => Math.Abs(v - 89.0)  > 0.001,  banEligible: false);
        QueryCheck(client, "cl_yawspeed",    v => Math.Abs(v - 210.0) > 0.001,  banEligible: false);
        QueryCheck(client, "sv_cheats",      v => v != 0.0, cheatClass: true);
        QueryCheck(client, "cl_showpos",     v => v != 0.0, cheatClass: true);
        QueryCheck(client, "cam_showangles", v => v != 0.0, cheatClass: true);
        QueryCheck(client, "cl_drawhud",     v => v == 0.0, cheatClass: true);
        QueryCheck(client, "fov_cs_debug",   v => v != 0.0, cheatClass: true);
    }

    private void QueryCheck(IGameClient client, string cvar, Func<double, bool> isViolation, bool banEligible = true, bool cheatClass = false)
    {
        _clientManager.QueryConVar(client, cvar, (cl, status, name, value) =>
        {
            if (status != QueryConVarValueStatus.ValueIntact || !cl.IsValid)
                return;

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !isViolation(v))
                return;

            // Cheat-class cvars mirror the server's sv_cheats — grace the replication window after a toggle.
            if (cheatClass && _modSharp.GetGlobals().CurTime - _svCheatsOnUntil < SvCheatsGrace)
                return;

            Flag(cl, $"invalid cvar ({name} = {value})", banEligible);
        });
    }

    private void Flag(IGameClient client, string reason, bool banEligible = true)
    {
        // Attach a replay-evidence clip of the last 20s when a run recording exists (cs2kz infraction evidence).
        if (_evidence?.SaveEvidenceClip(client.Slot, 20f) is { } clip)
            reason += $" [clip {clip}]";

        _logger.LogWarning("[KZ.AC] {Name} ({Sid}) flagged: {Reason}", client.Name, client.SteamId, reason);

        // Persist the infraction for review (cs2kz infractions.cpp) — fire-and-forget, degrades to no-op
        // if the request manager isn't available. Split the "<type> (<details>)" reason for the columns.
        if (_request is { } req)
        {
            var sid = client.SteamId;
            var paren = reason.IndexOf('(');
            var type = (paren > 0 ? reason[..paren] : reason).Trim();
            var details = paren > 0 ? reason[paren..].Trim('(', ')', ' ') : null;
            _ = SaveInfractionAsync(req, sid, type, details);
        }

        // Autoban accumulation (cs2kz Infraction→Finalize, simplified): once flags cross the threshold within
        // the window, ban + kick. Slot state resets on disconnect (see OnClientDisconnected). FP-sensitive
        // detector classes (snaptap/desubtick — cs2kz keeps their kin kick-only) never feed the ban counter.
        if (banEligible && _autoban.GetBool() && _request is { } banReq)
        {
            var slot = client.Slot;
            var now  = _modSharp.GetGlobals().CurTime;

            if (now - _infractionWindow[slot] > BanWindow)
            {
                _infractions[slot]      = 0;
                _infractionWindow[slot] = now;
            }

            if (++_infractions[slot] >= _banThreshold.GetInt32())
            {
                _infractions[slot] = 0;
                var expiresAt = DateTime.UtcNow.AddMinutes(_banMinutes.GetInt32());
                _ = BanAsync(banReq, client.SteamId, reason, expiresAt);
                _clientManager.KickClient(client, $"KZ: Anticheat ban ({reason})");
                return;
            }
        }

        if (_autokick.GetBool())
            _clientManager.KickClient(client, $"KZ: {reason}");
        else
            client.Print(HudPrintChannel.Chat, $"[KZ] Anticheat flagged: {reason}.");
    }

    private async System.Threading.Tasks.Task BanAsync(IRequestManager req, SteamID sid, string reason, DateTime expiresAt)
    {
        try
        {
            await req.AddBanAsync(sid, $"Anticheat: {reason}", expiresAt);
            _logger.LogWarning("[KZ.AC] auto-banned {Sid} until {Exp} ({Reason})", sid, expiresAt, reason);
        }
        catch (Exception e) { _logger.LogError(e, "[KZ.AC] failed to auto-ban {Sid}", sid); }
    }

    private async System.Threading.Tasks.Task SaveInfractionAsync(IRequestManager req, SteamID sid, string type, string? details)
    {
        try { await req.SaveInfractionAsync(sid, type, details); }
        catch (Exception e) { _logger.LogError(e, "[KZ.AC] failed to persist infraction for {Sid}", sid); }
    }

    private static double? Value(IGameClient client, string cvar)
        => double.TryParse(client.GetConVarValue(cvar), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
