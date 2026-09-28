// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using Microsoft.TeamFoundation.Controls;
using Microsoft.TeamFoundation.MVVM;
using TfsTeamExplorerSectionBase = Microsoft.TeamFoundation.Controls.WPF.TeamExplorer.TeamExplorerSectionBase;

namespace MultiMerge.Base
{
	public abstract class TeamExplorerSectionBase : TfsTeamExplorerSectionBase
	{
		public override void Initialize(object sender, SectionInitializeEventArgs e)
		{
			base.Initialize(sender, e);
			var viewModelBase = ViewModel as ViewModelBase;
			if (View != null && viewModelBase != null)
			{
				viewModelBase.RegisterService(View);
			}
		}
	}
}
