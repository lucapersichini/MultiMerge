// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Globalization;
using MultiMerge.Prism;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    // Una riga della lista dei conflitti nella scheda "Merge from Task". Solo presentazione: la
    // risoluzione vera avviene nel resolver integrato (ConflictResolverViewModel) del conflitto
    // selezionato, che il view model principale tiene aperto finche' il conflitto resta nella lista.
    public sealed class TaskMergeConflictViewModel : BindableBase
    {
        public TaskMergeConflictViewModel(Conflict conflict)
        {
            if (conflict == null)
                throw new ArgumentNullException("conflict");

            Conflict = conflict;

            Path = conflict.YourServerItem ?? conflict.TheirServerItem ?? conflict.TargetLocalItem ?? conflict.FileName ?? string.Empty;
            var separator = Path.LastIndexOfAny(new[] { '/', '\\' });
            FileName = separator >= 0 && separator < Path.Length - 1 ? Path.Substring(separator + 1) : Path;
            Folder = separator > 0 ? Path.Substring(0, separator) : string.Empty;

            CanMergeContent = conflict.CanMergeContent;
            IsNamespaceConflict = conflict.IsNamespaceConflict;
            TypeText = DescribeConflict(conflict);
        }

        public Conflict Conflict { get; private set; }

        // Server path completo (o locale, se TFVC non da' il server path).
        public string Path { get; private set; }

        public string FileName { get; private set; }

        public string Folder { get; private set; }

        // Conflitto di contenuto che TFVC sa fondere: il resolver apre l'editor a 3 vie.
        public bool CanMergeContent { get; private set; }

        public bool IsNamespaceConflict { get; private set; }

        public string TypeText { get; private set; }

        // Descrizione breve per la lista (in inglese, come il resto della UI).
        private static string DescribeConflict(Conflict conflict)
        {
            if (conflict.IsNamespaceConflict)
                return "Name conflict";

            var source = conflict.TheirChangeType;
            var target = conflict.YourChangeType;
            var sourceDeleted = Has(source, ChangeType.Delete);
            var targetDeleted = Has(target, ChangeType.Delete);
            var sourceEdited = Has(source, ChangeType.Edit);
            var targetEdited = Has(target, ChangeType.Edit);

            if (sourceDeleted && targetEdited)
                return "Deleted in source, edited in target";
            if (targetDeleted && sourceEdited)
                return "Deleted in target, edited in source";
            if (sourceEdited && targetEdited)
                return conflict.CanMergeContent ? "Edited in both branches" : "Edited in both branches (not text-mergeable)";

            return string.Format(CultureInfo.InvariantCulture, "Source: {0} \u00B7 target: {1}", source, target);
        }

        private static bool Has(ChangeType value, ChangeType flag)
        {
            return (value & flag) == flag;
        }
    }
}
