using Sharp.Shared.GameEntities;
using Kreedz.Shared.Models.Zone;

namespace Kreedz.Shared.Interfaces.Listeners;

public interface IZoneModuleListener
{
    void OnZoneStartTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
    }

    void OnZoneEndTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
    }

    void OnZoneTrigger(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
    }
}
