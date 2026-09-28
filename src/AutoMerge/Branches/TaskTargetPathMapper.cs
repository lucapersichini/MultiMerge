using System;

namespace AutoMerge
{
    // Flusso Task: il merge parte dal top folder dei changeset del gruppo, che puo' essere una
    // sottocartella del branch sorgente. Il target scritto dall'utente e' invece la radice del branch
    // di destinazione: la stessa sottocartella va riportata sotto quella radice, altrimenti TFVC fonde
    // $/Src/Web/Rest dentro $/Rel (radice) invece che dentro $/Rel/Web/Rest.
    public static class TaskTargetPathMapper
    {
        public static string MapToTarget(string sourceBranch, string sourceTopFolder, string targetBranch)
        {
            if (string.IsNullOrEmpty(targetBranch))
                return targetBranch;

            var target = targetBranch.TrimEnd('/');
            if (string.IsNullOrEmpty(sourceBranch) || string.IsNullOrEmpty(sourceTopFolder))
                return target;

            var branch = sourceBranch.TrimEnd('/');
            var folder = sourceTopFolder.TrimEnd('/');

            // Percorsi TFVC: maiuscole/minuscole non contano.
            if (folder.StartsWith(branch + "/", StringComparison.OrdinalIgnoreCase))
                return target + folder.Substring(branch.Length);

            // Top folder uguale al branch sorgente (o, per difesa, fuori da esso): radice del target.
            return target;
        }
    }
}
