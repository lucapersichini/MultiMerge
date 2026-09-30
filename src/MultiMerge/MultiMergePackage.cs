// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using MultiMerge.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace MultiMerge
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    ///
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    // This attribute is used to register the information needed to show this package
    // in the Help/About dialog of Visual Studio.
    [InstalledProductRegistration("#110", "#112", "1.2", IconResourceID = 400)]
    [Guid(GuidList.guidMultiMergePkgString)]
    // Versione 2: la command table ha un comando nuovo ("Merge from Task..."), cosi' VS ricarica i menu.
    [ProvideMenuResource("Menus.ctmenu", 2)]
    [ProvideBindingPath]
    // Scheda "Merge from Task": tool window a istanza singola, aperta tra i documenti.
    [ProvideToolWindow(typeof(TaskMergeToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell", MultiInstances = false)]
    // Scheda "Merge Policies" (regole di merge di team e personali): stessa forma, aperta da Merge from Task.
    [ProvideToolWindow(typeof(MergePolicyToolWindow), Style = VsDockStyle.Tabbed, Window = "DocumentWell", MultiInstances = false)]
    public sealed class MultiMergePackage : AsyncPackage
    {
        protected override async System.Threading.Tasks.Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await ShowMultiMergeWindow.InitializeAsync(this);
            await ShowTaskMergeWindow.InitializeAsync(this);
        }

        // Creazione asincrona delle tool window: il package stesso e' lo "state" passato al
        // costruttore di TaskMergeToolWindow e di MergePolicyToolWindow (serve come service provider
        // dei view model).
        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType)
        {
            return toolWindowType.Equals(GuidList.TaskMergeToolWindowGuid)
                || toolWindowType.Equals(GuidList.MergePolicyToolWindowGuid)
                ? this
                : null;
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id)
        {
            if (toolWindowType == typeof(TaskMergeToolWindow))
                return TaskMergeToolWindow.WindowCaption;
            if (toolWindowType == typeof(MergePolicyToolWindow))
                return MergePolicyToolWindow.WindowCaption;
            return base.GetToolWindowTitle(toolWindowType, id);
        }

        protected override System.Threading.Tasks.Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
        {
            return System.Threading.Tasks.Task.FromResult<object>(this);
        }
    }
}
