// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.ComponentModel.Design;
using Microsoft.TeamFoundation.Common.Internal;
using Microsoft.TeamFoundation.Controls;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace MultiMerge.Commands
{
    internal sealed class ShowMultiMergeWindow
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;

            // must match the button GUID and ID specified in the .vsct file
            var cmdId = new CommandID(GuidList.ShowMultiMergeCmdSet, 0x0100);
            var cmd = new MenuCommand((s, e) => Execute(package), cmdId);
            commandService.AddCommand(cmd);
        }

        private static void Execute(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var teamExplorer = package.GetService<ITeamExplorer>();
            teamExplorer.NavigateToPage(GuidList.MultiMergePageGuid, null);
        }
    }
}
