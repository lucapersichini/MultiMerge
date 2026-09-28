// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
	public class FileMergeInfo
	{
		public string SourceFile { get; set; }

		public string TargetFile { get; set; }

		public ChangesetVersionSpec ChangesetVersionSpec { get; set; }
	}
}