// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Windows.Controls;
using Microsoft.TeamFoundation.Controls.MVVM;

namespace MultiMerge
{
	/// <summary>
	/// Interaction logic for RecentChangesetsView.xaml
	/// </summary>
	public partial class RecentChangesetsView : UserControl, IFocusService
	{
		public RecentChangesetsView()
		{
			InitializeComponent();
		}

		public void SetFocus(string id, params object[] args)
		{
			switch (id)
			{
				case RecentChangesetFocusableControlNames.AddChangesetByIdLink:
					addChangesetByIdLink.Focus();
					break;
				case RecentChangesetFocusableControlNames.ChangesetIdTextBox:
					changesetIdTextBox.FocusTextBox();
					changesetIdTextBox.TextBoxControl.SelectionStart = changesetIdTextBox.TextBoxControl.Text.Length;
					break;
				case RecentChangesetFocusableControlNames.ChangesetList:
					if (changesetList.SelectedItem != null)
					{
						changesetList.UpdateLayout();
						var item = changesetList.ItemContainerGenerator.ContainerFromIndex(changesetList.SelectedIndex);
						((ListBoxItem) item).Focus();
					}
					else
					{
						changesetList.Focus();
					}
					break;
			}
		}
	}
}
