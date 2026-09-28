namespace AutoMerge
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