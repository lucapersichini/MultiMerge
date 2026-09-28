using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;

namespace AutoMerge
{
    public enum TaskMergeCheckInOutcome
    {
        // Changeset creato.
        CheckedIn,

        // La valutazione di TFVC non lo consente (conflitti, note obbligatorie, policy fallite o non
        // valutabili): niente check-in automatico.
        NotAllowed,

        // Il target ha il gated check-in: il check-in passa da una build, non lo fa la scheda.
        Gated,

        // TFVC ha rifiutato il check-in (CheckinException) o non ha creato un changeset.
        Failed,

        // I pending sotto il target riletti subito prima del check-in non sono quelli del controllo
        // finale (o non si sono potuti rileggere), oppure e' cambiato il contenuto di un file che il
        // controllo aveva verificato: niente check-in, il controllo va rifatto.
        Changed
    }

    public sealed class TaskMergeCheckInResult
    {
        private TaskMergeCheckInResult(TaskMergeCheckInOutcome outcome, int changesetId, IReadOnlyList<string> problems)
        {
            Outcome = outcome;
            ChangesetId = changesetId;
            Problems = problems ?? new List<string>().AsReadOnly();
        }

        public TaskMergeCheckInOutcome Outcome { get; private set; }

        public int ChangesetId { get; private set; }

        // Motivi (in inglese, per il registro e il banner) quando il check-in non e' stato fatto.
        public IReadOnlyList<string> Problems { get; private set; }

        internal static TaskMergeCheckInResult CheckedIn(int changesetId)
        {
            return new TaskMergeCheckInResult(TaskMergeCheckInOutcome.CheckedIn, changesetId, null);
        }

        internal static TaskMergeCheckInResult NotDone(TaskMergeCheckInOutcome outcome, IEnumerable<string> problems)
        {
            return new TaskMergeCheckInResult(outcome, 0, problems.ToList().AsReadOnly());
        }
    }

    // Check-in automatico dei merge della catena "Merge from Task" (una parte, o l'ultima). Nessuna
    // UI: si chiama fuori dal thread UI. Il check-in parte solo se la valutazione di TFVC (conflitti,
    // note, policy) e' pulita; ogni altro caso torna a chi chiama, che apre Pending Changes.
    public static class TaskMergeCheckIn
    {
        // pendingChanges: ESATTAMENTE i pending controllati dal controllo finale (letti con le sorgenti
        // del merge). Subito prima del check-in i pending sotto targetPath si rileggono: se non sono
        // piu' gli stessi (merge fatto altrove dopo il controllo, undo, check-in da Pending Changes...)
        // niente check-in (Changed).
        public static TaskMergeCheckInResult Run(Workspace workspace, string targetPath, PendingChange[] pendingChanges, string comment,
            WorkItemStore workItemStore, int workItemId)
        {
            return Run(workspace, targetPath, pendingChanges, comment, workItemStore, workItemId, null);
        }

        // contentFingerprints: contenuto dei file verificato dal controllo finale (TaskMergeAuditRun.
        // ContentFingerprints: Discard invariati, righe protette). Nel riscontro dell'ultimo istante si
        // rileggono anche loro: un file cambiato dopo il controllo = niente check-in (Changed).
        public static TaskMergeCheckInResult Run(Workspace workspace, string targetPath, PendingChange[] pendingChanges, string comment,
            WorkItemStore workItemStore, int workItemId, IReadOnlyDictionary<string, string> contentFingerprints)
        {
            if (workspace == null)
                throw new ArgumentNullException("workspace");
            if (string.IsNullOrEmpty(targetPath))
                throw new ArgumentException("The target path is required.", "targetPath");
            if (pendingChanges == null || pendingChanges.Length == 0)
                throw new ArgumentException("There is nothing to check in.", "pendingChanges");
            if (workItemStore == null)
                throw new ArgumentNullException("workItemStore");

            // Il work item si rilegge adesso: qualcun altro puo' averlo modificato nel frattempo.
            var workItem = workItemStore.GetWorkItem(workItemId);
            var workItems = new[] { new WorkItemCheckinInfo(workItem, WorkItemCheckinAction.Associate) };

            var evaluation = workspace.EvaluateCheckin2(CheckinEvaluationOptions.All, pendingChanges, comment, null, workItems);
            var problems = DescribeEvaluation(evaluation);
            if (problems.Count > 0)
                return TaskMergeCheckInResult.NotDone(TaskMergeCheckInOutcome.NotAllowed, problems);

            // Ultimo riscontro, a ridosso del check-in: TFVC archivia lo stato attuale degli item, non
            // quello controllato. La finestra che resta e' quella tra questa lettura e la chiamata.
            var changed = CompareWithTarget(workspace, targetPath, pendingChanges).ToList();
            changed.AddRange(TaskMergeFileContent.CompareWithDisk(contentFingerprints));
            if (changed.Count > 0)
                return TaskMergeCheckInResult.NotDone(TaskMergeCheckInOutcome.Changed, changed);

            int changesetId;
            try
            {
                changesetId = workspace.CheckIn(new WorkspaceCheckInParameters(pendingChanges, comment)
                {
                    AssociatedWorkItems = workItems
                });
            }
            // Tipi riconosciuti per nome: la loro gerarchia passa da assembly (Services.Common) che il
            // progetto non referenzia, quindi niente catch tipizzato.
            catch (Exception ex) when (IsOfType(ex, "GatedCheckinException"))
            {
                return TaskMergeCheckInResult.NotDone(TaskMergeCheckInOutcome.Gated, new[] { ex.Message });
            }
            catch (Exception ex) when (IsOfType(ex, "CheckinException"))
            {
                return TaskMergeCheckInResult.NotDone(TaskMergeCheckInOutcome.Failed, new[] { ex.Message });
            }

            if (changesetId <= 0)
                return TaskMergeCheckInResult.NotDone(TaskMergeCheckInOutcome.Failed, new[] { "TFVC did not create a changeset." });

            return TaskMergeCheckInResult.CheckedIn(changesetId);
        }

        // Differenze tra i pending controllati e quelli sotto il target adesso (vuoto = identici).
        private static IReadOnlyList<string> CompareWithTarget(Workspace workspace, string targetPath, PendingChange[] checkedPending)
        {
            PendingChange[] current;
            try
            {
                current = TaskMergeAuditDataSource.ReadPending(workspace, targetPath);
            }
            catch (Exception ex)
            {
                return new[] { "The pending changes under the target could not be read again (" + ex.Message + ")." };
            }

            return TaskMergePendingSnapshot.Differences(
                TaskMergeAuditDataSource.ToAuditPendingList(checkedPending),
                TaskMergeAuditDataSource.ToAuditPendingList(current));
        }

        private static List<string> DescribeEvaluation(CheckinEvaluationResult evaluation)
        {
            var problems = new List<string>();
            if (evaluation == null)
                return problems;

            if (evaluation.Conflicts != null)
            {
                foreach (var conflict in evaluation.Conflicts.Where(c => c != null))
                    problems.Add("Conflict on " + (conflict.ServerItem ?? "?") + ": " + conflict.Message);
            }

            if (evaluation.NoteFailures != null)
            {
                foreach (var failure in evaluation.NoteFailures.Where(f => f != null))
                {
                    var name = failure.Definition == null ? "?" : failure.Definition.Name;
                    problems.Add("Check-in note \"" + name + "\": " + failure.Message);
                }
            }

            if (evaluation.PolicyFailures != null)
            {
                foreach (var failure in evaluation.PolicyFailures.Where(f => f != null))
                    problems.Add("Check-in policy: " + failure.Message);
            }

            if (evaluation.PolicyEvaluationException != null)
                problems.Add("Check-in policies could not be evaluated: " + evaluation.PolicyEvaluationException.Message);

            return problems;
        }

        private static bool IsOfType(Exception ex, string typeName)
        {
            for (var type = ex == null ? null : ex.GetType(); type != null; type = type.BaseType)
            {
                if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
