// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using MultiMerge.Base;
using Microsoft.TeamFoundation.Controls;

namespace MultiMerge
{
	[TeamExplorerSection(GuidList.RecentChangesetsSectionId, GuidList.MultiMergePageId, 10)]
	public class RecentChangesetsSection : TeamExplorerSectionBase
	{
		protected override ITeamExplorerSection CreateViewModel(SectionInitializeEventArgs e)
		{
			var viewModel = base.CreateViewModel(e) ?? new RecentChangesetsViewModel(new VsLogger(ServiceProvider));

			return viewModel;
		}

		protected override object CreateView(SectionInitializeEventArgs e)
		{
            return new RecentChangesetsView();
		}
	}
}
