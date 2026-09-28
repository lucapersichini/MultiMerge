// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using MultiMerge;
using Xunit;

namespace MultiMerge.Tests.TaskMerge
{
    public class TaskMergeSafetyTests
    {
        private const string Source = "$/ContosoMain";
        private const string Target = "$/Build/ContosoMain/rel1.0_new";

        private const TaskChangeKind AddNew = TaskChangeKind.Add | TaskChangeKind.Edit | TaskChangeKind.Encoding;
        private const TaskChangeKind AddFolder = TaskChangeKind.Add | TaskChangeKind.Encoding;

        // ------------------------------------------------------------------------------------------
        // TaskMergeDependencyAnalyzer
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Dependencies_AllSelected_NoWarnings()
        {
            var changes = new[]
            {
                Change(10, "a.cs"), Change(20, "a.cs"), Change(30, "b.cs")
            };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 10, 20, 30 });

            Assert.Empty(warnings);
        }

        [Fact]
        public void Dependencies_ExcludedBeforeSelectedOnSameItem_OneWarning()
        {
            var changes = new[]
            {
                Change(10, "a.cs"), Change(10, "b.cs"),
                Change(20, "a.cs"), Change(20, "c.cs")
            };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20 });

            var w = Assert.Single(warnings);
            Assert.Equal(10, w.ExcludedChangesetId);
            Assert.Equal(20, w.DependentChangesetId);
            Assert.Equal(new[] { Source + "/a.cs" }, w.SharedSourceItems.ToArray());
        }

        [Fact]
        public void Dependencies_ExcludedAfterSelected_NoWarning()
        {
            var changes = new[] { Change(10, "a.cs"), Change(20, "a.cs") };

            // il selezionato C10 viene PRIMA dell'escluso C20: non puo' dipendere da lui
            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 10 });

            Assert.Empty(warnings);
        }

        [Fact]
        public void Dependencies_ExcludedWithoutSharedItems_NoWarning()
        {
            var changes = new[] { Change(10, "a.cs"), Change(20, "b.cs"), Change(30, "dir/a.cs") };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20, 30 });

            Assert.Empty(warnings);
        }

        [Fact]
        public void Dependencies_CaseInsensitivePaths_WarnWithCasingOfSelected()
        {
            var changes = new[]
            {
                new TaskChangeInfo(10, "$/ContosoMain/Web/Rest.CSPROJ", TaskMergeItemKind.File, TaskChangeKind.Edit),
                new TaskChangeInfo(20, "$/contosomain/web/Rest.csproj", TaskMergeItemKind.File, TaskChangeKind.Edit)
            };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20 });

            var w = Assert.Single(warnings);
            Assert.Equal(new[] { "$/contosomain/web/Rest.csproj" }, w.SharedSourceItems.ToArray());
        }

        [Fact]
        public void Dependencies_TrailingSlashOnFolder_IsTheSameItem()
        {
            var changes = new[]
            {
                new TaskChangeInfo(10, Source + "/Folder/", TaskMergeItemKind.Folder, AddFolder),
                new TaskChangeInfo(20, Source + "/Folder", TaskMergeItemKind.Folder, TaskChangeKind.Property)
            };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20 });

            var w = Assert.Single(warnings);
            Assert.Equal(new[] { Source + "/Folder" }, w.SharedSourceItems.ToArray());
        }

        [Fact]
        public void Dependencies_ExcludedBeforeSeveralSelected_OneWarningPerPairInOrder()
        {
            var changes = new[]
            {
                Change(10, "a.cs"), Change(20, "a.cs"), Change(30, "b.cs"), Change(40, "a.cs")
            };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20, 30, 40 });

            Assert.Equal(2, warnings.Count);
            Assert.Equal(new[] { 10, 10 }, warnings.Select(w => w.ExcludedChangesetId).ToArray());
            Assert.Equal(new[] { 20, 40 }, warnings.Select(w => w.DependentChangesetId).ToArray());
        }

        [Fact]
        public void Dependencies_SeveralExcluded_WarningsOrderedByExcludedThenDependent()
        {
            var changes = new[]
            {
                Change(10, "a.cs"), Change(15, "b.cs"), Change(20, "b.cs"), Change(20, "a.cs"), Change(30, "a.cs")
            };

            // esclusi C10 e C15; selezionati C20 e C30
            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20, 30 });

            Assert.Equal(
                new[] { "10->20", "10->30", "15->20" },
                warnings.Select(w => w.ExcludedChangesetId + "->" + w.DependentChangesetId).ToArray());
            var w1520 = warnings.Single(w => w.ExcludedChangesetId == 15);
            Assert.Equal(new[] { Source + "/b.cs" }, w1520.SharedSourceItems.ToArray());
        }

        [Fact]
        public void Dependencies_SharedItemsSortedAndDeduplicated()
        {
            var changes = new[]
            {
                Change(10, "z.cs"), Change(10, "a.cs"), Change(10, "m.cs"),
                Change(20, "m.cs"), Change(20, "z.cs"), Change(20, "a.cs"), Change(20, "A.cs")
            };

            var warnings = TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20 });

            var w = Assert.Single(warnings);
            Assert.Equal(new[] { Source + "/a.cs", Source + "/m.cs", Source + "/z.cs" }, w.SharedSourceItems.ToArray());
        }

        [Fact]
        public void Dependencies_Message_NamesBothChangesetsAndTheItems()
        {
            var changes = new[] { Change(162115, "Web/Rest.csproj"), Change(162933, "Web/Rest.csproj") };

            var w = Assert.Single(TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 162933 }));

            Assert.Contains("C162115 is not selected", w.Message);
            Assert.Contains("C162933", w.Message);
            Assert.Contains(Source + "/Web/Rest.csproj", w.Message);
            Assert.Contains("may need C162115", w.Message);
        }

        [Fact]
        public void Dependencies_Message_TruncatesLongItemLists()
        {
            var changes = new List<TaskChangeInfo>();
            for (var i = 1; i <= 7; i++)
            {
                changes.Add(Change(10, "f" + i + ".cs"));
                changes.Add(Change(20, "f" + i + ".cs"));
            }

            var w = Assert.Single(TaskMergeDependencyAnalyzer.Analyze(changes, new[] { 20 }));

            Assert.Equal(7, w.SharedSourceItems.Count);
            Assert.Contains("7 items", w.Message);
            Assert.Contains("f5.cs", w.Message);
            Assert.DoesNotContain("f6.cs", w.Message);
            Assert.Contains("and 2 more", w.Message);
        }

        [Fact]
        public void Dependencies_NothingSelected_NoWarnings()
        {
            var changes = new[] { Change(10, "a.cs"), Change(20, "a.cs") };

            Assert.Empty(TaskMergeDependencyAnalyzer.Analyze(changes, new int[0]));
        }

        [Fact]
        public void Dependencies_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => TaskMergeDependencyAnalyzer.Analyze(null, new[] { 1 }));
            Assert.Throws<ArgumentNullException>(() => TaskMergeDependencyAnalyzer.Analyze(new TaskChangeInfo[0], null));
        }

        // ------------------------------------------------------------------------------------------
        // TaskMergeAudit: casi puliti
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Audit_AllPendingFromSelectedAndAllStepsMerged_IsClean()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(20, "b.cs", AddNew)
                .Change(30, "a.cs", TaskChangeKind.Edit);
            var plan = w.Plan();
            var audit = w.Audit(plan.Steps, w.PendingFor(plan.Steps));

            var result = TaskMergeAudit.Run(audit);

            Assert.True(result.IsClean, result.Summary);
            Assert.Empty(result.Issues);
            Assert.Equal("Final check passed: 2 pending merges, all from task changesets C10-C30; 2/2 steps merged.", result.Summary);
        }

        [Fact]
        public void Audit_NoPendingAndNoSteps_IsClean()
        {
            var result = TaskMergeAudit.Run(new TaskMergeAuditInput
            {
                Pending = new AuditPendingChange[0],
                SelectedChangesetIds = new[] { 10 },
                StepsToDeliver = new TaskMergeStep[0],
                ChangesetsTouchingSourceItem = (item, a, b) => new int[0],
                StepIsMerged = s => true
            });

            Assert.True(result.IsClean);
            Assert.Equal("Final check passed: no pending merges; 0/0 steps merged.", result.Summary);
        }

        [Fact]
        public void Audit_TargetPathCasingAndTrailingSlash_StillPlanned()
        {
            var w = new World().Change(10, "Dir/a.cs", TaskChangeKind.Edit).Exists("Dir/a.cs");
            var plan = w.Plan();
            var pending = new[]
            {
                Merge((Target + "/DIR/A.CS/").ToLowerInvariant(), Source + "/Dir/a.cs", 10, 10)
            };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.True(result.IsClean, result.Summary);
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void Audit_DescendantsOfDeletedFolderStep_ArePlanned()
        {
            var w = new World()
                .Change(10, "Old", TaskChangeKind.Delete, TaskMergeItemKind.Folder).Exists("Old");
            w.History(Source + "/Old/x.cs", 3, 10);
            var plan = w.Plan();
            var step = Assert.Single(plan.Steps);
            Assert.Equal(TaskMergeStepRecursion.Full, step.Recursion);

            var pending = new[]
            {
                Merge(Target + "/Old", Source + "/Old", 10, 10, "Delete, Merge"),
                Merge(Target + "/Old/x.cs", Source + "/Old/x.cs", 10, 10, "Delete, Merge")
            };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.True(result.IsClean, result.Summary);
            Assert.Empty(result.Issues);
        }

        [Fact]
        public void Audit_HistoryOutsideTheRange_IsIgnored()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.ChangesetsTouchingSourceItem = (item, a, b) => new[] { 5, 10, 99 };

            var result = TaskMergeAudit.Run(input);

            Assert.True(result.IsClean, result.Summary);
        }

        // ------------------------------------------------------------------------------------------
        // R1: solo merge
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Audit_PendingThatIsNotAMerge_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var pending = new[] { new AuditPendingChange(Target + "/a.cs", false, "Edit", new AuditMergeSource[0]) };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.NotAMerge, issue.Kind);
            Assert.True(issue.Blocking);
            Assert.Equal(Target + "/a.cs", issue.TargetItem);
            Assert.Contains("(Edit)", issue.Message);
            Assert.StartsWith("Final check failed: 1 blocking problem (not a merge: 1)", result.Summary);
        }

        // ------------------------------------------------------------------------------------------
        // R2: solo changeset selezionati negli intervalli in sospeso
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Audit_RangeContainingAColleagueChangeset_BlocksAsForeign()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Third("a.cs", 20);
            var plan = w.Plan();
            // qualcuno ha fuso a mano [10..30]: dentro c'e' il C20 di un collega
            var pending = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 30) };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps.Where(s => s.Part == 1).ToList(), pending));

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.ForeignChangeset, issue.Kind);
            Assert.True(issue.Blocking);
            Assert.Contains("C20", issue.Message);
            Assert.DoesNotContain("C10,", issue.Message);
        }

        [Fact]
        public void Audit_RangeContainingAChangesetExcludedByTheUser_BlocksAsForeign()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(20, "a.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit);
            var plan = w.Plan();
            var pending = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 30) };
            var input = w.Audit(plan.Steps, pending);
            input.SelectedChangesetIds = new[] { 10, 30 };

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.ForeignChangeset, issue.Kind);
            Assert.Contains("not selected", issue.Message);
            Assert.Contains("C20", issue.Message);
        }

        [Fact]
        public void Audit_HistoryNotAvailable_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.ChangesetsTouchingSourceItem = (item, a, b) => null;

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, issue.Kind);
            Assert.True(issue.Blocking);
        }

        [Fact]
        public void Audit_HistoryCallbackThrows_BlocksWithTheReason()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.ChangesetsTouchingSourceItem = (item, a, b) => { throw new InvalidOperationException("TF30063 not authorized"); };

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, issue.Kind);
            Assert.Contains("TF30063", issue.Message);
        }

        [Fact]
        public void Audit_NoHistoryCallback_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.ChangesetsTouchingSourceItem = null;

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, Assert.Single(result.Issues).Kind);
        }

        [Fact]
        public void Audit_MergeWithoutMergeSources_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();

            var nullSources = TaskMergeAudit.Run(w.Audit(plan.Steps, new[] { new AuditPendingChange(Target + "/a.cs", true, "Edit, Merge", null) }));
            var noSources = TaskMergeAudit.Run(w.Audit(plan.Steps, new[] { new AuditPendingChange(Target + "/a.cs", true, "Edit, Merge", new AuditMergeSource[0]) }));

            Assert.False(nullSources.IsClean);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, Assert.Single(nullSources.Issues).Kind);
            Assert.False(noSources.IsClean);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, Assert.Single(noSources.Issues).Kind);
        }

        [Fact]
        public void Audit_InvalidMergeRange_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();

            var reversed = TaskMergeAudit.Run(w.Audit(plan.Steps, new[] { Merge(Target + "/a.cs", Source + "/a.cs", 30, 10) }));
            var zero = TaskMergeAudit.Run(w.Audit(plan.Steps, new[] { Merge(Target + "/a.cs", Source + "/a.cs", 0, 10) }));

            Assert.False(reversed.IsClean);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, Assert.Single(reversed.Issues).Kind);
            Assert.False(zero.IsClean);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, Assert.Single(zero.Issues).Kind);
        }

        [Fact]
        public void Audit_RenameSource_IsIgnoredByTheChangesetCheck()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            // la sorgente del rename ha una storia piena di changeset estranei: non deve contare
            w.History(Source + "/old-name.cs", 1, 2, 3, 4, 5);
            var plan = w.Plan();
            var pending = new[]
            {
                new AuditPendingChange(Target + "/a.cs", true, "Edit, Merge", new[]
                {
                    new AuditMergeSource(Source + "/old-name.cs", 1, 5, true),
                    new AuditMergeSource(Source + "/a.cs", 10, 10, false)
                })
            };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.True(result.IsClean, result.Summary);
            Assert.DoesNotContain(w.HistoryCalls, c => c.Item1.EndsWith("old-name.cs", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Audit_MergeWithOnlyARenameSource_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var pending = new[]
            {
                new AuditPendingChange(Target + "/a.cs", true, "Rename, Merge", new[] { new AuditMergeSource(Source + "/old.cs", 10, 10, true) })
            };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.False(result.IsClean);
            Assert.Equal(AuditIssueKind.HistoryUnavailable, Assert.Single(result.Issues).Kind);
        }

        [Fact]
        public void Audit_SameSourceRange_HistoryReadOnce()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var pending = new[]
            {
                new AuditPendingChange(Target + "/a.cs", true, "Edit, Merge", new[]
                {
                    new AuditMergeSource(Source + "/a.cs", 10, 10, false),
                    new AuditMergeSource(Source + "/A.CS", 10, 10, false)
                })
            };

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.True(result.IsClean, result.Summary);
            Assert.Single(w.HistoryCalls);
        }

        // ------------------------------------------------------------------------------------------
        // R3: ogni passo consegnato e' fuso
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Audit_StepNotMerged_Blocks()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(20, "b.cs", TaskChangeKind.Edit).Exists("b.cs");
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps.Take(1)));
            input.StepIsMerged = s => s.RelativePath != "b.cs";

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.StepNotMerged, issue.Kind);
            Assert.Equal(Target + "/b.cs", issue.TargetItem);
            Assert.Contains("b.cs", issue.Message);
            Assert.Contains("not fully merged", issue.Message);
            Assert.Contains("1/2 steps merged", result.Summary);
        }

        [Fact]
        public void Audit_StepPreviewUnavailable_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.StepIsMerged = s => null;

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.StepNotMerged, issue.Kind);
            Assert.True(issue.Blocking);
            Assert.Contains("could not be verified", issue.Message);
        }

        [Fact]
        public void Audit_StepPreviewThrowsOrMissing_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var throwing = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            throwing.StepIsMerged = s => { throw new TimeoutException("server timeout"); };
            var missing = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            missing.StepIsMerged = null;

            var r1 = TaskMergeAudit.Run(throwing);
            var r2 = TaskMergeAudit.Run(missing);

            Assert.False(r1.IsClean);
            Assert.Contains("server timeout", Assert.Single(r1.Issues).Message);
            Assert.False(r2.IsClean);
            Assert.Equal(AuditIssueKind.StepNotMerged, Assert.Single(r2.Issues).Kind);
        }

        // ------------------------------------------------------------------------------------------
        // R4: pending non pianificati
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Audit_UnplannedButCleanPending_IsANonBlockingNote()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(10, "b.cs", TaskChangeKind.Edit).Exists("b.cs");
            var plan = w.Plan();
            // si consegna solo a.cs, ma e' in sospeso anche b.cs (merge pulito di C10)
            var steps = plan.Steps.Where(s => s.RelativePath == "a.cs").ToList();
            var input = w.Audit(steps, w.PendingFor(plan.Steps));

            var result = TaskMergeAudit.Run(input);

            Assert.True(result.IsClean, result.Summary);
            var issue = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.UnplannedPendingChange, issue.Kind);
            Assert.False(issue.Blocking);
            Assert.Equal(Target + "/b.cs", issue.TargetItem);
            Assert.Equal("Final check passed: 2 pending merges, all from task changeset C10; 1/1 steps merged; 1 note.", result.Summary);
        }

        [Fact]
        public void Audit_UnplannedPendingWithForeignChangesets_Blocks()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            w.History(Source + "/other.cs", 7);
            var plan = w.Plan();
            var pending = w.PendingFor(plan.Steps).Concat(new[] { Merge(Target + "/other.cs", Source + "/other.cs", 7, 7) }).ToList();

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.False(result.IsClean);
            Assert.Equal(2, result.Issues.Count);
            Assert.All(result.Issues, i => Assert.True(i.Blocking));
            Assert.Contains(result.Issues, i => i.Kind == AuditIssueKind.ForeignChangeset && i.TargetItem == Target + "/other.cs");
            Assert.Contains(result.Issues, i => i.Kind == AuditIssueKind.UnplannedPendingChange && i.TargetItem == Target + "/other.cs");
        }

        [Fact]
        public void Audit_UnplannedPendingThatIsNotAMerge_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var pending = w.PendingFor(plan.Steps)
                .Concat(new[] { new AuditPendingChange(Target + "/local-edit.txt", false, "Add", null) })
                .ToList();

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.False(result.IsClean);
            Assert.Contains(result.Issues, i => i.Kind == AuditIssueKind.NotAMerge && i.Blocking);
            Assert.Contains(result.Issues, i => i.Kind == AuditIssueKind.UnplannedPendingChange && i.Blocking);
        }

        // ------------------------------------------------------------------------------------------
        // Ingressi mancanti e ordine dei problemi
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Audit_PendingTeamPolicyFile_BlocksWithAMessageThatSaysWhatToDo()
        {
            // il file di team salvato dalla scheda Merge Policies e' una modifica in sospeso sotto il
            // target che non e' un merge: blocca come ogni altra (R1), e il messaggio dice cosa fare
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var pending = w.PendingFor(plan.Steps)
                .Concat(new[] { new AuditPendingChange(Target + "/" + MergePolicyStore.TeamFileName, false, "Add", null) })
                .ToList();

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.False(result.IsClean);
            var issue = Assert.Single(result.Issues, i => i.Kind == AuditIssueKind.NotAMerge);
            Assert.True(issue.Blocking);
            Assert.Contains("team merge policy file", issue.Message);
            Assert.Contains("check it in on its own (or undo it) first", issue.Message);
            var other = TaskMergeAudit.Run(w.Audit(plan.Steps, w.PendingFor(plan.Steps)
                .Concat(new[] { new AuditPendingChange(Target + "/local-edit.txt", false, "Add", null) }).ToList()));
            Assert.DoesNotContain(other.Issues, i => i.Message.Contains("team merge policy file"));
        }

        [Fact]
        public void Audit_PendingOrStepsNotKnown_Blocks()
        {
            var w = new World().Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs");
            var plan = w.Plan();
            var noPending = w.Audit(plan.Steps, null);
            var noSteps = w.Audit(null, w.PendingFor(plan.Steps));

            var r1 = TaskMergeAudit.Run(noPending);
            var r2 = TaskMergeAudit.Run(noSteps);

            Assert.False(r1.IsClean);
            Assert.Contains(r1.Issues, i => i.Kind == AuditIssueKind.HistoryUnavailable && i.TargetItem == null);
            Assert.False(r2.IsClean);
            Assert.Contains(r2.Issues, i => i.Kind == AuditIssueKind.StepNotMerged && i.TargetItem == null);
            Assert.Throws<ArgumentNullException>(() => TaskMergeAudit.Run(null));
        }

        [Fact]
        public void Audit_BlockingIssuesComeFirst_AndSummaryCountsEachKind()
        {
            var w = new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(10, "b.cs", TaskChangeKind.Edit).Exists("b.cs")
                .Change(10, "c.cs", TaskChangeKind.Edit).Exists("c.cs");
            var plan = w.Plan();
            var steps = plan.Steps.Where(s => s.RelativePath != "a.cs").ToList();
            var pending = new[]
            {
                Merge(Target + "/a.cs", Source + "/a.cs", 10, 10),                     // non pianificato ma pulito: nota
                new AuditPendingChange(Target + "/b.cs", false, "Edit", null),         // non un merge
                Merge(Target + "/c.cs", Source + "/c.cs", 10, 10)
            };
            var input = w.Audit(steps, pending);
            input.StepIsMerged = s => s.RelativePath == "c.cs";

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            Assert.Equal(new[] { true, true, false }, result.Issues.Select(i => i.Blocking).ToArray());
            Assert.Equal(AuditIssueKind.UnplannedPendingChange, result.Issues[2].Kind);
            Assert.Equal(
                "Final check failed: 2 blocking problems (not a merge: 1, steps not merged: 1); 3 pending changes; 1/2 steps merged. The check-in is blocked.",
                result.Summary);
        }

        // ------------------------------------------------------------------------------------------
        // Il work item 128458 in piccolo (dati veri, ridotti)
        // ------------------------------------------------------------------------------------------

        private const string Csproj = "Web/Contoso.Web.Rest/Contoso.Web.Rest.csproj";
        private const string Packages = "Web/Contoso.Web.Rest/packages.config";
        private static readonly int[] Colleagues = { 162213, 162222, 162290, 162650, 162832, 162892, 162910 };

        private static World Task128458()
        {
            return new World()
                .Change(162095, "vNext/Utilities", AddFolder, TaskMergeItemKind.Folder)
                .Change(162095, "vNext/Utilities/PKCEHelper.cs", AddNew)
                .Change(162095, "vNext/AppHost/Infrastructure/IapExtensions.cs", TaskChangeKind.Edit).Exists("vNext/AppHost/Infrastructure/IapExtensions.cs")
                .Change(162095, "vNext/AppHost/keycloak/import/realm-demo-export.json", TaskChangeKind.Delete).Exists("vNext/AppHost/keycloak/import/realm-demo-export.json")
                .Change(162108, Csproj, TaskChangeKind.Edit).Change(162108, Packages, TaskChangeKind.Edit)
                .Change(162115, Csproj, TaskChangeKind.Edit).Change(162115, Packages, TaskChangeKind.Edit)
                .Change(162931, "vNext/Solutions/Main.slnx", TaskChangeKind.Edit).Exists("vNext/Solutions/Main.slnx")
                .Change(162933, Csproj, TaskChangeKind.Edit).Change(162933, Packages, TaskChangeKind.Edit)
                .Change(162978, Csproj, TaskChangeKind.Edit).Change(162978, Packages, TaskChangeKind.Edit)
                .Change(163661, "vNext/Utilities/PKCEHelper.cs", TaskChangeKind.Edit)
                .Exists(Csproj).Exists(Packages).Exists("vNext")
                .Third(Csproj, Colleagues)
                .Third(Packages, Colleagues);
        }

        [Fact]
        public void Task128458_PlanHasTwoPartsAndEachPartPassesTheFinalCheck()
        {
            var w = Task128458();
            var plan = w.Plan();
            Assert.True(plan.IsValid, string.Join(" | ", plan.Errors));
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(162931, plan.Parts[0].ToChangesetId);
            Assert.Equal(162933, plan.Parts[1].FromChangesetId);

            var part1 = plan.Parts[0].Steps;
            var r1 = TaskMergeAudit.Run(w.Audit(part1, w.PendingFor(part1)));
            Assert.True(r1.IsClean, r1.Summary);
            Assert.Equal(
                "Final check passed: 7 pending merges, all from task changesets C162095-C162931; 7/7 steps merged.",
                r1.Summary);

            var part2 = plan.Parts[1].Steps;
            var r2 = TaskMergeAudit.Run(w.Audit(part2, w.PendingFor(part2)));
            Assert.True(r2.IsClean, r2.Summary);
            Assert.Equal(
                "Final check passed: 3 pending merges, all from task changesets C162933-C163661; 3/3 steps merged.",
                r2.Summary);
        }

        [Fact]
        public void Task128458_SingleCheckInOverTheColleaguesChangesets_IsBlocked()
        {
            var w = Task128458();
            var plan = w.Plan();
            // la vecchia strada: tutto in un colpo, csproj e packages.config in sospeso su [162108..162978]
            var pending = w.PendingFor(plan.Parts[0].Steps.Where(s => s.RelativePath != Csproj && s.RelativePath != Packages))
                .Concat(new[]
                {
                    Merge(Target + "/" + Csproj, Source + "/" + Csproj, 162108, 162978),
                    Merge(Target + "/" + Packages, Source + "/" + Packages, 162108, 162978)
                })
                .ToList();

            var result = TaskMergeAudit.Run(w.Audit(plan.Steps, pending));

            Assert.False(result.IsClean);
            var foreign = result.Issues.Where(i => i.Kind == AuditIssueKind.ForeignChangeset).ToList();
            Assert.Equal(2, foreign.Count);
            foreach (var issue in foreign)
            {
                foreach (var id in Colleagues)
                    Assert.Contains("C" + id, issue.Message);
                Assert.DoesNotContain("C162933", issue.Message.Substring(issue.Message.IndexOf("selected for the task", StringComparison.Ordinal)));
            }
        }

        [Fact]
        public void Task128458_ExcludingC162115_WarnsForTheLaterChangesetsOfTheSameFiles()
        {
            var w = Task128458();
            var selected = w.TaskIds.Where(id => id != 162115).ToList();

            var warnings = TaskMergeDependencyAnalyzer.Analyze(w.Changes, selected);

            Assert.Equal(new[] { "162115->162933", "162115->162978" },
                warnings.Select(x => x.ExcludedChangesetId + "->" + x.DependentChangesetId).ToArray());
            Assert.All(warnings, x => Assert.Equal(
                new[] { Source + "/" + Csproj, Source + "/" + Packages },
                x.SharedSourceItems.ToArray()));

            // con C162115 escluso diventa "di terzi": il piano resta valido e il check-in va prima di C162933
            var plan = w.Plan(selected);
            Assert.True(plan.IsValid, string.Join(" | ", plan.Errors));
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(162933, plan.Parts[1].FromChangesetId);
            Assert.DoesNotContain(plan.Steps, s => s.TaskChangesetIds.Contains(162115));
        }

        // ------------------------------------------------------------------------------------------
        // TaskMergePendingSnapshot: pending riletti subito prima del check-in
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Snapshot_SamePendingInAnotherOrderAndCasing_NoDifferences()
        {
            var checkedPending = new[]
            {
                Merge(Target + "/a.cs", Source + "/a.cs", 10, 12),
                new AuditPendingChange(Target + "/b.cs", true, "Edit, Merge", new[]
                {
                    new AuditMergeSource(Source + "/b.cs", 10, 10, false),
                    new AuditMergeSource(Source + "/old/b.cs", 5, 5, true)
                })
            };
            var current = new[]
            {
                new AuditPendingChange((Target + "/B.CS/").ToUpperInvariant(), true, "Edit, Merge", new[]
                {
                    new AuditMergeSource(Source + "/old/b.cs", 5, 5, true),
                    new AuditMergeSource(Source + "/b.cs", 10, 10, false)
                }),
                Merge(Target + "/a.cs", Source + "/a.cs", 10, 12)
            };

            Assert.Empty(TaskMergePendingSnapshot.Differences(checkedPending, current));
        }

        [Fact]
        public void Snapshot_WiderRangeMergedAfterTheCheck_IsADifference()
        {
            // il caso del difetto: controllato [C150], poi qualcuno fonde [C100..C200] sullo stesso item
            // (TFVC lo accetta perche' comprende quello in sospeso) prima del check-in
            var checkedPending = new[] { Merge(Target + "/x.cs", Source + "/x.cs", 150, 150) };
            var current = new[] { Merge(Target + "/x.cs", Source + "/x.cs", 100, 200) };

            var differences = TaskMergePendingSnapshot.Differences(checkedPending, current);

            var d = Assert.Single(differences);
            Assert.Contains(Target + "/x.cs", d);
            Assert.Contains("C150", d);
            Assert.Contains("C100-C200", d);
        }

        [Fact]
        public void Snapshot_ChangeTypeChanged_IsADifference()
        {
            var checkedPending = new[] { Merge(Target + "/x.cs", Source + "/x.cs", 10, 10, "Merge") };
            var current = new[] { Merge(Target + "/x.cs", Source + "/x.cs", 10, 10, "Edit, Merge") };

            Assert.Single(TaskMergePendingSnapshot.Differences(checkedPending, current));
        }

        [Fact]
        public void Snapshot_PendingAddedOrGone_AreDifferences()
        {
            var checkedPending = new[]
            {
                Merge(Target + "/a.cs", Source + "/a.cs", 10, 10),
                Merge(Target + "/b.cs", Source + "/b.cs", 10, 10)
            };
            var current = new[]
            {
                Merge(Target + "/a.cs", Source + "/a.cs", 10, 10),
                new AuditPendingChange(Target + "/c.cs", false, "Edit", null)
            };

            var differences = TaskMergePendingSnapshot.Differences(checkedPending, current);

            Assert.Equal(2, differences.Count);
            Assert.Contains(differences, d => d.StartsWith(Target + "/b.cs", StringComparison.Ordinal) && d.Contains("no longer there"));
            Assert.Contains(differences, d => d.StartsWith(Target + "/c.cs", StringComparison.Ordinal) && d.Contains("added"));
        }

        [Fact]
        public void Snapshot_EverythingCheckedInElsewhere_IsADifference()
        {
            var checkedPending = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 10) };

            Assert.Single(TaskMergePendingSnapshot.Differences(checkedPending, new AuditPendingChange[0]));
        }

        [Fact]
        public void Snapshot_CurrentPendingNotReadable_IsADifference()
        {
            var checkedPending = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 10) };

            var d = Assert.Single(TaskMergePendingSnapshot.Differences(checkedPending, null));
            Assert.Contains("could not be read again", d);
        }

        [Fact]
        public void Snapshot_EntryWithoutItem_IsADifference()
        {
            var checkedPending = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 10) };
            var current = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 10), null };

            Assert.Single(TaskMergePendingSnapshot.Differences(checkedPending, current));
        }

        [Fact]
        public void Snapshot_Describe_ShowsTheFirstFiveAndCountsTheRest()
        {
            var differences = Enumerable.Range(1, 7).Select(i => "d" + i + ".").ToList();

            var text = TaskMergePendingSnapshot.Describe(differences);

            Assert.Equal("d1. d2. d3. d4. d5. ... and 2 more.", text);
        }

        // ------------------------------------------------------------------------------------------
        // TaskMergePendingSteps: passi coperti dai pending (revisione senza punto di check-in)
        // ------------------------------------------------------------------------------------------

        private static World ItemInTwoParts()
        {
            // a.cs ha un passo in parte 1 (C10) e uno in parte 2 (C30): il C20 di un collega sta in mezzo
            return new World()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Third("a.cs", 20);
        }

        [Fact]
        public void PendingSteps_PendingOfTheSecondPart_GivesTheSecondPartStep()
        {
            var w = ItemInTwoParts();
            var plan = w.Plan();
            Assert.Equal(2, plan.Parts.Count);

            // parte 1 gia' archiviata, parte 2 in sospeso: il passo da verificare e' quello della parte 2,
            // non il primo per path
            var steps = TaskMergePendingSteps.CoveredBy(plan.Steps, new[] { Merge(Target + "/a.cs", Source + "/a.cs", 30, 30) });

            var step = Assert.Single(steps);
            Assert.Equal(2, step.Part);
        }

        [Fact]
        public void PendingSteps_PendingOfTheFirstPart_GivesTheFirstPartStep()
        {
            var w = ItemInTwoParts();
            var plan = w.Plan();

            var steps = TaskMergePendingSteps.CoveredBy(plan.Steps, new[] { Merge(Target + "/a.cs", Source + "/a.cs", 10, 10) });

            Assert.Equal(1, Assert.Single(steps).Part);
        }

        [Fact]
        public void PendingSteps_SecondPartPendingButNotMerged_TheFinalCheckBlocks()
        {
            var w = ItemInTwoParts();
            var plan = w.Plan();
            var pending = new[] { Merge(Target + "/a.cs", Source + "/a.cs", 30, 30) };
            var input = w.Audit(TaskMergePendingSteps.CoveredBy(plan.Steps, pending), pending);
            // la parte 1 e' archiviata (anteprima 0/0/0), il passo della parte 2 non e' completo
            input.StepIsMerged = s => s.Part == 1;

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            Assert.Equal(AuditIssueKind.StepNotMerged, Assert.Single(result.Issues).Kind);
        }

        [Fact]
        public void PendingSteps_PendingWithoutMergeSources_GivesEveryStepOfTheItem()
        {
            var w = ItemInTwoParts();
            var plan = w.Plan();

            var steps = TaskMergePendingSteps.CoveredBy(plan.Steps, new[] { new AuditPendingChange(Target + "/a.cs", true, "Merge", null) });

            Assert.Equal(new[] { 1, 2 }, steps.Select(s => s.Part).ToArray());
        }

        [Fact]
        public void PendingSteps_PendingUnderADeletedFolder_GivesTheFolderStep()
        {
            var w = new World()
                .Change(10, "Old", TaskChangeKind.Delete, TaskMergeItemKind.Folder).Exists("Old")
                .Change(10, "b.cs", TaskChangeKind.Edit).Exists("b.cs");
            w.History(Source + "/Old/x.cs", 3, 10);
            var plan = w.Plan();

            var steps = TaskMergePendingSteps.CoveredBy(plan.Steps, new[] { Merge(Target + "/Old/x.cs", Source + "/Old/x.cs", 10, 10, "Delete, Merge") });

            var step = Assert.Single(steps);
            Assert.Equal("Old", step.RelativePath);
            Assert.Equal(TaskMergeStepRecursion.Full, step.Recursion);
        }

        [Fact]
        public void PendingSteps_PendingOutsideThePlan_GivesNoStep()
        {
            var w = ItemInTwoParts();
            var plan = w.Plan();

            var steps = TaskMergePendingSteps.CoveredBy(plan.Steps, new[] { Merge(Target + "/other.cs", Source + "/other.cs", 10, 10), null });

            Assert.Empty(steps);
        }

        // ------------------------------------------------------------------------------------------
        // Regole di merge (policy): R3 con Skip, R5 (Discard), R6 (righe protette)
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Policy_SkippedStep_IsNotRequired_AndIsReportedAsANote()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var delivered = plan.Steps.Where(s => s.RelativePath != "c.props").ToList();
            var input = w.Audit(plan.Steps, w.PendingFor(delivered));
            input.StepAction = s => s.RelativePath == "c.props" ? MergePolicyAction.Skip : MergePolicyAction.Merge;
            var previewed = new List<string>();
            input.StepIsMerged = s =>
            {
                previewed.Add(s.RelativePath);
                return true;
            };

            var result = TaskMergeAudit.Run(input);

            Assert.True(result.IsClean, result.Summary);
            var note = Assert.Single(result.Issues);
            Assert.Equal(AuditIssueKind.SkippedByPolicy, note.Kind);
            Assert.False(note.Blocking);
            Assert.Contains("c.props", note.Message);
            Assert.DoesNotContain("c.props", previewed);
            Assert.Equal("Final check passed: 2 pending merges, all from task changeset C10; 2/2 steps merged; 1 skipped.", result.Summary);
        }

        [Fact]
        public void Policy_PendingMergeOnASkippedItem_Blocks()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.StepAction = s => s.RelativePath == "c.props" ? MergePolicyAction.Skip : MergePolicyAction.Merge;

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            var issue = result.Issues.Single(i => i.Blocking);
            Assert.Equal(AuditIssueKind.UnplannedPendingChange, issue.Kind);
            Assert.Equal(Target + "/c.props", issue.TargetItem);
            Assert.Contains("says Skip", issue.Message);
        }

        [Fact]
        public void Policy_DiscardedStep_MergedAndTargetContentKept_IsClean()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.StepAction = s => s.RelativePath == "c.props" ? MergePolicyAction.Discard : MergePolicyAction.Merge;
            var asked = new List<string>();
            input.TargetContentPreserved = s =>
            {
                asked.Add(s.RelativePath);
                return true;
            };

            var result = TaskMergeAudit.Run(input);

            Assert.True(result.IsClean, result.Summary);
            Assert.Empty(result.Issues);
            Assert.Equal(new[] { "c.props" }, asked.ToArray());
            Assert.Equal("Final check passed: 3 pending merges, all from task changesets C10-C20; 3/3 steps merged; 1 discarded.", result.Summary);
        }

        [Fact]
        public void Policy_DiscardedStepWithChangedOrUnverifiableContent_Blocks()
        {
            var w = ThreeFiles();
            var plan = w.Plan();

            Func<Func<TaskMergeStep, bool?>, bool, TaskMergeAuditResult> run = (preserved, set) =>
            {
                var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
                input.StepAction = s => s.RelativePath == "c.props" ? MergePolicyAction.Discard : MergePolicyAction.Merge;
                if (set)
                    input.TargetContentPreserved = preserved;
                return TaskMergeAudit.Run(input);
            };

            var changed = run(s => false, true);
            var unknown = run(s => null, true);
            var throws = run(s => { throw new InvalidOperationException("download failed"); }, true);
            var missing = run(null, false);

            foreach (var result in new[] { changed, unknown, throws, missing })
            {
                Assert.False(result.IsClean);
                var issue = Assert.Single(result.Issues);
                Assert.Equal(AuditIssueKind.DiscardChangedContent, issue.Kind);
                Assert.True(issue.Blocking);
                Assert.Equal(Target + "/c.props", issue.TargetItem);
            }
            Assert.Contains("differs from the target's content", changed.Issues[0].Message);
            Assert.Contains("could not be verified", unknown.Issues[0].Message);
            Assert.Contains("download failed", throws.Issues[0].Message);
            Assert.StartsWith("Final check failed: 1 blocking problem (discarded items changed: 1)", changed.Summary);
        }

        [Fact]
        public void Policy_DiscardedStepNotMerged_BlocksAsR3()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.StepAction = s => s.RelativePath == "c.props" ? MergePolicyAction.Discard : MergePolicyAction.Merge;
            input.TargetContentPreserved = s => true;
            input.StepIsMerged = s => s.RelativePath != "c.props";

            var result = TaskMergeAudit.Run(input);

            Assert.False(result.IsClean);
            Assert.Equal(AuditIssueKind.StepNotMerged, Assert.Single(result.Issues).Kind);
        }

        [Fact]
        public void Policy_ProtectedLines_ViolationsOrUnverifiable_Block()
        {
            var w = ThreeFiles();
            var plan = w.Plan();

            Func<Func<TaskMergeStep, IReadOnlyList<string>>, TaskMergeAuditResult> run = violations =>
            {
                var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
                input.StepAction = s => MergePolicyAction.Merge;
                input.ProtectedLineViolations = violations;
                return TaskMergeAudit.Run(input);
            };

            var changed = run(s => s.RelativePath == "a.config" ? new[] { "Target line 3 is protected ..." } : new string[0]);
            var unknown = run(s => s.RelativePath == "a.config" ? null : new string[0]);
            var throws = run(s => { throw new InvalidOperationException("cannot read"); });

            var issue = Assert.Single(changed.Issues);
            Assert.Equal(AuditIssueKind.ProtectedLinesChanged, issue.Kind);
            Assert.True(issue.Blocking);
            Assert.Contains("Target line 3 is protected", issue.Message);
            Assert.Equal(AuditIssueKind.ProtectedLinesChanged, Assert.Single(unknown.Issues).Kind);
            Assert.Equal(3, throws.Issues.Count(i => i.Kind == AuditIssueKind.ProtectedLinesChanged && i.Blocking));
            Assert.Contains("cannot read", throws.Issues[0].Message);
            Assert.StartsWith("Final check failed: 1 blocking problem (protected lines changed: 1)", changed.Summary);
        }

        [Fact]
        public void Policy_ProtectedLinesKept_SummaryCountsTheFiles()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.StepAction = s => s.RelativePath == "c.props" ? MergePolicyAction.Discard : MergePolicyAction.Merge;
            input.TargetContentPreserved = s => true;
            input.ProtectedLineViolations = s => new string[0];
            var checkedSteps = new List<string>();
            input.StepHasProtectedLines = s =>
            {
                checkedSteps.Add(s.RelativePath);
                return s.RelativePath == "a.config";
            };

            var result = TaskMergeAudit.Run(input);
            input.StepHasProtectedLines = null;
            var withoutFilter = TaskMergeAudit.Run(input);

            Assert.True(result.IsClean, result.Summary);
            // i Discard non si controllano per le righe protette: il contenuto e' tutto del target
            Assert.Equal(new[] { "a.config", "b.cs" }, checkedSteps.ToArray());
            Assert.EndsWith("3/3 steps merged; 1 discarded, 1 file with protected lines kept.", result.Summary);
            Assert.EndsWith("3/3 steps merged; 1 discarded, protected lines checked on 2 files.", withoutFilter.Summary);
        }

        [Fact]
        public void Policy_StepActionThatThrowsOrIsUnknown_Blocks()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var throwing = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            throwing.StepAction = s => { throw new InvalidOperationException("policy not loaded"); };
            var unknown = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            unknown.StepAction = s => (MergePolicyAction)99;

            var r1 = TaskMergeAudit.Run(throwing);
            var r2 = TaskMergeAudit.Run(unknown);

            Assert.False(r1.IsClean);
            Assert.Equal(3, r1.Issues.Count(i => i.Blocking && i.Kind == AuditIssueKind.StepNotMerged));
            Assert.Contains("policy not loaded", r1.Issues[0].Message);
            Assert.False(r2.IsClean);
            Assert.Contains("unknown action 99", r2.Issues[0].Message);
        }

        [Fact]
        public void Policy_WithoutPolicyCallbacks_BehavesAsBefore()
        {
            var w = ThreeFiles();
            var plan = w.Plan();
            var input = w.Audit(plan.Steps, w.PendingFor(plan.Steps));
            input.StepAction = s => MergePolicyAction.Merge;

            var result = TaskMergeAudit.Run(input);

            Assert.True(result.IsClean, result.Summary);
            Assert.Equal("Final check passed: 3 pending merges, all from task changesets C10-C20; 3/3 steps merged.", result.Summary);
        }

        // tre file esistenti nel target, un solo check-in
        private static World ThreeFiles()
        {
            return new World()
                .Change(10, "a.config", TaskChangeKind.Edit).Exists("a.config")
                .Change(10, "b.cs", TaskChangeKind.Edit).Exists("b.cs")
                .Change(20, "c.props", TaskChangeKind.Edit).Exists("c.props");
        }

        // ------------------------------------------------------------------------------------------
        // Supporto
        // ------------------------------------------------------------------------------------------

        private static TaskChangeInfo Change(int changesetId, string relativePath)
        {
            return new TaskChangeInfo(changesetId, Source + "/" + relativePath, TaskMergeItemKind.File, TaskChangeKind.Edit);
        }

        private static AuditPendingChange Merge(string targetItem, string sourceItem, int from, int to, string changeType = "Edit, Merge")
        {
            return new AuditPendingChange(targetItem, true, changeType, new[] { new AuditMergeSource(sourceItem, from, to, false) });
        }

        // Un piccolo mondo TFVC finto: changeset del task, item esistenti nel target, storia degli item.
        private sealed class World
        {
            // path sorgente -> tutti i changeset che toccano l'item (task + terzi)
            private readonly Dictionary<string, SortedSet<int>> _history = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public World()
            {
                TaskIds = new List<int>();
                Changes = new List<TaskChangeInfo>();
                HistoryCalls = new List<Tuple<string, int, int>>();
            }

            public List<int> TaskIds { get; }

            public List<TaskChangeInfo> Changes { get; }

            public List<Tuple<string, int, int>> HistoryCalls { get; }

            public World Change(int changesetId, string relativePath, TaskChangeKind kind, TaskMergeItemKind itemKind = TaskMergeItemKind.File)
            {
                if (!TaskIds.Contains(changesetId))
                    TaskIds.Add(changesetId);
                Changes.Add(new TaskChangeInfo(changesetId, Source + "/" + relativePath, itemKind, kind));
                History(Source + "/" + relativePath, changesetId);
                return this;
            }

            public World Exists(string relativePath)
            {
                _existing.Add(Target + "/" + relativePath);
                return this;
            }

            public World Third(string relativePath, params int[] changesetIds)
            {
                History(Source + "/" + relativePath, changesetIds);
                return this;
            }

            public void History(string sourceItem, params int[] changesetIds)
            {
                SortedSet<int> set;
                if (!_history.TryGetValue(sourceItem, out set))
                {
                    set = new SortedSet<int>();
                    _history.Add(sourceItem, set);
                }

                foreach (var id in changesetIds)
                    set.Add(id);
            }

            public TaskMergePlan Plan(IReadOnlyCollection<int> selected = null)
            {
                var ids = selected ?? TaskIds;
                return TaskMergePlanner.Build(new TaskMergePlanInput
                {
                    SourceBranch = Source,
                    TargetBranch = Target,
                    TaskChangesetIds = ids.ToList(),
                    Changes = Changes.Where(c => ids.Contains(c.ChangesetId)).ToList(),
                    TargetItemExists = item => _existing.Contains(item),
                    ThirdPartyChangesetsBetween = (item, a, b) => Touching(item, a + 1, b - 1).Where(id => !ids.Contains(id)).ToList()
                });
            }

            // pending "come li lascia il motore": un merge per passo, sull'intervallo del passo
            public List<AuditPendingChange> PendingFor(IEnumerable<TaskMergeStep> steps)
            {
                return steps.Select(s => Merge(s.TargetItem, s.SourceItem, s.FromChangesetId, s.ToChangesetId)).ToList();
            }

            public TaskMergeAuditInput Audit(IReadOnlyList<TaskMergeStep> steps, IReadOnlyList<AuditPendingChange> pending)
            {
                return new TaskMergeAuditInput
                {
                    Pending = pending,
                    SelectedChangesetIds = TaskIds.ToList(),
                    StepsToDeliver = steps,
                    ChangesetsTouchingSourceItem = (item, a, b) =>
                    {
                        HistoryCalls.Add(Tuple.Create(item, a, b));
                        return Touching(item, a, b);
                    },
                    StepIsMerged = s => true
                };
            }

            private List<int> Touching(string item, int from, int to)
            {
                SortedSet<int> set;
                if (!_history.TryGetValue(item.TrimEnd('/'), out set))
                    return new List<int>();
                return set.Where(id => id >= from && id <= to).ToList();
            }
        }
    }
}
