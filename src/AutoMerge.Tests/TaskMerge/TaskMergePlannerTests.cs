using System;
using System.Collections.Generic;
using System.Linq;
using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.TaskMerge
{
    public class TaskMergePlannerTests
    {
        private const string Source = "$/ContosoMain";
        private const string Target = "$/Build/ContosoMain/rel1.0_new";

        private const TaskChangeKind AddNew = TaskChangeKind.Add | TaskChangeKind.Edit | TaskChangeKind.Encoding;
        private const TaskChangeKind AddFolder = TaskChangeKind.Add | TaskChangeKind.Encoding;

        // ------------------------------------------------------------------------------------------
        // Parti e passi
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void NoMultiTouch_SinglePart_OneStepPerItem()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit).Exists("a.cs")
                .Change(20, "b.cs", AddNew)
                .Change(30, "c.cs", TaskChangeKind.Edit).Exists("c.cs");

            var plan = s.Build();

            AssertValid(plan);
            var part = Assert.Single(plan.Parts);
            Assert.Equal(new[] { 10, 20, 30 }, part.TaskChangesetIds.ToArray());
            Assert.Equal(10, part.FromChangesetId);
            Assert.Equal(30, part.ToChangesetId);
            Assert.Null(part.EndReason);
            Assert.Equal(new[] { "a.cs", "b.cs", "c.cs" }, plan.Steps.Select(x => x.RelativePath).ToArray());
            Assert.All(plan.Steps, x => Assert.Equal(x.FromChangesetId, x.ToChangesetId));
            Assert.Equal(0, plan.CheckpointCount);
        }

        [Fact]
        public void MultiTouchClean_ExistingItem_OneStepFromFirstToLast()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "b.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Single(plan.Parts);
            var step = plan.Steps.Single(x => x.RelativePath == "a.cs");
            Assert.Equal(10, step.FromChangesetId);
            Assert.Equal(30, step.ToChangesetId);
            Assert.Equal(new[] { 10, 30 }, step.TaskChangesetIds.ToArray());
            Assert.Empty(step.InterleavedThirdParty);
        }

        [Fact]
        public void Interleaved_ExistingItem_TwoPartsCutRightBeforeSecondChangeset()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "b.cs", TaskChangeKind.Edit)
                .Change(30, "c.cs", TaskChangeKind.Edit)
                .Change(40, "a.cs", TaskChangeKind.Edit)
                .Change(50, "d.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs").Exists("c.cs").Exists("d.cs")
                .Third("a.cs", 35);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(new[] { 10, 20, 30 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 40, 50 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Equal(1, plan.CheckpointCount);

            var aSteps = plan.Steps.Where(x => x.RelativePath == "a.cs").ToList();
            Assert.Equal(2, aSteps.Count);
            Assert.Equal(1, aSteps[0].Part);
            Assert.Equal(new[] { 10 }, aSteps[0].TaskChangesetIds.ToArray());
            Assert.Equal(2, aSteps[1].Part);
            Assert.Equal(new[] { 40 }, aSteps[1].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 35 }, aSteps[1].InterleavedThirdParty.ToArray());
        }

        [Fact]
        public void TwoInterleavedItems_OverlappingIntervals_OneCut()
        {
            // a: (10,30) con terzo 15; b: (20,40) con terzo 35 -> un solo taglio prima di 30
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "b.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(40, "b.cs", TaskChangeKind.Edit)
                .Change(50, "c.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs").Exists("c.cs")
                .Third("a.cs", 15)
                .Third("b.cs", 35);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(new[] { 10, 20 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 30, 40, 50 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Contains("a.cs", plan.Parts[0].EndReason);
            Assert.Contains("C30", plan.Parts[0].EndReason);
        }

        [Fact]
        public void TwoInterleavedItems_DisjointIntervals_TwoCuts()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "a.cs", TaskChangeKind.Edit)
                .Change(30, "b.cs", TaskChangeKind.Edit)
                .Change(40, "b.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs")
                .Third("a.cs", 15)
                .Third("b.cs", 35);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(3, plan.Parts.Count);
            Assert.Equal(2, plan.CheckpointCount);
            Assert.Equal(new[] { 10 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 20, 30 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 40 }, plan.Parts[2].TaskChangesetIds.ToArray());
            Assert.Contains("C20", plan.Parts[0].EndReason);
            Assert.Contains("C40", plan.Parts[1].EndReason);
            Assert.Null(plan.Parts[2].EndReason);
        }

        // Anche per un item NUOVO nel target ogni coppia interleaved ha un check-in in mezzo: che TFVC
        // accetti un secondo merge non contiguo su un Branch in sospeso e' verificato, cosa ci metta
        // dentro no. Dopo il check-in il merge successivo e' un merge normale su un item esistente.
        [Fact]
        public void Interleaved_NewItem_CheckInBetweenEveryInterleavedPair()
        {
            var s = new Scenario()
                .Change(10, "n.cs", AddNew)
                .Change(30, "n.cs", TaskChangeKind.Edit)
                .Change(50, "n.cs", TaskChangeKind.Edit)
                .Third("n.cs", 20, 40);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(3, plan.Parts.Count);
            Assert.Equal(2, plan.CheckpointCount);
            Assert.Equal(new[] { 10, 30, 50 }, plan.Steps.Select(x => x.FromChangesetId).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, plan.Steps.Select(x => x.Part).ToArray());
            Assert.All(plan.Steps, x => Assert.False(x.TargetExists));
            Assert.All(plan.Steps, x => Assert.Equal(new[] { 20, 40 }, x.InterleavedThirdParty.ToArray()));
            Assert.Contains("n.cs", plan.Parts[0].EndReason);
            Assert.Contains("C30", plan.Parts[0].EndReason);
            Assert.Contains("C50", plan.Parts[1].EndReason);
        }

        [Fact]
        public void NewItem_PartiallyInterleaved_CheckInOnlyWhereTheThirdPartyIs()
        {
            var s = new Scenario()
                .Change(10, "n.cs", AddNew)
                .Change(20, "n.cs", TaskChangeKind.Edit)
                .Change(30, "n.cs", TaskChangeKind.Edit)
                .Change(40, "n.cs", TaskChangeKind.Edit)
                .Third("n.cs", 25);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(new[] { 10, 20 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 30, 40 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Equal(2, plan.Steps.Count);
            Assert.Equal(new[] { 10, 20 }, plan.Steps[0].TaskChangesetIds.ToArray());
            Assert.Equal(1, plan.Steps[0].Part);
            Assert.Equal(new[] { 30, 40 }, plan.Steps[1].TaskChangesetIds.ToArray());
            Assert.Equal(2, plan.Steps[1].Part);
        }

        // Invariante: nessun item ha piu' di un passo nella stessa parte, qualunque sia la storia.
        [Fact]
        public void EveryItem_AtMostOneStepPerPart()
        {
            var s = new Scenario()
                .Change(10, "n.cs", AddNew)
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "n.cs", TaskChangeKind.Edit)
                .Change(20, "b.cs", AddNew)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(30, "b.cs", TaskChangeKind.Edit)
                .Change(40, "n.cs", TaskChangeKind.Edit)
                .Change(40, "b.cs", TaskChangeKind.Edit)
                .Exists("a.cs")
                .Third("n.cs", 15, 35)
                .Third("b.cs", 25)
                .Third("a.cs", 12);

            var plan = s.Build();

            AssertValid(plan);
            Assert.All(plan.Steps.GroupBy(x => x.RelativePath), g =>
                Assert.Equal(g.Count(), g.Select(x => x.Part).Distinct().Count()));
            // n.cs: (10,20) e (20,40) interleaved -> tre passi in tre parti
            Assert.Equal(3, plan.Steps.Count(x => x.RelativePath == "n.cs"));
        }

        [Fact]
        public void CleanItem_TouchedInBothParts_OneStepPerPart()
        {
            // c.cs [10,40] senza terzi, ma il taglio prima di 30 (per a.cs) lo divide
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(10, "c.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(40, "c.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("c.cs")
                .Third("a.cs", 20);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            var cSteps = plan.Steps.Where(x => x.RelativePath == "c.cs").ToList();
            Assert.Equal(2, cSteps.Count);
            Assert.Equal(1, cSteps[0].Part);
            Assert.Equal(new[] { 10 }, cSteps[0].TaskChangesetIds.ToArray());
            Assert.Equal(2, cSteps[1].Part);
            Assert.Equal(new[] { 40 }, cSteps[1].TaskChangesetIds.ToArray());
            Assert.All(cSteps, x => Assert.Empty(x.InterleavedThirdParty));
        }

        [Fact]
        public void InterleavedOnEveryPair_ExistingItem_OneStepPerPart_AllThirdsReported()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "a.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Exists("a.cs")
                .Third("a.cs", 15, 25);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(3, plan.Parts.Count);
            Assert.Equal(new[] { 1, 2, 3 }, plan.Steps.Select(x => x.Part).ToArray());
            Assert.All(plan.Steps, x => Assert.Equal(new[] { 15, 25 }, x.InterleavedThirdParty.ToArray()));
        }

        [Fact]
        public void NewItem_CheckedInByEarlierPart_LaterInterleavedPairNeedsACut()
        {
            // a.cs esistente impone il taglio prima di 30; n.cs (nuovo, aggiunto in 20) dopo il
            // check-in esiste nel target: la coppia (40,50) con un terzo in mezzo chiede un altro taglio
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "n.cs", AddNew)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(40, "n.cs", TaskChangeKind.Edit)
                .Change(50, "n.cs", TaskChangeKind.Edit)
                .Exists("a.cs")
                .Third("a.cs", 15)
                .Third("n.cs", 45);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(3, plan.Parts.Count);
            Assert.Equal(new[] { 10, 20 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 30, 40 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 50 }, plan.Parts[2].TaskChangesetIds.ToArray());
            Assert.Contains("n.cs", plan.Parts[1].EndReason);
        }

        [Fact]
        public void NewItem_FirstMergedAfterTheCut_InterleavedPairGetsItsOwnCut()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(40, "n.cs", AddNew)
                .Change(50, "n.cs", TaskChangeKind.Edit)
                .Exists("a.cs")
                .Third("a.cs", 15)
                .Third("n.cs", 45);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(3, plan.Parts.Count);
            Assert.Equal(new[] { 10 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 30, 40 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 50 }, plan.Parts[2].TaskChangesetIds.ToArray());
            var nSteps = plan.Steps.Where(x => x.RelativePath == "n.cs").ToList();
            Assert.Equal(new[] { 2, 3 }, nSteps.Select(x => x.Part).ToArray());
        }

        // Un solo taglio basta per due coppie interleaved che si sovrappongono, anche se una e' di un
        // item nuovo nel target (numero minimo di check-in).
        [Fact]
        public void NewAndExistingInterleavedPairs_Overlapping_OneCut()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "n.cs", AddNew)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(40, "n.cs", TaskChangeKind.Edit)
                .Exists("a.cs")
                .Third("a.cs", 25)
                .Third("n.cs", 25);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(new[] { 10, 20 }, plan.Parts[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 30, 40 }, plan.Parts[1].TaskChangesetIds.ToArray());
        }

        [Fact]
        public void DeletedFolder_ExistingDescendantChangedEarlier_NeedsACutBeforeTheDelete()
        {
            var s = new Scenario()
                .Change(10, "Old/a.cs", TaskChangeKind.Edit)
                .Change(20, "x.cs", TaskChangeKind.Edit)
                .Change(30, "Old", TaskChangeKind.Delete, TaskMergeItemKind.Folder)
                .Change(30, "Old/a.cs", TaskChangeKind.Delete)
                .Change(30, "Old/b.cs", TaskChangeKind.Delete)
                .Exists("Old").Exists("Old/a.cs").Exists("Old/b.cs").Exists("x.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(new[] { 30 }, plan.Parts[1].TaskChangesetIds.ToArray());
            Assert.Contains("Old", plan.Parts[0].EndReason);
            Assert.Equal(
                new[] { "Old/a.cs", "Old/b.cs", "Old" },
                plan.Parts[1].Steps.Select(x => x.RelativePath).ToArray());
            Assert.Equal(TaskMergeStepRecursion.Full, plan.Parts[1].Steps.Last().Recursion);
        }

        [Fact]
        public void DeletedFolder_NewDescendant_NoCut()
        {
            var s = new Scenario()
                .Change(10, "Old/n.cs", AddNew)
                .Change(30, "Old", TaskChangeKind.Delete, TaskMergeItemKind.Folder)
                .Change(30, "Old/n.cs", TaskChangeKind.Delete)
                .Exists("Old");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Single(plan.Parts);
            Assert.Equal(new[] { "Old/n.cs", "Old" }, plan.Steps.Select(x => x.RelativePath).ToArray());
            Assert.Equal(new[] { 10, 30 }, plan.Steps[0].TaskChangesetIds.ToArray());
        }

        // ------------------------------------------------------------------------------------------
        // Ordine, ricorsione, ChangeKind
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Ordering_AddedFoldersByDepth_ThenFilesByPath_ThenDeletedFoldersByDepthDescending()
        {
            var s = new Scenario()
                .Change(10, "z.cs", TaskChangeKind.Edit)
                .Change(10, "D/E", TaskChangeKind.Delete, TaskMergeItemKind.Folder)
                .Change(10, "A/B", AddFolder, TaskMergeItemKind.Folder)
                .Change(10, "A/B/x.cs", AddNew)
                .Change(10, "D", TaskChangeKind.Delete, TaskMergeItemKind.Folder)
                .Change(10, "C", AddFolder, TaskMergeItemKind.Folder)
                .Change(10, "A/a.cs", AddNew)
                .Change(10, "A", AddFolder, TaskMergeItemKind.Folder)
                .Exists("z.cs").Exists("D").Exists("D/E");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(
                new[] { "A", "C", "A/B", "A/a.cs", "A/B/x.cs", "z.cs", "D/E", "D" },
                plan.Steps.Select(x => x.RelativePath).ToArray());
            Assert.Equal(Enumerable.Range(1, 8).ToArray(), plan.Steps.Select(x => x.Number).ToArray());
        }

        [Fact]
        public void Recursion_OnlyDeletedFoldersAreFull()
        {
            var s = new Scenario()
                .Change(10, "Added", AddFolder, TaskMergeItemKind.Folder)
                .Change(10, "Props", TaskChangeKind.Property, TaskMergeItemKind.Folder)
                .Change(10, "Gone", TaskChangeKind.Delete, TaskMergeItemKind.Folder)
                .Change(10, "gone.cs", TaskChangeKind.Delete)
                .Change(10, "edit.cs", TaskChangeKind.Edit)
                .Exists("Props").Exists("Gone").Exists("gone.cs").Exists("edit.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(TaskMergeStepRecursion.None, Step(plan, "Added").Recursion);
            Assert.Equal(TaskMergeStepRecursion.None, Step(plan, "Props").Recursion);
            Assert.Equal(TaskMergeStepRecursion.Full, Step(plan, "Gone").Recursion);
            Assert.Equal(TaskMergeStepRecursion.None, Step(plan, "gone.cs").Recursion);
            Assert.Equal(TaskMergeStepRecursion.None, Step(plan, "edit.cs").Recursion);
            Assert.Equal(TaskMergeItemKind.Folder, Step(plan, "Props").Kind);
            Assert.Equal(TaskMergeItemKind.File, Step(plan, "edit.cs").Kind);
            // la cartella solo-proprieta' va con quelle "aggiunte" (in testa), quella cancellata in coda
            Assert.Equal("Gone", plan.Steps.Last().RelativePath);
            Assert.Equal(TaskMergeItemKind.Folder, plan.Steps[0].Kind);
        }

        [Fact]
        public void StepChangeKind_IsTheOrOfTheTaskChangesInTheStep()
        {
            var s = new Scenario()
                .Change(10, "n.cs", AddNew)
                .Change(20, "n.cs", TaskChangeKind.Edit | TaskChangeKind.Property);

            var plan = s.Build();

            AssertValid(plan);
            var step = Assert.Single(plan.Steps);
            Assert.Equal(AddNew | TaskChangeKind.Property, step.ChangeKind);
        }

        // ------------------------------------------------------------------------------------------
        // Path
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Mapping_CaseInsensitivePrefix_TrailingSlashes()
        {
            var existsCalls = new List<string>();
            var s = new Scenario
            {
                SourceBranch = "$/contosomain/",
                TargetBranch = "$/Build/ContosoMain/rel1.0_new/"
            };
            s.Changes.Add(new TaskChangeInfo(10, "$/ContosoMain/Web/Foo.cs", TaskMergeItemKind.File, TaskChangeKind.Edit));
            s.Changes.Add(new TaskChangeInfo(10, "$/CONTOSOMAIN/Web/New/", TaskMergeItemKind.Folder, AddFolder));
            s.TaskIds.Add(10);
            s.OnExists = existsCalls.Add;
            s.ExistingInTarget.Add(Target + "/Web/Foo.cs");
            s.ExistingInTarget.Add(Target + "/Web");

            var plan = s.Build();

            AssertValid(plan);
            var file = Step(plan, "Web/Foo.cs");
            Assert.Equal("$/ContosoMain/Web/Foo.cs", file.SourceItem);
            Assert.Equal("$/Build/ContosoMain/rel1.0_new/Web/Foo.cs", file.TargetItem);
            var folder = Step(plan, "Web/New");
            Assert.Equal("$/CONTOSOMAIN/Web/New", folder.SourceItem);
            Assert.Equal("$/Build/ContosoMain/rel1.0_new/Web/New", folder.TargetItem);
            Assert.Contains("$/Build/ContosoMain/rel1.0_new/Web/Foo.cs", existsCalls);
            Assert.Contains("$/Build/ContosoMain/rel1.0_new/Web/New", existsCalls);
        }

        [Fact]
        public void RelativePath_TargetItem_AndTargetExistsPerItem()
        {
            var s = new Scenario()
                .Change(10, "Web/Contoso.Web.Rest/Auth/authorizations.xml", TaskChangeKind.Edit)
                .Change(10, "vNext/Utilities/Program.cs", AddNew)
                .Exists("Web/Contoso.Web.Rest/Auth/authorizations.xml")
                .Exists("vNext/Utilities");

            var plan = s.Build();

            AssertValid(plan);
            var existing = Step(plan, "Web/Contoso.Web.Rest/Auth/authorizations.xml");
            Assert.Equal(Target + "/Web/Contoso.Web.Rest/Auth/authorizations.xml", existing.TargetItem);
            Assert.Equal(Source + "/Web/Contoso.Web.Rest/Auth/authorizations.xml", existing.SourceItem);
            Assert.True(existing.TargetExists);
            Assert.False(Step(plan, "vNext/Utilities/Program.cs").TargetExists);
        }

        [Fact]
        public void SameItemWithDifferentCasing_IsOneItem_LatestCasingWins()
        {
            var s = new Scenario()
                .Change(10, "web/foo.cs", TaskChangeKind.Edit)
                .Change(20, "Web/Foo.cs", TaskChangeKind.Edit)
                .Exists("Web/Foo.cs");

            var plan = s.Build();

            AssertValid(plan);
            var step = Assert.Single(plan.Steps);
            Assert.Equal("Web/Foo.cs", step.RelativePath);
            Assert.Equal(new[] { 10, 20 }, step.TaskChangesetIds.ToArray());
            Assert.Single(s.HistoryCalls);
        }

        [Theory]
        [InlineData("$/Other/x.cs")]
        [InlineData("$/ContosoMainOther/x.cs")]
        [InlineData("$/Test")]
        public void ItemOutsideTheSourceBranch_IsAPlanError(string item)
        {
            var s = new Scenario().Change(10, "ok.cs", TaskChangeKind.Edit);
            s.Changes.Add(new TaskChangeInfo(10, item, TaskMergeItemKind.File, TaskChangeKind.Edit));

            var plan = s.Build();

            Assert.False(plan.IsValid);
            var error = Assert.Single(plan.Errors);
            Assert.Contains(item, error);
            Assert.Empty(plan.Parts);
            Assert.Empty(plan.Steps);
            Assert.Equal(0, plan.CheckpointCount);
            Assert.Empty(s.ExistsCalls);
            Assert.Empty(s.HistoryCalls);
        }

        // ------------------------------------------------------------------------------------------
        // Cambi supportati / non supportati e controlli strutturali
        // ------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(TaskChangeKind.Rename)]
        [InlineData(TaskChangeKind.SourceRename)]
        [InlineData(TaskChangeKind.Undelete)]
        [InlineData(TaskChangeKind.Branch)]
        [InlineData(TaskChangeKind.Merge)]
        [InlineData(TaskChangeKind.Rollback)]
        [InlineData(TaskChangeKind.Other)]
        [InlineData(TaskChangeKind.Edit | TaskChangeKind.Rename)]
        [InlineData(TaskChangeKind.Branch | TaskChangeKind.Merge | TaskChangeKind.Encoding)]
        [InlineData(TaskChangeKind.None)]
        public void UnsupportedChange_IsAPlanError(TaskChangeKind kind)
        {
            var s = new Scenario()
                .Change(10, "ok.cs", TaskChangeKind.Edit)
                .Change(20, "bad.cs", kind);

            var plan = s.Build();

            Assert.False(plan.IsValid);
            var error = Assert.Single(plan.Errors);
            Assert.Contains("C20", error);
            Assert.Contains("bad.cs", error);
            Assert.Empty(plan.Steps);
        }

        [Fact]
        public void UnsupportedChanges_AreAllListed()
        {
            var s = new Scenario()
                .Change(10, "r.cs", TaskChangeKind.Rename)
                .Change(20, "m.cs", TaskChangeKind.Merge | TaskChangeKind.Edit)
                .Change(30, "u.cs", TaskChangeKind.Undelete);

            var plan = s.Build();

            Assert.Equal(3, plan.Errors.Count);
            Assert.Contains(plan.Errors, e => e.Contains("r.cs") && e.Contains("Rename"));
            Assert.Contains(plan.Errors, e => e.Contains("m.cs") && e.Contains("Merge"));
            Assert.Contains(plan.Errors, e => e.Contains("u.cs") && e.Contains("Undelete"));
        }

        [Theory]
        [InlineData(TaskChangeKind.Add | TaskChangeKind.Edit | TaskChangeKind.Encoding)]
        [InlineData(TaskChangeKind.Edit)]
        [InlineData(TaskChangeKind.Delete)]
        [InlineData(TaskChangeKind.Encoding)]
        [InlineData(TaskChangeKind.Property)]
        [InlineData(TaskChangeKind.Edit | TaskChangeKind.Property | TaskChangeKind.Encoding)]
        public void SupportedChange_IsAccepted(TaskChangeKind kind)
        {
            var s = new Scenario().Change(10, "f.cs", kind).Exists("f.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(kind, Assert.Single(plan.Steps).ChangeKind);
        }

        [Fact]
        public void DeleteFollowedByAnotherChange_IsAPlanError()
        {
            var s = new Scenario()
                .Change(10, "f.cs", TaskChangeKind.Delete)
                .Change(20, "f.cs", AddNew)
                .Exists("f.cs");

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Contains("C10", Assert.Single(plan.Errors));
        }

        [Fact]
        public void DeletedFolderAlsoChangedByAnotherChangeset_IsAPlanError()
        {
            var s = new Scenario()
                .Change(10, "F", TaskChangeKind.Property, TaskMergeItemKind.Folder)
                .Change(20, "F", TaskChangeKind.Delete, TaskMergeItemKind.Folder)
                .Exists("F");

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Single(plan.Errors);
        }

        [Fact]
        public void SamePathAsFileAndFolder_IsAPlanError()
        {
            var s = new Scenario()
                .Change(10, "X", TaskChangeKind.Edit)
                .Change(20, "X", TaskChangeKind.Property, TaskMergeItemKind.Folder);

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Contains("file and a folder", Assert.Single(plan.Errors));
        }

        [Fact]
        public void DeletingTheSourceBranchRoot_IsAPlanError()
        {
            var s = new Scenario();
            s.TaskIds.Add(10);
            s.Changes.Add(new TaskChangeInfo(10, Source, TaskMergeItemKind.Folder, TaskChangeKind.Delete));

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Empty(plan.Steps);
        }

        [Fact]
        public void PropertyOnTheSourceBranchRoot_MapsToTheTargetRoot()
        {
            var s = new Scenario();
            s.TaskIds.Add(10);
            s.Changes.Add(new TaskChangeInfo(10, Source + "/", TaskMergeItemKind.Folder, TaskChangeKind.Property));
            s.ExistingInTarget.Add(Target);

            var plan = s.Build();

            AssertValid(plan);
            var step = Assert.Single(plan.Steps);
            Assert.Equal(".", step.RelativePath);
            Assert.Equal(Target, step.TargetItem);
            Assert.True(step.TargetExists);
        }

        [Fact]
        public void ChangeOfAChangesetThatIsNotInTheTask_IsAPlanError()
        {
            var s = new Scenario().Change(10, "a.cs", TaskChangeKind.Edit);
            s.Changes.Add(new TaskChangeInfo(99, Source + "/b.cs", TaskMergeItemKind.File, TaskChangeKind.Edit));

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Contains("C99", Assert.Single(plan.Errors));
        }

        [Fact]
        public void NoTaskChangesets_IsAPlanError()
        {
            var plan = new Scenario().Build();

            Assert.False(plan.IsValid);
            Assert.Empty(plan.Parts);
        }

        [Fact]
        public void SameSourceAndTargetBranch_IsAPlanError()
        {
            var s = new Scenario { TargetBranch = "$/contosomain/" }.Change(10, "a.cs", TaskChangeKind.Edit);

            var plan = s.Build();

            Assert.False(plan.IsValid);
        }

        [Fact]
        public void MissingCallbacksOrInput_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => TaskMergePlanner.Build(null));
            Assert.Throws<ArgumentException>(() => TaskMergePlanner.Build(new TaskMergePlanInput
            {
                SourceBranch = Source,
                TargetBranch = Target,
                TaskChangesetIds = new[] { 10 },
                Changes = new TaskChangeInfo[0],
                ThirdPartyChangesetsBetween = (i, a, b) => new int[0]
            }));
            Assert.Throws<ArgumentException>(() => TaskMergePlanner.Build(new TaskMergePlanInput
            {
                SourceBranch = Source,
                TargetBranch = Target,
                TaskChangesetIds = new[] { 10 },
                Changes = new TaskChangeInfo[0],
                TargetItemExists = x => true
            }));
        }

        // ------------------------------------------------------------------------------------------
        // Storia (ThirdPartyChangesetsBetween)
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void History_IsQueriedOnlyForConsecutivePairsOfTheSameItem()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "b.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Change(40, "c.cs", TaskChangeKind.Edit)
                .Change(50, "a.cs", TaskChangeKind.Edit)
                .Change(50, "b.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs").Exists("c.cs");

            s.Build();

            var calls = s.HistoryCalls.Select(c => c.Item1.Substring(Source.Length + 1) + ":" + c.Item2 + "-" + c.Item3)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "a.cs:10-30", "a.cs:30-50", "b.cs:20-50" }, calls);
        }

        [Fact]
        public void History_ResultIsFilteredToThirdPartiesStrictlyBetween()
        {
            // il callback "sbaglia": restituisce estremi, changeset del task e changeset fuori intervallo
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "b.cs", TaskChangeKind.Edit)
                .Change(30, "a.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs");
            s.RawHistory = (item, a, b) => new[] { 5, 10, 20, 30, 40 };

            var plan = s.Build();

            AssertValid(plan);
            Assert.Single(plan.Parts);
            var step = Step(plan, "a.cs");
            Assert.Equal(new[] { 10, 30 }, step.TaskChangesetIds.ToArray());
            Assert.Empty(step.InterleavedThirdParty);
        }

        [Fact]
        public void History_NullResult_IsAPlanError()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(20, "a.cs", TaskChangeKind.Edit)
                .Exists("a.cs");
            s.RawHistory = (item, a, b) => null;

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Empty(plan.Steps);
        }

        // ------------------------------------------------------------------------------------------
        // Input disordinato, changeset senza cambi, determinismo
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void TaskChangesets_UnsortedAndDuplicated_AreSortedAndDeduplicated()
        {
            var s = new Scenario();
            s.TaskIds.AddRange(new[] { 30, 10, 20, 10 });
            s.Changes.Add(new TaskChangeInfo(30, Source + "/a.cs", TaskMergeItemKind.File, TaskChangeKind.Edit));
            s.Changes.Add(new TaskChangeInfo(10, Source + "/a.cs", TaskMergeItemKind.File, TaskChangeKind.Edit));
            s.Changes.Add(new TaskChangeInfo(20, Source + "/b.cs", TaskMergeItemKind.File, TaskChangeKind.Edit));
            s.ExistingInTarget.Add(Target + "/a.cs");
            s.ExistingInTarget.Add(Target + "/b.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(new[] { 10, 20, 30 }, plan.Parts[0].TaskChangesetIds.ToArray());
            var step = Step(plan, "a.cs");
            Assert.Equal(10, step.FromChangesetId);
            Assert.Equal(30, step.ToChangesetId);
            Assert.Equal("$/ContosoMain/a.cs:10-30", s.HistoryCalls.Select(c => c.Item1 + ":" + c.Item2 + "-" + c.Item3).Single());
        }

        [Fact]
        public void TaskChangesetWithoutChanges_IsAWarning_AndStaysInThePartWindow()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(30, "b.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs");
            s.TaskIds.Add(20);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Contains("C20", Assert.Single(plan.Warnings));
            Assert.Equal(new[] { 10, 20, 30 }, Assert.Single(plan.Parts).TaskChangesetIds.ToArray());
            Assert.Equal(2, plan.Steps.Count);
        }

        [Fact]
        public void Plan_IsDeterministic_RegardlessOfTheOrderOfTheChanges()
        {
            Func<bool, Scenario> make = reversed =>
            {
                var s = new Scenario()
                    .Change(10, "a.cs", TaskChangeKind.Edit)
                    .Change(10, "F", AddFolder, TaskMergeItemKind.Folder)
                    .Change(10, "F/n.cs", AddNew)
                    .Change(20, "b.cs", TaskChangeKind.Edit)
                    .Change(30, "a.cs", TaskChangeKind.Edit)
                    .Change(30, "F/n.cs", TaskChangeKind.Edit)
                    .Change(40, "b.cs", TaskChangeKind.Edit)
                    .Exists("a.cs").Exists("b.cs")
                    .Third("a.cs", 15).Third("b.cs", 35).Third("F/n.cs", 25);
                if (reversed)
                    s.Changes.Reverse();
                return s;
            };

            var first = Describe(make(false).Build());
            var second = Describe(make(true).Build());
            var third = Describe(make(false).Build());

            Assert.Equal(first, second);
            Assert.Equal(first, third);
        }

        [Fact]
        public void StepsAndParts_AreConsistentlyNumbered()
        {
            var s = new Scenario()
                .Change(10, "a.cs", TaskChangeKind.Edit)
                .Change(10, "b.cs", TaskChangeKind.Edit)
                .Change(20, "a.cs", TaskChangeKind.Edit)
                .Change(20, "c.cs", AddNew)
                .Change(30, "b.cs", TaskChangeKind.Edit)
                .Exists("a.cs").Exists("b.cs")
                .Third("a.cs", 15).Third("b.cs", 25);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(plan.Parts.SelectMany(p => p.Steps).ToArray(), plan.Steps.ToArray());
            Assert.Equal(Enumerable.Range(1, plan.Steps.Count).ToArray(), plan.Steps.Select(x => x.Number).ToArray());
            for (var i = 0; i < plan.Parts.Count; i++)
            {
                Assert.Equal(i + 1, plan.Parts[i].Number);
                Assert.All(plan.Parts[i].Steps, x => Assert.Equal(i + 1, x.Part));
                Assert.All(plan.Parts[i].Steps, x => Assert.InRange(x.FromChangesetId, plan.Parts[i].FromChangesetId, plan.Parts[i].ToChangesetId));
                Assert.All(plan.Parts[i].Steps, x => Assert.InRange(x.ToChangesetId, plan.Parts[i].FromChangesetId, plan.Parts[i].ToChangesetId));
                if (i < plan.Parts.Count - 1)
                    Assert.False(string.IsNullOrEmpty(plan.Parts[i].EndReason));
                else
                    Assert.Null(plan.Parts[i].EndReason);
            }
            Assert.Equal(plan.Parts.Count - 1, plan.CheckpointCount);
        }

        // ------------------------------------------------------------------------------------------
        // Scenario reale: work item 128458 in piccolo
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void RealScenario128458_TwoParts_CutBefore162933()
        {
            const string restCsproj = "Web/Contoso.Web.Rest/Contoso.Web.Rest.csproj";
            const string restPackages = "Web/Contoso.Web.Rest/packages.config";
            const string authorizations = "Web/Contoso.Web.Rest/Auth/authorizations.xml";
            const string utilitiesFolder = "vNext/Utilities";
            const string newFile = "vNext/Utilities/Authentication/SE.Utilities.Authentication.Infrastructure/Extensions/OAuthInfrastructureExtensions.cs";
            // cartella gia' nel target (creata da C164097 come semplice Add, senza legame di merge)
            const string oauthFolder = "Web/Contoso.Web.Rest/Controllers/OAuth";
            const string oauthController = oauthFolder + "/OAuthController.cs";
            var task = new[]
            {
                162095, 162108, 162112, 162114, 162115, 162864, 162866, 162872, 162903, 162906,
                162928, 162931, 162933, 162934, 162960, 162975, 162978, 162980, 163661
            };
            var thirds = new[] { 162213, 162222, 162290, 162650, 162832, 162892, 162910 };

            var s = new Scenario();
            foreach (var id in task)
                s.Change(id, "Other/Single" + id + ".cs", TaskChangeKind.Edit).Exists("Other/Single" + id + ".cs");
            foreach (var id in new[] { 162108, 162115, 162933, 162978 })
            {
                s.Change(id, restCsproj, TaskChangeKind.Edit);
                s.Change(id, restPackages, TaskChangeKind.Edit);
            }
            s.Change(162108, authorizations, TaskChangeKind.Edit)
                .Change(162933, authorizations, TaskChangeKind.Edit)
                .Change(162095, utilitiesFolder, AddFolder, TaskMergeItemKind.Folder)
                .Change(162095, "vNext/Utilities/Authentication", AddFolder, TaskMergeItemKind.Folder)
                .Change(162095, "vNext/Utilities/Authentication/SE.Utilities.Authentication.Infrastructure", AddFolder, TaskMergeItemKind.Folder)
                .Change(162095, "vNext/Utilities/Authentication/SE.Utilities.Authentication.Infrastructure/Extensions", AddFolder, TaskMergeItemKind.Folder)
                .Change(162095, newFile, AddNew)
                .Change(162114, newFile, TaskChangeKind.Edit)
                .Change(162872, newFile, TaskChangeKind.Edit)
                .Change(162108, oauthFolder, AddFolder, TaskMergeItemKind.Folder)
                .Change(162108, oauthController, AddNew)
                .Change(162115, oauthController, TaskChangeKind.Edit)
                .Change(162933, oauthController, TaskChangeKind.Edit)
                .Change(162934, oauthController, TaskChangeKind.Edit)
                .Change(162978, oauthController, TaskChangeKind.Edit)
                .Exists(restCsproj).Exists(restPackages).Exists(authorizations)
                .Exists("vNext").Exists("Web/Contoso.Web.Rest/Controllers")
                .Exists(oauthFolder).Exists(oauthController)
                .Third(restCsproj, thirds)
                .Third(restPackages, thirds);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(2, plan.Parts.Count);
            Assert.Equal(1, plan.CheckpointCount);
            Assert.Equal(162095, plan.Parts[0].FromChangesetId);
            Assert.Equal(162931, plan.Parts[0].ToChangesetId);
            Assert.Equal(12, plan.Parts[0].TaskChangesetIds.Count);
            Assert.Equal(162933, plan.Parts[1].FromChangesetId);
            Assert.Equal(163661, plan.Parts[1].ToChangesetId);
            Assert.Equal(7, plan.Parts[1].TaskChangesetIds.Count);
            Assert.Contains("C162933", plan.Parts[0].EndReason);
            Assert.Contains(restCsproj, plan.Parts[0].EndReason);
            Assert.Contains(restPackages, plan.Parts[0].EndReason);
            Assert.DoesNotContain(authorizations, plan.Parts[0].EndReason);

            foreach (var rest in new[] { restCsproj, restPackages })
            {
                var steps = plan.Steps.Where(x => x.RelativePath == rest).ToList();
                Assert.Equal(2, steps.Count);
                Assert.Equal(1, steps[0].Part);
                Assert.Equal(new[] { 162108, 162115 }, steps[0].TaskChangesetIds.ToArray());
                Assert.Equal(2, steps[1].Part);
                Assert.Equal(new[] { 162933, 162978 }, steps[1].TaskChangesetIds.ToArray());
                Assert.All(steps, x => Assert.Equal(thirds, x.InterleavedThirdParty.ToArray()));
                Assert.All(steps, x => Assert.True(x.TargetExists));
            }

            var auth = plan.Steps.Where(x => x.RelativePath == authorizations).ToList();
            Assert.Equal(2, auth.Count);
            Assert.Equal(new[] { 1, 2 }, auth.Select(x => x.Part).ToArray());
            Assert.Equal(new[] { 162108 }, auth[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 162933 }, auth[1].TaskChangesetIds.ToArray());
            Assert.All(auth, x => Assert.Empty(x.InterleavedThirdParty));

            // file nuovo nel target toccato piu' volte, senza colleghi in mezzo: un solo passo
            var created = Step(plan, newFile);
            Assert.Equal(1, created.Part);
            Assert.False(created.TargetExists);
            Assert.Equal(new[] { 162095, 162114, 162872 }, created.TaskChangesetIds.ToArray());
            Assert.Equal(AddNew, created.ChangeKind);

            // la cartella OAuth esiste gia' nel target: nessun passo (il suo merge darebbe
            // NoMergeRelationshipException), un avviso; il file dentro ha un passo per parte
            Assert.DoesNotContain(plan.Steps, x => x.RelativePath == oauthFolder);
            var warning = Assert.Single(plan.Warnings);
            Assert.Contains(oauthFolder, warning);
            Assert.Contains("nothing to merge", warning);
            var controller = plan.Steps.Where(x => x.RelativePath == oauthController).ToList();
            Assert.Equal(new[] { 1, 2 }, controller.Select(x => x.Part).ToArray());
            Assert.Equal(new[] { 162108, 162115 }, controller[0].TaskChangesetIds.ToArray());
            Assert.Equal(new[] { 162933, 162934, 162978 }, controller[1].TaskChangesetIds.ToArray());

            // la cartella aggiunta apre la parte 1, con ricorsione None
            Assert.Equal(utilitiesFolder, plan.Parts[0].Steps[0].RelativePath);
            Assert.Equal(TaskMergeStepRecursion.None, plan.Parts[0].Steps[0].Recursion);

            // storia letta solo per le coppie consecutive degli item multi-touch
            Assert.Equal(3 + 3 + 1 + 2 + 4, s.HistoryCalls.Count);
            Assert.Contains(s.HistoryCalls, c => c.Item1 == Source + "/" + restCsproj && c.Item2 == 162115 && c.Item3 == 162933);
            Assert.Contains(s.HistoryCalls, c => c.Item1 == Source + "/" + authorizations && c.Item2 == 162108 && c.Item3 == 162933);
        }

        // ------------------------------------------------------------------------------------------
        // Controlli sul target: cartelle gia' presenti, item non creati dal task, cartelle madri
        // ------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(TaskChangeKind.Add)]
        [InlineData(TaskChangeKind.Add | TaskChangeKind.Encoding)]
        public void ExistingFolder_OnlyAddedByTheTask_NoStep_Warning(TaskChangeKind kind)
        {
            var s = new Scenario()
                .Change(10, "F", kind, TaskMergeItemKind.Folder)
                .Change(10, "F/x.cs", AddNew)
                .Change(20, "a.cs", TaskChangeKind.Edit)
                .Exists("F").Exists("a.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.DoesNotContain(plan.Steps, x => x.RelativePath == "F");
            Assert.Equal(new[] { "a.cs", "F/x.cs" }, plan.Steps.Select(x => x.RelativePath).ToArray());
            var warning = Assert.Single(plan.Warnings);
            Assert.Contains("Folder F already exists in the target", warning);
            Assert.Contains("C10", warning);
            Assert.Contains("nothing to merge", warning);
        }

        [Fact]
        public void ExistingFolder_OnlyAdded_OtherChangesetsInBetween_NoCutAndNoHistory()
        {
            var s = new Scenario()
                .Change(10, "F", TaskChangeKind.Add, TaskMergeItemKind.Folder)
                .Change(30, "F", TaskChangeKind.Encoding, TaskMergeItemKind.Folder)
                .Exists("F")
                .Third("F", 20);
            s.Change(20, "a.cs", TaskChangeKind.Edit).Exists("a.cs");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Single(plan.Parts);
            Assert.Equal(new[] { "a.cs" }, plan.Steps.Select(x => x.RelativePath).ToArray());
            Assert.Empty(s.HistoryCalls);
        }

        [Fact]
        public void ExistingFolder_WithAPropertyChange_StillHasAStep()
        {
            var s = new Scenario()
                .Change(10, "F", AddFolder | TaskChangeKind.Property, TaskMergeItemKind.Folder)
                .Exists("F");

            var plan = s.Build();

            AssertValid(plan);
            var step = Assert.Single(plan.Steps);
            Assert.Equal("F", step.RelativePath);
            Assert.Empty(plan.Warnings);
        }

        [Fact]
        public void NewFolder_AddedByTheTask_HasAStep()
        {
            var s = new Scenario().Change(10, "F", AddFolder, TaskMergeItemKind.Folder);

            var plan = s.Build();

            AssertValid(plan);
            var step = Assert.Single(plan.Steps);
            Assert.False(step.TargetExists);
            Assert.Equal(TaskMergeStepRecursion.None, step.Recursion);
            Assert.Empty(plan.Warnings);
        }

        [Fact]
        public void ExistingFolder_OnlyAdded_OnlyItem_PlanHasNoSteps()
        {
            var s = new Scenario()
                .Change(10, "F", AddFolder, TaskMergeItemKind.Folder)
                .Exists("F");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Empty(plan.Steps);
            Assert.Single(plan.Warnings);
        }

        // Un item assente nel target che il task non crea: il merge porterebbe l'intero item (anche
        // i changeset di altri precedenti al task, es. un collega che l'ha creato e mai fuso).
        [Theory]
        [InlineData(TaskChangeKind.Edit, TaskMergeItemKind.File)]
        [InlineData(TaskChangeKind.Edit | TaskChangeKind.Encoding, TaskMergeItemKind.File)]
        [InlineData(TaskChangeKind.Property, TaskMergeItemKind.File)]
        [InlineData(TaskChangeKind.Property, TaskMergeItemKind.Folder)]
        public void ItemNotInTheTarget_NotAddedByTheTask_IsAPlanError(TaskChangeKind kind, TaskMergeItemKind itemKind)
        {
            var s = new Scenario()
                .Change(200, "X", kind, itemKind)
                .Change(200, "ok.cs", TaskChangeKind.Edit).Exists("ok.cs");

            var plan = s.Build();

            Assert.False(plan.IsValid);
            var error = Assert.Single(plan.Errors);
            Assert.Contains("X does not exist in the target", error);
            Assert.Contains("C200", error);
            Assert.Empty(plan.Steps);
        }

        // Changeset che crea il file escluso dall'utente: i selezionati successivi lo modificano soltanto.
        [Fact]
        public void ItemNotInTheTarget_AddedByAnExcludedChangeset_IsAPlanError()
        {
            var s = new Scenario()
                .Change(20, "n.cs", TaskChangeKind.Edit)
                .Change(30, "n.cs", TaskChangeKind.Edit);

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Contains("n.cs does not exist in the target", Assert.Single(plan.Errors));
        }

        [Fact]
        public void ItemNotInTheTarget_DeletedByTheTask_IsAccepted()
        {
            var s = new Scenario()
                .Change(10, "d.cs", TaskChangeKind.Edit)
                .Change(20, "d.cs", TaskChangeKind.Delete);

            var plan = s.Build();

            AssertValid(plan);
            var step = Assert.Single(plan.Steps);
            Assert.False(step.TargetExists);
            Assert.Equal(new[] { 10, 20 }, step.TaskChangesetIds.ToArray());
        }

        [Fact]
        public void ItemNotInTheTarget_AddedByTheTask_LaterEdited_IsAccepted()
        {
            var s = new Scenario()
                .Change(10, "n.cs", AddNew)
                .Change(20, "n.cs", TaskChangeKind.Edit);

            var plan = s.Build();

            AssertValid(plan);
            Assert.Single(plan.Steps);
        }

        [Fact]
        public void ParentFolderMissingInTheTarget_NotCreatedByThePlan_IsAPlanError()
        {
            var s = new Scenario()
                .Change(10, "A/B/n.cs", AddNew)
                .Change(10, "A/B/m.cs", AddNew)
                .Exists("A");

            var plan = s.Build();

            Assert.False(plan.IsValid);
            var error = Assert.Single(plan.Errors);
            Assert.Contains("Folder A/B does not exist in the target", error);
            Assert.Contains("A/B/m.cs and 1 more item(s)", error);
            Assert.Equal(1, s.ExistsCalls.Count(x => x == Target + "/A/B"));
        }

        // La cartella madre la aggiunge un changeset escluso dall'utente: nessun passo la crea.
        [Fact]
        public void ParentFolderAddedByAnExcludedChangeset_IsAPlanError()
        {
            var s = new Scenario()
                .Change(20, "F/n.cs", AddNew);

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Contains("Folder F does not exist in the target", Assert.Single(plan.Errors));
        }

        [Fact]
        public void ParentFoldersCreatedByTheTask_OrExistingInTheTarget_AreAccepted()
        {
            var s = new Scenario()
                .Change(10, "A", AddFolder, TaskMergeItemKind.Folder)
                .Change(10, "A/B", AddFolder, TaskMergeItemKind.Folder)
                .Change(20, "A/B/n.cs", AddNew)
                .Change(20, "E/F/m.cs", AddNew)
                .Exists("E/F");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(new[] { "A", "A/B", "A/B/n.cs", "E/F/m.cs" }, plan.Steps.Select(x => x.RelativePath).ToArray());
            // A/B e' del piano: il controllo delle madri non interroga di nuovo il target (una sola
            // domanda, quella dell'item stesso); E/F esiste: E non si interroga
            Assert.Equal(1, s.ExistsCalls.Count(x => x == Target + "/A/B"));
            Assert.Equal(1, s.ExistsCalls.Count(x => x == Target + "/E/F"));
            Assert.DoesNotContain(Target + "/E", s.ExistsCalls);
        }

        // Una cartella del piano che viene DOPO l'item (changeset successivo) non conta: al merge
        // dell'item non esiste ancora.
        [Fact]
        public void ParentFolderAddedAfterTheItem_IsAPlanError()
        {
            var s = new Scenario()
                .Change(10, "F/n.cs", AddNew)
                .Change(20, "F", AddFolder, TaskMergeItemKind.Folder);

            var plan = s.Build();

            Assert.False(plan.IsValid);
            Assert.Contains("Folder F does not exist in the target", Assert.Single(plan.Errors));
        }

        // Cartella gia' nel target e senza passo: fa da madre esistente per i file nuovi.
        [Fact]
        public void ParentFolderExistingWithNothingToMerge_IsAccepted()
        {
            var s = new Scenario()
                .Change(10, "F", AddFolder, TaskMergeItemKind.Folder)
                .Change(10, "F/n.cs", AddNew)
                .Exists("F");

            var plan = s.Build();

            AssertValid(plan);
            Assert.Equal(new[] { "F/n.cs" }, plan.Steps.Select(x => x.RelativePath).ToArray());
        }

        // ------------------------------------------------------------------------------------------
        // Supporto
        // ------------------------------------------------------------------------------------------

        private static void AssertValid(TaskMergePlan plan)
        {
            Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.Errors));
            Assert.Empty(plan.Errors);
        }

        private static TaskMergeStep Step(TaskMergePlan plan, string relativePath)
        {
            return plan.Steps.Single(x => x.RelativePath == relativePath);
        }

        private static string Describe(TaskMergePlan plan)
        {
            return string.Join("|", plan.Parts.Select(p =>
                p.Number + ":" + string.Join(",", p.TaskChangesetIds) + "[" + p.EndReason + "]"
                + string.Join(";", p.Steps.Select(x =>
                    x.Number + " " + x.SourceItem + " " + x.TargetItem + " " + x.Kind + " " + x.ChangeKind + " "
                    + string.Join(",", x.TaskChangesetIds) + " " + x.Recursion + " " + x.TargetExists + " "
                    + string.Join(",", x.InterleavedThirdParty)))));
        }

        // Scenario di prova: path relativi al branch sorgente, target e storia finti.
        private sealed class Scenario
        {
            public Scenario()
            {
                SourceBranch = Source;
                TargetBranch = Target;
                TaskIds = new List<int>();
                Changes = new List<TaskChangeInfo>();
                ExistingInTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ThirdParty = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                HistoryCalls = new List<Tuple<string, int, int>>();
                ExistsCalls = new List<string>();
            }

            public string SourceBranch { get; set; }

            public string TargetBranch { get; set; }

            public List<int> TaskIds { get; }

            public List<TaskChangeInfo> Changes { get; }

            public HashSet<string> ExistingInTarget { get; }

            // path sorgente -> changeset di terzi che toccano l'item
            public Dictionary<string, List<int>> ThirdParty { get; }

            public List<Tuple<string, int, int>> HistoryCalls { get; }

            public List<string> ExistsCalls { get; }

            public Action<string> OnExists { get; set; }

            public Func<string, int, int, IReadOnlyList<int>> RawHistory { get; set; }

            public Scenario Change(int changesetId, string relativePath, TaskChangeKind kind, TaskMergeItemKind itemKind = TaskMergeItemKind.File)
            {
                if (!TaskIds.Contains(changesetId))
                    TaskIds.Add(changesetId);
                Changes.Add(new TaskChangeInfo(changesetId, Source + "/" + relativePath, itemKind, kind));
                return this;
            }

            public Scenario Exists(string relativePath)
            {
                ExistingInTarget.Add(Target + "/" + relativePath);
                return this;
            }

            public Scenario Third(string relativePath, params int[] changesetIds)
            {
                List<int> list;
                var key = Source + "/" + relativePath;
                if (!ThirdParty.TryGetValue(key, out list))
                {
                    list = new List<int>();
                    ThirdParty.Add(key, list);
                }
                list.AddRange(changesetIds);
                return this;
            }

            public TaskMergePlan Build()
            {
                return TaskMergePlanner.Build(new TaskMergePlanInput
                {
                    SourceBranch = SourceBranch,
                    TargetBranch = TargetBranch,
                    TaskChangesetIds = TaskIds.ToList(),
                    Changes = Changes.ToList(),
                    TargetItemExists = item =>
                    {
                        ExistsCalls.Add(item);
                        if (OnExists != null)
                            OnExists(item);
                        return ExistingInTarget.Contains(item);
                    },
                    ThirdPartyChangesetsBetween = (item, a, b) =>
                    {
                        HistoryCalls.Add(Tuple.Create(item, a, b));
                        if (RawHistory != null)
                            return RawHistory(item, a, b);
                        List<int> list;
                        if (!ThirdParty.TryGetValue(item, out list))
                            return new int[0];
                        return list.Where(id => id > a && id < b).ToList();
                    }
                });
            }
        }
    }
}
