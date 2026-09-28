// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
namespace MultiMerge
{
	public enum BranchValidationResult
	{
		Undefined,

		Success,
		
		BranchNotMapped,

		ItemHasLocalChanges,

		AlreadyMerged,

		NoAccess,

		// Flusso Task: il target ha ancora conflitti aperti dall'ultimo stop della catena.
		HasUnresolvedConflicts
	}
}