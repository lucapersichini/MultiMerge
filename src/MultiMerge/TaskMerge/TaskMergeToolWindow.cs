// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace MultiMerge
{
    // Scheda "Merge from Task": tool window a istanza singola nel document well (vedi
    // ProvideToolWindow su MultiMergePackage). Chiudere la scheda la nasconde soltanto; in piu' il view
    // model e' tenuto in un campo statico, cosi' lo stato della catena (piano, batch fusi, conflitti,
    // registro) resta in memoria per tutta la sessione di VS anche se la finestra venisse ricreata.
    [Guid(GuidList.TaskMergeToolWindowString)]
    public sealed class TaskMergeToolWindow : ToolWindowPane
    {
        public const string WindowCaption = "Merge from Task";

        private static TaskMergeViewModel _sharedViewModel;
        private static GitMergeViewModel _sharedGitViewModel;

        private readonly IServiceProvider _services;
        private readonly TaskMergeView _tfvcView;
        private readonly MergeContextView _contextView;
        private readonly System.Windows.Threading.DispatcherTimer _contextTimer;
        private string _contextKey;
        private static string _tfvcKey;
        private bool _detecting, _disposed;
        private int _generation;
        private void OnTransferStateChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsBusy" || e.PropertyName == "IsChainInProgress" || e.PropertyName == "IsActive")
                _contextView.SetBusy(IsTransferActive || _detecting);
        }
        private void PrepareTfvcContext(string key)
        {
            if (IsTransferActive) return;
            if (_tfvcKey != null && _tfvcKey != key && !IsTransferActive)
            {
                _sharedViewModel.PropertyChanged -= OnTransferStateChanged;
                _sharedViewModel = new TaskMergeViewModel(_services, new VsLogger(_services));
                _sharedViewModel.PropertyChanged += OnTransferStateChanged;
                _tfvcView.DataContext = _sharedViewModel;
            }
            _tfvcKey = key;
        }
        private bool IsTransferActive { get { return !_sharedGitViewModel.CanLeave || _sharedViewModel.IsBusy || _sharedViewModel.IsChainInProgress; } }

        public void RefreshContext() { _contextKey = null; _ = DetectContextAsync(); }

        private async Task DetectContextAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _contextView.SetBusy(IsTransferActive || _detecting);
            if (_disposed || _detecting || IsTransferActive) return;
            try
            {
                var snapshot = VisualStudioMergeContext.Capture(_services);
                if (snapshot.Key == _contextKey) return;
                _detecting = true; _contextView.SetBusy(true);
                int generation = ++_generation;
                var choice = await Task.Run(() => VisualStudioMergeContext.Detect(snapshot));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_disposed || generation != _generation || IsTransferActive) return;
                // A solution/provider switch during asynchronous detection invalidates its result.
                if (VisualStudioMergeContext.Capture(_services).Key != snapshot.Key) return;
                if (choice.Kind == MergeContextKind.Tfvc) PrepareTfvcContext(snapshot.Key);
                _contextKey = snapshot.Key;
                _contextView.ShowContext(choice, false);
                if (choice.Kind == MergeContextKind.Git && !string.Equals(_sharedGitViewModel.RepositoryPath, choice.Location, StringComparison.OrdinalIgnoreCase))
                {
                    _sharedGitViewModel.RepositoryPath = choice.Location;
                    await _sharedGitViewModel.LoadRepositoryCommand.Execute();
                }
            }
            catch (Exception ex)
            {
                if (!_disposed && !IsTransferActive) _contextView.ShowContext(MergeContextResolver.Unknown("Context detection failed: " + ex.Message + ". Choose the context."), false);
                ActivityLog.LogError("MultiMerge", "Repository context: " + ex);
            }
            finally { _detecting = false; if (!_disposed) _contextView.SetBusy(IsTransferActive); }
        }
        private void ChooseGit()
        {
            if (IsTransferActive || _detecting) return;
            _contextView.ShowContext(new MergeContextChoice(MergeContextKind.Git, "", "Git · choose a repository folder below"), false);
        }
        private void ChooseTfvc()
        {
            if (IsTransferActive || _detecting) return;
            if (!VersionControlNavigationHelper.IsConnectedToTfsCollectionAndProject(_services))
            {
                _contextView.ShowContext(MergeContextResolver.Unknown("Connect Team Explorer to a TFVC project, then Detect context."), false);
                return;
            }
            PrepareTfvcContext(VisualStudioMergeContext.Capture(_services).Key);
            _contextView.ShowContext(new MergeContextChoice(MergeContextKind.Tfvc, "", "TFVC · explicitly selected connected project"), false);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _disposed = true; ++_generation; _contextTimer.Stop(); _sharedGitViewModel.PropertyChanged -= OnTransferStateChanged; _sharedViewModel.PropertyChanged -= OnTransferStateChanged; }
            base.Dispose(disposing);
        }


        public TaskMergeToolWindow()
            : this(null)
        {
        }

        // state: il package (vedi MultiMergePackage.InitializeToolWindowAsync).
        public TaskMergeToolWindow(object state)
            : base(null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Caption = WindowCaption;
            BitmapImageMoniker = KnownMonikers.Merge;

            var serviceProvider = state as IServiceProvider ?? ServiceProvider.GlobalProvider;
            if (_sharedViewModel == null)
                _sharedViewModel = new TaskMergeViewModel(serviceProvider, new VsLogger(serviceProvider));

            if (_sharedGitViewModel == null) _sharedGitViewModel = new GitMergeViewModel(serviceProvider);
            _services = serviceProvider;
            _tfvcView = new TaskMergeView { DataContext = _sharedViewModel };
            _contextView = new MergeContextView(new GitMergeView { DataContext = _sharedGitViewModel }, _tfvcView);
            _sharedGitViewModel.PropertyChanged += OnTransferStateChanged;
            _sharedViewModel.PropertyChanged += OnTransferStateChanged;
            _contextView.GitRequested += ChooseGit;
            _contextView.TfvcRequested += ChooseTfvc;
            _contextView.DetectionRequested += RefreshContext;
            _contextTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _contextTimer.Tick += (s,e) => { _ = DetectContextAsync(); };
            _contextView.Loaded += (s,e) => { _contextTimer.Start(); RefreshContext(); };
            _contextView.Unloaded += (s,e) => _contextTimer.Stop();
            Content = _contextView;
        }

        // View model condiviso della scheda (null finche' la scheda non e' mai stata creata).
        internal static TaskMergeViewModel SharedViewModel
        {
            get { return _sharedViewModel; }
        }

        // Apre (o porta in primo piano) la scheda e la precompila con work item e target scritti nella
        // sezione "Merge From Task" di Team Explorer; se non c'e' una catena in corso, carica subito.
        // La sezione di Team Explorer puo' essere usata prima che il package sia caricato (Team Explorer
        // carica le sezioni via MEF), quindi il package si carica qui se serve. Nessuna eccezione esce:
        // gli errori finiscono in una message box.
        public static async Task ShowAndOpenAsync(string workItemIdText, string targetBranchText)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Nome qualificato: dentro ToolWindowPane "Package" e' la proprieta' d'istanza.
                var shell = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsShell)) as IVsShell;
                if (shell == null)
                    throw new InvalidOperationException("The Visual Studio shell service is not available.");

                var packageGuid = new Guid(GuidList.guidMultiMergePkgString);
                IVsPackage vsPackage;
                if (ErrorHandler.Failed(shell.IsPackageLoaded(ref packageGuid, out vsPackage)) || vsPackage == null)
                    ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref packageGuid, out vsPackage));

                var package = vsPackage as MultiMergePackage;
                if (package == null)
                    throw new InvalidOperationException("The MultiMerge package could not be loaded.");

                var window = await package.ShowToolWindowAsync(typeof(TaskMergeToolWindow), 0, true, package.DisposalToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (window == null || window.Frame == null || _sharedViewModel == null)
                    throw new NotSupportedException("Cannot create the Merge from Task window.");

                if (_sharedGitViewModel != null && !_sharedGitViewModel.CanLeave)
                    throw new InvalidOperationException("Finish or abort the Git transfer before opening a TFVC task.");
                var taskWindow = (TaskMergeToolWindow)window;
                ++taskWindow._generation;
                if (taskWindow._contextView.Current.Kind != MergeContextKind.Tfvc && taskWindow.IsTransferActive)
                    throw new InvalidOperationException("Finish the current transfer before changing context.");
                // Opening from Team Explorer is an explicit TFVC action, not a heuristic.
                var requestedContextKey = VisualStudioMergeContext.Capture(taskWindow._services).Key;
                if (taskWindow.IsTransferActive && requestedContextKey != _tfvcKey)
                    throw new InvalidOperationException("Finish the TFVC transfer before changing the connected project or solution.");
                taskWindow._contextKey = requestedContextKey;
                taskWindow.PrepareTfvcContext(taskWindow._contextKey);
                taskWindow._contextView.ShowContext(new MergeContextChoice(MergeContextKind.Tfvc, "", "TFVC · opened from Team Explorer"), taskWindow.IsTransferActive);
                _sharedViewModel.OpenFromTeamExplorer(workItemIdText, targetBranchText);
            }
            catch (Exception ex)
            {
                ActivityLog.LogError("MultiMerge", "Merge from Task window (from Team Explorer): " + ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider,
                    "Cannot open the Merge from Task tab: " + ex.Message,
                    "MultiMerge",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }
    }
}
