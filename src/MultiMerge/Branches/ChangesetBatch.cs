// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
namespace MultiMerge
{
    public class ChangesetBatch
    {
        public ChangesetBatch(int fromChangesetId, int toChangesetId)
        {
            FromChangesetId = fromChangesetId;
            ToChangesetId = toChangesetId;
        }

        public int FromChangesetId { get; private set; }
        public int ToChangesetId { get; private set; }
    }
}
