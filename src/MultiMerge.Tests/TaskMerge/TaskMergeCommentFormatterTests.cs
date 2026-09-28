// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using MultiMerge;
using Xunit;

namespace MultiMerge.Tests.TaskMerge
{
    public class TaskMergeCommentFormatterTests
    {
        [Fact]
        public void IncludesOnlyChangesetsDeliveredInThisPart()
        {
            var result = TaskMergeCommentFormatter.AppendChangesetComments("Task - part 1 of 2", new[] { 20 },
                new[] { Info(10, "Excluded change"), Info(20, "Delivered change"), Info(30, "Next part") });
            Assert.Equal("Task - part 1 of 2\n\nC20: Delivered change", result);
        }

        [Fact]
        public void KeepsFullMultilineCommentsAndUnicode()
        {
            var result = TaskMergeCommentFormatter.AppendChangesetComments("Task", new[] { 20 },
                new[] { Info(20, "  Autenticazione già pronta\r\nSeconda riga\rTerza riga  ") });
            Assert.Equal("Task\n\nC20: Autenticazione già pronta\nSeconda riga\nTerza riga", result);
        }

        [Fact]
        public void OrdersChronologicallyAndDoesNotRepeatChangesetIds()
        {
            var result = TaskMergeCommentFormatter.AppendChangesetComments("Partial", new[] { 30, 20, 30 },
                new[] { Info(30, "Third"), Info(20, "Second") });
            Assert.Equal("Partial\n\nC20: Second\n\nC30: Third", result);
        }

        [Fact]
        public void EmptyOrUnavailableCommentsPreserveHeader()
        {
            var result = TaskMergeCommentFormatter.AppendChangesetComments("Task (changesets 10, 20, 30)",
                new[] { 10, 20, 30 }, new[] { Info(10, null), Info(20, " \r\n ") });
            Assert.Equal("Task (changesets 10, 20, 30)", result);
        }

        [Fact]
        public void NoDeliveredChangesetsAddsNoComments()
        {
            Assert.Equal("Task", TaskMergeCommentFormatter.AppendChangesetComments("Task", Array.Empty<int>(),
                new[] { Info(20, "Not delivered") }));
        }

        [Fact]
        public void DuplicateMetadataAddsOneComment()
        {
            Assert.Equal("Task\n\nC20: First", TaskMergeCommentFormatter.AppendChangesetComments("Task", new[] { 20 },
                new[] { Info(20, "First"), Info(20, "Duplicate") }));
        }

        private static TaskMergeChangesetInfo Info(int id, string comment)
        {
            return new TaskMergeChangesetInfo(id, null, "Demo", comment, 1, 0);
        }
    }
}