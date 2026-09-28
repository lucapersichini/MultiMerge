// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.ObjectModel;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    public class BranchesViewModelContext
    {
        public ObservableCollection<MergeInfoViewModel> Branches { get; set; }

        public MergeInfoViewModel SelectedBranch { get; set; }

        public MergeOption MergeOption { get; set; }

        public string ErrorMessage { get; set; }

        public Workspace Workspace { get; set; }

        public ObservableCollection<Workspace> Workspaces { get; set; }

        public bool ShowWorkspaceChooser { get; set; }

        public MergeMode MergeMode { get; set; }

        public ObservableCollection<MergeMode> MergeModes { get; set; }

        public ChangesetViewModel Changeset { get; set; }
    }
}
