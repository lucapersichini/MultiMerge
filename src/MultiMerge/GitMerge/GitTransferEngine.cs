// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiMerge
{
    internal sealed class GitResult
    {
        public int Code;
        public string Output;
        public string Error;
    }

    internal static class GitCli
    {
        internal static GitResult Run(string directory, bool allowFailure, params string[] args)
        {
            var start = new ProcessStartInfo("git.exe", string.Join(" ", args.Select(Quote)))
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000))
                {
                    process.Kill();
                    throw new InvalidOperationException("Git command timed out. Inspect the repository before retrying.");
                }
                var result = new GitResult { Code = process.ExitCode, Output = output.GetAwaiter().GetResult(), Error = error.GetAwaiter().GetResult() };
                if (result.Code != 0 && !allowFailure)
                    throw new InvalidOperationException(result.Error.Trim() + "\n" + result.Output.Trim());
                return result;
            }
        }

        private static string Quote(string value)
        {
            // Windows argv quoting; no shell, command substitution or concatenated shell commands.
            var text = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                text.Append(c);
                slashes = 0;
            }
            return text.Append('\\', slashes * 2).Append('"').ToString();
        }
    }

    public sealed class GitCommitInfo
    {
        public string Sha { get; internal set; }
        public string ShortSha { get { return Sha.Substring(0, 8); } }
        public string Author { get; internal set; }
        public string Date { get; internal set; }
        public string Comment { get; internal set; }
        public string Subject { get { return Comment.Split('\n')[0]; } }
        public bool AlreadyApplied { get; internal set; }
        public bool IsMerge { get; internal set; }
        public int ParentCount { get; internal set; }
        public IReadOnlyList<string> Paths { get; internal set; }
    }

    public sealed class GitRepositoryInfo
    {
        public string Root { get; internal set; }
        public IReadOnlyList<string> Branches { get; internal set; }
        public string CurrentBranch { get; internal set; }
    }

    public sealed class GitTransferStep
    {
        public GitCommitInfo Commit { get; internal set; }
        public string Status { get; internal set; }
        public string Details { get; internal set; }
        public int Mainline { get; internal set; }
        public IReadOnlyList<string> Paths { get; internal set; }
        public string Target { get; internal set; }
    }

    public sealed class GitTransferPlan
    {
        public string Root { get; internal set; }
        public string Source { get; internal set; }
        public string Target { get; internal set; }
        public string SourceHead { get; internal set; }
        public string TargetHead { get; internal set; }
        public IReadOnlyList<GitTransferStep> Steps { get; internal set; }
        public IReadOnlyList<string> Warnings { get; internal set; }
        public bool IsValid { get; internal set; }
        public GitPolicySnapshot Policy { get; internal set; }
    }

    public sealed class GitTransferSession
    {
        public GitTransferPlan Plan { get; internal set; }
        public int NextIndex { get; internal set; }
        public string ExpectedHead { get; internal set; }
        public string Status { get; internal set; }
        public string Message { get; internal set; }
        public bool IsActive { get { return Status != "Completed" && Status != "Aborted"; } }
        public IReadOnlyList<string> Conflicts { get; internal set; }
    }

    public sealed class GitConflictContent
    {
        public string Path { get; internal set; }
        public string Current { get; internal set; }
        public string Incoming { get; internal set; }
        public string Result { get; internal set; }
        public bool CanEdit { get; internal set; }
        public string Note { get; internal set; }
        internal bool Bom;
        internal bool CrLf;
    }

    public sealed partial class GitTransferEngine
    {
        private static string Output(string root, params string[] args) { return GitCli.Run(root, false, args).Output.Trim(); }
        private static string Resolve(string root, string reference)
        {
            return Output(root, "rev-parse", "--verify", "--end-of-options", reference + "^{commit}");
        }
        private static string[] Lines(string text) { return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); }
        private static string[] Paths(string text) { return text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries); }

        public GitRepositoryInfo Inspect(string path)
        {
            var full = System.IO.Path.GetFullPath(path);
            var root = Output(full, "rev-parse", "--show-toplevel");
            if (Output(root, "rev-parse", "--is-bare-repository") != "false")
                throw new InvalidOperationException("A Git working tree is required.");
            return new GitRepositoryInfo
            {
                Root = System.IO.Path.GetFullPath(root),
                Branches = Lines(Output(root, "for-each-ref", "--format=%(refname:short)", "refs/heads")).ToList().AsReadOnly(),
                CurrentBranch = Output(root, "branch", "--show-current")
            };
        }

        private static void CheckBranches(string root, string source, string target)
        {
            if (source == target) throw new InvalidOperationException("Choose different source and target branches.");
            foreach (var branch in new[] { source, target })
            {
                GitCli.Run(root, false, "check-ref-format", "--branch", branch);
                Resolve(root, "refs/heads/" + branch);
            }
        }

        public IReadOnlyList<GitCommitInfo> LoadCommits(string path, string source, string target)
        {
            var root = Inspect(path).Root;
            CheckBranches(root, source, target);
            var ids = Lines(Output(root, "rev-list", "--reverse", "--topo-order", "--max-count=200", "refs/heads/" + source, "--not", "refs/heads/" + target));
            var equivalents = new HashSet<string>(Lines(Output(root, "cherry", "refs/heads/" + target, "refs/heads/" + source))
                .Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line.Substring(2).Trim()), StringComparer.Ordinal);
            var commits = new List<GitCommitInfo>();
            foreach (var id in ids)
                commits.Add(ReadCommit(root, id, equivalents.Contains(id)));
            return commits.AsReadOnly();
        }

        private static GitCommitInfo ReadCommit(string root, string id, bool alreadyApplied)
        {
            var metadata = GitCli.Run(root, false, "show", "--no-patch", "--format=%an%n%aI%n%B", id)
                .Output.Replace("\r\n", "\n").Split(new[] { '\n' }, 3);
            var parents = Output(root, "rev-list", "--parents", "-n", "1", id).Split(' ');
            return new GitCommitInfo {
                Sha = id, Author = metadata[0], Date = metadata[1], Comment = metadata[2].Trim(),
                AlreadyApplied = alreadyApplied, IsMerge = parents.Length > 2, ParentCount = parents.Length - 1,
                Paths = Paths(GitCli.Run(root, false, "diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "-z", id)
                    .Output).ToList().AsReadOnly()
            };
        }

        private static bool IsAncestor(string root, string ancestor, string branch)
        {
            return GitCli.Run(root, true, "merge-base", "--is-ancestor", ancestor, "refs/heads/" + branch).Code == 0;
        }

        public IReadOnlyList<GitCommitInfo> LoadCommitCandidates(string path, string source, IEnumerable<string> targets)
        {
            var root = Inspect(path).Root;
            var branches = targets.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList();
            if (branches.Count == 0) throw new InvalidOperationException("Choose a target branch.");
            var commits = new Dictionary<string, GitCommitInfo>(StringComparer.Ordinal);
            foreach (var target in branches)
                foreach (var commit in LoadCommits(root, source, target))
                    if (!commits.ContainsKey(commit.Sha)) commits.Add(commit.Sha, commit);
            var first = branches[0];
            foreach (var commit in commits.Values)
                if (IsAncestor(root, commit.Sha, first)) commit.AlreadyApplied = true;
            var order = Lines(Output(root, "rev-list", "--reverse", "--topo-order", "--max-count=200", "refs/heads/" + source));
            return order.Where(commits.ContainsKey).Select(id => commits[id]).ToList().AsReadOnly();
        }

        public GitTransferPlan Preview(string path, string source, string target, IEnumerable<string> selectedIds)
        {
            return Preview(path, source, target, selectedIds, null);
        }

        public GitTransferPlan Preview(string path, string source, string target, IEnumerable<string> selectedIds,
            IReadOnlyDictionary<string, int> mainlines)
        {
            var root = Inspect(path).Root;
            var all = LoadCommits(root, source, target);
            var selected = new HashSet<string>(selectedIds, StringComparer.Ordinal);
            if (selected.Count == 0) throw new InvalidOperationException("Select at least one commit.");
            var available = all.ToList();
            var sourceOrder = Lines(Output(root, "rev-list", "--reverse", "--topo-order", "--max-count=200", "refs/heads/" + source)).ToList();
            foreach (var id in selected.Where(id => available.All(c => c.Sha != id)))
            {
                if (!sourceOrder.Contains(id) || !IsAncestor(root, id, target))
                    throw new InvalidOperationException("The selected commits are stale or outside the latest 200. Load the branches again.");
                available.Add(ReadCommit(root, id, true));
            }
            var chosen = available.Where(c => selected.Contains(c.Sha)).OrderBy(c => sourceOrder.IndexOf(c.Sha)).ToList();
            var warnings = new List<string>();
            foreach (var commit in chosen)
            {
                if (commit.IsMerge && (mainlines == null || !mainlines.ContainsKey(commit.Sha) ||
                    mainlines[commit.Sha] < 1 || mainlines[commit.Sha] > commit.ParentCount))
                    warnings.Add(commit.ShortSha + ": choose a mainline parent from 1 to " + commit.ParentCount + ".");
                if (commit.AlreadyApplied) continue;
                foreach (var earlier in all.TakeWhile(c => c.Sha != commit.Sha).Where(c => !selected.Contains(c.Sha) && !c.AlreadyApplied))
                    if (earlier.Paths.Intersect(commit.Paths, StringComparer.Ordinal).Any())
                        warnings.Add(commit.ShortSha + " changes files also changed by excluded " + earlier.ShortSha + "; it may depend on those changes.");
            }
            var plan = new GitTransferPlan
            {
                Root = root, Source = source, Target = target,
                SourceHead = Resolve(root, "refs/heads/" + source), TargetHead = Resolve(root, "refs/heads/" + target),
                Steps = chosen.Select(c => new GitTransferStep { Commit = c, Status = "Waiting", Details = "",
                    Mainline = mainlines != null && mainlines.ContainsKey(c.Sha) ? mainlines[c.Sha] : 0 }).ToList().AsReadOnly(),
                IsValid = !chosen.Any(c => c.IsMerge && (mainlines == null || !mainlines.ContainsKey(c.Sha) ||
                    mainlines[c.Sha] < 1 || mainlines[c.Sha] > c.ParentCount))
            };
            if (plan.IsValid)
            {
                plan.Policy = LoadPolicy(root, target);
                warnings.Add(plan.Policy.Description);
                foreach (var step in plan.Steps) step.Paths = ChangedPaths(root, step);
                var scratchRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MultiMerge-Git-Preview");
                var scratch = System.IO.Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratchRoot);
                try
                {
                    GitCli.Run(scratchRoot, false, "clone", "--quiet", "--no-local", "--no-checkout", "--", root, scratch);
                    GitCli.Run(scratch, false, "switch", "--detach", plan.TargetHead);
                    GitCli.Run(scratch, false, "config", "user.name", Output(root, "config", "user.name"));
                    GitCli.Run(scratch, false, "config", "user.email", Output(root, "config", "user.email"));
                    bool blocked = false;
                    foreach (var step in plan.Steps)
                    {
                        if (step.Commit.AlreadyApplied) { step.Status = "Already applied"; continue; }
                        if (blocked) { step.Status = "After conflict"; step.Details = "Will be checked after resolving the preceding conflict."; continue; }
                        GitResult pick;
                        try { pick = plan.Policy.Effective.PathRules.Any() || plan.Policy.Effective.LineRules.Any()
                            ? PickWithPolicy(scratch, step, plan.Policy)
                            : GitCli.Run(scratch, true, PickArguments(step, false)); }
                        catch (InvalidOperationException ex) { step.Status = "Failed"; step.Details = ex.Message; plan.IsValid = false; blocked = true; continue; }
                        if (pick.Code == 0) {
                            if (step.Status != "Skipped by policy") step.Status = "Ready";
                            if (!string.IsNullOrWhiteSpace(step.Details))
                                warnings.Add(step.Commit.ShortSha + " policy: " + step.Details);
                            continue;
                        }
                        var conflicts = GetConflicts(scratch);
                        if (conflicts.Count != 0)
                        {
                            step.Status = "Conflict"; step.Details = string.Join(", ", conflicts);
                            warnings.Add(step.Commit.ShortSha + ": expected conflicts in " + step.Details);
                            blocked = true;
                        }
                        else if (HasPick(scratch) && IsEmptyPick(scratch))
                        {
                            GitCli.Run(scratch, false, "cherry-pick", "--skip"); step.Status = "Already applied";
                        }
                        else { step.Status = "Failed"; step.Details = pick.Error.Trim(); plan.IsValid = false; blocked = true; }
                    }
                }
                finally
                {
                    // Only our newly generated GUID directory under the fixed scratch parent may be deleted.
                    var resolved = System.IO.Path.GetFullPath(scratch);
                    if (System.IO.Path.GetDirectoryName(resolved) == System.IO.Path.GetFullPath(scratchRoot)
                        && Regex.IsMatch(System.IO.Path.GetFileName(resolved), "^[a-f0-9]{32}$") && Directory.Exists(resolved))
                    {
                        try { DeleteScratchTree(resolved, resolved); }
                        catch (IOException ex) { Trace.TraceWarning("Git preview cleanup: " + ex.Message); }
                        catch (UnauthorizedAccessException ex) { Trace.TraceWarning("Git preview cleanup: " + ex.Message); }
                    }
                }
            }
            plan.Warnings = warnings.AsReadOnly();
            return plan;
        }

        private static void DeleteScratchTree(string directory, string scratch)
        {
            var full = System.IO.Path.GetFullPath(directory);
            var root = System.IO.Path.GetFullPath(scratch).TrimEnd('\\');
            if (full != root && !full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Scratch cleanup path is outside the preview directory.");
            foreach (var entry in Directory.GetFileSystemEntries(full))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attrs & FileAttributes.Directory) != 0) Directory.Delete(entry, false);
                    else File.Delete(entry);
                }
                else if ((attrs & FileAttributes.Directory) != 0) DeleteScratchTree(entry, scratch);
                else { File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly); File.Delete(entry); }
            }
            Directory.Delete(full, false);
        }

        public static void RequireClean(string root)
        {
            if (GitCli.Run(root, false, "status", "--porcelain", "--untracked-files=all").Output.Length != 0)
                throw new InvalidOperationException("The working tree has uncommitted or untracked changes. Commit or stash them before applying.");
            foreach (var operation in new[] { "CHERRY_PICK_HEAD", "MERGE_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer" })
            {
                var file = Output(root, "rev-parse", "--git-path", operation);
                if (!System.IO.Path.IsPathRooted(file)) file = System.IO.Path.Combine(root, file);
                if (File.Exists(file) || Directory.Exists(file)) throw new InvalidOperationException("Another Git operation is active: " + operation);
            }
        }

        public GitTransferSession Apply(GitTransferPlan plan)
        {
            if (plan == null || !plan.IsValid) throw new InvalidOperationException("Update a valid plan before applying.");
            RequireClean(plan.Root);
            if (Resolve(plan.Root, "refs/heads/" + plan.Source) != plan.SourceHead || Resolve(plan.Root, "refs/heads/" + plan.Target) != plan.TargetHead)
                throw new InvalidOperationException("A branch changed since preview. Update the plan again.");
            if (plan.Policy == null || LoadPolicy(plan.Root, plan.Target).Fingerprint != plan.Policy.Fingerprint)
                throw new InvalidOperationException("Git merge policies changed since preview. Update the plan.");
            GitCli.Run(plan.Root, false, "var", "GIT_AUTHOR_IDENT");
            GitCli.Run(plan.Root, false, "var", "GIT_COMMITTER_IDENT");
            if (HasRecoverableSession(plan.Root))
                throw new InvalidOperationException("A saved Git transfer already exists. Recover or inspect it before starting a new transfer.");
            GitCli.Run(plan.Root, false, "switch", "--", plan.Target);
            var session = new GitTransferSession { Plan = plan, ExpectedHead = plan.TargetHead, Status = "Running", Conflicts = new List<string>().AsReadOnly() };
            SaveSession(session);
            Advance(session);
            return session;
        }

        private static bool HasPick(string root) { return GitCli.Run(root, true, "rev-parse", "--verify", "CHERRY_PICK_HEAD").Code == 0; }
        private static bool IsEmptyPick(string root) { return GitCli.Run(root, true, "diff", "--quiet", "HEAD", "--").Code == 0 && GetConflicts(root).Count == 0; }
        private static IReadOnlyList<string> GetConflicts(string root) { return Paths(GitCli.Run(root, false, "diff", "--name-only", "--diff-filter=U", "-z").Output).ToList().AsReadOnly(); }
        private static void CheckSession(GitTransferSession session)
        {
            if (session == null || !session.IsActive) throw new InvalidOperationException("No Git transfer is active.");
            if (Output(session.Plan.Root, "branch", "--show-current") != session.Plan.Target || Resolve(session.Plan.Root, "HEAD") != session.ExpectedHead)
                throw new InvalidOperationException("The working branch or HEAD changed outside MultiMerge. Inspect Git state before continuing.");
        }
        private static void CheckPendingScope(GitTransferSession session)
        {
            var allowed = new HashSet<string>(session.Plan.Steps[session.NextIndex].Paths ??
                session.Plan.Steps[session.NextIndex].Commit.Paths, StringComparer.Ordinal);
            var changed = Paths(GitCli.Run(session.Plan.Root, false, "diff", "--name-only", "HEAD", "-z", "--").Output);
            if (changed.Any(path => !allowed.Contains(path))) throw new InvalidOperationException("Unrelated files changed during conflict resolution. Keep them out of this transfer before continuing or aborting.");
        }

        private static void Advance(GitTransferSession session)
        {
            try { AdvanceCore(session); }
            catch (Exception ex)
            {
                session.Status = "Paused"; session.Message = "Transfer paused: " + ex.Message;
                try { session.Conflicts = GetConflicts(session.Plan.Root); }
                catch { session.Conflicts = new List<string>().AsReadOnly(); }
                SaveSession(session);
            }
        }

        private static void AdvanceCore(GitTransferSession session)
        {
            CheckSession(session);
            var root = session.Plan.Root;
            while (session.NextIndex < session.Plan.Steps.Count)
            {
                var step = session.Plan.Steps[session.NextIndex];
                if (step.Status == "Already applied" || step.Status == "Skipped by policy") { session.NextIndex++; SaveSession(session); continue; }
                RequireClean(root);
                GitResult pick;
                try { pick = session.Plan.Policy.Effective.PathRules.Any() || session.Plan.Policy.Effective.LineRules.Any()
                    ? PickWithPolicy(root, step, session.Plan.Policy)
                    : GitCli.Run(root, true, PickArguments(step, false)); }
                catch (InvalidOperationException ex) { session.Status = "Paused"; session.Message = ex.Message;
                    session.Conflicts = GetConflicts(root); step.Status = "Failed"; SaveSession(session); return; }
                if (pick.Code == 0)
                {
                    if (step.Status != "Skipped by policy") step.Status = "Applied";
                    session.ExpectedHead = Resolve(root, "HEAD"); session.NextIndex++; SaveSession(session); continue;
                }
                session.Conflicts = GetConflicts(root);
                if (session.Conflicts.Count == 0 && HasPick(root) && IsEmptyPick(root))
                {
                    GitCli.Run(root, false, "cherry-pick", "--skip"); step.Status = "Already applied"; session.NextIndex++; SaveSession(session); continue;
                }
                session.Status = "Paused"; session.Message = pick.Error.Trim() + "\n" + pick.Output.Trim();
                step.Status = session.Conflicts.Count == 0 ? "Failed" : "Conflict";
                SaveSession(session);
                return;
            }
            session.Status = "Completed"; session.Message = "Transfer completed. Build and test the target before pushing.";
            session.Conflicts = new List<string>().AsReadOnly();
            SaveSession(session);
        }

        public void Continue(GitTransferSession session)
        {
            CheckSession(session); CheckPendingScope(session);
            var root = session.Plan.Root;
            if (GetConflicts(root).Count != 0) throw new InvalidOperationException("Resolve and stage all conflicting files first.");
            CheckProtectedStagedFiles(session);
            if (HasPick(root))
            {
                if (Output(root, "rev-parse", "CHERRY_PICK_HEAD") != session.Plan.Steps[session.NextIndex].Commit.Sha)
                    throw new InvalidOperationException("A different cherry-pick is active.");
                var result = IsEmptyPick(root) ? GitCli.Run(root, false, "cherry-pick", "--skip")
                    : GitCli.Run(root, false, "-c", "core.editor=true", "cherry-pick", "--continue");
                session.Plan.Steps[session.NextIndex].Status = IsEmptyPick(root) && Resolve(root, "HEAD") == session.ExpectedHead ? "Already applied" : "Applied";
                session.ExpectedHead = Resolve(root, "HEAD"); session.NextIndex++;
                SaveSession(session);
            }
            Advance(session);
        }

        public void Abort(GitTransferSession session)
        {
            CheckSession(session);
            if (HasPick(session.Plan.Root))
            {
                CheckPendingScope(session);
                if (Output(session.Plan.Root, "rev-parse", "CHERRY_PICK_HEAD") != session.Plan.Steps[session.NextIndex].Commit.Sha)
                    throw new InvalidOperationException("A different cherry-pick is active.");
                GitCli.Run(session.Plan.Root, false, "cherry-pick", "--abort");
            }
            session.Status = "Aborted"; session.Conflicts = new List<string>().AsReadOnly();
            session.Message = "Current cherry-pick aborted. Earlier completed commits remain on the target branch.";
            SaveSession(session);
        }
        private static string LocalPath(string root, string relative)
        {
            if (System.IO.Path.IsPathRooted(relative)) throw new InvalidOperationException("Invalid conflict path.");
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            var prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Conflict path escapes the working tree.");
            for (var entry = path; entry != null && entry.Length >= prefix.Length; entry = System.IO.Path.GetDirectoryName(entry))
                if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Edit symbolic links outside the embedded conflict editor.");
            return path;
        }

        public GitConflictContent ReadConflict(GitTransferSession session, string path)
        {
            CheckSession(session);
            if (!GetConflicts(session.Plan.Root).Contains(path)) throw new InvalidOperationException("This file no longer has a Git conflict.");
            var current = GitCli.Run(session.Plan.Root, true, "show", ":2:" + path);
            var incoming = GitCli.Run(session.Plan.Root, true, "show", ":3:" + path);
            var local = LocalPath(session.Plan.Root, path);
            var result = File.Exists(local) ? File.ReadAllText(local, Encoding.UTF8) : "";
            var editable = current.Code == 0 && incoming.Code == 0 && new[] { current.Output, incoming.Output, result }.All(text => text.IndexOf('\0') < 0 && text.IndexOf('\ufffd') < 0);
            var bytes = File.Exists(local) ? File.ReadAllBytes(local) : new byte[0];
            return new GitConflictContent
            {
                Path = path, Current = current.Output, Incoming = incoming.Output, Result = result, CanEdit = editable,
                Note = editable ? "Edit the result, remove conflict markers, then Save and stage." : "Binary, non-UTF-8 or structural conflict: resolve and stage it in Visual Studio or another Git tool, then Continue.",
                Bom = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191, CrLf = result.Contains("\r\n")
            };
        }

        public void SaveConflict(GitTransferSession session, GitConflictContent content, string result)
        {
            CheckSession(session); CheckPendingScope(session);
            if (!content.CanEdit || !GetConflicts(session.Plan.Root).Contains(content.Path)) throw new InvalidOperationException("This conflict cannot be edited here.");
            if (result.IndexOf('\0') >= 0 || Regex.IsMatch(result, @"(?m)^(<<<<<<< |=======\r?$|>>>>>>> )"))
                throw new InvalidOperationException("Remove all conflict markers before saving and staging.");
            CheckProtectedResult(session, content.Path, result);
            var text = result.Replace("\r\n", "\n");
            if (content.CrLf) text = text.Replace("\n", "\r\n");
            File.WriteAllText(LocalPath(session.Plan.Root, content.Path), text, new UTF8Encoding(content.Bom));
            GitCli.Run(session.Plan.Root, false, "add", "--", content.Path);
            session.Conflicts = GetConflicts(session.Plan.Root);
            SaveSession(session);
        }
    }
}
