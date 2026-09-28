// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MultiMerge.Prism;
using MultiMerge.Prism.Command;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Controls;
using Microsoft.TeamFoundation.Controls.WPF.TeamExplorer;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;

namespace MultiMerge
{
    public enum TaskMergeChainState
    {
        Idle,                    // nessun piano caricato
        Ready,                   // piano e anteprima pronti, catena non avviata
        Running,                 // merge in corso
        StoppedConflicts,        // fermata: conflitti da risolvere nella scheda
        StoppedFailures,         // fermata: failures di TFVC su un passo (o get latest fallito)
        StoppedNeedsCheckIn,     // fermata: TFVC non considera fatti i merge in sospeso
        StoppedPendingChanges,   // fermata: il target ha modifiche in sospeso non della catena
        StoppedCheckpoint,       // fermata: punto di check-in (fine di una parte, o TF203015 sulla catena)
        Completed                // tutti i passi fatti
    }

    // Tipo del banner di stato della scheda (icona e colore), derivato dallo stato della catena.
    public enum TaskMergeStatusKind
    {
        Info,
        Running,
        Success,
        Warning,
        Error
    }

    // View model della scheda "Merge from Task" (TaskMergeToolWindow). Fonde i changeset di un work
    // item PER FILE: TaskMergePlanDataSource legge da TFVC cambi, esistenza nel target e storia degli
    // item toccati piu' volte; TaskMergePlanner (puro) ne ricava le PARTI (finestre cronologiche chiuse
    // da un check-in, dove un collega ha cambiato lo stesso file tra due changeset del task) e i PASSI
    // (un merge di un item su un intervallo di changeset del task). La catena tiene lo stato in memoria
    // per tutta la sessione di VS, invece di dipendere dalle pagine di Team Explorer.
    //
    // Fatti TFVC su cui si regge il motore:
    // - per un item che esiste nel target TFVC tiene UN solo intervallo di merge in sospeso: un nuovo
    //   merge e' accettato solo se il suo intervallo comprende quello in sospeso, altrimenti TF203015
    //   (ChangeAlreadyPendingException). Per questo tra le parti serve un check-in;
    // - un Merge con anche un solo failure non mette in sospeso nulla: ogni passo passa prima da
    //   un'anteprima (NoMerge) e si fonde solo se TFVC lo accetta;
    // - "gia' fuso" solo con 0 operazioni, 0 conflitti e 0 failure nell'anteprima.
    //
    // I conflitti si risolvono dentro la scheda (resolver integrato, editor a 3 vie). Un conflitto su
    // un FILE non ferma la catena (gli altri file della parte proseguono); su una CARTELLA si' (i suoi
    // file dipendono da lei). A fine parte, con conflitti aperti, la catena si ferma e riparte da sola
    // quando TFVC segna risolto l'ultimo (stessa strada di Continue, con la verifica di ripresa).
    //
    // Sicurezza ("utile ma sicuro al 100%"):
    // - l'utente sceglie i changeset del task con le caselle: gli esclusi diventano per il planner
    //   changeset "di terzi"; gli avvisi di dipendenza (TaskMergeDependencyAnalyzer) e i changeset di
    //   colleghi non fusi nel target tra due passi dello stesso item chiedono una conferma a Start;
    // - prima di OGNI check-in (e prima di aprire Pending Changes precompilato) il controllo finale
    //   indipendente (TaskMergeAudit, dati riletti da TFVC in quel momento) deve essere pulito:
    //   altrimenti il check-in e' bloccato, senza pulsanti che lo aggirino.
    //
    // Regole di merge (MergePolicy: file di team nella radice del target + file personale, che vince):
    // lette con il piano; ogni passo ha un'azione (Merge, Discard, Skip: cambiabile nella scheda prima
    // del merge, per questa esecuzione) e le righe protette del suo file. Skip non chiama TFVC; Discard
    // fonde con AlwaysAcceptMine (che nasconde i failure) tra un'anteprima e una verifica (anteprima
    // 0/0/0 e contenuto locale identico a prima); un file la cui sorgente cambia righe protette si fonde
    // con il risultato che tiene quelle del target (TaskMergeProtectedLines). Il controllo finale
    // verifica di nuovo i Discard (R5) e le righe protette (R6); cio' che resta da fare a mano e' nel
    // promemoria "Manual follow-ups" e in una riga del commento di check-in.
    //
    // Threading: i command handler partono sul thread UI; ogni chiamata TFS va in Task.Run e le
    // proprieta'/collezioni bindate si aggiornano solo dopo l'await (di nuovo sul thread UI).
    public sealed class TaskMergeViewModel : BindableBase
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        private readonly StringBuilder _log = new StringBuilder();
        // Thread UI (il view model nasce nel costruttore della tool window, sul thread UI): serve a
        // riportarci l'avanzamento dei lavori in background e l'evento CommitCheckin di TFVC.
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        // Durante le anteprime di tutti i passi il testo del registro si aggiorna a blocchi (ricostruire
        // la stringa a ogni riga costerebbe troppo con centinaia di passi).
        private bool _deferLogText;
        // Numero dell'ultimo lavoro in background che puo' scrivere l'avanzamento nel banner.
        private int _progressSeq;

        // Connessione (letta al Load dal contesto corrente di Team Explorer).
        private TfsTeamProjectCollection _tfs;
        private VersionControlServer _versionControl;
        private WorkItemStore _workItemStore;

        // Piano corrente.
        private int _workItemId;
        // Radice del branch target come scritta dall'utente (per log e confronto con l'input).
        private string _targetPath;
        private string _sourceTopFolder;
        // Cartella del target che contiene tutti gli item del piano: la sottocartella del top folder
        // sorgente riportata sotto _targetPath (TaskTargetPathMapper). Get latest, conflitti, pending.
        private string _targetMergePath;
        private TaskChangesetGroup _plannedGroup;
        // Dati letti da TFVC per il gruppo (cambi, esistenza nel target, storia): servono a ricalcolare
        // il piano per qualsiasi scelta di changeset senza rileggere nulla.
        private TaskMergePlanData _planData;
        private TaskMergePlan _plan;
        // Changeset del task con cui e' stato calcolato _plan (null finche' non c'e' un piano).
        private IReadOnlyList<int> _plannedSelection;
        // Avvisi del piano corrente: changeset esclusi da cui dipendono selezionati successivi, e
        // changeset di colleghi non fusi nel target tra due passi dello stesso item.
        private IReadOnlyList<ChangesetDependencyWarning> _dependencyWarnings = new List<ChangesetDependencyWarning>();
        private IReadOnlyList<TaskMergeColleagueWarning> _colleagueWarnings = new List<TaskMergeColleagueWarning>();
        private readonly Dictionary<int, TaskMergePartViewModel> _partsByNumber = new Dictionary<int, TaskMergePartViewModel>();
        private readonly List<TaskMergeCheckpointMarkerViewModel> _markers = new List<TaskMergeCheckpointMarkerViewModel>();
        // Workspace in cui la catena ha messo in sospeso i merge (fissata al primo merge reale).
        private Workspace _chainWorkspace;
        // La catena ha fatto almeno un merge reale in questa sessione e non e' ancora chiusa. Resta vero
        // anche dopo i check-in intermedi, che azzerano MergedInSession dei passi archiviati.
        private bool _chainActive;
        // Punto di check-in su cui la catena e' ferma (StoppedCheckpoint): la parte i cui merge sono in
        // sospeso. _checkpointDynamicReason e' valorizzato quando il check-in serve PRIMA di un passo
        // della stessa parte (TF203015 su un item gia' fuso dalla catena: non previsto dal piano).
        private int _checkpointPart;
        private string _checkpointDynamicReason;
        // Attesa del check-in fatto da Pending Changes (evento CommitCheckin di TFVC).
        private VersionControlServer _commitWatchServer;
        private string _commitWatchTarget;
        // Pending Changes e' stato aperto precompilato al punto di check-in (o a catena finita) e la
        // scheda aspetta il check-in fatto li': l'iscrizione si toglie mentre la scheda lavora
        // (Continue, check-in della scheda) e si rimette quando la catena torna allo stesso punto
        // senza che il check-in sia avvenuto (Continue che ritrova il punto, conferma negata...).
        private bool _reviewWaiting;
        // Changeset archiviato sotto il target (CommitCheckin) arrivato mentre la scheda era occupata:
        // si ricontrolla alla fine dell'operazione in corso, invece di perderlo.
        private int _checkinSeenWhileBusy;
        // L'anteprima di tutti i passi del piano corrente e' arrivata in fondo (Start la richiede,
        // salvo catena in corso o ferma: in quei casi Start/Continue rifa' comunque l'anteprima).
        private bool _previewCompleted;

        private bool _suppressSelectionHandlers;
        // true mentre la lista dei conflitti viene ricostruita: la ListBox spinge SelectedItem = null
        // quando la collezione si svuota, e quel null non deve chiudere il resolver.
        private bool _rebuildingConflicts;
        // Evita la ricorsione se il resolver notifica IsBusy dentro RaiseCanExecuteChanged.
        private bool _raisingState;
        // L'ultima operazione e' finita con un errore (banner rosso finche' non ne parte un'altra).
        private bool _hasError;
        // Resolver aperti, uno per conflitto (chiave: ConflictKey), finche' il conflitto resta nella
        // lista: cambiando selezione si ritrova il lavoro fatto (scelte per blocco, modifiche a mano).
        // Un resolver che ha chiuso il suo conflitto in TFVC esce subito da qui: se lo stesso file
        // ricompare nella lista e' un conflitto nuovo, con un resolver nuovo.
        private readonly Dictionary<string, ConflictResolverViewModel> _resolvers =
            new Dictionary<string, ConflictResolverViewModel>(StringComparer.OrdinalIgnoreCase);

        // Richiesta arrivata da Team Explorer quando la scheda non poteva caricarla subito (catena in
        // corso o operazione in corso): le caselle restano quelle della catena (altrimenti Continue e la
        // ripartenza automatica si bloccherebbero), la richiesta aspetta nel suo riquadro.
        private string _requestedWorkItemText;
        private string _requestedTargetText;

        // Regole di merge (MergePolicy) del piano corrente, lette con il piano dal file di team del target
        // e dal file personale (il personale vince sempre). Le decisioni stanno nei passi (azione di
        // esecuzione, righe protette): una catena in corso tiene quelle con cui e' partita.
        private EffectiveMergePolicy _policy;
        private readonly List<string> _policyLoadErrors = new List<string>();
        // Passo del piano -> riga della scheda (le regole ragionano sui passi del planner).
        private readonly Dictionary<TaskMergeStep, TaskMergeStepViewModel> _stepViewModels =
            new Dictionary<TaskMergeStep, TaskMergeStepViewModel>();
        // Righe protette per item del target (passi Merge di file): il resolver dei conflitti parte dal
        // risultato che le tiene.
        private readonly Dictionary<string, IReadOnlyList<MergeLineRule>> _lineRulesByTarget =
            new Dictionary<string, IReadOnlyList<MergeLineRule>>(StringComparer.OrdinalIgnoreCase);
        // Contenuto locale del target fotografato subito prima di ogni Discard: la verifica dopo il merge
        // e il controllo finale lo confrontano con il disco.
        private readonly Dictionary<TaskMergeStep, TaskMergeContentSnapshot> _discardSnapshots =
            new Dictionary<TaskMergeStep, TaskMergeContentSnapshot>();
        // Le regole sono cambiate (MergePolicyStore.PolicyChanged) mentre la scheda lavorava: si
        // riguardano alla fine dell'operazione in corso.
        private bool _policyChangedWhileBusy;

        public TaskMergeViewModel(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            Groups = new ObservableCollection<TaskChangesetGroup>();
            Workspaces = new ObservableCollection<Workspace>();
            Steps = new ObservableCollection<TaskMergeStepViewModel>();
            BarItems = new ObservableCollection<object>();
            Conflicts = new ObservableCollection<TaskMergeConflictViewModel>();
            PlanErrors = new ObservableCollection<string>();
            PlanWarnings = new ObservableCollection<string>();
            SafetyWarnings = new ObservableCollection<string>();
            AuditNotices = new ObservableCollection<TaskMergeNotice>();
            TaskChangesets = new ObservableCollection<TaskMergeChangesetViewModel>();
            PolicyErrors = new ObservableCollection<string>();
            FollowUps = new ObservableCollection<TaskMergeFollowUpViewModel>();

            LoadCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(LoadCoreAsync), LoadCanExecute);
            StartCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(StartCommandCoreAsync), StartCanExecute);
            CheckInCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(CheckInCoreAsync), CheckInCanExecute);
            ReviewPendingChangesCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(ReviewPendingChangesCoreAsync), ReviewPendingChangesCanExecute);
            SelectAllChangesetsCommand = new DelegateCommand(SelectAllChangesets, SelectAllChangesetsCanExecute);
            IncludeChangesetsCommand = new DelegateCommand<IList>(rows => SetChangesetsIncluded(rows, true), rows => CanSetChangesetsIncluded(rows, true));
            ExcludeChangesetsCommand = new DelegateCommand<IList>(rows => SetChangesetsIncluded(rows, false), rows => CanSetChangesetsIncluded(rows, false));
            RecomputePlanCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(ReplanCoreAsync), () => CanChangeSelection && ShowRecomputePlan);
            RefreshConflictsCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(RefreshConflictsAndContinueCoreAsync), RefreshConflictsCanExecute);
            OpenInVsResolveConflictsCommand = new DelegateCommand(OpenInVsResolveConflicts, OpenInVsResolveConflictsCanExecute);
            RestoreInputCommand = new DelegateCommand(RestoreInput, () => HasStaleInput);
            LoadRequestCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(LoadRequestCoreAsync), LoadRequestCanExecute);
            DismissRequestCommand = new DelegateCommand(ClearTeamExplorerRequest, () => HasTeamExplorerRequest);
            OpenPoliciesCommand = DelegateCommand.FromAsyncHandler(OpenPoliciesAsync);
            CopyFollowUpsCommand = new DelegateCommand(CopyFollowUps, () => HasFollowUps);

            // Il view model vive quanto la sessione di VS (campo statico della tool window): l'evento
            // statico non lo tiene in vita piu' del necessario.
            MergePolicyStore.PolicyChanged += OnPolicyChanged;

            _state = TaskMergeChainState.Idle;
            StatusMessage = "Enter a work item and the target branch, then press Load.";
            UpdateProgress();
        }

        #region Bindable properties

        public string WorkItemIdText
        {
            get { return _workItemIdText; }
            set
            {
                if (SetProperty(ref _workItemIdText, value))
                    RaiseInputDependentProperties();
            }
        }
        private string _workItemIdText;

        public string TargetBranchText
        {
            get { return _targetBranchText; }
            set
            {
                if (SetProperty(ref _targetBranchText, value))
                    RaiseInputDependentProperties();
            }
        }
        private string _targetBranchText;

        public string StatusMessage
        {
            get { return _statusMessage; }
            set { SetProperty(ref _statusMessage, value); }
        }
        private string _statusMessage;

        // Icona e colore del banner di stato.
        public TaskMergeStatusKind StatusKind
        {
            get
            {
                if (IsBusy || State == TaskMergeChainState.Running)
                    return TaskMergeStatusKind.Running;
                if (_hasError)
                    return TaskMergeStatusKind.Error;

                switch (State)
                {
                    case TaskMergeChainState.Completed:
                        return TaskMergeStatusKind.Success;
                    case TaskMergeChainState.StoppedFailures:
                        return TaskMergeStatusKind.Error;
                    case TaskMergeChainState.StoppedConflicts:
                    case TaskMergeChainState.StoppedNeedsCheckIn:
                    case TaskMergeChainState.StoppedPendingChanges:
                    case TaskMergeChainState.StoppedCheckpoint:
                        return TaskMergeStatusKind.Warning;
                    default:
                        return TaskMergeStatusKind.Info;
                }
            }
        }

        // Titolo del work item caricato (null finche' non c'e' un piano).
        public string WorkItemTitle
        {
            get { return _workItemTitle; }
            private set
            {
                if (SetProperty(ref _workItemTitle, value))
                    OnPropertyChanged("LoadedWorkItemText");
            }
        }
        private string _workItemTitle;

        // "#1234": il work item del piano caricato (non le caselle, che l'utente puo' aver cambiato).
        // Null finche' non c'e' un titolo.
        public string LoadedWorkItemText
        {
            get
            {
                return _workItemTitle == null || _workItemId <= 0
                    ? null
                    : "#" + _workItemId.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string SourceTopFolder
        {
            get { return _sourceTopFolderText; }
            private set { SetProperty(ref _sourceTopFolderText, value); }
        }
        private string _sourceTopFolderText;

        public string TargetFolder
        {
            get { return _targetFolderText; }
            private set { SetProperty(ref _targetFolderText, value); }
        }
        private string _targetFolderText;

        // Work item o target modificati dopo l'ultimo Load: la catena lavorerebbe ancora sul piano
        // vecchio, quindi Start resta disabilitato finche' non si rifa' Load.
        public bool HasStaleInput
        {
            get
            {
                if (_plannedGroup == null)
                    return false;

                int id;
                var idMatches = int.TryParse(WorkItemIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                    && id == _workItemId;
                var target = (TargetBranchText ?? string.Empty).Trim().TrimEnd('/');
                return !idMatches || !string.Equals(target, _targetPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Testo dell'avviso "input cambiato": a catena in corso Load la scarterebbe, quindi si invita a
        // rimettere i valori della catena (mostrati), non a premere Load.
        public string StaleInputText
        {
            get
            {
                if (IsChainInProgress)
                    return string.Format(CultureInfo.InvariantCulture,
                        "Work item or target changed while the chain of work item {0} is in progress: restore {0} and {1} to continue it (Load would discard the chain).",
                        _workItemId, _targetPath);
                return "Work item or target changed since the last Load: press Load to plan again (Start is disabled until then).";
            }
        }

        // Richiesta di Team Explorer in attesa (vedi _requestedWorkItemText).
        public bool HasTeamExplorerRequest
        {
            get { return _requestedWorkItemText != null; }
        }

        public string TeamExplorerRequestText
        {
            get
            {
                if (!HasTeamExplorerRequest)
                    return null;
                var request = string.Format(CultureInfo.InvariantCulture, "Team Explorer asked for work item {0} ({1}).",
                    _requestedWorkItemText, _requestedTargetText);
                if (!IsChainInProgress)
                    return request;
                if (State == TaskMergeChainState.Completed)
                    return request + string.Format(CultureInfo.InvariantCulture,
                        " Work item {0} is merged but not checked in yet: check it in (Check in & finish), then load the new one.", _workItemId);
                return request + string.Format(CultureInfo.InvariantCulture,
                    " The chain of work item {0} is not finished: complete it first (it continues where it stopped), or discard it.", _workItemId);
            }
        }

        public string LoadRequestButtonText
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    IsChainInProgress ? "Discard this chain and load work item {0}" : "Load work item {0}", _requestedWorkItemText);
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    RaiseStateDependentProperties();
            }
        }
        private bool _isBusy;

        // Il resolver attivo sta lavorando (download delle versioni, risoluzione in TFVC): intanto la
        // scheda non avvia altre operazioni TFVC sullo stesso workspace.
        public bool IsResolverBusy
        {
            get { return _activeResolver != null && _activeResolver.IsBusy; }
        }

        public TaskMergeChainState State
        {
            get { return _state; }
            private set
            {
                if (SetProperty(ref _state, value))
                    RaiseStateDependentProperties();
            }
        }
        private TaskMergeChainState _state;

        public ObservableCollection<TaskChangesetGroup> Groups { get; private set; }

        public bool HasMultipleGroups
        {
            get { return Groups.Count > 1; }
        }

        public TaskChangesetGroup SelectedGroup
        {
            get { return _selectedGroup; }
            set
            {
                if (ReferenceEquals(_selectedGroup, value))
                    return;
                _selectedGroup = value;
                OnPropertyChanged("SelectedGroup");
                if (!_suppressSelectionHandlers && value != null)
                    FireAndForget(() => RunBusyAsync(PlanAndPreviewCoreAsync));
            }
        }
        private TaskChangesetGroup _selectedGroup;

        public ObservableCollection<Workspace> Workspaces { get; private set; }

        public Workspace SelectedWorkspace
        {
            get { return _selectedWorkspace; }
            set
            {
                if (ReferenceEquals(_selectedWorkspace, value))
                    return;
                _selectedWorkspace = value;
                OnPropertyChanged("SelectedWorkspace");

                // Senza catena in corso, workspace e conflitti mostrati seguono la nuova scelta: un
                // _chainWorkspace rimasto dal workspace precedente farebbe lavorare Refresh conflicts,
                // il resolver e Pending Changes sul workspace sbagliato.
                if (!IsChainInProgress)
                {
                    _chainWorkspace = null;
                    ClearConflicts();
                }

                if (!_suppressSelectionHandlers && value != null && _plannedGroup != null)
                    FireAndForget(() => RunBusyAsync(PreviewCoreAsync));
            }
        }
        private Workspace _selectedWorkspace;

        // Workspace e source branch si possono cambiare solo quando la catena non e' in corso.
        public bool CanChangeSelection
        {
            get { return CanAct() && !IsChainInProgress; }
        }

        // La lista dei conflitti si puo' cambiare solo quando nessuna operazione e' in corso.
        public bool CanSelectConflict
        {
            get { return CanAct(); }
        }

        // La catena e' "in corso" se questa finestra ha lanciato almeno un merge reale che non e' ancora
        // arrivato alla fine (merge in sospeso nel workspace, o parti successive da fondere dopo un
        // check-in intermedio).
        public bool IsChainInProgress
        {
            get { return _chainActive || Steps.Any(s => s.MergedInSession); }
        }

        // Merge della catena in sospeso nel workspace (non ancora archiviati).
        public bool HasPendingChainMerges
        {
            get { return Steps.Any(s => s.MergedInSession); }
        }

        public ObservableCollection<TaskMergeStepViewModel> Steps { get; private set; }

        // Elementi della barra segmentata, in ordine: i passi e, tra una parte e l'altra, il punto di
        // check-in (TaskMergeCheckpointMarkerViewModel).
        public ObservableCollection<object> BarItems { get; private set; }

        public bool HasSteps
        {
            get { return Steps.Count > 0; }
        }

        // Problemi del piano: con errori Start resta disabilitato.
        public ObservableCollection<string> PlanErrors { get; private set; }

        public ObservableCollection<string> PlanWarnings { get; private set; }

        public bool HasPlanErrors
        {
            get { return PlanErrors.Count > 0; }
        }

        public bool HasPlanWarnings
        {
            get { return PlanWarnings.Count > 0; }
        }

        // Avvisi di sicurezza del piano (dipendenze da changeset esclusi, changeset di colleghi non fusi
        // nel target): con avvisi Start chiede una conferma esplicita.
        public ObservableCollection<string> SafetyWarnings { get; private set; }

        public bool HasSafetyWarnings
        {
            get { return SafetyWarnings.Count > 0; }
        }

        // Esito dell'ultimo controllo finale (problemi bloccanti e note), finche' la catena non riparte.
        public ObservableCollection<TaskMergeNotice> AuditNotices { get; private set; }

        public bool HasAuditNotices
        {
            get { return AuditNotices.Count > 0; }
        }

        public bool HasPlanIssues
        {
            get { return HasPlanErrors || HasPlanWarnings || HasSafetyWarnings || HasAuditNotices || HasPolicyErrors; }
        }

        // Problemi delle regole di merge (file non leggibile, regole non valide, azioni incompatibili tra
        // i passi): con problemi Start/Continue resta disabilitato.
        public ObservableCollection<string> PolicyErrors { get; private set; }

        public bool HasPolicyErrors
        {
            get { return PolicyErrors.Count > 0; }
        }

        // "Merge policy: 3 to discard · 1 skipped · 5 files with protected lines" (null senza piano).
        public string PolicySummaryText
        {
            get { return _policySummaryText; }
            private set
            {
                if (SetProperty(ref _policySummaryText, value))
                    OnPropertyChanged("HasPolicySummary");
            }
        }
        private string _policySummaryText;

        public bool HasPolicySummary
        {
            get { return !string.IsNullOrEmpty(_policySummaryText); }
        }

        // Da dove vengono le regole (file di team, file personale), per il tooltip del riepilogo.
        public string PolicyDescriptionText
        {
            get { return _policyDescriptionText; }
            private set { SetProperty(ref _policyDescriptionText, value); }
        }
        private string _policyDescriptionText;

        // Avviso sulle regole (es. cambiate durante una catena: valgono dal prossimo Load).
        public string PolicyNoticeText
        {
            get { return _policyNoticeText; }
            private set
            {
                if (SetProperty(ref _policyNoticeText, value))
                    OnPropertyChanged("HasPolicyNotice");
            }
        }
        private string _policyNoticeText;

        public bool HasPolicyNotice
        {
            get { return !string.IsNullOrEmpty(_policyNoticeText); }
        }

        // Promemoria "Manual follow-ups": cio' che le regole di merge lasciano da fare a mano.
        public ObservableCollection<TaskMergeFollowUpViewModel> FollowUps { get; private set; }

        public bool HasFollowUps
        {
            get { return FollowUps.Count > 0; }
        }

        public string FollowUpsHeaderText
        {
            get
            {
                var attention = FollowUps.Count(f => f.IsWarning);
                var text = string.Format(CultureInfo.InvariantCulture, "Manual follow-ups ({0})", FollowUps.Count);
                return attention > 0
                    ? text + string.Format(CultureInfo.InvariantCulture, " · {0} with protected lines", TaskMergeText.Count(attention, "file"))
                    : text;
            }
        }

        // Changeset del task con le caselle (A2): modificabili solo senza catena in corso.
        public ObservableCollection<TaskMergeChangesetViewModel> TaskChangesets { get; private set; }

        public bool HasTaskChangesets
        {
            get { return TaskChangesets.Count > 0; }
        }

        public bool HasDependencyWarnings
        {
            get { return !HasStaleSelection && _dependencyWarnings.Count > 0; }
        }

        // "Task changesets: 17 of 19 selected · 2 dependency warnings"
        public string ChangesetsHeaderText
        {
            get
            {
                var selected = TaskChangesets.Count(c => c.IsSelected);
                var text = string.Format(CultureInfo.InvariantCulture, "Task changesets: {0} of {1} selected", selected, TaskChangesets.Count);
                if (!HasStaleSelection && _dependencyWarnings.Count > 0)
                    text += " · " + TaskMergeText.Count(_dependencyWarnings.Count, "dependency warning");
                return text;
            }
        }

        // Le caselle non corrispondono piu' al piano calcolato (es. ricalcolo fallito): Start resta
        // disabilitato finche' il piano non si ricalcola.
        public bool HasStaleSelection
        {
            get
            {
                if (_plannedSelection == null || TaskChangesets.Count == 0)
                    return false;
                var current = new HashSet<int>(TaskChangesets.Where(c => c.IsSelected).Select(c => c.ChangesetId));
                return !current.SetEquals(_plannedSelection);
            }
        }

        // Il piano va ricalcolato con le caselle attuali (caselle cambiate, o ricalcolo non riuscito).
        public bool ShowRecomputePlan
        {
            get { return _planData != null && !IsBusy && !IsChainInProgress && (_plan == null || HasStaleSelection); }
        }

        // Modifica le caselle; Update plan applica la selezione in un solo ricalcolo.
        public DelegateCommand SelectAllChangesetsCommand { get; private set; }

        public DelegateCommand<IList> IncludeChangesetsCommand { get; private set; }

        public DelegateCommand<IList> ExcludeChangesetsCommand { get; private set; }

        // Ricalcola il piano con le caselle attuali.
        public DelegateCommand RecomputePlanCommand { get; private set; }

        // "110 steps in 2 parts · 1 check-in point · 9 expected conflicts · 3 already merged"
        public string PlanSummaryText
        {
            get { return _planSummaryText; }
            private set
            {
                if (SetProperty(ref _planSummaryText, value))
                    OnPropertyChanged("HasPlanSummary");
            }
        }
        private string _planSummaryText;

        public bool HasPlanSummary
        {
            get { return !string.IsNullOrEmpty(_planSummaryText); }
        }

        public int PartCount
        {
            get { return _plan == null ? 0 : _plan.Parts.Count; }
        }

        public int ProgressValue
        {
            get { return _progressValue; }
            private set { SetProperty(ref _progressValue, value); }
        }
        private int _progressValue;

        public int ProgressMaximum
        {
            get { return _progressMaximum; }
            private set { SetProperty(ref _progressMaximum, value); }
        }
        private int _progressMaximum;

        // Passi fatti (fusi in questa sessione o gia' dentro secondo TFVC).
        public int DoneStepCount
        {
            get { return _doneStepCount; }
            private set { SetProperty(ref _doneStepCount, value); }
        }
        private int _doneStepCount;

        public int RemainingStepCount
        {
            get { return _remainingStepCount; }
            private set { SetProperty(ref _remainingStepCount, value); }
        }
        private int _remainingStepCount;

        // "73/110 steps merged · part 1 of 2 · 1 check-in point"
        public string ProgressText
        {
            get { return _progressText; }
            private set { SetProperty(ref _progressText, value); }
        }
        private string _progressText;

        public ObservableCollection<TaskMergeConflictViewModel> Conflicts { get; private set; }

        // Con conflitti aperti l'area principale mostra lista dei conflitti + resolver; senza, la
        // lista dei passi.
        public bool ShowConflictsPanel
        {
            get { return Conflicts.Count > 0; }
        }

        public bool ShowStepList
        {
            get { return !ShowConflictsPanel; }
        }

        // Catena in corso o conflitti aperti: la vista riduce l'intestazione a una riga (le caselle si
        // riaprono con "Edit") e lascia l'altezza al lavoro sui conflitti e sui passi.
        public bool IsCompactHeader
        {
            get { return IsChainInProgress || ShowConflictsPanel; }
        }

        public string ConflictsHeaderText
        {
            get
            {
                return Conflicts.Count == 1
                    ? "1 conflict"
                    : string.Format(CultureInfo.InvariantCulture, "{0} conflicts", Conflicts.Count);
            }
        }

        // Conflitto selezionato nella lista: il resolver integrato lavora su questo.
        public TaskMergeConflictViewModel SelectedConflict
        {
            get { return _selectedConflict; }
            set
            {
                if (_rebuildingConflicts || ReferenceEquals(_selectedConflict, value))
                    return;
                _selectedConflict = value;
                OnPropertyChanged("SelectedConflict");
                ActivateResolver(value);
            }
        }
        private TaskMergeConflictViewModel _selectedConflict;

        // Resolver integrato del conflitto selezionato (null se nessun conflitto e' selezionato).
        public ConflictResolverViewModel ActiveResolver
        {
            get { return _activeResolver; }
        }
        private ConflictResolverViewModel _activeResolver;

        public bool HasActiveResolver
        {
            get { return _activeResolver != null; }
        }

        public string StartButtonText
        {
            get
            {
                return IsChainInProgress || IsStopped(State) ? "Continue" : "Start merge";
            }
        }

        // Al punto di check-in, o a catena finita con merge in sospeso, il pulsante principale e' il
        // check-in (con "Review in Pending Changes" accanto) invece di Start/Continue.
        public bool ShowCheckInActions
        {
            get
            {
                return State == TaskMergeChainState.StoppedCheckpoint
                    || (State == TaskMergeChainState.Completed && HasPendingChainMerges);
            }
        }

        public bool ShowStartButton
        {
            get { return !ShowCheckInActions; }
        }

        // Al punto di check-in resta un "Continue" discreto: per chi ha archiviato altrove.
        public bool ShowContinueLink
        {
            get { return State == TaskMergeChainState.StoppedCheckpoint; }
        }

        public string CheckInButtonText
        {
            get
            {
                if (State == TaskMergeChainState.StoppedCheckpoint)
                {
                    return _checkpointDynamicReason == null && PartCount > 1
                        ? string.Format(CultureInfo.InvariantCulture, "Check in part {0} of {1} & continue", _checkpointPart, PartCount)
                        : "Check in & continue";
                }
                return "Check in & finish";
            }
        }

        public string CheckInToolTip
        {
            get
            {
                return State == TaskMergeChainState.StoppedCheckpoint
                    ? "Check in what the chain merged so far (after a confirmation), associated with the work item; then get latest and continue with the next steps. If conflicts, check-in notes or policies prevent it, Pending Changes opens instead."
                    : "Check in the last merged steps (after a confirmation), associated with the work item. If conflicts, check-in notes or policies prevent it, Pending Changes opens instead.";
            }
        }

        public string LogText
        {
            get { return _logText; }
            private set { SetProperty(ref _logText, value); }
        }
        private string _logText;

        public DelegateCommand LoadCommand { get; private set; }

        public DelegateCommand StartCommand { get; private set; }

        // Check-in della parte (o dell'ultima) con conferma; poi la catena continua da sola.
        public DelegateCommand CheckInCommand { get; private set; }

        // Pending Changes precompilato (commento della parte e work item); al punto di check-in la
        // catena aspetta il check-in fatto li' e poi continua da sola.
        public DelegateCommand ReviewPendingChangesCommand { get; private set; }

        public DelegateCommand RefreshConflictsCommand { get; private set; }

        // Ripiego per i casi che il resolver integrato non gestisce: pagina Resolve Conflicts di VS.
        public DelegateCommand OpenInVsResolveConflictsCommand { get; private set; }

        // Rimette nelle caselle work item e target del piano caricato.
        public DelegateCommand RestoreInputCommand { get; private set; }

        // Carica la richiesta di Team Explorer in attesa (scartando la catena, con conferma).
        public DelegateCommand LoadRequestCommand { get; private set; }

        public DelegateCommand DismissRequestCommand { get; private set; }

        // Apre la scheda "Merge Policies" (regole di team del target e personali).
        public DelegateCommand OpenPoliciesCommand { get; private set; }

        // Copia il promemoria negli appunti.
        public DelegateCommand CopyFollowUpsCommand { get; private set; }

        #endregion

        #region Command guards

        private bool CanAct()
        {
            return !IsBusy && !IsResolverBusy;
        }

        private bool LoadCanExecute()
        {
            int id;
            return CanAct()
                && int.TryParse(WorkItemIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0
                && !string.IsNullOrWhiteSpace(TargetBranchText);
        }

        // Start solo con un piano valido, calcolato con le caselle attuali, anteprima arrivata in fondo,
        // regole di merge senza problemi e nessun passo bloccato da TFVC (un passo con azione Skip non e'
        // bloccato: non si tocca). A catena in corso o ferma, Continue rifa' l'anteprima da se'.
        private bool StartCanExecute()
        {
            return CanAct() && _plannedGroup != null && _plan != null && _plan.IsValid && Steps.Count > 0
                && State != TaskMergeChainState.Running
                && (_previewCompleted || IsChainInProgress || IsStopped(State))
                && !HasStaleInput
                && !HasStaleSelection
                && !HasPolicyErrors
                && !Steps.Any(s => s.Status == TaskMergeStepStatus.Blocked);
        }

        private bool SelectAllChangesetsCanExecute()
        {
            return CanChangeSelection && _planData != null && TaskChangesets.Any(c => !c.IsSelected);
        }

        private bool CheckInCanExecute()
        {
            return CanAct() && ShowCheckInActions && _chainWorkspace != null && _plan != null
                && !string.IsNullOrEmpty(_targetMergePath) && !HasStaleInput;
        }

        // Anche fermi su modifiche in sospeso senza catena di questa sessione (es. merge di una sessione
        // precedente i cui conflitti sono stati risolti qui): Pending Changes e' la strada per proseguire.
        // Con work item o target cambiati dopo il Load no, come il check-in: dopo il check-in fatto in
        // Pending Changes la catena ripartirebbe da sola su un piano che la scheda dice superato.
        private bool ReviewPendingChangesCanExecute()
        {
            return CanAct() && !string.IsNullOrEmpty(_targetMergePath) && !HasStaleInput
                && (IsChainInProgress || State == TaskMergeChainState.StoppedPendingChanges);
        }

        private bool LoadRequestCanExecute()
        {
            return CanAct() && HasTeamExplorerRequest;
        }

        private bool RefreshConflictsCanExecute()
        {
            return CanAct() && (_chainWorkspace ?? _selectedWorkspace) != null && !string.IsNullOrEmpty(_targetMergePath);
        }

        private bool OpenInVsResolveConflictsCanExecute()
        {
            return CanAct() && _chainWorkspace != null && Conflicts.Count > 0;
        }

        private static bool IsStopped(TaskMergeChainState state)
        {
            return state == TaskMergeChainState.StoppedConflicts
                || state == TaskMergeChainState.StoppedFailures
                || state == TaskMergeChainState.StoppedNeedsCheckIn
                || state == TaskMergeChainState.StoppedPendingChanges
                || state == TaskMergeChainState.StoppedCheckpoint;
        }

        private void RaiseStateDependentProperties()
        {
            if (_raisingState)
                return;

            _raisingState = true;
            try
            {
                OnPropertyChanged("CanChangeSelection");
                OnPropertyChanged("CanSelectConflict");
                OnPropertyChanged("IsChainInProgress");
                OnPropertyChanged("HasPendingChainMerges");
                OnPropertyChanged("IsResolverBusy");
                OnPropertyChanged("StartButtonText");
                OnPropertyChanged("ShowCheckInActions");
                OnPropertyChanged("ShowStartButton");
                OnPropertyChanged("ShowContinueLink");
                OnPropertyChanged("CheckInButtonText");
                OnPropertyChanged("CheckInToolTip");
                OnPropertyChanged("ShowConflictsPanel");
                OnPropertyChanged("ShowStepList");
                OnPropertyChanged("IsCompactHeader");
                OnPropertyChanged("ConflictsHeaderText");
                OnPropertyChanged("HasStaleInput");
                OnPropertyChanged("StaleInputText");
                OnPropertyChanged("StatusKind");
                OnPropertyChanged("HasStaleSelection");
                OnPropertyChanged("ShowRecomputePlan");
                RaiseRequestProperties();
                LoadCommand.RaiseCanExecuteChanged();
                StartCommand.RaiseCanExecuteChanged();
                CheckInCommand.RaiseCanExecuteChanged();
                ReviewPendingChangesCommand.RaiseCanExecuteChanged();
                RefreshConflictsCommand.RaiseCanExecuteChanged();
                OpenInVsResolveConflictsCommand.RaiseCanExecuteChanged();
                RestoreInputCommand.RaiseCanExecuteChanged();
                SelectAllChangesetsCommand.RaiseCanExecuteChanged();
                RaiseChangesetRowSelectionCommands();
                RecomputePlanCommand.RaiseCanExecuteChanged();
                CopyFollowUpsCommand.RaiseCanExecuteChanged();
                UpdatePolicyEditability();
                if (_activeResolver != null)
                    _activeResolver.RaiseCanExecuteChanged();
            }
            finally
            {
                _raisingState = false;
            }
        }

        private void RaiseInputDependentProperties()
        {
            OnPropertyChanged("HasStaleInput");
            OnPropertyChanged("StaleInputText");
            LoadCommand.RaiseCanExecuteChanged();
            StartCommand.RaiseCanExecuteChanged();
            CheckInCommand.RaiseCanExecuteChanged();
            ReviewPendingChangesCommand.RaiseCanExecuteChanged();
            RestoreInputCommand.RaiseCanExecuteChanged();
        }

        private void RaiseRequestProperties()
        {
            OnPropertyChanged("HasTeamExplorerRequest");
            OnPropertyChanged("TeamExplorerRequestText");
            OnPropertyChanged("LoadRequestButtonText");
            LoadRequestCommand.RaiseCanExecuteChanged();
            DismissRequestCommand.RaiseCanExecuteChanged();
        }

        private void RaisePlanProperties()
        {
            OnPropertyChanged("HasSteps");
            OnPropertyChanged("PartCount");
            OnPropertyChanged("HasPlanErrors");
            OnPropertyChanged("HasPlanWarnings");
            OnPropertyChanged("HasSafetyWarnings");
            OnPropertyChanged("HasAuditNotices");
            OnPropertyChanged("HasPolicyErrors");
            OnPropertyChanged("HasPlanIssues");
            OnPropertyChanged("HasFollowUps");
            OnPropertyChanged("FollowUpsHeaderText");
            CopyFollowUpsCommand.RaiseCanExecuteChanged();
            RaiseSelectionProperties();
        }

        private void RaiseSelectionProperties()
        {
            OnPropertyChanged("HasTaskChangesets");
            OnPropertyChanged("HasDependencyWarnings");
            OnPropertyChanged("ChangesetsHeaderText");
            OnPropertyChanged("HasStaleSelection");
            OnPropertyChanged("ShowRecomputePlan");
            RaiseChangesetRowSelectionCommands();
            StartCommand.RaiseCanExecuteChanged();
            SelectAllChangesetsCommand.RaiseCanExecuteChanged();
            RecomputePlanCommand.RaiseCanExecuteChanged();
        }

        #endregion

        #region Infrastructure

        // Esegue un'azione con la scheda "occupata"; qualsiasi eccezione finisce nel registro e nel
        // messaggio di stato (mai fuori: i command handler sono async void lato WPF).
        private async Task RunBusyAsync(Func<Task> action)
        {
            if (IsBusy)
                return;

            _hasError = false;
            IsBusy = true;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _deferLogText = false;
                Log("ERROR: " + ex.Message);
                _logger.Error("Merge from Task: unexpected error", ex);
                StatusMessage = "Error: " + ex.Message;
                _hasError = true;
                // Il passo in corso non resta "Merging": la barra e la lista devono dire dove si e' fermata.
                foreach (var step in Steps.Where(s => s.Status == TaskMergeStepStatus.Merging))
                {
                    step.Status = TaskMergeStepStatus.Failed;
                    step.Details = "error: " + ex.Message;
                }
                if (State == TaskMergeChainState.Running)
                    State = TaskMergeChainState.StoppedFailures;
            }
            finally
            {
                IsBusy = false;
                UpdateProgress();
                RaiseStateDependentProperties();

                // Un check-in sotto il target arrivato mentre la scheda lavorava: si ricontrolla ora
                // (OnTargetCheckedInAsync verifica di nuovo attesa e stato).
                var seen = _checkinSeenWhileBusy;
                if (seen != 0)
                {
                    _checkinSeenWhileBusy = 0;
                    _dispatcher.BeginInvoke(new Action(() => FireAndForget(() => OnTargetCheckedInAsync(seen))));
                }

                // Regole di merge salvate mentre la scheda lavorava: si applicano ora (se si puo').
                if (_policyChangedWhileBusy)
                {
                    _policyChangedWhileBusy = false;
                    _dispatcher.BeginInvoke(new Action(() => FireAndForget(OnPolicyChangedCoreAsync)));
                }
            }
        }

        // async void di comodo per partire dai setter e dalle callback: nessuna eccezione esce.
        private async void FireAndForget(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                try
                {
                    Log("ERROR: " + ex.Message);
                    _logger.Error("Merge from Task: unexpected error", ex);
                }
                catch (Exception)
                {
                    // Il registro stesso ha fallito: niente altro da fare in un async void.
                }
            }
        }

        private void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message;
            _log.AppendLine(line);
            if (!_deferLogText)
                LogText = _log.ToString();
            _logger.Info("[Merge from Task] " + message);
        }

        private void FlushLog()
        {
            LogText = _log.ToString();
        }

        // Messaggio di errore "senza eccezione" (input non valido, connessione assente): banner rosso.
        private void ShowError(string message)
        {
            StatusMessage = message;
            _hasError = true;
            OnPropertyChanged("StatusKind");
        }

        // Avanzamento di un lavoro in background (thread qualsiasi) nel banner: riportato sul thread UI
        // e ignorato se nel frattempo il lavoro e' finito (EndBackgroundProgress).
        private Action<string> CreateBackgroundProgress()
        {
            var token = ++_progressSeq;
            return text => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (token == _progressSeq && IsBusy)
                    StatusMessage = text;
            }));
        }

        private void EndBackgroundProgress()
        {
            _progressSeq++;
        }

        private void UpdateProgress()
        {
            var total = Steps.Count;
            var done = Steps.Count(s => s.IsDone);
            ProgressMaximum = total == 0 ? 1 : total;
            ProgressValue = done;
            DoneStepCount = done;
            RemainingStepCount = total - done;

            if (total == 0)
            {
                ProgressText = string.Empty;
            }
            else
            {
                var skipped = Steps.Count(s => s.Status == TaskMergeStepStatus.Skipped);
                var text = skipped > 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0}/{1} steps done ({2} skipped)", done, total, skipped)
                    : string.Format(CultureInfo.InvariantCulture, "{0}/{1} steps merged", done, total);
                var parts = PartCount;
                if (parts > 1)
                    text += string.Format(CultureInfo.InvariantCulture, " · part {0} of {1} · {2}",
                        CurrentPartForDisplay(), parts, TaskMergeText.Count(parts - 1, "check-in point"));
                ProgressText = text;
            }

            UpdateMarkers();
            UpdateLocks();
            UpdatePolicyEditability();
            OnPropertyChanged("HasSteps");
        }

        // A1: i passi delle parti che vengono dopo il prossimo check-in sono visibili ma grigi (lista e
        // barra), con la dicitura "Available after the check-in of part N".
        private void UpdateLocks()
        {
            var current = PartCount > 1 ? CurrentPartForDisplay() : int.MaxValue;
            foreach (var part in _partsByNumber.Values)
                part.IsLocked = part.Number > current;
            foreach (var step in Steps)
                step.IsLocked = step.Part > current;
        }

        // Parte "corrente" per il testo di avanzamento: quella del punto di check-in su cui si e'
        // fermi, altrimenti la prima con passi da fare (l'ultima se e' tutto fatto).
        private int CurrentPartForDisplay()
        {
            if (State == TaskMergeChainState.StoppedCheckpoint && _checkpointPart > 0)
                return _checkpointPart;
            var part = FirstPartWithWork();
            return part == 0 ? PartCount : part;
        }

        private void UpdateMarkers()
        {
            foreach (var marker in _markers)
            {
                var partSteps = Steps.Where(s => s.Part == marker.PartNumber).ToList();
                if (State == TaskMergeChainState.StoppedCheckpoint && _checkpointDynamicReason == null && _checkpointPart == marker.PartNumber)
                    marker.State = TaskMergeCheckpointState.Active;
                else if (partSteps.All(s => s.IsDone && !s.MergedInSession))
                    marker.State = TaskMergeCheckpointState.Passed;
                else
                    marker.State = TaskMergeCheckpointState.Upcoming;
            }
        }

        private static string FormatStatus(GetStatus status)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "NumOperations={0}, NumConflicts={1}, NumFailures={2}, NumWarnings={3}",
                status.NumOperations, status.NumConflicts, status.NumFailures, status.NumWarnings);
        }

        private void LogFailures(string prefix, GetStatus status)
        {
            if (status == null || status.NumFailures == 0)
                return;

            foreach (var failure in status.GetFailures().Take(20))
                Log(string.Format(CultureInfo.InvariantCulture, "{0} failure {1}: {2} ({3})",
                    prefix, failure.Code, failure.Message, failure.ServerItem ?? failure.LocalItem));
        }

        private static string FirstFailureMessage(GetStatus status)
        {
            var failure = status.GetFailures().FirstOrDefault();
            return failure == null ? null : failure.Message;
        }

        private static string FailureSummary(GetStatus status)
        {
            var first = FirstFailureMessage(status);
            return status.NumFailures == 1
                ? first ?? "1 failure"
                : string.Format(CultureInfo.InvariantCulture, "{0} failures: {1}", status.NumFailures, first);
        }

        // Path TFVC: maiuscole/minuscole non contano.
        private static bool PathEquals(string a, string b)
        {
            if (a == null || b == null)
                return false;
            return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        // item e' strettamente dentro folder.
        private static bool IsUnder(string item, string folder)
        {
            if (string.IsNullOrEmpty(item) || string.IsNullOrEmpty(folder))
                return false;
            var f = folder.TrimEnd('/');
            return item.Length > f.Length + 1
                && item.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                && item[f.Length] == '/';
        }

        private static bool IsSameOrUnder(string item, string folder)
        {
            return PathEquals(item, folder) || IsUnder(item, folder);
        }

        private static string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            var trimmed = path.TrimEnd('/');
            var index = trimmed.LastIndexOf('/');
            return index <= 1 ? "$/" : trimmed.Substring(0, index);
        }

        // Cartella comune (per segmenti, case-insensitive) di un insieme di path server.
        private static string CommonFolder(IEnumerable<string> paths)
        {
            string[] common = null;
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;
                var segments = path.TrimEnd('/').Split('/');
                if (common == null)
                {
                    common = segments;
                    continue;
                }
                var n = 0;
                while (n < common.Length && n < segments.Length && string.Equals(common[n], segments[n], StringComparison.OrdinalIgnoreCase))
                    n++;
                common = common.Take(n).ToArray();
            }
            if (common == null || common.Length == 0)
                return null;
            var result = string.Join("/", common);
            return result == "$" ? "$/" : result;
        }

        #endregion

        #region Team Explorer entry point

        // Chiamato (thread UI) dal pulsante della sezione "Merge From Task" di Team Explorer, dopo aver
        // portato in primo piano la scheda. Una catena in corso non viene mai scartata da qui, e le sue
        // caselle non si toccano: la richiesta aspetta nel suo riquadro (Load / Discard and load).
        public void OpenFromTeamExplorer(string workItemIdText, string targetBranchText)
        {
            var idText = (workItemIdText ?? string.Empty).Trim();
            var targetText = (targetBranchText ?? string.Empty).Trim();

            if (IsChainInProgress && IsCurrentPlan(idText, targetText))
            {
                // Stessa catena: la scheda mostra gia' il suo stato. Si riallineano solo le caselle,
                // se nel frattempo erano state modificate (niente avviso "input cambiato").
                ClearTeamExplorerRequest();
                if (HasStaleInput)
                {
                    WorkItemIdText = idText;
                    TargetBranchText = targetText;
                }
                return;
            }

            if (!CanAct())
            {
                // Operazione in corso (anche l'avvio di una catena): le caselle restano quelle del piano
                // che sta lavorando, altrimenti la catena appena partita non si potrebbe piu' continuare.
                SetTeamExplorerRequest(idText, targetText);
                Log(string.Format(CultureInfo.InvariantCulture,
                    "Team Explorer asked for work item {0}, target {1}: another operation is running, the request waits in the tab.",
                    idText, targetText));
                return;
            }

            if (IsChainInProgress)
            {
                // Una catena completata e gia' archiviata si chiude e la richiesta si carica subito;
                // altrimenti la richiesta resta in attesa.
                SetTeamExplorerRequest(idText, targetText);
                FireAndForget(() => RunBusyAsync(OpenRequestDuringChainCoreAsync));
                return;
            }

            ClearTeamExplorerRequest();
            WorkItemIdText = idText;
            TargetBranchText = targetText;
            FireAndForget(() => RunBusyAsync(LoadCoreAsync));
        }

        private bool IsCurrentPlan(string idText, string targetText)
        {
            int id;
            return int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                && id == _workItemId
                && string.Equals((targetText ?? string.Empty).Trim().TrimEnd('/'), _targetPath, StringComparison.OrdinalIgnoreCase);
        }

        private void SetTeamExplorerRequest(string idText, string targetText)
        {
            _requestedWorkItemText = idText ?? string.Empty;
            _requestedTargetText = targetText ?? string.Empty;
            RaiseRequestProperties();
        }

        private void ClearTeamExplorerRequest()
        {
            if (_requestedWorkItemText == null && _requestedTargetText == null)
                return;
            _requestedWorkItemText = null;
            _requestedTargetText = null;
            RaiseRequestProperties();
        }

        private async Task OpenRequestDuringChainCoreAsync()
        {
            if (await CloseChainIfCheckedInAsync())
            {
                await LoadRequestCoreAsync();
                return;
            }

            Log(string.Format(CultureInfo.InvariantCulture,
                "Team Explorer asked for work item {0}, target {1}: not loaded yet, the chain of work item {2} is not finished ({3}).",
                _requestedWorkItemText, _requestedTargetText, _workItemId, DescribeChainState()));
        }

        // Pulsante del riquadro della richiesta: scarta la catena (con conferma, se non e' finita e
        // archiviata) e carica il work item chiesto da Team Explorer.
        private async Task LoadRequestCoreAsync()
        {
            var idText = _requestedWorkItemText;
            var targetText = _requestedTargetText;
            if (idText == null)
                return;

            // "No": la catena resta com'e', caselle comprese.
            if (!await ConfirmDiscardChainAsync(idText))
                return;

            ClearTeamExplorerRequest();
            WorkItemIdText = idText;
            TargetBranchText = targetText;
            await LoadCoreAsync(true);
        }

        private void RestoreInput()
        {
            if (_plannedGroup == null)
                return;
            WorkItemIdText = _workItemId.ToString(CultureInfo.InvariantCulture);
            TargetBranchText = _targetPath;
        }

        // True se la catena si puo' scartare: non ce n'e' una in corso, e' completata e archiviata, oppure
        // l'utente conferma (i merge gia' fatti restano in sospeso nel workspace). Thread UI.
        private async Task<bool> ConfirmDiscardChainAsync(string newWorkItemText)
        {
            if (!IsChainInProgress)
                return true;
            if (await CloseChainIfCheckedInAsync())
                return true;

            var message = string.Format(CultureInfo.InvariantCulture,
                "The merge chain of work item {0} is not finished ({1}).\n\nDiscard it and load work item {2}?\n\nWhat was already merged and not checked in stays as pending changes in workspace '{3}': check it in or undo it from Pending Changes.",
                _workItemId, DescribeChainState(), newWorkItemText, _chainWorkspace == null ? "?" : _chainWorkspace.Name);
            var answer = Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                _serviceProvider ?? Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                message,
                "Merge from Task",
                Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            if (answer == IdYes)
                return true;

            Log(string.Format(CultureInfo.InvariantCulture, "Kept the chain of work item {0}: nothing was discarded.", _workItemId));
            return false;
        }

        // Valore di ritorno "Si'" di ShowMessageBox (IDYES).
        private const int IdYes = 6;

        // Una catena completata i cui merge non sono piu' in sospeso sotto il target (check-in fatto, o
        // undo) non ha piu' niente da finire: smette di essere "in corso", cosi' Team Explorer e Load
        // possono caricare un altro task senza avvisi. A meta' catena: sempre false.
        private async Task<bool> CloseChainIfCheckedInAsync()
        {
            if (!IsChainInProgress)
                return true;
            if (State != TaskMergeChainState.Completed)
                return false;

            var workspace = _chainWorkspace;
            var target = _targetMergePath;
            if (workspace == null || string.IsNullOrEmpty(target))
                return false;

            var hasPendingMerges = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).Any(p => p.IsMerge));
            if (hasPendingMerges)
                return false;

            CloseChain(string.Format(CultureInfo.InvariantCulture,
                "The chain of work item {0} is closed: no merge is pending under {1} any more (checked in).", _workItemId, target));
            return true;
        }

        // Chiude la catena: niente piu' in sospeso da seguire, niente attesa di check-in.
        private void CloseChain(string message)
        {
            foreach (var step in Steps)
                step.MergedInSession = false;
            _chainActive = false;
            _checkpointPart = 0;
            _checkpointDynamicReason = null;
            UnsubscribeCommitCheckin();
            _reviewWaiting = false;
            if (!string.IsNullOrEmpty(message))
                Log(message);
            UpdateProgress();
            RaiseStateDependentProperties();
        }

        private string DescribeChainState()
        {
            switch (State)
            {
                case TaskMergeChainState.StoppedConflicts:
                    return "stopped at conflicts";
                case TaskMergeChainState.StoppedFailures:
                    return "stopped by a failure";
                case TaskMergeChainState.StoppedNeedsCheckIn:
                    return "waiting for a check-in";
                case TaskMergeChainState.StoppedPendingChanges:
                    return "stopped on pending changes";
                case TaskMergeChainState.StoppedCheckpoint:
                    return PartCount > 1 && _checkpointDynamicReason == null
                        ? string.Format(CultureInfo.InvariantCulture, "waiting for the check-in of part {0} of {1}", _checkpointPart, PartCount)
                        : "waiting for a check-in";
                case TaskMergeChainState.Completed:
                    return "all steps merged, not checked in yet";
                default:
                    return "in progress";
            }
        }

        #endregion

        #region Load / plan / preview

        private Task LoadCoreAsync()
        {
            return LoadCoreAsync(false);
        }

        // discardConfirmed: chi chiama ha gia' chiesto (e ottenuto) di scartare la catena in corso.
        // Altrimenti una catena non finita si scarta solo dopo la conferma dell'utente: Load (o Invio in
        // una casella) non butta mai via il lavoro in silenzio.
        private async Task LoadCoreAsync(bool discardConfirmed)
        {
            int workItemId;
            if (!int.TryParse(WorkItemIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out workItemId) || workItemId <= 0)
            {
                ShowError("Invalid work item id.");
                return;
            }

            var target = (TargetBranchText ?? string.Empty).Trim().TrimEnd('/');
            if (target.IndexOf(',') >= 0)
            {
                ShowError("One target branch at a time.");
                return;
            }
            if (!target.StartsWith("$/", StringComparison.Ordinal))
            {
                ShowError("The target branch must be a server path (e.g. $/Project/Release).");
                return;
            }

            if (!discardConfirmed && !await ConfirmDiscardChainAsync(workItemId.ToString(CultureInfo.InvariantCulture)))
                return;

            if (!await EnsureConnectionAsync())
                return;

            if (IsChainInProgress)
                Log(string.Format(CultureInfo.InvariantCulture,
                    "WARNING: the previous chain (work item {0}) is discarded; its pending changes, if any, are still in workspace '{1}'.",
                    _workItemId, _chainWorkspace == null ? "?" : _chainWorkspace.Name));

            // Un Load esplicito supera qualsiasi richiesta di Team Explorer in attesa.
            ClearTeamExplorerRequest();
            ResetPlan();
            WorkItemTitle = null;
            _suppressSelectionHandlers = true;
            try
            {
                Groups.Clear();
                Workspaces.Clear();
                SelectedGroup = null;
                SelectedWorkspace = null;
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }
            OnPropertyChanged("HasMultipleGroups");
            State = TaskMergeChainState.Idle;

            StatusMessage = "Loading work item " + workItemId + "...";
            Log(string.Format(CultureInfo.InvariantCulture, "Load: work item {0}, target {1}", workItemId, target));

            var tfs = _tfs;
            var versionControl = _versionControl;
            var workItemStore = _workItemStore;
            var load = await Task.Run(() =>
            {
                var changesetIds = new WorkItemChangesetService(workItemStore).GetLinkedChangesetIds(workItemId);
                var title = workItemStore.GetWorkItem(workItemId).Title;
                var grouping = changesetIds.Count == 0
                    ? null
                    : new TaskChangesetGroupingService(new ChangesetService(versionControl)).GroupByBranch(workItemId, changesetIds);
                var workspaces = versionControl.QueryWorkspaces(null, tfs.AuthorizedIdentity.UniqueName, Environment.MachineName)
                    .OrderBy(w => w.Name)
                    .ToList();
                var mappingWorkspace = workspaces.FirstOrDefault(w => w.IsServerPathMapped(target));
                return new LoadResult
                {
                    ChangesetIds = changesetIds,
                    Title = title,
                    Grouping = grouping,
                    Workspaces = workspaces,
                    MappingWorkspace = mappingWorkspace
                };
            });

            _workItemId = workItemId;
            WorkItemTitle = load.Title;
            _targetPath = target;

            Log(string.Format(CultureInfo.InvariantCulture, "Work item {0} \"{1}\": {2} linked changeset(s): {3}",
                workItemId, load.Title, load.ChangesetIds.Count, string.Join(", ", load.ChangesetIds)));

            if (load.ChangesetIds.Count == 0 || load.Grouping == null)
            {
                StatusMessage = string.Format(CultureInfo.InvariantCulture, "Work item {0} has no linked changesets.", workItemId);
                return;
            }

            if (load.Grouping.UnresolvedChangesetIds.Count > 0)
                Log(string.Format(CultureInfo.InvariantCulture, "{0} changeset(s) skipped (ambiguous or unknown branch): {1}",
                    load.Grouping.UnresolvedChangesetIds.Count, string.Join(", ", load.Grouping.UnresolvedChangesetIds)));

            _suppressSelectionHandlers = true;
            try
            {
                foreach (var group in load.Grouping.Groups)
                {
                    group.TargetBranchPaths = new List<string> { target };
                    Groups.Add(group);
                    Log(string.Format(CultureInfo.InvariantCulture, "Group: source branch {0}, {1} changeset(s)",
                        group.SourceBranch, group.ChangesetIds.Count));
                }
                foreach (var workspace in load.Workspaces)
                    Workspaces.Add(workspace);

                SelectedWorkspace = load.MappingWorkspace ?? Workspaces.FirstOrDefault();
                SelectedGroup = Groups.FirstOrDefault();
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }
            OnPropertyChanged("HasMultipleGroups");

            if (Groups.Count == 0)
            {
                StatusMessage = "No usable changeset found for this task.";
                return;
            }

            if (Workspaces.Count == 0)
            {
                ShowError("No workspace found for this user on this machine.");
                return;
            }

            if (load.MappingWorkspace == null)
                Log("No workspace maps the target " + target + ".");
            else
                Log("Workspace: " + load.MappingWorkspace.Name + " (maps the target)");

            await PlanAndPreviewCoreAsync();
        }

        private sealed class LoadResult
        {
            public IReadOnlyList<int> ChangesetIds { get; set; }
            public string Title { get; set; }
            public TaskChangesetGroupingResult Grouping { get; set; }
            public List<Workspace> Workspaces { get; set; }
            public Workspace MappingWorkspace { get; set; }
        }

        private sealed class PlanResult
        {
            public TaskMergePlan Plan { get; set; }
            public string TopFolder { get; set; }
            public IReadOnlyList<TaskMergeColleagueWarning> Colleagues { get; set; }
            // Regole di merge del target (null se non lette: PolicyError dice perche').
            public EffectiveMergePolicy Policy { get; set; }
            public string PolicyDescription { get; set; }
            public string PolicyError { get; set; }
        }

        // Legge la connessione TFS dal contesto corrente di Team Explorer (thread UI); i servizi
        // (VersionControlServer, WorkItemStore) si ottengono fuori dal thread UI, perche' la prima
        // GetService<WorkItemStore> scarica i metadati dal server e puo' durare secondi.
        private async Task<bool> EnsureConnectionAsync()
        {
            var contextManager = _serviceProvider == null
                ? null
                : _serviceProvider.GetService(typeof(ITeamFoundationContextManager)) as ITeamFoundationContextManager;
            var context = contextManager == null ? null : contextManager.CurrentContext;
            var tfs = context == null ? null : context.TeamProjectCollection;
            if (tfs == null)
            {
                ShowError("Not connected to a Team Foundation Server collection: connect in Team Explorer first.");
                return false;
            }

            if (_tfs == null || _tfs.Uri != tfs.Uri)
            {
                StatusMessage = "Connecting to " + tfs.Uri + "...";
                var services = await Task.Run(() => Tuple.Create(
                    tfs.GetService<VersionControlServer>(),
                    tfs.GetService<WorkItemStore>()));
                // L'attesa del check-in e' sull'evento della connessione vecchia.
                UnsubscribeCommitCheckin();
                _tfs = tfs;
                _versionControl = services.Item1;
                _workItemStore = services.Item2;
            }
            return true;
        }

        // Tutto il piano, compresi i dati letti da TFVC e le caselle dei changeset (Load, altro gruppo).
        private void ResetPlan()
        {
            _plannedGroup = null;
            _planData = null;
            TaskChangesets.Clear();
            ResetSteps();
        }

        // Il piano calcolato (passi, parti, avvisi, catena), ma non i dati del gruppo ne' le caselle:
        // serve al ricalcolo dopo un cambio delle caselle.
        private void ResetSteps()
        {
            UnsubscribeCommitCheckin();
            _reviewWaiting = false;
            _checkinSeenWhileBusy = 0;
            _previewCompleted = false;
            _plan = null;
            _plannedSelection = null;
            _dependencyWarnings = new List<ChangesetDependencyWarning>();
            _colleagueWarnings = new List<TaskMergeColleagueWarning>();
            _sourceTopFolder = null;
            _targetMergePath = null;
            _chainWorkspace = null;
            _chainActive = false;
            _checkpointPart = 0;
            _checkpointDynamicReason = null;
            SourceTopFolder = null;
            TargetFolder = null;
            PlanSummaryText = null;
            _partsByNumber.Clear();
            _markers.Clear();
            BarItems.Clear();
            Steps.Clear();
            PlanErrors.Clear();
            PlanWarnings.Clear();
            SafetyWarnings.Clear();
            AuditNotices.Clear();
            // Regole di merge: si rileggono con il piano (BuildPlanAndPreviewAsync).
            _policy = null;
            _policyLoadErrors.Clear();
            _stepViewModels.Clear();
            _lineRulesByTarget.Clear();
            _discardSnapshots.Clear();
            PolicyErrors.Clear();
            FollowUps.Clear();
            PolicySummaryText = null;
            PolicyDescriptionText = null;
            PolicyNoticeText = null;
            foreach (var row in TaskChangesets)
                row.SetWarning(null, false);
            ClearConflicts();
            RaisePlanProperties();
            UpdateProgress();
        }

        // Gruppo selezionato (Load o cambio di source branch): dati da TFVC (TaskMergePlanDataSource),
        // caselle dei changeset (tutte selezionate), poi piano e anteprima. Il lavoro TFS va in background.
        private async Task PlanAndPreviewCoreAsync()
        {
            var group = SelectedGroup;
            if (group == null || _versionControl == null)
                return;

            if (IsChainInProgress)
            {
                StatusMessage = "A chain is in progress: the source branch cannot change now.";
                return;
            }

            ResetPlan();
            State = TaskMergeChainState.Idle;
            StatusMessage = "Reading the task changesets from TFVC...";
            Log(string.Format(CultureInfo.InvariantCulture, "Reading: source branch {0}, target {1}, changesets {2}",
                group.SourceBranch, _targetPath, string.Join(", ", group.ChangesetIds)));

            var versionControl = _versionControl;
            var targetRoot = _targetPath;
            var progress = CreateBackgroundProgress();
            TaskMergePlanData data;
            try
            {
                data = await Task.Run(() => TaskMergePlanDataSource.GatherData(versionControl, group, targetRoot, progress));
            }
            finally
            {
                EndBackgroundProgress();
            }

            if (!ReferenceEquals(group, SelectedGroup))
                return;

            _planData = data;
            _plannedGroup = group;
            foreach (var info in data.Changesets)
                TaskChangesets.Add(new TaskMergeChangesetViewModel(info, OnChangesetSelectionChanged));
            RaiseSelectionProperties();

            await BuildPlanAndPreviewAsync();
        }

        // Caselle cambiate: nuovo piano con i soli changeset selezionati e nuova anteprima, sugli stessi
        // dati del gruppo (nessuna nuova lettura dei changeset).
        private async Task ReplanCoreAsync()
        {
            if (_planData == null || _plannedGroup == null)
                return;

            if (IsChainInProgress)
            {
                StatusMessage = "A chain is in progress: the changeset selection cannot change now.";
                return;
            }

            ResetSteps();
            State = TaskMergeChainState.Idle;
            await BuildPlanAndPreviewAsync();
        }

        // Le caselle cambiano subito, ma il piano si ricalcola solo con Update plan.
        // HasStaleSelection impedisce Start mentre il piano appartiene alla scelta precedente.
        private void OnChangesetSelectionChanged(TaskMergeChangesetViewModel row)
        {
            RaiseSelectionProperties();
        }

        public void RaiseChangesetRowSelectionCommands()
        {
            IncludeChangesetsCommand.RaiseCanExecuteChanged();
            ExcludeChangesetsCommand.RaiseCanExecuteChanged();
        }

        private bool CanSetChangesetsIncluded(IList rows, bool included)
        {
            return CanChangeSelection && rows != null && rows.OfType<TaskMergeChangesetViewModel>()
                .Any(row => TaskChangesets.Contains(row) && row.IsSelected != included);
        }

        private void SetChangesetsIncluded(IList rows, bool included)
        {
            if (!CanSetChangesetsIncluded(rows, included))
                return;
            // Snapshot: SelectedItems appartiene alla vista e puo' cambiare durante le notifiche.
            foreach (var row in rows.OfType<TaskMergeChangesetViewModel>()
                .Where(TaskChangesets.Contains).ToList())
                row.SetSelectedSilently(included);
            RaiseSelectionProperties();
        }

        private void SelectAllChangesets()
        {
            if (!SelectAllChangesetsCanExecute())
                return;
            foreach (var row in TaskChangesets)
                row.SetSelectedSilently(true);
            RaiseSelectionProperties();
        }

        // Piano con i changeset selezionati (planner puro), changeset di colleghi non fusi nel target,
        // avvisi di dipendenza, poi anteprima di tutti i passi.
        private async Task BuildPlanAndPreviewAsync()
        {
            var group = _plannedGroup;
            var data = _planData;
            if (group == null || data == null)
                return;

            var selection = TaskChangesets.Where(c => c.IsSelected).Select(c => c.ChangesetId).OrderBy(id => id).ToList();
            StatusMessage = "Planning the merge file by file...";
            Log(string.Format(CultureInfo.InvariantCulture, "Plan: {0} of {1} task changesets selected: {2}",
                selection.Count, data.AllChangesetIds.Count, TaskMergeText.Changesets(selection)));
            var excluded = data.AllChangesetIds.Where(id => !selection.Contains(id)).ToList();
            if (excluded.Count > 0)
                Log("Excluded by the user (treated as other people's changesets): " + TaskMergeText.Changesets(excluded));

            var progress = CreateBackgroundProgress();
            var versionControl = _versionControl;
            var targetRoot = _targetPath;
            PlanResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    var plan = TaskMergePlanner.Build(data.CreateInput(selection));
                    if (plan.IsValid)
                        progress("Checking whether colleagues' changesets in between are in the target...");
                    var colleagues = data.FindColleagueChangesetsNotInTarget(plan);
                    var planResult = new PlanResult { Plan = plan, TopFolder = CalculateTopFolder(group), Colleagues = colleagues };

                    // Regole di merge: file di team nella radice del target (dal server, a Latest) e file
                    // personale; il personale vince sempre. Non leggibili = problema bloccante.
                    progress("Reading the merge policies...");
                    try
                    {
                        string description;
                        planResult.Policy = MergePolicyStore.LoadEffective(versionControl, targetRoot, out description);
                        planResult.PolicyDescription = description;
                        if (planResult.Policy == null)
                            planResult.PolicyError = string.IsNullOrEmpty(description) ? "the merge policies could not be read" : description;
                    }
                    catch (Exception ex)
                    {
                        planResult.Policy = null;
                        planResult.PolicyError = ex.Message;
                    }
                    return planResult;
                });
            }
            finally
            {
                EndBackgroundProgress();
            }

            if (!ReferenceEquals(data, _planData) || !ReferenceEquals(group, _plannedGroup))
                return;

            _plan = result.Plan;
            _plannedSelection = selection.AsReadOnly();
            _dependencyWarnings = TaskMergeDependencyAnalyzer.Analyze(data.AllChanges, selection);
            _colleagueWarnings = result.Colleagues ?? new List<TaskMergeColleagueWarning>();
            TaskMergeChangesetViewModel.ApplyWarnings(TaskChangesets, _dependencyWarnings);
            _sourceTopFolder = result.TopFolder ?? group.SourceBranch;
            _targetMergePath = TaskTargetPathMapper.MapToTarget(group.SourceBranch, _sourceTopFolder, _targetPath);

            // Difesa: get latest, conflitti e pending lavorano sotto _targetMergePath, quindi deve
            // contenere ogni item del piano. Se il top folder non basta si usa la cartella comune.
            var outside = _plan.Steps.Where(s => !IsSameOrUnder(s.TargetItem, _targetMergePath)).ToList();
            if (outside.Count > 0)
            {
                var targetFolder = CommonFolder(_plan.Steps.Select(s => ParentOf(s.TargetItem))) ?? _targetPath;
                var sourceFolder = CommonFolder(_plan.Steps.Select(s => ParentOf(s.SourceItem))) ?? group.SourceBranch;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "WARNING: {0} item(s) of the plan are outside {1} (e.g. {2}): the chain works on {3} instead.",
                    outside.Count, _targetMergePath, outside[0].TargetItem, targetFolder));
                _targetMergePath = targetFolder;
                _sourceTopFolder = sourceFolder;
            }

            SourceTopFolder = _sourceTopFolder;
            TargetFolder = _targetMergePath;

            BuildStepViewModels(_plan);
            ApplyPolicy(result.Policy, result.PolicyDescription, result.PolicyError);
            LogPlan(_plan);
            UpdateProgress();
            RaiseStateDependentProperties();

            if (!_plan.IsValid)
            {
                ShowError(string.Format(CultureInfo.InvariantCulture,
                    "This task cannot be merged file by file ({0}): see the list below. Start is disabled.",
                    TaskMergeText.Count(_plan.Errors.Count, "problem")));
                return;
            }

            if (Steps.Count == 0)
            {
                StatusMessage = "Nothing to merge: the plan has no steps.";
                return;
            }

            await PreviewCoreAsync();
        }

        // Top folder del gruppo (come il flusso Task): cartella del target su cui lavora la catena.
        private static string CalculateTopFolder(TaskChangesetGroup group)
        {
            var changes = group.ChangesByChangesetId;
            if (changes == null)
                return null;
            try
            {
                return BranchesViewModel.CalculateGroupTopFolder(group.ChangesetIds
                    .Where(id => changes.ContainsKey(id) && changes[id] != null && changes[id].Length > 0)
                    .Select(id => changes[id]));
            }
            catch (Exception)
            {
                // La cartella comune degli item del piano (PlanAndPreviewCoreAsync) fa da ripiego.
                return null;
            }
        }

        private void BuildStepViewModels(TaskMergePlan plan)
        {
            _partsByNumber.Clear();
            _markers.Clear();
            _stepViewModels.Clear();
            BarItems.Clear();
            Steps.Clear();
            PlanErrors.Clear();
            PlanWarnings.Clear();

            TaskMergePart previous = null;
            foreach (var part in plan.Parts)
            {
                var partInfo = new TaskMergePartViewModel(part, plan.Parts.Count, previous);
                _partsByNumber[part.Number] = partInfo;
                foreach (var step in part.Steps)
                {
                    var row = new TaskMergeStepViewModel(step, partInfo) { PolicyActionChanged = OnStepPolicyActionChanged };
                    _stepViewModels[step] = row;
                    Steps.Add(row);
                    BarItems.Add(row);
                }
                if (part.Number < plan.Parts.Count)
                {
                    var marker = new TaskMergeCheckpointMarkerViewModel(part, plan.Parts.Count);
                    _markers.Add(marker);
                    BarItems.Add(marker);
                }
                previous = part;
            }

            foreach (var error in plan.Errors)
                PlanErrors.Add(error);
            foreach (var warning in plan.Warnings)
                PlanWarnings.Add(warning);

            // Avvisi di sicurezza: accanto ai changeset (caselle), sugli item (lista) e qui.
            SafetyWarnings.Clear();
            if (plan.IsValid)
            {
                foreach (var colleague in _colleagueWarnings)
                {
                    SafetyWarnings.Add(colleague.Message);
                    foreach (var step in Steps.Where(s => PathEquals(s.SourceItem, colleague.SourceItem)))
                        step.ColleaguesNotInTarget = colleague.NotInTarget;
                }
            }
            foreach (var dependency in _dependencyWarnings)
                SafetyWarnings.Add(dependency.Message);

            RaisePlanProperties();
        }

        // Avvisi che chiedono la conferma esplicita a Start (vuoto se nessuno).
        private List<string> StartWarnings()
        {
            var warnings = new List<string>();
            if (_plan == null || !_plan.IsValid)
                return warnings;
            warnings.AddRange(_dependencyWarnings.Select(w => w.Message));
            warnings.AddRange(_colleagueWarnings.Select(w => w.Message));
            return warnings;
        }

        private void LogPlan(TaskMergePlan plan)
        {
            _deferLogText = true;
            try
            {
                Log(string.Format(CultureInfo.InvariantCulture, "Plan: {0} in {1}, {2}; {3} -> {4}",
                    TaskMergeText.Count(plan.Steps.Count, "step"), TaskMergeText.Count(plan.Parts.Count, "part"),
                    TaskMergeText.Count(plan.CheckpointCount, "check-in point"), _sourceTopFolder, _targetMergePath));
                foreach (var error in plan.Errors)
                    Log("PLAN ERROR: " + error);
                foreach (var warning in plan.Warnings)
                    Log("Plan warning: " + warning);
                foreach (var warning in SafetyWarnings)
                    Log("WARNING: " + warning);
                foreach (var part in plan.Parts)
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "Part {0} of {1}: {2} ({3}), {4}{5}",
                        part.Number, plan.Parts.Count, TaskMergeText.Range(part.FromChangesetId, part.ToChangesetId),
                        TaskMergeText.Count(part.TaskChangesetIds.Count, "task changeset"), TaskMergeText.Count(part.Steps.Count, "step"),
                        string.IsNullOrEmpty(part.EndReason) ? string.Empty : ". " + part.EndReason));
                    foreach (var step in part.Steps)
                        Log(string.Format(CultureInfo.InvariantCulture, "  #{0} {1} {2} {3}{4}{5}{6}",
                            step.Number, TaskMergeText.ChangeKind(step.ChangeKind), step.RelativePath,
                            TaskMergeText.Range(step.FromChangesetId, step.ToChangesetId),
                            step.Recursion == TaskMergeStepRecursion.Full ? " (recursive)" : string.Empty,
                            step.TargetExists ? string.Empty : " (new in target)",
                            step.InterleavedThirdParty != null && step.InterleavedThirdParty.Count > 0
                                ? " interleaved with " + TaskMergeText.Changesets(step.InterleavedThirdParty)
                                : string.Empty));
                }
                LogPolicy();
            }
            finally
            {
                _deferLogText = false;
                FlushLog();
            }
        }

        // Regole di merge del piano nel registro: da dove vengono, problemi, passi con un'azione diversa
        // da Merge o con righe protette.
        private void LogPolicy()
        {
            Log("Merge policy: " + (string.IsNullOrEmpty(PolicyDescriptionText) ? "no rules" : PolicyDescriptionText));
            foreach (var error in PolicyErrors)
                Log("POLICY ERROR: " + error);
            foreach (var step in Steps.Where(s => s.PolicyAction != MergePolicyAction.Merge || s.HasProtectedLines))
            {
                var rule = step.PolicyRule == null || step.PolicyRule.Rule == null ? null : step.PolicyRule.Rule.Id;
                Log(string.Format(CultureInfo.InvariantCulture, "  policy #{0} {1}: {2}{3}{4}{5}",
                    step.Number, step.RelativePath, step.PolicyAction,
                    rule == null ? string.Empty : " (rule " + rule + ")",
                    step.IsPolicyOverridden ? " (changed in the tab)" : string.Empty,
                    step.HasProtectedLines
                        ? "; protected lines: " + string.Join(", ", step.ProtectedLineRules.Select(r => r.Id))
                        : string.Empty));
            }
        }

        private async Task PreviewCoreAsync()
        {
            if (_plan == null || !_plan.IsValid || Steps.Count == 0)
                return;

            if (IsChainInProgress)
            {
                StatusMessage = "A chain is in progress: the workspace cannot change now.";
                return;
            }

            var workspace = SelectedWorkspace;
            if (!await PreviewAllStepsAsync(workspace))
                return;

            var blocked = Steps.Where(s => s.Status == TaskMergeStepStatus.Blocked).ToList();
            if (blocked.Count > 0)
            {
                State = TaskMergeChainState.Ready;
                var target = _targetMergePath;
                var pendingCount = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).Count());
                var message = string.Format(CultureInfo.InvariantCulture,
                    "TFVC refuses {0} (e.g. step #{1} {2}: {3}). Start is disabled: fix the cause, then press Load again.",
                    TaskMergeText.Count(blocked.Count, "step"), blocked[0].Number, blocked[0].RelativePath, blocked[0].Details);
                if (pendingCount > 0)
                    message += string.Format(CultureInfo.InvariantCulture,
                        " The target has {0} pending change(s) in workspace '{1}' (e.g. an earlier merge): check them in or undo them first.",
                        pendingCount, workspace.Name);
                ShowError(message);
                Log(message);
                return;
            }

            State = Steps.All(s => s.IsDone) ? TaskMergeChainState.Completed : TaskMergeChainState.Ready;
            var warnings = StartWarnings().Count;
            if (HasPolicyErrors)
            {
                ShowError(string.Format(CultureInfo.InvariantCulture,
                    "{0}. The merge policies have {1}: Start is disabled until they are fixed (Policies...), see the list below.",
                    PlanSummaryText, TaskMergeText.Count(PolicyErrors.Count, "problem")));
                Log(StatusMessage);
                return;
            }
            if (State == TaskMergeChainState.Completed)
                StatusMessage = Steps.Any(s => s.Status == TaskMergeStepStatus.Skipped)
                    ? "Nothing to merge: every step is already merged or skipped by the merge policies."
                    : "Nothing to merge: TFVC reports every step as already merged.";
            else if (warnings > 0)
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "{0}. {1}: see the list below (Start asks for confirmation).", PlanSummaryText, TaskMergeText.Count(warnings, "warning"));
            else
                StatusMessage = PlanSummaryText + ". Click Start merge.";
            Log(StatusMessage);
        }

        // Anteprima (MergeOptions.NoMerge, nessuna modifica al workspace) di tutti i passi, con
        // avanzamento. Nessuna operazione, nessun conflitto e nessun failure -> "Already merged".
        // I conflitti contano: TFVC puo' dare NumOperations=0 con NumConflicts>0 (es. C162866 su
        // AddInCommonSettings.cs, mai fuso) e quel passo non va saltato.
        // Un'eccezione di TFVC su un passo (VersionControlException, es. NoMergeRelationshipException
        // su un item del target senza legame di merge con la sorgente) blocca QUEL passo (Blocked, con
        // il messaggio) e l'anteprima prosegue con gli altri; altre eccezioni (es. connessione) fermano
        // l'anteprima, che resta incompleta (Start disabilitato finche' non si rifa').
        private async Task<bool> PreviewAllStepsAsync(Workspace workspace)
        {
            _previewCompleted = false;
            if (workspace == null)
            {
                StatusMessage = "Select a workspace.";
                return false;
            }

            var target = _targetMergePath;
            var mapped = await Task.Run(() => workspace.IsServerPathMapped(target));
            if (!mapped)
            {
                foreach (var step in Steps)
                {
                    if (step.PolicyAction == MergePolicyAction.Skip)
                    {
                        MarkSkipped(step);
                        continue;
                    }
                    step.Status = TaskMergeStepStatus.Pending;
                    step.Details = "not previewed";
                }
                UpdateProgress();
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "The target {0} is not mapped in workspace '{1}': choose a workspace that maps it.", target, workspace.Name);
                Log(StatusMessage);
                State = TaskMergeChainState.Idle;
                return false;
            }

            var steps = Steps.ToList();
            _deferLogText = true;
            try
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    // Skip (regola di merge): nessuna chiamata a TFVC.
                    if (step.PolicyAction == MergePolicyAction.Skip)
                    {
                        MarkSkipped(step);
                        UpdateProgress();
                        continue;
                    }
                    StatusMessage = string.Format(CultureInfo.InvariantCulture, "Previewing {0}/{1}...", i + 1, steps.Count);
                    GetStatus status;
                    try
                    {
                        status = await PreviewStepAsync(workspace, step);
                    }
                    catch (Exception ex) when (IsVersionControlException(ex))
                    {
                        LogStepException("Preview", step, ex);
                        step.PreviewConflicts = 0;
                        step.Status = TaskMergeStepStatus.Blocked;
                        step.Details = "TFVC error: " + ex.Message;
                        UpdateProgress();
                        continue;
                    }
                    Log(string.Format(CultureInfo.InvariantCulture, "Preview step #{0} {1} {2}: {3}",
                        step.Number, step.RelativePath, step.RangeText, FormatStatus(status)));
                    LogFailures("Preview step #" + step.Number, status);
                    ApplyPreview(step, status);
                    UpdateProgress();
                    if ((i + 1) % 20 == 0)
                        FlushLog();
                }
            }
            finally
            {
                _deferLogText = false;
                FlushLog();
            }

            UpdatePlanSummary();
            _previewCompleted = true;
            return true;
        }

        // Eccezione di TFVC su un item (VersionControlException e derivate, es.
        // NoMergeRelationshipException, ItemNotFoundException): riguarda quel passo, non la connessione.
        // Riconosciuta per nome del tipo: VersionControlException deriva da un tipo di
        // Microsoft.VisualStudio.Services.Common, assembly che il progetto non referenzia.
        private static bool IsVersionControlException(Exception ex)
        {
            for (var type = ex == null ? null : ex.GetType(); type != null; type = type.BaseType)
            {
                if (string.Equals(type.FullName, "Microsoft.TeamFoundation.VersionControl.Client.VersionControlException", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private void LogStepException(string what, TaskMergeStepViewModel step, Exception ex)
        {
            Log(string.Format(CultureInfo.InvariantCulture, "{0} step #{1} {2} {3}: EXCEPTION {4}: {5}",
                what, step.Number, step.RelativePath, step.RangeText, ex.GetType().Name, ex.Message));
            _logger.Error("Merge from Task: " + what.ToLowerInvariant() + " of step #" + step.Number, ex);
        }

        private void ApplyPreview(TaskMergeStepViewModel step, GetStatus status)
        {
            step.PreviewConflicts = status.NumConflicts;
            if (status.NumFailures > 0)
            {
                if (AreOwnPendingFailures(status))
                {
                    // TF203015 su un item che la catena stessa ha gia' in sospeso: il passo aspetta il
                    // check-in di quanto fuso prima, non e' un blocco.
                    step.Status = TaskMergeStepStatus.Pending;
                    step.Details = "waits for the check-in of the steps merged before it (TF203015)";
                }
                else
                {
                    step.Status = TaskMergeStepStatus.Blocked;
                    step.Details = FailureSummary(status);
                }
            }
            else if (status.NumOperations == 0 && status.NumConflicts == 0)
            {
                step.Status = TaskMergeStepStatus.AlreadyMerged;
                step.Details = "0 operations (preview)";
            }
            else
            {
                step.Status = TaskMergeStepStatus.Pending;
                step.Details = status.NumConflicts > 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0} operations, {1} expected conflict(s) (preview)", status.NumOperations, status.NumConflicts)
                    : string.Format(CultureInfo.InvariantCulture, "{0} operations (preview)", status.NumOperations);
            }
        }

        private void UpdatePlanSummary()
        {
            if (_plan == null || Steps.Count == 0)
            {
                PlanSummaryText = null;
                return;
            }

            // Un Discard non lascia conflitti (tiene il target): non conta.
            var expectedConflicts = Steps.Where(s => !s.IsDone && s.PolicyAction == MergePolicyAction.Merge).Sum(s => s.PreviewConflicts);
            var alreadyMerged = Steps.Count(s => s.Status == TaskMergeStepStatus.AlreadyMerged);
            var blocked = Steps.Count(s => s.Status == TaskMergeStepStatus.Blocked);

            var text = string.Format(CultureInfo.InvariantCulture, "{0} in {1}",
                TaskMergeText.Count(Steps.Count, "step"), TaskMergeText.Count(PartCount, "part"));
            if (_plan.CheckpointCount > 0)
                text += " · " + TaskMergeText.Count(_plan.CheckpointCount, "check-in point");
            text += " · " + TaskMergeText.Count(expectedConflicts, "expected conflict");
            text += string.Format(CultureInfo.InvariantCulture, " · {0} already merged", alreadyMerged);
            if (blocked > 0)
                text += string.Format(CultureInfo.InvariantCulture, " · {0} blocked", blocked);
            var interleaved = Steps.Where(s => s.IsInterleaved).Select(s => s.TargetItem).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (interleaved > 0)
                text += " · " + TaskMergeText.Count(interleaved, "item") + " changed by others in between";
            var skipped = Steps.Count(s => s.Status == TaskMergeStepStatus.Skipped);
            if (skipped > 0)
                text += string.Format(CultureInfo.InvariantCulture, " · {0} skipped by policy", skipped);
            PlanSummaryText = text;
            UpdatePolicySummary();
        }

        private Task<GetStatus> PreviewStepAsync(Workspace workspace, TaskMergeStepViewModel step)
        {
            return MergeStepAsync(workspace, step, MergeOptions.NoMerge);
        }

        // Merge (o anteprima) di un passo: l'item sorgente sull'item target, intervallo del passo,
        // ricorsione del passo. L'overload con MergeOptions aggiunge da solo NoAutoResolve: la
        // risoluzione automatica la fa AutoResolveConflicts dopo il merge.
        private static Task<GetStatus> MergeStepAsync(Workspace workspace, TaskMergeStepViewModel step, MergeOptions options)
        {
            var source = step.SourceItem;
            var target = step.TargetItem;
            var from = step.FromChangesetId;
            var to = step.ToChangesetId;
            var recursion = step.Recursion == TaskMergeStepRecursion.Full ? RecursionType.Full : RecursionType.None;
            return Task.Run(() => workspace.Merge(source, target,
                new ChangesetVersionSpec(from), new ChangesetVersionSpec(to),
                LockLevel.None, recursion, options));
        }

        #endregion

        #region Chain bookkeeping

        // Prima parte con passi non fatti (0 se e' tutto fatto).
        private int FirstPartWithWork()
        {
            return Steps.Where(s => !s.IsDone).Select(s => s.Part).DefaultIfEmpty(0).Min();
        }

        // L'item e' stato messo in sospeso dalla catena in questa sessione: e' il target di un passo
        // fuso (o sta sotto una cartella fusa con ricorsione Full).
        private bool IsItemMergedByChain(string serverItem)
        {
            if (string.IsNullOrEmpty(serverItem))
                return false;
            foreach (var step in Steps)
            {
                if (!step.MergedInSession)
                    continue;
                if (PathEquals(serverItem, step.TargetItem))
                    return true;
                if (step.Recursion == TaskMergeStepRecursion.Full && IsUnder(serverItem, step.TargetItem))
                    return true;
            }
            return false;
        }

        // Pending change creato dalla catena: un merge su un item della catena, oppure una cartella
        // padre che TFVC ha messo in sospeso da solo per un item nuovo nel target.
        private bool IsChainPendingChange(PendingChange change)
        {
            if (change == null)
                return false;
            if (change.IsMerge && IsItemMergedByChain(change.ServerItem))
                return true;
            return change.ItemType == ItemType.Folder
                && (change.IsBranch || change.IsMerge || change.IsAdd)
                && Steps.Any(s => s.MergedInSession && IsUnder(s.TargetItem, change.ServerItem));
        }

        // Tutti i failure dell'anteprima sono TF203015 (ChangeAlreadyPendingException) su item che la
        // catena stessa ha in sospeso: serve un check-in, non e' un errore.
        private bool AreOwnPendingFailures(GetStatus status)
        {
            var failures = status.GetFailures();
            if (failures == null || failures.Length == 0)
                return false;
            return failures.All(f => f != null && IsChangeAlreadyPending(f) && IsItemMergedByChain(f.ServerItem));
        }

        private static bool IsChangeAlreadyPending(Failure failure)
        {
            return string.Equals(failure.Code, "ChangeAlreadyPendingException", StringComparison.Ordinal)
                || (failure.Message != null && failure.Message.StartsWith("TF203015", StringComparison.Ordinal));
        }

        // Due passi toccano lo stesso item (stesso target, o uno dentro una cartella fusa Full).
        private static bool Overlaps(TaskMergeStepViewModel a, TaskMergeStepViewModel b)
        {
            if (PathEquals(a.TargetItem, b.TargetItem))
                return true;
            if (a.Recursion == TaskMergeStepRecursion.Full && IsUnder(b.TargetItem, a.TargetItem))
                return true;
            return b.Recursion == TaskMergeStepRecursion.Full && IsUnder(a.TargetItem, b.TargetItem);
        }

        private TaskMergePartViewModel GetPartInfo(int number)
        {
            TaskMergePartViewModel part;
            return _partsByNumber.TryGetValue(number, out part) ? part : null;
        }

        #endregion

        #region Start / Continue

        // Pulsante Start/Continue. Senza catena in corso, con avvisi di sicurezza (changeset esclusi da
        // cui dipendono selezionati successivi, changeset di colleghi non fusi nel target) serve una
        // conferma esplicita (default No) PRIMA di qualsiasi modifica al workspace.
        private async Task StartCommandCoreAsync()
        {
            if (!IsChainInProgress && !ConfirmStartWithWarnings())
            {
                StatusMessage = "Start cancelled: nothing was changed. Review the warnings (or the changeset selection), then Start again.";
                Log(StatusMessage);
                return;
            }
            await StartOrContinueCoreAsync();
        }

        private bool ConfirmStartWithWarnings()
        {
            var warnings = StartWarnings();
            if (warnings.Count == 0)
                return true;

            const int maxShown = 12;
            var text = new StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture, "The plan of work item {0} has {1}:\n\n", _workItemId, TaskMergeText.Count(warnings.Count, "warning"));
            foreach (var warning in warnings.Take(maxShown))
                text.Append("- ").Append(warning).Append("\n\n");
            if (warnings.Count > maxShown)
                text.AppendFormat(CultureInfo.InvariantCulture, "... and {0} more (see the tab and the log).\n\n", warnings.Count - maxShown);
            text.Append("Start the merge anyway?");

            Log(string.Format(CultureInfo.InvariantCulture, "Start: asking for confirmation ({0}).", TaskMergeText.Count(warnings.Count, "warning")));
            var answer = Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                _serviceProvider ?? Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                text.ToString(),
                "Merge from Task - warnings",
                Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_WARNING,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            if (answer != IdYes)
                return false;

            Log("Start confirmed despite the warnings.");
            return true;
        }

        private async Task StartOrContinueCoreAsync()
        {
            if (_plan == null || !_plan.IsValid || Steps.Count == 0)
                return;

            // Continue a mano (o ripartenza): l'attesa di Pending Changes resta iscritta mentre la scheda
            // lavora. Un check-in fatto li' dopo che Continue ha letto i pending arriva come
            // _checkinSeenWhileBusy e si ricontrolla alla fine (ContinueAfterExternalCheckInAsync riconta
            // i merge in sospeso, quindi non riparte due volte); se invece Continue trova il target
            // pulito, l'attesa si chiude li'. Se l'iscrizione era stata tolta (check-in della scheda,
            // ripartenza dopo l'evento) e la catena torna al punto di check-in senza che il check-in sia
            // avvenuto, l'attesa riprende (_reviewWaiting, anche dopo un'uscita anticipata o un'eccezione).
            try
            {
                await StartOrContinueBodyAsync();
            }
            finally
            {
                ResumeWaitingForReview();
            }
        }

        private async Task StartOrContinueBodyAsync()
        {
            ClearAuditNotices();

            // Regole di merge con problemi (anche azioni cambiate nella scheda che non stanno insieme):
            // la catena non va avanti. Start/Continue sono gia' disabilitati; qui si fermano le ripartenze
            // automatiche (dopo un check-in, dopo l'ultimo conflitto risolto).
            if (HasPolicyErrors)
            {
                if (IsChainInProgress)
                    State = TaskMergeChainState.StoppedFailures;
                ShowError(string.Format(CultureInfo.InvariantCulture,
                    "The chain does not continue: the merge policies have {0} (see the list below). Fix them (Policy column or Policies...), then Continue.",
                    TaskMergeText.Count(PolicyErrors.Count, "problem")));
                Log(StatusMessage);
                return;
            }

            var workspace = IsChainInProgress && _chainWorkspace != null ? _chainWorkspace : SelectedWorkspace;
            if (workspace == null)
            {
                StatusMessage = "Select a workspace.";
                return;
            }

            var target = _targetMergePath;
            if (!await Task.Run(() => workspace.IsServerPathMapped(target)))
            {
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "The target {0} is not mapped in workspace '{1}'.", target, workspace.Name);
                return;
            }

            // 1. Nessun conflitto aperto sul target.
            var openConflicts = await Task.Run(() => workspace.QueryConflicts(new[] { target }, true));
            if (openConflicts != null && openConflicts.Length > 0)
            {
                // workspace e' gia' _chainWorkspace se la catena e' in corso; altrimenti i conflitti
                // (di una sessione precedente) sono in quello selezionato, e li' si risolvono.
                _chainWorkspace = workspace;
                ShowConflicts(openConflicts);
                State = TaskMergeChainState.StoppedConflicts;
                // Senza catena di questa sessione i conflitti vengono da un merge precedente, le cui
                // modifiche restano in sospeso: dopo i conflitti serve il check-in, non si riparte da soli.
                StatusMessage = IsChainInProgress
                    ? string.Format(CultureInfo.InvariantCulture,
                        "{0} conflict(s) still open on the target: resolve them below and the merge continues by itself.", openConflicts.Length)
                    : string.Format(CultureInfo.InvariantCulture,
                        "{0} conflict(s) from an earlier merge are open on the target: resolve them below, then check in that merge (Review in Pending Changes) and Start again.",
                        openConflicts.Length);
                Log(StatusMessage);
                return;
            }
            ClearConflicts();

            // 2. Stato delle modifiche in sospeso del target.
            var pending = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).ToList());

            if (pending.Count == 0)
            {
                // Target pulito: primo avvio, oppure ripresa dopo un check-in (intermedio o fatto a mano)
                // o un undo di cio' che la catena aveva messo in sospeso. In ogni caso: get latest,
                // anteprima di tutti i passi, poi merge dalla prima parte con passi da fare.
                if (IsChainInProgress)
                    Log("Target is clean: the pending merges of this session were checked in (or undone). Re-checking every step with TFVC.");
                // Stato coerente PRIMA di azzerare il punto di check-in: se get latest o l'anteprima si
                // fermano (anche con un'eccezione) la catena finisce in StoppedFailures, non in un
                // StoppedCheckpoint senza parte ("Check in part 0 of 2"). Il check-in atteso e' avvenuto
                // (o i merge sono stati annullati): l'attesa di Pending Changes e' chiusa.
                State = TaskMergeChainState.Running;
                StatusMessage = "Target is clean: getting latest and checking every step with TFVC...";
                UnsubscribeCommitCheckin();
                _reviewWaiting = false;
                // Le righe "Merged"/"Conflicts"/"Failed" della sessione non valgono piu' finche'
                // l'anteprima non le ricalcola: se get latest o anteprima si fermano, la lista non deve
                // mostrare come fatto cio' che non e' stato verificato. "Already merged" resta: il
                // server non "disfa" un merge gia' registrato.
                // Anche "Skipped" resta: e' l'azione della regola di merge, non un esito di TFVC.
                foreach (var step in Steps)
                {
                    step.MergedInSession = false;
                    step.NeedsProtectedResult = false;
                    step.ActionChangedWhilePending = false;
                    if (step.Status != TaskMergeStepStatus.AlreadyMerged && step.Status != TaskMergeStepStatus.Skipped)
                    {
                        step.Status = TaskMergeStepStatus.Pending;
                        step.Details = null;
                    }
                }
                _checkpointPart = 0;
                _checkpointDynamicReason = null;
                UpdateProgress();
                _chainWorkspace = workspace;

                if (!await GetLatestTargetAsync(workspace))
                    return;

                if (!await PreviewAllStepsAsync(workspace))
                    return;

                var blocked = Steps.Where(s => s.Status == TaskMergeStepStatus.Blocked).ToList();
                if (blocked.Count > 0)
                {
                    State = TaskMergeChainState.StoppedFailures;
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "TFVC refuses {0} (e.g. step #{1} {2}: {3}): nothing was merged. Fix the cause, then press Load again.",
                        TaskMergeText.Count(blocked.Count, "step"), blocked[0].Number, blocked[0].RelativePath, blocked[0].Details);
                    Log(StatusMessage);
                    return;
                }
            }
            else
            {
                var chainSteps = Steps.Where(s => s.MergedInSession).ToList();
                if (chainSteps.Count == 0)
                {
                    State = TaskMergeChainState.StoppedPendingChanges;
                    StatusMessage = "Target has pending changes (e.g. a merge from an earlier session): check them in or undo them (Review in Pending Changes), then Start again."
                        + TeamPolicyFileHint(pending);
                    Log(string.Format(CultureInfo.InvariantCulture, "{0} ({1} pending change(s) under {2})",
                        StatusMessage, pending.Count, target));
                    return;
                }

                // La catena non fonde mai sopra lavoro che non e' suo: ogni pending sotto il target deve
                // essere un merge di un passo di questa sessione.
                var foreign = pending.Where(p => !IsChainPendingChange(p)).ToList();
                if (foreign.Count > 0)
                {
                    State = TaskMergeChainState.StoppedPendingChanges;
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "Target has {0} pending change(s) that this chain did not make (e.g. {1}): the chain never merges on top of other work. Check in or undo them, then Continue.",
                        foreign.Count, foreign[0].ServerItem)
                        + TeamPolicyFileHint(foreign);
                    Log(StatusMessage);
                    foreach (var change in foreign.Take(20))
                        Log(string.Format(CultureInfo.InvariantCulture, "  not from this chain: {0} ({1})", change.ServerItem, change.ChangeType));
                    return;
                }

                // Passi falliti il cui merge in sospeso e' stato annullato a mano: tornano da fare, con
                // l'azione (regola di merge) che hanno adesso.
                ReleaseUndoneFailedSteps(pending);
                chainSteps = Steps.Where(s => s.MergedInSession).ToList();

                // VERIFICA DI RIPRESA: TFVC considera gia' fatti i merge messi in sospeso in questa
                // sessione? Anteprima di ciascuno. I passi falliti non si verificano (la loro anteprima
                // avrebbe per forza operazioni): si rifanno quando la catena li incontra.
                Log("Resume check: previewing the steps merged in this session (pending, not checked in)...");
                var notDone = new List<TaskMergeStepViewModel>();
                var unverifiable = 0;
                foreach (var step in chainSteps.Where(s => s.Status != TaskMergeStepStatus.Failed))
                {
                    GetStatus status;
                    try
                    {
                        status = await PreviewStepAsync(workspace, step);
                    }
                    catch (Exception ex) when (IsVersionControlException(ex))
                    {
                        // Non verificabile: non si da' per fatto.
                        LogStepException("Resume check", step, ex);
                        notDone.Add(step);
                        unverifiable++;
                        continue;
                    }
                    Log(string.Format(CultureInfo.InvariantCulture, "Resume check step #{0} {1} {2}: {3}",
                        step.Number, step.RelativePath, step.RangeText, FormatStatus(status)));
                    LogFailures("Resume check step #" + step.Number, status);
                    if (status.NumOperations > 0 || status.NumConflicts > 0 || status.NumFailures > 0)
                        notDone.Add(step);
                }

                if (notDone.Count > 0)
                {
                    // Questi merge in sospeso non si possono archiviare cosi' come sono: il controllo finale
                    // (R3) li blocca, quindi non si indica il check-in ma come uscirne.
                    State = TaskMergeChainState.StoppedNeedsCheckIn;
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "TFVC does not confirm {0} of this chain as complete ({1}; see the log), so they cannot be checked in as they are. "
                        + "Undo the pending changes under {2} (Pending Changes, Undo), then click Continue: the chain merges those steps again.{3}",
                        TaskMergeText.Count(notDone.Count, "pending merge"),
                        string.Join(", ", notDone.Take(10).Select(s => "#" + s.Number + " " + s.RelativePath)) + (notDone.Count > 10 ? ", ..." : string.Empty),
                        target,
                        unverifiable > 0 ? " If the log shows a TFVC or connection error, click Continue first to check again." : string.Empty);
                    Log(string.Format(CultureInfo.InvariantCulture, "{0} Steps not confirmed: {1}",
                        StatusMessage, string.Join(", ", notDone.Select(s => "#" + s.Number))));
                    return;
                }

                Log("Resume check OK: TFVC treats every pending merge of this session as done. Continuing without check-in and without get latest.");
                foreach (var step in chainSteps.Where(s => !s.IsDone && s.Status != TaskMergeStepStatus.Failed))
                {
                    step.Status = TaskMergeStepStatus.Merged;
                    step.Details = string.IsNullOrEmpty(step.Details) ? "resolved" : step.Details + " - resolved";
                }
                UpdateProgress();
            }

            await RunChainAsync(workspace);
        }

        // Get latest del solo target (il target e' pulito a questo punto). La GetLatest esistente di
        // BranchesViewModel lavora per file su MergeRelation calcolate con QueryMergeRelationships
        // (costose su un gruppo intero), quindi qui si usa direttamente workspace.Get sul target.
        private async Task<bool> GetLatestTargetAsync(Workspace workspace)
        {
            var target = _targetMergePath;
            StatusMessage = "Getting latest version of the target...";
            Log("Get latest: " + target);
            var status = await Task.Run(() => workspace.Get(new[] { target }, VersionSpec.Latest, RecursionType.Full, GetOptions.None));
            Log("Get latest: " + FormatStatus(status));
            LogFailures("Get latest", status);

            if (status.NumFailures > 0 || status.NumConflicts > 0)
            {
                State = TaskMergeChainState.StoppedFailures;
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "Get latest of the target did not complete ({0} failures, {1} conflicts): see the log, fix the cause, then Start again.",
                    status.NumFailures, status.NumConflicts);
                return false;
            }
            return true;
        }

        // Esegue la catena parte per parte. Tra una parte e la successiva, se la parte ha lasciato merge
        // in sospeso, si ferma al punto di check-in (StoppedCheckpoint).
        private async Task RunChainAsync(Workspace workspace)
        {
            _chainWorkspace = workspace;
            State = TaskMergeChainState.Running;
            StatusMessage = "Merging...";

            while (true)
            {
                var part = FirstPartWithWork();
                if (part == 0)
                {
                    FinishChain();
                    return;
                }

                // Merge in sospeso di una parte precedente: TFVC non accetterebbe gli intervalli della
                // parte successiva sugli stessi item (TF203015) finche' non sono archiviati.
                var pendingPart = Steps.Where(s => s.MergedInSession && s.Part < part).Select(s => s.Part).DefaultIfEmpty(0).Max();
                if (pendingPart > 0)
                {
                    EnterCheckpoint(pendingPart, null);
                    return;
                }

                if (!await RunPartAsync(workspace, part))
                    return;
            }
        }

        // Merge reale dei passi di una parte, in ordine. Ogni passo passa prima dall'anteprima: un
        // failure non mette in sospeso nulla (quindi si puo' fermare senza danni) e un TF203015 su un
        // item della catena chiede un check-in. Salta SOLO i passi che l'anteprima da' a 0/0/0.
        // Conflitti: su un file restano aperti e la parte prosegue; su una cartella ci si ferma subito.
        // Regole di merge: Skip non chiama TFVC; Discard fonde con AlwaysAcceptMine e verifica (anteprima
        // 0/0/0 e contenuto invariato); un file con righe protette si fonde tenendo quelle del target.
        // True se la parte e' finita (tutti i passi fatti, nessun conflitto aperto).
        private async Task<bool> RunPartAsync(Workspace workspace, int partNumber)
        {
            var target = _targetMergePath;
            var partSteps = Steps.Where(s => s.Part == partNumber).ToList();
            var partText = PartCount > 1
                ? string.Format(CultureInfo.InvariantCulture, " (part {0} of {1})", partNumber, PartCount)
                : string.Empty;

            foreach (var step in partSteps)
            {
                // Azione cambiata (o Skip) su un passo con un merge di un tentativo precedente ancora in
                // sospeso: quel merge e' stato fatto con l'azione vecchia, non si riusa. Va annullato
                // (poi Continue lo libera: ReleaseUndoneFailedSteps).
                if (step.MergedInSession && !step.IsDone && step.Status != TaskMergeStepStatus.Conflicts
                    && (step.ActionChangedWhilePending || step.PolicyAction == MergePolicyAction.Skip))
                {
                    return StopStep(step, "its action changed after a merge was pended",
                        string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}) is set to {2}, but a merge of an earlier attempt is still pending on it: undo that pending change in Pending Changes, then Continue.",
                            step.Number, step.RelativePath, step.PolicyAction));
                }

                // Skip: nessuna chiamata a TFVC.
                if (step.PolicyAction == MergePolicyAction.Skip && !step.MergedInSession && step.Status != TaskMergeStepStatus.AlreadyMerged)
                {
                    if (step.Status != TaskMergeStepStatus.Skipped)
                    {
                        MarkSkipped(step);
                        Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: skipped by the merge policy (not merged, TFVC not called).",
                            step.Number, step.RelativePath));
                        UpdateProgress();
                    }
                    continue;
                }
                if (step.IsDone)
                    continue;
                // Conflitto ancora aperto di questa sessione: si risolve a fine parte.
                if (step.Status == TaskMergeStepStatus.Conflicts && step.MergedInSession)
                    continue;

                // Un item con un conflitto aperto (passo precedente della catena) non si tocca finche'
                // il conflitto non e' risolto.
                var blocking = Steps.FirstOrDefault(s => !ReferenceEquals(s, step) && s.MergedInSession
                    && s.Status == TaskMergeStepStatus.Conflicts && Overlaps(s, step));
                if (blocking != null)
                {
                    var open = await Task.Run(() => workspace.QueryConflicts(new[] { target }, true));
                    ShowConflicts(open);
                    State = TaskMergeChainState.StoppedConflicts;
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}) waits for the conflict of step #{2} on the same item: resolve it below and the merge continues by itself.",
                        step.Number, step.RelativePath, blocking.Number);
                    Log(StatusMessage);
                    return false;
                }

                step.Status = TaskMergeStepStatus.Merging;
                step.Details = null;
                StatusMessage = string.Format(CultureInfo.InvariantCulture, "Merging step {0} of {1}{2}: {3} ({4})...",
                    step.Number, Steps.Count, partText, step.RelativePath, step.RangeText);

                GetStatus preview;
                try
                {
                    preview = await PreviewStepAsync(workspace, step);
                }
                catch (Exception ex) when (IsVersionControlException(ex))
                {
                    // Anteprima: nulla e' stato messo in sospeso per questo passo; gli altri restano come sono.
                    LogStepException("Preview", step, ex);
                    step.Status = TaskMergeStepStatus.Failed;
                    step.Details = "TFVC error: " + ex.Message;
                    State = TaskMergeChainState.StoppedFailures;
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}) cannot be merged: TFVC error in its preview ({2}). Nothing was changed for this step: see the log, fix the cause, then Continue.",
                        step.Number, step.RelativePath, ex.Message);
                    Log(StatusMessage);
                    UpdateProgress();
                    return false;
                }
                Log(string.Format(CultureInfo.InvariantCulture, "Preview step #{0} {1} {2}: {3}",
                    step.Number, step.RelativePath, step.RangeText, FormatStatus(preview)));
                if (preview.NumFailures > 0)
                {
                    LogFailures("Preview step #" + step.Number, preview);
                    if (AreOwnPendingFailures(preview))
                    {
                        step.Status = TaskMergeStepStatus.Pending;
                        step.Details = "needs a check-in of the steps merged before it (TF203015)";
                        UpdateProgress();
                        EnterCheckpoint(partNumber, string.Format(CultureInfo.InvariantCulture,
                            "TFVC accepts step #{0} ({1}) only after the merges already pending on the same item are checked in (TF203015)",
                            step.Number, step.RelativePath));
                        return false;
                    }

                    step.Status = TaskMergeStepStatus.Failed;
                    step.Details = FailureSummary(preview);
                    State = TaskMergeChainState.StoppedFailures;
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}) cannot be merged: {2}. Nothing was changed for this step: see the log, fix the cause, then Continue.",
                        step.Number, step.RelativePath, FirstFailureMessage(preview));
                    Log(StatusMessage);
                    UpdateProgress();
                    return false;
                }

                if (preview.NumOperations == 0 && preview.NumConflicts == 0)
                {
                    // Un tentativo precedente interrotto di questo passo ha gia' messo in sospeso il merge:
                    // con regole di merge il contenuto va verificato, non dato per buono.
                    if (step.MergedInSession && !await VerifyInterruptedPolicyStepAsync(step))
                        return false;
                    if (step.MergedInSession && step.PolicyAction == MergePolicyAction.Discard)
                    {
                        step.Status = TaskMergeStepStatus.Merged;
                        step.Details = "discarded: recorded as merged, the target content is unchanged (verified)";
                        UpdateProgress();
                        continue;
                    }
                    step.Status = TaskMergeStepStatus.AlreadyMerged;
                    step.Details = "0 operations (preview)";
                    Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: skipped (TFVC preview: nothing to merge)",
                        step.Number, step.RelativePath));
                    UpdateProgress();
                    continue;
                }

                // Discard: registrato come fuso, contenuto del target invariato (verificato).
                if (step.PolicyAction == MergePolicyAction.Discard)
                {
                    if (!await DiscardStepAsync(workspace, step, preview))
                        return false;
                    continue;
                }

                // Righe protette: prima del merge si prepara il risultato che le tiene (o ci si ferma,
                // senza aver messo nulla in sospeso).
                ProtectedMergePreparation protectedMerge = null;
                if (step.HasProtectedLines)
                {
                    protectedMerge = await PrepareProtectedMergeAsync(workspace, step, preview);
                    if (protectedMerge.Mode == ProtectedMergeMode.Stop)
                    {
                        step.Status = TaskMergeStepStatus.Failed;
                        step.Details = "protected lines: " + protectedMerge.Message;
                        State = TaskMergeChainState.StoppedFailures;
                        StatusMessage = string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}) is not merged: {2}. Nothing was changed for this step. Set its policy to Skip or Discard (Policy column) and merge the file by hand, or change the merge policies; then Continue.",
                            step.Number, step.RelativePath, protectedMerge.Message);
                        Log(StatusMessage);
                        UpdateProgress();
                        return false;
                    }
                    Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: protected lines ({2}): {3}.",
                        step.Number, step.RelativePath, string.Join(", ", step.ProtectedLineRules.Select(r => r.Id)), protectedMerge.Message));
                    step.NewFileProtectedNote = null;
                    step.KeptTargetDifferences = null;
                }

                // Segnato prima della chiamata: se Merge lancia a meta', il workspace puo' gia' contenere
                // parte delle modifiche e Continue deve passare dalla verifica di ripresa.
                var wasChainActive = _chainActive;
                // Gia' vero se un tentativo precedente di questo passo si e' interrotto con un'eccezione:
                // i pending che ha lasciato restano della catena anche se questo tentativo fallisce.
                var wasMergedInSession = step.MergedInSession;
                step.MergedInSession = true;
                _chainActive = true;
                // Anche per le righe protette, prima della chiamata: se il merge di TFVC si interrompe,
                // il suo contenuto (con le righe protette del task) non si da' per buono al giro dopo.
                var wasNeedsProtectedResult = step.NeedsProtectedResult;
                if (protectedMerge != null && protectedMerge.Mode == ProtectedMergeMode.OwnResult)
                    step.NeedsProtectedResult = true;
                RaiseStateDependentProperties();

                var status = await MergeStepAsync(workspace, step, MergeOptions.None);
                Log(string.Format(CultureInfo.InvariantCulture, "Merge step #{0} {1} {2}: {3}",
                    step.Number, step.RelativePath, step.RangeText, FormatStatus(status)));

                if (status.NumFailures > 0)
                {
                    LogFailures("Merge step #" + step.Number, status);
                    // Con un failure TFVC non mette in sospeso NULLA di quella chiamata: il passo torna
                    // com'era prima (della catena solo se un tentativo precedente interrotto ha lasciato
                    // pending), e la catena e' "in corso" solo se lo era gia' (o ha altri merge in
                    // sospeso). Altrimenti caselle e workspace resterebbero bloccati senza pending.
                    step.MergedInSession = wasMergedInSession;
                    step.NeedsProtectedResult = wasNeedsProtectedResult;
                    _chainActive = wasChainActive || HasPendingChainMerges;
                    RaiseStateDependentProperties();
                    if (AreOwnPendingFailures(status))
                    {
                        step.Status = TaskMergeStepStatus.Pending;
                        step.Details = "needs a check-in of the steps merged before it (TF203015)";
                        UpdateProgress();
                        EnterCheckpoint(partNumber, string.Format(CultureInfo.InvariantCulture,
                            "TFVC accepts step #{0} ({1}) only after the merges already pending on the same item are checked in (TF203015)",
                            step.Number, step.RelativePath));
                        return false;
                    }

                    step.Status = TaskMergeStepStatus.Failed;
                    step.Details = FailureSummary(status);
                    State = TaskMergeChainState.StoppedFailures;
                    StatusMessage = wasMergedInSession
                        ? string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}) failed ({2}). This attempt pended nothing; the pending changes left by its earlier interrupted attempt are still under the target and stay with this chain: see the log, fix the cause, then Continue.",
                            step.Number, step.RelativePath, FirstFailureMessage(status))
                        : string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}) failed ({2}); TFVC pended nothing for this step: see the log, fix the cause, then Continue.",
                            step.Number, step.RelativePath, FirstFailureMessage(status));
                    Log(StatusMessage);
                    UpdateProgress();
                    return false;
                }

                // Il task cambia righe protette: niente risoluzione automatica di TFVC, il risultato e'
                // quello calcolato (conflitto risolto con il nostro file, o file in sospeso riscritto),
                // oppure il conflitto resta al resolver, che parte da quel risultato.
                if (protectedMerge != null && protectedMerge.Mode == ProtectedMergeMode.OwnResult)
                {
                    if (!await ApplyProtectedResultAsync(workspace, step, status, protectedMerge, partText))
                        return false;
                    continue;
                }

                var autoResolved = 0;
                if (status.NumConflicts > 0)
                {
                    // Stesso tentativo automatico del resto dello strumento con l'opzione "manuale":
                    // solo cio' che TFVC sa fondere da solo (AutoResolveValidConflicts), sul solo item.
                    var itemPath = step.TargetItem;
                    var remaining = await Task.Run(() =>
                        BranchesViewModel.AutoResolveConflicts(workspace, itemPath, MergeOption.ManualResolveConflict));
                    var remainingCount = remaining == null ? 0 : remaining.Length;
                    autoResolved = Math.Max(0, status.NumConflicts - remainingCount);
                    Log(string.Format(CultureInfo.InvariantCulture, "Step #{0}: automatic resolution, {1} conflict(s) left",
                        step.Number, remainingCount));

                    if (remainingCount > 0)
                    {
                        step.Status = TaskMergeStepStatus.Conflicts;
                        step.Details = string.Format(CultureInfo.InvariantCulture, "{0} operations, {1} conflict(s) to resolve",
                            status.NumOperations, remainingCount);
                        UpdateProgress();

                        if (step.IsFolder)
                        {
                            // I file della cartella dipendono da lei: ci si ferma subito.
                            var open = await Task.Run(() => workspace.QueryConflicts(new[] { target }, true));
                            ShowConflicts(open);
                            State = TaskMergeChainState.StoppedConflicts;
                            StatusMessage = string.Format(CultureInfo.InvariantCulture,
                                "Folder step #{0} ({1}) stopped with {2} conflict(s): resolve them below and the merge continues by itself.",
                                step.Number, step.RelativePath, remainingCount);
                            Log(StatusMessage);
                            return false;
                        }

                        // File: il conflitto resta aperto, la catena prosegue con gli altri file della
                        // parte (item diversi non si bloccano a vicenda).
                        Log(string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} {1}: {2} conflict(s) left open, continuing with the next steps{3}.",
                            step.Number, step.RelativePath, remainingCount, partText));
                        continue;
                    }
                }

                step.Status = TaskMergeStepStatus.Merged;
                step.Details = autoResolved > 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0} operations ({1} conflict(s) auto-resolved)", status.NumOperations, autoResolved)
                    : string.Format(CultureInfo.InvariantCulture, "{0} operations", status.NumOperations);

                // File nuovo nel target con regole di righe protette: si porta intero; se contiene righe
                // protette va nel promemoria.
                if (protectedMerge != null && protectedMerge.Mode == ProtectedMergeMode.NewFile)
                    await CheckNewFileProtectedLinesAsync(workspace, step);
                UpdateProgress();
            }

            // Fine parte: tutti i conflitti aperti nella lista del resolver.
            var openConflicts = await Task.Run(() => workspace.QueryConflicts(new[] { target }, true));
            if (openConflicts != null && openConflicts.Length > 0)
            {
                ShowConflicts(openConflicts);
                State = TaskMergeChainState.StoppedConflicts;
                StatusMessage = PartCount > 1
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Part {0} of {1} is merged with {2} conflict(s) to resolve: resolve them below and the merge continues by itself.",
                        partNumber, PartCount, openConflicts.Length)
                    : string.Format(CultureInfo.InvariantCulture,
                        "All steps are merged with {0} conflict(s) to resolve: resolve them below and the merge continues by itself.",
                        openConflicts.Length);
                Log(StatusMessage);
                return false;
            }

            var unfinished = partSteps.Where(s => !s.IsDone).ToList();
            if (unfinished.Count > 0)
            {
                // Non dovrebbe succedere (passi con conflitti che TFVC non da' piu' aperti): la verifica
                // di ripresa di Continue li ricontrolla con l'anteprima invece di darli per fatti.
                State = TaskMergeChainState.StoppedConflicts;
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "{0} of part {1} had conflicts that are no longer open: click Continue to verify them with TFVC.",
                    TaskMergeText.Count(unfinished.Count, "step"), partNumber);
                Log(StatusMessage);
                return false;
            }

            Log(string.Format(CultureInfo.InvariantCulture, "Part {0} of {1} merged.", partNumber, Math.Max(1, PartCount)));
            return true;
        }

        // Punto di check-in: la parte partNumber ha merge in sospeso che vanno archiviati prima di
        // proseguire. dynamicReason != null: il check-in serve prima di un passo della stessa parte.
        private void EnterCheckpoint(int partNumber, string dynamicReason)
        {
            _checkpointPart = partNumber;
            _checkpointDynamicReason = dynamicReason;
            State = TaskMergeChainState.StoppedCheckpoint;

            if (dynamicReason == null)
            {
                var part = GetPartInfo(partNumber);
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "Part {0} of {1} is merged: check it in to continue with part {2} (Check in part {0} of {1} & continue, or Review in Pending Changes).{3}",
                    partNumber, PartCount, partNumber + 1,
                    part == null || string.IsNullOrEmpty(part.EndReason) ? string.Empty : " " + part.EndReason);
            }
            else
            {
                StatusMessage = dynamicReason + ": check in what is merged so far (Check in & continue, or Review in Pending Changes), then the chain continues.";
            }
            Log(StatusMessage);
            ResumeWaitingForReview();
            UpdateProgress();
            RaiseStateDependentProperties();
        }

        // Pending Changes era stato aperto precompilato e il check-in non e' ancora avvenuto (Continue
        // che ritrova lo stesso punto, conferma negata, controllo non pulito...): l'attesa riprende.
        private void ResumeWaitingForReview()
        {
            if (!_reviewWaiting || _commitWatchServer != null || !ShowCheckInActions)
                return;
            SubscribeCommitCheckin(_targetMergePath);
            if (_commitWatchServer != null)
                Log("Still waiting for the check-in from Pending Changes: the chain goes on by itself once no merge is pending under the target.");
        }

        // Tutti i passi fatti.
        private void FinishChain()
        {
            State = TaskMergeChainState.Completed;
            if (HasPendingChainMerges)
            {
                StatusMessage = PartCount > 1
                    ? string.Format(CultureInfo.InvariantCulture,
                        "All {0} parts are merged: check in the last one (Check in & finish) or review it in Pending Changes.", PartCount)
                    : "All steps are merged: check in (Check in & finish) or review in Pending Changes.";
                Log(StatusMessage);
                ResumeWaitingForReview();
            }
            else if (_chainActive)
            {
                StatusMessage = "All steps are merged and checked in.";
                CloseChain(StatusMessage);
            }
            else
            {
                StatusMessage = Steps.Any(s => s.Status == TaskMergeStepStatus.Skipped)
                    ? "Nothing to merge: every step is already merged or skipped by the merge policies."
                    : "Nothing to merge: TFVC reports every step as already merged.";
                Log(StatusMessage);
            }
            RebuildFollowUps();
            UpdateProgress();
        }

        #endregion

        #region Check-in

        // Parte da archiviare: quella del punto di check-in, oppure (a catena finita) l'ultima con
        // merge in sospeso.
        private int CheckInPartNumber()
        {
            if (State == TaskMergeChainState.StoppedCheckpoint && _checkpointPart > 0)
                return _checkpointPart;
            return Steps.Where(s => s.MergedInSession).Select(s => s.Part).DefaultIfEmpty(Math.Max(1, PartCount)).Max();
        }

        private bool IsPartialCheckIn
        {
            get { return State == TaskMergeChainState.StoppedCheckpoint && _checkpointDynamicReason != null; }
        }

        // Changeset del commento: quelli della parte; per un check-in parziale (dinamico) quelli dei
        // passi effettivamente in sospeso.
        private IReadOnlyList<int> CheckInChangesets(int partNumber)
        {
            if (IsPartialCheckIn)
                return Steps.Where(s => s.MergedInSession).SelectMany(s => s.TaskChangesetIds).Distinct().OrderBy(id => id).ToList();
            var part = GetPartInfo(partNumber);
            if (part != null)
                return part.TaskChangesetIds;
            return PlannedChangesetIds();
        }

        // Changeset del task con cui e' stato calcolato il piano (quelli selezionati con le caselle).
        private IReadOnlyList<int> PlannedChangesetIds()
        {
            return _plannedSelection ?? new List<int>().AsReadOnly();
        }

        // "Merge from Task #<id> "<titolo>" - part <k> of <n> (changesets <elenco della parte>)";
        // con una sola parte senza "part". Se le regole di merge hanno agito sui passi consegnati, una
        // seconda riga lo dice (vedi PolicyCommentLine).
        private string BuildCheckInComment(int partNumber, IReadOnlyList<int> changesets, bool partial)
        {
            var text = string.Format(CultureInfo.InvariantCulture, "Merge from Task #{0} \"{1}\"", _workItemId, _workItemTitle);
            if (PartCount > 1)
                text += string.Format(CultureInfo.InvariantCulture, " - part {0} of {1}", partNumber, PartCount);
            if (partial)
                text += PartCount > 1 ? ", partial" : " - partial";
            text += " (changesets " + string.Join(", ", changesets) + ")";
            return WithPolicyLine(text, StepsToDeliverForCheckIn(partNumber, partial));
        }

        // Commento per Pending Changes aperto a mano: della parte se la catena aspetta un check-in,
        // altrimenti di tutto il task (i changeset selezionati).
        private string ReviewComment()
        {
            if (ShowCheckInActions)
            {
                var part = CheckInPartNumber();
                return BuildCheckInComment(part, CheckInChangesets(part), IsPartialCheckIn);
            }
            if (HasPendingChainMerges)
            {
                // Catena ferma fuori dal punto di check-in: la parte e' intera solo se tutti i suoi passi
                // sono fatti; altrimenti "partial" con i changeset dei passi in sospeso.
                var part = Steps.Where(s => s.MergedInSession).Select(s => s.Part).Max();
                var info = GetPartInfo(part);
                if (info != null && Steps.Where(s => s.Part == part).All(s => s.IsDone))
                    return BuildCheckInComment(part, info.TaskChangesetIds, false);
                return BuildCheckInComment(part,
                    Steps.Where(s => s.MergedInSession).SelectMany(s => s.TaskChangesetIds).Distinct().OrderBy(id => id).ToList(),
                    true);
            }
            return WithPolicyLine(string.Format(CultureInfo.InvariantCulture, "Merge from Task #{0} \"{1}\" (changesets {2})",
                _workItemId, _workItemTitle, string.Join(", ", PlannedChangesetIds())), Steps.Select(s => s.Step));
        }

        private string WithPolicyLine(string comment, IEnumerable<TaskMergeStep> steps)
        {
            var line = PolicyCommentLine(steps);
            return line == null ? comment : comment + "\n" + line;
        }

        // Passi consegnati dal check-in: quelli delle parti con merge della catena in sospeso (di norma
        // la sola parte del punto di check-in, o l'ultima a catena finita); per un check-in parziale
        // (dinamico), della sua parte solo i passi gia' fatti.
        private List<TaskMergeStep> StepsToDeliverForCheckIn(int partNumber, bool partial)
        {
            var parts = new HashSet<int>(Steps.Where(s => s.MergedInSession).Select(s => s.Part));
            parts.Add(partNumber);
            return Steps
                .Where(s => parts.Contains(s.Part) && (!partial || s.Part != partNumber || s.IsDone))
                .Select(s => s.Step)
                .ToList();
        }

        // Changeset ammessi nei merge in sospeso di un check-in al punto di check-in (o a catena
        // finita): quelli delle parti consegnate, non tutti i selezionati. Un pending il cui intervallo
        // arriva in una parte successiva verrebbe archiviato con il commento (e l'elenco dei changeset)
        // della parte sbagliata: il controllo finale lo segnala come changeset estraneo.
        private IReadOnlyCollection<int> DeliveredChangesetIds(IEnumerable<TaskMergeStep> steps, int partNumber)
        {
            var parts = new HashSet<int>(steps.Select(s => s.Part)) { partNumber };
            var ids = new SortedSet<int>();
            foreach (var number in parts)
            {
                var part = GetPartInfo(number);
                if (part == null)
                    continue;
                foreach (var id in part.TaskChangesetIds)
                    ids.Add(id);
            }
            return ids.ToList().AsReadOnly();
        }

        // A3: controllo finale indipendente (TaskMergeAudit) su dati riletti da TFVC adesso, fuori dal
        // thread UI. Il risultato porta anche i pending controllati: sono quelli da archiviare.
        // allowedChangesets: i changeset che i merge in sospeso possono contenere (quelli delle parti
        // consegnate al punto di check-in; tutti i selezionati per una revisione senza catena).
        private async Task<TaskMergeAuditRun> RunAuditAsync(Workspace workspace, string target,
            IReadOnlyCollection<int> allowedChangesets,
            Func<IReadOnlyList<PendingChange>, IReadOnlyList<TaskMergeStep>> stepsToDeliver)
        {
            ClearAuditNotices();
            StatusMessage = "Final check: reading the pending changes, the history of their sources and the steps from TFVC...";
            Log("Final check: pending changes under " + target + " (with their merge sources), history of every source item, preview of the steps to deliver; allowed changesets: "
                + TaskMergeText.Changesets(allowedChangesets.OrderBy(id => id).ToList()) + ".");
            var selected = allowedChangesets;
            // Regole di merge dei passi (azioni, righe protette, contenuto prima dei Discard) fotografate
            // qui, sul thread UI: il controllo in background non legge i view model.
            var policy = CreateAuditPolicy();
            var progress = CreateBackgroundProgress();
            try
            {
                return await Task.Run(() => TaskMergeAuditDataSource.Run(workspace, target, selected, stepsToDeliver, progress, policy));
            }
            finally
            {
                EndBackgroundProgress();
            }
        }

        // Controllo finale non pulito: problemi nella scheda e nel registro, banner rosso. Nessun
        // pulsante lo aggira: si toglie la causa e si riprova.
        private void ReportAuditFailure(TaskMergeAuditRun audit, bool checkIn)
        {
            ShowAuditNotices(audit);
            var summary = audit.Result == null ? "Final check failed. The check-in is blocked." : audit.Result.Summary;
            ShowError(summary + (checkIn
                ? " Nothing was checked in: see the problems in the list below and in the log."
                : " Pending Changes was not opened for this check-in: see the problems in the list below and in the log."));
        }

        private void ShowAuditNotices(TaskMergeAuditRun audit)
        {
            AuditNotices.Clear();
            if (audit != null && audit.Result != null)
            {
                _deferLogText = true;
                try
                {
                    Log(audit.Result.Summary);
                    if (!audit.IsClean)
                        AuditNotices.Add(new TaskMergeNotice(audit.Result.Summary, true));
                    foreach (var issue in audit.Result.Issues)
                    {
                        AuditNotices.Add(new TaskMergeNotice(issue.Message, issue.Blocking));
                        Log((issue.Blocking ? "  FINAL CHECK: " : "  Final check note: ") + issue.Message);
                    }
                }
                finally
                {
                    _deferLogText = false;
                    FlushLog();
                }
            }
            RaisePlanProperties();
        }

        private void ClearAuditNotices()
        {
            if (AuditNotices.Count == 0)
                return;
            AuditNotices.Clear();
            RaisePlanProperties();
        }

        // Check in & continue (al punto di check-in) / Check in & finish (a catena finita): pending della
        // catena sotto il target, controllo finale, conferma, valutazione di TFVC e check-in con il work
        // item associato. Se la valutazione non e' pulita, o il target ha il gated check-in, niente
        // check-in automatico: Pending Changes si apre precompilato e la catena aspetta il check-in fatto li'.
        private async Task CheckInCoreAsync()
        {
            var workspace = _chainWorkspace;
            var target = _targetMergePath;
            if (workspace == null || string.IsNullOrEmpty(target) || _plan == null || !ShowCheckInActions)
                return;

            var continueAfter = State == TaskMergeChainState.StoppedCheckpoint;
            // L'attesa di Pending Changes (se c'e') resta attiva finche' la scheda non archivia davvero:
            // conferma negata, conflitti o controllo non pulito non la devono perdere.

            var conflicts = await Task.Run(() => workspace.QueryConflicts(new[] { target }, true));
            if (conflicts != null && conflicts.Length > 0)
            {
                ShowConflicts(conflicts);
                State = TaskMergeChainState.StoppedConflicts;
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "{0} conflict(s) are open on the target: resolve them below first.", conflicts.Length);
                Log(StatusMessage);
                return;
            }

            var pending = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).ToArray());
            if (pending.Length == 0)
            {
                await ContinueWithNothingPendingAsync(continueAfter);
                return;
            }

            if (!CheckAllFromChain(pending, "Not checked in"))
                return;

            var partNumber = CheckInPartNumber();
            var partial = IsPartialCheckIn;
            var changesets = CheckInChangesets(partNumber);
            var comment = BuildCheckInComment(partNumber, changesets, partial);

            // A3: controllo finale indipendente, su dati riletti adesso da TFVC. Non pulito: niente check-in.
            var steps = StepsToDeliverForCheckIn(partNumber, partial).AsReadOnly();
            var audit = await RunAuditAsync(workspace, target, DeliveredChangesetIds(steps, partNumber), p => steps);
            if (!audit.IsClean)
            {
                ReportAuditFailure(audit, true);
                return;
            }
            ShowAuditNotices(audit);

            // Si archiviano ESATTAMENTE i pending controllati dall'audit (riletti da TFVC): tutti della catena.
            var toCheckIn = audit.Pending;
            if (toCheckIn.Length == 0)
            {
                await ContinueWithNothingPendingAsync(continueAfter);
                return;
            }
            if (!CheckAllFromChain(toCheckIn, "Not checked in"))
                return;

            if (!ConfirmCheckIn(workspace, target, toCheckIn.Length, partNumber, changesets, comment, partial, audit.Result.Summary))
            {
                StatusMessage = "Check-in cancelled: nothing was checked in.";
                Log(StatusMessage);
                return;
            }

            StatusMessage = "Checking in...";
            Log(string.Format(CultureInfo.InvariantCulture, "Check-in of {0} pending change(s) under {1}: {2}", toCheckIn.Length, target, comment));
            // Il check-in della scheda solleva a sua volta CommitCheckin: l'attesa di Pending Changes si
            // chiude adesso, altrimenti la catena ripartirebbe due volte. Se il check-in non avviene
            // (esito negativo, pending cambiati, eccezione, Pending Changes che non si apre) l'attesa
            // riparte nel finally: un check-in fatto li' nel frattempo (_checkinSeenWhileBusy) si
            // ricontrolla alla fine dell'operazione.
            UnsubscribeCommitCheckin();
            var workItemStore = _workItemStore;
            var workItemId = _workItemId;
            var checkedIn = false;
            try
            {
                // Dentro Run, subito prima del check-in, i pending sotto il target si rileggono e si
                // confrontano con quelli controllati (TaskMergePendingSnapshot), e cosi' il contenuto dei
                // file che il controllo ha verificato (Discard, righe protette).
                var fingerprints = audit.ContentFingerprints;
                var result = await Task.Run(() => TaskMergeCheckIn.Run(workspace, target, toCheckIn, comment, workItemStore, workItemId, fingerprints));

                if (result.Outcome == TaskMergeCheckInOutcome.Changed)
                {
                    // Cio' che si archivierebbe non e' piu' cio' che e' stato controllato: niente
                    // check-in e niente Pending Changes precompilato con dati superati.
                    foreach (var problem in result.Problems.Take(50))
                        Log("  changed after the final check: " + problem);
                    ShowError("Nothing was checked in: the pending changes under the target (or the content of files the final check verified) changed after the final check. "
                        + TaskMergePendingSnapshot.Describe(result.Problems)
                        + " Click the check-in again to repeat the final check on what is pending now.");
                    Log(StatusMessage);
                    return;
                }

                if (result.Outcome != TaskMergeCheckInOutcome.CheckedIn)
                {
                    foreach (var problem in result.Problems)
                        Log("  " + problem);
                    var reason = DescribeCheckInProblem(result);
                    // I pending sono gli stessi appena controllati: Pending Changes si apre precompilato.
                    var opened = OpenPendingChangesPrefilled(workspace, target, toCheckIn, comment);
                    StatusMessage = opened
                        ? reason + " Pending Changes is open with the comment and the work item: check in there"
                            + (continueAfter ? " and the chain continues by itself." : " to finish the chain.")
                        : reason;
                    Log(StatusMessage);
                    return;
                }

                checkedIn = true;
                Log(string.Format(CultureInfo.InvariantCulture, "Checked in {0} change(s) as changeset C{1}.", toCheckIn.Length, result.ChangesetId));

                if (continueAfter)
                {
                    if (HasStaleInput)
                    {
                        // Stessa regola della ripartenza dopo un check-in fatto in Pending Changes: work
                        // item o target cambiati durante il controllo o la conferma, niente ripartenza.
                        _reviewWaiting = false;
                        StatusMessage = string.Format(CultureInfo.InvariantCulture,
                            "Checked in as C{0}. The work item or target was edited after Load: restore them and click Continue, or press Load to start over.",
                            result.ChangesetId);
                        Log(StatusMessage);
                        return;
                    }

                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "Checked in as C{0}: getting latest and continuing...", result.ChangesetId);
                    await StartOrContinueCoreAsync();
                    return;
                }

                var left = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).Any(p => p.IsMerge));
                if (left)
                {
                    StatusMessage = string.Format(CultureInfo.InvariantCulture,
                        "Checked in as C{0}, but merges are still pending under the target: review them in Pending Changes.", result.ChangesetId);
                    Log(StatusMessage);
                    ResumeWaitingForReview();
                    return;
                }

                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "Checked in as C{0}: work item {1} is merged into {2}.", result.ChangesetId, _workItemId, _targetPath);
                CloseChain(StatusMessage);
            }
            finally
            {
                if (!checkedIn)
                    ResumeWaitingForReview();
            }
        }

        // Niente in sospeso sotto il target (archiviato altrove, o annullato): al punto di check-in si
        // prosegue (stessa strada di Continue), a catena finita la catena si chiude.
        private async Task ContinueWithNothingPendingAsync(bool continueAfter)
        {
            Log("Check-in: nothing is pending under the target (already checked in, or undone).");
            if (continueAfter && HasStaleInput)
            {
                // Work item o target cambiati durante l'operazione: la catena non riparte da sola.
                // Non c'e' piu' niente da archiviare, quindi l'attesa di Pending Changes e' finita.
                UnsubscribeCommitCheckin();
                _reviewWaiting = false;
                StatusMessage = "Nothing is pending under the target any more. The work item or target was edited after Load: restore them and click Continue, or press Load to start over.";
                Log(StatusMessage);
                return;
            }
            if (continueAfter)
                await StartOrContinueCoreAsync();
            else
                CloseChain(string.Format(CultureInfo.InvariantCulture, "The chain of work item {0} is finished.", _workItemId));
        }

        // D7: sotto il target solo merge di questa catena; altrimenti stop con l'elenco nel registro.
        private bool CheckAllFromChain(IEnumerable<PendingChange> pending, string what)
        {
            var foreign = pending.Where(p => !IsChainPendingChange(p)).ToList();
            if (foreign.Count == 0)
                return true;

            ShowError(string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} pending change(s) under the target were not made by this chain (e.g. {2}). Check them in or undo them from Pending Changes, then try again.",
                what, foreign.Count, foreign[0].ServerItem));
            foreach (var change in foreign.Take(20))
                Log(string.Format(CultureInfo.InvariantCulture, "  not from this chain: {0} ({1})", change.ServerItem, change.ChangeType));
            return false;
        }

        private static string DescribeCheckInProblem(TaskMergeCheckInResult result)
        {
            var problems = result.Problems ?? new List<string>();
            var first = string.Join("; ", problems.Take(3)) + (problems.Count > 3 ? "; ..." : string.Empty);
            switch (result.Outcome)
            {
                case TaskMergeCheckInOutcome.Gated:
                    return "The target uses gated check-in, so the tab does not check in by itself (" + first + ").";
                case TaskMergeCheckInOutcome.NotAllowed:
                    return "Not checked in automatically: " + first + ".";
                default:
                    return "The check-in failed: " + first + ".";
            }
        }

        // Conferma esplicita (default No) con numero di modifiche, target, parte, changeset, esito del
        // controllo finale e commento.
        private bool ConfirmCheckIn(Workspace workspace, string target, int count, int partNumber,
            IReadOnlyList<int> changesets, string comment, bool partial, string auditSummary)
        {
            var partText = PartCount > 1
                ? string.Format(CultureInfo.InvariantCulture, "Part {0} of {1}{2}\n", partNumber, PartCount, partial ? " (partial)" : string.Empty)
                : partial ? "Partial check-in\n" : string.Empty;
            var message = string.Format(CultureInfo.InvariantCulture,
                "Check in {0} pending change(s) under {1}?\n\n{2}Changesets: {3}\nWork item: #{4} \"{5}\" (associated)\nWorkspace: {6}\n\n{7}\n\nComment:\n{8}",
                count, target, partText, string.Join(", ", changesets), _workItemId, _workItemTitle, workspace.Name,
                auditSummary, comment);
            var answer = Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                _serviceProvider ?? Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                message,
                "Merge from Task - check-in",
                Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_QUERY,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            return answer == IdYes;
        }

        // Attesa del check-in fatto in Pending Changes: quando TFVC archivia qualcosa sotto il target
        // e sotto il target non restano merge in sospeso, la catena prosegue da sola (get latest e parte
        // successiva) o, a catena finita, si chiude.
        private void SubscribeCommitCheckin(string target)
        {
            UnsubscribeCommitCheckin();
            var server = _versionControl;
            if (server == null || string.IsNullOrEmpty(target))
                return;
            _commitWatchServer = server;
            _commitWatchTarget = target;
            server.CommitCheckin += OnCommitCheckin;
        }

        private void UnsubscribeCommitCheckin()
        {
            var server = _commitWatchServer;
            if (server == null)
                return;
            server.CommitCheckin -= OnCommitCheckin;
            _commitWatchServer = null;
            _commitWatchTarget = null;
        }

        // Evento di TFVC: arriva sul thread che ha fatto il check-in, non su quello UI.
        private void OnCommitCheckin(object sender, CommitCheckinEventArgs e)
        {
            try
            {
                var target = _commitWatchTarget;
                if (target == null || e == null || e.Changes == null)
                    return;
                if (!e.Changes.Any(c => c != null && IsSameOrUnder(c.ServerItem, target)))
                    return;

                var changesetId = e.ChangesetId;
                _dispatcher.BeginInvoke(new Action(() => FireAndForget(() => OnTargetCheckedInAsync(changesetId))));
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.Error("Merge from Task: check-in notification", ex);
                }
                catch (Exception)
                {
                    // Niente altro da fare in un gestore di evento di TFVC.
                }
            }
        }

        private async Task OnTargetCheckedInAsync(int changesetId)
        {
            if (_commitWatchServer == null)
                return;
            if (IsBusy)
            {
                // Non si perde: alla fine dell'operazione in corso RunBusyAsync lo ricontrolla (stato
                // compreso: durante Continue la catena puo' essere Running e tornare al punto di check-in).
                _checkinSeenWhileBusy = changesetId;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "C{0} was checked in under the target while another operation was running: it is checked again when that operation ends.", changesetId));
                return;
            }
            if (State != TaskMergeChainState.StoppedCheckpoint && State != TaskMergeChainState.Completed)
                return;
            await RunBusyAsync(() => ContinueAfterExternalCheckInAsync(changesetId));
        }

        private async Task ContinueAfterExternalCheckInAsync(int changesetId)
        {
            var workspace = _chainWorkspace;
            var target = _targetMergePath;
            if (workspace == null || string.IsNullOrEmpty(target))
                return;

            var remaining = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).Count(p => p.IsMerge));
            if (remaining > 0)
            {
                // Il workspace puo' aggiornare i propri pending un attimo dopo l'evento: si riguarda una volta.
                await Task.Delay(1500);
                remaining = await Task.Run(() => workspace.GetPendingChangesEnumerable(target, RecursionType.Full).Count(p => p.IsMerge));
            }
            if (remaining > 0)
            {
                Log(string.Format(CultureInfo.InvariantCulture,
                    "C{0} checked in from Pending Changes; {1} merge(s) are still pending under the target: waiting for their check-in (or click Continue).",
                    changesetId, remaining));
                return;
            }

            UnsubscribeCommitCheckin();
            Log(string.Format(CultureInfo.InvariantCulture,
                "C{0} checked in from Pending Changes: no merge is pending under the target any more.", changesetId));

            if (State == TaskMergeChainState.StoppedCheckpoint && HasStaleInput)
            {
                // Stessa regola della ripartenza dopo i conflitti: con work item o target cambiati la
                // catena non riparte da sola.
                _reviewWaiting = false;
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    "Checked in as C{0}. The work item or target was edited after Load: restore them and click Continue, or press Load to start over.",
                    changesetId);
                Log(StatusMessage);
                return;
            }

            if (State == TaskMergeChainState.StoppedCheckpoint)
            {
                StatusMessage = string.Format(CultureInfo.InvariantCulture, "Checked in as C{0}: getting latest and continuing...", changesetId);
                await StartOrContinueCoreAsync();
                return;
            }

            StatusMessage = string.Format(CultureInfo.InvariantCulture,
                "Checked in as C{0}: work item {1} is merged into {2}.", changesetId, _workItemId, _targetPath);
            CloseChain(StatusMessage);
        }

        #endregion

        #region Conflicts

        // Ricostruisce la lista dei conflitti aperti. I resolver dei conflitti ancora aperti restano (col
        // lavoro in corso), quelli dei conflitti spariti si chiudono. Selezione: lo stesso file se c'e'
        // ancora, altrimenti quello che ha preso il suo posto nella lista (il "prossimo"), altrimenti il primo.
        private void ShowConflicts(IEnumerable<Conflict> conflicts)
        {
            var previous = _selectedConflict;
            var previousIndex = previous == null ? -1 : Conflicts.IndexOf(previous);

            var rows = new List<TaskMergeConflictViewModel>();
            if (conflicts != null)
            {
                foreach (var conflict in conflicts)
                    rows.Add(new TaskMergeConflictViewModel(conflict));
            }

            _rebuildingConflicts = true;
            try
            {
                Conflicts.Clear();
                foreach (var row in rows)
                    Conflicts.Add(row);
            }
            finally
            {
                _rebuildingConflicts = false;
            }

            var openKeys = new HashSet<string>(rows.Select(ConflictKey), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _resolvers.Keys.Where(k => !openKeys.Contains(k)).ToList())
                ForgetResolver(key);

            TaskMergeConflictViewModel next = null;
            if (previous != null)
                next = rows.FirstOrDefault(r => string.Equals(r.Path, previous.Path, StringComparison.OrdinalIgnoreCase));
            if (next == null && rows.Count > 0)
                next = rows[Math.Max(0, Math.Min(previousIndex, rows.Count - 1))];

            SelectConflict(next);
            RaiseStateDependentProperties();
        }

        private void ClearConflicts()
        {
            _rebuildingConflicts = true;
            try
            {
                Conflicts.Clear();
            }
            finally
            {
                _rebuildingConflicts = false;
            }
            foreach (var key in _resolvers.Keys.ToList())
                ForgetResolver(key);
            SelectConflict(null);
            RaiseStateDependentProperties();
        }

        private void SelectConflict(TaskMergeConflictViewModel row)
        {
            _selectedConflict = row;
            // Sempre notificato: durante la ricostruzione la ListBox ha perso la selezione.
            OnPropertyChanged("SelectedConflict");
            ActivateResolver(row);
        }

        // Chiave di un conflitto per i resolver aperti: id del conflitto in TFVC e path.
        private static string ConflictKey(TaskMergeConflictViewModel row)
        {
            return row.Conflict.ConflictId.ToString(CultureInfo.InvariantCulture) + "|" + row.Path;
        }

        // Mostra il resolver del conflitto selezionato: quello gia' aperto per lo stesso conflitto (col
        // lavoro fatto), altrimenti uno nuovo che carica le tre versioni del file (TFS fuori dal thread
        // UI, dentro LoadAsync). Il precedente resta aperto se il suo conflitto e' ancora nella lista.
        private void ActivateResolver(TaskMergeConflictViewModel row)
        {
            var previous = _activeResolver;
            var next = row != null && _chainWorkspace != null ? GetOrCreateResolver(row) : null;

            if (!ReferenceEquals(previous, next))
            {
                if (previous != null)
                    previous.PropertyChanged -= OnResolverPropertyChanged;
                _activeResolver = next;
                if (next != null)
                    next.PropertyChanged += OnResolverPropertyChanged;

                OnPropertyChanged("ActiveResolver");
                OnPropertyChanged("HasActiveResolver");
                RaiseStateDependentProperties();

                // Risolto, sparito dalla lista o lista svuotata: nessuno lo riusera'.
                if (previous != null && !_resolvers.ContainsValue(previous))
                    DisposeResolver(previous);
            }

            if (next != null)
                FireAndForget(next.LoadAsync);
        }

        private ConflictResolverViewModel GetOrCreateResolver(TaskMergeConflictViewModel row)
        {
            var key = ConflictKey(row);
            ConflictResolverViewModel resolver;
            if (_resolvers.TryGetValue(key, out resolver))
            {
                if (!resolver.IsResolved && !resolver.IsDisposed && ReferenceEquals(resolver.Workspace, _chainWorkspace))
                    return resolver;
                ForgetResolver(key);
            }

            try
            {
                resolver = new ConflictResolverViewModel(row.Conflict, _chainWorkspace, _logger, () => !IsBusy, OnResolverResolvedAsync);
            }
            catch (Exception ex)
            {
                Log("ERROR opening the conflict on " + row.Path + ": " + ex.Message);
                _logger.Error("Merge from Task: conflict resolver", ex);
                return null;
            }

            // File con righe protette (regole di merge): l'editor parte dal risultato che tiene quelle
            // del target; le modifiche del task a quelle righe sono nel promemoria.
            var rules = LineRulesForConflict(row.Conflict);
            if (rules != null && rules.Count > 0)
            {
                resolver.UseMergeOverride(
                    (baseText, sourceText, targetText) => MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, rules).Merge,
                    "Merge policy: protected lines keep the target's version (" + string.Join(", ", rules.Select(r => r.Id)) + ")");
            }
            _resolvers[key] = resolver;
            return resolver;
        }

        // Righe protette dell'item del conflitto (null se nessuna): quelle dei passi Merge di file.
        private IReadOnlyList<MergeLineRule> LineRulesForConflict(Conflict conflict)
        {
            var item = conflict == null ? null : conflict.YourServerItem;
            IReadOnlyList<MergeLineRule> rules;
            return !string.IsNullOrEmpty(item) && _lineRulesByTarget.TryGetValue(item.TrimEnd('/'), out rules) ? rules : null;
        }

        // Toglie un resolver da quelli aperti; si chiude subito se non e' quello mostrato (quello mostrato
        // si chiude quando ActivateResolver lo sostituisce).
        private void ForgetResolver(string key)
        {
            ConflictResolverViewModel resolver;
            if (!_resolvers.TryGetValue(key, out resolver))
                return;
            _resolvers.Remove(key);
            if (!ReferenceEquals(resolver, _activeResolver))
                DisposeResolver(resolver);
        }

        private void DisposeResolver(ConflictResolverViewModel resolver)
        {
            try
            {
                resolver.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Error("Merge from Task: closing the conflict resolver", ex);
            }
        }

        private void OnResolverPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _activeResolver))
                return;
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "IsBusy")
                RaiseStateDependentProperties();
        }

        // Callback del resolver (thread UI) dopo che TFVC ha segnato risolto il suo conflitto.
        // La rilettura parte dopo che il resolver ha chiuso il proprio comando: rileggere i conflitti
        // sostituisce (e chiude) proprio il resolver che sta chiamando.
        private Task OnResolverResolvedAsync(ConflictResolverViewModel resolver, string description)
        {
            try
            {
                // Non si riusa piu': se lo stesso file ricompare e' un conflitto nuovo. Resta mostrato
                // ("Resolved") finche' la rilettura non lo sostituisce.
                foreach (var key in _resolvers.Where(p => ReferenceEquals(p.Value, resolver)).Select(p => p.Key).ToList())
                    _resolvers.Remove(key);
                Log(string.Format(CultureInfo.InvariantCulture, "Conflict resolved: {0} ({1})",
                    resolver == null ? "?" : resolver.Path, description));
            }
            catch (Exception ex)
            {
                _logger.Error("Merge from Task: conflict resolved callback", ex);
            }

            FireAndForget(AfterConflictResolvedAsync);
            return Task.FromResult(0);
        }

        private async Task AfterConflictResolvedAsync()
        {
            await Task.Yield();
            if (IsBusy)
            {
                // Non dovrebbe succedere (i comandi della scheda sono disabilitati mentre il resolver
                // lavora), ma RunBusyAsync salterebbe la rilettura senza dirlo.
                StatusMessage = "Conflict resolved: press Refresh to update the list and continue.";
                Log(StatusMessage);
                return;
            }
            await RunBusyAsync(RefreshConflictsAndContinueCoreAsync);
        }

        // Rilegge i conflitti; se la catena era ferma sui conflitti e non ne restano, riparte da sola
        // (stessa strada di Continue: conflitti, modifiche in sospeso, verifica di ripresa, passi).
        private async Task RefreshConflictsAndContinueCoreAsync()
        {
            await RefreshConflictsCoreAsync();

            if (Conflicts.Count > 0 || State != TaskMergeChainState.StoppedConflicts)
                return;

            if (HasStaleInput)
            {
                StatusMessage = "All conflicts resolved. The work item or target was edited after Load: restore them and click Continue, or press Load to start over.";
                Log(StatusMessage);
                return;
            }

            StatusMessage = "All conflicts resolved: continuing the merge...";
            Log(StatusMessage);
            await StartOrContinueCoreAsync();
        }

        private async Task RefreshConflictsCoreAsync()
        {
            var workspace = _chainWorkspace ?? SelectedWorkspace;
            if (workspace == null || string.IsNullOrEmpty(_targetMergePath))
                return;

            var target = _targetMergePath;
            var conflicts = await Task.Run(() => workspace.QueryConflicts(new[] { target }, true));
            // Il resolver lavora su _chainWorkspace: e' il workspace appena interrogato.
            _chainWorkspace = workspace;
            ShowConflicts(conflicts);
            Log(string.Format(CultureInfo.InvariantCulture, "Refresh conflicts: {0} open", Conflicts.Count));

            if (Conflicts.Count == 0 && State == TaskMergeChainState.StoppedConflicts)
                StatusMessage = "All conflicts resolved: click Continue.";
            else if (Conflicts.Count > 0)
                StatusMessage = string.Format(CultureInfo.InvariantCulture,
                    IsChainInProgress
                        ? "{0} conflict(s) left to resolve: the merge continues by itself after the last one."
                        : "{0} conflict(s) left to resolve: then check in that merge (Review in Pending Changes) and Start again.",
                    Conflicts.Count);
        }

        // Ripiego: pagina Resolve Conflicts di VS filtrata sul conflitto selezionato (o sul target);
        // la scheda resta aperta e "Refresh" rilegge lo stato quando l'utente ha finito.
        private void OpenInVsResolveConflicts()
        {
            try
            {
                var row = _selectedConflict;
                var path = row == null
                    ? _targetMergePath
                    : row.Conflict.YourServerItem ?? row.Conflict.TargetLocalItem ?? row.Path;
                BranchesViewModel.InvokeResolveConflictsPage(_chainWorkspace, new[] { path });
                Log("Opened VS Resolve Conflicts for " + path + ": press Refresh here when done.");
            }
            catch (Exception ex)
            {
                Log("ERROR opening Resolve Conflicts: " + ex.Message);
                _logger.Error("Merge from Task: Resolve Conflicts page", ex);
                ShowError("Cannot open Resolve Conflicts: " + ex.Message);
            }
        }

        #endregion

        #region Merge policy

        // Regole di merge sul piano appena calcolato (o rilette senza catena in corso): decisione per ogni
        // passo (azione, regola, righe protette; un cambio fatto nella scheda si perde), stato "Skipped"
        // dei passi da saltare, problemi (regole non leggibili o non valide, azioni incompatibili tra i
        // passi: Start disabilitato), promemoria e riepilogo. keepExecutedSteps (regole rilette su un
        // piano gia' in uso): i passi gia' eseguiti (vedi IsPolicyFrozen) tengono azione, regola e righe
        // protette con cui sono stati fusi, cosi' la lista e i promemoria descrivono cio' che e' stato fatto.
        private void ApplyPolicy(EffectiveMergePolicy policy, string description, string loadError, bool keepExecutedSteps = false)
        {
            _policy = policy;
            _policyLoadErrors.Clear();
            if (!string.IsNullOrEmpty(loadError))
            {
                const string prefix = "Merge policies: ";
                var reason = loadError.TrimEnd('.', ' ');
                if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    reason = reason.Substring(prefix.Length);
                _policyLoadErrors.Add("The merge policies could not be read (" + reason + "): fix them (Policies...), then Load again.");
            }
            PolicyDescriptionText = description;

            foreach (var step in Steps)
            {
                if (keepExecutedSteps && IsPolicyFrozen(step))
                    continue;

                var action = MergePolicyAction.Merge;
                EffectivePathRule rule = null;
                IReadOnlyList<EffectiveLineRule> lineRules = null;
                if (policy != null)
                {
                    try
                    {
                        var path = PolicyPathOf(step);
                        var decision = MergePolicyEngine.Decide(policy, path);
                        if (decision != null)
                        {
                            action = decision.Action;
                            rule = decision.MatchedRule;
                            // Le righe protette valgono per il Merge. Decide non le da' per un Discard/Skip,
                            // ma l'azione si puo' cambiare nella scheda: si calcolano sempre, con lo stesso
                            // criterio (regola abilitata, FilePattern che corrisponde al path).
                            var candidates = action == MergePolicyAction.Merge ? decision.LineRules : policy.LineRules;
                            lineRules = candidates == null
                                ? null
                                : candidates
                                    .Where(r => r != null && r.Rule != null && r.Rule.Enabled && MergePolicyEngine.GlobMatch(r.Rule.FilePattern, path))
                                    .ToList()
                                    .AsReadOnly();
                        }
                    }
                    catch (Exception ex)
                    {
                        _policyLoadErrors.Add(string.Format(CultureInfo.InvariantCulture,
                            "The merge policies cannot decide step #{0} ({1}): {2}", step.Number, step.RelativePath, ex.Message));
                    }
                }
                step.SetPolicyDecision(action, rule, lineRules);
                step.ActionChangedWhilePending = false;
                SyncPolicyStatus(step);
            }

            RefreshPolicyState();
        }

        // Se fra le modifiche in sospeso c'e' il file di team delle regole di merge (salvato dalla scheda
        // Merge Policies): come sbloccare. Vuoto altrimenti.
        private static string TeamPolicyFileHint(IEnumerable<PendingChange> pending)
        {
            var file = (pending ?? Enumerable.Empty<PendingChange>())
                .FirstOrDefault(p => p != null && !p.IsMerge && MergePolicyStore.IsTeamFile(p.ServerItem));
            return file == null
                ? string.Empty
                : " One of them is the team merge policy file (" + file.ServerItem + ", saved from Merge Policies): check it in on its own, or undo it, then continue here.";
        }

        // Passo gia' eseguito con la sua azione: fuso (anche Discard), in corso, con conflitti, o con un
        // merge in sospeso della catena. Le regole rilette non lo cambiano.
        private static bool IsPolicyFrozen(TaskMergeStepViewModel step)
        {
            return step.MergedInSession
                || step.Status == TaskMergeStepStatus.Merged
                || step.Status == TaskMergeStepStatus.Merging
                || step.Status == TaskMergeStepStatus.Conflicts;
        }

        // Path del passo relativo alla radice del ramo target, con '/' (come lo vogliono le regole).
        private string PolicyPathOf(TaskMergeStepViewModel step)
        {
            var root = (_targetPath ?? string.Empty).TrimEnd('/');
            var item = (step.TargetItem ?? string.Empty).TrimEnd('/');
            if (root.Length > 0 && string.Equals(item, root, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (root.Length > 0 && item.Length > root.Length + 1
                && item.StartsWith(root, StringComparison.OrdinalIgnoreCase) && item[root.Length] == '/')
                return item.Substring(root.Length + 1);
            var relative = step.RelativePath ?? string.Empty;
            return relative == "." ? string.Empty : relative;
        }

        // Azione di esecuzione di un passo del planner (Merge se non e' nel piano).
        private MergePolicyAction ActionOf(TaskMergeStep step)
        {
            TaskMergeStepViewModel row;
            return step != null && _stepViewModels.TryGetValue(step, out row) ? row.PolicyAction : MergePolicyAction.Merge;
        }

        private TaskMergeStepViewModel RowOf(TaskMergeStep step)
        {
            TaskMergeStepViewModel row;
            return step != null && _stepViewModels.TryGetValue(step, out row) ? row : null;
        }

        // Stato di un passo che la catena non ha toccato, dopo una decisione o un cambio di azione:
        // Skip -> "Skipped"; da Skip a Merge/Discard -> da fare (la catena lo rivede con l'anteprima di
        // TFVC prima del merge). I passi gia' fusi (o gia' dentro) non cambiano.
        private void SyncPolicyStatus(TaskMergeStepViewModel step)
        {
            if (step.MergedInSession)
                return;
            if (step.PolicyAction == MergePolicyAction.Skip)
            {
                if (step.Status == TaskMergeStepStatus.Pending || step.Status == TaskMergeStepStatus.Blocked
                    || step.Status == TaskMergeStepStatus.Failed || step.Status == TaskMergeStepStatus.Skipped)
                    MarkSkipped(step);
            }
            else if (step.Status == TaskMergeStepStatus.Skipped)
            {
                step.Status = TaskMergeStepStatus.Pending;
                step.Details = "not previewed yet (its action changed): TFVC previews it before merging";
            }
        }

        private static void MarkSkipped(TaskMergeStepViewModel step)
        {
            step.Status = TaskMergeStepStatus.Skipped;
            var rule = step.PolicyRule == null || step.PolicyRule.Rule == null ? null : step.PolicyRule.Rule.Id;
            step.Details = step.IsPolicyOverridden || string.IsNullOrEmpty(rule)
                ? "skipped (set in this tab): not merged, TFVC is not called"
                : "skipped by the merge policy (rule " + rule + "): not merged, TFVC is not called";
        }

        // Dopo ogni decisione o cambio di azione: righe protette per il resolver, problemi (regole e
        // azioni incompatibili tra i passi), promemoria e riepilogo.
        private void RefreshPolicyState()
        {
            _lineRulesByTarget.Clear();
            foreach (var step in Steps)
            {
                var rules = step.ProtectedLineRules;
                if (rules.Count > 0 && !string.IsNullOrEmpty(step.TargetItem))
                    _lineRulesByTarget[step.TargetItem.TrimEnd('/')] = rules;
            }

            PolicyErrors.Clear();
            foreach (var error in _policyLoadErrors)
                PolicyErrors.Add(error);
            if (_policy != null && _policy.Errors != null)
            {
                foreach (var error in _policy.Errors.Where(e => !string.IsNullOrEmpty(e)))
                    PolicyErrors.Add("Merge policy: " + error);
            }
            if (_plan != null && _plan.IsValid && Steps.Count > 0)
            {
                IReadOnlyList<string> actionErrors;
                try
                {
                    actionErrors = MergePolicyEngine.ValidateStepActions(_plan, ActionOf);
                }
                catch (Exception ex)
                {
                    actionErrors = new List<string> { "the actions of the steps could not be checked (" + ex.Message + ")" };
                }
                foreach (var error in (actionErrors ?? new List<string>()).Where(e => !string.IsNullOrEmpty(e)))
                    PolicyErrors.Add("Merge policy: " + error);
            }

            RebuildFollowUps();
            UpdatePolicySummary();
            RaisePlanProperties();
            StartCommand.RaiseCanExecuteChanged();
        }

        // "Merge policies: 3 to discard · 1 skipped · 5 files with protected lines"
        private void UpdatePolicySummary()
        {
            if (Steps.Count == 0)
            {
                PolicySummaryText = null;
                return;
            }

            var parts = new List<string>();
            var discard = Steps.Count(s => s.PolicyAction == MergePolicyAction.Discard);
            var skip = Steps.Count(s => s.PolicyAction == MergePolicyAction.Skip);
            var protectedFiles = Steps.Count(s => s.HasProtectedLines);
            var changed = Steps.Count(s => s.IsPolicyOverridden);
            if (discard > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} to discard", discard));
            if (skip > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} skipped", skip));
            if (protectedFiles > 0)
                parts.Add(TaskMergeText.Count(protectedFiles, "file") + " with protected lines");
            if (changed > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} changed in this tab", changed));
            if (PolicyErrors.Count > 0)
                parts.Add(TaskMergeText.Count(PolicyErrors.Count, "problem"));

            PolicySummaryText = "Merge policies: " + (parts.Count == 0 ? "every step is merged normally" : string.Join(" · ", parts));
        }

        // Promemoria "Manual follow-ups": per file, cio' che le regole di merge lasciano da fare a mano.
        private void RebuildFollowUps()
        {
            var items = new List<TaskMergeFollowUpViewModel>();
            foreach (var step in Steps)
            {
                var kept = step.KeptTargetDifferences;
                if (kept.Count > 0)
                {
                    items.Add(new TaskMergeFollowUpViewModel(TaskMergeFollowUpKind.ProtectedLinesKept, step.Number, step.RelativePath,
                        string.Format(CultureInfo.InvariantCulture,
                            "{0} of the task on protected lines not merged: the target's version is kept. Apply by hand what is needed.",
                            TaskMergeText.Count(kept.Count, "change")),
                        kept.Select(DescribeDifference)));
                }
                if (!string.IsNullOrEmpty(step.NewFileProtectedNote))
                {
                    items.Add(new TaskMergeFollowUpViewModel(TaskMergeFollowUpKind.NewFileWithProtectedLines, step.Number, step.RelativePath,
                        step.NewFileProtectedNote + ".", null));
                }
                if (step.PolicyAction == MergePolicyAction.Discard && step.Status != TaskMergeStepStatus.AlreadyMerged)
                {
                    items.Add(new TaskMergeFollowUpViewModel(TaskMergeFollowUpKind.Discarded, step.Number, step.RelativePath,
                        string.Format(CultureInfo.InvariantCulture,
                            step.Status == TaskMergeStepStatus.Merged
                                ? "discarded: recorded as merged, the target content is kept; the task changes ({0}) are not in the target."
                                : "to be discarded: it will be recorded as merged with the target content kept; the task changes ({0}) will not be in the target.",
                            step.RangeText),
                        null));
                }
                if (step.PolicyAction == MergePolicyAction.Skip && step.Status == TaskMergeStepStatus.Skipped)
                {
                    items.Add(new TaskMergeFollowUpViewModel(TaskMergeFollowUpKind.Skipped, step.Number, step.RelativePath,
                        string.Format(CultureInfo.InvariantCulture,
                            "skipped: the task changes ({0}) are not merged and TFVC does not record them as merged.", step.RangeText),
                        null));
                }
            }

            FollowUps.Clear();
            foreach (var item in items)
                FollowUps.Add(item);
            OnPropertyChanged("HasFollowUps");
            OnPropertyChanged("FollowUpsHeaderText");
            CopyFollowUpsCommand.RaiseCanExecuteChanged();
        }

        private static string DescribeDifference(ProtectedLineDifference difference)
        {
            if (difference == null)
                return null;
            if (!string.IsNullOrEmpty(difference.Summary))
                return difference.Summary;
            return string.Format(CultureInfo.InvariantCulture, "{0}: task {1} / target {2}",
                string.IsNullOrEmpty(difference.RuleId) ? "protected line" : difference.RuleId,
                difference.SourceText == null ? "(none)" : "\"" + difference.SourceText.Trim() + "\"",
                difference.TargetText == null ? "(none)" : "\"" + difference.TargetText.Trim() + "\"");
        }

        // Riga di riepilogo delle regole per il commento di check-in (null se non hanno agito sui passi).
        private string PolicyCommentLine(IEnumerable<TaskMergeStep> steps)
        {
            var rows = (steps ?? Enumerable.Empty<TaskMergeStep>()).Select(RowOf).Where(r => r != null).Distinct().ToList();
            var discarded = rows.Count(r => r.PolicyAction == MergePolicyAction.Discard && r.Status == TaskMergeStepStatus.Merged);
            var skipped = rows.Count(r => r.PolicyAction == MergePolicyAction.Skip && r.Status == TaskMergeStepStatus.Skipped);
            var kept = rows.Count(r => r.KeptTargetDifferences.Count > 0);
            var newFiles = rows.Count(r => !string.IsNullOrEmpty(r.NewFileProtectedNote));

            var parts = new List<string>();
            if (discarded > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} discarded (target content kept)", discarded));
            if (skipped > 0)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} skipped", skipped));
            if (kept > 0)
                parts.Add(TaskMergeText.Count(kept, "file") + " with protected lines kept from the target");
            if (newFiles > 0)
                parts.Add(TaskMergeText.Count(newFiles, "new file") + " with protected lines");
            return parts.Count == 0 ? null : "Merge policy: " + string.Join(", ", parts) + ".";
        }

        // Si puo' cambiare l'azione di un passo solo con la scheda ferma e prima che il passo sia fuso:
        // da fare, bloccato, saltato, o fallito (anche con un merge rimasto in sospeso da un tentativo
        // interrotto: la catena lo rifiuta finche' quel merge non e' annullato, vedi RunPartAsync).
        private void UpdatePolicyEditability()
        {
            var canEdit = !IsBusy && !IsResolverBusy && State != TaskMergeChainState.Running;
            foreach (var step in Steps)
            {
                step.IsPolicyEditable = canEdit
                    && (step.Status == TaskMergeStepStatus.Failed
                        || (!step.MergedInSession
                            && (step.Status == TaskMergeStepStatus.Pending
                                || step.Status == TaskMergeStepStatus.Blocked
                                || step.Status == TaskMergeStepStatus.Skipped)));
            }
        }

        // L'utente ha cambiato l'azione di un passo dal menu a tendina (thread UI).
        private void OnStepPolicyActionChanged(TaskMergeStepViewModel step)
        {
            if (step == null)
                return;

            Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: action set to {2} in this tab{3}.",
                step.Number, step.RelativePath, step.PolicyAction,
                step.IsPolicyOverridden
                    ? " (the merge policy says " + step.DecidedPolicyAction + "; this merge only)"
                    : " (as the merge policy says)"));
            if (step.MergedInSession)
            {
                // Un tentativo precedente ha lasciato un merge in sospeso: con l'azione nuova la catena non
                // riparte da quello (vedi RunPartAsync), va prima annullato.
                step.ActionChangedWhilePending = true;
                Log(string.Format(CultureInfo.InvariantCulture,
                    "Step #{0}: a merge of an earlier attempt may still be pending on {1}: undo it in Pending Changes before Continue.",
                    step.Number, step.RelativePath));
            }

            SyncPolicyStatus(step);
            RefreshPolicyState();
            // Il riepilogo del piano esiste solo dopo l'anteprima: senza, solo quello delle regole.
            if (HasPlanSummary)
                UpdatePlanSummary();
            if (!IsChainInProgress && (State == TaskMergeChainState.Ready || State == TaskMergeChainState.Completed))
                State = Steps.All(s => s.IsDone) ? TaskMergeChainState.Completed : TaskMergeChainState.Ready;
            UpdateProgress();
            RaiseStateDependentProperties();

            if (HasPolicyErrors)
            {
                StatusMessage = InconsistentActionsMessage;
                Log(StatusMessage);
            }
            else if (StatusMessage == InconsistentActionsMessage)
            {
                StatusMessage = "The actions of the steps are consistent again. " + PolicySummaryText + ".";
            }
        }

        private const string InconsistentActionsMessage =
            "The actions of the steps are not consistent: see the problems in the list below (Start / Continue is disabled until they are fixed).";

        // Evento di MergePolicyStore (dopo ogni salvataggio, da qualsiasi thread).
        private void OnPolicyChanged(object sender, EventArgs e)
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(() => FireAndForget(OnPolicyChangedCoreAsync)));
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.Error("Merge from Task: merge policy change notification", ex);
                }
                catch (Exception)
                {
                    // Niente altro da fare in un gestore di evento.
                }
            }
        }

        // Senza catena in corso le decisioni si ricalcolano con le regole nuove; con una catena in corso
        // restano quelle con cui e' partita (avviso: valgono dal prossimo Load).
        private async Task OnPolicyChangedCoreAsync()
        {
            if (_plan == null || _plannedGroup == null || _versionControl == null || Steps.Count == 0)
                return;
            if (IsBusy)
            {
                _policyChangedWhileBusy = true;
                return;
            }
            if (IsChainInProgress)
            {
                PolicyNoticeText = "The merge policies changed. The chain in progress keeps the rules it started with; the new ones apply after a new Load.";
                Log(PolicyNoticeText);
                return;
            }
            await RunBusyAsync(ReloadPolicyCoreAsync);
        }

        private async Task ReloadPolicyCoreAsync()
        {
            var plan = _plan;
            if (plan == null || IsChainInProgress)
                return;

            var versionControl = _versionControl;
            var targetRoot = _targetPath;
            StatusMessage = "The merge policies changed: reading them again...";
            var loaded = await Task.Run(() =>
            {
                var result = new PlanResult();
                try
                {
                    string description;
                    result.Policy = MergePolicyStore.LoadEffective(versionControl, targetRoot, out description);
                    result.PolicyDescription = description;
                    if (result.Policy == null)
                        result.PolicyError = string.IsNullOrEmpty(description) ? "the merge policies could not be read" : description;
                }
                catch (Exception ex)
                {
                    result.PolicyError = ex.Message;
                }
                return result;
            });

            if (!ReferenceEquals(plan, _plan) || IsChainInProgress)
                return;

            // Le azioni cambiate nella scheda restano dove la regola decide la stessa cosa di prima (es. il
            // salvataggio del file di team, che mette solo una modifica in sospeso: le regole in uso, lette a
            // Latest, non cambiano); si perdono solo dove la decisione della regola e' cambiata. I passi gia'
            // eseguiti non si toccano (ApplyPolicy con keepExecutedSteps).
            var overrides = Steps
                .Where(s => s.IsPolicyOverridden && !IsPolicyFrozen(s))
                .ToDictionary(s => s, s => Tuple.Create(s.DecidedPolicyAction, s.PolicyAction));
            ApplyPolicy(loaded.Policy, loaded.PolicyDescription, loaded.PolicyError, true);
            var overridden = 0;
            foreach (var pair in overrides)
            {
                var step = pair.Key;
                if (step.DecidedPolicyAction == pair.Value.Item1)
                {
                    step.RestorePolicyOverride(pair.Value.Item2);
                    SyncPolicyStatus(step);
                }
                else
                {
                    overridden++;
                }
            }
            if (overrides.Count > 0)
                RefreshPolicyState();
            PolicyNoticeText = null;
            if (State == TaskMergeChainState.Ready || State == TaskMergeChainState.Completed)
                State = Steps.All(s => s.IsDone) ? TaskMergeChainState.Completed : TaskMergeChainState.Ready;
            if (HasPlanSummary)
                UpdatePlanSummary();
            UpdateProgress();
            _deferLogText = true;
            try
            {
                LogPolicy();
            }
            finally
            {
                _deferLogText = false;
                FlushLog();
            }

            if (HasPolicyErrors)
            {
                ShowError("The merge policies changed and have problems: Start is disabled until they are fixed (see the list below).");
            }
            else
            {
                var frozen = Steps.Count(IsPolicyFrozen);
                StatusMessage = "The merge policies changed: the actions of the steps were computed again"
                    + (overridden > 0
                        ? string.Format(CultureInfo.InvariantCulture, " ({0} set in this tab were reset because their rule changed)", overridden)
                        : string.Empty)
                    + (frozen > 0
                        ? string.Format(CultureInfo.InvariantCulture, "; {0} already merged keep the action they were merged with", TaskMergeText.Count(frozen, "step"))
                        : string.Empty)
                    + ". " + PolicySummaryText + ".";
            }
            Log(StatusMessage);
        }

        // Pulsante "Policies...": scheda delle regole di merge (team del target caricato, o di quello
        // scritto nella casella, e personali).
        private async Task OpenPoliciesAsync()
        {
            try
            {
                var workspace = _chainWorkspace ?? SelectedWorkspace;
                await MergePolicyToolWindow.ShowAsync(PolicyTargetRoot(), workspace);
            }
            catch (Exception ex)
            {
                Log("ERROR opening the merge policies: " + ex.Message);
                _logger.Error("Merge from Task: Merge Policies window", ex);
                ShowError("Cannot open the merge policies: " + ex.Message);
            }
        }

        private string PolicyTargetRoot()
        {
            if (!string.IsNullOrEmpty(_targetPath) && _plannedGroup != null)
                return _targetPath;
            var typed = (TargetBranchText ?? string.Empty).Trim().TrimEnd('/');
            return typed.StartsWith("$/", StringComparison.Ordinal) && typed.IndexOf(',') < 0 ? typed : null;
        }

        private void CopyFollowUps()
        {
            if (FollowUps.Count == 0)
                return;
            var text = new StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture, "Manual follow-ups - Merge from Task #{0} \"{1}\" -> {2}\n",
                _workItemId, _workItemTitle, _targetPath);
            foreach (var item in FollowUps)
                text.Append(item.ToClipboardText()).Append('\n');
            try
            {
                System.Windows.Clipboard.SetText(text.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"));
                StatusMessage = TaskMergeText.Count(FollowUps.Count, "manual follow-up") + " copied to the clipboard.";
            }
            catch (Exception ex)
            {
                ShowError("Cannot copy to the clipboard: " + ex.Message);
            }
        }

        // Regole dei passi per il controllo finale, fotografate sul thread UI.
        private TaskMergeAuditPolicy CreateAuditPolicy()
        {
            return new TaskMergeAuditPolicy(
                Steps.Select(s => new KeyValuePair<TaskMergeStep, MergePolicyAction>(s.Step, s.PolicyAction)).ToList(),
                Steps.Where(s => s.HasProtectedLines)
                    .Select(s => new KeyValuePair<TaskMergeStep, IReadOnlyList<MergeLineRule>>(s.Step, s.ProtectedLineRules))
                    .ToList(),
                _discardSnapshots.ToList());
        }

        // Passo fermo con un errore: Failed, catena in StoppedFailures, messaggio nel banner e nel registro.
        private bool StopStep(TaskMergeStepViewModel step, string details, string message)
        {
            step.Status = TaskMergeStepStatus.Failed;
            step.Details = details;
            State = TaskMergeChainState.StoppedFailures;
            StatusMessage = message;
            Log(message);
            RaiseStateDependentProperties();
            UpdateProgress();
            return false;
        }

        // Passi falliti della catena il cui item non ha piu' modifiche in sospeso (undo fatto a mano):
        // tornano "non toccati", cosi' la ripresa non li aspetta e la loro azione si puo' cambiare.
        private void ReleaseUndoneFailedSteps(IReadOnlyCollection<PendingChange> pending)
        {
            foreach (var step in Steps.Where(s => s.MergedInSession && s.Status == TaskMergeStepStatus.Failed).ToList())
            {
                var stillPending = pending.Any(p => p != null
                    && (PathEquals(p.ServerItem, step.TargetItem)
                        || (step.Recursion == TaskMergeStepRecursion.Full && IsUnder(p.ServerItem, step.TargetItem))));
                if (stillPending)
                    continue;
                step.MergedInSession = false;
                step.NeedsProtectedResult = false;
                step.ActionChangedWhilePending = false;
                Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: nothing is pending for it any more (undone): it is merged again from the start.",
                    step.Number, step.RelativePath));
            }
            _chainActive = _chainActive && (HasPendingChainMerges || pending.Count > 0);
            SyncAllPolicyStatuses();
        }

        private void SyncAllPolicyStatuses()
        {
            foreach (var step in Steps)
                SyncPolicyStatus(step);
            RebuildFollowUps();
            UpdateProgress();
        }

        // Preview 0/0/0 su un passo che un tentativo precedente interrotto ha gia' messo in sospeso: con
        // le regole di merge quel merge non si da' per buono senza verifica. False = catena ferma.
        private async Task<bool> VerifyInterruptedPolicyStepAsync(TaskMergeStepViewModel step)
        {
            if (step.ActionChangedWhilePending)
                return StopStep(step, "its action changed after a merge was pended",
                    string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}): its action changed to {2} while a merge of an earlier attempt is still pending on it. Undo that pending change in Pending Changes, then Continue.",
                        step.Number, step.RelativePath, step.PolicyAction));

            if (step.PolicyAction == MergePolicyAction.Discard)
            {
                TaskMergeContentSnapshot before;
                if (!_discardSnapshots.TryGetValue(step.Step, out before))
                    return StopStep(step, "discard: the target content before the merge is not known",
                        string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}): a merge of an earlier attempt is pending, but the target content before it is not known, so the discard cannot be verified. Undo that pending change in Pending Changes, then Continue.",
                            step.Number, step.RelativePath));
                var after = await Task.Run(() => before.Recapture());
                var differences = before.Differences(after);
                if (differences.Count > 0)
                {
                    foreach (var difference in differences.Take(20))
                        Log("  discard check: " + difference);
                    return StopStep(step, "discard: the target content changed",
                        string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}) was not discarded safely: the target content changed ({2}). Undo its pending change in Pending Changes, then Continue.",
                            step.Number, step.RelativePath, differences[0]));
                }
                return true;
            }

            if (step.NeedsProtectedResult)
                return StopStep(step, "protected lines: an earlier attempt was interrupted",
                    string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}): an earlier attempt was interrupted before the result that keeps its protected lines was saved, so its pending change may contain the task's version of those lines. Undo it in Pending Changes, then Continue.",
                        step.Number, step.RelativePath));
            return true;
        }

        // Discard (come tf merge /discard): il merge si registra come fatto e il contenuto del target resta
        // quello di prima. AlwaysAcceptMine nasconde i failure di TFVC (NumFailures=0), quindi: anteprima
        // prima (gia' fatta dal chiamante: un failure ferma la catena senza toccare nulla), fotografia del
        // contenuto locale, merge, poi anteprima 0/0/0 E contenuto identico alla fotografia (per un item
        // che non c'era nel target: nessun file creato). Altrimenti stop con la spiegazione.
        // True se il passo e' fatto e verificato.
        private async Task<bool> DiscardStepAsync(Workspace workspace, TaskMergeStepViewModel step, GetStatus preview)
        {
            var recursive = step.Recursion == TaskMergeStepRecursion.Full;
            var targetItem = step.TargetItem;

            // Un tentativo precedente interrotto ha gia' messo in sospeso qualcosa: vale la fotografia di
            // allora (quella di adesso conterrebbe gia' il suo effetto).
            TaskMergeContentSnapshot before;
            if (!step.MergedInSession || !_discardSnapshots.TryGetValue(step.Step, out before))
            {
                if (step.MergedInSession)
                    return StopStep(step, "discard: the target content before the merge is not known",
                        string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}): a merge of an earlier attempt may be pending, so the target content before the discard is not known. Undo the pending change of {1} in Pending Changes, then Continue.",
                            step.Number, step.RelativePath));
                before = await Task.Run(() => TaskMergeContentSnapshot.Capture(workspace, targetItem, recursive));
                if (before.Error != null)
                    return StopStep(step, "discard: " + before.Error,
                        string.Format(CultureInfo.InvariantCulture,
                            "Step #{0} ({1}) is not discarded: the target content cannot be recorded before the merge ({2}). Nothing was changed for this step: fix the cause, then Continue.",
                            step.Number, step.RelativePath, before.Error));
                _discardSnapshots[step.Step] = before;
            }

            var wasChainActive = _chainActive;
            var wasMergedInSession = step.MergedInSession;
            step.MergedInSession = true;
            _chainActive = true;
            RaiseStateDependentProperties();

            StatusMessage = string.Format(CultureInfo.InvariantCulture, "Discarding step {0} of {1}: {2} ({3})...",
                step.Number, Steps.Count, step.RelativePath, step.RangeText);
            var source = step.SourceItem;
            var from = step.FromChangesetId;
            var to = step.ToChangesetId;
            var recursion = recursive ? RecursionType.Full : RecursionType.None;
            var status = await Task.Run(() => workspace.Merge(source, targetItem,
                new ChangesetVersionSpec(from), new ChangesetVersionSpec(to), LockLevel.None, recursion,
                Microsoft.TeamFoundation.VersionControl.Common.MergeOptionsEx.AlwaysAcceptMine));
            Log(string.Format(CultureInfo.InvariantCulture, "Discard step #{0} {1} {2} (AlwaysAcceptMine): {3}",
                step.Number, step.RelativePath, step.RangeText, FormatStatus(status)));
            LogFailures("Discard step #" + step.Number, status);

            // Verifica: TFVC da' il passo per fuso E il contenuto locale non e' cambiato.
            GetStatus check = null;
            string checkError = null;
            try
            {
                check = await PreviewStepAsync(workspace, step);
                Log(string.Format(CultureInfo.InvariantCulture, "Discard check step #{0}: {1}", step.Number, FormatStatus(check)));
            }
            catch (Exception ex) when (IsVersionControlException(ex))
            {
                LogStepException("Discard check", step, ex);
                checkError = ex.Message;
            }
            var after = await Task.Run(() => before.Recapture());
            var differences = before.Differences(after);

            var problems = new List<string>();
            if (status.NumFailures > 0)
                problems.Add("TFVC reported " + FailureSummary(status));
            if (status.NumConflicts > 0)
                problems.Add(TaskMergeText.Count(status.NumConflicts, "conflict") + " in the merge");
            if (checkError != null)
                problems.Add("the check preview failed (" + checkError + ")");
            else if (check.NumOperations != 0 || check.NumConflicts != 0 || check.NumFailures != 0)
                problems.Add("TFVC does not treat the step as merged afterwards (" + FormatStatus(check) + ")");
            if (differences.Count > 0)
                problems.Add("the target content changed (" + differences[0] + (differences.Count > 1 ? " and more" : string.Empty) + ")");

            if (problems.Count == 0)
            {
                step.Status = TaskMergeStepStatus.Merged;
                step.Details = string.Format(CultureInfo.InvariantCulture,
                    "discarded: recorded as merged, the target content is unchanged (verified; {0} operations)", status.NumOperations);
                RebuildFollowUps();
                UpdateProgress();
                return true;
            }

            foreach (var difference in differences.Take(20))
                Log("  discard check: " + difference);

            // Cosa e' rimasto in sospeso di questo passo?
            var left = await Task.Run(() => (workspace.GetPendingChanges(targetItem, recursion) ?? new PendingChange[0]).Length);
            if (left == 0)
            {
                step.MergedInSession = wasMergedInSession;
                _chainActive = wasChainActive || HasPendingChainMerges;
            }
            return StopStep(step, "discard not verified: " + string.Join("; ", problems),
                left == 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}) could not be discarded: {2}. Nothing is pending for it: see the log, fix the cause (or change its policy), then Continue.",
                        step.Number, step.RelativePath, string.Join("; ", problems))
                    : string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}) was not discarded safely: {2}. Its pending change stays under the target: undo it in Pending Changes, then Continue (or change its policy after the undo).",
                        step.Number, step.RelativePath, string.Join("; ", problems)));
        }

        // Prima del merge di un file con righe protette: versioni lette (base, sorgente, target) e
        // risultato che tiene le righe protette del target, oppure il motivo per fermarsi. Nessuna
        // modifica al workspace.
        private async Task<ProtectedMergePreparation> PrepareProtectedMergeAsync(Workspace workspace, TaskMergeStepViewModel step, GetStatus preview)
        {
            var planStep = step.Step;
            var rules = step.ProtectedLineRules;
            var expectsConflicts = preview.NumConflicts > 0;
            var folder = TaskMergeFileContent.NewTempFolder("policy");
            StatusMessage = string.Format(CultureInfo.InvariantCulture, "Step {0} of {1}: reading the versions to keep the protected lines of {2}...",
                step.Number, Steps.Count, step.RelativePath);
            try
            {
                return await Task.Run(() => TaskMergeProtectedLines.Prepare(workspace, planStep, rules, expectsConflicts, folder));
            }
            finally
            {
                TaskMergeFileContent.DeleteFolder(folder);
            }
        }

        // Dopo il merge di TFVC di un file la cui sorgente cambia righe protette: il risultato e' quello
        // calcolato. Conflitto di TFVC e risultato senza conflitti -> risolto con il nostro file
        // (AcceptMerge) e verificato; risultato con conflitti -> il conflitto resta al resolver, che parte
        // dal nostro testo con i marker; nessun conflitto di TFVC -> il file in sospeso si riscrive con il
        // nostro risultato e si verifica. False = catena ferma (il messaggio dice cosa fare).
        private async Task<bool> ApplyProtectedResultAsync(Workspace workspace, TaskMergeStepViewModel step, GetStatus status,
            ProtectedMergePreparation prepared, string partText)
        {
            var planStep = step.Step;
            var result = prepared.Result;
            step.KeptTargetDifferences = result.KeptTargetDifferences;
            step.NeedsProtectedResult = true;
            var kept = step.KeptTargetDifferences.Count;
            var keptText = kept > 0
                ? "; " + TaskMergeText.Count(kept, "change") + " of the task on protected lines not merged (see Manual follow-ups)"
                : string.Empty;

            if (status.NumConflicts > 0)
            {
                if (result.HasConflicts)
                {
                    // Lo decide l'utente nel resolver, che parte dal nostro risultato (LineRulesForConflict);
                    // il controllo finale verifica poi le righe protette.
                    step.NeedsProtectedResult = false;
                    step.Status = TaskMergeStepStatus.Conflicts;
                    step.Details = string.Format(CultureInfo.InvariantCulture,
                        "{0} operations, {1} to resolve in the editor (it starts from the result that keeps the protected lines){2}",
                        status.NumOperations, TaskMergeText.Count(result.Merge.ConflictCount, "conflict"), keptText);
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} {1}: the result that keeps the protected lines has {2}: left open, continuing with the next steps{3}.",
                        step.Number, step.RelativePath, TaskMergeText.Count(result.Merge.ConflictCount, "conflict"), partText));
                    RebuildFollowUps();
                    UpdateProgress();
                    return true;
                }

                var bytes = prepared.ResultBytes;
                var folder = TaskMergeFileContent.NewTempFolder("policy");
                ProtectedResolveOutcome outcome;
                try
                {
                    outcome = await Task.Run(() => TaskMergeProtectedLines.ResolveConflictWithResult(workspace, planStep, bytes, folder));
                }
                finally
                {
                    TaskMergeFileContent.DeleteFolder(folder);
                }

                if (outcome.Verified)
                {
                    step.NeedsProtectedResult = false;
                    step.Status = TaskMergeStepStatus.Merged;
                    step.Details = string.Format(CultureInfo.InvariantCulture,
                        "{0} operations; conflict resolved with the result that keeps the protected lines (verified){1}", status.NumOperations, keptText);
                    Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: conflict resolved with the result that keeps the protected lines (verified).",
                        step.Number, step.RelativePath));
                    RebuildFollowUps();
                    UpdateProgress();
                    return true;
                }

                if (!outcome.Resolved)
                {
                    // Il conflitto resta aperto: si risolve nel resolver, che parte dallo stesso risultato.
                    step.NeedsProtectedResult = false;
                    step.Status = TaskMergeStepStatus.Conflicts;
                    step.Details = string.Format(CultureInfo.InvariantCulture,
                        "{0} operations, conflict to resolve in the editor (automatic resolution with the protected lines not possible: {1}){2}",
                        status.NumOperations, outcome.Message, keptText);
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} {1}: the conflict could not be resolved with the result that keeps the protected lines ({2}): left open for the editor{3}.",
                        step.Number, step.RelativePath, outcome.Message, partText));
                    RebuildFollowUps();
                    UpdateProgress();
                    return true;
                }

                RebuildFollowUps();
                return StopStep(step, "protected lines: " + outcome.Message,
                    string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}): {2}. Undo its pending change in Pending Changes, then Continue.",
                        step.Number, step.RelativePath, outcome.Message));
            }

            if (result.HasConflicts)
            {
                RebuildFollowUps();
                return StopStep(step, "protected lines: TFVC merged without conflicts, the result that keeps them has overlapping changes",
                    string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}): TFVC merged it without conflicts, but the result that keeps its protected lines has overlapping changes to resolve by hand, and its pending change contains the task's version of those lines. Undo it in Pending Changes, then Continue (after the undo you can set the step to Skip or Discard and merge the file by hand).",
                        step.Number, step.RelativePath));
            }

            string error;
            try
            {
                var bytes = prepared.ResultBytes;
                error = await Task.Run(() => TaskMergeProtectedLines.WriteResult(workspace, planStep, bytes));
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            if (error != null)
            {
                RebuildFollowUps();
                return StopStep(step, "protected lines: " + error,
                    string.Format(CultureInfo.InvariantCulture,
                        "Step #{0} ({1}) is merged by TFVC, but the result that keeps its protected lines could not be saved ({2}), so its pending change contains the task's version of those lines. Undo it in Pending Changes, then Continue.",
                        step.Number, step.RelativePath, error));
            }

            step.NeedsProtectedResult = false;
            step.Status = TaskMergeStepStatus.Merged;
            step.Details = string.Format(CultureInfo.InvariantCulture,
                "{0} operations; protected lines kept from the target (result saved and verified){1}", status.NumOperations, keptText);
            Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: saved the result that keeps the protected lines (verified){2}.",
                step.Number, step.RelativePath, keptText));
            RebuildFollowUps();
            UpdateProgress();
            return true;
        }

        // Dopo il merge di un file nuovo nel target con regole di righe protette: se contiene righe
        // protette va nel promemoria (si porta intero, per regola).
        private async Task CheckNewFileProtectedLinesAsync(Workspace workspace, TaskMergeStepViewModel step)
        {
            var planStep = step.Step;
            var rules = step.ProtectedLineRules;
            var note = await Task.Run(() => TaskMergeProtectedLines.DescribeNewFile(workspace, planStep, rules));
            step.NewFileProtectedNote = note;
            if (note == null)
                return;
            Log(string.Format(CultureInfo.InvariantCulture, "Step #{0} {1}: {2}.", step.Number, step.RelativePath, note));
            RebuildFollowUps();
        }

        #endregion

        #region Pending Changes

        // Review in Pending Changes: prima il controllo finale (A3) su dati riletti adesso da TFVC; se non
        // e' pulito Pending Changes NON si apre precompilato e la scheda spiega perche'. Al punto di
        // check-in (o a catena finita) la scheda aspetta poi il check-in fatto li' (CommitCheckin).
        private async Task ReviewPendingChangesCoreAsync()
        {
            var workspace = _chainWorkspace ?? SelectedWorkspace;
            var target = _targetMergePath;
            if (workspace == null || string.IsNullOrEmpty(target))
                return;

            if (_plan == null || !_plan.IsValid)
            {
                ShowError("There is no valid plan to check the pending changes against: press Load first.");
                return;
            }

            // Al punto di check-in (o a catena finita) si consegnano i passi della parte. Con una catena
            // ferma altrove (conflitti, failure...) ma con merge in sospeso, i passi che la catena ha
            // messo in sospeso: sono quelli che un check-in da Pending Changes consegnerebbe (anche di
            // una parte a meta'), non quelli di una parte gia' archiviata. Senza catena, i passi del
            // piano che i pending coprono davvero (item e intervallo delle sorgenti del merge).
            Func<IReadOnlyList<PendingChange>, IReadOnlyList<TaskMergeStep>> stepsToDeliver;
            IReadOnlyCollection<int> allowed;
            if (ShowCheckInActions)
            {
                var partNumber = CheckInPartNumber();
                var steps = StepsToDeliverForCheckIn(partNumber, IsPartialCheckIn).AsReadOnly();
                stepsToDeliver = p => steps;
                allowed = DeliveredChangesetIds(steps, partNumber);
            }
            else if (HasPendingChainMerges)
            {
                var steps = Steps.Where(s => s.MergedInSession).Select(s => s.Step).ToList().AsReadOnly();
                stepsToDeliver = p => steps;
                allowed = DeliveredChangesetIds(steps, steps.Max(s => s.Part));
            }
            else
            {
                var all = Steps.Select(s => s.Step).ToList().AsReadOnly();
                stepsToDeliver = p => TaskMergePendingSteps.CoveredBy(all, TaskMergeAuditDataSource.ToAuditPendingList(p));
                allowed = PlannedChangesetIds().ToList().AsReadOnly();
            }

            var audit = await RunAuditAsync(workspace, target, allowed, stepsToDeliver);
            if (!audit.IsClean)
            {
                ReportAuditFailure(audit, false);
                return;
            }
            ShowAuditNotices(audit);

            if (audit.Pending.Length == 0)
            {
                StatusMessage = "Nothing is pending under the target: there is nothing to review.";
                Log(StatusMessage);
                return;
            }

            // Con una catena in corso sotto il target ci sono solo i suoi merge (D7).
            if (HasPendingChainMerges && !CheckAllFromChain(audit.Pending, "Pending Changes not opened"))
                return;

            if (OpenPendingChangesPrefilled(workspace, target, audit.Pending, ReviewComment()))
                StatusMessage = audit.Result.Summary + " Pending Changes is open with the comment and the work item"
                    + (ShowCheckInActions
                        ? (State == TaskMergeChainState.StoppedCheckpoint
                            ? ": after the check-in there the chain continues by itself."
                            : ": the chain is finished after the check-in there.")
                        : ".");
        }

        // Come BranchesViewModel.OpenPendingChanges: Pending Changes con commento, le modifiche indicate
        // (gia' controllate) selezionate, workspace della catena e work item associato. Al punto di
        // check-in (o a catena finita) la scheda aspetta il check-in fatto li' (CommitCheckin) e poi
        // prosegue da sola. Thread UI.
        private bool OpenPendingChangesPrefilled(Workspace workspace, string target, PendingChange[] pendingChanges, string comment)
        {
            var teamExplorer = _serviceProvider == null ? null : _serviceProvider.GetService(typeof(ITeamExplorer)) as ITeamExplorer;
            if (teamExplorer == null)
            {
                ShowError("Team Explorer is not available.");
                return false;
            }

            var page = teamExplorer.NavigateToPage(new Guid(TeamExplorerPageIds.PendingChanges), null) as TeamExplorerPageBase;
            var model = page == null ? null : page.Model as IPendingCheckin;
            if (model == null)
            {
                ShowError("Cannot open Pending Changes.");
                return false;
            }

            model.PendingChanges.Comment = comment;
            model.PendingChanges.CheckedPendingChanges = pendingChanges;
            ReapplyCommentWhenPageSettles(model, comment);

            if (Workspaces.Count > 1)
            {
                var workspaceProperty = model.GetType().GetProperty("Workspace");
                if (workspaceProperty != null && workspaceProperty.CanWrite)
                    workspaceProperty.SetValue(model, workspace);
            }

            if (_workItemId > 0)
            {
                var method = model.GetType().GetMethod("AddWorkItemsByIdAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (method != null)
                    method.Invoke(model, new object[] { new[] { _workItemId }, 1 /* Add */ });
            }

            Log(string.Format(CultureInfo.InvariantCulture, "Opened Pending Changes: {0} merge change(s), comment: {1}",
                pendingChanges.Length, comment));

            if (ShowCheckInActions)
            {
                _reviewWaiting = true;
                SubscribeCommitCheckin(target);
                Log(State == TaskMergeChainState.StoppedCheckpoint
                    ? "Waiting for the check-in from Pending Changes: the chain continues by itself once no merge is pending under the target."
                    : "Waiting for the check-in from Pending Changes: the chain is finished once no merge is pending under the target.");
            }
            return true;
        }

        // In VS 2026 la pagina Pending Changes finisce di caricarsi in modo asincrono dopo NavigateToPage
        // e in quel momento azzera il commento appena impostato (il work item associato invece resta: visto
        // nella prova sul 128458). Si riapplica il commento alcune volte nei primi secondi, SOLO se nel
        // frattempo e' vuoto: mai sopra a quello che l'utente ha gia' scritto. Thread UI (le continuazioni
        // di await tornano sul dispatcher).
        private static async void ReapplyCommentWhenPageSettles(IPendingCheckin model, string comment)
        {
            try
            {
                foreach (var delay in new[] { 200, 500, 1000, 2000 })
                {
                    await Task.Delay(delay);
                    if (string.IsNullOrEmpty(model.PendingChanges.Comment))
                        model.PendingChanges.Comment = comment;
                }
            }
            catch (Exception)
            {
                // Solo un aiuto: se la pagina non lo accetta, il commento resta da scrivere a mano.
            }
        }

        #endregion
    }
}
