// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using MultiMerge.VersionControl;
using Microsoft.TeamFoundation.Controls.WPF.TeamExplorer;

using TfsTeamExplorerNavigationItemBase = Microsoft.TeamFoundation.Controls.WPF.TeamExplorer.TeamExplorerNavigationItemBase;

namespace MultiMerge.Base
{
	public abstract class TeamExplorerNavigationItemBase : TfsTeamExplorerNavigationItemBase
    {
		private readonly VersionControlProvider _versionControlProvider;
		private Guid _pageId;

		protected IServiceProvider ServiceProvider { get; private set; }

		protected TeamExplorerNavigationItemBase(IServiceProvider serviceProvider,
			string pageId, VersionControlProvider versionControlProvider)
		{
			_versionControlProvider = versionControlProvider;
			ServiceProvider = serviceProvider;
			_pageId = new Guid(pageId);
		}

		public override void Execute()
		{
			TeamExplorerUtils.Instance.NavigateToPage(_pageId.ToString(), ServiceProvider, null);
		}

		public override void Invalidate()
		{
			base.Invalidate();
			IsVisible = CalculateVisible();
		}

		private bool CalculateVisible()
		{
			return VersionControlNavigationHelper.IsProviderActive(ServiceProvider, _versionControlProvider)
			&& VersionControlNavigationHelper.IsConnectedToTfsCollectionAndProject(ServiceProvider);
		}
	}
}
