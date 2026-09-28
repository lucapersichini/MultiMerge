// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.

using MultiMerge.Base;
using Microsoft.TeamFoundation.Controls;

namespace MultiMerge
{
	[TeamExplorerPage(GuidList.MultiMergePageId)]
	public class MultiMergePage : TeamExplorerPageBase
	{

		/// <summary>
		/// Constructor.
		/// </summary>
		public MultiMergePage()
		{
			Title = Resources.MultiMergePageName;
		}
	}
}
