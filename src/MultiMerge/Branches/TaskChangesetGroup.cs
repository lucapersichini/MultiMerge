// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.Generic;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    public class TaskChangesetGroup
    {
        public int WorkItemId { get; set; }
        public string SourceBranch { get; set; }   // es. "$/Proj/Main" — il branch radice associato
        public List<int> ChangesetIds { get; set; } // ordinati crescenti, tutti sullo stesso branch

        // Percorsi dei branch di destinazione scelti a mano dall'utente (es. "$/Proj/Release1").
        // Null/vuota finche' non viene impostata da WorkItemMergeViewModel.LoadTaskExecute dopo il
        // parsing di TargetBranchesText; gli stessi target si applicano a ogni gruppo del task.
        public IReadOnlyList<string> TargetBranchPaths { get; set; }

        // Cache di breve durata: il Change[] per changeset viene richiesto al server TFS una sola
        // volta (in BranchesViewModel.GetBranchesForTask, quando il pannello Branches viene calcolato)
        // e riusato in MergeExecuteInternalForTask (quando l'utente clicca Merge), invece di richiederlo
        // di nuovo — evita di raddoppiare i round-trip di rete per un task con molti changeset.
        public Dictionary<int, Change[]> ChangesByChangesetId { get; set; }
    }
}
