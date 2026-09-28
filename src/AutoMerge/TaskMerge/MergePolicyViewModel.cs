using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutoMerge.Prism;
using AutoMerge.Prism.Command;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
// Microsoft.VisualStudio.Shell ha anche una classe Task (task list): qui Task e' sempre quello di TPL.
using Task = System.Threading.Tasks.Task;

namespace AutoMerge
{
    public enum MergePolicyRuleKind
    {
        Path,
        Line
    }

    // Voce di una ComboBox della scheda: valore + testo mostrato.
    public sealed class MergePolicyOption<T>
    {
        public MergePolicyOption(T value, string text)
        {
            Value = value;
            Text = text;
        }

        public T Value { get; private set; }
        public string Text { get; private set; }
    }

    // View model della scheda "Merge Policies" (MergePolicyToolWindow). Mostra e modifica le regole di
    // merge di un ramo di destinazione:
    // - file di TEAM (MergePolicyStore.TeamFileName nella radice del ramo): le modifiche restano in memoria
    //   finche' non si preme "Save to team file", che scrive il file nel workspace e mette in sospeso
    //   un'aggiunta/modifica (MAI un check-in). Se il file ha gia' un'aggiunta/modifica in sospeso si
    //   modifica quella versione locale (altrimenti un secondo salvataggio perderebbe il primo);
    // - file PERSONALE: ogni modifica si salva subito; vince sempre su quello di team.
    // L'elenco mostra le regole EFFETTIVE (EffectiveMergePolicy: ordine di valutazione e origine) piu'
    // quelle non applicate (sostituite da una personale con lo stesso Id, o scartate per un errore),
    // cosi' ogni regola dei due file resta visibile e modificabile.
    //
    // Sicurezza: prima di sovrascrivere un file si controlla che nessuno l'abbia cambiato dopo la
    // lettura (file personale: testo; file di team: changeset sul server o testo del file in sospeso);
    // un file che non si puo' leggere non si modifica da qui (niente sovrascritture alla cieca).
    //
    // Threading: i command handler partono sul thread UI; ogni chiamata TFS va in Task.Run e le
    // proprieta'/collezioni bindate si aggiornano solo dopo l'await (di nuovo sul thread UI).
    public sealed class MergePolicyViewModel : BindableBase
    {
        private const string MessageTitle = "Merge Policies";

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

        // Connessione e ramo caricati.
        private Workspace _workspace;
        private VersionControlServer _vcs;
        private string _loadedTarget;

        // File di team: documento in modifica (null = non disponibile), da dove viene e se e' cambiato.
        private MergePolicyDocument _teamDoc;
        private bool _teamLoaded;
        private string _teamError;
        private int _teamChangeset;
        private bool _teamExistsOnServer;
        private MergePolicyTeamFileState _teamState;
        private bool _teamFromPending;
        private string _teamPendingSnapshot;

        // File personale: documento (null = file assente) e testo letto/scritto per ultimo.
        private MergePolicyDocument _personalDoc;
        private bool _personalLoaded;
        private string _personalError;
        private string _personalSnapshot;

        private EffectiveMergePolicy _effective;
        private bool _addTargetChosen;

        // Richiesta di apertura arrivata mentre la scheda lavorava: si esegue alla fine.
        private bool _hasPendingOpen;
        private string _pendingOpenTarget;
        private Workspace _pendingOpenWorkspace;

        public MergePolicyViewModel(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            PathRules = new ObservableCollection<MergePolicyRuleRowViewModel>();
            LineRules = new ObservableCollection<MergePolicyRuleRowViewModel>();
            PolicyErrors = new ObservableCollection<string>();
            TestLineRules = new ObservableCollection<string>();

            AddTargetOptions = new List<MergePolicyOption<MergeRuleOrigin>>
            {
                new MergePolicyOption<MergeRuleOrigin>(MergeRuleOrigin.Personal, "Personal file (saved immediately)"),
                new MergePolicyOption<MergeRuleOrigin>(MergeRuleOrigin.Team, "Team file (saved with Save to team file)")
            };
            _addTarget = MergeRuleOrigin.Personal;

            LoadCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(ReloadCoreAsync), () => !IsBusy);
            SaveTeamCommand = DelegateCommand.FromAsyncHandler(() => RunBusyAsync(SaveTeamCoreAsync), () => CanSaveTeam);
            OpenPersonalFileCommand = new DelegateCommand(OpenPersonalFile, () => !IsBusy);
            AddPathRuleCommand = new DelegateCommand(() => OpenNewRuleEditor(MergePolicyRuleKind.Path), () => CanAddRules);
            AddLineRuleCommand = new DelegateCommand(() => OpenNewRuleEditor(MergePolicyRuleKind.Line), () => CanAddRules);
            ShowNuGetPromptCommand = new DelegateCommand(ShowNuGetPrompt, () => CanAddRules);
            ApplyNuGetPresetCommand = new DelegateCommand(ApplyNuGetPreset, () => !IsBusy && IsNuGetPromptOpen);
            CancelNuGetPresetCommand = new DelegateCommand(CloseNuGetPrompt);
            AddCentralPackageManagementPresetCommand = new DelegateCommand(
                () => AddPreset(SafePreset(MergePolicyEngine.CentralPackageManagementPreset), "Central Package Management preset"),
                () => CanAddRules);
            AddAssemblyVersionPresetCommand = new DelegateCommand(
                () => AddPreset(SafePreset(MergePolicyEngine.AssemblyVersionPreset), "Assembly versions preset"),
                () => CanAddRules);
            ApplyEditorCommand = new DelegateCommand(ApplyEditor, () => !IsBusy && Editor != null && !Editor.HasErrors);
            CancelEditorCommand = new DelegateCommand(CloseEditor, () => Editor != null);

            StatusMessage = "Open this tab from Merge from Task (Policies...), or enter a target branch and press Load.";
            UpdateTeamTexts();
            UpdatePersonalTexts();

            // Scheda ripristinata da VS senza passare da Merge from Task: almeno le regole personali si
            // leggono subito. Se nel frattempo arriva Open, questa lettura si salta (o Open aspetta la fine).
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsBusy && !_personalLoaded && _personalError == null && _loadedTarget == null)
                    FireAndForget(() => RunBusyAsync(LoadCoreAsync));
            }));
        }

        #region Bindable properties

        public ObservableCollection<MergePolicyRuleRowViewModel> PathRules { get; private set; }
        public ObservableCollection<MergePolicyRuleRowViewModel> LineRules { get; private set; }
        public ObservableCollection<string> PolicyErrors { get; private set; }
        public ObservableCollection<string> TestLineRules { get; private set; }
        public IReadOnlyList<MergePolicyOption<MergeRuleOrigin>> AddTargetOptions { get; private set; }

        public string TargetBranchText
        {
            get { return _targetBranchText; }
            set { SetProperty(ref _targetBranchText, value); }
        }
        private string _targetBranchText;

        public string LoadedTargetText
        {
            get { return _loadedTarget ?? "(no target branch)"; }
        }

        public string WorkspaceText
        {
            get
            {
                if (_workspace != null)
                    return _workspace.Name;
                return _loadedTarget == null ? "-" : "(no workspace maps the target branch)";
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged("StatusKind");
                    RaiseCommandStates();
                }
            }
        }
        private bool _isBusy;

        public string StatusMessage
        {
            get { return _statusMessage; }
            private set { SetProperty(ref _statusMessage, value); }
        }
        private string _statusMessage;

        public TaskMergeStatusKind StatusKind
        {
            get { return IsBusy ? TaskMergeStatusKind.Running : _statusKind; }
        }
        private TaskMergeStatusKind _statusKind = TaskMergeStatusKind.Info;

        // --- Team ---

        public string TeamServerPathText
        {
            get { return _teamServerPathText; }
            private set { SetProperty(ref _teamServerPathText, value); }
        }
        private string _teamServerPathText;

        public string TeamLocalPathText
        {
            get { return _teamLocalPathText; }
            private set { SetProperty(ref _teamLocalPathText, value); }
        }
        private string _teamLocalPathText;

        public string TeamStatusText
        {
            get { return _teamStatusText; }
            private set { SetProperty(ref _teamStatusText, value); }
        }
        private string _teamStatusText;

        public TaskMergeStatusKind TeamStatusKind
        {
            get { return _teamStatusKind; }
            private set { SetProperty(ref _teamStatusKind, value); }
        }
        private TaskMergeStatusKind _teamStatusKind;

        public bool IsTeamDirty
        {
            get { return _isTeamDirty; }
            private set
            {
                if (SetProperty(ref _isTeamDirty, value))
                    RaiseCommandStates();
            }
        }
        private bool _isTeamDirty;

        // Perche' "Save to team file" non e' disponibile (vuoto se lo e').
        public string TeamSaveHint
        {
            get { return _teamSaveHint; }
            private set { SetProperty(ref _teamSaveHint, value); }
        }
        private string _teamSaveHint;

        public bool CanSaveTeam
        {
            get { return !IsBusy && Editor == null && IsTeamDirty && TeamSaveBlocker() == null; }
        }

        // --- Personal ---

        public string PersonalPathText
        {
            get { return MergePolicyStore.PersonalFilePath; }
        }

        public string PersonalStatusText
        {
            get { return _personalStatusText; }
            private set { SetProperty(ref _personalStatusText, value); }
        }
        private string _personalStatusText;

        public TaskMergeStatusKind PersonalStatusKind
        {
            get { return _personalStatusKind; }
            private set { SetProperty(ref _personalStatusKind, value); }
        }
        private TaskMergeStatusKind _personalStatusKind;

        // --- Regole ---

        public bool HasPolicyErrors
        {
            get { return PolicyErrors.Count > 0; }
        }

        public bool HasPathRules
        {
            get { return PathRules.Count > 0; }
        }

        public bool HasLineRules
        {
            get { return LineRules.Count > 0; }
        }

        // Azioni sulle righe e aggiunte: non mentre la scheda lavora o un editor e' aperto (gli indici
        // delle righe devono restare quelli dell'editor).
        public bool AreRowsEnabled
        {
            get { return !IsBusy && Editor == null && !IsNuGetPromptOpen; }
        }

        public MergeRuleOrigin AddTarget
        {
            get { return _addTarget; }
            set
            {
                _addTargetChosen = true;
                if (SetProperty(ref _addTarget, value))
                {
                    OnPropertyChanged("AddTargetHint");
                    OnPropertyChanged("NuGetTargetText");
                    RaiseCommandStates();
                }
            }
        }
        private MergeRuleOrigin _addTarget;

        public string AddTargetHint
        {
            get
            {
                if (AddTarget == MergeRuleOrigin.Team && !_teamLoaded)
                    return "The team file is not available (see the Team file box): add the rules to the personal file, or load a target branch.";
                if (AddTarget == MergeRuleOrigin.Personal && !_personalLoaded)
                    return "The personal file cannot be read (see the Personal file box): fix it and press Reload.";
                return null;
            }
        }

        public bool CanAddRules
        {
            get { return AreRowsEnabled && IsDocumentAvailable(AddTarget); }
        }

        // --- Editor e modelli pronti ---

        public MergePolicyRuleEditorViewModel Editor
        {
            get { return _editor; }
            private set
            {
                var old = _editor;
                if (SetProperty(ref _editor, value))
                {
                    if (old != null)
                        old.ValidityChanged -= OnEditorValidityChanged;
                    if (value != null)
                        value.ValidityChanged += OnEditorValidityChanged;
                    OnPropertyChanged("HasEditor");
                    OnPropertyChanged("AreRowsEnabled");
                    RaiseCommandStates();
                }
            }
        }
        private MergePolicyRuleEditorViewModel _editor;

        public bool HasEditor
        {
            get { return Editor != null; }
        }

        public bool IsNuGetPromptOpen
        {
            get { return _isNuGetPromptOpen; }
            private set
            {
                if (SetProperty(ref _isNuGetPromptOpen, value))
                {
                    OnPropertyChanged("AreRowsEnabled");
                    RaiseCommandStates();
                }
            }
        }
        private bool _isNuGetPromptOpen;

        public string NuGetPrefixesText
        {
            get { return _nuGetPrefixesText; }
            set
            {
                if (SetProperty(ref _nuGetPrefixesText, value))
                    NuGetPrefixesError = null;
            }
        }
        private string _nuGetPrefixesText;

        public string NuGetPrefixesError
        {
            get { return _nuGetPrefixesError; }
            private set { SetProperty(ref _nuGetPrefixesError, value); }
        }
        private string _nuGetPrefixesError;

        public string NuGetTargetText
        {
            get { return AddTarget == MergeRuleOrigin.Team ? "the team rules" : "your personal rules"; }
        }

        // --- Tester ---

        public string TestPathText
        {
            get { return _testPathText; }
            set
            {
                if (SetProperty(ref _testPathText, value))
                    RecomputeTest();
            }
        }
        private string _testPathText;

        public bool HasTestResult
        {
            get { return _hasTestResult; }
            private set { SetProperty(ref _hasTestResult, value); }
        }
        private bool _hasTestResult;

        public string TestNormalizedPathText
        {
            get { return _testNormalizedPathText; }
            private set { SetProperty(ref _testNormalizedPathText, value); }
        }
        private string _testNormalizedPathText;

        // "Merge" / "Discard" / "Skip" / "Error": colore del risultato.
        public string TestResultKind
        {
            get { return _testResultKind; }
            private set { SetProperty(ref _testResultKind, value); }
        }
        private string _testResultKind;

        public string TestDecisionText
        {
            get { return _testDecisionText; }
            private set { SetProperty(ref _testDecisionText, value); }
        }
        private string _testDecisionText;

        public string TestRuleText
        {
            get { return _testRuleText; }
            private set { SetProperty(ref _testRuleText, value); }
        }
        private string _testRuleText;

        public string TestLineRulesHeader
        {
            get { return _testLineRulesHeader; }
            private set { SetProperty(ref _testLineRulesHeader, value); }
        }
        private string _testLineRulesHeader;

        public string TestNoteText
        {
            get { return _testNoteText; }
            private set { SetProperty(ref _testNoteText, value); }
        }
        private string _testNoteText;

        #endregion

        #region Commands

        public DelegateCommand LoadCommand { get; private set; }
        public DelegateCommand SaveTeamCommand { get; private set; }
        public DelegateCommand OpenPersonalFileCommand { get; private set; }
        public DelegateCommand AddPathRuleCommand { get; private set; }
        public DelegateCommand AddLineRuleCommand { get; private set; }
        public DelegateCommand ShowNuGetPromptCommand { get; private set; }
        public DelegateCommand ApplyNuGetPresetCommand { get; private set; }
        public DelegateCommand CancelNuGetPresetCommand { get; private set; }
        public DelegateCommand AddCentralPackageManagementPresetCommand { get; private set; }
        public DelegateCommand AddAssemblyVersionPresetCommand { get; private set; }
        public DelegateCommand ApplyEditorCommand { get; private set; }
        public DelegateCommand CancelEditorCommand { get; private set; }

        #endregion

        #region Open and load

        // Chiamato da MergePolicyToolWindow.ShowAsync (thread UI) con ramo e workspace di Merge from Task.
        public void Open(string targetBranchRoot, Workspace workspace)
        {
            if (IsBusy)
            {
                _hasPendingOpen = true;
                _pendingOpenTarget = targetBranchRoot;
                _pendingOpenWorkspace = workspace;
                return;
            }

            var normalized = MergePolicyStore.NormalizeBranchRoot(targetBranchRoot);
            var sameTarget = normalized != null && IdComparer.Equals(normalized, _loadedTarget ?? string.Empty);

            if (sameTarget && IsTeamDirty)
            {
                // Stesso ramo con modifiche al file di team non salvate: non si ricarica (si perderebbero).
                if (workspace != null)
                    SetWorkspace(workspace);
                SetStatus("The unsaved changes to the team rules are kept. Press Reload to read the files again (the unsaved changes are discarded).", TaskMergeStatusKind.Warning);
                return;
            }

            if (IsTeamDirty && !Confirm("The team rules of " + _loadedTarget + " have unsaved changes.\n\nDiscard them and open the policies of "
                    + (normalized ?? "(no target branch)") + "?", false))
                return;

            if (workspace != null || !sameTarget)
                SetWorkspace(workspace);
            TargetBranchText = normalized ?? (targetBranchRoot ?? string.Empty);
            FireAndForget(() => RunBusyAsync(LoadCoreAsync));
        }

        private async Task ReloadCoreAsync()
        {
            if (IsTeamDirty && !Confirm("The team rules of " + _loadedTarget + " have unsaved changes.\n\nDiscard them and read the files again?", false))
                return;

            await LoadCoreAsync();
        }

        private sealed class LoadResult
        {
            public VersionControlServer Vcs;
            public Workspace Workspace;
            public MergePolicyDocument Team;
            public int TeamChangeset;
            public string TeamError;
            public MergePolicyTeamFileState TeamState;
            public string TeamStateError;
            public MergePolicyDocument Personal;
            public string PersonalError;
            public string PersonalSnapshot;
        }

        private async Task LoadCoreAsync()
        {
            CloseEditor();
            CloseNuGetPrompt();

            var text = TargetBranchText;
            var target = MergePolicyStore.NormalizeBranchRoot(text);
            if (target == null && !string.IsNullOrWhiteSpace(text))
            {
                SetStatus("The target branch must be a server path such as $/Project/Main.", TaskMergeStatusKind.Error);
                return;
            }

            SetStatus(target == null ? "Reading the personal rules..." : "Reading the merge policies of " + target + "...", TaskMergeStatusKind.Info);

            // Connessione: quella del workspace, altrimenti il contesto corrente di Team Explorer (letto qui,
            // sul thread UI; il servizio si ottiene fuori).
            var workspace = _workspace;
            var vcs = workspace != null ? workspace.VersionControlServer : null;
            TfsTeamProjectCollection collection = null;
            if (vcs == null && target != null)
                collection = GetCurrentCollection();

            var result = await Task.Run(() =>
            {
                var r = new LoadResult();
                r.Personal = MergePolicyStore.LoadPersonal(out r.PersonalError);
                if (r.PersonalError == null)
                {
                    try
                    {
                        r.PersonalSnapshot = MergePolicyStore.ReadPersonalText();
                    }
                    catch (Exception ex)
                    {
                        r.Personal = null;
                        r.PersonalError = "The personal policy file " + MergePolicyStore.PersonalFilePath + " cannot be read: " + ex.Message;
                    }
                }

                if (target == null)
                    return r;

                var server = vcs;
                if (server == null && collection != null)
                    server = collection.GetService<VersionControlServer>();
                r.Vcs = server;
                if (server == null)
                {
                    r.TeamError = "Not connected to a Team Foundation Server collection: connect in Team Explorer and press Reload.";
                    return r;
                }

                // Il workspace dato (quello di Merge from Task) se mappa il ramo, altrimenti uno dell'utente
                // su questa macchina che lo mappa.
                r.Workspace = workspace != null && SafeIsMapped(workspace, target)
                    ? workspace
                    : FindWorkspace(server, target) ?? workspace;
                r.Team = MergePolicyStore.LoadTeam(server, target, out r.TeamChangeset, out r.TeamError);
                try
                {
                    r.TeamState = MergePolicyStore.GetTeamFileState(r.Workspace, target);
                }
                catch (Exception ex)
                {
                    r.TeamStateError = "Cannot read the state of the team policy file in the workspace: " + ex.Message;
                }
                return r;
            });

            // Di nuovo sul thread UI.
            _vcs = result.Vcs;
            SetWorkspace(result.Workspace ?? workspace);
            _loadedTarget = target;
            OnPropertyChanged("LoadedTargetText");
            OnPropertyChanged("WorkspaceText");

            _personalDoc = result.Personal;
            _personalError = result.PersonalError;
            _personalLoaded = result.PersonalError == null;
            _personalSnapshot = result.PersonalSnapshot;

            _teamState = result.TeamState;
            _teamChangeset = result.TeamChangeset;
            _teamExistsOnServer = result.Team != null;
            _teamFromPending = false;
            _teamPendingSnapshot = null;
            _teamDoc = null;
            _teamLoaded = false;
            _teamError = null;
            if (target != null)
            {
                if (result.TeamError != null)
                    _teamError = result.TeamError;
                else if (result.TeamStateError != null)
                    _teamError = result.TeamStateError;
                else if (_teamState != null && _teamState.PendingIsAddOrEdit)
                {
                    if (_teamState.PendingError != null || _teamState.PendingDocument == null)
                        _teamError = _teamState.PendingError ?? "The pending team policy file cannot be read.";
                    else
                    {
                        _teamDoc = _teamState.PendingDocument;
                        _teamFromPending = true;
                        _teamPendingSnapshot = _teamState.PendingText;
                        _teamLoaded = true;
                    }
                }
                else
                {
                    _teamDoc = result.Team ?? NewDocument();
                    _teamLoaded = true;
                }
            }
            IsTeamDirty = false;

            // Dove vanno le regole nuove: il file di team se c'e', salvo scelta esplicita dell'utente.
            if (!_addTargetChosen)
            {
                _addTarget = _teamLoaded ? MergeRuleOrigin.Team : MergeRuleOrigin.Personal;
                OnPropertyChanged("AddTarget");
            }

            UpdateTeamTexts();
            UpdatePersonalTexts();
            Rebuild();

            if (_teamError != null || _personalError != null)
                SetStatus("Some policy files cannot be read: see the Team file and Personal file boxes.", TaskMergeStatusKind.Error);
            else if (target == null)
                SetStatus("Personal rules loaded. Enter a target branch and press Load to see its team rules.", TaskMergeStatusKind.Info);
            else
                SetStatus("Merge policies of " + target + " loaded.", TaskMergeStatusKind.Success);
            Log("Loaded: " + (target ?? "(no target)") + ", team " + (_teamLoaded ? MergePolicyStore.DescribeCounts(_teamDoc) : "not available")
                + ", personal " + (_personalLoaded ? MergePolicyStore.DescribeCounts(_personalDoc) : "not available"));
        }

        private TfsTeamProjectCollection GetCurrentCollection()
        {
            var contextManager = _serviceProvider == null
                ? null
                : _serviceProvider.GetService(typeof(ITeamFoundationContextManager)) as ITeamFoundationContextManager;
            var context = contextManager == null ? null : contextManager.CurrentContext;
            return context == null ? null : context.TeamProjectCollection;
        }

        // Workspace dell'utente su questa macchina che mappa il ramo (il piu' recente usato se piu' d'uno).
        // Chiamata TFVC: fuori dal thread UI.
        private static Workspace FindWorkspace(VersionControlServer vcs, string target)
        {
            try
            {
                var candidates = vcs.QueryWorkspaces(null, vcs.AuthorizedUser, Environment.MachineName)
                    .Where(w => SafeIsMapped(w, target))
                    .ToList();
                if (candidates.Count == 0)
                    return null;
                return WorkspaceHelper.GetWorkspace(vcs, candidates) ?? candidates[0];
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool SafeIsMapped(Workspace workspace, string serverPath)
        {
            if (workspace == null || string.IsNullOrEmpty(serverPath))
                return false;
            try
            {
                return workspace.IsServerPathMapped(serverPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void SetWorkspace(Workspace workspace)
        {
            _workspace = workspace;
            OnPropertyChanged("WorkspaceText");
        }

        #endregion

        #region Team file

        private async Task SaveTeamCoreAsync()
        {
            if (!CanSaveTeamNow())
                return;

            IReadOnlyList<string> errors = ValidateDocument(_teamDoc);
            if (errors.Count > 0)
            {
                SetStatus("The team rules have errors: fix them before saving. " + string.Join(" ", errors.Take(3)), TaskMergeStatusKind.Error);
                return;
            }

            var state = _teamState;
            var workspace = _workspace;
            string what;
            if (state.PendingIsAddOrEdit)
                what = "The pending " + state.PendingChangeName + " of the file in workspace '" + workspace.Name + "' is updated.";
            else if (_teamExistsOnServer)
                what = "The latest version of this file is downloaded and a pending edit is created in workspace '" + workspace.Name + "'.";
            else
                what = "The file is created and a pending add is created in workspace '" + workspace.Name + "'.";

            // Il file in sospeso sta sotto il ramo di destinazione e non e' un merge: ferma Merge from Task su
            // quel ramo finche' non e' archiviato o annullato. Con merge gia' in sospeso sotto il ramo (una
            // catena a meta') la conferma lo dice per primo e la risposta predefinita e' No.
            var branch = _loadedTarget;
            int pendingMerges;
            try
            {
                pendingMerges = await Task.Run(() => CountPendingMerges(workspace, branch));
            }
            catch (Exception ex)
            {
                Log("Could not read the pending changes under " + branch + ": " + ex.Message);
                pendingMerges = -1;
            }

            var mergeWarning = pendingMerges > 0
                ? "WARNING: " + pendingMerges + " merge(s) are pending under " + branch + " in workspace '" + workspace.Name
                    + "' (e.g. a Merge from Task chain that is not checked in). The team policy file would be one more pending change under the branch that is not a merge: "
                    + "the chain stops at Continue and at the check-in until the file is checked in or undone. Better: check in the merges first, then save.\n\n"
                : string.Empty;
            if (!Confirm(mergeWarning + "Write the team merge policy file?\n\n" + state.LocalPath + "\n(" + state.ServerPath + ")\n\n" + what
                    + "\n\nNothing is checked in: review the file in Pending Changes and check it in to share the rules with the team. "
                    + "Until then, merges use the version in the branch.\n\n" + MergePolicyStore.PendingTeamFileBlocksMergeText, pendingMerges <= 0))
                return;

            var doc = CloneDocument(_teamDoc);
            var target = _loadedTarget;
            var vcs = _vcs;
            var fromPending = _teamFromPending;
            var snapshot = _teamPendingSnapshot;
            var loadedChangeset = _teamChangeset;
            var localPath = state.LocalPath;

            SetStatus("Saving the team policy file...", TaskMergeStatusKind.Info);
            var outcome = await Task.Run(() =>
            {
                // Nessuna sovrascrittura di modifiche fatte da altri dopo la lettura.
                if (fromPending)
                {
                    var now = MergePolicyStore.ReadLocalText(localPath);
                    if (!string.Equals(now, snapshot, StringComparison.Ordinal))
                        throw new InvalidOperationException("The pending team policy file " + localPath
                            + " changed after it was loaded: nothing was written. Press Reload (the unsaved changes here are discarded) and apply them again.");
                }
                else
                {
                    var current = MergePolicyStore.GetTeamFileChangeset(vcs, target);
                    if (current != loadedChangeset)
                        throw new InvalidOperationException((current == 0
                                ? "The team policy file was deleted from the branch after it was loaded"
                                : "The team policy file changed in the branch (changeset " + current + ") after it was loaded"
                                    + (loadedChangeset == 0 ? "" : " (changeset " + loadedChangeset + ")"))
                            + ": nothing was written. Press Reload (the unsaved changes here are discarded) and apply them again.");
                    var now = MergePolicyStore.GetTeamFileState(workspace, target);
                    if (now.PendingChangeName != null)
                        throw new InvalidOperationException("The team policy file got a pending " + now.PendingChangeName + " in workspace '" + workspace.Name
                            + "' after it was loaded: nothing was written. Press Reload and apply the changes again.");
                }

                var message = MergePolicyStore.SaveTeam(workspace, target, doc);
                var after = MergePolicyStore.GetTeamFileState(workspace, target);
                return Tuple.Create(message, after);
            });

            IsTeamDirty = false;
            _teamState = outcome.Item2;
            if (_teamState != null && _teamState.PendingIsAddOrEdit && _teamState.PendingDocument != null)
            {
                _teamFromPending = true;
                _teamPendingSnapshot = _teamState.PendingText;
                _teamDoc = _teamState.PendingDocument;
            }
            UpdateTeamTexts();
            Rebuild();
            SetStatus(outcome.Item1, TaskMergeStatusKind.Success);
            Log(outcome.Item1);
        }

        // Merge in sospeso sotto il ramo nel workspace (es. una catena di Merge from Task non ancora
        // archiviata). Chiamata TFVC: fuori dal thread UI.
        private static int CountPendingMerges(Workspace workspace, string branch)
        {
            var root = MergePolicyStore.NormalizeBranchRoot(branch);
            if (workspace == null || root == null)
                return 0;
            return workspace.GetPendingChangesEnumerable(root, RecursionType.Full).Count(p => p != null && p.IsMerge);
        }

        // Perche' il file di team non si puo' salvare da qui (null = si puo', se ci sono modifiche).
        private string TeamSaveBlocker()
        {
            if (_loadedTarget == null)
                return "No target branch loaded.";
            if (!_teamLoaded)
                return "The team file is not available (see above).";
            if (_workspace == null)
                return "No workspace of yours on this computer maps the target branch: the team file cannot be written.";
            if (_teamState == null || _teamState.LocalPath == null)
                return "The target branch is not mapped (or is cloaked) in workspace '" + _workspace.Name + "'.";
            if (_teamState.PendingChangeName != null && !_teamState.PendingIsAddOrEdit)
                return "The team file has a pending " + _teamState.PendingChangeName + ": check it in or undo it, then press Reload.";
            if (_vcs == null)
                return "Not connected to version control.";
            return null;
        }

        private bool CanSaveTeamNow()
        {
            var blocker = TeamSaveBlocker();
            if (blocker != null)
            {
                SetStatus(blocker, TaskMergeStatusKind.Error);
                return false;
            }
            return IsTeamDirty;
        }

        private void UpdateTeamTexts()
        {
            if (_loadedTarget == null)
            {
                TeamServerPathText = "-";
                TeamLocalPathText = "-";
                TeamStatusText = "No target branch: open this tab from Merge from Task, or enter a target branch above and press Load.";
                TeamStatusKind = TaskMergeStatusKind.Info;
            }
            else
            {
                TeamServerPathText = _teamState != null ? _teamState.ServerPath : SafeTeamServerPath(_loadedTarget);
                if (_teamState != null && _teamState.LocalPath != null)
                    TeamLocalPathText = _teamState.LocalPath;
                else if (_workspace == null)
                    TeamLocalPathText = "(no workspace of yours on this computer maps the target branch)";
                else
                    TeamLocalPathText = "(not mapped in workspace '" + _workspace.Name + "')";

                if (_teamError != null)
                {
                    TeamStatusText = _teamError + " The team rules cannot be edited from here until the file can be read.";
                    TeamStatusKind = TaskMergeStatusKind.Error;
                }
                else if (_teamFromPending)
                {
                    TeamStatusText = "Pending " + _teamState.PendingChangeName + " in workspace '" + _teamState.WorkspaceName
                        + "' (not checked in): you are editing that version. Merges use "
                        + (_teamExistsOnServer ? "changeset " + _teamChangeset : "no team rules") + " until it is checked in. "
                        + MergePolicyStore.PendingTeamFileBlocksMergeText;
                    TeamStatusKind = TaskMergeStatusKind.Warning;
                }
                else if (_teamState != null && _teamState.PendingChangeName != null)
                {
                    TeamStatusText = "The file has a pending " + _teamState.PendingChangeName + " in workspace '" + _teamState.WorkspaceName
                        + "'. Shown: " + (_teamExistsOnServer ? "changeset " + _teamChangeset : "no file") + " in the branch. "
                        + MergePolicyStore.PendingTeamFileBlocksMergeText;
                    TeamStatusKind = TaskMergeStatusKind.Warning;
                }
                else if (_teamExistsOnServer)
                {
                    TeamStatusText = "Loaded at changeset " + _teamChangeset + " (latest version in the branch).";
                    TeamStatusKind = TaskMergeStatusKind.Success;
                }
                else
                {
                    TeamStatusText = "Not in the branch yet: the first save creates it as a pending add.";
                    TeamStatusKind = TaskMergeStatusKind.Info;
                }
            }

            UpdateTeamSaveHint();
        }

        private void UpdateTeamSaveHint()
        {
            var blocker = TeamSaveBlocker();
            if (Editor != null && IsTeamDirty)
                TeamSaveHint = "Apply or cancel the rule being edited first.";
            else if (blocker != null && (_teamLoaded || _loadedTarget == null))
                TeamSaveHint = blocker;
            else if (blocker == null && IsTeamDirty)
                TeamSaveHint = "Unsaved changes: the rules and the tester below already include them. Saving pends the file under the target branch: "
                    + "Merge from Task on that branch stops until it is checked in or undone.";
            else if (blocker == null)
                TeamSaveHint = "No unsaved changes.";
            else
                TeamSaveHint = null;
            SaveTeamCommand.RaiseCanExecuteChanged();
        }

        private static string SafeTeamServerPath(string target)
        {
            try
            {
                return MergePolicyStore.TeamFileServerPath(target);
            }
            catch (ArgumentException)
            {
                return "-";
            }
        }

        #endregion

        #region Personal file

        private void UpdatePersonalTexts()
        {
            if (_personalError != null)
            {
                PersonalStatusText = _personalError + " Fix the file (Open file) and press Reload: it is not changed from here until it can be read.";
                PersonalStatusKind = TaskMergeStatusKind.Error;
            }
            else if (!_personalLoaded)
            {
                PersonalStatusText = "Not read yet.";
                PersonalStatusKind = TaskMergeStatusKind.Info;
            }
            else if (_personalDoc == null)
            {
                PersonalStatusText = "No personal rules yet: the file is created with the first one.";
                PersonalStatusKind = TaskMergeStatusKind.Info;
            }
            else
            {
                PersonalStatusText = MergePolicyStore.DescribeCounts(_personalDoc) + ". Changes are saved immediately and apply to every branch.";
                PersonalStatusKind = TaskMergeStatusKind.Success;
            }
        }

        private void OpenPersonalFile()
        {
            try
            {
                var path = MergePolicyStore.PersonalFilePath;
                if (!System.IO.File.Exists(path))
                {
                    SetStatus("There is no personal policy file yet: it is created with the first personal rule.", TaskMergeStatusKind.Info);
                    return;
                }
                VsShellUtilities.OpenDocument(_serviceProvider ?? ServiceProvider.GlobalProvider, path);
                SetStatus("After editing the personal file by hand, save it and press Reload.", TaskMergeStatusKind.Info);
            }
            catch (Exception ex)
            {
                SetStatus("Cannot open the personal policy file: " + ex.Message, TaskMergeStatusKind.Error);
            }
        }

        #endregion

        #region Changes to the documents

        private bool IsDocumentAvailable(MergeRuleOrigin origin)
        {
            return origin == MergeRuleOrigin.Team ? _teamLoaded : _personalLoaded;
        }

        private MergePolicyDocument GetDocument(MergeRuleOrigin origin)
        {
            return origin == MergeRuleOrigin.Team ? _teamDoc : _personalDoc;
        }

        // Applica una modifica a una copia del documento scelto: personale = salvato subito (se il file
        // non e' cambiato fuori dalla scheda); team = in memoria, da salvare con "Save to team file".
        // change ritorna la descrizione della modifica (per il messaggio di stato).
        private bool ApplyChange(MergeRuleOrigin origin, Func<MergePolicyDocument, string> change)
        {
            try
            {
                if (!IsDocumentAvailable(origin))
                {
                    SetStatus(origin == MergeRuleOrigin.Team
                        ? "The team file is not available: the change was not applied."
                        : "The personal file cannot be read: the change was not applied.", TaskMergeStatusKind.Error);
                    return false;
                }

                var doc = CloneDocument(GetDocument(origin)) ?? NewDocument();
                var what = change(doc);

                if (origin == MergeRuleOrigin.Team)
                {
                    _teamDoc = doc;
                    IsTeamDirty = true;
                    SetStatus(what + " (team rules, not saved yet: press Save to team file).", TaskMergeStatusKind.Warning);
                    return true;
                }

                string current;
                try
                {
                    current = MergePolicyStore.ReadPersonalText();
                }
                catch (Exception ex)
                {
                    SetStatus("Cannot read the personal policy file before saving: " + ex.Message, TaskMergeStatusKind.Error);
                    return false;
                }
                if (!string.Equals(current, _personalSnapshot, StringComparison.Ordinal))
                {
                    SetStatus("The personal policy file changed outside this tab after it was loaded: nothing was saved. Press Reload and apply the change again.",
                        TaskMergeStatusKind.Error);
                    return false;
                }

                MergePolicyStore.SavePersonal(doc);
                _personalDoc = doc;
                try
                {
                    _personalSnapshot = MergePolicyStore.ReadPersonalText();
                }
                catch (Exception ex)
                {
                    // Salvato ma non riletto: niente altre modifiche finche' non si ricarica.
                    _personalLoaded = false;
                    _personalError = "The personal policy file was saved but cannot be read back: " + ex.Message;
                }
                SetStatus(what + " (personal rules, saved).", TaskMergeStatusKind.Success);
                Log(what + " (personal)");
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("The change was not applied: " + ex.Message, TaskMergeStatusKind.Error);
                _logger.Error("Merge Policies: change failed", ex);
                return false;
            }
            finally
            {
                UpdateTeamTexts();
                UpdatePersonalTexts();
                Rebuild();
            }
        }

        // Regola della riga nel suo documento (la posizione deve corrispondere ancora all'Id).
        private int LocateRow(MergePolicyDocument doc, MergePolicyRuleRowViewModel row)
        {
            if (doc == null || row.DocumentIndex < 0)
                return -1;
            if (row.Kind == MergePolicyRuleKind.Path)
            {
                var list = doc.PathRules;
                return list != null && row.DocumentIndex < list.Count && SameId(list[row.DocumentIndex].Id, row.Id) ? row.DocumentIndex : -1;
            }
            var lines = doc.LineRules;
            return lines != null && row.DocumentIndex < lines.Count && SameId(lines[row.DocumentIndex].Id, row.Id) ? row.DocumentIndex : -1;
        }

        internal void ToggleRow(MergePolicyRuleRowViewModel row)
        {
            ApplyRowChange(row, (doc, index) =>
            {
                bool enabled;
                if (row.Kind == MergePolicyRuleKind.Path)
                    enabled = doc.PathRules[index].Enabled = !doc.PathRules[index].Enabled;
                else
                    enabled = doc.LineRules[index].Enabled = !doc.LineRules[index].Enabled;
                return "Rule '" + row.Id + "' " + (enabled ? "enabled" : "disabled");
            });
        }

        internal void DeleteRow(MergePolicyRuleRowViewModel row)
        {
            var where = row.Origin == MergeRuleOrigin.Team
                ? "the team rules? The team file changes only when you press Save to team file."
                : "your personal rules? The personal file is saved immediately.";
            if (!Confirm("Delete the rule '" + row.Id + "' (" + row.PatternText + ") from " + where, false))
                return;

            ApplyRowChange(row, (doc, index) =>
            {
                if (row.Kind == MergePolicyRuleKind.Path)
                    doc.PathRules.RemoveAt(index);
                else
                    doc.LineRules.RemoveAt(index);
                return "Rule '" + row.Id + "' deleted";
            });
        }

        internal void MoveRow(MergePolicyRuleRowViewModel row, int delta)
        {
            ApplyRowChange(row, (doc, index) =>
            {
                var other = index + delta;
                if (row.Kind == MergePolicyRuleKind.Path)
                {
                    if (other < 0 || other >= doc.PathRules.Count)
                        return "Rule '" + row.Id + "' not moved";
                    var rule = doc.PathRules[index];
                    doc.PathRules[index] = doc.PathRules[other];
                    doc.PathRules[other] = rule;
                }
                else
                {
                    if (other < 0 || other >= doc.LineRules.Count)
                        return "Rule '" + row.Id + "' not moved";
                    var rule = doc.LineRules[index];
                    doc.LineRules[index] = doc.LineRules[other];
                    doc.LineRules[other] = rule;
                }
                return "Rule '" + row.Id + "' moved " + (delta < 0 ? "up" : "down");
            });
        }

        private void ApplyRowChange(MergePolicyRuleRowViewModel row, Func<MergePolicyDocument, int, string> change)
        {
            if (!AreRowsEnabled)
            {
                Rebuild();
                return;
            }
            var index = LocateRow(GetDocument(row.Origin), row);
            if (index < 0)
            {
                SetStatus("The rules changed meanwhile: press Reload.", TaskMergeStatusKind.Error);
                Rebuild();
                return;
            }
            ApplyChange(row.Origin, doc => change(doc, index));
        }

        private void AddPreset(MergePolicyDocument preset, string name)
        {
            if (preset == null)
                return;
            var origin = AddTarget;
            ApplyChange(origin, doc =>
            {
                int added = 0, replaced = 0;
                foreach (var rule in preset.PathRules ?? new List<MergePathRule>())
                {
                    var index = doc.PathRules.FindIndex(r => SameId(r.Id, rule.Id));
                    if (index >= 0)
                    {
                        doc.PathRules[index] = rule;
                        replaced++;
                    }
                    else
                    {
                        doc.PathRules.Add(rule);
                        added++;
                    }
                }
                foreach (var rule in preset.LineRules ?? new List<MergeLineRule>())
                {
                    var index = doc.LineRules.FindIndex(r => SameId(r.Id, rule.Id));
                    if (index >= 0)
                    {
                        doc.LineRules[index] = rule;
                        replaced++;
                    }
                    else
                    {
                        doc.LineRules.Add(rule);
                        added++;
                    }
                }
                return name + ": " + added + (added == 1 ? " rule added" : " rules added")
                    + (replaced > 0 ? ", " + replaced + (replaced == 1 ? " rule with the same Id replaced" : " rules with the same Id replaced") : "");
            });
        }

        private MergePolicyDocument SafePreset(Func<MergePolicyDocument> factory)
        {
            try
            {
                return factory();
            }
            catch (Exception ex)
            {
                SetStatus("Cannot build the preset: " + ex.Message, TaskMergeStatusKind.Error);
                return null;
            }
        }

        #endregion

        #region NuGet preset

        private void ShowNuGetPrompt()
        {
            NuGetPrefixesError = null;
            IsNuGetPromptOpen = true;
            OnPropertyChanged("NuGetTargetText");
        }

        private void CloseNuGetPrompt()
        {
            IsNuGetPromptOpen = false;
            NuGetPrefixesError = null;
        }

        private void ApplyNuGetPreset()
        {
            string error;
            var prefixes = ParsePrefixes(NuGetPrefixesText, out error);
            if (error != null)
            {
                NuGetPrefixesError = error;
                return;
            }

            var preset = SafePreset(() => MergePolicyEngine.NuGetPreset(prefixes));
            if (preset == null)
                return;

            IsNuGetPromptOpen = false;
            AddPreset(preset, "NuGet preset (" + string.Join(", ", prefixes) + ")");
        }

        // Prefissi separati da virgola (o punto e virgola); un '*' finale si toglie (i prefissi sono
        // letterali). Niente spazi dentro un prefisso: un id di pacchetto non ne ha.
        internal static List<string> ParsePrefixes(string text, out string error)
        {
            error = null;
            var prefixes = new List<string>();
            foreach (var part in (text ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var prefix = part.Trim().TrimEnd('*').Trim();
                if (prefix.Length == 0)
                    continue;
                if (prefix.Any(char.IsWhiteSpace) || prefix.IndexOfAny(new[] { '*', '?', '"', '<', '>' }) >= 0)
                {
                    error = "'" + prefix + "' is not a package id prefix: use the literal beginning of the ids, e.g. Contoso. (no spaces or wildcards).";
                    return null;
                }
                if (!prefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                    prefixes.Add(prefix);
            }
            if (prefixes.Count == 0)
            {
                error = "Enter at least one package id prefix, e.g. Contoso., Fabrikam.Core";
                return null;
            }
            return prefixes;
        }

        #endregion

        #region Rule editor

        private void OpenNewRuleEditor(MergePolicyRuleKind kind)
        {
            var origin = AddTarget;
            if (!IsDocumentAvailable(origin))
                return;
            var editor = CreateEditor(kind, origin, true, -1, null);
            editor.Id = NextId(kind, origin);
            if (kind == MergePolicyRuleKind.Path)
                editor.Action = MergePolicyAction.Discard;
            editor.IsEnabled = true;
            Editor = editor;
            UpdateTeamSaveHint();
        }

        internal void EditRow(MergePolicyRuleRowViewModel row)
        {
            if (!AreRowsEnabled)
                return;
            var index = LocateRow(GetDocument(row.Origin), row);
            if (index < 0)
            {
                SetStatus("The rules changed meanwhile: press Reload.", TaskMergeStatusKind.Error);
                Rebuild();
                return;
            }
            var editor = CreateEditor(row.Kind, row.Origin, false, index, row.Id);
            CopyRuleToEditor(GetDocument(row.Origin), row.Kind, index, editor);
            Editor = editor;
            UpdateTeamSaveHint();
        }

        // Copia una regola di team nel file personale con lo stesso Id: da li' vince su quella di team.
        internal void OverrideRow(MergePolicyRuleRowViewModel row)
        {
            if (!AreRowsEnabled || row.Origin != MergeRuleOrigin.Team)
                return;
            if (!_personalLoaded)
            {
                SetStatus("The personal file cannot be read: fix it and press Reload.", TaskMergeStatusKind.Error);
                return;
            }
            var index = LocateRow(_teamDoc, row);
            if (index < 0)
            {
                SetStatus("The rules changed meanwhile: press Reload.", TaskMergeStatusKind.Error);
                Rebuild();
                return;
            }
            var editor = CreateEditor(row.Kind, MergeRuleOrigin.Personal, true, -1, null);
            CopyRuleToEditor(_teamDoc, row.Kind, index, editor);
            editor.Title = "Override the team rule '" + row.Id + "' for you (personal file)";
            Editor = editor;
            UpdateTeamSaveHint();
        }

        private MergePolicyRuleEditorViewModel CreateEditor(MergePolicyRuleKind kind, MergeRuleOrigin origin, bool isNew, int index, string originalId)
        {
            // Id delle altre regole dello stesso tipo nello stesso file (l'engine li vuole unici per tipo).
            var doc = GetDocument(origin);
            var otherIds = new HashSet<string>(IdComparer);
            if (doc != null && kind == MergePolicyRuleKind.Path)
            {
                var rules = doc.PathRules ?? new List<MergePathRule>();
                for (var i = 0; i < rules.Count; i++)
                    if (i != index && rules[i] != null && !string.IsNullOrWhiteSpace(rules[i].Id))
                        otherIds.Add(rules[i].Id.Trim());
            }
            else if (doc != null)
            {
                var rules = doc.LineRules ?? new List<MergeLineRule>();
                for (var i = 0; i < rules.Count; i++)
                    if (i != index && rules[i] != null && !string.IsNullOrWhiteSpace(rules[i].Id))
                        otherIds.Add(rules[i].Id.Trim());
            }

            // Regole di team dello stesso tipo: una personale con lo stesso Id le sostituisce.
            var teamIds = new HashSet<string>(IdComparer);
            if (origin == MergeRuleOrigin.Personal && _teamLoaded && _teamDoc != null)
            {
                if (kind == MergePolicyRuleKind.Path)
                    teamIds.UnionWith((_teamDoc.PathRules ?? new List<MergePathRule>()).Where(r => !string.IsNullOrWhiteSpace(r.Id)).Select(r => r.Id.Trim()));
                else
                    teamIds.UnionWith((_teamDoc.LineRules ?? new List<MergeLineRule>()).Where(r => !string.IsNullOrWhiteSpace(r.Id)).Select(r => r.Id.Trim()));
            }

            var title = (isNew ? "New " : "Edit ") + (kind == MergePolicyRuleKind.Path ? "path rule" : "line rule")
                + (isNew ? "" : " '" + originalId + "'")
                + (origin == MergeRuleOrigin.Team ? " (team file)" : " (personal file)");
            return new MergePolicyRuleEditorViewModel(kind, origin, isNew, index, title, otherIds, teamIds);
        }

        private static void CopyRuleToEditor(MergePolicyDocument doc, MergePolicyRuleKind kind, int index, MergePolicyRuleEditorViewModel editor)
        {
            if (kind == MergePolicyRuleKind.Path)
            {
                var rule = doc.PathRules[index];
                editor.Id = rule.Id;
                editor.Pattern = rule.Pattern;
                editor.Action = rule.Action;
                editor.Description = rule.Description;
                editor.IsEnabled = rule.Enabled;
            }
            else
            {
                var rule = doc.LineRules[index];
                editor.Id = rule.Id;
                editor.FilePattern = rule.FilePattern;
                editor.IsBlockMode = string.IsNullOrEmpty(rule.LinePattern)
                    && (!string.IsNullOrEmpty(rule.BlockStartPattern) || !string.IsNullOrEmpty(rule.BlockEndPattern));
                editor.LinePattern = rule.LinePattern;
                editor.BlockStartPattern = rule.BlockStartPattern;
                editor.BlockEndPattern = rule.BlockEndPattern;
                editor.BlockContainsPattern = rule.BlockContainsPattern;
                editor.Description = rule.Description;
                editor.IsEnabled = rule.Enabled;
            }
        }

        private void ApplyEditor()
        {
            var editor = Editor;
            if (editor == null || editor.HasErrors)
                return;

            var applied = ApplyChange(editor.Origin, doc =>
            {
                if (editor.Kind == MergePolicyRuleKind.Path)
                {
                    var rule = editor.BuildPathRule();
                    if (editor.IsNew)
                        doc.PathRules.Add(rule);
                    else if (editor.DocumentIndex >= 0 && editor.DocumentIndex < doc.PathRules.Count)
                        doc.PathRules[editor.DocumentIndex] = rule;
                    else
                        throw new InvalidOperationException("the rule being edited is no longer in the document: press Reload");
                    return "Path rule '" + rule.Id + "' " + (editor.IsNew ? "added" : "updated");
                }
                else
                {
                    var rule = editor.BuildLineRule();
                    if (editor.IsNew)
                        doc.LineRules.Add(rule);
                    else if (editor.DocumentIndex >= 0 && editor.DocumentIndex < doc.LineRules.Count)
                        doc.LineRules[editor.DocumentIndex] = rule;
                    else
                        throw new InvalidOperationException("the rule being edited is no longer in the document: press Reload");
                    return "Line rule '" + rule.Id + "' " + (editor.IsNew ? "added" : "updated");
                }
            });

            if (applied)
                CloseEditor();
        }

        private void CloseEditor()
        {
            Editor = null;
            UpdateTeamSaveHint();
        }

        private void OnEditorValidityChanged(object sender, EventArgs e)
        {
            ApplyEditorCommand.RaiseCanExecuteChanged();
        }

        // "team.path.3" / "my.line.1": il prefisso evita che una regola personale nuova sostituisca per
        // caso una di team con lo stesso Id.
        private string NextId(MergePolicyRuleKind kind, MergeRuleOrigin origin)
        {
            var prefix = (origin == MergeRuleOrigin.Team ? "team." : "my.") + (kind == MergePolicyRuleKind.Path ? "path." : "line.");
            var used = new HashSet<string>(IdComparer);
            foreach (var doc in new[] { _teamDoc, _personalDoc })
            {
                if (doc == null)
                    continue;
                used.UnionWith((doc.PathRules ?? new List<MergePathRule>()).Where(r => r.Id != null).Select(r => r.Id.Trim()));
                used.UnionWith((doc.LineRules ?? new List<MergeLineRule>()).Where(r => r.Id != null).Select(r => r.Id.Trim()));
            }
            var n = 1;
            while (used.Contains(prefix + n.ToString(CultureInfo.InvariantCulture)))
                n++;
            return prefix + n.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region Effective rules and tester

        private void Rebuild()
        {
            EffectiveMergePolicy effective = null;
            string engineError = null;
            try
            {
                effective = new EffectiveMergePolicy(_teamLoaded ? _teamDoc : null, _personalLoaded ? _personalDoc : null);
            }
            catch (Exception ex)
            {
                engineError = ex.Message;
            }
            _effective = effective;

            PolicyErrors.Clear();
            if (_teamError != null)
                PolicyErrors.Add("Team file: " + _teamError + " Its rules are not listed below; merges that use the policies of this branch stop until it can be read.");
            if (_personalError != null)
                PolicyErrors.Add("Personal file: " + _personalError + " Its rules are not listed below; merges that use the policies stop until it can be read.");
            if (engineError != null)
                PolicyErrors.Add("The rules cannot be evaluated: " + engineError);
            if (effective != null && effective.Errors != null)
                foreach (var error in effective.Errors)
                    PolicyErrors.Add(error);

            var teamDoc = _teamLoaded ? _teamDoc : null;
            var personalDoc = _personalLoaded ? _personalDoc : null;

            PathRules.Clear();
            foreach (var row in BuildPathRows(effective, teamDoc, personalDoc))
                PathRules.Add(row);
            LineRules.Clear();
            foreach (var row in BuildLineRows(effective, teamDoc, personalDoc))
                LineRules.Add(row);

            OnPropertyChanged("HasPolicyErrors");
            OnPropertyChanged("HasPathRules");
            OnPropertyChanged("HasLineRules");
            OnPropertyChanged("AddTargetHint");
            RaiseCommandStates();
            RecomputeTest();
        }

        private List<MergePolicyRuleRowViewModel> BuildPathRows(EffectiveMergePolicy effective, MergePolicyDocument teamDoc, MergePolicyDocument personalDoc)
        {
            var team = teamDoc == null ? new List<MergePathRule>() : teamDoc.PathRules ?? new List<MergePathRule>();
            var personal = personalDoc == null ? new List<MergePathRule>() : personalDoc.PathRules ?? new List<MergePathRule>();
            var usedTeam = new HashSet<int>();
            var usedPersonal = new HashSet<int>();
            var rows = new List<MergePolicyRuleRowViewModel>();
            var order = 0;

            if (effective != null && effective.PathRules != null)
            {
                foreach (var item in effective.PathRules)
                {
                    var list = item.Origin == MergeRuleOrigin.Team ? team : personal;
                    var used = item.Origin == MergeRuleOrigin.Team ? usedTeam : usedPersonal;
                    var index = Locate(list, item.Rule, used, r => r.Id);
                    if (index >= 0)
                        used.Add(index);
                    rows.Add(PathRow(item.Rule, item.Origin, index, list.Count, ++order, item.OverridesTeamRule, null));
                }
            }

            for (var i = 0; i < team.Count; i++)
            {
                if (usedTeam.Contains(i))
                    continue;
                var id = team[i].Id;
                var reason = personal.Any(p => SameId(p.Id, id))
                    ? "Replaced by your personal rule with the same Id (personal rules always win)."
                    : "Not applied: see the errors above.";
                rows.Add(PathRow(team[i], MergeRuleOrigin.Team, i, team.Count, 0, false, reason));
            }
            for (var i = 0; i < personal.Count; i++)
            {
                if (!usedPersonal.Contains(i))
                    rows.Add(PathRow(personal[i], MergeRuleOrigin.Personal, i, personal.Count, 0, false, "Not applied: see the errors above."));
            }
            return rows;
        }

        private List<MergePolicyRuleRowViewModel> BuildLineRows(EffectiveMergePolicy effective, MergePolicyDocument teamDoc, MergePolicyDocument personalDoc)
        {
            var team = teamDoc == null ? new List<MergeLineRule>() : teamDoc.LineRules ?? new List<MergeLineRule>();
            var personal = personalDoc == null ? new List<MergeLineRule>() : personalDoc.LineRules ?? new List<MergeLineRule>();
            var usedTeam = new HashSet<int>();
            var usedPersonal = new HashSet<int>();
            var rows = new List<MergePolicyRuleRowViewModel>();
            var order = 0;

            if (effective != null && effective.LineRules != null)
            {
                foreach (var item in effective.LineRules)
                {
                    var list = item.Origin == MergeRuleOrigin.Team ? team : personal;
                    var used = item.Origin == MergeRuleOrigin.Team ? usedTeam : usedPersonal;
                    var index = Locate(list, item.Rule, used, r => r.Id);
                    if (index >= 0)
                        used.Add(index);
                    rows.Add(LineRow(item.Rule, item.Origin, index, list.Count, ++order, item.OverridesTeamRule, null));
                }
            }

            for (var i = 0; i < team.Count; i++)
            {
                if (usedTeam.Contains(i))
                    continue;
                var id = team[i].Id;
                var reason = personal.Any(p => SameId(p.Id, id))
                    ? "Replaced by your personal rule with the same Id (personal rules always win)."
                    : "Not applied: see the errors above.";
                rows.Add(LineRow(team[i], MergeRuleOrigin.Team, i, team.Count, 0, false, reason));
            }
            for (var i = 0; i < personal.Count; i++)
            {
                if (!usedPersonal.Contains(i))
                    rows.Add(LineRow(personal[i], MergeRuleOrigin.Personal, i, personal.Count, 0, false, "Not applied: see the errors above."));
            }
            return rows;
        }

        // Posizione di una regola effettiva nel suo documento: prima per riferimento, poi per Id.
        private static int Locate<T>(List<T> list, T rule, HashSet<int> used, Func<T, string> idOf) where T : class
        {
            for (var i = 0; i < list.Count; i++)
                if (!used.Contains(i) && ReferenceEquals(list[i], rule))
                    return i;
            var id = rule == null ? null : idOf(rule);
            for (var i = 0; i < list.Count; i++)
                if (!used.Contains(i) && SameId(idOf(list[i]), id))
                    return i;
            return -1;
        }

        private MergePolicyRuleRowViewModel PathRow(MergePathRule rule, MergeRuleOrigin origin, int index, int count, int order, bool overrides, string notAppliedReason)
        {
            return new MergePolicyRuleRowViewModel(this, MergePolicyRuleKind.Path, origin, index, count, order, overrides, notAppliedReason,
                rule.Id, rule.Enabled, rule.Pattern, ActionLabel(rule.Action), ActionDescription(rule.Action), rule.Description);
        }

        private MergePolicyRuleRowViewModel LineRow(MergeLineRule rule, MergeRuleOrigin origin, int index, int count, int order, bool overrides, string notAppliedReason)
        {
            var detail = DescribeLineRule(rule);
            return new MergePolicyRuleRowViewModel(this, MergePolicyRuleKind.Line, origin, index, count, order, overrides, notAppliedReason,
                rule.Id, rule.Enabled, rule.FilePattern, detail, "Protected lines keep the target version: source changes to them are not merged.", rule.Description);
        }

        internal static string DescribeLineRule(MergeLineRule rule)
        {
            if (!string.IsNullOrEmpty(rule.LinePattern))
                return "Lines matching " + rule.LinePattern;
            var text = "Blocks from " + (rule.BlockStartPattern ?? "?") + " to " + (rule.BlockEndPattern ?? "?");
            if (!string.IsNullOrEmpty(rule.BlockContainsPattern))
                text += " containing " + rule.BlockContainsPattern;
            return text;
        }

        internal static string ActionLabel(MergePolicyAction action)
        {
            switch (action)
            {
                case MergePolicyAction.Discard:
                    return "Discard";
                case MergePolicyAction.Skip:
                    return "Skip";
                default:
                    return "Merge";
            }
        }

        internal static string ActionDescription(MergePolicyAction action)
        {
            switch (action)
            {
                case MergePolicyAction.Discard:
                    return "Discard: recorded as merged, but the target keeps its content (tf merge /discard). Checked by the final audit.";
                case MergePolicyAction.Skip:
                    return "Skip: not merged at all (the changes stay to be merged later).";
                default:
                    return "Merge: merged normally (useful as an exception before a broader rule).";
            }
        }

        private void RecomputeTest()
        {
            TestLineRules.Clear();
            var text = TestPathText;
            if (string.IsNullOrWhiteSpace(text))
            {
                HasTestResult = false;
                return;
            }

            HasTestResult = true;
            string note;
            var relative = NormalizeTestPath(text, out note);
            TestNormalizedPathText = relative == null ? "-" : relative;

            var notes = new List<string>();
            if (note != null)
                notes.Add(note);
            if (IsTeamDirty)
                notes.Add("Includes unsaved changes to the team rules: merges use the version in the branch until the file is saved and checked in.");
            else if (_teamFromPending)
                notes.Add("Includes the pending (not checked in) team file: merges use the version in the branch until it is checked in.");
            TestNoteText = notes.Count == 0 ? null : string.Join(" ", notes);

            if (relative == null)
            {
                TestResultKind = "Error";
                TestDecisionText = "Enter a path relative to the target branch, e.g. src/App/App.csproj";
                TestRuleText = null;
                TestLineRulesHeader = null;
                return;
            }
            if (_effective == null)
            {
                TestResultKind = "Error";
                TestDecisionText = "The rules cannot be evaluated: fix the errors above.";
                TestRuleText = null;
                TestLineRulesHeader = null;
                return;
            }

            try
            {
                var decision = MergePolicyEngine.Decide(_effective, relative);
                TestResultKind = ActionLabel(decision.Action);
                TestDecisionText = ActionDescription(decision.Action);
                if (decision.MatchedRule == null)
                    TestRuleText = "No enabled path rule matches: the default is Merge.";
                else
                    TestRuleText = "Decided by rule '" + decision.MatchedRule.Rule.Id + "' (" + OriginLabel(decision.MatchedRule.Origin, decision.MatchedRule.OverridesTeamRule)
                        + "): " + decision.MatchedRule.Rule.Pattern;

                var lineRules = decision.LineRules ?? new List<EffectiveLineRule>();
                foreach (var lineRule in lineRules)
                    TestLineRules.Add("'" + lineRule.Rule.Id + "' (" + OriginLabel(lineRule.Origin, lineRule.OverridesTeamRule) + "): "
                        + DescribeLineRule(lineRule.Rule));
                if (decision.Action != MergePolicyAction.Merge)
                    TestLineRulesHeader = "Line rules do not apply: the content of this file is not merged.";
                else
                    TestLineRulesHeader = lineRules.Count == 0
                        ? "No protected lines apply to this file."
                        : "Protected lines in this file (they keep the target version):";
            }
            catch (Exception ex)
            {
                TestResultKind = "Error";
                TestDecisionText = "Cannot evaluate the rules: " + ex.Message;
                TestRuleText = null;
                TestLineRulesHeader = null;
            }
        }

        // Percorso del tester relativo alla radice del ramo con '/': accetta anche un percorso server
        // sotto il ramo o un percorso locale sotto la cartella mappata.
        private string NormalizeTestPath(string text, out string note)
        {
            note = null;
            var path = text.Trim().Trim('"').Trim();
            if (path.Length == 0)
                return null;

            if (path.StartsWith("$/", StringComparison.Ordinal) || path.StartsWith("$\\", StringComparison.Ordinal))
            {
                path = path.Replace('\\', '/');
                if (_loadedTarget != null && path.StartsWith(_loadedTarget.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                    return NonEmpty(path.Substring(_loadedTarget.TrimEnd('/').Length + 1));
                note = "The server path is not under the target branch " + (_loadedTarget ?? "(none)") + ".";
                return null;
            }

            var localRoot = _teamState != null && _teamState.LocalPath != null ? System.IO.Path.GetDirectoryName(_teamState.LocalPath) : null;
            if (localRoot != null && path.Length > 2 && path[1] == ':')
            {
                var root = localRoot.TrimEnd('\\', '/') + "\\";
                var local = path.Replace('/', '\\');
                if (local.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return NonEmpty(local.Substring(root.Length).Replace('\\', '/'));
                note = "The local path is not under the folder mapped to the target branch (" + localRoot + ").";
                return null;
            }

            // Percorso locale (disco o UNC) senza una cartella mappata nota: non e' un path relativo.
            if ((path.Length > 1 && path[1] == ':') || path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                note = "A local path can be tested only when the target branch is mapped in a workspace: enter a path relative to the target branch or a server path.";
                return null;
            }

            return NonEmpty(path.Replace('\\', '/').TrimStart('/'));
        }

        private static string NonEmpty(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        internal static string OriginLabel(MergeRuleOrigin origin, bool overridesTeamRule)
        {
            if (origin == MergeRuleOrigin.Team)
                return "Team";
            return overridesTeamRule ? "Personal override" : "Personal";
        }

        #endregion

        #region Infrastructure

        private void RaiseCommandStates()
        {
            OnPropertyChanged("AreRowsEnabled");
            OnPropertyChanged("CanAddRules");
            OnPropertyChanged("CanSaveTeam");
            if (LoadCommand == null)
                return;
            LoadCommand.RaiseCanExecuteChanged();
            SaveTeamCommand.RaiseCanExecuteChanged();
            OpenPersonalFileCommand.RaiseCanExecuteChanged();
            AddPathRuleCommand.RaiseCanExecuteChanged();
            AddLineRuleCommand.RaiseCanExecuteChanged();
            ShowNuGetPromptCommand.RaiseCanExecuteChanged();
            ApplyNuGetPresetCommand.RaiseCanExecuteChanged();
            AddCentralPackageManagementPresetCommand.RaiseCanExecuteChanged();
            AddAssemblyVersionPresetCommand.RaiseCanExecuteChanged();
            ApplyEditorCommand.RaiseCanExecuteChanged();
            CancelEditorCommand.RaiseCanExecuteChanged();
        }

        private void SetStatus(string message, TaskMergeStatusKind kind)
        {
            _statusKind = kind;
            StatusMessage = message;
            OnPropertyChanged("StatusKind");
        }

        // Esegue un'azione con la scheda "occupata"; qualsiasi eccezione finisce nel messaggio di stato
        // (mai fuori: i command handler sono async void lato WPF).
        private async Task RunBusyAsync(Func<Task> action)
        {
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logger.Error("Merge Policies: unexpected error", ex);
                SetStatus("Error: " + ex.Message, TaskMergeStatusKind.Error);
            }
            finally
            {
                IsBusy = false;
                UpdateTeamSaveHint();

                // Apertura richiesta da Merge from Task mentre la scheda lavorava.
                if (_hasPendingOpen)
                {
                    _hasPendingOpen = false;
                    var target = _pendingOpenTarget;
                    var workspace = _pendingOpenWorkspace;
                    _pendingOpenTarget = null;
                    _pendingOpenWorkspace = null;
                    _dispatcher.BeginInvoke(new Action(() => Open(target, workspace)));
                }
            }
        }

        // async void di comodo: nessuna eccezione esce.
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
                    _logger.Error("Merge Policies: unexpected error", ex);
                }
                catch (Exception)
                {
                    // Il registro stesso ha fallito: niente altro da fare in un async void.
                }
            }
        }

        private bool Confirm(string text, bool defaultYes)
        {
            var result = VsShellUtilities.ShowMessageBox(_serviceProvider ?? ServiceProvider.GlobalProvider,
                text,
                MessageTitle,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                defaultYes ? OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST : OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            return result == (int)VSConstants.MessageBoxResult.IDYES;
        }

        private void Log(string message)
        {
            try
            {
                _logger.Info("[Merge Policies] " + message);
            }
            catch (Exception)
            {
                // Il registro non deve mai fermare la scheda.
            }
        }

        internal static bool SameId(string a, string b)
        {
            return string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // Regole valide per l'engine (Errors di un EffectiveMergePolicy con il solo documento).
        internal static IReadOnlyList<string> ValidateDocument(MergePolicyDocument doc)
        {
            try
            {
                var errors = new EffectiveMergePolicy(doc, null).Errors;
                return errors ?? (IReadOnlyList<string>)new List<string>();
            }
            catch (Exception ex)
            {
                return new List<string> { ex.Message };
            }
        }

        internal static MergePolicyDocument NewDocument()
        {
            return new MergePolicyDocument
            {
                Version = 1,
                PathRules = new List<MergePathRule>(),
                LineRules = new List<MergeLineRule>()
            };
        }

        // Copia profonda (le righe e l'editor non devono mai toccare il documento salvato).
        internal static MergePolicyDocument CloneDocument(MergePolicyDocument doc)
        {
            if (doc == null)
                return null;
            return new MergePolicyDocument
            {
                Version = doc.Version <= 0 ? 1 : doc.Version,
                PathRules = (doc.PathRules ?? new List<MergePathRule>()).Where(r => r != null).Select(r => new MergePathRule
                {
                    Id = r.Id,
                    Pattern = r.Pattern,
                    Action = r.Action,
                    Description = r.Description,
                    Enabled = r.Enabled
                }).ToList(),
                LineRules = (doc.LineRules ?? new List<MergeLineRule>()).Where(r => r != null).Select(r => new MergeLineRule
                {
                    Id = r.Id,
                    FilePattern = r.FilePattern,
                    LinePattern = r.LinePattern,
                    BlockStartPattern = r.BlockStartPattern,
                    BlockEndPattern = r.BlockEndPattern,
                    BlockContainsPattern = r.BlockContainsPattern,
                    Description = r.Description,
                    Enabled = r.Enabled
                }).ToList()
            };
        }

        #endregion
    }

    // Una riga dell'elenco delle regole (percorso o riga), con le azioni sulla regola nel suo documento.
    public sealed class MergePolicyRuleRowViewModel : BindableBase
    {
        private readonly MergePolicyViewModel _owner;

        internal MergePolicyRuleRowViewModel(MergePolicyViewModel owner, MergePolicyRuleKind kind, MergeRuleOrigin origin,
            int documentIndex, int documentCount, int order, bool overridesTeamRule, string notAppliedReason,
            string id, bool enabled, string pattern, string detail, string detailToolTip, string description)
        {
            _owner = owner;
            Kind = kind;
            Origin = origin;
            DocumentIndex = documentIndex;
            Order = order;
            OverridesTeamRule = overridesTeamRule;
            NotAppliedReason = notAppliedReason;
            Id = id;
            IsEnabled = enabled;
            PatternText = pattern;
            DetailText = detail;
            DetailToolTip = detailToolTip;
            Description = description;

            var located = documentIndex >= 0;
            EditCommand = new DelegateCommand(() => _owner.EditRow(this), () => located);
            DeleteCommand = new DelegateCommand(() => _owner.DeleteRow(this), () => located);
            ToggleEnabledCommand = new DelegateCommand(() => _owner.ToggleRow(this), () => located);
            MoveUpCommand = new DelegateCommand(() => _owner.MoveRow(this, -1), () => located && documentIndex > 0);
            MoveDownCommand = new DelegateCommand(() => _owner.MoveRow(this, 1), () => located && documentIndex < documentCount - 1);
            OverrideCommand = new DelegateCommand(() => _owner.OverrideRow(this), () => located && CanOverride);
        }

        public MergePolicyRuleKind Kind { get; private set; }
        public MergeRuleOrigin Origin { get; private set; }
        // Posizione nel documento (team o personale) da cui viene; -1 se non trovata.
        public int DocumentIndex { get; private set; }
        // Posizione nell'ordine di valutazione (0 = non applicata).
        public int Order { get; private set; }
        public bool OverridesTeamRule { get; private set; }
        public string NotAppliedReason { get; private set; }
        public string Id { get; private set; }
        public bool IsEnabled { get; private set; }
        public string PatternText { get; private set; }
        public string DetailText { get; private set; }
        public string DetailToolTip { get; private set; }
        public string Description { get; private set; }

        public bool IsApplied
        {
            get { return NotAppliedReason == null; }
        }

        public bool IsTeam
        {
            get { return Origin == MergeRuleOrigin.Team; }
        }

        public bool CanOverride
        {
            get { return Origin == MergeRuleOrigin.Team && IsApplied; }
        }

        public bool HasDescription
        {
            get { return !string.IsNullOrWhiteSpace(Description); }
        }

        public string OrderText
        {
            get { return Order > 0 ? Order.ToString(CultureInfo.InvariantCulture) : "-"; }
        }

        public string OriginText
        {
            get
            {
                if (!IsApplied)
                    return Origin == MergeRuleOrigin.Team ? "Team (not applied)" : "Personal (not applied)";
                return MergePolicyViewModel.OriginLabel(Origin, OverridesTeamRule);
            }
        }

        public string EnabledToolTip
        {
            get
            {
                return Origin == MergeRuleOrigin.Team
                    ? "Enable or disable this rule in the team rules (saved with Save to team file). To disable it only for you, use Override."
                    : "Enable or disable this personal rule (saved immediately).";
            }
        }

        public string ToolTipText
        {
            get
            {
                var text = new StringBuilder();
                text.Append("Id: ").Append(Id);
                text.Append("\nOrigin: ").Append(OriginText);
                if (!IsEnabled)
                    text.Append(" (disabled)");
                if (NotAppliedReason != null)
                    text.Append("\n").Append(NotAppliedReason);
                if (HasDescription)
                    text.Append("\n").Append(Description);
                return text.ToString();
            }
        }

        public DelegateCommand EditCommand { get; private set; }
        public DelegateCommand DeleteCommand { get; private set; }
        public DelegateCommand ToggleEnabledCommand { get; private set; }
        public DelegateCommand MoveUpCommand { get; private set; }
        public DelegateCommand MoveDownCommand { get; private set; }
        public DelegateCommand OverrideCommand { get; private set; }
    }

    // Editor di una regola (nuova o esistente) con la validazione in linea: glob non vuoti con '/',
    // regex compilabili, Id unico nel documento, e in fondo il giudizio dell'engine sulla sola regola.
    public sealed class MergePolicyRuleEditorViewModel : BindableBase
    {
        // Liste condivise da tutti gli editor: cambiando editor le ComboBox tengono lo stesso ItemsSource
        // (un ItemsSource nuovo azzererebbe la selezione e la rimanderebbe al view model).
        private static readonly IReadOnlyList<MergePolicyOption<MergePolicyAction>> SharedActionOptions = new List<MergePolicyOption<MergePolicyAction>>
        {
            new MergePolicyOption<MergePolicyAction>(MergePolicyAction.Discard, "Discard - record as merged, keep the target content"),
            new MergePolicyOption<MergePolicyAction>(MergePolicyAction.Skip, "Skip - do not merge"),
            new MergePolicyOption<MergePolicyAction>(MergePolicyAction.Merge, "Merge - merge normally (exception to a later rule)")
        };

        private static readonly IReadOnlyList<MergePolicyOption<bool>> SharedModeOptions = new List<MergePolicyOption<bool>>
        {
            new MergePolicyOption<bool>(false, "Single lines (regular expression)"),
            new MergePolicyOption<bool>(true, "Blocks (from a start line to an end line)")
        };

        private readonly ISet<string> _otherIds;
        private readonly ISet<string> _teamIds;

        internal MergePolicyRuleEditorViewModel(MergePolicyRuleKind kind, MergeRuleOrigin origin, bool isNew, int documentIndex,
            string title, ISet<string> otherIds, ISet<string> teamIds)
        {
            Kind = kind;
            Origin = origin;
            IsNew = isNew;
            DocumentIndex = documentIndex;
            _title = title;
            _otherIds = otherIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _teamIds = teamIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _isEnabled = true;
            Validate();
        }

        // Sollevato quando HasErrors puo' essere cambiato.
        public event EventHandler ValidityChanged;

        public MergePolicyRuleKind Kind { get; private set; }
        public MergeRuleOrigin Origin { get; private set; }
        public bool IsNew { get; private set; }
        public int DocumentIndex { get; private set; }

        public IReadOnlyList<MergePolicyOption<MergePolicyAction>> ActionOptions
        {
            get { return SharedActionOptions; }
        }

        public IReadOnlyList<MergePolicyOption<bool>> ModeOptions
        {
            get { return SharedModeOptions; }
        }

        public bool IsPathRule
        {
            get { return Kind == MergePolicyRuleKind.Path; }
        }

        public bool IsLineRule
        {
            get { return Kind == MergePolicyRuleKind.Line; }
        }

        public bool ShowLinePattern
        {
            get { return IsLineRule && !IsBlockMode; }
        }

        public bool ShowBlockPatterns
        {
            get { return IsLineRule && IsBlockMode; }
        }

        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value); }
        }
        private string _title;

        public string ApplyText
        {
            get { return Origin == MergeRuleOrigin.Personal ? "Apply and save" : "Apply"; }
        }

        public string Id
        {
            get { return _id; }
            set
            {
                if (SetProperty(ref _id, value))
                    Validate();
            }
        }
        private string _id;

        public string Pattern
        {
            get { return _pattern; }
            set
            {
                if (SetProperty(ref _pattern, value))
                    Validate();
            }
        }
        private string _pattern;

        public MergePolicyAction Action
        {
            get { return _action; }
            set
            {
                if (SetProperty(ref _action, value))
                    Validate();
            }
        }
        private MergePolicyAction _action;

        public string FilePattern
        {
            get { return _filePattern; }
            set
            {
                if (SetProperty(ref _filePattern, value))
                    Validate();
            }
        }
        private string _filePattern;

        public bool IsBlockMode
        {
            get { return _isBlockMode; }
            set
            {
                if (SetProperty(ref _isBlockMode, value))
                {
                    OnPropertyChanged("ShowLinePattern");
                    OnPropertyChanged("ShowBlockPatterns");
                    Validate();
                }
            }
        }
        private bool _isBlockMode;

        public string LinePattern
        {
            get { return _linePattern; }
            set
            {
                if (SetProperty(ref _linePattern, value))
                    Validate();
            }
        }
        private string _linePattern;

        public string BlockStartPattern
        {
            get { return _blockStartPattern; }
            set
            {
                if (SetProperty(ref _blockStartPattern, value))
                    Validate();
            }
        }
        private string _blockStartPattern;

        public string BlockEndPattern
        {
            get { return _blockEndPattern; }
            set
            {
                if (SetProperty(ref _blockEndPattern, value))
                    Validate();
            }
        }
        private string _blockEndPattern;

        public string BlockContainsPattern
        {
            get { return _blockContainsPattern; }
            set
            {
                if (SetProperty(ref _blockContainsPattern, value))
                    Validate();
            }
        }
        private string _blockContainsPattern;

        public string Description
        {
            get { return _description; }
            set { SetProperty(ref _description, value); }
        }
        private string _description;

        public bool IsEnabled
        {
            get { return _isEnabled; }
            set { SetProperty(ref _isEnabled, value); }
        }
        private bool _isEnabled;

        // Testo di prova per una regola di riga: quali righe risultano protette (calcolo dell'engine).
        public string SampleText
        {
            get { return _sampleText; }
            set
            {
                if (SetProperty(ref _sampleText, value))
                    UpdateSample();
            }
        }
        private string _sampleText;

        public string SampleResultText
        {
            get { return _sampleResultText; }
            private set { SetProperty(ref _sampleResultText, value); }
        }
        private string _sampleResultText;

        public string IdError
        {
            get { return _idError; }
            private set { SetProperty(ref _idError, value); }
        }
        private string _idError;

        public string IdInfo
        {
            get { return _idInfo; }
            private set { SetProperty(ref _idInfo, value); }
        }
        private string _idInfo;

        public string PatternError
        {
            get { return _patternError; }
            private set { SetProperty(ref _patternError, value); }
        }
        private string _patternError;

        public string FilePatternError
        {
            get { return _filePatternError; }
            private set { SetProperty(ref _filePatternError, value); }
        }
        private string _filePatternError;

        public string LinePatternError
        {
            get { return _linePatternError; }
            private set { SetProperty(ref _linePatternError, value); }
        }
        private string _linePatternError;

        public string BlockStartError
        {
            get { return _blockStartError; }
            private set { SetProperty(ref _blockStartError, value); }
        }
        private string _blockStartError;

        public string BlockEndError
        {
            get { return _blockEndError; }
            private set { SetProperty(ref _blockEndError, value); }
        }
        private string _blockEndError;

        public string BlockContainsError
        {
            get { return _blockContainsError; }
            private set { SetProperty(ref _blockContainsError, value); }
        }
        private string _blockContainsError;

        public string EngineErrorText
        {
            get { return _engineErrorText; }
            private set { SetProperty(ref _engineErrorText, value); }
        }
        private string _engineErrorText;

        public bool HasErrors
        {
            get { return _hasErrors; }
            private set { SetProperty(ref _hasErrors, value); }
        }
        private bool _hasErrors;

        internal MergePathRule BuildPathRule()
        {
            return new MergePathRule
            {
                Id = (Id ?? string.Empty).Trim(),
                Pattern = NormalizeGlob(Pattern),
                Action = Action,
                Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                Enabled = IsEnabled
            };
        }

        internal MergeLineRule BuildLineRule()
        {
            return new MergeLineRule
            {
                Id = (Id ?? string.Empty).Trim(),
                FilePattern = NormalizeGlob(FilePattern),
                LinePattern = IsBlockMode ? null : LinePattern,
                BlockStartPattern = IsBlockMode ? BlockStartPattern : null,
                BlockEndPattern = IsBlockMode ? BlockEndPattern : null,
                BlockContainsPattern = IsBlockMode && !string.IsNullOrEmpty(BlockContainsPattern) ? BlockContainsPattern : null,
                Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                Enabled = IsEnabled
            };
        }

        private static string NormalizeGlob(string pattern)
        {
            return (pattern ?? string.Empty).Trim();
        }

        private void Validate()
        {
            var id = (Id ?? string.Empty).Trim();
            if (id.Length == 0)
                IdError = "Enter an Id: rules with the same Id in the personal file replace the team rule.";
            else if (_otherIds.Contains(id))
                IdError = "Another rule of this file already has this Id.";
            else
                IdError = null;
            IdInfo = IdError == null && Origin == MergeRuleOrigin.Personal && _teamIds.Contains(id)
                ? "This personal rule replaces the team rule '" + id + "' for you (personal rules always win)."
                : null;

            if (IsPathRule)
            {
                PatternError = GlobError(Pattern);
                FilePatternError = null;
                LinePatternError = null;
                BlockStartError = null;
                BlockEndError = null;
                BlockContainsError = null;
            }
            else
            {
                PatternError = null;
                FilePatternError = GlobError(FilePattern);
                if (IsBlockMode)
                {
                    LinePatternError = null;
                    BlockStartError = RegexError(BlockStartPattern, true);
                    BlockEndError = RegexError(BlockEndPattern, true);
                    BlockContainsError = RegexError(BlockContainsPattern, false);
                }
                else
                {
                    LinePatternError = RegexError(LinePattern, true);
                    BlockStartError = null;
                    BlockEndError = null;
                    BlockContainsError = null;
                }
            }

            var fieldErrors = IdError != null || PatternError != null || FilePatternError != null || LinePatternError != null
                || BlockStartError != null || BlockEndError != null || BlockContainsError != null;

            // Il giudizio dell'engine sulla sola regola (solo se i campi sono a posto: niente doppioni).
            // Abilitata per il controllo: l'engine verifica solo le regole attive, ma una regola salvata
            // disattivata deve essere valida il giorno che la si riattiva.
            EngineErrorText = null;
            if (!fieldErrors)
            {
                var doc = MergePolicyViewModel.NewDocument();
                if (IsPathRule)
                {
                    var rule = BuildPathRule();
                    rule.Enabled = true;
                    doc.PathRules.Add(rule);
                }
                else
                {
                    var rule = BuildLineRule();
                    rule.Enabled = true;
                    doc.LineRules.Add(rule);
                }
                var errors = MergePolicyViewModel.ValidateDocument(doc);
                if (errors.Count > 0)
                    EngineErrorText = string.Join(" ", errors);
            }

            HasErrors = fieldErrors || EngineErrorText != null;
            var handler = ValidityChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);

            UpdateSample();
        }

        private static string GlobError(string pattern)
        {
            var text = (pattern ?? string.Empty).Trim();
            if (text.Length == 0)
                return "Enter a pattern, e.g. **/packages.config or src/Legacy/**";
            if (text.IndexOf('\\') >= 0)
                return "Use '/' between folders.";
            if (text.StartsWith("$", StringComparison.Ordinal) || (text.Length > 1 && text[1] == ':'))
                return "Use a path relative to the root of the branch, e.g. src/**/*.config";
            return null;
        }

        private static string RegexError(string pattern, bool required)
        {
            if (string.IsNullOrEmpty(pattern))
                return required ? "Enter a regular expression." : null;
            try
            {
                new Regex(pattern);
                return null;
            }
            catch (ArgumentException ex)
            {
                return "Not a valid regular expression: " + ex.Message;
            }
        }

        private void UpdateSample()
        {
            if (!IsLineRule || string.IsNullOrEmpty(SampleText))
            {
                SampleResultText = null;
                return;
            }
            if (HasErrors)
            {
                SampleResultText = "Fix the rule to test it.";
                return;
            }

            try
            {
                var rule = BuildLineRule();
                rule.Enabled = true;
                var result = MergePolicyEngine.MergeWithProtectedLines(string.Empty, SampleText, string.Empty, new List<MergeLineRule> { rule });
                var kept = result.KeptTargetDifferences ?? new List<ProtectedLineDifference>();
                if (kept.Count == 0)
                {
                    SampleResultText = "No line of the sample is protected by this rule.";
                    return;
                }
                var lines = kept
                    .Select(d => !string.IsNullOrEmpty(d.SourceText) ? d.SourceText : d.Summary)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Select(t => t.Replace("\r", string.Empty).Replace("\n", " | ").Trim())
                    .Take(6)
                    .ToList();
                SampleResultText = "Protected in the sample (the target version would be kept): " + string.Join("  /  ", lines)
                    + (kept.Count > lines.Count ? "  ..." : "");
            }
            catch (Exception ex)
            {
                SampleResultText = "Cannot test the rule: " + ex.Message;
            }
        }
    }
}
