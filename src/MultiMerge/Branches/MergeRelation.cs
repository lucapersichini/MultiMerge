// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    public class MergeRelation
    {
        public string Item { get; set; }

        public string Source { get; set; }

        public string Target { get; set; }

        public ItemType TargetItemType { get; set; }

        public string GetLatesPath { get; set; }

        public bool Recursively { get; set; }
    }
}
