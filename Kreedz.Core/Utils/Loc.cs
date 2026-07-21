using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace Kreedz;

/// <summary>
/// Thin localization helper over <see cref="ILocalizerManager"/> — the KZ port's single path for
/// user-facing text. A missing LocalizerManager degrades to a silent no-op (text just doesn't print),
/// so modules can localize unconditionally. Locale files are `.assets/locales/kreedz*.json`, key-first
/// per-culture with `{{double-brace}}` colors (see the ModSharp locale schema).
/// </summary>
internal static class Loc
{
    /// <summary>Localized chat line to one client.</summary>
    public static void Chat(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm?.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);

    /// <summary>Localized string for one client (no print) — for HUD/menu text.</summary>
    public static string Text(ILocalizerManager? lm, IGameClient client, string key, params object?[] args)
        => lm is null ? string.Empty : lm.For(client).Localized(key, args).Prefix(null).Build();

    /// <summary>Localized chat line to every in-game human.</summary>
    public static void ChatAll(ILocalizerManager? lm, IClientManager clients, string key, params object?[] args)
    {
        if (lm is null) return;

        foreach (var client in clients.GetGameClients(inGame: true))
        {
            if (client.IsFakeClient) continue;

            lm.For(client).Localized(key, args).Prefix(null)
              .Transform(ChatFormat.ProcessColorCodes).Print(HudPrintChannel.Chat);
        }
    }
}
