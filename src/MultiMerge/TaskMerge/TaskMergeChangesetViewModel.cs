// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MultiMerge.Prism;

namespace MultiMerge
{
    // Un changeset del task nel pannello delle caselle della scheda "Merge from Task": selezionato
    // entra nel piano, escluso diventa per il planner un changeset "di terzi". Il cambio della casella
    // notifica la scelta al view model; Update plan ricalcola piano e anteprima.
    public sealed class TaskMergeChangesetViewModel : BindableBase
    {
        private readonly Action<TaskMergeChangesetViewModel> _selectionChanged;
        private bool _isSelected = true;
        private string _warningText;
        private bool _isWarningOnExcluded;

        public TaskMergeChangesetViewModel(TaskMergeChangesetInfo info, Action<TaskMergeChangesetViewModel> selectionChanged)
        {
            if (info == null)
                throw new ArgumentNullException("info");

            _selectionChanged = selectionChanged;
            ChangesetId = info.ChangesetId;
            IdText = "C" + info.ChangesetId.ToString(CultureInfo.InvariantCulture);
            DateText = info.CreationDate.HasValue
                ? info.CreationDate.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            Author = info.Author ?? string.Empty;
            Comment = info.Comment ?? string.Empty;
            CommentFirstLine = FirstLine(info.Comment);

            var files = TaskMergeText.Count(info.FileCount, "file");
            ItemCountText = info.FolderCount > 0
                ? files + ", " + TaskMergeText.Count(info.FolderCount, "folder")
                : files;
        }

        public int ChangesetId { get; private set; }

        // "C162095"
        public string IdText { get; private set; }

        // "2026-09-12 14:03" (vuoto se il changeset non si e' potuto leggere)
        public string DateText { get; private set; }

        public string Author { get; private set; }

        public string Comment { get; private set; }

        public string CommentFirstLine { get; private set; }

        // "3 files" / "3 files, 1 folder"
        public string ItemCountText { get; private set; }

        public string ToolTipText
        {
            get
            {
                var text = string.Format(CultureInfo.InvariantCulture, "{0}  {1}  {2}\n{3}\n{4}",
                    IdText, DateText, Author, ItemCountText, string.IsNullOrEmpty(Comment) ? "(no comment)" : Comment);
                return HasWarning ? text + "\n\n" + _warningText : text;
            }
        }

        // Selezionato: il changeset entra nel prossimo piano applicato con Update plan.
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (!SetProperty(ref _isSelected, value))
                    return;
                if (_selectionChanged != null)
                    _selectionChanged(this);
            }
        }

        // Cambio senza ricalcolo (es. "Select all", che ricalcola una volta sola alla fine).
        internal void SetSelectedSilently(bool value)
        {
            SetProperty(ref _isSelected, value, "IsSelected");
        }

        // Avviso di dipendenza accanto al changeset (null se nessuno).
        public string WarningText
        {
            get { return _warningText; }
        }

        public bool HasWarning
        {
            get { return !string.IsNullOrEmpty(_warningText); }
        }

        // L'avviso e' sul changeset escluso (true) o sul selezionato che ne dipende (false).
        public bool IsWarningOnExcluded
        {
            get { return _isWarningOnExcluded; }
        }

        internal void SetWarning(string text, bool onExcluded)
        {
            _warningText = string.IsNullOrEmpty(text) ? null : text;
            _isWarningOnExcluded = onExcluded;
            OnPropertyChanged("WarningText");
            OnPropertyChanged("HasWarning");
            OnPropertyChanged("IsWarningOnExcluded");
            OnPropertyChanged("ToolTipText");
        }

        // Avvisi di dipendenza da mostrare accanto a ogni changeset: sull'escluso l'elenco dei
        // selezionati successivi che toccano gli stessi item, sul selezionato quello degli esclusi.
        internal static void ApplyWarnings(IEnumerable<TaskMergeChangesetViewModel> rows, IReadOnlyList<ChangesetDependencyWarning> warnings)
        {
            var list = warnings ?? new List<ChangesetDependencyWarning>();
            foreach (var row in rows)
            {
                var id = row.ChangesetId;
                if (!row.IsSelected)
                {
                    var dependents = list.Where(w => w.ExcludedChangesetId == id).Select(w => w.DependentChangesetId).Distinct().OrderBy(x => x).ToList();
                    row.SetWarning(dependents.Count == 0
                        ? null
                        : string.Format(CultureInfo.InvariantCulture,
                            "Excluded, but the selected {0} also change the same items: the result may need this changeset.",
                            TaskMergeText.Changesets(dependents)),
                        true);
                }
                else
                {
                    var excluded = list.Where(w => w.DependentChangesetId == id).Select(w => w.ExcludedChangesetId).Distinct().OrderBy(x => x).ToList();
                    row.SetWarning(excluded.Count == 0
                        ? null
                        : string.Format(CultureInfo.InvariantCulture,
                            "Changes items also changed by the excluded {0}: it may need them.",
                            TaskMergeText.Changesets(excluded)),
                        false);
                }
            }
        }

        private static string FirstLine(string comment)
        {
            if (string.IsNullOrWhiteSpace(comment))
                return string.Empty;
            var line = comment.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return line == null ? string.Empty : line.Trim();
        }
    }
}
