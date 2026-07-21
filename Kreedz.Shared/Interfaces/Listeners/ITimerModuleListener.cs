using Sharp.Shared.GameEntities;
using Kreedz.Shared.Models.Timer;

namespace Kreedz.Shared.Interfaces.Listeners;

public interface ITimerModuleListener
{
    /// <summary>
    /// Called before the timer starts. Return <c>false</c> to prevent the timer from starting.
    /// </summary>
    bool CanStartTimer(IPlayerController controller, IPlayerPawn pawn) => true;

    void OnPlayerTimerStart(IPlayerController controller,
                            IPlayerPawn       pawn,
                            ITimerInfo        timerInfo)
    {
    }

    void OnPlayerFinishMap(IPlayerController controller,
                           IPlayerPawn       pawn,
                           ITimerInfo        timerInfo)
    {
    }

    void OnPlayerStageTimerStart(IPlayerController controller,
                                 IPlayerPawn       pawn,
                                 IStageTimerInfo   stageTimerInfo)
    {
    }

    void OnPlayerStageTimerFinish(IPlayerController controller,
                                  IPlayerPawn       pawn,
                                  IStageTimerInfo   stageTimerInfo)
    {
    }

    void OnReachCheckpoint(IPlayerController controller,
                           IPlayerPawn       pawn,
                           ITimerInfo        timerInfo,
                           int               checkpoint)
    {
    }
}
