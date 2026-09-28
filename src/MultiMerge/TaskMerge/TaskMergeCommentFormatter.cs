// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MultiMerge
{
    public static class TaskMergeCommentFormatter
    {
        // The caller supplies only the changesets being delivered in this part or partial check-in.
        public static string AppendChangesetComments(string header, IEnumerable<int> changesetIds,
            IEnumerable<TaskMergeChangesetInfo> changesets)
        {
            if (header == null)
                throw new ArgumentNullException("header");
            if (changesetIds == null)
                throw new ArgumentNullException("changesetIds");
            if (changesets == null)
                throw new ArgumentNullException("changesets");

            var comments = changesets.Where(c => c != null).GroupBy(c => c.ChangesetId)
                .ToDictionary(g => g.Key, g => g.First().Comment);
            var text = new StringBuilder(header);
            foreach (var id in changesetIds.Distinct().OrderBy(id => id))
            {
                string comment;
                if (!comments.TryGetValue(id, out comment) || string.IsNullOrWhiteSpace(comment))
                    continue;
                text.Append("\n\nC").Append(id.ToString(CultureInfo.InvariantCulture)).Append(": ");
                text.Append(comment.Trim().Replace("\r\n", "\n").Replace('\r', '\n'));
            }
            return text.ToString();
        }
    }
}