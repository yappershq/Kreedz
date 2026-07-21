using Kreedz.Shared.Interfaces;

namespace Kreedz.Shared.Models.Replay;

public readonly record struct ReplaySaveContext
{
    public ulong          SteamId       { get; init; }
    public float          FinishTime    { get; init; }
    public EAttemptResult AttemptResult { get; init; }
}
