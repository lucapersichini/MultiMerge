using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AutoMerge
{
    // =============================================================================================
    // Sicurezza del "Merge from Task" (puro: nessuna dipendenza da TFVC/VS; usa i tipi del planner).
    //
    // Due strumenti indipendenti dal motore:
    // - TaskMergeDependencyAnalyzer: avvisa quando l'utente esclude un changeset del task che tocca
    //   item cambiati anche da un changeset selezionato SUCCESSIVO (il risultato puo' averne bisogno);
    // - TaskMergeAudit: controllo finale prima di ogni check-in, su dati riletti da TFVC in quel
    //   momento. Se non e' pulito il check-in e' bloccato: nessun pulsante lo aggira.
    //
    // Principio del controllo finale: nel dubbio si blocca. Un dato che manca (storia non leggibile,
    // sorgente del merge assente, anteprima in errore, callback che lancia) e' un problema BLOCCANTE,
    // mai un "via libera per mancanza di prove".
    // =============================================================================================

    // Avviso di dipendenza: il changeset escluso E tocca item cambiati anche dal selezionato S > E.
    public sealed class ChangesetDependencyWarning
    {
        public ChangesetDependencyWarning(int excludedChangesetId, int dependentChangesetId, IReadOnlyList<string> sharedSourceItems, string message)
        {
            ExcludedChangesetId = excludedChangesetId;
            DependentChangesetId = dependentChangesetId;
            SharedSourceItems = sharedSourceItems ?? new List<string>().AsReadOnly();
            Message = message;
        }

        public int ExcludedChangesetId { get; }

        public int DependentChangesetId { get; }

        // item sorgente in comune (path completi, ordinati)
        public IReadOnlyList<string> SharedSourceItems { get; }

        public string Message { get; }
    }

    public static class TaskMergeDependencyAnalyzer
    {
        // quanti item al massimo nel testo del messaggio (il resto e' "and N more")
        private const int MaxItemsInText = 5;

        // allChanges: change di TUTTI i changeset del task (selezionati e no). Un avviso per ogni
        // coppia (escluso E, selezionato S) con S > E che condividono almeno un item (confronto
        // case-insensitive, come TFVC). Un escluso SUCCESSIVO al selezionato non crea avvisi: il
        // selezionato non puo' dipendere da un changeset che viene dopo.
        public static IReadOnlyList<ChangesetDependencyWarning> Analyze(IReadOnlyList<TaskChangeInfo> allChanges, IEnumerable<int> selectedChangesetIds)
        {
            if (allChanges == null)
                throw new ArgumentNullException("allChanges");
            if (selectedChangesetIds == null)
                throw new ArgumentNullException("selectedChangesetIds");

            var selected = new HashSet<int>(selectedChangesetIds);

            // changeset -> item toccati (chiave normalizzata case-insensitive -> path del changeset)
            var itemsByChangeset = new SortedDictionary<int, Dictionary<string, string>>();
            foreach (var change in allChanges)
            {
                if (change == null)
                    continue;
                var item = NormalizeItem(change.SourceServerItem);
                if (item == null)
                    continue;

                Dictionary<string, string> items;
                if (!itemsByChangeset.TryGetValue(change.ChangesetId, out items))
                {
                    items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    itemsByChangeset.Add(change.ChangesetId, items);
                }

                if (!items.ContainsKey(item))
                    items.Add(item, item);
            }

            var excluded = itemsByChangeset.Keys.Where(id => !selected.Contains(id)).ToList();
            var chosen = itemsByChangeset.Keys.Where(id => selected.Contains(id)).ToList();

            var result = new List<ChangesetDependencyWarning>();
            foreach (var e in excluded)
            {
                var excludedItems = itemsByChangeset[e];
                foreach (var s in chosen.Where(id => id > e))
                {
                    // casing del selezionato (il piu' recente), ordine deterministico
                    var shared = itemsByChangeset[s]
                        .Where(pair => excludedItems.ContainsKey(pair.Key))
                        .Select(pair => pair.Value)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(path => path, StringComparer.Ordinal)
                        .ToList();
                    if (shared.Count == 0)
                        continue;

                    result.Add(new ChangesetDependencyWarning(e, s, shared.AsReadOnly(), DescribeDependency(e, s, shared)));
                }
            }

            return result.AsReadOnly();
        }

        private static string DescribeDependency(int excluded, int dependent, List<string> shared)
        {
            // es. "C10 is not selected, but the selected C20 also changes an item changed by C10:
            // $/P/a.cs. The result of C20 may need C10."
            var sb = new StringBuilder();
            sb.Append(SafetyText.Format("C{0} is not selected, but the selected C{1} also changes ", excluded, dependent));
            sb.Append(shared.Count == 1 ? "an item" : SafetyText.Format("{0} items", shared.Count));
            sb.Append(SafetyText.Format(" changed by C{0}: ", excluded));
            sb.Append(string.Join(", ", shared.Take(MaxItemsInText)));
            if (shared.Count > MaxItemsInText)
                sb.Append(SafetyText.Format(" and {0} more", shared.Count - MaxItemsInText));
            sb.Append(SafetyText.Format(". The result of C{0} may need C{1}.", dependent, excluded));
            return sb.ToString();
        }

        private static string NormalizeItem(string item)
        {
            if (string.IsNullOrWhiteSpace(item))
                return null;
            var trimmed = item.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Controllo finale (audit) prima del check-in
    // ---------------------------------------------------------------------------------------------

    public enum AuditIssueKind
    {
        // un pending sotto il target non e' un merge (R1)
        NotAMerge,

        // un intervallo di merge in sospeso porta changeset non selezionati (R2)
        ForeignChangeset,

        // un passo da consegnare non risulta fuso, o non si e' potuto verificare (R3)
        StepNotMerged,

        // un pending sotto il target che non appartiene a nessun passo da consegnare (R4)
        UnplannedPendingChange,

        // storia, sorgente del merge o elenco dei pending non disponibili: non si puo' verificare
        HistoryUnavailable,

        // un passo Discard della policy non ha tenuto il contenuto del target, o non si e' potuto
        // verificare (R5)
        DiscardChangedContent,

        // le righe protette dalla policy nel contenuto in sospeso non sono quelle del target, o non si e'
        // potuto verificare (R6)
        ProtectedLinesChanged,

        // passo non fuso perche' la policy dice Skip (nota, non bloccante)
        SkippedByPolicy
    }

    public sealed class AuditIssue
    {
        public AuditIssue(AuditIssueKind kind, string targetItem, string message, bool blocking)
        {
            Kind = kind;
            TargetItem = targetItem;
            Message = message;
            Blocking = blocking;
        }

        public AuditIssueKind Kind { get; }

        // item del target interessato (null se il problema non riguarda un item)
        public string TargetItem { get; }

        public string Message { get; }

        // true = il check-in e' bloccato
        public bool Blocking { get; }
    }

    // Una voce per ogni pending change sotto il target (riletta da TFVC con includeMergeSourceInfo).
    public sealed class AuditPendingChange
    {
        public AuditPendingChange(string targetItem, bool isMerge, string changeTypeText, IReadOnlyList<AuditMergeSource> mergeSources)
        {
            TargetItem = targetItem;
            IsMerge = isMerge;
            ChangeTypeText = changeTypeText;
            MergeSources = mergeSources;
        }

        public string TargetItem { get; }

        public bool IsMerge { get; }

        // es. "Edit, Merge" (solo per i messaggi)
        public string ChangeTypeText { get; }

        // null o vuoto = informazione sulla sorgente non disponibile
        public IReadOnlyList<AuditMergeSource> MergeSources { get; }
    }

    // Specchio "puro" di MergeSource di TFVC.
    public sealed class AuditMergeSource
    {
        public AuditMergeSource(string sourceItem, int versionFrom, int versionTo, bool isRename)
        {
            SourceItem = sourceItem;
            VersionFrom = versionFrom;
            VersionTo = versionTo;
            IsRename = isRename;
        }

        public string SourceItem { get; }

        public int VersionFrom { get; }

        public int VersionTo { get; }

        public bool IsRename { get; }
    }

    public sealed class TaskMergeAuditInput
    {
        public IReadOnlyList<AuditPendingChange> Pending { get; set; }

        public IReadOnlyCollection<int> SelectedChangesetIds { get; set; }

        // passi delle parti incluse in questo check-in
        public IReadOnlyList<TaskMergeStep> StepsToDeliver { get; set; }

        // (sourceItem, from, to) INCLUSIVO -> tutti i changeset che toccano l'item (null = non disponibile)
        public Func<string, int, int, IReadOnlyList<int>> ChangesetsTouchingSourceItem { get; set; }

        // anteprima 0/0/0 = true; con operazioni/conflitti = false; errore = null
        public Func<TaskMergeStep, bool?> StepIsMerged { get; set; }

        // --- Regole di merge (policy). Tutte facoltative: null = comportamento senza policy. ---

        // azione della policy per il passo (null = Merge per tutti)
        public Func<TaskMergeStep, MergePolicyAction> StepAction { get; set; }

        // per i passi Discard: il contenuto in sospeso e' identico a quello del target prima del merge
        // (null = non verificabile -> bloccante; callback assente con passi Discard -> bloccante)
        public Func<TaskMergeStep, bool?> TargetContentPreserved { get; set; }

        // per i passi Merge (file) con righe protette: violazioni (vuota = ok; null = non verificabile ->
        // bloccante). Per un passo senza righe protette deve tornare una lista VUOTA, oppure il passo va
        // escluso con StepHasProtectedLines. null = controllo non attivo.
        public Func<TaskMergeStep, IReadOnlyList<string>> ProtectedLineViolations { get; set; }

        // facoltativo: quali passi hanno righe protette (gli altri non si controllano). null = si
        // controllano tutti i passi Merge di file. Se lancia, il passo si controlla.
        public Func<TaskMergeStep, bool> StepHasProtectedLines { get; set; }
    }

    public sealed class TaskMergeAuditResult
    {
        // costruttore interno: un risultato "pulito" si ottiene solo eseguendo l'audit
        internal TaskMergeAuditResult(IReadOnlyList<AuditIssue> issues, string summary)
        {
            Issues = issues;
            Summary = summary;
        }

        // nessun problema bloccante
        public bool IsClean
        {
            get { return !Issues.Any(i => i.Blocking); }
        }

        // prima i bloccanti, poi le note (ordine stabile)
        public IReadOnlyList<AuditIssue> Issues { get; }

        public string Summary { get; }
    }

    // Controllo finale indipendente prima di OGNI check-in.
    //
    // Regole (bloccanti salvo dove detto):
    // R1 ogni pending sotto il target deve essere un merge -> NotAMerge;
    // R2 per ogni sorgente non-rename [a..b] di un merge: TUTTI i changeset che toccano l'item sorgente
    //    in [a..b] devono essere tra i selezionati -> altrimenti ForeignChangeset (con l'elenco);
    //    storia non disponibile -> HistoryUnavailable. Un merge senza alcuna sorgente non-rename (o con
    //    un intervallo non valido) non si puo' verificare -> HistoryUnavailable;
    // R3 ogni passo da consegnare deve risultare fuso (StepIsMerged true); false o null -> StepNotMerged;
    // R4 pending il cui target non e' in nessun passo da consegnare -> UnplannedPendingChange: bloccante
    //    se viola R1/R2, altrimenti nota non bloccante. Un pending SOTTO la cartella di un passo con
    //    ricorsione Full (cartella cancellata) appartiene a quel passo: il merge ricorsivo lo produce.
    //
    // Con le regole di merge (policy; tutto facoltativo, senza callback il comportamento e' quello sopra):
    // - i passi Skip non sono richiesti da R3 (nota non bloccante SkippedByPolicy) e non sono "passi da
    //   consegnare" per R4: un pending su un item saltato e' bloccante (lo Skip non e' stato rispettato);
    // R5 ogni passo Discard deve risultare fuso (R3) E con il contenuto del target (TargetContentPreserved
    //    true); false, null o callback assente -> DiscardChangedContent;
    // R6 ogni passo Merge di un file con righe protette deve avere 0 violazioni (ProtectedLineViolations
    //    vuota); null o eccezione -> ProtectedLinesChanged. Attivo solo se ProtectedLineViolations c'e'.
    public static class TaskMergeAudit
    {
        // quanti changeset estranei al massimo nel testo di un messaggio
        private const int MaxIdsInText = 12;

        // quante violazioni delle righe protette al massimo nel testo di un messaggio
        private const int MaxViolationsInText = 3;

        public static TaskMergeAuditResult Run(TaskMergeAuditInput input)
        {
            if (input == null)
                throw new ArgumentNullException("input");

            var issues = new List<AuditIssue>();
            var selected = new HashSet<int>(input.SelectedChangesetIds ?? (IEnumerable<int>)new int[0]);
            var historyCache = new Dictionary<string, HistoryResult>(StringComparer.OrdinalIgnoreCase);
            var contributing = new SortedSet<int>();

            // -------------------------------------------------------------------------------------
            // Passi da consegnare: target esatti e cartelle con ricorsione Full
            // -------------------------------------------------------------------------------------
            var steps = new List<TaskMergeStep>();
            if (input.StepsToDeliver == null)
            {
                issues.Add(new AuditIssue(AuditIssueKind.StepNotMerged, null,
                    "The steps to check in are not known.", true));
            }
            else
            {
                foreach (var step in input.StepsToDeliver)
                {
                    if (step == null)
                    {
                        issues.Add(new AuditIssue(AuditIssueKind.StepNotMerged, null,
                            "The list of steps to check in contains an empty entry.", true));
                        continue;
                    }

                    steps.Add(step);
                }
            }

            // azione della policy per ogni passo (senza policy: Merge). Un'azione che non si riesce a
            // leggere blocca e il passo si controlla come Merge.
            var actions = new Dictionary<TaskMergeStep, MergePolicyAction>();
            foreach (var step in steps)
            {
                var action = MergePolicyAction.Merge;
                if (input.StepAction != null)
                {
                    string error = null;
                    try
                    {
                        action = input.StepAction(step);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    if (error == null && !Enum.IsDefined(typeof(MergePolicyAction), action))
                        error = SafetyText.Format("unknown action {0}", (int)action);
                    if (error != null)
                    {
                        issues.Add(new AuditIssue(AuditIssueKind.StepNotMerged, NormalizePath(step.TargetItem),
                            StepLabel(step) + ": the merge policy action could not be determined (" + error + ").", true));
                        action = MergePolicyAction.Merge;
                    }
                }
                actions[step] = action;
            }

            // target dei passi da consegnare (Merge e Discard) e di quelli saltati (Skip)
            var stepTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fullFolderPrefixes = new List<string>();
            var skippedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedFolderPrefixes = new List<string>();
            foreach (var step in steps)
            {
                var target = NormalizePath(step.TargetItem);
                if (target == null)
                    continue;
                var skipped = actions[step] == MergePolicyAction.Skip;
                (skipped ? skippedTargets : stepTargets).Add(target);
                if (step.Recursion == TaskMergeStepRecursion.Full)
                    (skipped ? skippedFolderPrefixes : fullFolderPrefixes).Add(target + "/");
            }

            // -------------------------------------------------------------------------------------
            // Pending sotto il target: R1, R2, R4
            // -------------------------------------------------------------------------------------
            var pendingCount = 0;
            if (input.Pending == null)
            {
                issues.Add(new AuditIssue(AuditIssueKind.HistoryUnavailable, null,
                    "The pending changes under the target could not be read.", true));
            }
            else
            {
                var ordered = input.Pending
                    .Select((p, index) => new { Pending = p, Index = index })
                    .OrderBy(x => x.Pending == null ? string.Empty : NormalizePath(x.Pending.TargetItem) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Index)
                    .Select(x => x.Pending)
                    .ToList();

                foreach (var pending in ordered)
                {
                    if (pending == null)
                    {
                        issues.Add(new AuditIssue(AuditIssueKind.UnplannedPendingChange, null,
                            "The list of pending changes contains an empty entry.", true));
                        continue;
                    }

                    pendingCount++;
                    var target = NormalizePath(pending.TargetItem);
                    var shown = target ?? "(unknown item)";
                    var pendingIssues = new List<AuditIssue>();

                    // R1 (il file di team delle regole di merge, salvato dalla scheda Merge Policies, e' un
                    // caso tipico: stessa regola, messaggio che dice cosa fare)
                    if (!pending.IsMerge)
                    {
                        var message = SafetyText.Format("{0}: the pending change ({1}) is not a merge.", shown, ChangeTypeOf(pending));
                        if (IsTeamPolicyFile(target))
                            message += " It is the team merge policy file: check it in on its own (or undo it) first, then check in the merge.";
                        pendingIssues.Add(new AuditIssue(AuditIssueKind.NotAMerge, target, message, true));
                    }
                    else
                    {
                        // R2
                        CheckMergeSources(pending, target, shown, selected, input.ChangesetsTouchingSourceItem, historyCache, contributing, pendingIssues);
                    }

                    // R4 (un pending su un item che la policy salta e' sempre bloccante: lo Skip non e'
                    // stato rispettato)
                    if (!IsPlanned(target, stepTargets, fullFolderPrefixes))
                    {
                        var skippedByPolicy = IsPlanned(target, skippedTargets, skippedFolderPrefixes);
                        var dirty = pendingIssues.Any(i => i.Blocking);
                        string message;
                        if (skippedByPolicy)
                            message = SafetyText.Format("{0}: the merge policy says Skip for this item, but it has a pending merge.", shown);
                        else if (dirty)
                            message = SafetyText.Format("{0}: the pending change is not part of the steps being checked in and fails the checks above.", shown);
                        else
                            message = SafetyText.Format("{0}: the pending merge is not part of the steps being checked in (it only contains selected changesets).", shown);
                        pendingIssues.Add(new AuditIssue(AuditIssueKind.UnplannedPendingChange, target, message, dirty || skippedByPolicy));
                    }

                    issues.AddRange(pendingIssues);
                }
            }

            // -------------------------------------------------------------------------------------
            // R3: ogni passo da consegnare e' fuso (i passi Skip no: sono solo riportati);
            // R5: ogni passo Discard ha tenuto il contenuto del target;
            // R6: ogni passo Merge con righe protette ha le righe protette del target
            // -------------------------------------------------------------------------------------
            var mergedSteps = 0;
            var requiredSteps = 0;
            var policy = new PolicyCounts();
            foreach (var step in steps)
            {
                var action = actions[step];
                if (action == MergePolicyAction.Skip)
                {
                    policy.Skipped++;
                    issues.Add(new AuditIssue(AuditIssueKind.SkippedByPolicy, NormalizePath(step.TargetItem),
                        StepLabel(step) + ": not merged, the merge policy says Skip.", false));
                    continue;
                }

                requiredSteps++;
                if (action == MergePolicyAction.Discard)
                {
                    policy.Discarded++;
                    CheckDiscard(step, input.TargetContentPreserved, issues);
                }
                else
                {
                    CheckProtectedLines(step, input, issues, policy);
                }

                bool? merged;
                string error = null;
                if (input.StepIsMerged == null)
                {
                    merged = null;
                }
                else
                {
                    try
                    {
                        merged = input.StepIsMerged(step);
                    }
                    catch (Exception ex)
                    {
                        merged = null;
                        error = ex.Message;
                    }
                }

                if (merged == true)
                {
                    mergedSteps++;
                    continue;
                }

                var label = StepLabel(step);
                string message;
                if (merged == false)
                    message = label + ": not fully merged; a preview still finds changes or conflicts to merge.";
                else if (error != null)
                    message = label + ": the merge could not be verified (" + error + ").";
                else
                    message = label + ": the merge could not be verified.";
                issues.Add(new AuditIssue(AuditIssueKind.StepNotMerged, NormalizePath(step.TargetItem), message, true));
            }

            // prima i bloccanti (OrderBy e' stabile)
            var final = issues.OrderBy(i => i.Blocking ? 0 : 1).ToList().AsReadOnly();
            var summary = Summarize(final, pendingCount, contributing, mergedSteps, requiredSteps, policy, input.StepHasProtectedLines != null);
            return new TaskMergeAuditResult(final, summary);
        }

        private static string StepLabel(TaskMergeStep step)
        {
            return SafetyText.Format("Step {0} ({1}, {2})", step.Number, step.RelativePath, SafetyText.Range(step.FromChangesetId, step.ToChangesetId));
        }

        // R5: Discard = il merge si registra ma il contenuto resta quello del target. Nel dubbio si blocca.
        private static void CheckDiscard(TaskMergeStep step, Func<TaskMergeStep, bool?> preserved, List<AuditIssue> issues)
        {
            bool? kept = null;
            string error = null;
            if (preserved != null)
            {
                try
                {
                    kept = preserved(step);
                }
                catch (Exception ex)
                {
                    kept = null;
                    error = ex.Message;
                }
            }

            if (kept == true)
                return;

            var label = StepLabel(step);
            string message;
            if (kept == false)
                message = label + ": the merge policy says Discard, but the pending content differs from the target's content before the merge.";
            else
                message = label + ": the merge policy says Discard, but it could not be verified that the target's content is kept"
                          + (error == null ? "." : " (" + error + ").");
            issues.Add(new AuditIssue(AuditIssueKind.DiscardChangedContent, NormalizePath(step.TargetItem), message, true));
        }

        // R6: nei file con righe protette, le righe protette del contenuto in sospeso sono quelle del
        // target. Solo se il controllo e' attivo (ProtectedLineViolations); le cartelle non hanno righe.
        private static void CheckProtectedLines(TaskMergeStep step, TaskMergeAuditInput input, List<AuditIssue> issues, PolicyCounts policy)
        {
            if (input.ProtectedLineViolations == null || step.Kind == TaskMergeItemKind.Folder)
                return;

            if (input.StepHasProtectedLines != null)
            {
                bool has;
                try
                {
                    has = input.StepHasProtectedLines(step);
                }
                catch (Exception)
                {
                    has = true; // nel dubbio si controlla
                }
                if (!has)
                    return;
            }

            IReadOnlyList<string> violations;
            string error = null;
            try
            {
                violations = input.ProtectedLineViolations(step);
            }
            catch (Exception ex)
            {
                violations = null;
                error = ex.Message;
            }

            var label = StepLabel(step);
            if (violations == null)
            {
                issues.Add(new AuditIssue(AuditIssueKind.ProtectedLinesChanged, NormalizePath(step.TargetItem),
                    label + ": the protected lines could not be verified" + (error == null ? "." : " (" + error + ")."), true));
                return;
            }

            var real = violations.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (real.Count == 0)
            {
                policy.ProtectedFiles++;
                return;
            }

            var text = string.Join(" ", real.Take(MaxViolationsInText));
            if (real.Count > MaxViolationsInText)
                text += SafetyText.Format(" ... and {0} more.", real.Count - MaxViolationsInText);
            issues.Add(new AuditIssue(AuditIssueKind.ProtectedLinesChanged, NormalizePath(step.TargetItem),
                label + ": the protected lines differ from the target's: " + text, true));
        }

        // conteggi della policy per il riepilogo
        private sealed class PolicyCounts
        {
            public int Discarded;
            public int Skipped;
            public int ProtectedFiles;
        }

        // R2 su tutte le sorgenti di un pending di merge.
        private static void CheckMergeSources(
            AuditPendingChange pending,
            string target,
            string shown,
            HashSet<int> selected,
            Func<string, int, int, IReadOnlyList<int>> history,
            Dictionary<string, HistoryResult> historyCache,
            SortedSet<int> contributing,
            List<AuditIssue> issues)
        {
            var sources = (pending.MergeSources ?? new AuditMergeSource[0]).Where(s => s != null && !s.IsRename).ToList();
            if (sources.Count == 0)
            {
                issues.Add(new AuditIssue(AuditIssueKind.HistoryUnavailable, target,
                    SafetyText.Format("{0}: the source of the pending merge is not known, so its changesets cannot be verified.", shown), true));
                return;
            }

            foreach (var source in sources)
            {
                var sourceItem = NormalizePath(source.SourceItem);
                var range = SafetyText.Range(source.VersionFrom, source.VersionTo);
                if (sourceItem == null || source.VersionFrom <= 0 || source.VersionTo < source.VersionFrom)
                {
                    issues.Add(new AuditIssue(AuditIssueKind.HistoryUnavailable, target,
                        SafetyText.Format("{0}: the pending merge has an invalid source ({1} {2}).", shown, sourceItem ?? "(no path)", range), true));
                    continue;
                }

                var h = ReadHistory(history, historyCache, sourceItem, source.VersionFrom, source.VersionTo);
                if (h.Ids == null)
                {
                    issues.Add(new AuditIssue(AuditIssueKind.HistoryUnavailable, target,
                        SafetyText.Format("{0}: the history of {1} in {2} could not be read{3}.",
                            shown, sourceItem, range, h.Error == null ? string.Empty : " (" + h.Error + ")"), true));
                    continue;
                }

                var foreign = h.Ids.Where(id => !selected.Contains(id)).ToList();
                if (foreign.Count > 0)
                {
                    issues.Add(new AuditIssue(AuditIssueKind.ForeignChangeset, target,
                        SafetyText.Format("{0}: the pending merge of {1} {2} also contains changesets that are not selected for the task: {3}.",
                            shown, sourceItem, range, SafetyText.Ids(foreign, MaxIdsInText)), true));
                    continue;
                }

                foreach (var id in h.Ids)
                    contributing.Add(id);
            }
        }

        private static HistoryResult ReadHistory(
            Func<string, int, int, IReadOnlyList<int>> history,
            Dictionary<string, HistoryResult> cache,
            string sourceItem,
            int from,
            int to)
        {
            var key = SafetyText.Format("{0}|{1}|{2}", sourceItem, from, to);
            HistoryResult result;
            if (cache.TryGetValue(key, out result))
                return result;

            result = new HistoryResult();
            if (history != null)
            {
                try
                {
                    var raw = history(sourceItem, from, to);
                    // difesa: solo i changeset dentro l'intervallo (inclusivo)
                    if (raw != null)
                        result.Ids = raw.Where(id => id >= from && id <= to).Distinct().OrderBy(id => id).ToList();
                }
                catch (Exception ex)
                {
                    result.Ids = null;
                    result.Error = ex.Message;
                }
            }

            cache[key] = result;
            return result;
        }

        // Il file di team delle regole di merge (MergePolicyStore.TeamFileName: costante, nessuna
        // dipendenza da TFVC).
        private static bool IsTeamPolicyFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            var slash = path.LastIndexOf('/');
            return string.Equals(slash >= 0 ? path.Substring(slash + 1) : path, MergePolicyStore.TeamFileName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlanned(string target, HashSet<string> stepTargets, List<string> fullFolderPrefixes)
        {
            if (target == null)
                return false;
            if (stepTargets.Contains(target))
                return true;
            return fullFolderPrefixes.Any(prefix => target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static string Summarize(IReadOnlyList<AuditIssue> issues, int pendingCount, SortedSet<int> contributing, int mergedSteps, int stepCount,
            PolicyCounts policy, bool knowsProtectedFiles)
        {
            var blocking = issues.Where(i => i.Blocking).ToList();
            // i passi saltati dalla policy si contano a parte, non come note
            var notes = issues.Count(i => !i.Blocking && i.Kind != AuditIssueKind.SkippedByPolicy);
            var stepsText = SafetyText.Format("{0}/{1} steps merged", mergedSteps, stepCount);

            // es. "; 3 discarded, 1 skipped, 5 files with protected lines kept" (niente se la policy non
            // ha toccato nulla: il riepilogo resta quello di sempre)
            var policyParts = new List<string>();
            if (policy.Discarded > 0)
                policyParts.Add(SafetyText.Format("{0} discarded", policy.Discarded));
            if (policy.Skipped > 0)
                policyParts.Add(SafetyText.Format("{0} skipped", policy.Skipped));
            if (policy.ProtectedFiles > 0)
            {
                policyParts.Add(knowsProtectedFiles
                    ? SafetyText.Count(policy.ProtectedFiles, "1 file with protected lines kept", "{0} files with protected lines kept")
                    : SafetyText.Count(policy.ProtectedFiles, "protected lines checked on 1 file", "protected lines checked on {0} files"));
            }
            if (policyParts.Count > 0)
                stepsText += "; " + string.Join(", ", policyParts);

            if (blocking.Count == 0)
            {
                string pendingText;
                if (pendingCount == 0)
                    pendingText = "no pending merges";
                else if (contributing.Count == 0)
                    pendingText = SafetyText.Count(pendingCount, "1 pending merge", "{0} pending merges");
                else
                    pendingText = SafetyText.Format("{0}, all from task {1} {2}",
                        SafetyText.Count(pendingCount, "1 pending merge", "{0} pending merges"),
                        contributing.Count == 1 ? "changeset" : "changesets",
                        SafetyText.Range(contributing.Min, contributing.Max));

                var text = "Final check passed: " + pendingText + "; " + stepsText;
                if (notes > 0)
                    text += "; " + SafetyText.Count(notes, "1 note", "{0} notes");
                return text + ".";
            }

            var kinds = blocking
                .GroupBy(i => i.Kind)
                .OrderBy(g => g.Key)
                .Select(g => SafetyText.Format("{0}: {1}", KindText(g.Key), g.Count()));
            return SafetyText.Format(
                "Final check failed: {0} ({1}); {2}; {3}. The check-in is blocked.",
                SafetyText.Count(blocking.Count, "1 blocking problem", "{0} blocking problems"),
                string.Join(", ", kinds),
                SafetyText.Count(pendingCount, "1 pending change", "{0} pending changes"),
                stepsText);
        }

        private static string KindText(AuditIssueKind kind)
        {
            switch (kind)
            {
                case AuditIssueKind.NotAMerge:
                    return "not a merge";
                case AuditIssueKind.ForeignChangeset:
                    return "changesets not selected";
                case AuditIssueKind.StepNotMerged:
                    return "steps not merged";
                case AuditIssueKind.UnplannedPendingChange:
                    return "unplanned changes";
                case AuditIssueKind.HistoryUnavailable:
                    return "not verifiable";
                case AuditIssueKind.DiscardChangedContent:
                    return "discarded items changed";
                case AuditIssueKind.ProtectedLinesChanged:
                    return "protected lines changed";
                case AuditIssueKind.SkippedByPolicy:
                    return "skipped by policy";
                default:
                    return kind.ToString();
            }
        }

        private static string ChangeTypeOf(AuditPendingChange pending)
        {
            return string.IsNullOrWhiteSpace(pending.ChangeTypeText) ? "unknown change" : pending.ChangeTypeText.Trim();
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var trimmed = path.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private sealed class HistoryResult
        {
            // null = storia non disponibile
            public List<int> Ids { get; set; }

            public string Error { get; set; }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Pending riletti subito prima del check-in
    // ---------------------------------------------------------------------------------------------

    // Il check-in di TFVC archivia lo stato ATTUALE degli item indicati, non quello visto dal controllo
    // finale: un merge fatto altrove dopo il controllo con un intervallo piu' largo (TFVC lo accetta se
    // comprende quello in sospeso) finirebbe nel check-in con changeset di terzi. Subito prima del
    // check-in i pending sotto il target si rileggono e si confrontano con quelli controllati: item,
    // tipo di modifica e sorgenti del merge (item, intervallo, rename) devono essere identici, e non
    // devono esserci pending in piu' o in meno. Qualsiasi differenza (o una lettura mancata) = niente
    // check-in.
    public static class TaskMergePendingSnapshot
    {
        // quante differenze al massimo nel riepilogo (il resto e' "and N more")
        private const int MaxShown = 5;

        // Differenze tra i pending controllati e quelli riletti (vuoto = identici). null = non disponibili.
        public static IReadOnlyList<string> Differences(IReadOnlyList<AuditPendingChange> checkedPending, IReadOnlyList<AuditPendingChange> currentPending)
        {
            var differences = new List<string>();
            if (checkedPending == null)
            {
                differences.Add("The pending changes checked by the final check are not known.");
                return differences.AsReadOnly();
            }
            if (currentPending == null)
            {
                differences.Add("The pending changes under the target could not be read again.");
                return differences.AsReadOnly();
            }

            var before = Index(checkedPending, "checked", differences);
            var now = Index(currentPending, "current", differences);
            var items = before.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item, StringComparer.Ordinal);
            foreach (var item in items)
            {
                string was;
                string isNow;
                before.TryGetValue(item, out was);
                now.TryGetValue(item, out isNow);
                if (was == null)
                    differences.Add(SafetyText.Format("{0}: pending change added after the final check ({1}).", item, isNow));
                else if (isNow == null)
                    differences.Add(SafetyText.Format("{0}: pending change no longer there (checked in or undone after the final check).", item));
                else if (!string.Equals(was, isNow, StringComparison.OrdinalIgnoreCase))
                    differences.Add(SafetyText.Format("{0}: pending change changed after the final check (was {1}; now {2}).", item, was, isNow));
            }

            return differences.AsReadOnly();
        }

        // Riepilogo per il banner: le prime differenze e quante sono.
        public static string Describe(IReadOnlyList<string> differences)
        {
            if (differences == null || differences.Count == 0)
                return string.Empty;
            var text = string.Join(" ", differences.Take(MaxShown));
            if (differences.Count > MaxShown)
                text += SafetyText.Format(" ... and {0} more.", differences.Count - MaxShown);
            return text;
        }

        // item -> firme (una per pending; piu' voci sullo stesso item si confrontano come insieme)
        private static Dictionary<string, string> Index(IReadOnlyList<AuditPendingChange> pending, string which, List<string> differences)
        {
            var signatures = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in pending)
            {
                var item = change == null ? null : Normalize(change.TargetItem);
                if (item == null)
                {
                    differences.Add("The " + which + " list of pending changes contains an entry without an item.");
                    continue;
                }

                List<string> list;
                if (!signatures.TryGetValue(item, out list))
                {
                    list = new List<string>();
                    signatures.Add(item, list);
                }
                list.Add(Signature(change));
            }

            return signatures.ToDictionary(
                pair => pair.Key,
                pair => string.Join(" + ", pair.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
        }

        // es. "Edit, Merge from $/S/a.cs C10-C12"
        private static string Signature(AuditPendingChange change)
        {
            var text = string.IsNullOrWhiteSpace(change.ChangeTypeText) ? "unknown change" : change.ChangeTypeText.Trim();
            if (!change.IsMerge)
                text += " (not a merge)";
            var sources = (change.MergeSources ?? new AuditMergeSource[0])
                .Where(s => s != null)
                .Select(s => SafetyText.Format("{0} {1}{2}",
                    Normalize(s.SourceItem) ?? "(no path)",
                    SafetyText.Range(s.VersionFrom, s.VersionTo),
                    s.IsRename ? " (rename)" : string.Empty))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return sources.Count == 0 ? text : text + " from " + string.Join(", ", sources);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var trimmed = path.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Passi coperti dai pending (revisione senza punto di check-in)
    // ---------------------------------------------------------------------------------------------

    // Revisione dei pending senza una catena che sappia quali passi ha fuso (es. merge di una sessione
    // precedente): quali passi del piano consegnano quei pending. Per ogni pending, i passi dello stesso
    // item (target uguale, o dentro una cartella fusa con ricorsione Full) il cui intervallo tocca
    // quello registrato da TFVC nelle sorgenti del merge: cosi' un item con un passo in una parte gia'
    // archiviata e uno nella parte in sospeso porta al passo della parte in sospeso, non al primo per
    // path. Se nessun passo dell'item tocca l'intervallo (o le sorgenti mancano), tutti i passi
    // dell'item: nel dubbio il controllo finale ne verifica di piu', non di meno.
    public static class TaskMergePendingSteps
    {
        public static IReadOnlyList<TaskMergeStep> CoveredBy(IReadOnlyList<TaskMergeStep> steps, IReadOnlyList<AuditPendingChange> pending)
        {
            if (steps == null)
                throw new ArgumentNullException("steps");

            var covered = new HashSet<TaskMergeStep>();
            foreach (var change in pending ?? new AuditPendingChange[0])
            {
                var item = change == null ? null : Normalize(change.TargetItem);
                if (item == null)
                    continue;

                var candidates = steps
                    .Where(s => s != null && Belongs(item, s))
                    .ToList();
                if (candidates.Count == 0)
                    continue;

                var ranges = (change.MergeSources ?? new AuditMergeSource[0])
                    .Where(m => m != null && !m.IsRename && m.VersionFrom > 0 && m.VersionTo >= m.VersionFrom)
                    .ToList();
                var matching = candidates
                    .Where(s => ranges.Any(m => s.FromChangesetId <= m.VersionTo && m.VersionFrom <= s.ToChangesetId))
                    .ToList();

                foreach (var step in matching.Count > 0 ? matching : candidates)
                    covered.Add(step);
            }

            return steps.Where(s => s != null && covered.Contains(s)).ToList().AsReadOnly();
        }

        private static bool Belongs(string item, TaskMergeStep step)
        {
            var target = Normalize(step.TargetItem);
            if (target == null)
                return false;
            if (string.Equals(item, target, StringComparison.OrdinalIgnoreCase))
                return true;
            return step.Recursion == TaskMergeStepRecursion.Full
                && item.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var trimmed = path.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }
    }

    // Testi in inglese per i messaggi (formato invariante).
    internal static class SafetyText
    {
        public static string Format(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        public static string Range(int from, int to)
        {
            return from == to ? Format("C{0}", from) : Format("C{0}-C{1}", from, to);
        }

        public static string Ids(IList<int> ids, int max)
        {
            var text = string.Join(", ", ids.Take(max).Select(id => "C" + id.ToString(CultureInfo.InvariantCulture)));
            if (ids.Count > max)
                text += Format(" and {0} more", ids.Count - max);
            return text;
        }

        public static string Count(int count, string one, string many)
        {
            return count == 1 ? one : Format(many, count);
        }
    }
}
