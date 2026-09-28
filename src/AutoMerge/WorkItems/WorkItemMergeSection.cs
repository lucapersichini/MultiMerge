using AutoMerge.Base;
using Microsoft.TeamFoundation.Controls;

namespace AutoMerge
{
    [TeamExplorerSection(GuidList.WorkItemMergeSectionId, GuidList.AutoMergePageId, 5)]
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
