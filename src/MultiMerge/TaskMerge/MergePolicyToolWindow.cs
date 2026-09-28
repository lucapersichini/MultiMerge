// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Runtime.InteropServices;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace MultiMerge
{
    // Scheda "Merge Policies": tool window a istanza singola nel document well (vedi ProvideToolWindow su
    // MultiMergePackage), aperta dal pulsante "Policies..." della scheda Merge from Task. Come per
    // TaskMergeToolWindow il view model e' in un campo statico: le modifiche al file di team non ancora
    // salvate restano in memoria anche se la finestra venisse ricreata.
    [Guid(GuidList.MergePolicyToolWindowString)]
    public sealed class MergePolicyToolWindow : ToolWindowPane
    {
        public const string WindowCaption = "Merge Policies";

        private static MergePolicyViewModel _sharedViewModel;

        public MergePolicyToolWindow()
            : this(null)
        {
        }

        // state: il package (vedi MultiMergePackage.InitializeToolWindowAsync).
        public MergePolicyToolWindow(object state)
            : base(null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Caption = WindowCaption;
            BitmapImageMoniker = KnownMonikers.Policy;

            var serviceProvider = state as IServiceProvider ?? ServiceProvider.GlobalProvider;
            if (_sharedViewModel == null)
                _sharedViewModel = new MergePolicyViewModel(serviceProvider, new VsLogger(serviceProvider));

            Content = new MergePolicyView { DataContext = _sharedViewModel };
        }

        // View model condiviso della scheda (null finche' la scheda non e' mai stata creata).
        internal static MergePolicyViewModel SharedViewModel
        {
            get { return _sharedViewModel; }
        }

        // Apre (o porta in primo piano) la scheda sul ramo di destinazione e sul workspace dati (quelli di
        // Merge from Task) e ricarica le regole. Nessuna eccezione esce: gli errori finiscono in una
        // message box.
        public static async Task ShowAsync(string targetBranchRoot, Workspace workspace)
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

                var window = await package.ShowToolWindowAsync(typeof(MergePolicyToolWindow), 0, true, package.DisposalToken);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (window == null || window.Frame == null || _sharedViewModel == null)
                    throw new NotSupportedException("Cannot create the Merge Policies window.");

                _sharedViewModel.Open(targetBranchRoot, workspace);
            }
            catch (Exception ex)
            {
                ActivityLog.LogError("MultiMerge", "Merge Policies window: " + ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider,
                    "Cannot open the Merge Policies tab: " + ex.Message,
                    "MultiMerge",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }
    }
}
