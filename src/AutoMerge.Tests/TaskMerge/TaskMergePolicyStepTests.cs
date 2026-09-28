using System.Collections.Generic;
using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.TaskMerge
{
    // Riga di un passo con le regole di merge (azione di esecuzione, stato Skipped) e voci del promemoria.
    public class TaskMergePolicyStepTests
    {
        [Fact]
        public void SkippedStatus_CountsAsDone_AndSaysPolicy()
        {
            var step = CreateStep();

            step.Status = TaskMergeStepStatus.Skipped;

            Assert.True(step.IsDone);
            Assert.Equal("Skipped (policy)", step.StatusText);
        }

        [Fact]
        public void PolicyAction_NotEditable_ChangeIsIgnored()
        {
            var step = CreateStep();

            Assert.False(step.IsPolicyEditable);
            step.PolicyAction = MergePolicyAction.Skip;

            Assert.Equal(MergePolicyAction.Merge, step.PolicyAction);
            Assert.False(step.IsPolicyOverridden);
        }

        [Fact]
        public void PolicyAction_Editable_ChangesTheExecutionOnly()
        {
            var step = CreateStep();
            step.IsPolicyEditable = true;

            step.PolicyAction = MergePolicyAction.Discard;

            Assert.Equal(MergePolicyAction.Discard, step.PolicyAction);
            Assert.Equal(MergePolicyAction.Merge, step.DecidedPolicyAction);
            Assert.True(step.IsPolicyOverridden);
            Assert.Equal("set here", step.PolicySourceText);
            Assert.False(step.HasProtectedLines);
            Assert.Empty(step.ProtectedLineRules);

            step.Status = TaskMergeStepStatus.Merged;
            Assert.Equal("Discarded", step.StatusText);
        }

        [Fact]
        public void PolicyActions_AreMergeDiscardSkip()
        {
            Assert.Equal(new[] { MergePolicyAction.Merge, MergePolicyAction.Discard, MergePolicyAction.Skip },
                TaskMergeStepViewModel.PolicyActions);
        }

        [Fact]
        public void FollowUp_ProtectedLines_ShowsFirstDetailsAndCount()
        {
            var item = new TaskMergeFollowUpViewModel(TaskMergeFollowUpKind.ProtectedLinesKept, 3, "src/a.csproj", "2 changes",
                new[] { "l1", "l2", "l3", "l4", "l5", "l6" });

            Assert.True(item.IsWarning);
            Assert.True(item.HasDetails);
            Assert.StartsWith("l1\nl2\nl3\nl4\n", item.DetailsText);
            Assert.Contains("2 more", item.DetailsText);
            Assert.StartsWith("#3 src/a.csproj: 2 changes\n    l1", item.ToClipboardText());
        }

        [Fact]
        public void FollowUp_Skipped_IsInformationOnly()
        {
            var item = new TaskMergeFollowUpViewModel(TaskMergeFollowUpKind.Skipped, 7, "b.cs", "skipped", null);

            Assert.False(item.IsWarning);
            Assert.False(item.HasDetails);
            Assert.Equal("#7 b.cs: skipped", item.ToClipboardText());
        }

        // Un passo: Edit di un file che esiste nel target.
        private static TaskMergeStepViewModel CreateStep()
        {
            var plan = TaskMergePlanner.Build(new TaskMergePlanInput
            {
                SourceBranch = "$/Source",
                TargetBranch = "$/Target",
                TaskChangesetIds = new List<int> { 10 },
                Changes = new List<TaskChangeInfo> { new TaskChangeInfo(10, "$/Source/a.cs", TaskMergeItemKind.File, TaskChangeKind.Edit) },
                TargetItemExists = item => true,
                ThirdPartyChangesetsBetween = (item, a, b) => new int[0]
            });
            Assert.True(plan.IsValid, string.Join("; ", plan.Errors));
            var part = plan.Parts[0];
            return new TaskMergeStepViewModel(part.Steps[0], new TaskMergePartViewModel(part, plan.Parts.Count, null));
        }
    }
}
