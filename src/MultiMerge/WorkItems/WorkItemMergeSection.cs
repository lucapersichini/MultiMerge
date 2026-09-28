// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using MultiMerge.Base;
using Microsoft.TeamFoundation.Controls;

namespace MultiMerge
{
    [TeamExplorerSection(GuidList.WorkItemMergeSectionId, GuidList.MultiMergePageId, 5)]
    public class WorkItemMergeSection : TeamExplorerSectionBase
    {
        protected override ITeamExplorerSection CreateViewModel(SectionInitializeEventArgs e)
        {
            return base.CreateViewModel(e) ?? new WorkItemMergeViewModel(new VsLogger(ServiceProvider));
        }

        protected override object CreateView(SectionInitializeEventArgs e)
        {
            return new WorkItemMergeView();
        }
    }
}
