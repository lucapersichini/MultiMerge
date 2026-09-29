// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.TeamFoundation.VersionControl.Client;
using MultiMerge.VersionControl;

namespace MultiMerge
{
    internal sealed class MergeContextSnapshot
    {
        public VersionControlProvider? Provider;
        public string Collection, Project;
        public List<string> Folders = new List<string>();
        public bool Incomplete;
        public string Key { get { return Provider + "|" + Collection + "|" + Project + "|" + Incomplete + "|" + string.Join("|", Folders); } }
    }
    internal static class VisualStudioMergeContext
    {
        // Shell/COM access stays on the UI thread; Git and cached TFVC mappings are inspected off-thread.
        internal static MergeContextSnapshot Capture(IServiceProvider services)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = new MergeContextSnapshot { Provider = VersionControlNavigationHelper.GetActiveProvider(services) };
            var context = VersionControlNavigationHelper.GetTeamFoundationContext(services);
            if (context != null && context.HasCollection && context.HasTeamProject)
            { snapshot.Collection = context.TeamProjectCollection.Uri.AbsoluteUri; snapshot.Project = context.TeamProjectName; }
            var solution = services.GetService(typeof(SVsSolution)) as IVsSolution;
            if (solution == null) return snapshot;
            string folder, file, options;
            if (ErrorHandler.Succeeded(solution.GetSolutionInfo(out folder, out file, out options)) && !string.IsNullOrWhiteSpace(folder))
                snapshot.Folders.Add(Path.GetFullPath(folder));
            IEnumHierarchies projects;
            var type = Guid.Empty;
            if (ErrorHandler.Succeeded(solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref type, out projects)))
            {
                var item = new IVsHierarchy[1]; uint fetched;
                while (projects.Next(1, item, out fetched) == VSConstants.S_OK && fetched == 1)
                {
                    object directory;
                    if (ErrorHandler.Succeeded(item[0].GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectDir, out directory))
                        && directory is string && !string.IsNullOrWhiteSpace((string)directory))
                        snapshot.Folders.Add(Path.GetFullPath((string)directory));
                }
            }
            snapshot.Folders = snapshot.Folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return snapshot;
        }
        internal static MergeContextChoice Detect(MergeContextSnapshot snapshot)
        {
            var gitRoots = new List<string>(); var workspaces = new List<string>();
            bool incomplete = snapshot.Incomplete;
            foreach (var folder in snapshot.Folders)
            {
                if (!Directory.Exists(folder)) { incomplete = true; continue; }
                try
                {
                    // Both directory and file .git markers are valid (linked worktrees use a file).
                    for (var path = folder; path != null; path = Path.GetDirectoryName(path))
                    {
                        var marker = Path.Combine(path, ".git");
                        if (Directory.Exists(marker) || File.Exists(marker))
                        { gitRoots.Add(new GitTransferEngine().Inspect(folder).Root); break; }
                    }
                    var workspace = Workstation.Current.GetLocalWorkspaceInfo(folder);
                    if (workspace != null)
                    {
                        if (string.IsNullOrEmpty(snapshot.Collection) || !Uri.Equals(workspace.ServerUri, new Uri(snapshot.Collection))) incomplete = true;
                        workspaces.Add(workspace.Name);
                    }
                }
                catch (Exception) { incomplete = true; }
            }
            return MergeContextResolver.Resolve(snapshot.Provider, gitRoots, workspaces, snapshot.Collection != null,
                snapshot.Folders.Count != 0, incomplete);
        }
    }
}
