// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.ObjectModel;

namespace MultiMerge
{
    public class RecentChangesetsViewModelContext
    {
        public ChangesetViewModel SelectedChangeset { get; set; }

        public ObservableCollection<ChangesetViewModel> Changesets { get; set; }

        public bool ShowAddByIdChangeset { get; set; }

        public string ChangesetIdsText { get; set; }

        public string Title { get; set; }
    }
}
