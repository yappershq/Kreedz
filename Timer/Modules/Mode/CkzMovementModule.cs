/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * CKZ (Classic) movement — a faithful port of cs2kz's kz_mode_ckz prestrafe + perf/bhop math.
 *
 * The constants and the two core algorithms below are transcribed verbatim from
 * KZGlobalteam/cs2kz-metamod `src/kz/mode/kz_mode_ckz.{h,cpp}` (CalcPrestrafe / GetPrestrafeGain /
 * OnStopTouchGround). Prestrafe: turning on the ground builds a per-direction ratio (rewarded by turn
 * rate, decayed after a landing grace period) that raises max ground speed above 250 up to ~276 via a
 * `26 * (ratio/0.5)^0.5` curve. Perf: a jump within the 0.02s window keeps/normalizes landing speed
 * through the logarithmic `(51.5 - groundTime*75)*ln(v) - NORMALIZE` formula.
 *
 * Fidelity notes (honest — this is the crux, validated live not here):
 *   • Constants + formulas are exact (verified against source).
 *   • frametime/curtime come from the engine globals (IGlobalVars), matching cs2kz's GetGlobals().
 *   • cs2kz spreads this across StartTouch/StopTouch/CalcPrestrafe on its detoured movement pipeline;
 *     here it runs off the single PlayerProcessMovePre hook + ground-state transitions.
 *   • The perf origin.z ground-snap and possibleLadderHop guard are omitted (refinements, not speed).
 * Final tick-for-tick fidelity is a demo-validated pass; the math is no longer approximated.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Modules;

internal interface ICkzMovementModule;

internal sealed class CkzMovementModule : IModule, ICkzMovementModule
{
    // cs2kz kz_mode_ckz.h — verbatim.
    private const float SpeedNormal          = 250.0f;
    private const float PsSpeedMax            = 26.0f;
    private const float PsMinRewardRate       = 2.0f;
    private const float PsMaxRewardRate       = 15.5f;
    private const float PsMaxPsTime           = 0.50f;
    private const float PsTurnRateWindow      = 0.02f;
    private const float PsDecrementRatio      = 3.0f;
    private const float PsRatioToSpeed        = 0.5f;
    private const float PsLandingGracePeriod  = 0.25f;
    private const float BhPerfWindow          = 0.02f;
    private const float BhBaseMultiplier      = 51.5f;
    private const float BhLandingDecrement    = 75.0f;

    // #define BH_NORMALIZE_FACTOR (BH_BASE_MULTIPLIER*log(SPEED_NORMAL+PS_SPEED_MAX) - (SPEED_NORMAL+PS_SPEED_MAX))
    private static readonly float BhNormalizeFactor =
        BhBaseMultiplier * MathF.Log(SpeedNormal + PsSpeedMax) - (SpeedNormal + PsSpeedMax);

    private readonly InterfaceBridge            _bridge;
    private readonly IModeModule                _mode;
    private readonly ILogger<CkzMovementModule> _logger;

    private readonly float[] _bonusSpeed   = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _leftPreRatio  = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _rightPreRatio = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _lastYaw       = new float[PlayerSlot.MaxPlayerCount];

    private readonly bool[]   _wasGround       = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[]  _landingTime     = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[]  _takeoffTime     = new float[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _landingVelocity = new Vector[PlayerSlot.MaxPlayerCount];

    // Turn-rate history over PsTurnRateWindow — cs2kz angleHistory (rate deg/s, duration s).
    private readonly List<(float Rate, float Duration)>[] _angleHistory =
        new List<(float, float)>[PlayerSlot.MaxPlayerCount];

    public CkzMovementModule(InterfaceBridge bridge, IModeModule mode, ILogger<CkzMovementModule> logger)
    {
        _bridge = bridge;
        _mode   = mode;
        _logger = logger;

        for (var i = 0; i < _angleHistory.Length; i++)
            _angleHistory[i] = [];
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _bridge.HookManager.PlayerGetMaxSpeed.InstallHookPre(OnGetMaxSpeed);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _bridge.HookManager.PlayerGetMaxSpeed.RemoveHookPre(OnGetMaxSpeed);
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient) return;

        var slot = client.Slot;
        if (!IsCkz(slot) || !arg.Pawn.IsAlive)
        {
            _bonusSpeed[slot] = _leftPreRatio[slot] = _rightPreRatio[slot] = 0f;
            _angleHistory[slot].Clear();
            _wasGround[slot] = arg.Pawn.GroundEntityHandle.IsValid();
            return;
        }

        var globals   = _bridge.ModSharp.GetGlobals();
        var frametime = globals.FrameTime;
        var curtime   = globals.CurTime;

        var velocity = arg.Pawn.GetAbsVelocity();
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        // Turn rate (deg/s) into the rolling window.
        var yaw   = arg.Pawn.GetEyeAngles().Y;
        var rate  = frametime > 0f ? NormalizeYaw(yaw - _lastYaw[slot]) / frametime : 0f;
        _lastYaw[slot] = yaw;
        PushAngle(slot, rate, frametime);

        // Ground-state transitions: landing captures velocity/time; takeoff runs the perf calc.
        if (onGround && !_wasGround[slot])
        {
            _landingTime[slot]     = curtime;
            _landingVelocity[slot] = velocity;
        }
        else if (!onGround && _wasGround[slot])
        {
            _takeoffTime[slot] = curtime;
            ApplyPerf(arg, slot, velocity);
        }

        CalcPrestrafe(slot, arg.Pawn.GetAbsVelocity(), onGround, frametime, curtime);

        _wasGround[slot] = onGround;
    }

    /// <summary>cs2kz KZClassicModeService::CalcPrestrafe — updates the L/R prestrafe ratios + bonusSpeed.</summary>
    private void CalcPrestrafe(PlayerSlot slot, Vector velocity, bool onGround, float frametime, float curtime)
    {
        var averageRate = AverageTurnRate(slot);

        var rewardRate = Math.Clamp(MathF.Abs(averageRate) / PsMaxRewardRate, 0f, 1f) * frametime;
        var punishRate = _landingTime[slot] + PsLandingGracePeriod < curtime ? frametime * PsDecrementRatio : 0f;

        if (onGround)
        {
            var speed = Math.Clamp(Length2D(velocity), 0f, SpeedNormal);

            var currentPreRatio = speed <= 0f
                ? 0f
                : MathF.Pow(_bonusSpeed[slot] / PsSpeedMax * SpeedNormal / speed, 1f / PsRatioToSpeed) * PsMaxPsTime;

            _leftPreRatio[slot]  = MathF.Min(_leftPreRatio[slot], currentPreRatio);
            _rightPreRatio[slot] = MathF.Min(_rightPreRatio[slot], currentPreRatio);

            _leftPreRatio[slot]  += averageRate > PsMinRewardRate  ? rewardRate : -punishRate;
            _rightPreRatio[slot] += averageRate < -PsMinRewardRate ? rewardRate : -punishRate;

            _leftPreRatio[slot]  = Math.Clamp(_leftPreRatio[slot],  0f, PsMaxPsTime);
            _rightPreRatio[slot] = Math.Clamp(_rightPreRatio[slot], 0f, PsMaxPsTime);

            _bonusSpeed[slot] = GetPrestrafeGain(slot) / SpeedNormal * speed;
        }
        else
        {
            var airReward = frametime;
            if (_leftPreRatio[slot] < _rightPreRatio[slot])
                _leftPreRatio[slot]  = Math.Clamp(_leftPreRatio[slot]  + airReward, 0f, _rightPreRatio[slot]);
            else
                _rightPreRatio[slot] = Math.Clamp(_rightPreRatio[slot] + airReward, 0f, _leftPreRatio[slot]);
        }
    }

    /// <summary>cs2kz KZClassicModeService::OnStopTouchGround — perf/bhop landing-speed preservation.</summary>
    private void ApplyPerf(IPlayerProcessMoveForwardParams arg, PlayerSlot slot, Vector takeoffVelocity)
    {
        var timeOnGround = _takeoffTime[slot] - _landingTime[slot];
        if (timeOnGround > BhPerfWindow) return;

        var landing    = _landingVelocity[slot];
        var landingLen = Length2D(landing);
        if (landingLen <= 0f) return;

        var nx = landing.X / landingLen;
        var ny = landing.Y / landingLen;

        var newSpeed = MathF.Max(landingLen, Length2D(takeoffVelocity));
        var floor    = SpeedNormal + GetPrestrafeGain(slot);

        if (newSpeed > floor)
        {
            newSpeed = MathF.Min(newSpeed, (BhBaseMultiplier - timeOnGround * BhLandingDecrement) * MathF.Log(newSpeed) - BhNormalizeFactor);
            newSpeed = MathF.Max(newSpeed, floor);
        }

        arg.Velocity = new Vector(newSpeed * nx, newSpeed * ny, takeoffVelocity.Z);
    }

    /// <summary>cs2kz KZClassicModeService::GetPrestrafeGain.</summary>
    private float GetPrestrafeGain(PlayerSlot slot)
        => PsSpeedMax * MathF.Pow(MathF.Max(_leftPreRatio[slot], _rightPreRatio[slot]) / PsMaxPsTime, PsRatioToSpeed);

    private HookReturnValue<float> OnGetMaxSpeed(IPlayerGetMaxSpeedHookParams @params, HookReturnValue<float> ret)
    {
        var client = @params.Client;
        if (client.IsFakeClient || !IsCkz(client.Slot))
            return new();

        return new(EHookAction.SkipCallReturnOverride, SpeedNormal + GetPrestrafeGain(client.Slot));
    }

    private void PushAngle(PlayerSlot slot, float rate, float duration)
    {
        var history = _angleHistory[slot];
        history.Add((rate, duration));

        // Trim the oldest samples once the window is full (keep at least the newest).
        var total = 0f;
        foreach (var (_, d) in history) total += d;
        while (history.Count > 1 && total - history[0].Duration >= PsTurnRateWindow)
        {
            total -= history[0].Duration;
            history.RemoveAt(0);
        }
    }

    private float AverageTurnRate(PlayerSlot slot)
    {
        float weighted = 0f, total = 0f;
        foreach (var (rate, duration) in _angleHistory[slot])
        {
            weighted += rate * duration;
            total    += duration;
        }

        return total == 0f ? 0f : weighted / total;
    }

    private bool IsCkz(PlayerSlot slot) => string.Equals(_mode.GetMode(slot), "ckz", StringComparison.OrdinalIgnoreCase);

    private static float Length2D(Vector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static float NormalizeYaw(float a)
    {
        while (a >  180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }
}
