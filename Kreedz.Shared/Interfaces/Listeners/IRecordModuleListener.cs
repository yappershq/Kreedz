using Kreedz.Shared.Events;

namespace Kreedz.Shared.Interfaces.Listeners;

public interface IRecordModuleListener
{
    void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
    }

    void OnMapRecordsLoaded()
    {
    }
}
