// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;

namespace MultiMerge
{
    public static class ChangesetBatchCalculator
    {
        // orderedChangesetIds: ID dei changeset del task appartenenti a UN SOLO branch/path
        // sorgente, già ordinati crescenti e deduplicati.
        // hasThirdPartyChangesetBetween(fromId, toId): true se, guardando SOLO la storia del
        // path sorgente di questo gruppo, esiste un changeset che NON è fromId né toId tra i due
        // (in genere implementato con QueryHistory, vedi BranchesViewModel.HasThirdPartyChangesetBetween).
        public static List<ChangesetBatch> CalculateBatches(
            IReadOnlyList<int> orderedChangesetIds,
            Func<int, int, bool> hasThirdPartyChangesetBetween)
        {
            if (orderedChangesetIds == null)
                throw new ArgumentNullException("orderedChangesetIds");
            if (hasThirdPartyChangesetBetween == null)
                throw new ArgumentNullException("hasThirdPartyChangesetBetween");

            var result = new List<ChangesetBatch>();
            if (orderedChangesetIds.Count == 0)
                return result;

            var batchStart = orderedChangesetIds[0];
            var batchEnd = orderedChangesetIds[0];

            for (var i = 1; i < orderedChangesetIds.Count; i++)
            {
                var candidate = orderedChangesetIds[i];
                if (hasThirdPartyChangesetBetween(batchEnd, candidate))
                {
                    // chiudi il batch corrente, aprine uno nuovo a partire da candidate
                    result.Add(new ChangesetBatch(batchStart, batchEnd));
                    batchStart = candidate;
                }
                batchEnd = candidate;
            }

            result.Add(new ChangesetBatch(batchStart, batchEnd));
            return result;
        }
    }
}
