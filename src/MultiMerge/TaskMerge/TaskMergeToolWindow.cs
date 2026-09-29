// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Data;
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
        private TabControl _tabs;

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

            if (_sharedGitViewModel == null) _sharedGitViewModel = new GitMergeViewModel();
            _tabs = new TabControl();
            var tfvc = new TabItem { Header = "TFVC", Content = new TaskMergeView { DataContext = _sharedViewModel } };
            tfvc.SetBinding(TabItem.IsEnabledProperty, new Binding("CanLeave") { Source = _sharedGitViewModel });
            var git = new TabItem { Header = "Git", Content = new GitMergeView { DataContext = _sharedGitViewModel } };
            var gitStyle = new System.Windows.Style(typeof(TabItem));
            foreach (var property in new[] { "IsBusy", "IsChainInProgress" })
            {
                var trigger = new System.Windows.DataTrigger { Binding = new Binding(property) { Source = _sharedViewModel }, Value = true };
                trigger.Setters.Add(new System.Windows.Setter(TabItem.IsEnabledProperty, false));
                gitStyle.Triggers.Add(trigger);
            }
            git.Style = gitStyle;
            _tabs.Items.Add(tfvc); _tabs.Items.Add(git);
            Content = _tabs;
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
                ((TaskMergeToolWindow)window)._tabs.SelectedIndex = 0;
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
