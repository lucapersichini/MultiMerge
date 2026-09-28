using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AutoMerge.Prism.Command;
using Microsoft.TeamFoundation.Controls;
using TeamExplorerSectionViewModelBase = AutoMerge.Base.TeamExplorerSectionViewModelBase;

namespace AutoMerge
{
    // Sezione "Merge From Task" della pagina Auto Merge in Team Explorer. E' il punto d'ingresso dove
    // l'utente e' abituato a lavorare: raccoglie work item e target branch e apre la scheda dedicata
    // "Merge from Task" (TaskMergeToolWindow), precompilata e gia' in caricamento. Il merge vero
    // (batch, avanzamento, risoluzione dei conflitti integrata) vive solo nella scheda.
    //
    // Il vecchio flusso a catena dentro Team Explorer (BranchesViewModel pilotato da un gruppo del
    // task) resta nel codice ma inerte: questa sezione non pubblica piu' SelectTaskChangesetGroupEvent
    // e tiene TaskMergeSession.ActiveGroup/Groups a null.
    public sealed class WorkItemMergeViewModel : TeamExplorerSectionViewModelBase
    {
        public WorkItemMergeViewModel(ILogger logger) : base(logger)
        {
            Title = "Merge From Task";
            IsVisible = true;
            IsExpanded = true;
            IsBusy = false;
            OpenTabCommand = new DelegateCommand(OpenTabExecute, OpenTabCanExecute);
        }

        // I setter scrivono anche in TaskMergeSession, cosi' i testi sopravvivono alla ricreazione
        // della pagina Auto Merge.
        public string WorkItemIdText
        {
            get { return _workItemIdText; }
            set
            {
                _workItemIdText = value;
                TaskMergeSession.WorkItemIdText = value;
                RaisePropertyChanged("WorkItemIdText");
                OpenTabCommand.RaiseCanExecuteChanged();
            }
        }
        private string _workItemIdText;

        // Branch di destinazione scritto a mano dall'utente (uno solo: la scheda lavora su un target
        // per volta).
        public string TargetBranchesText
        {
            get { return _targetBranchesText; }
            set
            {
                _targetBranchesText = value;
                TaskMergeSession.TargetBranchesText = value;
                RaisePropertyChanged("TargetBranchesText");
                OpenTabCommand.RaiseCanExecuteChanged();
            }
        }
        private string _targetBranchesText;

        public string StatusMessage
        {
            get { return _statusMessage; }
            set
            {
                _statusMessage = value;
                TaskMergeSession.StatusMessage = value;
                RaisePropertyChanged("StatusMessage");
            }
        }
        private string _statusMessage;

        public DelegateCommand OpenTabCommand { get; private set; }

        protected override Task InitializeAsync(object sender, SectionInitializeEventArgs e)
        {
            var tfs = Context == null ? null : Context.TeamProjectCollection;
            TaskMergeSession.EnsureCollection(tfs == null ? null : tfs.Uri);
            if (tfs != null)
                TaskMergeSession.CollectionUri = tfs.Uri;

            // Il flusso a catena dentro Team Explorer non si attiva piu' da qui.
            TaskMergeSession.ActiveGroup = null;
            TaskMergeSession.Groups = null;

            RestoreFromSession();
            return Task.FromResult(0);
        }

        // Ripristina i testi scritti prima che la pagina venisse ricreata (vedi TaskMergeSession).
        private void RestoreFromSession()
        {
            // Setter pubblici: sollevano RaisePropertyChanged, altrimenti le caselle in XAML
            // resterebbero vuote anche col valore giusto in memoria.
            WorkItemIdText = TaskMergeSession.WorkItemIdText;
            TargetBranchesText = TaskMergeSession.TargetBranchesText;
            StatusMessage = TaskMergeSession.StatusMessage;
        }

        private void OpenTabExecute()
        {
            try
            {
                int workItemId;
                if (!int.TryParse((WorkItemIdText ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out workItemId)
                    || workItemId <= 0)
                {
                    StatusMessage = "Invalid work item id.";
                    return;
                }

                var targets = (TargetBranchesText ?? string.Empty)
                    .Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
                if (targets.Count == 0)
                {
                    StatusMessage = "Enter the target branch.";
                    return;
                }
                if (targets.Count > 1)
                {
                    StatusMessage = "One target branch at a time.";
                    return;
                }
                if (!targets[0].StartsWith("$/", StringComparison.Ordinal))
                {
                    StatusMessage = "The target branch must be a server path (e.g. $/Project/Release).";
                    return;
                }

                StatusMessage = null;
                var idText = workItemId.ToString(CultureInfo.InvariantCulture);
                var target = targets[0];
                // La scheda si apre in modo asincrono (puo' dover caricare il package); gli errori
                // finiscono in una message box dentro ShowAndOpenAsync.
                _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(
                    () => TaskMergeToolWindow.ShowAndOpenAsync(idText, target));
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        private bool OpenTabCanExecute()
        {
            int workItemId;
            return int.TryParse((WorkItemIdText ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out workItemId)
                && workItemId > 0
                && !string.IsNullOrWhiteSpace(TargetBranchesText);
        }
    }
}
