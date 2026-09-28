// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.TeamFoundation.WorkItemTracking.Client;

namespace MultiMerge
{
    public class WorkItemChangesetService
    {
        private const string ChangesetArtifactPrefix = "vstfs:///VersionControl/Changeset/";

        private readonly WorkItemStore _workItemStore;

        public WorkItemChangesetService(WorkItemStore workItemStore)
        {
            _workItemStore = workItemStore;
        }

        // Risolve il work item, legge Links, filtra i cast a ExternalLink,
        // fa parsing manuale di LinkedArtifactUri (NON LinkingUtilities: non esiste
        // in questo assembly v18.0), dedup + ordina crescente.
        public IReadOnlyList<int> GetLinkedChangesetIds(int workItemId)
        {
            var workItem = _workItemStore.GetWorkItem(workItemId);

            var changesetIds = new List<int>();
            foreach (Link link in workItem.Links)
            {
                var externalLink = link as ExternalLink;
                if (externalLink == null)
                    continue;

                var changesetId = TryParseChangesetId(externalLink.LinkedArtifactUri);
                if (changesetId.HasValue)
                    changesetIds.Add(changesetId.Value);
            }

            return changesetIds.Distinct().OrderBy(id => id).ToList();
        }

        private static int? TryParseChangesetId(string linkedArtifactUri)
        {
            if (string.IsNullOrEmpty(linkedArtifactUri))
                return null;
            if (!linkedArtifactUri.StartsWith(ChangesetArtifactPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var idPart = linkedArtifactUri.Substring(linkedArtifactUri.LastIndexOf('/') + 1);
            int changesetId;
            return int.TryParse(idPart, out changesetId) ? (int?)changesetId : null;
        }
    }
}
