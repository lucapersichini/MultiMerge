// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MultiMerge
{
    public sealed class GitPolicySnapshot
    {
        public EffectiveMergePolicy Effective { get; internal set; }
        public string Fingerprint { get; internal set; }
        public string Description { get; internal set; }
    }

    public static class GitTaskLinks
    {
        public static bool MatchesTaskId(string comment, int taskId)
        {
            if (taskId <= 0) return false;
            var exact = taskId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pattern = @"(?i)(?:\bAB#\s*|\b(?:task|work\s*item)\s*#?\s*)" +
                Regex.Escape(exact) + @"(?!\d)";
            return Regex.IsMatch(comment ?? "", pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        public static IReadOnlyList<string> CommitIds(IEnumerable<string> artifactUris)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uri in artifactUris ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(uri) ||
                    uri.IndexOf("Git/Commit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match match in Regex.Matches(Uri.UnescapeDataString(uri),
                    @"(?i)(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])", RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
                    ids.Add(match.Value.ToLowerInvariant());
            }
            return ids.ToList().AsReadOnly();
        }
    }

    public sealed partial class GitTransferEngine
    {
        public const string GitTeamPolicyFile = ".automerge-policy.json";
        public static string GitPersonalPolicyFile { get { return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiMerge", "git-policy.personal.json"); } }

        public GitPolicySnapshot LoadPolicy(string root, string target)
        {
            var branch = "refs/heads/" + target;
            Resolve(root, branch);
            return LoadPolicyAt(root, branch);
        }

        private GitPolicySnapshot LoadPolicyAt(string root, string branch)
        {
            var teamResult = GitCli.Run(root, true, "show", branch + ":" + GitTeamPolicyFile);
            string team = teamResult.Code == 0 ? teamResult.Output : null;
            // Distinguish a missing file from a broken branch/object.
            if (teamResult.Code != 0 && GitCli.Run(root, false, "ls-tree", "--name-only", branch, "--", GitTeamPolicyFile)
                    .Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Contains(GitTeamPolicyFile))
                throw new InvalidOperationException("Cannot read the target's team Git policy.");
            var personal = File.Exists(GitPersonalPolicyFile) ? File.ReadAllText(GitPersonalPolicyFile, new UTF8Encoding(false, true)) : null;
            var effective = new EffectiveMergePolicy(
                team == null ? null : MergePolicyEngine.Parse(team),
                personal == null ? null : MergePolicyEngine.Parse(personal));
            if (effective.Errors.Count != 0) throw new InvalidOperationException("Invalid Git merge policy: " + string.Join("; ", effective.Errors));
            var text = (team ?? "") + "\0" + (personal ?? "");
            using (var hash = SHA256.Create())
            {
                return new GitPolicySnapshot {
                    Effective = effective,
                    Fingerprint = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", ""),
                    Description = "Team: " + (team == null ? "none" : GitTeamPolicyFile) + "; personal: " +
                        (personal == null ? "none" : GitPersonalPolicyFile) +
                        "; " + effective.PathRules.Count + " path rule(s), " + effective.LineRules.Count + " line rule(s)"
                };
            }
        }

        private static IReadOnlyList<string> ChangedPaths(string root, GitTransferStep step)
        {
            if (!step.Commit.IsMerge) return step.Commit.Paths;
            var parents = Output(root, "rev-list", "--parents", "-n", "1", step.Commit.Sha).Split(' ');
            if (step.Mainline < 1 || step.Mainline >= parents.Length)
                throw new InvalidOperationException("Select a valid mainline parent for merge commit " + step.Commit.ShortSha + ".");
            return Paths(GitCli.Run(root, false, "diff", "--name-only", "-z", parents[step.Mainline], step.Commit.Sha).Output).ToList().AsReadOnly();
        }

        private static string[] PickArguments(GitTransferStep step, bool noCommit)
        {
            var args = new List<string> { "cherry-pick" };
            if (noCommit) args.Add("--no-commit");
            else args.Add("-x");
            if (step.Commit.IsMerge) { args.Add("-m"); args.Add(step.Mainline.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            args.Add(step.Commit.Sha);
            return args.ToArray();
        }

        private static string ReadBlob(string root, string revision, string path)
        {
            var result = GitCli.Run(root, true, "show", revision + ":" + path);
            if (result.Code != 0 || result.Output.IndexOf('\0') >= 0 || result.Output.IndexOf('\ufffd') >= 0)
                throw new InvalidOperationException("Line policy needs a UTF-8 text file present in base, source and target: " + path);
            return result.Output;
        }

        private static string ParentFor(GitTransferStep step, string root)
        {
            var parents = Output(root, "rev-list", "--parents", "-n", "1", step.Commit.Sha).Split(' ');
            return parents[step.Commit.IsMerge ? step.Mainline : 1];
        }

        private static void CheckProtectedResult(GitTransferSession session, string path, string result)
        {
            var decision = MergePolicyEngine.Decide(session.Plan.Policy.Effective, path);
            if (decision.LineRules.Count == 0) return;
            var original = ReadBlob(session.Plan.Root, session.ExpectedHead, path);
            var violations = MergePolicyEngine.CompareProtectedLines(original, result,
                decision.LineRules.Select(r => r.Rule).ToList().AsReadOnly());
            if (violations.Count != 0)
                throw new InvalidOperationException("Protected lines changed in " + path + ": " + string.Join("; ", violations));
        }

        private static void CheckProtectedStagedFiles(GitTransferSession session)
        {
            var root = session.Plan.Root;
            var paths = session.Plan.Steps[session.NextIndex].Paths;
            foreach (var path in paths)
            {
                var decision = MergePolicyEngine.Decide(session.Plan.Policy.Effective, path);
                if (decision.LineRules.Count == 0) continue;
                var staged = GitCli.Run(root, true, "show", ":" + path);
                if (staged.Code != 0)
                    throw new InvalidOperationException("Protected file " + path + " is missing from the Git index.");
                CheckProtectedResult(session, path, staged.Output);
            }
        }

        // A policy pick never stages unrelated files: Apply starts from a clean working tree.
        // A full Skip omits the commit; a full Discard records an empty provenance commit.
        private static GitResult PickWithPolicy(string root, GitTransferStep step, GitPolicySnapshot policy)
        {
            var paths = step.Paths ?? ChangedPaths(root, step);
            var decisions = paths.ToDictionary(p => p, p => MergePolicyEngine.Decide(policy.Effective, p), StringComparer.Ordinal);
            var policyNotes = decisions.Where(d => d.Value.Action != MergePolicyAction.Merge)
                .Select(d => d.Value.Action + " " + d.Key).ToList();
            if (decisions.Count == 0 || decisions.Values.All(d => d.Action == MergePolicyAction.Skip))
            {
                step.Status = "Skipped by policy";
                step.Details = string.Join("; ", policyNotes);
                return new GitResult { Code = 0, Output = "Skipped by policy.", Error = "" };
            }
            var targetHead = Output(root, "rev-parse", "HEAD");
            var protectedResults = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in decisions.Where(d => d.Value.Action == MergePolicyAction.Merge && d.Value.LineRules.Count > 0))
            {
                var rules = entry.Value.LineRules.Select(r => r.Rule).ToList().AsReadOnly();
                var merger = MergePolicyEngine.MergeWithProtectedLines(
                    ReadBlob(root, ParentFor(step, root), entry.Key),
                    ReadBlob(root, step.Commit.Sha, entry.Key),
                    ReadBlob(root, targetHead, entry.Key), rules);
                if (merger.HasConflicts)
                    throw new InvalidOperationException("Protected-line policy needs manual review for " + entry.Key +
                        ". No commit was applied; change the policy or resolve this case separately.");
                if (merger.KeptTargetDifferences.Count != 0)
                    policyNotes.Add("Kept target lines in " + entry.Key + ": " + merger.KeptTargetDifferences.Count +
                        " source change(s) require manual review");
                protectedResults[entry.Key] = ThreeWayMerge.BuildTextWithMarkers(merger.Merge, "", "");
            }
            step.Details = string.Join("; ", policyNotes);
            var pick = GitCli.Run(root, true, PickArguments(step, true));
            foreach (var entry in decisions.Where(d => d.Value.Action != MergePolicyAction.Merge))
            {
                // Restore both index and worktree to the target version, including an absent path.
                var restored = GitCli.Run(root, true, "restore", "--source=" + targetHead, "--staged", "--worktree", "--", entry.Key);
                if (restored.Code != 0)
                {
                    GitCli.Run(root, true, "rm", "-f", "--", entry.Key);
                    if (File.Exists(Path.Combine(root, entry.Key))) File.Delete(Path.Combine(root, entry.Key));
                }
            }
            foreach (var entry in protectedResults)
            {
                var local = LocalPath(root, entry.Key);
                File.WriteAllText(local, entry.Value, new UTF8Encoding(false));
                GitCli.Run(root, false, "add", "--", entry.Key);
            }
            var conflicts = GetConflicts(root);
            if (conflicts.Count != 0) return new GitResult { Code = 1, Error = pick.Error, Output = pick.Output };
            if (pick.Code != 0 && !HasPick(root) && !IsEmptyPick(root))
                return pick;
            var message = step.Commit.Comment.TrimEnd() + "\n\n(cherry picked from commit " + step.Commit.Sha + ")";
            var empty = IsEmptyPick(root);
            if (empty && decisions.Values.All(d => d.Action == MergePolicyAction.Skip))
            {
                if (HasPick(root)) GitCli.Run(root, false, "cherry-pick", "--skip");
                step.Status = "Skipped by policy";
                return new GitResult { Code = 0, Output = "Skipped by policy.", Error = "" };
            }
            var commit = empty ? GitCli.Run(root, true, "commit", "--allow-empty", "-m", message) : GitCli.Run(root, true, "commit", "-m", message);
            return commit;
        }

        public IReadOnlyList<GitCommitInfo> FindTaskCommits(string path, string source, string target, int taskId)
        {
            if (taskId <= 0) throw new InvalidOperationException("Enter a positive task ID.");
            return LoadCommits(path, source, target).Where(c => GitTaskLinks.MatchesTaskId(c.Comment, taskId))
                .ToList().AsReadOnly();
        }
    }
}
