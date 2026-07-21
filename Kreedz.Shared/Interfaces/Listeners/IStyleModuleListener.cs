using System.Collections.Generic;
using Sharp.Shared.Units;
using Kreedz.Shared.Models.Style;

namespace Kreedz.Shared.Interfaces.Listeners;

public interface IStyleModuleListener
{
    void OnStyleConfigLoaded(IReadOnlyList<StyleSetting> styles)
    {
    }

    void OnClientStyleChanged(PlayerSlot slot, int oldStyle, int newStyle)
    {
    }
}
