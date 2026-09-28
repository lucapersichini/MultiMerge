// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MultiMerge.Prism;

namespace MultiMerge
{
    public enum TaskMergeStepStatus
    {
        Pending,
        AlreadyMerged,
        Merging,
        Merged,
        Conflicts,
        Failed,
        // L'anteprima di TFVC rifiuta il passo (failure non dovuto alla catena stessa): Start resta
        // disabilitato finche' la causa non e' tolta e il piano ricaricato.
        Blocked,

        // Regola di merge "Skip" (o scelta nella scheda): il passo non si fonde, nessuna chiamata a TFVC.
        // Conta come fatto per l'avanzamento della catena.
        Skipped
    }

    // Stato di un punto di check-in nella barra: da raggiungere, raggiunto (la catena aspetta il
    // check-in della parte), superato (la parte e' archiviata o non aveva niente da archiviare).
    public enum TaskMergeCheckpointState
    {
        Upcoming,
        Active,
        Passed
    }

    // Una parte del piano (finestra cronologica di changeset del task chiusa da un check-in):
    // intestazione del gruppo nella lista dei passi.
    public sealed class TaskMergePartViewModel : BindableBase
    {
        private bool _isLocked;

        public TaskMergePartViewModel(TaskMergePart part, int partCount, TaskMergePart previous)
        {
            if (part == null)
                throw new ArgumentNullException("part");

            Part = part;
            Number = part.Number;
            PartCount = partCount;
            FromChangesetId = part.FromChangesetId;
            ToChangesetId = part.ToChangesetId;
            TaskChangesetIds = part.TaskChangesetIds ?? new List<int>().AsReadOnly();
            StepCount = part.Steps == null ? 0 : part.Steps.Count;
            EndReason = part.EndReason;

            var range = TaskMergeText.Range(FromChangesetId, ToChangesetId);
            var steps = TaskMergeText.Count(StepCount, "step");
            HeaderText = partCount > 1
                ? string.Format(CultureInfo.InvariantCulture, "Part {0} of {1} - {2} - {3}", Number, partCount, range, steps)
                : string.Format(CultureInfo.InvariantCulture, "{0} - {1}", range, steps);
            HeaderToolTip = string.Format(CultureInfo.InvariantCulture, "Task changesets of this part: {0}{1}",
                TaskMergeText.Changesets(TaskChangesetIds),
                string.IsNullOrEmpty(EndReason) ? string.Empty : "\n" + EndReason);

            if (previous != null)
            {
                HasCheckpointBefore = true;
                CheckpointBeforeText = string.Format(CultureInfo.InvariantCulture,
                    "Check-in point: part {0} of {1} ends here (C{2})", previous.Number, partCount, previous.ToChangesetId);
                CheckpointBeforeToolTip = string.IsNullOrEmpty(previous.EndReason)
                    ? CheckpointBeforeText
                    : CheckpointBeforeText + "\n" + previous.EndReason;
            }
        }

        public TaskMergePart Part { get; private set; }

        public int Number { get; private set; }

        public int PartCount { get; private set; }

        public int FromChangesetId { get; private set; }

        public int ToChangesetId { get; private set; }

        public IReadOnlyList<int> TaskChangesetIds { get; private set; }

        public int StepCount { get; private set; }

        // Perche' la parte finisce con un check-in (null per l'ultima).
        public string EndReason { get; private set; }

        // "Part 1 of 2 - C162095-C162931 - 104 steps"
        public string HeaderText { get; private set; }

        public string HeaderToolTip { get; private set; }

        // Riga "Check-in point" mostrata in testa al gruppo, cioe' tra la parte precedente e questa.
        public bool HasCheckpointBefore { get; private set; }

        public string CheckpointBeforeText { get; private set; }

        public string CheckpointBeforeToolTip { get; private set; }

        // La parte viene dopo un check-in non ancora fatto: i suoi passi sono visibili ma grigi.
        public bool IsLocked
        {
            get { return _isLocked; }
            set
            {
                if (SetProperty(ref _isLocked, value))
                {
                    OnPropertyChanged("LockedText");
                    OnPropertyChanged("LockedSuffix");
                }
            }
        }

        // Testo accanto all'intestazione del gruppo ("   · Available after the check-in of part 1").
        public string LockedSuffix
        {
            get { return _isLocked ? "   · " + LockedText : string.Empty; }
        }

        // "Available after the check-in of part 1" (null se la parte e' eseguibile).
        public string LockedText
        {
            get
            {
                return _isLocked
                    ? string.Format(CultureInfo.InvariantCulture, "Available after the check-in of part {0}", Number - 1)
                    : null;
            }
        }
    }

    // Punto di check-in tra due parti nella barra segmentata (separatore verticale con icona).
    public sealed class TaskMergeCheckpointMarkerViewModel : BindableBase
    {
        private TaskMergeCheckpointState _state;

        public TaskMergeCheckpointMarkerViewModel(TaskMergePart endingPart, int partCount)
        {
            if (endingPart == null)
                throw new ArgumentNullException("endingPart");

            PartNumber = endingPart.Number;
            PartCount = partCount;
            LastChangesetId = endingPart.ToChangesetId;
            EndReason = endingPart.EndReason;
            _state = TaskMergeCheckpointState.Upcoming;
        }

        // Numero della parte che finisce in questo punto.
        public int PartNumber { get; private set; }

        public int PartCount { get; private set; }

        public int LastChangesetId { get; private set; }

        public string EndReason { get; private set; }

        // Per il pannello della barra: i punti di check-in hanno larghezza fissa.
        public bool IsCheckpointMarker
        {
            get { return true; }
        }

        public TaskMergeCheckpointState State
        {
            get { return _state; }
            set
            {
                if (SetProperty(ref _state, value))
                    OnPropertyChanged("ToolTipText");
            }
        }

        public string ToolTipText
        {
            get
            {
                var text = string.Format(CultureInfo.InvariantCulture, "Check-in point: part {0} of {1} ends here (C{2})",
                    PartNumber, PartCount, LastChangesetId);
                switch (_state)
                {
                    case TaskMergeCheckpointState.Active:
                        text += "\nThe chain is waiting for the check-in of this part.";
                        break;
                    case TaskMergeCheckpointState.Passed:
                        text += "\nPassed: this part is checked in (or had nothing to check in).";
                        break;
                }
                return string.IsNullOrEmpty(EndReason) ? text : text + "\n" + EndReason;
            }
        }
    }

    // Un passo della catena: UN merge di UN item (file o cartella) su un intervallo di changeset del
    // task. Una riga della lista e un segmento della barra di avanzamento nella scheda "Merge from Task".
    public sealed class TaskMergeStepViewModel : BindableBase
    {
        private static readonly IReadOnlyList<int> NoChangesets = new List<int>().AsReadOnly();
        private static readonly IReadOnlyList<EffectiveLineRule> NoLineRules = new List<EffectiveLineRule>().AsReadOnly();
        private static readonly IReadOnlyList<MergeLineRule> NoMergeLineRules = new List<MergeLineRule>().AsReadOnly();
        private static readonly IReadOnlyList<ProtectedLineDifference> NoDifferences = new List<ProtectedLineDifference>().AsReadOnly();

        // Azioni del menu a tendina della colonna "Policy".
        public static readonly IReadOnlyList<MergePolicyAction> PolicyActions =
            new List<MergePolicyAction> { MergePolicyAction.Merge, MergePolicyAction.Discard, MergePolicyAction.Skip }.AsReadOnly();

        private TaskMergeStepStatus _status;
        private string _details;
        private bool _isLocked;
        private IReadOnlyList<int> _colleaguesNotInTarget = NoChangesets;

        // Regole di merge: azione decisa dalla regola, azione di esecuzione (puo' cambiarla l'utente nella
        // scheda, solo per questa esecuzione), regola che l'ha decisa, righe protette dell'item.
        private MergePolicyAction _policyAction = MergePolicyAction.Merge;
        private MergePolicyAction _decidedPolicyAction = MergePolicyAction.Merge;
        private EffectivePathRule _policyRule;
        private IReadOnlyList<EffectiveLineRule> _lineRules = NoLineRules;
        private bool _isPolicyEditable;
        private IReadOnlyList<ProtectedLineDifference> _keptTargetDifferences = NoDifferences;

        public TaskMergeStepViewModel(TaskMergeStep step, TaskMergePartViewModel partInfo)
        {
            if (step == null)
                throw new ArgumentNullException("step");

            Step = step;
            PartInfo = partInfo;
            TaskChangesetIds = step.TaskChangesetIds ?? NoChangesets;
            InterleavedThirdParty = step.InterleavedThirdParty ?? NoChangesets;
            ChangeText = TaskMergeText.ChangeKind(step.ChangeKind);
            RangeText = TaskMergeText.Range(step.FromChangesetId, step.ToChangesetId);
            _status = TaskMergeStepStatus.Pending;
        }

        public TaskMergeStep Step { get; private set; }

        public int Number
        {
            get { return Step.Number; }
        }

        public int Part
        {
            get { return Step.Part; }
        }

        // Parte del passo: chiave del raggruppamento nella lista.
        public TaskMergePartViewModel PartInfo { get; private set; }

        public string SourceItem
        {
            get { return Step.SourceItem; }
        }

        public string TargetItem
        {
            get { return Step.TargetItem; }
        }

        // Path relativo alla radice del target ("." per la radice).
        public string RelativePath
        {
            get { return Step.RelativePath; }
        }

        public TaskMergeItemKind Kind
        {
            get { return Step.Kind; }
        }

        public bool IsFolder
        {
            get { return Step.Kind == TaskMergeItemKind.Folder; }
        }

        public TaskChangeKind ChangeKind
        {
            get { return Step.ChangeKind; }
        }

        // "Add", "Edit", "Add, Edit"...
        public string ChangeText { get; private set; }

        public int FromChangesetId
        {
            get { return Step.FromChangesetId; }
        }

        public int ToChangesetId
        {
            get { return Step.ToChangesetId; }
        }

        public IReadOnlyList<int> TaskChangesetIds { get; private set; }

        // "C162108-C162115" (o "C162108" per un solo changeset).
        public string RangeText { get; private set; }

        public bool TargetExists
        {
            get { return Step.TargetExists; }
        }

        public TaskMergeStepRecursion Recursion
        {
            get { return Step.Recursion; }
        }

        // Changeset di colleghi sull'item tra i changeset del task (intero task).
        public IReadOnlyList<int> InterleavedThirdParty { get; private set; }

        public bool IsInterleaved
        {
            get { return InterleavedThirdParty.Count > 0; }
        }

        public string InterleavedText
        {
            get
            {
                if (!IsInterleaved)
                    return null;
                var text = string.Format(CultureInfo.InvariantCulture,
                    "Other changesets change this item between the task changesets: {0}.\nThey are not brought along: the task changes around them are merged in separate steps{1}.",
                    TaskMergeText.Changesets(InterleavedThirdParty),
                    TargetExists ? ", with a check-in in between" : string.Empty);
                if (HasColleagueWarning)
                    text += string.Format(CultureInfo.InvariantCulture,
                        "\nNot in the target: {0} (the result may need them).", TaskMergeText.Changesets(_colleaguesNotInTarget));
                return text;
            }
        }

        // Changeset di colleghi sull'item (tra i changeset del task) che non risultano fusi nel target.
        public IReadOnlyList<int> ColleaguesNotInTarget
        {
            get { return _colleaguesNotInTarget; }
            set
            {
                _colleaguesNotInTarget = value ?? NoChangesets;
                OnPropertyChanged("ColleaguesNotInTarget");
                OnPropertyChanged("HasColleagueWarning");
                OnPropertyChanged("InterleavedText");
                OnPropertyChanged("ItemToolTip");
                OnPropertyChanged("DetailsToolTip");
                OnPropertyChanged("ToolTipText");
            }
        }

        public bool HasColleagueWarning
        {
            get { return _colleaguesNotInTarget.Count > 0; }
        }

        // Tooltip della cella "Item": server path completi, sorgente e target.
        public string ItemToolTip
        {
            get
            {
                var text = string.Format(CultureInfo.InvariantCulture, "{0}\n→ {1}{2}{3}",
                    SourceItem, TargetItem,
                    TargetExists ? string.Empty : "\n(new in the target)",
                    Recursion == TaskMergeStepRecursion.Full ? "\n(merged with its whole content)" : string.Empty);
                if (_isLocked)
                    text += "\n" + LockedText;
                return IsInterleaved ? text + "\n\n" + InterleavedText : text;
            }
        }

        // Il passo appartiene a una parte che viene dopo un check-in non ancora fatto (A1): visibile
        // ma grigio, con la dicitura "Available after the check-in of part N".
        public bool IsLocked
        {
            get { return _isLocked; }
            set
            {
                if (SetProperty(ref _isLocked, value))
                {
                    OnPropertyChanged("LockedText");
                    OnPropertyChanged("DetailsText");
                    OnPropertyChanged("ItemToolTip");
                    OnPropertyChanged("DetailsToolTip");
                    OnPropertyChanged("ToolTipText");
                }
            }
        }

        // "Available after the check-in of part 1" (null se il passo e' eseguibile).
        public string LockedText
        {
            get
            {
                return _isLocked
                    ? string.Format(CultureInfo.InvariantCulture, "Available after the check-in of part {0}", Part - 1)
                    : null;
            }
        }

        // Testo della colonna Details: per un passo bloccato dietro un check-in, quando sara' eseguibile.
        public string DetailsText
        {
            get
            {
                if (!_isLocked || IsDone)
                    return _details;
                return string.IsNullOrEmpty(_details) ? LockedText : LockedText + " · " + _details;
            }
        }

        // Anteprima di TFVC: conflitti previsti (per il riepilogo dopo il Load).
        public int PreviewConflicts { get; set; }

        public TaskMergeStepStatus Status
        {
            get { return _status; }
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged("StatusText");
                    OnPropertyChanged("IsDone");
                    OnPropertyChanged("DetailsText");
                    OnPropertyChanged("ToolTipText");
                    OnPropertyChanged("DetailsToolTip");
                }
            }
        }

        public string StatusText
        {
            get
            {
                switch (_status)
                {
                    case TaskMergeStepStatus.AlreadyMerged:
                        return "Already merged";
                    case TaskMergeStepStatus.Skipped:
                        return "Skipped (policy)";
                    case TaskMergeStepStatus.Merged:
                        return _policyAction == MergePolicyAction.Discard ? "Discarded" : "Merged";
                    default:
                        return _status.ToString();
                }
            }
        }

        // "Fatto" per la barra e per la catena: gia' dentro secondo l'anteprima di TFVC, fuso in questa
        // sessione (con i conflitti risolti) oppure saltato per le regole di merge.
        public bool IsDone
        {
            get
            {
                return _status == TaskMergeStepStatus.AlreadyMerged
                    || _status == TaskMergeStepStatus.Merged
                    || _status == TaskMergeStepStatus.Skipped;
            }
        }

        public string Details
        {
            get { return _details; }
            set
            {
                if (SetProperty(ref _details, value))
                {
                    OnPropertyChanged("DetailsText");
                    OnPropertyChanged("ToolTipText");
                    OnPropertyChanged("DetailsToolTip");
                }
            }
        }

        public string DetailsToolTip
        {
            get
            {
                var text = string.IsNullOrEmpty(_details) ? StatusText : StatusText + ": " + _details;
                return IsInterleaved ? text + "\n\n" + InterleavedText : text;
            }
        }

        // Tooltip del segmento nella barra.
        public string ToolTipText
        {
            get
            {
                var text = string.Format(CultureInfo.InvariantCulture, "Step #{0}{1}: {2}\n{3} {4} ({5})\nStatus: {6}",
                    Number,
                    PartInfo != null && PartInfo.PartCount > 1
                        ? string.Format(CultureInfo.InvariantCulture, " (part {0} of {1})", Part, PartInfo.PartCount)
                        : string.Empty,
                    RelativePath, ChangeText, RangeText, TaskMergeText.Count(TaskChangesetIds.Count, "task changeset"),
                    StatusText);
                if (!string.IsNullOrEmpty(_details))
                    text += "\n" + _details;
                if (_isLocked)
                    text += "\n" + LockedText;
                if (IsInterleaved)
                    text += "\nOther changesets in between: " + TaskMergeText.Changesets(InterleavedThirdParty);
                if (HasColleagueWarning)
                    text += "\nNot in the target: " + TaskMergeText.Changesets(_colleaguesNotInTarget);
                if (_policyAction != MergePolicyAction.Merge)
                    text += "\nMerge policy: " + _policyAction;
                if (HasProtectedLines)
                    text += "\nProtected lines: the target's version is kept";
                return text;
            }
        }

        // Per il pannello della barra: un passo e' un segmento a larghezza variabile.
        public bool IsCheckpointMarker
        {
            get { return false; }
        }

        // true se in questa sessione la scheda ha lanciato il merge reale di questo passo (e quindi il
        // workspace puo' contenerne le modifiche in sospeso). Serve alla verifica di ripresa, al
        // riconoscimento dei pending della catena e al check-in.
        public bool MergedInSession { get; set; }

        #region Merge policy

        // Il view model principale: avvisato quando l'utente cambia l'azione dal menu a tendina.
        internal Action<TaskMergeStepViewModel> PolicyActionChanged { get; set; }

        // Azione con cui la catena esegue il passo: quella della regola di merge, o quella scelta nella
        // scheda (solo per questa esecuzione). Dal menu a tendina: ignorata se il passo non e'
        // modificabile (IsPolicyEditable), e il menu torna sul valore vero.
        public MergePolicyAction PolicyAction
        {
            get { return _policyAction; }
            set
            {
                if (value == _policyAction)
                    return;
                if (!_isPolicyEditable)
                {
                    OnPropertyChanged("PolicyAction");
                    return;
                }
                _policyAction = value;
                RaisePolicyProperties();
                var handler = PolicyActionChanged;
                if (handler != null)
                    handler(this);
            }
        }

        // Azione decisa dalla regola di merge (Merge se nessuna regola corrisponde).
        public MergePolicyAction DecidedPolicyAction
        {
            get { return _decidedPolicyAction; }
        }

        // Regola di percorso che ha deciso l'azione (null = nessuna: Merge di default).
        public EffectivePathRule PolicyRule
        {
            get { return _policyRule; }
        }

        // Regole di righe protette che corrispondono all'item (anche se l'azione non e' Merge).
        public IReadOnlyList<EffectiveLineRule> LineRules
        {
            get { return _lineRules; }
        }

        public bool IsPolicyOverridden
        {
            get { return _policyAction != _decidedPolicyAction; }
        }

        // Accanto al menu: da dove viene l'azione ("set here", l'id della regola, niente per il default).
        public string PolicySourceText
        {
            get
            {
                if (IsPolicyOverridden)
                    return "set here";
                var rule = _policyRule == null ? null : _policyRule.Rule;
                if (rule == null)
                    return null;
                return string.IsNullOrEmpty(rule.Id) ? "rule" : rule.Id;
            }
        }

        // Righe protette che valgono per l'esecuzione: solo per il Merge di un file.
        public IReadOnlyList<MergeLineRule> ProtectedLineRules
        {
            get
            {
                if (_policyAction != MergePolicyAction.Merge || IsFolder || _lineRules.Count == 0)
                    return NoMergeLineRules;
                return _lineRules
                    .Where(r => r != null && r.Rule != null)
                    .Select(r => r.Rule)
                    .ToList()
                    .AsReadOnly();
            }
        }

        public bool HasProtectedLines
        {
            get { return _policyAction == MergePolicyAction.Merge && !IsFolder && _lineRules.Any(r => r != null && r.Rule != null); }
        }

        // L'azione si puo' cambiare (lo decide il view model principale: solo prima che il passo sia
        // fuso e con la scheda ferma).
        public bool IsPolicyEditable
        {
            get { return _isPolicyEditable; }
            set
            {
                if (SetProperty(ref _isPolicyEditable, value))
                    OnPropertyChanged("PolicyToolTip");
            }
        }

        public string PolicyToolTip
        {
            get
            {
                var text = new System.Text.StringBuilder(DescribeAction(_policyAction));
                var rule = _policyRule == null ? null : _policyRule.Rule;
                if (rule != null)
                {
                    text.AppendFormat(CultureInfo.InvariantCulture, "\nRule: {0} ({1}){2}",
                        string.IsNullOrEmpty(rule.Id) ? "(no id)" : rule.Id,
                        DescribeOrigin(_policyRule.Origin, _policyRule.OverridesTeamRule),
                        string.IsNullOrEmpty(rule.Pattern) ? string.Empty : ", pattern " + rule.Pattern);
                    if (!string.IsNullOrEmpty(rule.Description))
                        text.Append("\n").Append(rule.Description);
                }
                else
                {
                    text.Append("\nNo path rule matches this item: Merge by default.");
                }

                if (IsPolicyOverridden)
                    text.AppendFormat(CultureInfo.InvariantCulture, "\nChanged in this tab (the rule says {0}): it applies to this merge only.", _decidedPolicyAction);

                var lineRules = _lineRules.Where(r => r != null && r.Rule != null).ToList();
                if (lineRules.Count > 0)
                {
                    text.Append("\nProtected lines (the target's version is kept): ");
                    text.Append(string.Join(", ", lineRules.Select(r => string.Format(CultureInfo.InvariantCulture, "{0} ({1})",
                        string.IsNullOrEmpty(r.Rule.Id) ? "(no id)" : r.Rule.Id, DescribeOrigin(r.Origin, r.OverridesTeamRule)))));
                    if (!HasProtectedLines)
                        text.Append(IsFolder ? " - not used for a folder." : " - not used: the step is not merged.");
                }

                if (!_isPolicyEditable)
                    text.Append("\nThe action can change only before the step is merged, while the tab is not working.");
                return text.ToString();
            }
        }

        // Promemoria: modifiche del task a righe protette che NON sono state fuse (resta il target).
        public IReadOnlyList<ProtectedLineDifference> KeptTargetDifferences
        {
            get { return _keptTargetDifferences; }
            set { _keptTargetDifferences = value ?? NoDifferences; }
        }

        // Promemoria per un file nuovo nel target con righe protette (null se niente da segnalare).
        public string NewFileProtectedNote { get; set; }

        // Il passo e' stato fuso con il risultato calcolato (righe protette) che pero' non e' ancora stato
        // scritto e verificato: un tentativo interrotto non si da' per buono.
        internal bool NeedsProtectedResult { get; set; }

        // L'azione e' cambiata mentre un merge di un tentativo precedente era in sospeso: quel merge non
        // si riusa, va annullato prima di proseguire.
        internal bool ActionChangedWhilePending { get; set; }

        // Decisione della regola di merge: azione (anche quella di esecuzione: un cambio fatto nella
        // scheda si perde), regola e righe protette. Nessun avviso al view model principale.
        internal void SetPolicyDecision(MergePolicyAction action, EffectivePathRule rule, IReadOnlyList<EffectiveLineRule> lineRules)
        {
            _decidedPolicyAction = action;
            _policyAction = action;
            _policyRule = rule;
            _lineRules = lineRules ?? NoLineRules;
            RaisePolicyProperties();
        }

        // Rimette l'azione scelta nella scheda dopo una nuova decisione della regola uguale alla precedente
        // (regole rilette). Nessun avviso al view model principale, come SetPolicyDecision.
        internal void RestorePolicyOverride(MergePolicyAction action)
        {
            _policyAction = action;
            RaisePolicyProperties();
        }

        private void RaisePolicyProperties()
        {
            OnPropertyChanged("PolicyAction");
            OnPropertyChanged("DecidedPolicyAction");
            OnPropertyChanged("PolicyRule");
            OnPropertyChanged("LineRules");
            OnPropertyChanged("IsPolicyOverridden");
            OnPropertyChanged("PolicySourceText");
            OnPropertyChanged("ProtectedLineRules");
            OnPropertyChanged("HasProtectedLines");
            OnPropertyChanged("PolicyToolTip");
            OnPropertyChanged("StatusText");
            OnPropertyChanged("ToolTipText");
            OnPropertyChanged("DetailsToolTip");
        }

        internal static string DescribeAction(MergePolicyAction action)
        {
            switch (action)
            {
                case MergePolicyAction.Discard:
                    return "Discard: recorded as merged in TFVC, the target content is kept (like tf merge /discard).";
                case MergePolicyAction.Skip:
                    return "Skip: not merged at all (TFVC is not called for this step).";
                default:
                    return "Merge: the task changes are merged.";
            }
        }

        internal static string DescribeOrigin(MergeRuleOrigin origin, bool overridesTeamRule)
        {
            if (origin == MergeRuleOrigin.Personal)
                return overridesTeamRule ? "personal policy, replaces the team rule with the same id" : "personal policy";
            return "team policy";
        }

        #endregion
    }

    // Una riga di avviso nella scheda (esito del controllo finale, avvisi di sicurezza): icona di
    // errore se blocca, di avviso altrimenti.
    public sealed class TaskMergeNotice
    {
        public TaskMergeNotice(string text, bool isError)
        {
            Text = text ?? string.Empty;
            IsError = isError;
        }

        public string Text { get; private set; }

        public bool IsError { get; private set; }
    }

    public enum TaskMergeFollowUpKind
    {
        // Modifiche del task a righe protette non fuse (resta la versione del target).
        ProtectedLinesKept,

        // File nuovo nel target, portato intero, che contiene righe protette.
        NewFileWithProtectedLines,

        // Discard: registrato come fuso, contenuto del target invariato.
        Discarded,

        // Skip: non fuso.
        Skipped
    }

    // Una voce del promemoria "Manual follow-ups": cio' che le regole di merge hanno lasciato da fare a
    // mano (per file, con i dettagli).
    public sealed class TaskMergeFollowUpViewModel
    {
        public TaskMergeFollowUpViewModel(TaskMergeFollowUpKind kind, int stepNumber, string itemPath, string text, IEnumerable<string> details)
        {
            Kind = kind;
            StepNumber = stepNumber;
            ItemPath = itemPath ?? string.Empty;
            Text = text ?? string.Empty;
            Details = (details ?? Enumerable.Empty<string>()).Where(d => !string.IsNullOrEmpty(d)).ToList().AsReadOnly();
        }

        public TaskMergeFollowUpKind Kind { get; private set; }

        public int StepNumber { get; private set; }

        // Path relativo alla radice del target.
        public string ItemPath { get; private set; }

        public string Text { get; private set; }

        public IReadOnlyList<string> Details { get; private set; }

        public bool HasDetails
        {
            get { return Details.Count > 0; }
        }

        // Le prime righe dei dettagli (tutte nel tooltip).
        public string DetailsText
        {
            get
            {
                const int maxShown = 4;
                var text = string.Join("\n", Details.Take(maxShown));
                if (Details.Count > maxShown)
                    text += string.Format(CultureInfo.InvariantCulture, "\n... and {0} more (see the tooltip or Copy)", Details.Count - maxShown);
                return text;
            }
        }

        // Da fare con attenzione (righe protette), non solo da sapere (Discard/Skip).
        public bool IsWarning
        {
            get { return Kind == TaskMergeFollowUpKind.ProtectedLinesKept || Kind == TaskMergeFollowUpKind.NewFileWithProtectedLines; }
        }

        public string ToolTipText
        {
            get
            {
                var text = string.Format(CultureInfo.InvariantCulture, "Step #{0}: {1}\n{2}", StepNumber, ItemPath, Text);
                return Details.Count == 0 ? text : text + "\n\n" + string.Join("\n", Details);
            }
        }

        // Riga di testo per la copia negli appunti.
        public string ToClipboardText()
        {
            var text = string.Format(CultureInfo.InvariantCulture, "#{0} {1}: {2}", StepNumber, ItemPath, Text);
            return Details.Count == 0 ? text : text + "\n" + string.Join("\n", Details.Select(d => "    " + d.Replace("\n", "\n    ")));
        }
    }

    // Testi condivisi della scheda (in inglese, come il resto della UI).
    internal static class TaskMergeText
    {
        private static readonly KeyValuePair<TaskChangeKind, string>[] ChangeNames =
        {
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Add, "Add"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Edit, "Edit"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Delete, "Delete"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Encoding, "Encoding"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Property, "Property"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Rename, "Rename"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.SourceRename, "Source rename"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Undelete, "Undelete"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Branch, "Branch"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Merge, "Merge"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Rollback, "Rollback"),
            new KeyValuePair<TaskChangeKind, string>(TaskChangeKind.Other, "Other")
        };

        public static string ChangeKind(TaskChangeKind kind)
        {
            var names = ChangeNames.Where(p => (kind & p.Key) == p.Key).Select(p => p.Value).ToList();
            return names.Count == 0 ? "-" : string.Join(", ", names);
        }

        public static string Range(int from, int to)
        {
            return from == to
                ? string.Format(CultureInfo.InvariantCulture, "C{0}", from)
                : string.Format(CultureInfo.InvariantCulture, "C{0}-C{1}", from, to);
        }

        public static string Changesets(IEnumerable<int> ids)
        {
            return ids == null
                ? string.Empty
                : string.Join(", ", ids.Select(id => "C" + id.ToString(CultureInfo.InvariantCulture)));
        }

        // "1 step", "3 steps"
        public static string Count(int count, string noun)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}{2}", count, noun, count == 1 ? string.Empty : "s");
        }
    }
}
