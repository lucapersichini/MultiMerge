// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MultiMerge;
using Xunit;

namespace MultiMerge.Tests.GitMerge
{
    public class GitTransferEngineTests
    {
        [Fact]
        public void PreviewIsIsolatedAndApplyTransfersOnlySelectedCommits()
        {
            using (var f = new Fixture())
            {
                var before = f.Git("rev-parse", "HEAD");
                var plan = f.Plan(f.First, f.Second);
                Assert.Equal(before, f.Git("rev-parse", "HEAD"));
                Assert.Equal("main", f.Git("branch", "--show-current"));
                Assert.Equal("", f.Git("status", "--porcelain"));
                Assert.All(plan.Steps, s => Assert.Equal("Ready", s.Status));
                var result = f.Engine.Apply(plan);
                Assert.Equal("Completed", result.Status);
                Assert.Equal("release", f.Git("branch", "--show-current"));
                Assert.True(File.Exists(f.PathOf("Formatter.cs")));
                Assert.False(File.Exists(f.PathOf("Other.cs")));
                Assert.Contains("Formatter.Format", File.ReadAllText(f.PathOf("Greeting.cs")));
                Assert.Contains(f.Second, f.Git("log", "-1", "--format=%B"));
                Assert.Contains("Task101 use formatter", f.Git("log", "-1", "--format=%B"));
            }
        }
        [Fact]
        public void EmptySelectionIsRejected() { using (var f = new Fixture()) Assert.Throws<InvalidOperationException>(() => f.Plan()); }
        [Fact]
        public void DirtyWorkingTreeBlocksApplyWithoutChangingBranch()
        {
            using (var f = new Fixture())
            {
                var plan = f.Plan(f.First); f.Write("local.txt", "uncommitted");
                Assert.Throws<InvalidOperationException>(() => f.Engine.Apply(plan));
                Assert.Equal("main", f.Git("branch", "--show-current"));
                Assert.Equal("uncommitted", File.ReadAllText(f.PathOf("local.txt")));
            }
        }
        [Fact]
        public void DirtyWorkingTreeCanBePreviewedWithoutTouchingItsFiles()
        {
            using (var f = new Fixture())
            {
                f.Write("Greeting.cs", "uncommitted user content");
                Assert.True(f.Plan(f.First, f.Second).IsValid);
                Assert.Equal("uncommitted user content", File.ReadAllText(f.PathOf("Greeting.cs")));
            }
        }
        [Fact]
        public void ChangedTargetInvalidatesPreview()
        {
            using (var f = new Fixture())
            {
                var plan = f.Plan(f.First); f.Git("switch", "release"); f.Write("changed.txt", "new target commit"); f.Commit("Target moved"); f.Git("switch", "main");
                Assert.Throws<InvalidOperationException>(() => f.Engine.Apply(plan));
                Assert.Equal("main", f.Git("branch", "--show-current"));
            }
        }
        [Fact]
        public void ChangedSourceInvalidatesPreview()
        {
            using (var f = new Fixture())
            {
                var plan = f.Plan(f.First); f.Git("switch", "integration"); f.Write("changed.txt", "new source commit"); f.Commit("Source moved"); f.Git("switch", "main");
                Assert.Throws<InvalidOperationException>(() => f.Engine.Apply(plan));
            }
        }
        [Fact]
        public void EquivalentPatchIsSkippedWithoutDuplicateCommit()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "release"); f.Git("cherry-pick", "-x", f.First); var applied = f.Git("rev-parse", "HEAD"); f.Git("switch", "main");
                var plan = f.Plan(f.First); Assert.Equal("Already applied", plan.Steps[0].Status);
                Assert.Equal("Completed", f.Engine.Apply(plan).Status); Assert.Equal(applied, f.Git("rev-parse", "HEAD"));
            }
        }
        [Fact]
        public void ConflictCanBeEditedStagedAndContinued()
        {
            using (var f = new Fixture(true))
            {
                var plan = f.Plan(f.First, f.Second); Assert.Equal("Conflict", plan.Steps[1].Status);
                var session = f.Engine.Apply(plan); Assert.Equal("Paused", session.Status);
                var content = f.Engine.ReadConflict(session, "Greeting.cs"); Assert.True(content.CanEdit);
                Assert.Throws<InvalidOperationException>(() => f.Engine.SaveConflict(session, content, content.Result));
                f.Engine.SaveConflict(session, content, content.Incoming);
                f.Engine.Continue(session);
                Assert.Equal("Completed", session.Status); Assert.Equal("", f.Git("status", "--porcelain"));
                Assert.Contains("Formatter.Format", File.ReadAllText(f.PathOf("Greeting.cs")));
            }
        }
        [Fact]
        public void AbortKeepsEarlierCompletedCommits()
        {
            using (var f = new Fixture(true))
            {
                var session = f.Engine.Apply(f.Plan(f.First, f.Second)); f.Engine.Abort(session);
                Assert.Equal("Aborted", session.Status); Assert.Equal("", f.Git("status", "--porcelain"));
                Assert.True(File.Exists(f.PathOf("Formatter.cs"))); Assert.Contains("Release", File.ReadAllText(f.PathOf("Greeting.cs")));
            }
        }
        [Fact]
        public void ContinueRefusesUnrelatedStagedFiles()
        {
            using (var f = new Fixture(true))
            {
                var session = f.Engine.Apply(f.Plan(f.First, f.Second)); var content = f.Engine.ReadConflict(session, "Greeting.cs");
                f.Engine.SaveConflict(session, content, content.Incoming); f.Write("OtherUserFile.cs", "user work"); f.Git("add", "OtherUserFile.cs");
                var head = f.Git("rev-parse", "HEAD"); Assert.Throws<InvalidOperationException>(() => f.Engine.Continue(session));
                Assert.Equal(head, f.Git("rev-parse", "HEAD")); Assert.Equal("user work", File.ReadAllText(f.PathOf("OtherUserFile.cs")));
            }
        }
        [Fact]
        public void RenameIsSupported()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "integration"); f.Git("mv", "Greeting.cs", "NewGreeting.cs"); var rename = f.Commit("Rename file"); f.Git("switch", "main");
                Assert.Equal("Completed", f.Engine.Apply(f.Plan(f.First, f.Second, rename)).Status);
                Assert.True(File.Exists(f.PathOf("NewGreeting.cs"))); Assert.False(File.Exists(f.PathOf("Greeting.cs")));
            }
        }
        [Fact]
        public void MergeCommitIsExplicitlyBlocked()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "-c", "topic", "main"); f.Write("topic.txt", "topic"); f.Commit("Topic"); f.Git("switch", "integration");
                f.Git("merge", "--no-ff", "topic", "-m", "Merge topic"); var merge = f.Git("rev-parse", "HEAD"); f.Git("switch", "main");
                var plan = f.Plan(merge); Assert.False(plan.IsValid); Assert.Contains(plan.Warnings, w => w.Contains("mainline"));
                Assert.Throws<InvalidOperationException>(() => f.Engine.Apply(plan));
            }
        }
        [Fact]
        public void ExplicitMainlineAllowsAMergeCommit()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "-c", "topic", "main"); f.Write("topic.txt", "topic"); f.Commit("Topic task");
                f.Git("switch", "integration"); f.Git("merge", "--no-ff", "topic", "-m", "Merge topic for Task #101");
                var merge = f.Git("rev-parse", "HEAD"); f.Git("switch", "main");
                var plan = f.Engine.Preview(f.Root, "integration", "release", new[] { merge },
                    new System.Collections.Generic.Dictionary<string, int> { { merge, 1 } });
                Assert.True(plan.IsValid);
                Assert.Equal("Ready", plan.Steps[0].Status);
                Assert.Equal("Completed", f.Engine.Apply(plan).Status);
                Assert.True(File.Exists(f.PathOf("topic.txt")));
            }
        }
        [Fact]
        public void SecondMainlineTransfersTheOtherSideOfAMerge()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "-c", "topic", "main"); f.Write("topic.txt", "topic"); f.Commit("Topic");
                f.Git("switch", "integration"); f.Git("merge", "--no-ff", "topic", "-m", "Merge topic");
                var merge = f.Git("rev-parse", "HEAD"); f.Git("switch", "main");
                var plan = f.Engine.Preview(f.Root, "integration", "release", new[] { merge },
                    new System.Collections.Generic.Dictionary<string, int> { { merge, 2 } });
                Assert.True(plan.IsValid);
                Assert.Equal("Completed", f.Engine.Apply(plan).Status);
                Assert.False(File.Exists(f.PathOf("topic.txt")));
                Assert.True(File.Exists(f.PathOf("Formatter.cs")));
                Assert.True(File.Exists(f.PathOf("Other.cs")));
            }
        }
        [Fact]
        public void TaskDiscoveryMatchesExactIdsAndLeavesUnrelatedCommitsOut()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "integration"); f.Write("task.txt", "task"); var tagged = f.Commit("Fix AB#101");
                f.Write("other.txt", "other"); var other = f.Commit("Fix AB#1010"); f.Git("switch", "main");
                var found = f.Engine.FindTaskCommits(f.Root, "integration", "release", 101);
                Assert.Contains(found, c => c.Sha == tagged);
                Assert.Contains(found, c => c.Sha == f.First);
                Assert.Contains(found, c => c.Sha == f.Second);
                Assert.DoesNotContain(found, c => c.Sha == other);
            }
        }
        [Fact]
        public void AzureBoardsLinksRecognizeOnlyGitCommitArtifacts()
        {
            var sha = new string('a', 40);
            var ids = GitTaskLinks.CommitIds(new[] {
                "vstfs:///Git/Commit/project%2Frepository%2F" + sha,
                "vstfs:///VersionControl/Changeset/123",
                "vstfs:///Git/PullRequest/project%2Frepository%2F17",
                "vstfs:///Git/Commit/project%2Frepository%2F" + sha.ToUpperInvariant()
            });
            Assert.Single(ids);
            Assert.Equal(sha, ids[0]);
        }
        [Fact]
        public void PathPolicySkipsChangesAndPreservesTarget()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "release");
                f.Write(".automerge-policy.json", "{\"version\":1,\"pathRules\":[{\"id\":\"skip-formatter\",\"pattern\":\"Formatter.cs\",\"action\":\"Skip\"}],\"lineRules\":[]}");
                f.Commit("Add target Git policy"); f.Git("switch", "main");
                var plan = f.Plan(f.First, f.Second);
                Assert.True(plan.IsValid);
                Assert.Equal("Skipped by policy", plan.Steps[0].Status);
                var session = f.Engine.Apply(plan);
                Assert.Equal("Completed", session.Status);
                Assert.False(File.Exists(f.PathOf("Formatter.cs")));
            }
        }
        [Fact]
        public void LinePolicyKeepsTheTargetLineAndRecordsTheCommit()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "release");
                f.Write(".automerge-policy.json",
                    "{\"version\":1,\"pathRules\":[],\"lineRules\":[{\"id\":\"keep-greeting\",\"filePattern\":\"Greeting.cs\",\"linePattern\":\"return\"}]}");
                f.Commit("Protect greeting line"); f.Git("switch", "main");
                var plan = f.Plan(f.Second);
                Assert.True(plan.IsValid);
                Assert.Equal("Ready", plan.Steps[0].Status);
                var session = f.Engine.Apply(plan);
                Assert.Equal("Completed", session.Status);
                Assert.Contains("return \"Hello\"", File.ReadAllText(f.PathOf("Greeting.cs")));
                Assert.Contains(f.Second, f.Git("log", "-1", "--format=%B"));
            }
        }
        [Fact]
        public void DiscardPolicyRecordsProvenanceWithoutChangingTargetContent()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "release");
                f.Write(".automerge-policy.json",
                    "{\"version\":1,\"pathRules\":[{\"id\":\"discard-formatter\",\"pattern\":\"Formatter.cs\",\"action\":\"Discard\"}],\"lineRules\":[]}");
                f.Commit("Discard formatter on release"); f.Git("switch", "main");
                var plan = f.Plan(f.First);
                Assert.True(plan.IsValid);
                var targetBefore = f.Git("rev-parse", "release");
                Assert.Equal("Completed", f.Engine.Apply(plan).Status);
                Assert.False(File.Exists(f.PathOf("Formatter.cs")));
                Assert.NotEqual(targetBefore, f.Git("rev-parse", "release"));
                Assert.Contains(f.First, f.Git("log", "-1", "--format=%B"));
            }
        }
        [Fact]
        public void ChangingPolicyAfterPreviewBlocksApply()
        {
            using (var f = new Fixture())
            {
                var plan = f.Plan(f.First);
                f.Git("switch", "release"); f.Write(".automerge-policy.json",
                    "{\"version\":1,\"pathRules\":[],\"lineRules\":[]}"); f.Commit("Add policy");
                f.Git("switch", "main");
                Assert.Throws<InvalidOperationException>(() => f.Engine.Apply(plan));
            }
        }
        [Fact]
        public void ConflictedTransferCanRecoverAfterEngineRestart()
        {
            using (var f = new Fixture(true))
            {
                var session = f.Engine.Apply(f.Plan(f.First, f.Second));
                Assert.Equal("Paused", session.Status);
                var restarted = new GitTransferEngine();
                Assert.True(restarted.HasRecoverableSession(f.Root));
                var recovered = restarted.Recover(f.Root);
                Assert.Equal(f.Second, recovered.Plan.Steps[recovered.NextIndex].Commit.Sha);
                var content = restarted.ReadConflict(recovered, "Greeting.cs");
                restarted.SaveConflict(recovered, content, content.Incoming);
                restarted.Continue(recovered);
                Assert.Equal("Completed", recovered.Status);
                Assert.False(restarted.HasRecoverableSession(f.Root));
                Assert.Equal("", f.Git("status", "--porcelain"));
            }
        }
        [Fact]
        public void RestartRecoveryRefusesAnExternallyChangedHead()
        {
            using (var f = new Fixture(true))
            {
                f.Engine.Apply(f.Plan(f.First, f.Second));
                f.Git("cherry-pick", "--abort"); f.Write("changed.txt", "outside"); f.Commit("External change");
                Assert.Throws<InvalidOperationException>(() => new GitTransferEngine().Recover(f.Root));
            }
        }
        [Fact]
        public void RecoveryRetainsASelectedCommitAlreadyInTheTargetHistory()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "-c", "exact", f.First);
                f.Write("Greeting.cs", "namespace Demo { public class Greeting { public string Text() { return \"Exact branch\"; } } }\n");
                f.Commit("Exact branch greeting");
                f.Git("switch", "main");
                var plan = f.Engine.Preview(f.Root, "integration", "exact", new[] { f.First, f.Second });
                Assert.Equal("Already applied", plan.Steps[0].Status);
                var active = f.Engine.Apply(plan);
                Assert.Equal("Paused", active.Status);
                var recovered = new GitTransferEngine().Recover(f.Root);
                Assert.Equal(f.First, recovered.Plan.Steps[0].Commit.Sha);
                Assert.True(recovered.Plan.Steps[0].Commit.AlreadyApplied);
                var conflict = f.Engine.ReadConflict(recovered, "Greeting.cs");
                f.Engine.SaveConflict(recovered, conflict, conflict.Incoming);
                f.Engine.Continue(recovered);
                Assert.Equal("Completed", recovered.Status);
            }
        }
        [Fact]
        public void BatchTransfersSelectedCommitsToTwoTargets()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "-c", "release2", "main"); f.Write("SecondRelease.txt", "second");
                f.Commit("Second release setup"); f.Git("switch", "main");
                var batch = f.Engine.PreviewTargets(f.Root, "integration", new[] { "release", "release2" },
                    new[] { f.First, f.Second }, null);
                Assert.True(batch.IsValid);
                var session = f.Engine.ApplyBatch(batch);
                Assert.Equal("Completed", session.Status);
                Assert.Equal(2, session.TargetIndex);
                Assert.Equal("release2", f.Git("branch", "--show-current"));
                Assert.True(File.Exists(f.PathOf("Formatter.cs")));
                Assert.True(File.Exists(f.PathOf("SecondRelease.txt")));
                Assert.Equal("", f.Git("status", "--porcelain"));
                Assert.False(f.Engine.HasRecoverableBatch(f.Root));
                Assert.Contains(f.First, f.Git("log", "release", "-3", "--format=%B"));
            }
        }
        [Fact]
        public void BatchStopsAtConflictAndRecoversAfterRestart()
        {
            using (var f = new Fixture(true))
            {
                f.Git("switch", "-c", "release2", "main"); f.Write("SecondRelease.txt", "second");
                f.Commit("Second release setup"); f.Git("switch", "main");
                var batch = f.Engine.PreviewTargets(f.Root, "integration", new[] { "release", "release2" },
                    new[] { f.First, f.Second }, null);
                var session = f.Engine.ApplyBatch(batch);
                Assert.Equal("Paused", session.Status);
                Assert.Equal(0, session.TargetIndex);
                Assert.True(f.Engine.HasRecoverableBatch(f.Root));
                Assert.False(f.Git("log", "release2", "--format=%B").Contains(f.First));
                var restarted = new GitTransferEngine();
                var restored = restarted.RecoverBatch(f.Root);
                Assert.Equal(0, restored.TargetIndex);
                var conflict = restarted.ReadConflict(restored.Current, "Greeting.cs");
                restarted.SaveConflict(restored.Current, conflict, conflict.Incoming);
                restarted.ContinueBatch(restored);
                Assert.Equal("Completed", restored.Status);
                Assert.Equal("release2", f.Git("branch", "--show-current"));
                Assert.False(restarted.HasRecoverableBatch(f.Root));
            }
        }
        [Fact]
        public void BatchRejectsChangedSecondTargetBeforeTouchingFirst()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "-c", "release2", "main"); f.Write("SecondRelease.txt", "second");
                f.Commit("Second release setup"); f.Git("switch", "main");
                var batch = f.Engine.PreviewTargets(f.Root, "integration", new[] { "release", "release2" },
                    new[] { f.First }, null);
                var firstBefore = f.Git("rev-parse", "release");
                f.Git("switch", "release2"); f.Write("changed.txt", "changed"); f.Commit("Move second release"); f.Git("switch", "main");
                Assert.Throws<InvalidOperationException>(() => f.Engine.ApplyBatch(batch));
                Assert.Equal(firstBefore, f.Git("rev-parse", "release"));
            }
        }
        [Fact]
        public void AlreadyAppliedOnFirstTargetIsStillSelectableForSecondTarget()
        {
            using (var f = new Fixture())
            {
                f.Git("switch", "release"); f.Git("cherry-pick", "-x", f.First);
                var firstHead = f.Git("rev-parse", "HEAD");
                f.Git("switch", "-c", "release2", "main"); f.Write("SecondRelease.txt", "second");
                f.Commit("Second release setup"); f.Git("switch", "main");
                var candidates = f.Engine.LoadCommitCandidates(f.Root, "integration", new[] { "release", "release2" });
                Assert.Contains(candidates, c => c.Sha == f.First && c.AlreadyApplied);
                var batch = f.Engine.PreviewTargets(f.Root, "integration", new[] { "release", "release2" },
                    new[] { f.First }, null);
                Assert.Equal("Already applied", batch.Targets[0].Steps[0].Status);
                Assert.Equal("Ready", batch.Targets[1].Steps[0].Status);
                Assert.Equal("Completed", f.Engine.ApplyBatch(batch).Status);
                Assert.Equal(firstHead, f.Git("rev-parse", "release"));
                Assert.Contains(f.First, f.Git("log", "release2", "-2", "--format=%B"));
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "MultiMerge-Git-Unit", Guid.NewGuid().ToString("N"));
            public readonly GitTransferEngine Engine = new GitTransferEngine();
            public string First, Second;
            public Fixture(bool conflict = false)
            {
                Directory.CreateDirectory(Root); Git("init", "-b", "main"); Git("config", "user.name", "Luca Persichini");
                Git("config", "user.email", "154256435+lucapersichini@users.noreply.github.com");
                Write("Greeting.cs", "namespace Demo { public class Greeting { public string Text() { return \"Hello\"; } } }\n"); Commit("Baseline");
                Git("switch", "-c", "release"); Write("Release.txt", "release marker"); Commit("Release setup");
                if (conflict) { Write("Greeting.cs", "namespace Demo { public class Greeting { public string Text() { return \"Release\"; } } }\n"); Commit("Release custom greeting"); }
                Git("switch", "-c", "integration", "main"); Write("Formatter.cs", "namespace Demo { public static class Formatter { public static string Format(string text) { return text; } } }\n"); First = Commit("Task101 add formatter");
                Write("Other.cs", "// unrelated Task202\n"); Commit("Task202 unrelated change");
                Write("Greeting.cs", "namespace Demo { public class Greeting { public string Text() { return Formatter.Format(\"Hello\"); } } }\n"); Second = Commit("Task101 use formatter"); Git("switch", "main");
            }
            public string PathOf(string path) { return Path.Combine(Root, path); }
            public void Write(string path, string text) { File.WriteAllText(PathOf(path), text); }
            public string Commit(string text) { Git("add", "-A"); Git("commit", "-m", text); return Git("rev-parse", "HEAD"); }
            public GitTransferPlan Plan(params string[] ids) { return Engine.Preview(Root, "integration", "release", ids); }
            public string Git(params string[] args)
            {
                var start = new ProcessStartInfo("git.exe", string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")))
                { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(start))
                {
                    var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync(); p.WaitForExit();
                    if (p.ExitCode != 0) throw new InvalidOperationException(error.Result + output.Result);
                    return output.Result.Trim();
                }
            }
            public void Dispose()
            {
                // Retained as synthetic test artifacts for inspection; no user checkout is deleted.
            }
        }
    }
}
