// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Linq;
using MultiMerge.VersionControl;

namespace MultiMerge
{
    public enum MergeContextKind { Unknown, Git, Tfvc }
    public sealed class MergeContextChoice
    {
        public MergeContextKind Kind { get; private set; }
        public string Location { get; private set; }
        public string Message { get; private set; }
        public MergeContextChoice(MergeContextKind kind, string location, string message)
        { Kind = kind; Location = location; Message = message; }
    }

    public static class MergeContextResolver
    {
        // A provider setting or an Azure DevOps connection alone is not proof of the folder's VCS.
        public static MergeContextChoice Resolve(VersionControlProvider? provider, IEnumerable<string> gitRoots,
            IEnumerable<string> tfvcWorkspaces, bool tfvcConnected, bool hasFolders, bool incomplete)
        {
            var git = gitRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tfvc = tfvcWorkspaces.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (incomplete) return Unknown("Some project folders could not be inspected. Choose the repository or TFVC context.");
            if (git.Count > 1 || tfvc.Count > 1 || (git.Count != 0 && tfvc.Count != 0))
                return Unknown("Multiple repositories or conflicting Git/TFVC mappings were found. Choose the context.");
            if (git.Count == 1 && provider != VersionControlProvider.TeamFoundation)
                return new MergeContextChoice(MergeContextKind.Git, git[0], "Git · " + git[0]);
            if (tfvc.Count == 1 && tfvcConnected && provider != VersionControlProvider.Git)
                return new MergeContextChoice(MergeContextKind.Tfvc, tfvc[0], "TFVC · " + tfvc[0]);
            if (!hasFolders && git.Count == 0 && tfvc.Count == 0 && tfvcConnected && provider == VersionControlProvider.TeamFoundation)
                return new MergeContextChoice(MergeContextKind.Tfvc, "", "TFVC · connected project (choose a target and workspace below)");
            return Unknown("The current context is unavailable or ambiguous. Choose a Git repository or a connected TFVC context.");
        }
        public static MergeContextChoice Unknown(string message) { return new MergeContextChoice(MergeContextKind.Unknown, "", message); }
    }
}
