// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.Generic;

namespace MultiMerge
{
	public class ChangesetViewModel
	{
		public int ChangesetId { get; set; }

		public string Comment { get; set; }

		public List<string> Branches { get; set; }

		public string DisplayBranchName
		{
			get { return BranchHelper.GetDisplayBranchName(Branches); }
		}
	}
}