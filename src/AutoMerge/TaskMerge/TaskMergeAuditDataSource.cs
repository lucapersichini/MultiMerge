using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace AutoMerge
{
    // Esito del controllo finale: il risultato di TaskMergeAudit e i pending che ha controllato (sono
    // ESATTAMENTE quelli da archiviare se il controllo e' pulito: nessun pending non controllato entra
    // nel check-in).
    public sealed class TaskMergeAuditRun
    {
        internal TaskMergeAuditRun(TaskMergeAuditResult result, PendingChange[] pending, IReadOnlyList<TaskMergeStep> steps,
            IReadOnlyDictionary<string, string> contentFingerprints)
        {
            Result = result;
            Pending = pending ?? new PendingChange[0];
            Steps = steps ?? new List<TaskMergeStep>().AsReadOnly();
            ContentFingerprints = contentFingerprints ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public TaskMergeAuditResult Result { get; private set; }

        public PendingChange[] Pending { get; private set; }

        // passi controllati (quelli da consegnare con questo check-in)
        public IReadOnlyList<TaskMergeStep> Steps { get; private set; }

        // Contenuto dei file locali verificato dal controllo (Discard con contenuto invariato, righe
        // protette): path locale -> impronta al momento della verifica. Subito prima del check-in si
        // rileggono (TaskMergeCheckIn): un file cambiato dopo il controllo = niente check-in.
        public IReadOnlyDictionary<string, string> ContentFingerprints { get; private set; }

        public bool IsClean
        {
            get { return Result != null && Result.IsClean; }
        }
    }

    // Regole di merge dei passi da consegnare, fotografate sul thread UI prima del controllo (il
    // controllo gira in background e non legge i view model).
    public sealed class TaskMergeAuditPolicy
    {
        private static readonly IReadOnlyList<MergeLineRule> NoRules = new List<MergeLineRule>().AsReadOnly();

        private readonly Dictionary<TaskMergeStep, MergePolicyAction> _actions;
        private readonly Dictionary<TaskMergeStep, IReadOnlyList<MergeLineRule>> _lineRules;
        private readonly Dictionary<TaskMergeStep, TaskMergeContentSnapshot> _discardSnapshots;

        // actions: azione di esecuzione di ogni passo (assente = Merge); lineRules: righe protette dei
        // passi Merge di file (assente = nessuna); discardSnapshots: contenuto locale del target
        // registrato subito prima di ogni Discard eseguito dalla catena.
        internal TaskMergeAuditPolicy(
            IEnumerable<KeyValuePair<TaskMergeStep, MergePolicyAction>> actions,
            IEnumerable<KeyValuePair<TaskMergeStep, IReadOnlyList<MergeLineRule>>> lineRules,
            IEnumerable<KeyValuePair<TaskMergeStep, TaskMergeContentSnapshot>> discardSnapshots)
        {
            _actions = new Dictionary<TaskMergeStep, MergePolicyAction>();
            foreach (var pair in actions ?? Enumerable.Empty<KeyValuePair<TaskMergeStep, MergePolicyAction>>())
                _actions[pair.Key] = pair.Value;
            _lineRules = new Dictionary<TaskMergeStep, IReadOnlyList<MergeLineRule>>();
            foreach (var pair in lineRules ?? Enumerable.Empty<KeyValuePair<TaskMergeStep, IReadOnlyList<MergeLineRule>>>())
            {
                if (pair.Value != null && pair.Value.Count > 0)
                    _lineRules[pair.Key] = pair.Value.Where(r => r != null).ToList().AsReadOnly();
            }
            _discardSnapshots = new Dictionary<TaskMergeStep, TaskMergeContentSnapshot>();
            foreach (var pair in discardSnapshots ?? Enumerable.Empty<KeyValuePair<TaskMergeStep, TaskMergeContentSnapshot>>())
                _discardSnapshots[pair.Key] = pair.Value;
        }

        public MergePolicyAction ActionOf(TaskMergeStep step)
        {
            MergePolicyAction action;
            return step != null && _actions.TryGetValue(step, out action) ? action : MergePolicyAction.Merge;
        }

        public IReadOnlyList<MergeLineRule> LineRulesOf(TaskMergeStep step)
        {
            IReadOnlyList<MergeLineRule> rules;
            return step != null && _lineRules.TryGetValue(step, out rules) ? rules : NoRules;
        }

        internal TaskMergeContentSnapshot DiscardSnapshotOf(TaskMergeStep step)
        {
            TaskMergeContentSnapshot snapshot;
            return step != null && _discardSnapshots.TryGetValue(step, out snapshot) ? snapshot : null;
        }
    }

    // Raccoglie da TFVC, in quel momento, i dati del controllo finale (TaskMergeAudit) prima di un
    // check-in o dell'apertura di Pending Changes: pending sotto il target con le sorgenti del merge,
    // storia di ogni item sorgente sull'intervallo registrato, anteprima NoMerge di ogni passo da
    // consegnare e, con le regole di merge, il contenuto dei file (Discard invariati, righe protette
    // uguali a quelle del target). Nessuna UI e nessuna modifica al workspace: si chiama fuori dal
    // thread UI.
    public static class TaskMergeAuditDataSource
    {
        // Senza regole di merge (ogni passo e' un Merge normale).
        public static TaskMergeAuditRun Run(
            Workspace workspace,
            string targetPath,
            IReadOnlyCollection<int> selectedChangesetIds,
            Func<IReadOnlyList<PendingChange>, IReadOnlyList<TaskMergeStep>> stepsToDeliver,
            Action<string> progress)
        {
            return Run(workspace, targetPath, selectedChangesetIds, stepsToDeliver, progress, null);
        }

        // stepsToDeliver: dai pending appena letti ai passi da consegnare (per il check-in di una parte
        // sono i passi della parte, indipendenti dai pending; per una revisione senza catena, i passi
        // i cui item hanno un pending). policy: azioni e righe protette dei passi (null = nessuna regola).
        public static TaskMergeAuditRun Run(
            Workspace workspace,
            string targetPath,
            IReadOnlyCollection<int> selectedChangesetIds,
            Func<IReadOnlyList<PendingChange>, IReadOnlyList<TaskMergeStep>> stepsToDeliver,
            Action<string> progress,
            TaskMergeAuditPolicy policy)
        {
            if (workspace == null)
                throw new ArgumentNullException("workspace");
            if (string.IsNullOrEmpty(targetPath))
                throw new ArgumentException("The target path is required.", "targetPath");
            if (stepsToDeliver == null)
                throw new ArgumentNullException("stepsToDeliver");

            var report = progress ?? (text => { });
            var vcs = workspace.VersionControlServer;

            // 1. Pending sotto il target, riletti adesso con le sorgenti del merge. Un errore di lettura
            //    arriva all'audit come "pending non disponibili" (bloccante).
            report("Final check: reading the pending changes...");
            PendingChange[] pending = null;
            try
            {
                pending = ReadPending(workspace, targetPath);
            }
            catch (Exception)
            {
                pending = null;
            }

            var auditPending = ToAuditPendingList(pending);
            var steps = stepsToDeliver(pending ?? new PendingChange[0]);

            // 2. Storia degli item sorgente (inclusiva) e anteprima dei passi, su richiesta dell'audit.
            var previewed = 0;
            var stepCount = steps == null ? 0 : steps.Count;
            var input = new TaskMergeAuditInput
            {
                Pending = auditPending,
                SelectedChangesetIds = selectedChangesetIds,
                StepsToDeliver = steps,
                ChangesetsTouchingSourceItem = (item, from, to) =>
                {
                    report(string.Format(CultureInfo.InvariantCulture, "Final check: reading the history of {0}...", item));
                    Exception error;
                    var ids = TaskMergePlanDataSource.ReadItemHistory(vcs, item, to, from, to, out error);
                    if (ids == null)
                        throw new InvalidOperationException(error == null ? "unknown error" : error.Message, error);
                    return ids.AsReadOnly();
                },
                StepIsMerged = step =>
                {
                    previewed++;
                    report(string.Format(CultureInfo.InvariantCulture, "Final check: previewing step {0}/{1}...", previewed, stepCount));
                    var status = workspace.Merge(step.SourceItem, step.TargetItem,
                        new ChangesetVersionSpec(step.FromChangesetId), new ChangesetVersionSpec(step.ToChangesetId),
                        LockLevel.None,
                        step.Recursion == TaskMergeStepRecursion.Full ? RecursionType.Full : RecursionType.None,
                        MergeOptions.NoMerge);
                    if (status.NumFailures > 0)
                    {
                        var failure = status.GetFailures().FirstOrDefault();
                        throw new InvalidOperationException("TFVC preview failure: " + (failure == null ? "unknown" : failure.Message));
                    }
                    return status.NumOperations == 0 && status.NumConflicts == 0;
                }
            };

            // 3. Regole di merge: azione di ogni passo, contenuto dei Discard, righe protette. Ogni
            //    contenuto verificato lascia la sua impronta: subito prima del check-in si rilegge.
            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tempFolder = TaskMergeFileContent.NewTempFolder("audit");
            try
            {
                if (policy != null)
                {
                    input.StepAction = policy.ActionOf;
                    input.StepHasProtectedLines = step => policy.LineRulesOf(step).Count > 0;
                    input.TargetContentPreserved = step =>
                    {
                        report(string.Format(CultureInfo.InvariantCulture, "Final check: verifying the discarded content of {0}...", step.RelativePath));
                        return TargetContentPreserved(workspace, pending, step, policy, fingerprints, tempFolder);
                    };
                    input.ProtectedLineViolations = step =>
                    {
                        if (policy.LineRulesOf(step).Count > 0)
                            report(string.Format(CultureInfo.InvariantCulture, "Final check: comparing the protected lines of {0}...", step.RelativePath));
                        return ProtectedLineViolations(workspace, pending, step, policy, fingerprints, tempFolder);
                    };
                }

                var result = TaskMergeAudit.Run(input);
                return new TaskMergeAuditRun(result, pending, steps, fingerprints);
            }
            finally
            {
                TaskMergeFileContent.DeleteFolder(tempFolder);
            }
        }

        // Pending sotto il target con le sorgenti del merge (includeMergeSourceInfo): la stessa lettura
        // per il controllo finale e per il riscontro subito prima del check-in (TaskMergeCheckIn).
        internal static PendingChange[] ReadPending(Workspace workspace, string targetPath)
        {
            return workspace.GetPendingChanges(new[] { new ItemSpec(targetPath, RecursionType.Full) },
                false, int.MaxValue, null, true);
        }

        internal static IReadOnlyList<AuditPendingChange> ToAuditPendingList(IEnumerable<PendingChange> pending)
        {
            return pending == null ? null : pending.Select(p => ToAuditPending(p)).ToList().AsReadOnly();
        }

        internal static AuditPendingChange ToAuditPending(PendingChange change)
        {
            if (change == null)
                return null;
            var sources = change.MergeSources == null
                ? null
                : change.MergeSources
                    .Where(m => m != null)
                    .Select(m => new AuditMergeSource(m.ServerItem, m.VersionFrom, m.VersionTo, m.IsRename))
                    .ToList()
                    .AsReadOnly();
            return new AuditPendingChange(change.ServerItem, change.IsMerge, change.ChangeType.ToString(), sources);
        }

        // ------------------------------------------------------------------------------------------
        // R5: un Discard lascia il contenuto del target com'era prima del merge.
        // true = invariato (o nessun pending sull'item: il check-in non lo tocca); false = cambiato;
        // null = non verificabile (bloccante nell'audit). Non lancia.
        // ------------------------------------------------------------------------------------------
        private static bool? TargetContentPreserved(Workspace workspace, PendingChange[] pending, TaskMergeStep step,
            TaskMergeAuditPolicy policy, Dictionary<string, string> fingerprints, string tempFolder)
        {
            try
            {
                if (step == null)
                    return null;
                // Solo i Discard promettono il contenuto invariato: per gli altri passi la domanda non vale.
                if (policy.ActionOf(step) != MergePolicyAction.Discard)
                    return true;
                if (pending == null)
                    return null;
                var mine = PendingOf(pending, step);
                if (mine.Count == 0)
                    return true;
                // Un cambio di encoding cambia il file archiviato anche a byte invariati.
                if (mine.Any(p => (p.ChangeType & ChangeType.Encoding) == ChangeType.Encoding))
                    return false;

                var snapshot = policy.DiscardSnapshotOf(step);
                if (snapshot != null)
                {
                    // Fotografia registrata subito prima del Discard: stessi path, stesso contenuto.
                    var now = snapshot.Recapture();
                    if (now.Error != null)
                        return null;
                    foreach (var entry in snapshot.Entries.Keys.Union(now.Entries.Keys, StringComparer.OrdinalIgnoreCase))
                    {
                        string value;
                        fingerprints[entry] = now.Entries.TryGetValue(entry, out value) ? value : TaskMergeFileContent.Missing;
                    }
                    return snapshot.Differences(now).Count == 0;
                }

                // Nessuna fotografia (es. merge di una sessione precedente): ogni file in sospeso deve
                // essere identico alla versione su cui poggia il suo pending (DownloadBaseFile).
                Directory.CreateDirectory(tempFolder);
                foreach (var change in mine)
                {
                    if ((change.ChangeType & (ChangeType.Delete | ChangeType.Add | ChangeType.Branch | ChangeType.Undelete)) != 0)
                        return false;
                    if (change.ItemType == ItemType.Folder)
                        continue;

                    var local = change.LocalItem;
                    if (string.IsNullOrEmpty(local) || !File.Exists(local))
                        return false;
                    var baseFile = Path.Combine(tempFolder, Guid.NewGuid().ToString("N") + ".base");
                    change.DownloadBaseFile(baseFile);
                    if (!File.Exists(baseFile))
                        return null;
                    var localBytes = File.ReadAllBytes(local);
                    fingerprints[local] = TaskMergeFileContent.Fingerprint(localBytes);
                    if (!TaskMergeFileContent.SameBytes(localBytes, File.ReadAllBytes(baseFile)))
                        return false;
                }
                return true;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------------------------------
        // R6: nei passi Merge di file con righe protette, le righe protette del contenuto in sospeso
        // sono identiche (e in ordine) a quelle del target a Latest, scaricato adesso dal server.
        // Vuoto = ok (anche: nessuna regola, nessun pending sull'item, file nuovo nel target, che per
        // regola si porta intero); null = non verificabile (bloccante nell'audit). Non lancia.
        // ------------------------------------------------------------------------------------------
        private static IReadOnlyList<string> ProtectedLineViolations(Workspace workspace, PendingChange[] pending, TaskMergeStep step,
            TaskMergeAuditPolicy policy, Dictionary<string, string> fingerprints, string tempFolder)
        {
            var none = new List<string>().AsReadOnly();
            try
            {
                if (step == null)
                    return null;
                var rules = policy.LineRulesOf(step);
                if (rules.Count == 0 || step.Kind != TaskMergeItemKind.File || policy.ActionOf(step) != MergePolicyAction.Merge)
                    return none;
                if (pending == null)
                    return null;

                var mine = pending
                    .Where(p => p != null && SamePath(p.ServerItem, step.TargetItem))
                    .ToList();
                if (mine.Count == 0)
                    return none;
                if (mine.Count > 1)
                    return null;
                var change = mine[0];

                var vcs = workspace.VersionControlServer;
                if (!vcs.ServerItemExists(step.TargetItem, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.File))
                    return none;

                var targetItem = vcs.GetItem(step.TargetItem, VersionSpec.Latest);
                Directory.CreateDirectory(tempFolder);
                var targetFile = Path.Combine(tempFolder, Guid.NewGuid().ToString("N") + TaskMergeFileContent.TempExtension(step.TargetItem));
                targetItem.DownloadFile(targetFile);
                if (!File.Exists(targetFile))
                    return null;
                var target = ConflictResolverViewModel.ReadText(targetFile, "target", targetItem.Encoding, change.Encoding);
                if (target.Error != null)
                    return null;

                string resultText;
                var local = change.LocalItem;
                if ((change.ChangeType & ChangeType.Delete) == ChangeType.Delete)
                {
                    resultText = string.Empty;
                    if (!string.IsNullOrEmpty(local))
                        fingerprints[local] = TaskMergeFileContent.Fingerprint(local);
                }
                else
                {
                    if (string.IsNullOrEmpty(local) || !File.Exists(local))
                        return null;
                    var bytes = File.ReadAllBytes(local);
                    fingerprints[local] = TaskMergeFileContent.Fingerprint(bytes);
                    var result = ConflictResolverViewModel.DecodeBytes(bytes, "pending", change.Encoding, targetItem.Encoding);
                    if (result.Error != null)
                        return null;
                    resultText = result.Text;
                }

                return MergePolicyEngine.CompareProtectedLines(target.Text, resultText, rules);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Pending dell'item del passo: il target stesso e, per una cartella con ricorsione Full, tutto
        // cio' che sta sotto.
        private static List<PendingChange> PendingOf(IEnumerable<PendingChange> pending, TaskMergeStep step)
        {
            var target = (step.TargetItem ?? string.Empty).TrimEnd('/');
            var prefix = target + "/";
            return pending
                .Where(p => p != null && p.ServerItem != null
                    && (SamePath(p.ServerItem, target)
                        || (step.Recursion == TaskMergeStepRecursion.Full
                            && p.ServerItem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }

        private static bool SamePath(string a, string b)
        {
            if (a == null || b == null)
                return false;
            return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
