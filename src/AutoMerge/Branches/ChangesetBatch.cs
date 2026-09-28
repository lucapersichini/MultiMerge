namespace AutoMerge
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
