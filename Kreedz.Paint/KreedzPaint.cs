/*
 * yappershq/Kreedz (KZ) — Paint plugin (cs2kz src/kz/paint), standalone third-party module.
 *
 * !paint marks every takeoff spot so you can read your bhop lines: green = perf, red = non-perf.
 * Primary path = REAL engine decals exactly like cs2kz — UTIL_DecalTrace (sig via kreedz-paint
 * gamedata) at a downward ground trace with the tier0 _MakeGlobalSymbol("paint") symbol, and the
 * resulting GE_PlaceDecalEvent recolored (ABGR) in a PostEventAbstract hook while our pendingPaint
 * flag is set. If either native fails to resolve on a new build, falls back automatically to a
 * reused env_beam X-mark ring. Preference-persisted ("paint") via Core's IKzPreferences.
 *
 * Depends only on ISharedSystem + optional Core interfaces (IKzCommands, IKzPreferences) and the
 * LocalizerManager — installable/removable independently of Core (degrades: no command/persistence).
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Paint;

public sealed unsafe class KreedzPaint : IModSharpModule
{
    public string DisplayName   => "[Kreedz] Paint";
    public string DisplayAuthor => "yappershq";

    private const int   MarkCount = 48; // fallback marks per player; 2 beams each
    private const float MarkSize  = 6f;
    private const int   PerfTicks = 2;  // ground ticks <= this at takeoff = perf (jumpstats convention)

    private readonly ISharedSystem        _shared;
    private readonly IModSharp            _modSharp;
    private readonly IHookManager         _hookManager;
    private readonly IEntityManager       _entityManager;
    private readonly IClientManager       _clientManager;
    private readonly IPhysicsQueryManager _physics;
    private readonly ILogger<KreedzPaint> _logger;

    private ILocalizerManager? _localizer;
    private IKzPreferences?    _prefs;

    private readonly bool[]                _enabled     = new bool[PlayerSlot.MaxPlayerCount];
    private readonly IBaseModelEntity?[][] _marks       = new IBaseModelEntity?[PlayerSlot.MaxPlayerCount][];
    private readonly int[]                 _head        = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[]                _wasGround   = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]                 _groundTicks = new int[PlayerSlot.MaxPlayerCount];

    // ─── Real-decal path (cs2kz UTIL_DecalTrace + PlaceDecalEvent recolor) ───
    private static delegate* unmanaged<void*, ulong*, float, void> _fnDecalTrace;
    private static ulong _paintSymbol;
    private bool _decalsAvailable;
    private int  _pendingPaintSlot = -1;
    private bool _pendingPaintPerf;

    public KreedzPaint(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _modSharp      = shared.GetModSharp();
        _hookManager   = shared.GetHookManager();
        _entityManager = shared.GetEntityManager();
        _clientManager = shared.GetClientManager();
        _physics       = shared.GetPhysicsQueryManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzPaint>();
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        InitDecalPath();
        return true;
    }

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();

        _localizer = mgr.GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;
        _localizer?.LoadLocaleFile("kreedz-paint.json");

        _prefs = mgr.GetOptionalSharpModuleInterface<IKzPreferences>(IKzPreferences.Identity)?.Instance;
        if (_prefs is { } prefs)
            prefs.Loaded += slot => _enabled[slot] = prefs.Get(slot, "paint") == "1";

        var commands = mgr.GetOptionalSharpModuleInterface<IKzCommands>(IKzCommands.Identity)?.Instance;
        if (commands is null)
        {
            _logger.LogWarning("[KZ.Paint] IKzCommands not published (Kreedz.Core missing?) — !paint unavailable");
            return;
        }

        commands.AddClientChatCommand("paint", (slot, cmd) =>
        {
            _enabled[slot] = !_enabled[slot];
            _prefs?.Set(slot, "paint", _enabled[slot] ? "1" : "0");
            if (!_enabled[slot])
                KillMarks(slot);
            Msg(slot, _enabled[slot] ? "Kreedz_Paint_On" : "Kreedz_Paint_Off");
            return ECommandAction.Handled;
        });
    }

    public void Shutdown()
    {
        _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        if (_decalsAvailable)
            _hookManager.PostEventAbstract.RemoveHookPre(OnPlaceDecal);
    }

    private void InitDecalPath()
    {
        try
        {
            var gameData = _modSharp.GetGameData();
            gameData.Register("kreedz-paint.games");

            _fnDecalTrace = (delegate* unmanaged<void*, ulong*, float, void>) gameData.GetAddress("DecalTrace");
            _logger.LogInformation("[KZ.Paint] DecalTrace resolved: {Ok}", _fnDecalTrace != null);

            var makeSymbol = (delegate* unmanaged<byte*, ulong>) gameData.GetAddress("_MakeGlobalSymbol");
            _logger.LogInformation("[KZ.Paint] _MakeGlobalSymbol resolved: {Ok}", makeSymbol != null);

            if (makeSymbol != null)
            {
                var name = "paint\0"u8;
                fixed (byte* p = name)
                {
                    _paintSymbol = makeSymbol(p);
                }
            }

            _decalsAvailable = _fnDecalTrace != null && _paintSymbol != 0;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[KZ.Paint] decal natives unavailable — falling back to beam marks");
            _decalsAvailable = false;
        }

        if (_decalsAvailable)
        {
            _modSharp.HookNetMessage(ProtobufNetMessageType.GE_PlaceDecalEvent);
            _hookManager.PostEventAbstract.InstallHookPre(OnPlaceDecal);
            _logger.LogInformation("[KZ.Paint] real-decal path active (DecalTrace + _MakeGlobalSymbol resolved)");
        }
        else
        {
            _logger.LogWarning("[KZ.Paint] real-decal path unavailable — using beam X-marks");
        }
    }

    // cs2kz OnPostEvent GE_PlaceDecalEvent: while our own DecalTrace call is on the stack, restyle
    // the event (game expects ABGR; green = perf, red = non-perf). Foreign decals pass untouched.
    private HookReturnValue<NetworkReceiver> OnPlaceDecal(IPostEventAbstractHookParams param, HookReturnValue<NetworkReceiver> ret)
    {
        if (param.MsgId != ProtobufNetMessageType.GE_PlaceDecalEvent || _pendingPaintSlot < 0)
            return ret;

        param.Data.SetUInt32("color", _pendingPaintPerf ? 0xFF00FF00u : 0xFF2828FFu);
        return ret;
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (_enabled[slot] && !onGround && _wasGround[slot] && arg.Pawn.ActualMoveType == MoveType.Walk)
        {
            var perf = _groundTicks[slot] <= PerfTicks;
            if (!_decalsAvailable || !TryPlaceDecal(arg.Pawn, arg.Pawn.GetAbsOrigin(), perf, slot))
                PlaceMark(slot, arg.Pawn.GetAbsOrigin(), perf);
        }

        _groundTicks[slot] = onGround ? _groundTicks[slot] + 1 : 0;
        _wasGround[slot]   = onGround;
    }

    private bool TryPlaceDecal(IPlayerPawn pawn, Vector origin, bool perf, PlayerSlot slot)
    {
        // Ground trace under the takeoff spot — DecalTrace needs a real hit trace to project onto.
        var col  = pawn.GetCollisionProperty();
        var attr = RnQueryShapeAttr.PlayerMovement(col?.CollisionAttribute.InteractsWith ?? default);
        attr.SetEntityToIgnore(pawn, 0);

        var start = new Vector(origin.X, origin.Y, origin.Z + 4f);
        var end   = new Vector(origin.X, origin.Y, origin.Z - 24f);
        var tr    = _physics.TraceShape(new TraceShapeRay(new TraceShapeLine()), start, end, attr);

        if (!tr.DidHit())
            return false;

        // The managed GameTrace mirrors the native CGameTrace layout (sizeof == 192 per the ModSharp
        // engine header); copy it into a zeroed native-sized buffer for the engine call.
        var buf = stackalloc byte[192];
        Unsafe.InitBlock(buf, 0, 192);
        Unsafe.CopyBlock(buf, &tr, 185);

        var symbol = _paintSymbol;
        _pendingPaintSlot = slot.AsPrimitive();
        _pendingPaintPerf = perf;
        try
        {
            _fnDecalTrace(buf, &symbol, 0f);
        }
        finally
        {
            _pendingPaintSlot = -1;
        }

        return true;
    }

    // ─── Fallback: reused env_beam X-mark ring ───

    private void PlaceMark(PlayerSlot slot, Vector origin, bool perf)
    {
        var ring = _marks[slot] ??= new IBaseModelEntity?[MarkCount * 2];
        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", perf ? "0 255 0" : "255 40 40" },
            { "BoltWidth", "1" },
        };

        var z = origin.Z + 1f;
        Span<(Vector A, Vector B)> legs =
        [
            (new Vector(origin.X - MarkSize, origin.Y - MarkSize, z), new Vector(origin.X + MarkSize, origin.Y + MarkSize, z)),
            (new Vector(origin.X - MarkSize, origin.Y + MarkSize, z), new Vector(origin.X + MarkSize, origin.Y - MarkSize, z)),
        ];

        foreach (var (a, b) in legs)
        {
            var i = _head[slot];
            _head[slot] = (i + 1) % ring.Length;

            var beam = ring[i];
            if (beam is not { IsValidEntity: true })
            {
                beam = _entityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv);
                if (beam is not { IsValidEntity: true })
                    continue;
                ring[i] = beam;
            }
            else
            {
                beam.RenderColor = perf ? new Color32(0, 255, 0, 255) : new Color32(255, 40, 40, 255);
            }

            beam.SetAbsOrigin(a);
            beam.SetNetVar("m_vecEndPos", b);
        }
    }

    private void KillMarks(PlayerSlot slot)
    {
        if (_marks[slot] is not { } ring)
            return;

        foreach (var m in ring)
            if (m is { IsValidEntity: true })
                m.Kill();

        Array.Clear(ring);
        _head[slot] = 0;
    }

    private void Msg(PlayerSlot slot, string key)
    {
        if (_localizer is { } lm && _clientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            lm.For(client).Localized(key).Prefix(null).Transform(ProcessColors).Print(HudPrintChannel.Chat);
    }

    private static string ProcessColors(string s)
        => s.Replace("{green}", "\x04").Replace("{lime}", "\x06").Replace("{red}", "\x02").Replace("{default}", "\x01");
}
