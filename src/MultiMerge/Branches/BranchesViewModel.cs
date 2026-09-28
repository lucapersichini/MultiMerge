// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MultiMerge.Events;
using MultiMerge.Prism.Command;
using MultiMerge.Prism.Events;
using EnvDTE80;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Common.Internal;
using Microsoft.TeamFoundation.Controls;
using Microsoft.TeamFoundation.Controls.WPF.TeamExplorer;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.VersionControl.Common;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.VisualStudio.Shell.Interop;
using TeamExplorerSectionViewModelBase = MultiMerge.Base.TeamExplorerSectionViewModelBase;

namespace MultiMerge
{
    public sealed class BranchesViewModel : TeamExplorerSectionViewModelBase
    {
        private readonly IEventAggregator _eventAggregator;
        private ChangesetService _changesetService;
        private Workspace _workspace;

        private ChangesetViewModel _changeset;
        private TaskChangesetGroup _taskChangesetGroup;
        private bool _merging;

        public BranchesViewModel(ILogger logger)
            : base(logger)
        {
            Title = Resources.BrancheSectionName;
            IsVisible = true;
            IsExpanded = true;
            IsBusy = false;

            MergeCommand = new DelegateCommand<MergeMode?>(MergeExecute, m => MergeCanEcexute());
            SelectWorkspaceCommand = new DelegateCommand<Workspace>(SelectWorkspaceExecute);
            OpenSourceControlExplorerCommand = new DelegateCommand(OpenSourceControlExplorerExecute, OpenSourceControlExplorerCanExecute);

            _eventAggregator = EventAggregatorFactory.Get();
            _merging = false;
        }

        public ObservableCollection<MergeInfoViewModel> Branches
        {
            get
            {
                return _branches;
            }
            set
            {
                _branches = value;
                RaisePropertyChanged("Branches");
            }
        }
        private ObservableCollection<MergeInfoViewModel> _branches;

        private MergeInfoViewModel _selectedBranch;

        public MergeInfoViewModel SelectedBranch
        {
            get
            {
                return _selectedBranch;
            }
            set
            {
                _selectedBranch = value;
                RaisePropertyChanged("SelectedBranch");
            }
        }

        public DelegateCommand<MergeMode?> MergeCommand { get; private set; }

        public MergeOption MergeOption
        {
            get { return _mergeOption; }
            set
            {
                _mergeOption = value;
                RaisePropertyChanged("MergeOption");
            }
        }
        private MergeOption _mergeOption;

        public string ErrorMessage
        {
            get
            {
                return _errorMessage;
            }
            set
            {
                _errorMessage = value;
                RaisePropertyChanged("ErrorMessage");
            }
        }
        private string _errorMessage;

        public Workspace Workspace
        {
            get
            {
                return _workspace;
            }
            set
            {
                _workspace = value;
                RaisePropertyChanged("Workspace");
            }
        }

        private ObservableCollection<Workspace> _workspaces;

        public ObservableCollection<Workspace> Workspaces
        {
            get
            {
                return _workspaces;
            }
            set
            {
                _workspaces = value;
                RaisePropertyChanged("Workspaces");
            }
        }

        private bool _showWorkspaceChooser;
        public bool ShowWorkspaceChooser
        {
            get
            {
                return _showWorkspaceChooser;
            }
            set
            {
                _showWorkspaceChooser = value;
                RaisePropertyChanged("ShowWorkspaceChooser");
            }
        }

        private MergeMode _mergeMode;
        public MergeMode MergeMode
        {
            get
            {
                return _mergeMode;
            }
            set
            {
                _mergeMode = value;
                RaisePropertyChanged("MergeMode");
            }
        }

        private ObservableCollection<MergeMode> _mergeModes;

        public ObservableCollection<MergeMode> MergeModes
        {
            get
            {
                return _mergeModes;
            }
            set
            {
                _mergeModes = value;
                RaisePropertyChanged("MergeModes");
            }
        }

        public DelegateCommand<Workspace> SelectWorkspaceCommand { get; set; }

        public DelegateCommand OpenSourceControlExplorerCommand { get; set; }

        private static ObservableCollection<Workspace> GetWorkspaces(VersionControlServer versionControl, TfsTeamProjectCollection tfs)
        {
            var queryWorkspaces = versionControl.QueryWorkspaces(null, tfs.AuthorizedIdentity.UniqueName, Environment.MachineName);
            if (queryWorkspaces.Length > 1)
            {
                return new ObservableCollection<Workspace>(queryWorkspaces.OrderBy(w => w.Name));
            }
            return new ObservableCollection<Workspace>(queryWorkspaces);
        }

        protected async override Task InitializeAsync(object sender, SectionInitializeEventArgs e)
        {
            Logger.Debug("Start initilize branches section");

            var tfs = Context.TeamProjectCollection;
            var versionControl = tfs.GetService<VersionControlServer>();
            SubscribeWorkspaceChanges(versionControl);

            _changesetService = new ChangesetService(versionControl);

            _eventAggregator.GetEvent<SelectChangesetEvent>()
                .Subscribe(OnSelectedChangeset);
            _eventAggregator.GetEvent<SelectTaskChangesetGroupEvent>()
                .Subscribe(OnSelectedTaskChangesetGroup);
            _eventAggregator.GetEvent<BranchSelectedChangedEvent>()
                .Subscribe(OnBranchSelectedChanged);

            if (e.Context == null)
            {
                Workspaces = GetWorkspaces(versionControl, tfs);
                if (Workspaces.Count > 0)
                {
                    Workspace = WorkspaceHelper.GetWorkspace(versionControl, Workspaces);
                    ShowWorkspaceChooser = Workspaces.Count > 1;
                }
                else
                {
                    Workspace = null;
                }

                MergeModes = new ObservableCollection<MergeMode>
                {
                    MergeMode.Merge,
                    MergeMode.MergeAndCheckIn
                };
                MergeMode = Settings.Instance.LastMergeOperation;

                RestoreActiveTask();
                await RefreshAsync();
            }
            else
            {
                RestoreContext(e);
                // Con un task attivo si ricalcola sempre: la validazione (conflitti ancora aperti o
                // risolti) e l'avanzamento vanno letti dallo stato attuale del workspace, non da
                // quello salvato prima di andare a risolvere i conflitti.
                if (RestoreActiveTask())
                    await RefreshAsync();
            }

            Logger.Debug("End initialize branches section");
        }

        // Riprende il task che pilotava il pannello prima che la pagina venisse ricreata (vedi
        // TaskMergeSession). Letto qui direttamente, non via evento, per non dipendere dall'ordine
        // in cui Team Explorer inizializza le sezioni.
        private bool RestoreActiveTask()
        {
            var tfs = Context == null ? null : Context.TeamProjectCollection;
            TaskMergeSession.EnsureCollection(tfs == null ? null : tfs.Uri);

            var group = TaskMergeSession.ActiveGroup;
            if (group == null)
                return false;

            _taskChangesetGroup = group;
            _changeset = null;
            EnsureWorkspaceMapsTargets(group);
            return true;
        }

        // Sceglie la workspace per il task: prima quella in cui la catena ha lavorato l'ultima volta
        // (TaskMergeSession.WorkspaceName) se esiste ancora e mappa i target; altrimenti tiene quella
        // corrente se li mappa; altrimenti la prima che li mappa tutti. Senza questo, ad ogni ritorno
        // sulla pagina si ripartirebbe dalla workspace di default e il target risulterebbe "Branch
        // not mapped" anche se un'altra workspace lo mappa. Se nessuna li mappa lascia tutto com'e'
        // (la validazione mostrera' "Branch not mapped", che e' l'informazione corretta).
        private void EnsureWorkspaceMapsTargets(TaskChangesetGroup group)
        {
            if (group.TargetBranchPaths == null || group.TargetBranchPaths.Count == 0
                || Workspaces == null || Workspaces.Count == 0)
                return;

            Func<Workspace, bool> mapsAllTargets = w => group.TargetBranchPaths.All(w.IsServerPathMapped);

            var chainWorkspace = TaskMergeSession.WorkspaceName == null
                ? null
                : Workspaces.FirstOrDefault(w => string.Equals(w.QualifiedName, TaskMergeSession.WorkspaceName, StringComparison.OrdinalIgnoreCase));
            if (chainWorkspace != null && mapsAllTargets(chainWorkspace))
            {
                if (!ReferenceEquals(chainWorkspace, _workspace))
                    Workspace = chainWorkspace;
                return;
            }

            if (_workspace != null && mapsAllTargets(_workspace))
                return;

            var candidate = Workspaces.FirstOrDefault(mapsAllTargets);
            if (candidate != null)
                Workspace = candidate;
        }

        private void OnBranchSelectedChanged(MergeInfoViewModel obj)
        {
            MergeCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Refresh the changeset data asynchronously.
        /// </summary>
        protected override async Task RefreshAsync()
        {
            var changeset = _changeset;
            var taskGroup = _taskChangesetGroup;
            Logger.Debug("Start refresh branches section for changeset {0} ...",
                changeset == null ? "null" : changeset.ChangesetId.ToString(CultureInfo.InvariantCulture));

            string errorMessage = null;
            if (Workspaces.Count == 0)
            {
                errorMessage = "Workspaces not found";
            }

            if (taskGroup != null)
            {
                errorMessage = errorMessage ?? (taskGroup.ChangesetIds.Count == 0 ? "Task has no changesets for this branch" : null);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    ErrorMessage = errorMessage;
                    Branches = new ObservableCollection<MergeInfoViewModel>();
                }
                else
                {
                    // GetBranchesForTask puo' far esplodere QueryHistory (HasThirdPartyChangesetBetween)
                    // se il path e' stato da allora rinominato/spostato/cancellato: senza questo try/catch
                    // il calcolo dei batch (e quindi il popolamento della lista branch) esplode invece di
                    // degradare in un messaggio d'errore leggibile, stesso pattern gia' usato da LoadTaskExecute.
                    try
                    {
                        var branches = await Task.Run(() => GetBranchesForTask(Context, taskGroup));
                        if (ReferenceEquals(taskGroup, _taskChangesetGroup))
                        {
                            Branches = branches;
                            ErrorMessage = branches.Count <= 1 ? "Target branches not found" : null;
                            MergeCommand.RaiseCanExecuteChanged();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("Error while getting branches for task {0}", taskGroup.WorkItemId), ex);
                        if (ReferenceEquals(taskGroup, _taskChangesetGroup))
                        {
                            ErrorMessage = ex.Message;
                            Branches = new ObservableCollection<MergeInfoViewModel>();
                            MergeCommand.RaiseCanExecuteChanged();
                        }
                    }
                }
                return;
            }

            errorMessage = errorMessage ?? CalculateError(changeset);
            if (changeset == null || !string.IsNullOrEmpty(errorMessage))
            {
                ErrorMessage = errorMessage;
                Branches = new ObservableCollection<MergeInfoViewModel>();
            }
            else
            {
                Logger.Info("Getting branches for changeset {0} ...",
                    changeset.ChangesetId.ToString(CultureInfo.InvariantCulture));
                var branches = await Task.Run(() => GetBranches(Context, changeset));
                Logger.Info("Getting branches end for changeset {0}",
                    changeset.ChangesetId.ToString(CultureInfo.InvariantCulture));

                // Selected changeset in sequence. Confronta sulla variabile locale catturata a inizio
                // metodo, non sul campo _changeset: OnSelectedTaskChangesetGroup può impostare
                // _changeset = null mentre questo Task.Run (fetch di rete) è ancora in corso, il che
                // farebbe lanciare NullReferenceException leggendo _changeset.ChangesetId qui.
                // Stesso pattern del ramo Task gemello, che già usa ReferenceEquals sulla variabile locale.
                if (ReferenceEquals(changeset, _changeset))
                {
                    Branches = branches;
                    ErrorMessage = branches.Count <= 1 ? "Target branches not found" : null;
                    MergeCommand.RaiseCanExecuteChanged();
                }
            }
            Logger.Debug("End refresh branches section for changeset {0}",
                changeset == null ? "null" : changeset.ChangesetId.ToString(CultureInfo.InvariantCulture));
        }

        private static string CalculateError(ChangesetViewModel changeset)
        {
            if (changeset == null)
                return "Changeset not selected";

            if (changeset.Branches.IsNullOrEmpty())
                return "Changeset has not branch";

            if (changeset.Branches.Count > 1)
                return string.Format("Changeset has {0} branches. Merge not possible.", changeset.Branches.Count);

            return null;
        }

        private void OnSelectedChangeset(ChangesetViewModel changeset)
        {
            _changeset = changeset;
            _taskChangesetGroup = null;
            Refresh();
        }

        private void OnSelectedTaskChangesetGroup(TaskChangesetGroup group)
        {
            _taskChangesetGroup = group;
            _changeset = null;
            if (group != null)
                EnsureWorkspaceMapsTargets(group);
            Refresh();
        }

        private ObservableCollection<MergeInfoViewModel> GetBranches(ITeamFoundationContext context, ChangesetViewModel changesetViewModel)
        {
            if (context == null)
                return new ObservableCollection<MergeInfoViewModel>();

            var tfs = context.TeamProjectCollection;
            var versionControl = tfs.GetService<VersionControlServer>();
            var workspace = _workspace;
            var changesetService = _changesetService;

            var changes = changesetService.GetChanges(changesetViewModel.ChangesetId);
            var sourceTopFolder = CalculateTopFolder(changes);
            var mergesRelationships = GetMergesRelationships(sourceTopFolder, versionControl);

            if (mergesRelationships.Count == 0)
                return new ObservableCollection<MergeInfoViewModel>();

            var sourceBranchIdentifier = changesetViewModel.Branches.Select(b => new ItemIdentifier(b)).First();
            var sourceBranch = sourceBranchIdentifier.Item;
            var trackMerges = versionControl.TrackMerges(new[] { changesetViewModel.ChangesetId },
                new ItemIdentifier(sourceTopFolder), mergesRelationships.ToArray(), null);
            var changesetVersionSpec = new ChangesetVersionSpec(changesetViewModel.ChangesetId);
            var branchValidator = new BranchValidator(workspace, trackMerges);
            var branchFactory = new BranchFactory(sourceBranch, sourceTopFolder, changesetVersionSpec, branchValidator, _eventAggregator);

            return BuildTargetBranches(versionControl, sourceBranchIdentifier, sourceBranch, mergesRelationships, branchFactory);
        }

        private ObservableCollection<MergeInfoViewModel> GetBranchesForTask(ITeamFoundationContext context, TaskChangesetGroup group)
        {
            if (context == null || group == null || group.ChangesetIds.Count == 0)
                return new ObservableCollection<MergeInfoViewModel>();

            var tfs = context.TeamProjectCollection;
            var versionControl = tfs.GetService<VersionControlServer>();
            var changesetService = _changesetService;

            // Change[] per changeset: calcolato una sola volta e messo in cache sul gruppo (fix #5),
            // così MergeExecuteInternalForTask non deve richiederlo di nuovo al server quando l'utente
            // clicca Merge.
            var changesByChangesetId = group.ChangesByChangesetId;
            if (changesByChangesetId == null)
            {
                changesByChangesetId = new Dictionary<int, Change[]>();
                foreach (var id in group.ChangesetIds)
                    changesByChangesetId[id] = changesetService.GetChanges(id);
                group.ChangesByChangesetId = changesByChangesetId;
            }

            var sourceTopFolder = CalculateGroupTopFolder(group.ChangesetIds.Select(id => changesByChangesetId[id]));

            // Flusso Task: niente ricerca automatica delle relazioni di merge (QueryMergeRelationships +
            // QueryBranchObjectOwnership), misurata a 186s su un branch reale con 25 figli senza comunque
            // trovare un target valido. I target sono scelti a mano dall'utente in WorkItemMergeView
            // (TargetBranchesText -> WorkItemMergeViewModel.LoadTaskExecute -> group.TargetBranchPaths).
            // Il flusso a changeset singolo (GetBranches/BuildTargetBranches) resta invariato: continua
            // a scoprire i target automaticamente, perché lavora su un solo changeset con una cartella
            // sorgente stretta, non su un intero gruppo.
            if (group.TargetBranchPaths.IsNullOrEmpty())
            {
                // Difesa: la UI dovrebbe già impedire questo caso (LoadTaskExecute richiede targets non
                // vuoti prima di popolare Groups), stesso pattern del controllo ChangesetIds.Count == 0
                // all'inizio del metodo.
                Logger.Error(string.Format("Task {0}: no target branch specified", group.WorkItemId));
                return new ObservableCollection<MergeInfoViewModel>();
            }

            // Il top folder puo' essere una sottocartella del branch sorgente: la stessa sottocartella va
            // riportata sotto ogni target (TaskTargetPathMapper), sia per TrackMerges sia per il merge.
            var mergesRelationships = group.TargetBranchPaths
                .Select(p => new ItemIdentifier(TaskTargetPathMapper.MapToTarget(group.SourceBranch, sourceTopFolder, p)))
                .ToList();

            Logger.Info("Task {0}: sourceBranch={1}, computed sourceTopFolder={2}, manual targets={3}",
                group.WorkItemId, group.SourceBranch, sourceTopFolder ?? "(null)",
                string.Join(", ", mergesRelationships.Select(r => r.Item)));

            var sourceBranch = group.SourceBranch;

            var trackMerges = versionControl.TrackMerges(group.ChangesetIds.ToArray(),
                new ItemIdentifier(sourceTopFolder), mergesRelationships.ToArray(), null);

            // TENTATO E RITIRATO: restringere lo scope del controllo di contiguità alla sola coppia
            // (fromId, toId) confrontata in un'iterazione, invece del top-folder dell'intero gruppo,
            // era una REGRESSIONE DI CORRETTEZZA. Il merge reale (MergeToBranch -> workspace.Merge)
            // usa SEMPRE mergeInfoeViewModel.SourcePath, che BranchFactory imposta a sourceTopFolder
            // con RecursionType.Full sull'intero gruppo: lo scope vero del merge resta quello ampio.
            // Se il controllo di contiguità guardasse solo lo scope stretto della coppia, un changeset
            // di terzi ricadente DENTRO sourceTopFolder ma FUORI dallo scope stretto della coppia non
            // verrebbe più rilevato come "estraneo": il batch non si spezzerebbe dove dovrebbe, e il
            // merge reale (che opera sullo scope ampio) lo importerebbe comunque silenziosamente nel
            // target. Per questo il controllo usa sourceTopFolder: lo stesso path che il merge reale
            // userà davvero, cosicché lo scope del controllo corrisponda ESATTAMENTE allo scope del
            // merge reale. Comportamento attuale: corretto ma non ottimizzato per rami sorgente enormi
            // condivisi da molti progetti scollegati (i batch tenderanno a spezzarsi più spesso di
            // quanto sarebbe teoricamente possibile in quel caso), ma senza mai rischiare di fondere
            // codice non correlato al task.
            var batchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var thirdPartyCheckCount = 0;
            var batches = ChangesetBatchCalculator.CalculateBatches(group.ChangesetIds,
                (fromId, toId) =>
                {
                    thirdPartyCheckCount++;
                    var checkSw = System.Diagnostics.Stopwatch.StartNew();
                    var result = HasThirdPartyChangesetBetween(versionControl, sourceTopFolder, fromId, toId);
                    Logger.Debug("Task {0}: HasThirdPartyChangesetBetween #{1} (C{2}~C{3}) = {4} in {5} ms",
                        group.WorkItemId, thirdPartyCheckCount, fromId, toId, result, checkSw.ElapsedMilliseconds);
                    return result;
                });
            Logger.Info("Task {0}: CalculateBatches done, {1} checks, {2} batches, total {3} ms",
                group.WorkItemId, thirdPartyCheckCount, batches.Count, batchStopwatch.ElapsedMilliseconds);

            var branchValidator = new BranchValidator(_workspace, trackMerges, group.ChangesetIds);
            var branchFactory = new BranchFactory(sourceBranch, sourceTopFolder, batches, branchValidator, _eventAggregator);

            Logger.Info("Task {0}: validating with workspace '{1}'",
                group.WorkItemId, _workspace == null ? "(null)" : _workspace.Name);

            var buildSw = System.Diagnostics.Stopwatch.StartNew();
            var buildResult = BuildManualTargetBranches(branchFactory, group, sourceTopFolder);
            ApplyChainProgress(buildResult, group, batches, trackMerges);
            Logger.Info("Task {0}: BuildManualTargetBranches done, {1} target branches, total {2} ms",
                group.WorkItemId, buildResult.Count, buildSw.ElapsedMilliseconds);
            return buildResult;
        }

        // Badge "X/Y remaining": batch i cui changeset non risultano ancora tutti fusi nel target
        // secondo la storia dei merge sul server, con lo stesso criterio di "Already merged"
        // (BranchValidator.GetMergedChangesetIds). Solo informativo: Merge riparte comunque sempre dal
        // primo batch (vedi MergeToBranch).
        private static void ApplyChainProgress(IEnumerable<MergeInfoViewModel> branches, TaskChangesetGroup group,
            List<ChangesetBatch> batches, ExtendedMerge[] trackMerges)
        {
            foreach (var mergeInfo in branches)
            {
                if (mergeInfo.IsSourceBranch)
                    continue;

                var merged = BranchValidator.GetMergedChangesetIds(mergeInfo.SourcePath, mergeInfo.TargetPath, trackMerges);
                mergeInfo.RemainingBatches = batches.Count(batch => group.ChangesetIds
                    .Where(id => id >= batch.FromChangesetId && id <= batch.ToChangesetId)
                    .Any(id => !merged.Contains(id)));
            }
        }

        // Cache-first: riusa il Change[] già popolato su group.ChangesByChangesetId (da GetBranchesForTask)
        // invece di richiederlo di nuovo al server; ricade su ChangesetService.GetChanges solo se il
        // gruppo non è mai passato per il calcolo dei branch (percorso difensivo, non dovrebbe capitare
        // nell'uso normale perché Merge è abilitato solo dopo che Branches è stato popolato).
        private Change[] GetChangesForTaskChangeset(TaskChangesetGroup group, int changesetId)
        {
            Change[] changes;
            if (group.ChangesByChangesetId != null && group.ChangesByChangesetId.TryGetValue(changesetId, out changes))
                return changes;

            return _changesetService.GetChanges(changesetId);
        }

        private ObservableCollection<MergeInfoViewModel> BuildTargetBranches(
            VersionControlServer versionControl,
            ItemIdentifier sourceBranchIdentifier,
            string sourceBranch,
            List<ItemIdentifier> mergesRelationships,
            BranchFactory branchFactory)
        {
            var result = new ObservableCollection<MergeInfoViewModel>();
            var diagSw = System.Diagnostics.Stopwatch.StartNew();
            var validateCount = 0;
            var validateTotalMs = 0L;
            Func<ItemIdentifier, ItemIdentifier, MergeInfoViewModel> timedCreateTarget = (targetBranch, targetPath) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = branchFactory.CreateTargetBranchInfo(targetBranch, targetPath);
                validateCount++;
                validateTotalMs += sw.ElapsedMilliseconds;
                return r;
            };

            var qboSw = System.Diagnostics.Stopwatch.StartNew();
            var sourceBranchInfo = versionControl.QueryBranchObjects(sourceBranchIdentifier, RecursionType.None)[0];
            Logger.Debug("BuildTargetBranches: QueryBranchObjects took {0} ms", qboSw.ElapsedMilliseconds);

            var propsSw = System.Diagnostics.Stopwatch.StartNew();
            var hasParent = sourceBranchInfo.Properties != null && sourceBranchInfo.Properties.ParentBranch != null
                && !sourceBranchInfo.Properties.ParentBranch.IsDeleted;
            Logger.Debug("BuildTargetBranches: Properties/ParentBranch access took {0} ms (hasParent={1})", propsSw.ElapsedMilliseconds, hasParent);

            if (hasParent)
            {
                var targetBranch = sourceBranchInfo.Properties.ParentBranch;
                var gtpSw = System.Diagnostics.Stopwatch.StartNew();
                var targetPath = GetTargetPath(mergesRelationships, targetBranch);
                Logger.Debug("BuildTargetBranches: GetTargetPath(parent) took {0} ms (found={1})", gtpSw.ElapsedMilliseconds, targetPath != null);
                if (targetPath != null)
                {
                    var mergeInfo = timedCreateTarget(targetBranch, targetPath);
                    Logger.Debug("BuildTargetBranches: timedCreateTarget(parent) done, {0} ms cumulative so far", validateTotalMs);
                    mergeInfo._checked = mergeInfo.ValidationResult == BranchValidationResult.Success;

                    result.Add(mergeInfo);
                }
            }

            var currentBranchInfo = branchFactory.CreateSourceBranch();
            result.Add(currentBranchInfo);

            var childSw = System.Diagnostics.Stopwatch.StartNew();
            var childBranchesRaw = sourceBranchInfo.ChildBranches;
            Logger.Debug("BuildTargetBranches: ChildBranches access took {0} ms (count={1})",
                childSw.ElapsedMilliseconds, childBranchesRaw == null ? -1 : childBranchesRaw.Count());

            if (childBranchesRaw != null)
            {
                var childBranches = childBranchesRaw.Where(b => !b.IsDeleted)
                    .Reverse();
                var childIndex = 0;
                foreach (var childBranch in childBranches)
                {
                    childIndex++;
                    var targetBranch = childBranch;
                    var gtpSw = System.Diagnostics.Stopwatch.StartNew();
                    var targetPath = GetTargetPath(mergesRelationships, targetBranch);
                    Logger.Debug("BuildTargetBranches: GetTargetPath(child #{0}) took {1} ms (found={2})",
                        childIndex, gtpSw.ElapsedMilliseconds, targetPath != null);
                    if (targetPath != null)
                    {
                        var mergeInfo = timedCreateTarget(targetBranch, targetPath);
                        Logger.Debug("BuildTargetBranches: timedCreateTarget(child #{0}) done, {1} ms cumulative so far", childIndex, validateTotalMs);
                        result.Add(mergeInfo);
                    }
                }
            }

            // Feature branch
            if (mergesRelationships.Count > 0)
            {
                var gabSw = System.Diagnostics.Stopwatch.StartNew();
                var changetIds =
                    mergesRelationships.Select(r => r.Version).Cast<ChangesetVersionSpec>().Select(c => c.ChangesetId)
                    .Distinct()
                    .ToArray();
                var branches = _changesetService.GetAssociatedBranches(changetIds);
                Logger.Debug("BuildTargetBranches: GetAssociatedBranches ({0} ids) took {1} ms", changetIds.Length, gabSw.ElapsedMilliseconds);

                foreach (var mergesRelationship in mergesRelationships)
                {
                    var targetBranch = branches.FirstOrDefault(b => IsTargetPath(mergesRelationship, b));
                    if (targetBranch != null)
                    {
                        var mergeInfo = timedCreateTarget(targetBranch, mergesRelationship);
                        result.Add(mergeInfo);
                    }
                }
            }

            Logger.Info("BuildTargetBranches: {0} target branches, {1} ms cumulative validate, {2} ms total method",
                validateCount, validateTotalMs, diagSw.ElapsedMilliseconds);

            return result;
        }

        // Flusso Task: nessuna scoperta automatica (niente QueryBranchObjects né GetAssociatedBranches).
        // I target sono già noti (scelti a mano dall'utente): si costruisce direttamente un
        // MergeInfoViewModel per il branch sorgente (riga di riferimento, stesso comportamento di
        // BuildTargetBranches) e uno per ciascun target: targetBranch e' la stringa digitata
        // dall'utente (radice del branch), targetPath la stessa sottocartella del top folder sorgente
        // riportata sotto quella radice (TaskTargetPathMapper), che e' il path su cui lavora il merge.
        // La validazione avviene comunque, dentro BranchFactory.CreateTargetBranchInfo tramite
        // branchValidator (invariato).
        private static ObservableCollection<MergeInfoViewModel> BuildManualTargetBranches(
            BranchFactory branchFactory,
            TaskChangesetGroup group,
            string sourceTopFolder)
        {
            var result = new ObservableCollection<MergeInfoViewModel> { branchFactory.CreateSourceBranch() };

            foreach (var path in group.TargetBranchPaths)
            {
                var targetPath = TaskTargetPathMapper.MapToTarget(group.SourceBranch, sourceTopFolder, path);
                var mergeInfo = branchFactory.CreateTargetBranchInfo(new ItemIdentifier(path), new ItemIdentifier(targetPath));
                result.Add(mergeInfo);
            }

            return result;
        }

        // Combina il "top folder" di CalculateTopFolder (metodo statico esistente, invariato) su TUTTI
        // i changeset del gruppo, non solo sul primo: un batch può coprire file toccati solo da un
        // changeset successivo del gruppo, quindi il source path deve coprirli tutti.
        internal static string CalculateGroupTopFolder(IEnumerable<Change[]> changesPerChangeset)
        {
            string topFolder = null;
            foreach (var changes in changesPerChangeset)
            {
                var folder = CalculateTopFolder(changes);
                topFolder = topFolder == null ? folder : FindShareFolder(topFolder, folder);
            }
            return topFolder;
        }

        // Il wrapper "esistono changeset di terzi sul path X tra A e B?" richiesto per il calcolo dei batch.
        // Usa l'overload di QueryHistory: path, version, deletionId, recursion, user, versionFrom, versionTo,
        // maxCount, includeChanges, slotMode.
        internal static bool HasThirdPartyChangesetBetween(VersionControlServer versionControl, string path,
            int fromChangesetId, int toChangesetId)
        {
            var history = versionControl.QueryHistory(
                path,
                // Ancora la risoluzione dell'identità a toChangesetId (garantito esistere al path, visto
                // che è il changeset più recente dell'intervallo), non a VersionSpec.Latest: se la cartella
                // è stata da allora rinominata/spostata/cancellata, Latest non riesce a risolvere l'item e
                // QueryHistory lancia un'eccezione invece di restituire la storia PRIMA di ora attesa.
                new ChangesetVersionSpec(toChangesetId),
                0,
                RecursionType.Full,
                null,                                   // user: nessun filtro, vogliamo TUTTI gli autori
                new ChangesetVersionSpec(fromChangesetId),
                new ChangesetVersionSpec(toChangesetId),
                int.MaxValue,
                false,                                  // includeChanges: non ci servono i file, solo gli ID
                true)                                    // slotMode: segue l'item per path nell'intervallo
                .Cast<Changeset>();

            return history.Any(c => c.ChangesetId != fromChangesetId && c.ChangesetId != toChangesetId);
        }

        private static List<ItemIdentifier> GetMergesRelationships(string sourceTopFolder, VersionControlServer versionControl)
        {
            return versionControl.QueryMergeRelationships(sourceTopFolder)
                .Where(r => !r.IsDeleted)
                .ToList();
        }

        private TrackMergeInfo GetTrackMergeInfo(VersionControlServer versionControl,
            IEnumerable<ExtendedMerge> allTrackMerges,
            string sourcePath)
        {
            var result = new TrackMergeInfo
            {
                FromOriginalToSourceBranches = new List<string>(),
            };
            var trackMerges = allTrackMerges.Where(m => m.TargetItem.Item == sourcePath).ToArray();
            if (!trackMerges.IsNullOrEmpty())
            {
                var changesetIds = trackMerges.Select(t => t.SourceChangeset.ChangesetId).ToArray();
                var mergeSourceBranches = _changesetService.GetAssociatedBranches(changesetIds)
                    .Select(b => b.Item)
                    .Distinct()
                    .ToArray();

                if (mergeSourceBranches.Length == 1)
                {
                    result.FromOriginalToSourceBranches.Add(mergeSourceBranches[0]);

                    var sourceFolder = trackMerges.First().SourceItem.Item.ServerItem;
                    var comment = trackMerges.First().SourceChangeset.Comment;
                    if (trackMerges.Length > 1)
                    {
                        foreach (var merge in trackMerges.Skip(1))
                            sourceFolder = FindShareFolder(sourceFolder, merge.SourceItem.Item.ServerItem);
                        comment = "source changeset has several comments";
                    }

                    var sourceMergesRelationships = versionControl.QueryMergeRelationships(sourceFolder)
                    .Where(r => !r.IsDeleted)
                    .ToArray();

                    var sourceTrackMerges = versionControl.TrackMerges(changesetIds,
                        new ItemIdentifier(sourceFolder),
                        sourceMergesRelationships,
                        null);

                    var sourceTrackMergeInfo = GetTrackMergeInfo(versionControl, sourceTrackMerges, sourceFolder);

                    if (!sourceTrackMergeInfo.FromOriginalToSourceBranches.IsNullOrEmpty())
                    {
                        result.FromOriginalToSourceBranches.AddRange(sourceTrackMergeInfo.FromOriginalToSourceBranches);
                        result.OriginalComment = sourceTrackMergeInfo.OriginalComment;
                        result.OriginaBranch = sourceTrackMergeInfo.OriginaBranch;
                    }
                    else
                    {
                        result.OriginalComment = comment;
                        result.OriginaBranch = mergeSourceBranches[0];
                    }
                }
                else
                {
                    result.FromOriginalToSourceBranches.Add("multi");
                    result.OriginaBranch = "multi";
                    result.OriginalComment = "source changeset has several comments";
                }
            }

            return result;
        }

        private static ItemIdentifier GetTargetPath(ICollection<ItemIdentifier> mergesRelationships, ItemIdentifier targetBranch)
        {
            if (mergesRelationships == null || mergesRelationships.Count == 0)
                return null;
            var targetItem = mergesRelationships.FirstOrDefault(m => IsTargetPath(m, targetBranch));
            if (targetItem != null)
            {
                mergesRelationships.Remove(targetItem);
                return targetItem;
            }

            return null;
        }

        private static bool IsTargetPath(ItemIdentifier mergeRelations, ItemIdentifier branch)
        {
            return mergeRelations.Item.Contains(branch.Item + "/");
        }

        private static string CalculateTopFolder(IList<Change> changes)
        {
            if (changes == null || changes.Count == 0)
                throw new ArgumentNullException("changes");

            string topFolder = null;
            if (changes.Count == 1 &&
                (changes[0].ChangeType.HasFlag(ChangeType.Edit)
                && !changes[0].ChangeType.HasFlag(ChangeType.Add)
                && !changes[0].ChangeType.HasFlag(ChangeType.Branch)))
            {
                topFolder = changes[0].Item.ServerItem;
            }
            else
            {
                foreach (var change in changes)
                {
//                    if (SkipChange(change.ChangeType, change.Item))
//                        continue;

//                    if (topFolder != null)
//                    {
//                        if (!IsNeedCalculateTopFolder(change.ChangeType, change.Item) change.Item.ServerItem.Contains(topFolder) && change.Item.ServerItem != topFolder)
//                            continue;
//                    }

                    var changeFolder = ExtractFolder(change.ChangeType, change.Item);
                    if (changeFolder != topFolder)
                        topFolder = FindShareFolder(topFolder, changeFolder);
                }
            }

            if (topFolder != null && topFolder.EndsWith("/"))
            {
                topFolder = topFolder.Substring(0, topFolder.Length - 1);
            }

            return topFolder;
        }

        private static string FindShareFolder(string topFolder, string changeFolder)
        {
            if ((topFolder == null) || topFolder.Contains(changeFolder))
            {
                return changeFolder;
            }
            const string rootFolder = "$/";
            var folder = topFolder;
            while (folder != rootFolder && !changeFolder.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                folder = ExtractParentFolder(folder);
                if (folder != null && changeFolder.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    break;
            }

            return folder == rootFolder ? folder + "/" : folder;
        }

//        private static bool SkipChange(ChangeType changeType, Item item)
//        {
//            return changeType.HasFlag(ChangeType.SourceRename) && changeType.HasFlag(ChangeType.Delete);
//        }

        private static string ExtractFolder(ChangeType changeType, Item item)
        {
            return ExtractFolder(changeType, item.ServerItem, item.ItemType);
        }

        private static string ExtractFolder(ChangeType changeType, string path, ItemType itemType)
        {
            if (IsNeedCalculateTopFolder(changeType, itemType))
                return ExtractParentFolder(path);

            return itemType == ItemType.Folder
                ? path
                : ExtractParentFolder(path);
        }

        private static bool IsNeedCalculateTopFolder(ChangeType changeType, ItemType itemType)
        {
            return ((changeType.HasFlag(ChangeType.Add)
                     || changeType.HasFlag(ChangeType.Branch)
                     || changeType.HasFlag(ChangeType.Rename)) && itemType == ItemType.Folder)
                   || (itemType == ItemType.File);

        }

        private static string ExtractParentFolder(string serverItem)
        {
            if (string.IsNullOrWhiteSpace(serverItem))
                throw new ArgumentNullException("serverItem");

            if (serverItem.EndsWith("/"))
                serverItem = serverItem.Substring(0, serverItem.Length - 1);

            var lastPosDelimiter = serverItem.LastIndexOf('/');
            if (lastPosDelimiter < 0)
                throw new InvalidOperationException(string.Format("Folder delimiter for {0} not found", serverItem));

            return serverItem.Substring(0, lastPosDelimiter + 1);
        }

        private async void MergeExecute(MergeMode? mergeMode)
        {
            Logger.Info("Merging start...");
            if (!mergeMode.HasValue)
                return;
            MergeMode = mergeMode.Value;
            Settings.Instance.LastMergeOperation = mergeMode.Value;
            // Flusso Task: ricorda in quale workspace lavora la catena, cosi' al ritorno sulla pagina
            // (ricreata da Team Explorer) si riapre quella e non la workspace di default.
            if (_taskChangesetGroup != null && _workspace != null)
                TaskMergeSession.WorkspaceName = _workspace.QualifiedName;

            var stayedOnPage = false;
            switch (mergeMode)
            {
                case MergeMode.Merge:
                    stayedOnPage = await MergeAndCheckInExecute(false);
                    break;
                case MergeMode.MergeAndCheckIn:
                    stayedOnPage = await MergeAndCheckInExecute(true);
                    break;
            }
            Logger.Info("Merging end");

            // Flusso Task: se la pagina resta aperta (es. Merge & Check In riuscito) badge e validazione
            // vanno ricalcolati dallo stato attuale del server. Se il merge ha portato su Pending Changes /
            // Resolve Conflicts il ricalcolo lo fa gia' la pagina ricreata al ritorno (RestoreActiveTask).
            if (stayedOnPage && _taskChangesetGroup != null)
                Refresh();
        }

        // true se al termine si resta sulla pagina MultiMerge (nessuna navigazione verso Pending Changes).
        private async Task<bool> MergeAndCheckInExecute(bool checkInIfSuccess)
        {
            var stayedOnPage = false;
            try
            {
                IsBusy = true;
                _merging = true;

                MergeCommand.RaiseCanExecuteChanged();

                var result = await Task.Run(() => MergeExecuteInternal(checkInIfSuccess));
                var notifications = new List<Notification>();
                var notCheckedIn = new List<MergeResultModel>(result.Count);
                ClearNotifications();
                foreach (var resultModel in result)
                {
                    var notification = new Notification
                    {
                        NotificationType = NotificationType.Information,
                        Message = string.Empty
                    };
                    var mergePath = string.Format("MERGE {0} -> {1}",
                        BranchHelper.GetShortBranchName(resultModel.BranchInfo.SourceBranch),
                        BranchHelper.GetShortBranchName(resultModel.BranchInfo.TargetBranch));
                    switch (resultModel.MergeResult)
                    {
                        case MergeResult.CheckInEvaluateFail:
                            notification.NotificationType = NotificationType.Error;
                            notification.Message = "Check In evaluate failed.";
                            notCheckedIn.Add(resultModel);
                            break;
                        case MergeResult.CheckInFail:
                            notification.NotificationType = NotificationType.Error;
                            notification.Message = "Check In failed.";
                            notCheckedIn.Add(resultModel);
                            break;
                        case MergeResult.NothingMerge:
                            notification.NotificationType = NotificationType.Warning;
                            notification.Message = "Nothing merged.";
                            break;
                        case MergeResult.HasConflicts:
                            notification.NotificationType = NotificationType.Error;
                            notification.Message = resultModel.BranchInfo.StoppedAtBatchNumber > 0
                                ? string.Format("Has conflicts at batch {0} of {1}. Resolve them, check in, then Merge again to continue.",
                                    resultModel.BranchInfo.StoppedAtBatchNumber, resultModel.BranchInfo.Batches.Count)
                                : "Has conflicts.";
                            notCheckedIn.Add(resultModel);
                            break;
                        case MergeResult.CanNotGetLatest:
                            notification.NotificationType = NotificationType.Error;
                            notification.Message = "Can not get latest.";
                            break;
                        case MergeResult.UnexpectedFileRestored:
                            notification.NotificationType = NotificationType.Warning;
                            notification.Message = "Some unexpected files were restored.";
                            notCheckedIn.Add(resultModel);
                            break;
                        case MergeResult.HasLocalChanges:
                            notification.NotificationType = NotificationType.Error;
                            notification.Message = "Target has pending changes: check in or undo them, then Merge again.";
                            break;
                        case MergeResult.MergeFailed:
                            notification.NotificationType = NotificationType.Error;
                            notification.Message = string.Format(
                                "Merge stopped at batch {0} of {1}: some items could not be merged (see Output, Source Control). Fix the cause, check in what was merged, then Merge again.",
                                resultModel.BranchInfo.StoppedAtBatchNumber,
                                resultModel.BranchInfo.Batches == null ? 0 : resultModel.BranchInfo.Batches.Count);
                            notCheckedIn.Add(resultModel);
                            break;
                        case MergeResult.Merged:
                            notification.NotificationType = NotificationType.Information;
                            notification.Message = "Files merged but not checked in.";
                            notCheckedIn.Add(resultModel);
                            break;
                        case MergeResult.CheckIn:
                            var changesetId = resultModel.TagetChangesetId.Value;
                            notification.NotificationType = NotificationType.Information;
                            notification.Message = string.Format("[Changeset {0}](Click to view the changeset details) successfully checked in.", changesetId);
                            notification.Command = new DelegateCommand(() => ViewChangesetDetailsExecute(changesetId));
                            break;
                    }
                    notification.Message = string.Format("{0}: {1}", mergePath, notification.Message);
                    if (!string.IsNullOrEmpty(notification.Message))
                        notifications.Add(notification);
                }

                if (notCheckedIn.Count > 0)
                {
                    OpenPendingChanges(notCheckedIn);
                }
                else
                {
                    _eventAggregator.GetEvent<MergeCompleteEvent>().Publish(true);
                    stayedOnPage = true;
                }

                foreach (var notification in notifications)
                {
                    ShowNotification(notification.Message, notification.NotificationType, NotificationFlags.RequiresConfirmation, notification.Command, Guid.NewGuid());
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error while merging", ex);
                ClearNotifications();
                ShowError(ex.Message);
            }
            finally
            {
                IsBusy = false;
                _merging = false;
                MergeCommand.RaiseCanExecuteChanged();
            }
            return stayedOnPage;
        }

        private string ConsolidateDuplicateComments(IEnumerable<MergeResultModel> resultModels)
        {
            return String.Join(";", resultModels.Select(rm => rm.Comment).Distinct());
        }
        
        private void OpenPendingChanges(ICollection<MergeResultModel> resultModels)
        {
            var pendingChanges = new List<PendingChange>(20);
            // all results must have identical workItems
            var workItemIds = resultModels.First().WorkItemIds;

            var conflictsPath = new List<string>();
            foreach (var resultModel in resultModels)
            {
                if (resultModel.PendingChanges != null && resultModel.PendingChanges.Count > 0)
                    pendingChanges.AddRange(resultModel.PendingChanges);

                if (resultModel.MergeResult == MergeResult.HasConflicts
                    || resultModel.MergeResult == MergeResult.UnexpectedFileRestored)
                    conflictsPath.Add(resultModel.BranchInfo.TargetPath);
            }

            if (conflictsPath.Count > 0)
                InvokeResolveConflictsPage(_workspace, conflictsPath.ToArray());
            OpenPendingChanges(pendingChanges, workItemIds, ConsolidateDuplicateComments(resultModels));
        }

        private void OpenPendingChanges(List<PendingChange> pendingChanges, List<int> workItemIds, string comment)
        {
            var teamExplorer = ServiceProvider.GetService<ITeamExplorer>();
            var pendingChangesPage = (TeamExplorerPageBase)teamExplorer.NavigateToPage(new Guid(TeamExplorerPageIds.PendingChanges), null);
            var model = (IPendingCheckin)pendingChangesPage.Model;
            model.PendingChanges.Comment = comment;
            model.PendingChanges.CheckedPendingChanges = pendingChanges.ToArray();

            if (Workspaces.Count > 1)
            {
                var modelType = model.GetType();
                var workspaceProperty = modelType.GetProperty("Workspace");
                workspaceProperty.SetValue(model, Workspace);
            }

            if (workItemIds != null && workItemIds.Count > 0)
            {
                var modelType = model.GetType();
                var method = modelType.GetMethod("AddWorkItemsByIdAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var workItemsIdsArray = workItemIds.ToArray();
                method.Invoke(model, new object[] { workItemsIdsArray, 1 /* Add */});
            }
        }

        private List<MergeResultModel> MergeExecuteInternal(bool checkInIfSuccess)
        {
            return _taskChangesetGroup != null
                ? MergeExecuteInternalForTask(checkInIfSuccess)
                : MergeExecuteInternalForChangeset(checkInIfSuccess);
        }

        private List<MergeResultModel> MergeExecuteInternalForChangeset(bool checkInIfSuccess)
        {
            var result = new List<MergeResultModel>();
            var context = Context;
            var tfs = context.TeamProjectCollection;
            var versionControl = tfs.GetService<VersionControlServer>();

            var workspace = _workspace;

            var changesetId = _changeset.ChangesetId;
            var changesetService = _changesetService;
            var changeset = changesetService.GetChangeset(changesetId);
            changeset.Changes = changesetService.GetChanges(changesetId);
            var mergeOption = _mergeOption;
            var workItemStore = tfs.GetService<WorkItemStore>();
            var workItemIds = changeset.AssociatedWorkItems != null
                ? changeset.AssociatedWorkItems.Select(w => w.Id).ToList()
                : new List<int>();

            var mergeInfos = _branches;
            var targetBranches = mergeInfos.Select(m => m.TargetBranch).ToArray();
            var pendingChanges = GetChangesetPendingChanges(changeset.Changes);
            var mergeRelationships = GetMergeRelationships(pendingChanges, targetBranches, versionControl);

            var commentFormater = new CommentFormater(Settings.Instance.CommentFormat);
            foreach (var mergeInfo in mergeInfos.Where(b => b.Checked))
            {
                var mergeResultModel = new MergeResultModel
                {
                    SourceChangesetId = changesetId,
                    BranchInfo = mergeInfo,
                };

                var mergeResult = MergeToBranch(mergeInfo, mergeOption, mergeRelationships, workspace);
                var targetPendingChanges = GetPendingChanges(mergeInfo.TargetPath, workspace);
                if (mergeResult == MergeResult.UnexpectedFileRestored)
                {
                    workspace.Undo(targetPendingChanges.Select(pendingChange => new ItemSpec(pendingChange)).ToArray(),
                        true);
                    mergeResult = MergeByFile(changeset.Changes, mergeInfo.TargetBranch, mergeRelationships,
                        mergeInfo.ChangesetVersionSpec, mergeOption, workspace);
                    targetPendingChanges = GetPendingChangesByFile(mergeRelationships, mergeInfo.TargetBranch, workspace);
                }

                if (targetPendingChanges.Count == 0)
                {
                    mergeResult = MergeResult.NothingMerge;
                }
                mergeResultModel.MergeResult = mergeResult;
                mergeResultModel.PendingChanges = targetPendingChanges;
                mergeResultModel.WorkItemIds = workItemIds;

                var trackMergeInfo = GetTrackMergeInfo(mergeInfo, changeset, versionControl);
                var comment = commentFormater.Format(trackMergeInfo, mergeInfo.TargetBranch, mergeOption);
                mergeResultModel.Comment = comment;

                result.Add(mergeResultModel);
                if (checkInIfSuccess && mergeResultModel.MergeResult == MergeResult.Merged)
                {
                    var checkInResult = CheckIn(mergeResultModel.PendingChanges, comment, workspace, workItemIds, changeset.PolicyOverride, workItemStore);
                    mergeResultModel.TagetChangesetId = checkInResult.ChangesetId;
                    mergeResultModel.MergeResult = checkInResult.CheckinResult;
                }
            }

            return result;
        }

        private List<MergeResultModel> MergeExecuteInternalForTask(bool checkInIfSuccess)
        {
            var result = new List<MergeResultModel>();
            var context = Context;
            var tfs = context.TeamProjectCollection;
            var versionControl = tfs.GetService<VersionControlServer>();
            var workspace = _workspace;
            var group = _taskChangesetGroup;
            var mergeOption = _mergeOption;
            var workItemStore = tfs.GetService<WorkItemStore>();
            var workItemIds = new List<int> { group.WorkItemId };

            var mergeInfos = _branches;
            var targetBranches = mergeInfos.Select(m => m.TargetBranch).ToArray();

            // union dei Change[] di TUTTI i changeset del gruppo: serve a GetMergeRelationships
            // per scoprire correttamente add/rename/delete su ogni target, coerente col fatto che
            // il merge d'intervallo copre più changeset in un colpo solo.
            // Riusa la cache già popolata in GetBranchesForTask invece di richiedere di nuovo il
            // Change[] al server per ogni changeset (fix #5: evita di raddoppiare le chiamate di rete,
            // ricadendo su GetChanges solo se il gruppo non è mai passato per il Refresh del pannello).
            var allChanges = group.ChangesetIds.SelectMany(id => GetChangesForTaskChangeset(group, id)).ToArray();
            var pendingChanges = GetChangesetPendingChanges(allChanges);
            var mergeRelationships = GetMergeRelationships(pendingChanges, targetBranches, versionControl);

            var commentFormater = new CommentFormater(Settings.Instance.CommentFormat);
            var lastChangesetId = group.ChangesetIds[group.ChangesetIds.Count - 1];
            var lastChangeset = _changesetService.GetChangeset(lastChangesetId);
            var workItem = workItemStore.GetWorkItem(group.WorkItemId);

            foreach (var mergeInfo in mergeInfos.Where(b => b.Checked))
            {
                var mergeResultModel = new MergeResultModel
                {
                    SourceChangesetId = lastChangesetId,   // rappresentativo: l'ID più alto del gruppo
                    BranchInfo = mergeInfo,
                };

                var mergeResult = MergeToBranch(mergeInfo, mergeOption, mergeRelationships, workspace);
                // Esito prima dell'eventuale "Nothing merged": serve al commento di check-in, che su uno
                // stop (conflitti o failures) non deve dichiarare la copertura completa del gruppo.
                var chainResult = mergeResult;
                // NIENTE fallback MergeByFile qui: modalità Task riduce lo scope in modo esplicito.
                var targetPendingChanges = GetPendingChanges(mergeInfo.TargetPath, workspace);

                // Solo un merge riuscito senza modifiche diventa "Nothing merged": uno stop per failures o
                // per target non pulito deve restare visibile con il suo motivo.
                if (mergeResult == MergeResult.Merged && targetPendingChanges.Count == 0)
                    mergeResult = MergeResult.NothingMerge;

                mergeResultModel.MergeResult = mergeResult;
                mergeResultModel.PendingChanges = targetPendingChanges;
                mergeResultModel.WorkItemIds = workItemIds;

                var trackMergeInfo = GetTrackMergeInfoForTask(mergeInfo, group, workItem, versionControl, chainResult);
                var comment = commentFormater.Format(trackMergeInfo, mergeInfo.TargetBranch, mergeOption);
                mergeResultModel.Comment = comment;

                result.Add(mergeResultModel);
                if (checkInIfSuccess && mergeResultModel.MergeResult == MergeResult.Merged)
                {
                    var checkInResult = CheckIn(mergeResultModel.PendingChanges, comment, workspace, workItemIds,
                        lastChangeset.PolicyOverride, workItemStore);
                    mergeResultModel.TagetChangesetId = checkInResult.ChangesetId;
                    mergeResultModel.MergeResult = checkInResult.CheckinResult;
                }
            }

            return result;
        }

        // Analogo a GetTrackMergeInfo(MergeInfoViewModel, Changeset, VersionControlServer) esistente,
        // ma sorgente = gruppo di changeset invece di un solo Changeset.
        private TrackMergeInfo GetTrackMergeInfoForTask(MergeInfoViewModel mergeInfo, TaskChangesetGroup group,
            WorkItem workItem, VersionControlServer versionControl, MergeResult chainResult)
        {
            // Niente GetMergesRelationships/QueryMergeRelationships qui: sulla stessa cartella larga
            // dell'intero gruppo era la chiamata misurata a 186 secondi che il flusso Task ha gia'
            // eliminato dalla ricerca dei branch (GetBranchesForTask) - andava eliminata anche qui, non
            // solo li', perche' altrimenti riappariva al momento del click su Merge invece che alla
            // selezione del gruppo. Stessi target scelti a mano dall'utente, coerente con
            // GetBranchesForTask.
            var mergesRelationships = group.TargetBranchPaths.Select(p => new ItemIdentifier(p)).ToList();
            var trackMerges = versionControl.TrackMerges(group.ChangesetIds.ToArray(),
                new ItemIdentifier(mergeInfo.SourcePath), mergesRelationships.ToArray(), null);

            var trackMergeInfo = GetTrackMergeInfo(versionControl, trackMerges, mergeInfo.SourcePath);
            trackMergeInfo.FromOriginalToSourceBranches.Reverse();

            trackMergeInfo.SourceBranch = mergeInfo.SourceBranch;
            trackMergeInfo.SourceChangesetId = group.ChangesetIds[group.ChangesetIds.Count - 1];
            trackMergeInfo.SourceComment = FormatTaskComment(group, workItem, chainResult,
                mergeInfo.StoppedAtBatchNumber, mergeInfo.Batches.Count);
            trackMergeInfo.SourceWorkItemIds = new List<long> { group.WorkItemId };
            trackMergeInfo.SourceWorkItemTitles = new List<string> { workItem.Title };
            trackMergeInfo.OriginaBranch = trackMergeInfo.OriginaBranch ?? trackMergeInfo.SourceBranch;
            trackMergeInfo.OriginalComment = trackMergeInfo.OriginalComment ?? trackMergeInfo.SourceComment;
            return trackMergeInfo;
        }

        // Se MergeToBranch si e' fermato prima di applicare tutti i batch (conflitti o failures), il
        // commento non deve dichiarare la copertura completa del gruppo: prefisso "[MERGE PARZIALE]"
        // con il punto di arresto, la causa reale e cosa fare, invece del solo testo grezzo
        // TF14010/TF203015 che il server riporta nella finestra Output.
        private static string FormatTaskComment(TaskChangesetGroup group, WorkItem workItem, MergeResult chainResult,
            int stoppedAtBatchNumber, int totalBatches)
        {
            var comment = string.Format("Merge from Task #{0} \"{1}\" (changesets {2})",
                group.WorkItemId, workItem.Title, string.Join(", ", group.ChangesetIds));

            string cause;
            switch (chainResult)
            {
                case MergeResult.HasConflicts:
                case MergeResult.UnexpectedFileRestored:
                    cause = "risolvi i conflitti";
                    break;
                case MergeResult.MergeFailed:
                    cause = "alcuni file non sono stati fusi (vedi Output, Source Control): correggi la causa";
                    break;
                default:
                    return comment;
            }

            return string.Format("[MERGE PARZIALE - fermato al blocco {0} di {1}: {2}, fai check-in, poi torna su MultiMerge e riclicca Merge per continuare] {3}",
                stoppedAtBatchNumber, totalBatches, cause, comment);
        }

        // Copy from Microsoft.TeamFoundation.VersionControl.Controls.PendingChanges.ChangesetDataProvider.ResetDataFromChangeset,
        // Microsoft.TeamFoundation.VersionControl.Controls
        private static List<PendingChange> GetChangesetPendingChanges(Change[] changes)
        {
            var pendingChanges = new List<PendingChange>(changes.Length);
            foreach (var change in changes)
            {
                if (ChangeType.SourceRename != (change.ChangeType & (ChangeType.Add | ChangeType.Branch | ChangeType.Rename | ChangeType.SourceRename)))
                {
                    var pendingChange = new PendingChange(change);
                    if (change.MergeSources != null)
                    {
                        foreach (var mergeSource in change.MergeSources)
                        {
                            if (mergeSource.IsRename)
                            {
                                pendingChange.UpdateSourceItems(null, mergeSource.ServerItem);
                                break;
                            }
                        }
                    }
                    pendingChanges.Add(pendingChange);
                }
            }

            return pendingChanges;
        }

        private static List<MergeRelation> GetMergeRelationships(List<PendingChange> pendingChanges, string[] targetBranches, VersionControlServer versionControl)
        {
            var mergeRelationships = new List<MergeRelation>();

            foreach (var pendingChange in pendingChanges)
            {
                if (pendingChange.IsAdd || pendingChange.IsBranch)
                {
                    var parentFolder = ExtractFolder(pendingChange.ChangeType, pendingChange.ServerItem, pendingChange.ItemType);
                    var parentFolderRelationships = versionControl.QueryMergeRelationships(parentFolder);
                    if (parentFolderRelationships != null)
                    {
                        foreach (var parentFolderRelationship in parentFolderRelationships.Where(r => !r.IsDeleted))
                        {
                            mergeRelationships.Add(new MergeRelation
                            {
                                Item = pendingChange.ServerItem,
                                Source = parentFolder,
                                Target = parentFolderRelationship.Item,
                                TargetItemType = ItemType.Folder,
                                GetLatesPath = parentFolderRelationship.Item,
                                Recursively = pendingChange.ItemType == ItemType.Folder
                            });
                        }
                    }
                }
                else if (pendingChange.IsRename)
                {
                    var shareFolder = FindShareFolder(pendingChange.ServerItem, pendingChange.SourceServerItem);
                    var shareFolderRelationships = versionControl.QueryMergeRelationships(shareFolder);
                    var sourceRelationships = versionControl.QueryMergeRelationships(pendingChange.SourceServerItem) ?? new ItemIdentifier[0];
                    if (shareFolderRelationships != null)
                    {
                        foreach (var shareFolderRelationship in shareFolderRelationships.Where(r => !r.IsDeleted))
                        {
                            var targetBranch =
                                targetBranches.FirstOrDefault(branch => shareFolderRelationship.Item.Contains(branch));
                            if (targetBranch != null)
                            {
                                var sourceRelationship = sourceRelationships
                                    .FirstOrDefault(r => r.Item.Contains(targetBranch));
                                mergeRelationships.Add(new MergeRelation
                                {
                                    Item = pendingChange.ServerItem,
                                    Source = shareFolder,
                                    Target = shareFolderRelationship.Item,
                                    TargetItemType = ItemType.Folder,
                                    GetLatesPath = sourceRelationship != null ? sourceRelationship.Item : null,
                                    Recursively = true
                                });
                            }
                        }
                    }
                }
                else
                {
                    var changeRelationShips = versionControl.QueryMergeRelationships(pendingChange.ServerItem);
                    if (changeRelationShips != null)
                    {
                        foreach (var changeRelationShip in changeRelationShips.Where(r => !r.IsDeleted))
                        {
                            mergeRelationships.Add(new MergeRelation
                            {
                                Item = pendingChange.ServerItem,
                                Source = pendingChange.ServerItem,
                                Target = changeRelationShip.Item,
                                TargetItemType = pendingChange.ItemType,
                                GetLatesPath = changeRelationShip.Item
                            });
                        }
                    }
                }
            }
            return mergeRelationships;
        }


        private TrackMergeInfo GetTrackMergeInfo(MergeInfoViewModel mergeInfo, Changeset changeset, VersionControlServer versionControl)
        {
            var mergesRelationships = GetMergesRelationships(mergeInfo.SourcePath, versionControl);
            var trackMerges = versionControl.TrackMerges(new[] {changeset.ChangesetId},
                new ItemIdentifier(mergeInfo.SourcePath),
                mergesRelationships.ToArray(),
                null);

            var trackMergeInfo = GetTrackMergeInfo(versionControl,
                trackMerges, mergeInfo.SourcePath);
            trackMergeInfo.FromOriginalToSourceBranches.Reverse();
            trackMergeInfo.SourceComment = changeset.Comment;
            trackMergeInfo.SourceBranch = mergeInfo.SourceBranch;
            trackMergeInfo.SourceChangesetId = changeset.ChangesetId;
            trackMergeInfo.SourceWorkItemIds = changeset.AssociatedWorkItems != null
                ? changeset.AssociatedWorkItems.Select(w => (long) w.Id).ToList()
                : new List<long>(0);
            trackMergeInfo.SourceWorkItemTitles = changeset.AssociatedWorkItems != null
                ? changeset.AssociatedWorkItems.Select(w => w.Title).ToList()
                : new List<string>(0);
            trackMergeInfo.OriginaBranch = trackMergeInfo.OriginaBranch ?? trackMergeInfo.SourceBranch;
            trackMergeInfo.OriginalComment = trackMergeInfo.OriginalComment ?? trackMergeInfo.SourceComment;
            return trackMergeInfo;
        }

        private static CheckInResult CheckIn(IReadOnlyCollection<PendingChange> targetPendingChanges, string comment,
            Workspace workspace, IReadOnlyCollection<int> workItemIds, PolicyOverrideInfo policyOverride, WorkItemStore workItemStore)
        {
            var result = new CheckInResult();

            // Another user can update workitem. Need re-read before update.
            var workItems = GetWorkItemCheckinInfo(workItemIds, workItemStore);

            var evaluateCheckIn = workspace.EvaluateCheckin2(CheckinEvaluationOptions.All,
                targetPendingChanges,
                comment,
                null,
                workItems);

            var skipPolicyValidate = !policyOverride.PolicyFailures.IsNullOrEmpty();
            if (!CanCheckIn(evaluateCheckIn, skipPolicyValidate))
            {
                result.CheckinResult = MergeResult.CheckInEvaluateFail;
                return result;
            }

            var changesetId = workspace.CheckIn(targetPendingChanges.ToArray(), null, comment,
                null, workItems, policyOverride);
            if (changesetId > 0)
            {
                result.ChangesetId = changesetId;
                result.CheckinResult = MergeResult.CheckIn;
            }
            else
            {
                result.CheckinResult = MergeResult.CheckInFail;
            }
            return result;
        }

        private MergeResult MergeByFile(Change[] changes, string targetBranch, List<MergeRelation> mergeRelationships,
            VersionSpec version, MergeOption mergeOption, Workspace workspace)
        {
            if (!GetLatest(targetBranch, mergeRelationships, workspace))
            {
                return MergeResult.CanNotGetLatest;
            }

            var mergeOptions = ToTfsMergeOptions(mergeOption);
            var hasConflicts = false;
            foreach (var change in changes)
            {
                var mergeRelation =
                    mergeRelationships.FirstOrDefault(
                        r => r.Item == change.Item.ServerItem && r.Target.StartsWith(targetBranch));
                if (mergeRelation != null)
                {
                    var recursionType = CalculateRecursionType(mergeRelation);
                    var status = workspace.Merge(mergeRelation.Source, mergeRelation.Target, version, version,
                        LockLevel.None, recursionType, mergeOptions);
                    if (!hasConflicts && HasConflicts(status))
                    {
                        hasConflicts = true;
                    }
                }
                else
                {
                    Logger.Info("File {0} not merged to branch {1}", change.Item.ServerItem, targetBranch);
                }
            }

            if (hasConflicts)
            {
                var conflicts = AutoResolveConflicts(workspace, targetBranch, mergeOption);
                if (!conflicts.IsNullOrEmpty())
                {
                    return IsTryRestoreUnexpectedFile(conflicts)
                        ? MergeResult.UnexpectedFileRestored
                        : MergeResult.HasConflicts;
                }
            }

            return MergeResult.Merged;
        }

        private static MergeResult MergeToBranch(MergeInfoViewModel mergeInfoeViewModel, MergeOption mergeOption,
            List<MergeRelation> mergeRelationships, Workspace workspace)
        {
            var source = mergeInfoeViewModel.SourcePath;
            var target = mergeInfoeViewModel.TargetPath;

            // Flusso Task (Batches != null). Il flusso a changeset singolo (Batches == null) resta
            // identico a prima.
            var isTaskFlow = mergeInfoeViewModel.Batches != null;
            if (isTaskFlow)
            {
                mergeInfoeViewModel.StoppedAtBatchNumber = 0;

                // Target pulito anche al momento del click, non solo all'ultimo refresh del pannello:
                // modifiche fatte nel frattempo verrebbero assorbite dal merge ("merge, edit") e finirebbero
                // nel check-in con il commento del task.
                if (workspace.GetPendingChangesEnumerable(target, RecursionType.Full).Any())
                    return MergeResult.HasLocalChanges;
            }

            if (!GetLatest(target, mergeRelationships, workspace))
                return MergeResult.CanNotGetLatest;

            var mergeOptions = ToTfsMergeOptions(mergeOption);

            var batches = mergeInfoeViewModel.Batches;
            if (batches == null)
            {
                // modalità legacy: un solo "batch" implicito, stesso changeset da e a
                var version = mergeInfoeViewModel.ChangesetVersionSpec;
                batches = new List<ChangesetBatch> { new ChangesetBatch(version.ChangesetId, version.ChangesetId) };
            }

            // La catena riparte SEMPRE dal primo batch, su un target pulito: TFVC salta da solo, file per
            // file, cio' che e' gia' fuso e registrato nella storia (per esempio i batch portati e messi
            // in check-in prima di uno stop per conflitti). Rifondere un batch gia' fuso non costa nulla;
            // saltarlo sulla base di una contabilita' tenuta dall'estensione potrebbe perdere modifiche.
            //
            // Si ferma al primo batch con conflitti non auto-risolvibili o con failures, senza provare i
            // successivi: continuare a toccare file mentre uno precedente e' in conflitto genera solo
            // rumore ("TF14010: ... a merge conflict already exists", "TF203015: incompatible pending
            // change"), e un batch con failures (item non messi in sospeso) non e' completo.
            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var versionFrom = new ChangesetVersionSpec(batch.FromChangesetId);
                var versionTo = new ChangesetVersionSpec(batch.ToChangesetId);

                var status = workspace.Merge(source, target, versionFrom, versionTo, LockLevel.None, RecursionType.Full, mergeOptions);

                if (isTaskFlow && status.NumFailures > 0)
                {
                    mergeInfoeViewModel.StoppedAtBatchNumber = i + 1;
                    return MergeResult.MergeFailed;
                }

                if (HasConflicts(status))
                {
                    var conflicts = AutoResolveConflicts(workspace, target, mergeOption);
                    if (!conflicts.IsNullOrEmpty())
                    {
                        if (isTaskFlow)
                            mergeInfoeViewModel.StoppedAtBatchNumber = i + 1;
                        return IsTryRestoreUnexpectedFile(conflicts)
                            ? MergeResult.UnexpectedFileRestored
                            : MergeResult.HasConflicts;
                    }
                }
            }

            return MergeResult.Merged;
        }

        private static bool IsTryRestoreUnexpectedFile(Conflict[] conflicts)
        {
            foreach (var conflict in conflicts)
            {
                if (conflict.BaseChangeType.HasFlag(ChangeType.Undelete)
                    && !conflict.TheirChangeType.HasFlag(ChangeType.Undelete))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<PendingChange> GetPendingChanges(string target, Workspace workspace)
        {
            var allPendingChanges = workspace.GetPendingChangesEnumerable(target, RecursionType.Full);
            // OrdinalIgnoreCase: target puo' venire da un branch scritto a mano dall'utente (flusso Task),
            // con un casing diverso da quello canonico registrato sul server (es. "rel1.0_New" digitato
            // contro "rel1.0_new" reale) - un Contains() sensibile al maiuscolo/minuscolo qui farebbe
            // risultare "Nothing merged" anche quando il merge ha prodotto davvero modifiche in sospeso.
            var targetPendingChanges = allPendingChanges
                .Where(p => p.IsMerge && p.ServerItem.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            return targetPendingChanges;
        }

        private static List<PendingChange> GetPendingChangesByFile(List<MergeRelation> mergeRelationships, string targetBranch, Workspace workspace)
        {
            var itemSpecs = new List<ItemSpec>();
            foreach (var mergeRelationship in mergeRelationships)
            {
                if (mergeRelationship.Target.StartsWith(targetBranch))
                {
                    var recursionType = CalculateRecursionType(mergeRelationship);
                    itemSpecs.Add(new ItemSpec(mergeRelationship.Target, recursionType));
                }
            }
            return workspace.GetPendingChanges(itemSpecs.ToArray()).ToList();
        }

        private static RecursionType CalculateRecursionType(MergeRelation mergeRelationship)
        {
            var recursionType = mergeRelationship.Recursively
                ? RecursionType.Full
                : mergeRelationship.TargetItemType == ItemType.File
                    ? RecursionType.None
                    : RecursionType.OneLevel;
            return recursionType;
        }

        private static bool GetLatest(string targetPath, List<MergeRelation> mergeRelationships, Workspace workspace)
        {
            var getLatestFiles = new List<string>();
            foreach (var mergeRelationship in mergeRelationships.Where(r => r.TargetItemType == ItemType.File && r.GetLatesPath != null))
            {
                if (mergeRelationship.GetLatesPath.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase))
                    getLatestFiles.Add(mergeRelationship.GetLatesPath);
            }

            var getLatestFilesArray = getLatestFiles.ToArray();
            if (getLatestFilesArray.Length > 0)
            {
                const RecursionType recursionType = RecursionType.None;
                var getLatestResult = workspace.Get(getLatestFilesArray, VersionSpec.Latest, recursionType, GetOptions.None);
                if (!getLatestResult.NoActionNeeded)
                {
                    // HACK.
                    getLatestResult = workspace.Get(getLatestFilesArray, VersionSpec.Latest, recursionType, GetOptions.None);
                    if (!getLatestResult.NoActionNeeded)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static MergeOptionsEx ToTfsMergeOptions(MergeOption mergeOption)
        {
            switch (mergeOption)
            {
                case MergeOption.KeepTarget:
                    return MergeOptionsEx.AlwaysAcceptMine;
                default:
                    return MergeOptionsEx.None;
            }
        }

        public bool MergeCanEcexute()
        {
            return !_merging && _branches != null && _branches.Any(b => b.Checked);
        }

        private static bool HasConflicts(GetStatus mergeStatus)
        {
            return !mergeStatus.NoActionNeeded && mergeStatus.NumConflicts > 0;
        }

        internal static Conflict[] AutoResolveConflicts(Workspace workspace, string targetPath, MergeOption mergeOption)
        {
            var targetPaths = new[] {targetPath};
            var conflicts = workspace.QueryConflicts(targetPaths, true);
            if (conflicts.IsNullOrEmpty())
                return null;

            foreach (var conflict in conflicts)
            {
                TryResolve(workspace, conflict, mergeOption);
            }

            conflicts = workspace.QueryConflicts(targetPaths, true);
            if (conflicts.IsNullOrEmpty())
                return null;

            workspace.AutoResolveValidConflicts(conflicts, AutoResolveOptions.AllSilent);

            return workspace.QueryConflicts(targetPaths, true);
        }

        private static void TryResolve(Workspace workspace, Conflict conflict, MergeOption mergeOption)
        {
            if (mergeOption == MergeOption.KeepTarget)
            {
                conflict.Resolution = Resolution.AcceptYours;
                workspace.ResolveConflict(conflict);
            }
            if (mergeOption == MergeOption.OverwriteTarget)
            {
                conflict.Resolution = Resolution.AcceptTheirs;
                workspace.ResolveConflict(conflict);
            }
        }


        private static WorkItemCheckinInfo[] GetWorkItemCheckinInfo(IReadOnlyCollection<int> workItemIds, WorkItemStore workItemStore)
        {

            var result = new List<WorkItemCheckinInfo>(workItemIds.Count);
            foreach (var workItemId in workItemIds)
            {
                var workItem = workItemStore.GetWorkItem(workItemId);
                var workItemCheckinInfo = new WorkItemCheckinInfo(workItem, WorkItemCheckinAction.Associate);
                result.Add(workItemCheckinInfo);
            }

            return result.ToArray();
        }

        private static bool CanCheckIn(CheckinEvaluationResult checkinEvaluationResult, bool skipPolicy)
        {
            var result = checkinEvaluationResult.Conflicts.IsNullOrEmpty()
                && checkinEvaluationResult.NoteFailures.IsNullOrEmpty()
                && checkinEvaluationResult.PolicyEvaluationException == null;

            if (!skipPolicy)
                result &= checkinEvaluationResult.PolicyFailures.IsNullOrEmpty();
            return result;
        }

        internal static void InvokeResolveConflictsPage(Workspace workspace, string[] targetPath)
        {
            var versionControlAssembly = Assembly.Load("Microsoft.VisualStudio.TeamFoundation.VersionControl");
            if (versionControlAssembly == null)
                return;

            var rcMgr = versionControlAssembly.GetType("Microsoft.VisualStudio.TeamFoundation.VersionControl.ResolveConflictsManager");
            if (rcMgr == null)
                return;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var mi = rcMgr.GetMethod("Initialize", flags);
            var instantiatedType = Activator.CreateInstance(rcMgr, flags, null, null, null);
            mi.Invoke(instantiatedType, null);

            var resolveConflictsMethod = rcMgr.GetMethod("ResolveConflicts", BindingFlags.NonPublic | BindingFlags.Instance);
            resolveConflictsMethod.Invoke(instantiatedType,
                new object[] { workspace, targetPath, true, false });
        }

        private void SelectWorkspaceExecute(Workspace workspace)
        {
            Workspace = workspace;
            // Flusso Task: la scelta esplicita dell'utente diventa la workspace della catena.
            if (_taskChangesetGroup != null && workspace != null)
                TaskMergeSession.WorkspaceName = workspace.QualifiedName;
            Refresh();
        }

        private void SubscribeWorkspaceChanges(VersionControlServer versionControlServer)
        {
            versionControlServer.CreatedWorkspace += RefreshWorkspaces;
            versionControlServer.UpdatedWorkspace += RefreshWorkspaces;
            versionControlServer.DeletedWorkspace += RefreshWorkspaces;
        }

        private void RefreshWorkspaces(object sender, WorkspaceEventArgs e)
        {
            var tfs = Context.TeamProjectCollection;
            var versionControl = tfs.GetService<VersionControlServer>();

            Workspaces = GetWorkspaces(versionControl, tfs);
            if (Workspaces.Count > 0)
            {
                Workspace = Workspaces[0];
                ShowWorkspaceChooser = Workspaces.Count > 1;
                // Flusso Task: non restare sulla prima workspace in elenco se non e' quella giusta.
                if (_taskChangesetGroup != null)
                    EnsureWorkspaceMapsTargets(_taskChangesetGroup);
            }
            else
            {
                Workspace = null;
            }
            Refresh();
        }

        private void OpenSourceControlExplorerExecute()
        {
            // Using HACK.
            // Get any service which contain DTE object.

            var s = ServiceProvider.GetService<SVsSourceControl>() as SourceControl2;
            if (s != null)
            {
                dynamic ext = s.DTE.GetObject("Microsoft.VisualStudio.TeamFoundation.VersionControl.VersionControlExt");
                if (ext != null)
                {
                    var explorer = ext.Explorer;
                    if (explorer != null)
                    {
                        explorer.Navigate(SelectedBranch.TargetPath);
                    }
                }
            }
        }

        private bool OpenSourceControlExplorerCanExecute()
        {
            return SelectedBranch != null && !string.IsNullOrEmpty(SelectedBranch.TargetPath);
        }

        private void ViewChangesetDetailsExecute(int changesetId)
        {
            TeamExplorerUtils.Instance.NavigateToPage(TeamExplorerPageIds.ChangesetDetails, ServiceProvider, changesetId);
        }

        public override void Dispose()
        {
            base.Dispose();
            if (_eventAggregator != null)
            {
                _eventAggregator.GetEvent<SelectChangesetEvent>().Unsubscribe(OnSelectedChangeset);
                _eventAggregator.GetEvent<SelectTaskChangesetGroupEvent>().Unsubscribe(OnSelectedTaskChangesetGroup);
                _eventAggregator.GetEvent<BranchSelectedChangedEvent>().Unsubscribe(OnBranchSelectedChanged);
            }

            var tfs = Context.TeamProjectCollection;
            if (tfs != null)
            {
                var versionControl = tfs.GetService<VersionControlServer>();
                if (versionControl != null)
                {
                    versionControl.CreatedWorkspace -= RefreshWorkspaces;
                    versionControl.UpdatedWorkspace -= RefreshWorkspaces;
                    versionControl.DeletedWorkspace -= RefreshWorkspaces;
                }
            }
        }

        public override void SaveContext(object sender, SectionSaveContextEventArgs e)
        {
            var context = new BranchesViewModelContext
            {
                Branches = Branches,
                Changeset = _changeset,
                ErrorMessage = ErrorMessage,
                MergeMode = MergeMode,
                MergeModes = MergeModes,
                MergeOption = MergeOption,
                SelectedBranch = SelectedBranch,
                ShowWorkspaceChooser = ShowWorkspaceChooser,
                Workspace = Workspace,
                Workspaces = Workspaces
            };

            e.Context = context;
        }

        private void RestoreContext(SectionInitializeEventArgs e)
        {
            var context = (BranchesViewModelContext)e.Context;
            _changeset = context.Changeset;
            Branches = context.Branches;
            ErrorMessage = context.ErrorMessage;
            MergeMode = context.MergeMode;
            MergeModes = context.MergeModes;
            MergeOption = context.MergeOption;
            SelectedBranch = context.SelectedBranch;
            ShowWorkspaceChooser = context.ShowWorkspaceChooser;
            Workspace = context.Workspace;
            Workspaces = context.Workspaces;
        }
    }
}
