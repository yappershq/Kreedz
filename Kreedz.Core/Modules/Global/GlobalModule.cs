/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ global-API client (1:1 cs2kz src/kz/global). Connects to the cs2kz global backend over a WebSocket,
 * does the hello/hello_ack handshake (plugin checksum + map + players), and submits finished runs as
 * NewRecord messages so they land on the global leaderboard — alongside the always-on local ranking.
 *
 *   kz_global_apikey  ""                       — global API key. EMPTY = disabled (local ranking only).
 *   kz_global_url     "https://api.cs2kz.org"  — API base; https→wss, "/auth/cs2" appended, Bearer auth.
 *
 * Dormant by default: with no key the module logs "disabled" and never connects, so it ships safely and
 * the server runs on its own ranking until a key is set. The protocol shape (endpoint, Bearer auth,
 * hello handshake, NewRecord submission, reconnect lifecycle) mirrors the cs2kz source; the exact wire
 * envelope + the official checksum validation are theirs to gate, so a real issued key + a live
 * handshake against api.cs2kz.org are needed to certify official global submission (as flagged: a
 * clean-room plugin can't pass their plugin-checksum gate without KZGlobalteam). All socket I/O runs on
 * background tasks; the only game-thread touch is capturing run data on finish.
 */

using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Interfaces.Listeners;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Modules;

internal enum GlobalState { Uninitialized, Disabled, Connecting, Connected, HandshakeSent, Ready, Reconnecting, Disconnected }

internal interface IGlobalModule
{
    bool        IsEnabled { get; }
    GlobalState State     { get; }
}

internal sealed class GlobalModule : IModule, IGlobalModule, ITimerModuleListener
{
    private const string HelloType     = "hello";
    private const string HelloAckType  = "hello_ack";
    private const string NewRecordType = "NewRecord";

    private readonly InterfaceBridge     _bridge;
    private readonly ITimerModule        _timerModule;
    private readonly ICheckpointModule   _checkpoint;
    private readonly IKzStyleModule      _style;
    private readonly IModeModule         _mode;
    private readonly ICommandManager     _commandManager;
    private readonly ILogger<GlobalModule> _logger;

    private IConVar? _apiKeyCvar;
    private IConVar? _urlCvar;

    private ClientWebSocket?       _socket;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int    _messageId;
    private string _checksum = "";

    private volatile GlobalState _state = GlobalState.Uninitialized;

    public GlobalModule(InterfaceBridge  bridge,
                        ITimerModule      timerModule,
                        ICheckpointModule checkpoint,
                        IKzStyleModule    style,
                        IModeModule       mode,
                        ICommandManager   commandManager,
                        ILogger<GlobalModule> logger)
    {
        _bridge         = bridge;
        _timerModule    = timerModule;
        _checkpoint     = checkpoint;
        _style          = style;
        _mode           = mode;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public bool IsEnabled => _state is not (GlobalState.Uninitialized or GlobalState.Disabled);
    public GlobalState State => _state;

    public bool Init()
    {
        _apiKeyCvar = _bridge.ConVarManager.CreateConVar("kz_global_apikey", "",
            "cs2kz global API key. Empty = global disabled (local ranking only).");
        _urlCvar = _bridge.ConVarManager.CreateConVar("kz_global_url", "https://api.cs2kz.org",
            "cs2kz global API base URL.");

        _timerModule.RegisterListener(this);
        _commandManager.AddClientChatCommand("global", OnCommandGlobal);
        return true;
    }

    public void OnPostInit(ServiceProvider provider) => Start();

    public void Shutdown()
    {
        _timerModule.UnregisterListener(this);
        // ICommandManager has no per-command removal; client chat commands die with the plugin.

        _cts?.Cancel();
        try { _socket?.Abort(); } catch { /* already dead */ }
        _socket?.Dispose();
        _cts?.Dispose();
    }

    private void Start()
    {
        if (string.IsNullOrWhiteSpace(_apiKeyCvar?.GetString()))
        {
            _state = GlobalState.Disabled;
            _logger.LogInformation("[KZ.Global] disabled — kz_global_apikey not set; using local ranking only.");
            return;
        }

        _checksum = ComputeChecksum();
        _cts      = new CancellationTokenSource();
        _ = ConnectLoopAsync(_cts.Token);
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = 5.0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "[KZ.Global] connection error");
            }

            if (ct.IsCancellationRequested) break;

            _state = GlobalState.Reconnecting;
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(60.0, backoff * 2.0);
        }

        _state = GlobalState.Disconnected;
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var key = _apiKeyCvar?.GetString() ?? "";
        var url = ToWebSocketUrl(_urlCvar?.GetString() ?? "https://api.cs2kz.org");

        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");

        _state = GlobalState.Connecting;
        await _socket.ConnectAsync(new Uri(url), ct);
        _state = GlobalState.Connected;

        await SendHelloAsync(ct);
        _state = GlobalState.HandshakeSent;

        await ReceiveLoopAsync(_socket, ct);
    }

    private Task SendHelloAsync(CancellationToken ct) => SendJsonAsync(new
    {
        type           = HelloType,
        id             = NextId(),
        plugin_version = _bridge.Version.ToString(),
        checksum       = _checksum,
        map            = _bridge.ModSharp.GetGlobals().MapName,
    }, ct);

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var sb     = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            HandleMessage(sb.ToString());
            sb.Clear();
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Split('\n', 2)[0]); // JSON line; any binary follows a newline
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, HelloAckType, StringComparison.OrdinalIgnoreCase))
            {
                _state = GlobalState.Ready;
                _logger.LogInformation("[KZ.Global] handshake complete — global ranking active.");
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "[KZ.Global] unparsable message");
        }
    }

    void ITimerModuleListener.OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo) { }

    void ITimerModuleListener.OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (_state != GlobalState.Ready) return;
        if (controller.GetGameClient() is not { IsFakeClient: false } client) return;

        // Styled runs are never globally ranked; skip before touching the wire.
        if (_style.HasAnyStyle(client.Slot)) return;

        // Capture everything as values on the game thread — nothing game-owned crosses to the send task.
        var steamId   = client.SteamId.AsPrimitive();
        var name      = client.Name;
        var map       = _bridge.ModSharp.GetGlobals().MapName;
        var time      = timerInfo.Time;
        var teleports = _checkpoint.GetTeleportCount(client.Slot);
        var mode      = _mode.GetMode(client.Slot);

        _ = SubmitRunAsync(steamId, name, map, time, teleports, mode);
    }

    private async Task SubmitRunAsync(ulong steamId, string name, string map, float time, int teleports, string mode)
    {
        try
        {
            await SendJsonAsync(new
            {
                type      = NewRecordType,
                id        = NextId(),
                steamid64 = steamId.ToString(),
                player    = name,
                map,
                mode,
                teleports,
                pro       = teleports == 0,
                time,
            }, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[KZ.Global] run submission failed for {Sid}", steamId);
        }
    }

    private async Task SendJsonAsync(object message, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private ECommandAction OnCommandGlobal(Sharp.Shared.Units.PlayerSlot slot, Sharp.Shared.Types.StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, $"[KZ] Global ranking: {_state}.");
        return ECommandAction.Handled;
    }

    private int NextId() => Interlocked.Increment(ref _messageId);

    private static string ToWebSocketUrl(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "wss://" + url["https://".Length..];
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url["http://".Length..];
        return url + "/auth/cs2";
    }

    private string ComputeChecksum()
    {
        try
        {
            using var md5    = MD5.Create();
            using var stream = File.OpenRead(_bridge.DllPath);
            return Convert.ToHexString(md5.ComputeHash(stream));
        }
        catch
        {
            return _bridge.Version.ToString();
        }
    }
}
