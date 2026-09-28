// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
// Guids.cs
// MUST match guids.h
using System;

namespace MultiMerge
{
	public static class GuidList
	{
		public const string guidMultiMergePkgString = "c7d9470c-00e2-48d4-8a71-12036e43005f";
		public const string guidMultiMergeCmdSetString = "3e489283-3885-49d6-92ac-1c118ebfdced";

		public static readonly Guid ShowMultiMergeCmdSet = new Guid(guidMultiMergeCmdSetString);

		// Comando "Merge from Task..." (stesso command set di ShowMultiMergeCommandId, vedi VSCommandTable.vsct).
		public const int ShowTaskMergeCommandId = 0x0101;

		// Tool window "Merge from Task" (scheda nel document well).
		public const string TaskMergeToolWindowString = "069cbe65-0a53-4eff-a0ec-a9f0b8c9325e";
		public static readonly Guid TaskMergeToolWindowGuid = new Guid(TaskMergeToolWindowString);

		// Tool window "Merge Policies" (regole di merge di team e personali, aperta da Merge from Task).
		public const string MergePolicyToolWindowString = "b1766d6f-015d-42d0-bfb3-9994fd0457ac";
		public static readonly Guid MergePolicyToolWindowGuid = new Guid(MergePolicyToolWindowString);

		public const string MultiMergeNavigationItemId = "c1934786-fd1c-4b80-b990-da6e81dfcaa4";
		public const string MultiMergePageId = "832427bd-e2f8-48d2-ba85-32cc649aed99";
	    public static readonly Guid MultiMergePageGuid = new Guid(MultiMergePageId);
		public const string RecentChangesetsSectionId = "3ba6a6ac-415e-4dcc-b040-086899916628";

		public const string BranchesSectionId = "c2bece38-5ff8-4721-a556-ac76d995e22f";
		public const string WorkItemMergeSectionId = "a93ad5e6-a941-4102-8d30-e31899ff7dbe";
	};
}
