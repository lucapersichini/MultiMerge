using System;
using System.Collections.Generic;
using System.Linq;
// StringComparer.OrdinalIgnoreCase: coerente col resto del progetto (BranchesViewModel.FindShareFolder/
// ExtractParentFolder, MergeInfoViewModel.IsSourceBranch, WorkItemChangesetService) perché il casing dei
// path TFS non è garantito coerente tra query diverse.
using Microsoft.TeamFoundation.VersionControl.Client;

namespace AutoMerge
{
    public class TaskChangesetGroupingResult
    {
        public List<TaskChangesetGroup> Groups { get; set; }
        public List<int> UnresolvedChangesetIds { get; set; }
    }

    public class TaskChangesetGroupingService
    {
        private readonly ChangesetService _changesetService;

        public TaskChangesetGroupingService(ChangesetService changesetService)
        {
            _changesetService = changesetService;
        }

        // orderedChangesetIds: già ordinati/deduplicati (output di WorkItemChangesetService).
        // Per ogni changeset determina il branch radice associato (GetAssociatedBranches, esistente
        // su ChangesetService); un changeset con 0 o >1 branch associato è "irrisolvibile" e finisce
        // in UnresolvedChangesetIds invece di bloccare l'intero task. Un changeset non più
        // recuperabile (eccezione TFS, es. cancellato) è trattato allo stesso modo: unresolved.
        public TaskChangesetGroupingResult GroupByBranch(int workItemId, IReadOnlyList<int> orderedChangesetIds)
        {
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<int>();

            foreach (var changesetId in orderedChangesetIds)
            {
                List<ItemIdentifier> branches;
                try
                {
                    branches = _changesetService.GetAssociatedBranches(changesetId);
                }
                catch (Exception)
                {
                    unresolved.Add(changesetId);
                    continue;
                }

                if (branches == null || branches.Count != 1)
                {
                    unresolved.Add(changesetId);
                    continue;
                }

                var branch = branches[0].Item;
                List<int> list;
                if (!groups.TryGetValue(branch, out list))
                {
                    list = new List<int>();
                    groups[branch] = list;
                }
                list.Add(changesetId);
            }

            var resultGroups = groups.Select(kvp => new TaskChangesetGroup
            {
                WorkItemId = workItemId,
                SourceBranch = kvp.Key,
                ChangesetIds = kvp.Value.OrderBy(id => id).ToList()
            }).ToList();

            return new TaskChangesetGroupingResult
            {
                Groups = resultGroups,
                UnresolvedChangesetIds = unresolved
            };
        }
    }
}
