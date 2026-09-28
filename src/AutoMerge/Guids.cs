// Guids.cs
// MUST match guids.h
using System;

namespace AutoMerge
{
	public static class GuidList
	{
		public const string guidAutoMergePkgString = "f05bac3e-6794-4a9e-9ee7-1b8a200778ee";
		public const string guidAutoMergeCmdSetString = "550e8690-9fae-46d1-8ff7-d6d0edf9449c";

		public static readonly Guid ShowAutoMergeCmdSet = new Guid(guidAutoMergeCmdSetString);

		// Comando "Merge from Task..." (stesso command set di ShowAutoMergeCommandId, vedi VSCommandTable.vsct).
		public const int ShowTaskMergeCommandId = 0x0101;

		// Tool window "Merge from Task" (scheda nel document well).
		public const string TaskMergeToolWindowString = "7C1E5D2A-4B8F-4E6A-9D3C-2F5B8A1E6C47";
		public static readonly Guid TaskMergeToolWindowGuid = new Guid(TaskMergeToolWindowString);

		// Tool window "Merge Policies" (regole di merge di team e personali, aperta da Merge from Task).
		public const string MergePolicyToolWindowString = "3480820D-5921-4204-8A87-94D453A0F8D4";
		public static readonly Guid MergePolicyToolWindowGuid = new Guid(MergePolicyToolWindowString);

		public const string AutoMergeNavigationItemId = "02A9D8B3-287B-4C55-83E7-7BFDB435546D";
		public const string AutoMergePageId = "3B582638-5F12-4715-8719-5E5777AB4581";
	    public static readonly Guid AutoMergePageGuid = new Guid(AutoMergePageId);
		public const string RecentChangesetsSectionId = "8DA59790-3996-465E-A13F-27D64B3C2A9D";

		public const string BranchesSectionId = "36BF6F52-F4AC-44A0-9985-817B2A65B3B0";
		public const string WorkItemMergeSectionId = "A2C6E4B8-3D7F-4A1E-9B5C-6F8D2A4E1C9B";
	};
}
