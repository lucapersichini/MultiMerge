// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MultiMerge
{
    public sealed class GitBatchPlan
    {
        public IReadOnlyList<GitTransferPlan> Targets { get; internal set; }
        public int CommitCount { get { return Targets.Count == 0 ? 0 : Targets[0].Steps.Count; } }
        public bool IsValid { get { return Targets.Count > 0 && Targets.All(p => p.IsValid); } }
    }

    public sealed class GitBatchSession
    {
        public GitBatchPlan Plan { get; internal set; }
        public int TargetIndex { get; internal set; }
        public GitTransferSession Current { get; internal set; }
        public string Status { get; internal set; }
        public string Message { get; internal set; }
        public bool IsActive { get { return Status != "Completed" && Status != "Aborted"; } }
    }

    internal sealed class GitBatchRecord
    {
        public int Version { get; set; }
        public string Root { get; set; }
        public string Source { get; set; }
        public string SourceHead { get; set; }
        public string[] Targets { get; set; }
        public string[] TargetHeads { get; set; }
        public string[] PolicyFingerprints { get; set; }
        public string[] CommitIds { get; set; }
        public int[] Mainlines { get; set; }
        public int TargetIndex { get; set; }
    }

    public sealed partial class GitTransferEngine
    {
        private static string BatchFile(string root)
        {
            return Path.Combine(Output(root, "rev-parse", "--absolute-git-dir"), "multimerge-batch.json");
        }

        public GitBatchPlan PreviewTargets(string path, string source, IEnumerable<string> targets, IEnumerable<string> selected,
            IReadOnlyDictionary<string, int> mainlines)
        {
            var list = targets.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (list.Count == 0 || list.Count != list.Distinct(StringComparer.Ordinal).Count())
                throw new InvalidOperationException("Choose at least one unique target branch.");
            if (list.Contains(source)) throw new InvalidOperationException("A target cannot also be the source.");
            var ids = selected.ToList();
            return new GitBatchPlan { Targets = list.Select(t => Preview(path, source, t, ids, mainlines)).ToList().AsReadOnly() };
        }

        private static void SaveBatch(GitBatchSession session)
        {
            var path = BatchFile(session.Plan.Targets[0].Root);
            if (!session.IsActive) { if (File.Exists(path)) File.Delete(path); return; }
            var plans = session.Plan.Targets;
            var record = new GitBatchRecord {
                Version = 1, Root = plans[0].Root, Source = plans[0].Source, SourceHead = plans[0].SourceHead,
                Targets = plans.Select(p => p.Target).ToArray(), TargetHeads = plans.Select(p => p.TargetHead).ToArray(),
                PolicyFingerprints = plans.Select(p => p.Policy.Fingerprint).ToArray(),
                CommitIds = plans[0].Steps.Select(s => s.Commit.Sha).ToArray(),
                Mainlines = plans[0].Steps.Select(s => s.Mainline).ToArray(),
                TargetIndex = session.TargetIndex
            };
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public bool HasRecoverableBatch(string path) { return File.Exists(BatchFile(Inspect(path).Root)); }

        public GitBatchSession ApplyBatch(GitBatchPlan plan)
        {
            if (plan == null || !plan.IsValid) throw new InvalidOperationException("Preview all target branches first.");
            var root = plan.Targets[0].Root;
            RequireClean(root);
            if (HasRecoverableBatch(root) || HasRecoverableSession(root))
                throw new InvalidOperationException("A saved Git transfer already exists. Recover it before starting another batch.");
            foreach (var target in plan.Targets)
            {
                if (target.Root != root || target.Source != plan.Targets[0].Source ||
                    target.SourceHead != Resolve(root, "refs/heads/" + target.Source) ||
                    target.TargetHead != Resolve(root, "refs/heads/" + target.Target) ||
                    target.Policy.Fingerprint != LoadPolicy(root, target.Target).Fingerprint)
                    throw new InvalidOperationException("A source branch, target branch or policy changed since preview. Update every plan.");
            }
            var session = new GitBatchSession { Plan = plan, Status = "Running" };
            SaveBatch(session);
            AdvanceBatch(session);
            return session;
        }

        private void AdvanceBatch(GitBatchSession session)
        {
            while (session.TargetIndex < session.Plan.Targets.Count)
            {
                // No target beyond this point was touched when an earlier one pauses.
                if (session.Current == null)
                    session.Current = Apply(session.Plan.Targets[session.TargetIndex]);
                if (session.Current.IsActive)
                {
                    session.Status = "Paused";
                    session.Message = "Target " + session.Current.Plan.Target + " paused: " + session.Current.Message;
                    SaveBatch(session);
                    return;
                }
                session.TargetIndex++;
                session.Current = null;
                SaveBatch(session);
            }
            session.Status = "Completed";
            session.Message = "All " + session.Plan.Targets.Count + " target branches completed locally. Build and test each before pushing.";
            SaveBatch(session);
        }

        public void ContinueBatch(GitBatchSession session)
        {
            if (session == null || !session.IsActive) throw new InvalidOperationException("No Git batch is active.");
            if (session.Current != null && session.Current.IsActive) Continue(session.Current);
            if (session.Current != null && session.Current.IsActive)
            {
                session.Message = "Target " + session.Current.Plan.Target + " still needs attention.";
                SaveBatch(session);
                return;
            }
            AdvanceBatch(session);
        }

        public void AbortBatch(GitBatchSession session)
        {
            if (session == null || !session.IsActive) throw new InvalidOperationException("No Git batch is active.");
            if (session.Current != null && session.Current.IsActive) Abort(session.Current);
            session.Status = "Aborted";
            session.Message = "Current target aborted. Earlier completed target branches remain; later targets were not touched.";
            SaveBatch(session);
        }

        public GitBatchSession RecoverBatch(string path)
        {
            var root = Inspect(path).Root;
            var file = BatchFile(root);
            if (!File.Exists(file)) return null;
            GitBatchRecord data;
            try { data = new JavaScriptSerializer().Deserialize<GitBatchRecord>(File.ReadAllText(file, Encoding.UTF8)); }
            catch (Exception ex) { throw new InvalidOperationException("Cannot read the saved Git batch: " + ex.Message, ex); }
            if (data == null || data.Version != 1 || data.Root != root || data.Targets == null ||
                data.TargetHeads == null || data.PolicyFingerprints == null || data.CommitIds == null ||
                data.Mainlines == null || data.Targets.Length == 0 ||
                data.Targets.Length != data.TargetHeads.Length || data.Targets.Length != data.PolicyFingerprints.Length ||
                data.CommitIds.Length == 0 || data.CommitIds.Length != data.Mainlines.Length ||
                data.TargetIndex < 0 || data.TargetIndex > data.Targets.Length)
                throw new InvalidOperationException("Saved Git batch data is invalid. Inspect the journal before proceeding.");
            if (Resolve(root, "refs/heads/" + data.Source) != data.SourceHead)
                throw new InvalidOperationException("The source branch changed after the Git batch was saved.");
            if (data.TargetIndex == data.Targets.Length)
            {
                var finished = new GitBatchSession {
                    Plan = new GitBatchPlan { Targets = data.Targets.Select((t, i) => new GitTransferPlan {
                        Root = root, Source = data.Source, Target = t, SourceHead = data.SourceHead,
                        TargetHead = data.TargetHeads[i], Policy = new GitPolicySnapshot { Fingerprint = data.PolicyFingerprints[i] },
                        IsValid = true, Steps = new List<GitTransferStep>().AsReadOnly(),
                        Warnings = new List<string>().AsReadOnly()
                    }).ToList().AsReadOnly() }, TargetIndex = data.TargetIndex, Status = "Completed",
                    Message = "The saved Git batch had completed all target branches."
                };
                SaveBatch(finished);
                return finished;
            }
            var selections = data.CommitIds.Zip(data.Mainlines, (sha, number) => new { sha, number })
                .ToDictionary(x => x.sha, x => x.number);
            var plans = new List<GitTransferPlan>();
            GitTransferSession current = HasRecoverableSession(root) ? Recover(root) : null;
            for (int i = 0; i < data.Targets.Length; i++)
            {
                if (i < data.TargetIndex)
                {
                    plans.Add(new GitTransferPlan { Root = root, Source = data.Source, Target = data.Targets[i],
                        SourceHead = data.SourceHead, TargetHead = data.TargetHeads[i], IsValid = true,
                        Steps = new List<GitTransferStep>().AsReadOnly(), Warnings = new List<string>().AsReadOnly(),
                        Policy = new GitPolicySnapshot { Fingerprint = data.PolicyFingerprints[i] } });
                    continue;
                }
                if (i == data.TargetIndex && current != null)
                {
                    if (current.Plan.Target != data.Targets[i] || current.Plan.TargetHead != data.TargetHeads[i])
                        throw new InvalidOperationException("The recovered target differs from the saved batch.");
                    plans.Add(current.Plan); continue;
                }
                if (Resolve(root, "refs/heads/" + data.Targets[i]) != data.TargetHeads[i])
                    throw new InvalidOperationException("A batch target changed after preview: " + data.Targets[i]);
                var plan = Preview(root, data.Source, data.Targets[i], data.CommitIds, selections);
                if (plan.Policy.Fingerprint != data.PolicyFingerprints[i])
                    throw new InvalidOperationException("A batch target policy changed after preview: " + data.Targets[i]);
                plans.Add(plan);
            }
            var recovered = new GitBatchSession {
                Plan = new GitBatchPlan { Targets = plans.AsReadOnly() }, TargetIndex = data.TargetIndex,
                Current = current, Status = "Paused", Message = "Saved Git batch recovered. Inspect the current target before continuing."
            };
            return recovered;
        }
    }
}
