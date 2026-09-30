// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MultiMerge
{
    internal sealed class GitSessionRecord
    {
        public int Version { get; set; }
        public string Root { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string SourceHead { get; set; }
        public string TargetHead { get; set; }
        public string ExpectedHead { get; set; }
        public string PolicyFingerprint { get; set; }
        public int NextIndex { get; set; }
        public string Status { get; set; }
        public string[] CommitIds { get; set; }
        public int[] Mainlines { get; set; }
        public string[] StepStatuses { get; set; }
    }

    public sealed partial class GitTransferEngine
    {
        private static string SessionFile(string root)
        {
            var gitDirectory = Output(root, "rev-parse", "--absolute-git-dir");
            return Path.Combine(Path.GetFullPath(gitDirectory), "multimerge-session.json");
        }

        private static void SaveSession(GitTransferSession session)
        {
            var path = SessionFile(session.Plan.Root);
            if (!session.IsActive) { if (File.Exists(path)) File.Delete(path); return; }
            var record = new GitSessionRecord {
                Version = 1, Root = session.Plan.Root, Source = session.Plan.Source, Target = session.Plan.Target,
                SourceHead = session.Plan.SourceHead, TargetHead = session.Plan.TargetHead,
                ExpectedHead = session.ExpectedHead, PolicyFingerprint = session.Plan.Policy.Fingerprint,
                NextIndex = session.NextIndex, Status = session.Status,
                CommitIds = session.Plan.Steps.Select(s => s.Commit.Sha).ToArray(),
                Mainlines = session.Plan.Steps.Select(s => s.Mainline).ToArray(),
                StepStatuses = session.Plan.Steps.Select(s => s.Status).ToArray()
            };
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public bool HasRecoverableSession(string path)
        {
            return File.Exists(SessionFile(Inspect(path).Root));
        }

        public GitTransferSession Recover(string path)
        {
            var root = Inspect(path).Root;
            var file = SessionFile(root);
            if (!File.Exists(file)) return null;
            GitSessionRecord data;
            try { data = new JavaScriptSerializer().Deserialize<GitSessionRecord>(File.ReadAllText(file, Encoding.UTF8)); }
            catch (Exception ex) { throw new InvalidOperationException("The saved Git transfer cannot be read: " + ex.Message, ex); }
            if (data == null || data.Version != 1 || data.Root != root || data.CommitIds == null ||
                data.Mainlines == null || data.StepStatuses == null || data.CommitIds.Length == 0 ||
                data.CommitIds.Length != data.Mainlines.Length || data.CommitIds.Length != data.StepStatuses.Length ||
                data.NextIndex < 0 || data.NextIndex > data.CommitIds.Length)
                throw new InvalidOperationException("Saved Git transfer data is invalid. Inspect .git/multimerge-session.json before proceeding.");
            CheckBranches(root, data.Source, data.Target);
            if (Resolve(root, "refs/heads/" + data.Source) != data.SourceHead ||
                Output(root, "branch", "--show-current") != data.Target || Resolve(root, "HEAD") != data.ExpectedHead)
                throw new InvalidOperationException("The repository changed since the saved Git transfer. MultiMerge will not resume it automatically.");
            var policy = LoadPolicyAt(root, data.TargetHead);
            if (policy.Fingerprint != data.PolicyFingerprint)
                throw new InvalidOperationException("Git merge policies changed since the saved transfer. Inspect the repository before resuming.");
            var all = LoadCommits(root, data.Source, data.Target).ToDictionary(c => c.Sha);
            var steps = new List<GitTransferStep>();
            for (int i = 0; i < data.CommitIds.Length; i++)
            {
                GitCommitInfo commit;
                if (!all.TryGetValue(data.CommitIds[i], out commit))
                {
                    // A commit already contained verbatim in the target is excluded by rev-list source --not target.
                    // It can still be part of a multi-target selection, so restore it from the saved SHA.
                    if (!IsAncestor(root, data.CommitIds[i], data.Source) ||
                        !IsAncestor(root, data.CommitIds[i], data.Target))
                        throw new InvalidOperationException("A selected source commit is no longer available: " + data.CommitIds[i]);
                    commit = ReadCommit(root, data.CommitIds[i], true);
                }
                var step = new GitTransferStep { Commit = commit, Mainline = data.Mainlines[i], Status = data.StepStatuses[i], Details = "" };
                step.Paths = ChangedPaths(root, step);
                steps.Add(step);
            }
            var plan = new GitTransferPlan {
                Root = root, Source = data.Source, Target = data.Target, SourceHead = data.SourceHead,
                TargetHead = data.TargetHead, Policy = policy, IsValid = true, Steps = steps.AsReadOnly(),
                Warnings = new List<string>().AsReadOnly()
            };
            var session = new GitTransferSession {
                Plan = plan, ExpectedHead = data.ExpectedHead, NextIndex = data.NextIndex, Status = "Paused",
                Conflicts = GetConflicts(root), Message = "Saved Git transfer recovered. Inspect the current cherry-pick, then Continue or Abort."
            };
            if (data.NextIndex == data.CommitIds.Length)
            {
                if (HasPick(root) || session.Conflicts.Count != 0)
                    throw new InvalidOperationException("The saved transfer ended with unexpected Git conflicts.");
                Advance(session);
                return session;
            }
            if (HasPick(root) && Output(root, "rev-parse", "CHERRY_PICK_HEAD") != steps[data.NextIndex].Commit.Sha)
                throw new InvalidOperationException("A different cherry-pick is active. MultiMerge will not resume it.");
            if (!HasPick(root) && session.Conflicts.Count != 0)
                throw new InvalidOperationException("Git has unresolved files without the expected cherry-pick.");
            return session;
        }
    }
}
