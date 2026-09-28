// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using MultiMerge.Base;
using Microsoft.TeamFoundation.Controls;

namespace MultiMerge
{
    [TeamExplorerSection(GuidList.BranchesSectionId, GuidList.MultiMergePageId, 20)]
    public class BranchesSection : TeamExplorerSectionBase
    {
        protected override object CreateView(SectionInitializeEventArgs e)
        {
            return new BranchesView();
        }

        protected override ITeamExplorerSection CreateViewModel(SectionInitializeEventArgs e)
        {
            var viewModel = base.CreateViewModel(e) ?? new BranchesViewModel(new VsLogger(ServiceProvider));

            return viewModel;
        }
    }
}
