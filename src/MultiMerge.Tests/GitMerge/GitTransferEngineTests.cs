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