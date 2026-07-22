using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// Chat-command registration surface published by Core for satellite plugins (Paint, RankTitles, …) —
/// Core internally routes to the external CommandCenter provider or its built-in fallback, so
/// satellites never care which is installed.
/// </summary>
public interface IKzCommands
{
    static readonly string Identity = typeof(IKzCommands).FullName!;

    delegate ECommandAction Handler(PlayerSlot slot, StringCommand command);

    void AddClientChatCommand(string command, Handler handler);
}
