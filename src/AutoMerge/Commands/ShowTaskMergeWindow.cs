using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AutoMerge.Commands
{
    // Comando "Merge from Task..." (toolbar Auto Merge e menu Tools): apre la scheda dedicata
    // TaskMergeToolWindow. Stesso modello di ShowAutoMergeWindow.
    internal sealed class ShowTaskMergeWindow
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
                return;

            // must match the button GUID and ID specified in the .vsct file
            var cmdId = new CommandID(GuidList.ShowAutoMergeCmdSet, GuidList.ShowTaskMergeCommandId);
            var cmd = new MenuCommand((s, e) => Execute(package), cmdId);
            commandService.AddCommand(cmd);
        }

        private static void Execute(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Nessun .Result sul thread UI: la finestra viene creata/mostrata in modo asincrono.
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var window = await package.ShowToolWindowAsync(typeof(TaskMergeToolWindow), 0, true, package.DisposalToken);
                    if (window == null || window.Frame == null)
                        throw new NotSupportedException("Cannot create the Merge from Task window.");
                }
                catch (Exception ex)
                {
                    ActivityLog.LogError("AutoMerge", "Merge from Task window: " + ex);
                    // Non solo nell'ActivityLog: l'utente ha cliccato e deve sapere perche' non si apre nulla.
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    VsShellUtilities.ShowMessageBox(package,
                        "Cannot open the Merge from Task window: " + ex.Message,
                        "AutoMerge",
                        OLEMSGICON.OLEMSGICON_CRITICAL,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            });
        }
    }
}
