// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.ComponentModel.Composition;
using MultiMerge.Base;
using MultiMerge.VersionControl;
using Microsoft.TeamFoundation.Controls;
using Microsoft.VisualStudio.Shell;

namespace MultiMerge
{
	[TeamExplorerNavigationItem(GuidList.MultiMergeNavigationItemId, 210, TargetPageId = GuidList.MultiMergePageId)]
	public class MultiMergeNavigationItem : TeamExplorerNavigationItemBase
	{

		[ImportingConstructor]
		public MultiMergeNavigationItem(
			[Import(typeof(SVsServiceProvider))]
			IServiceProvider serviceProvider)
			: base(serviceProvider, GuidList.MultiMergePageId, VersionControlProvider.TeamFoundation)
		{
			Text = Resources.MultiMergePageName;
			Image = Resources.MergeImage;
		}
	}
}
