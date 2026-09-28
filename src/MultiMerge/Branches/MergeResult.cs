// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
namespace MultiMerge
{
    public enum MergeResult
    {
        CheckIn,
        Merged,
        NothingMerge,
        CheckInFail,
        CheckInEvaluateFail,
        HasConflicts,
        CanNotGetLatest,
        HasLocalChanges,
        UnexpectedFileRestored,
        // workspace.Merge ha riportato failures (item non messi in sospeso: bloccati da un altro
        // utente, pending change incompatibile, cloak...). Flusso Task: la catena si ferma li'.
        MergeFailed,
    }
}
