// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using MultiMerge.Prism;
using MultiMerge.Prism.Command;

namespace MultiMerge
{
    public sealed class GitCommitRow : BindableBase
    {
        private bool _selected;
        private int _mainline;
        private readonly Action _changed;
        public GitCommitRow(GitCommitInfo commit, Action changed) { Commit = commit; _changed = changed; }
        public GitCommitInfo Commit { get; private set; }
        public bool IsSelected { get { return _selected; } set { if (SetProperty(ref _selected, value)) _changed(); } }
        public int Mainline { get { return _mainline; } set { if (SetProperty(ref _mainline, value)) _changed(); } }
    }

    public sealed class GitMergeViewModel : BindableBase
    {
        private readonly GitTransferEngine _engine = new GitTransferEngine();
        private readonly IServiceProvider _services;
        private GitTransferPlan _plan;
        private GitBatchPlan _batchPlan;
        private GitBatchSession _batchSession;
        private GitTransferSession _session;
        private GitConflictContent _conflict;
        private string _root, _loadedSource, _loadedTarget;
        private string _repositoryPath = "", _source = "", _target = "", _otherTargets = "", _taskId = "",
            _policyDetails = "", _status = "Enter a Git repository folder and press Load repository.";
        private string _selectedConflict, _current = "", _incoming = "", _result = "", _note = "", _log = "";
        private bool _busy;
        public GitMergeViewModel() : this(null) { }
        public GitMergeViewModel(IServiceProvider services)
        {
            _services = services;
            Branches = new ObservableCollection<string>(); Commits = new ObservableCollection<GitCommitRow>();
            Steps = new ObservableCollection<GitTransferStep>(); Warnings = new ObservableCollection<string>(); Conflicts = new ObservableCollection<string>();
            LoadRepositoryCommand = DelegateCommand.FromAsyncHandler(() => Run(LoadRepository), () => CanEditInputs && !string.IsNullOrWhiteSpace(RepositoryPath));
            LoadCommitsCommand = DelegateCommand.FromAsyncHandler(() => Run(LoadCommits), () => CanEditInputs && _root != null && Source != Target);
            UpdatePlanCommand = DelegateCommand.FromAsyncHandler(() => Run(UpdatePlan), () => CanEditInputs && InputsLoaded && Commits.Any(c => c.IsSelected));
            FindTaskCommand = DelegateCommand.FromAsyncHandler(() => Run(FindTask), () => CanEditInputs && InputsLoaded && int.TryParse(TaskId, out _) && Commits.Count > 0);
            OpenPolicyCommand = new DelegateCommand(() => new GitPolicyWindow(_root, Target).Show(),
                () => CanEditInputs && _root != null && !string.IsNullOrWhiteSpace(Target));
            ApplyCommand = DelegateCommand.FromAsyncHandler(() => Run(Apply), () => CanEditInputs && _batchSession == null && _batchPlan != null && _batchPlan.IsValid && !IsPlanStale);
            ContinueCommand = DelegateCommand.FromAsyncHandler(() => Run(Continue), () => !IsBusy && IsActive);
            AbortCommand = DelegateCommand.FromAsyncHandler(() => Run(Abort), () => !IsBusy && IsActive);
            OpenConflictCommand = DelegateCommand.FromAsyncHandler(() => Run(OpenConflict), () => !IsBusy && IsActive && SelectedConflict != null);
            SaveConflictCommand = DelegateCommand.FromAsyncHandler(() => Run(SaveConflict), () => !IsBusy && IsActive && _conflict != null && _conflict.CanEdit);
            TakeCurrentCommand = new DelegateCommand(() => ResultText = CurrentText, () => CanEditResult);
            TakeIncomingCommand = new DelegateCommand(() => ResultText = IncomingText, () => CanEditResult);
            IncludeCommand = new DelegateCommand<IList>(rows => SelectRows(rows, true), rows => CanEditInputs && rows != null && rows.Count > 0);
            ExcludeCommand = new DelegateCommand<IList>(rows => SelectRows(rows, false), rows => CanEditInputs && rows != null && rows.Count > 0);
        }
        public ObservableCollection<string> Branches { get; private set; }
        public ObservableCollection<GitCommitRow> Commits { get; private set; }
        public ObservableCollection<GitTransferStep> Steps { get; private set; }
        public ObservableCollection<string> Warnings { get; private set; }
        public ObservableCollection<string> Conflicts { get; private set; }
        public string RepositoryPath { get { return _repositoryPath; } set { if (SetProperty(ref _repositoryPath, value)) Changed(); } }
        public string Source { get { return _source; } set { if (SetProperty(ref _source, value)) Changed(); } }
        public string Target { get { return _target; } set { if (SetProperty(ref _target, value)) Changed(); } }
        public string OtherTargets { get { return _otherTargets; } set { if (SetProperty(ref _otherTargets, value)) Changed(); } }
        public string TaskId { get { return _taskId; } set { if (SetProperty(ref _taskId, value)) Changed(); } }
        public string PolicyDetails { get { return _policyDetails; } private set { SetProperty(ref _policyDetails, value); } }
        public string Status { get { return _status; } private set { SetProperty(ref _status, value); } }
        public string LogText { get { return _log; } private set { SetProperty(ref _log, value); } }
        public bool IsBusy { get { return _busy; } private set { if (SetProperty(ref _busy, value)) Changed(); } }
        public bool IsActive { get { return _batchSession != null && _batchSession.IsActive; } }
        public bool CanLeave { get { return !IsBusy && !IsActive; } }
        public bool CanEditInputs { get { return CanLeave; } }
        public bool CanEditResult { get { return !IsBusy && IsActive && _conflict != null && _conflict.CanEdit; } }
        private bool InputsLoaded { get { return _root != null && string.Equals(RepositoryPath.TrimEnd('\\','/'), _root.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase) && Source == _loadedSource && Target == _loadedTarget; } }
        private IReadOnlyList<string> TargetList { get { return new[] { Target }.Concat(
            (OtherTargets ?? "").Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0)).ToList().AsReadOnly(); } }
        public bool IsPlanStale { get { return _batchPlan != null && (!InputsLoaded ||
            !TargetList.SequenceEqual(_batchPlan.Targets.Select(p => p.Target)) ||
            !Commits.Where(c => c.IsSelected).Select(c => c.Commit.Sha).SequenceEqual(_batchPlan.Targets[0].Steps.Select(s => s.Commit.Sha)) ||
            Commits.Where(c => c.IsSelected && c.Commit.IsMerge).Any(c => _batchPlan.Targets[0].Steps.First(s => s.Commit.Sha == c.Commit.Sha).Mainline != c.Mainline)); } }
        public string SelectionSummary { get { return Commits.Count(c => c.IsSelected) + " of " + Commits.Count + " commits selected" + (IsPlanStale ? " · selection changed: Update plan before applying" : ""); } }
        public string SelectedConflict { get { return _selectedConflict; } set { if (SetProperty(ref _selectedConflict, value)) Changed(); } }
        public string CurrentText { get { return _current; } private set { SetProperty(ref _current, value); } }
        public string IncomingText { get { return _incoming; } private set { SetProperty(ref _incoming, value); } }
        public string ResultText { get { return _result; } set { SetProperty(ref _result, value); } }
        public string ConflictNote { get { return _note; } private set { SetProperty(ref _note, value); } }
        public DelegateCommand LoadRepositoryCommand { get; private set; }
        public DelegateCommand LoadCommitsCommand { get; private set; }
        public DelegateCommand UpdatePlanCommand { get; private set; }
        public DelegateCommand FindTaskCommand { get; private set; }
        public DelegateCommand OpenPolicyCommand { get; private set; }
        public DelegateCommand ApplyCommand { get; private set; }
        public DelegateCommand ContinueCommand { get; private set; }
        public DelegateCommand AbortCommand { get; private set; }
        public DelegateCommand OpenConflictCommand { get; private set; }
        public DelegateCommand SaveConflictCommand { get; private set; }
        public DelegateCommand TakeCurrentCommand { get; private set; }
        public DelegateCommand TakeIncomingCommand { get; private set; }
        public DelegateCommand<IList> IncludeCommand { get; private set; }
        public DelegateCommand<IList> ExcludeCommand { get; private set; }
        public void Changed()
        {
            foreach (var name in new[] { "CanLeave", "CanEditInputs", "IsActive", "CanEditResult", "IsPlanStale", "SelectionSummary" }) OnPropertyChanged(name);
            foreach (var command in new[] { LoadRepositoryCommand, LoadCommitsCommand, UpdatePlanCommand, FindTaskCommand, OpenPolicyCommand, ApplyCommand, ContinueCommand, AbortCommand, OpenConflictCommand, SaveConflictCommand, TakeCurrentCommand, TakeIncomingCommand })
                if (command != null) command.RaiseCanExecuteChanged();
            if (IncludeCommand != null) IncludeCommand.RaiseCanExecuteChanged();
            if (ExcludeCommand != null) ExcludeCommand.RaiseCanExecuteChanged();
        }
        private void SelectRows(IList rows, bool included)
        {
            if (!CanEditInputs || rows == null) return;
            foreach (var row in rows.OfType<GitCommitRow>().Where(Commits.Contains).ToList()) row.IsSelected = included;
        }
        private async Task Run(Func<Task> action)
        {
            if (IsBusy) return;
            IsBusy = true;
            try { await action(); }
            catch (Exception ex) { Status = "Git: " + ex.Message; }
            finally { IsBusy = false; LogText += DateTime.Now.ToString("HH:mm:ss") + "  " + Status + "\n"; Changed(); }
        }
        private async Task LoadRepository()
        {
            var info = await Task.Run(() => _engine.Inspect(RepositoryPath));
            _root = info.Root; RepositoryPath = info.Root; Branches.Clear(); foreach (var branch in info.Branches) Branches.Add(branch);
            Target = Branches.Contains("release/1.0") ? "release/1.0" : info.CurrentBranch;
            Source = Branches.Contains("integration") ? "integration" : Branches.FirstOrDefault(b => b != Target);
            _plan = null; _session = null; _batchPlan = null; _batchSession = null; Commits.Clear(); Steps.Clear(); Warnings.Clear(); ClearConflict();
            _batchSession = await Task.Run(() => _engine.RecoverBatch(_root));
            if (_batchSession != null)
            {
                _batchPlan = _batchSession.Plan;
                _session = _batchSession.Current;
                _plan = _session == null ? _batchPlan.Targets[Math.Min(_batchSession.TargetIndex, _batchPlan.Targets.Count - 1)] : _session.Plan;
                Source = _plan.Source; Target = _plan.Target;
                OtherTargets = string.Join("; ", _batchPlan.Targets.Skip(_batchSession.TargetIndex + 1).Select(p => p.Target));
                _loadedSource = Source; _loadedTarget = Target;
                await ShowSession();
                return;
            }
            var single = await Task.Run(() => _engine.Recover(_root));
            if (single != null)
            {
                _plan = single.Plan;
                _batchPlan = new GitBatchPlan { Targets = new[] { _plan } };
                _batchSession = new GitBatchSession { Plan = _batchPlan, Current = single, Status = "Paused",
                    Message = single.Message };
                _session = single;
                Source = _plan.Source; Target = _plan.Target; _loadedSource = Source; _loadedTarget = Target;
                await ShowSession();
                return;
            }
            Status = "Choose source and target branches, then Load commits.";
        }
        private async Task LoadCommits()
        {
            if (!string.Equals(RepositoryPath, _root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Load the repository folder again.");
            var targets = TargetList;
            var commits = await Task.Run(() => _engine.LoadCommitCandidates(_root, Source, targets));
            _loadedSource = Source; _loadedTarget = Target; _plan = null; _session = null; _batchPlan = null; _batchSession = null;
            Commits.Clear(); foreach (var commit in commits) Commits.Add(new GitCommitRow(commit, Changed));
            Steps.Clear(); Warnings.Clear(); ClearConflict();
            Status = "Loaded " + commits.Count + " source commits relevant to " + targets.Count +
                " target(s) (latest 200 source commits). Select commits, then Update plan.";
        }
        private async Task UpdatePlan()
        {
            var ids = Commits.Where(c => c.IsSelected).Select(c => c.Commit.Sha).ToList();
            var mainlines = Commits.Where(c => c.IsSelected && c.Commit.IsMerge).ToDictionary(c => c.Commit.Sha, c => c.Mainline);
            var targets = TargetList;
            Status = "Previewing selected commits for " + targets.Count + " target branch(es) in isolated checkouts...";
            _batchPlan = await Task.Run(() => _engine.PreviewTargets(_root, Source, targets, ids, mainlines));
            _plan = _batchPlan.Targets[0]; _session = null; _batchSession = null; ClearConflict();
            PolicyDetails = string.Join(" | ", _batchPlan.Targets.Select(p => p.Target + ": " + (p.Policy == null ? "no policy" : p.Policy.Description)));
            ShowPlan(); Status = _batchPlan.IsValid ? "All target previews ready. Review warnings and Apply selected commits." : "A target preview has unsupported operations or errors. Review the plan.";
        }
        private async Task FindTask()
        {
            int task; if (!int.TryParse(TaskId, out task) || task <= 0) throw new InvalidOperationException("Enter a positive task ID.");
            var matching = Commits.Where(c => GitTaskLinks.MatchesTaskId(c.Commit.Comment, task))
                .Select(c => c.Commit).ToList();
            var ids = new HashSet<string>(matching.Select(c => c.Sha), StringComparer.Ordinal);
            var boardCount = 0;
            string boardNote = "";
            var context = _services == null ? null : VersionControlNavigationHelper.GetTeamFoundationContext(_services);
            if (context != null && context.HasCollection)
            {
                try
                {
                    var collection = context.TeamProjectCollection;
                    var linked = await Task.Run(() => FindLinkedGitCommits(collection, task));
                    var available = new HashSet<string>(Commits.Select(c => c.Commit.Sha), StringComparer.OrdinalIgnoreCase);
                    foreach (var sha in linked.Where(available.Contains))
                        if (ids.Add(sha)) boardCount++;
                }
                catch (Exception ex) { boardNote = " Azure Boards lookup was unavailable (" + ex.Message + "); message matches remain visible."; }
            }
            foreach (var row in Commits) row.IsSelected = ids.Contains(row.Commit.Sha);
            Status = ids.Count + " commit(s) identified for task " + task + " (" + matching.Count +
                " from messages, " + boardCount + " additional Azure Boards links)." + boardNote +
                " Review every checkbox and dependencies before Update plan.";
        }

        private static IReadOnlyList<string> FindLinkedGitCommits(TfsTeamProjectCollection collection, int taskId)
        {
            var store = collection.GetService<WorkItemStore>();
            var item = store.GetWorkItem(taskId);
            return GitTaskLinks.CommitIds(item.Links.OfType<ExternalLink>().Select(link => link.LinkedArtifactUri));
        }
        private void ShowPlan()
        {
            Steps.Clear(); if (_batchPlan != null) foreach (var plan in _batchPlan.Targets)
                foreach (var step in plan.Steps) { step.Target = plan.Target; Steps.Add(step); }
            Warnings.Clear(); if (_batchPlan != null) foreach (var plan in _batchPlan.Targets)
                foreach (var warning in plan.Warnings) Warnings.Add(plan.Target + ": " + warning);
        }
        private async Task Apply()
        {
            if (IsPlanStale || _batchPlan == null) throw new InvalidOperationException("Update every target plan first.");
            var messages = new List<string> { "Apply " + _batchPlan.CommitCount + " selected commits from " + Source + " to " +
                string.Join(", ", _batchPlan.Targets.Select(p => p.Target)) + ". Targets run in order; a conflict pauses the batch. Earlier completed targets remain local if a later one is aborted. Nothing is pushed. Build and test each target." };
            messages.AddRange(Warnings);
            var confirmation = new TaskMergeWarningsWindow(0, messages);
            confirmation.Show();
            if (!await confirmation.Confirmation) { Status = "Apply cancelled: no changes were made."; return; }
            Status = "Applying selected commits to " + _batchPlan.Targets.Count + " target(s)...";
            _batchSession = await Task.Run(() => _engine.ApplyBatch(_batchPlan));
            _session = _batchSession.Current;
            await ShowSession();
        }
        private async Task Continue() { await Task.Run(() => _engine.ContinueBatch(_batchSession)); _session = _batchSession.Current; await ShowSession(); }
        private async Task Abort() { await Task.Run(() => _engine.AbortBatch(_batchSession)); _session = _batchSession.Current; await ShowSession(); }
        private async Task ShowSession()
        {
            ShowPlan(); ClearConflict();
            if (_session != null) foreach (var path in _session.Conflicts) Conflicts.Add(path);
            Status = _batchSession == null ? _session.Message : _batchSession.Message;
            if (Conflicts.Count != 0) { SelectedConflict = Conflicts[0]; await OpenConflict(); }
        }
        private async Task OpenConflict()
        {
            _conflict = await Task.Run(() => _engine.ReadConflict(_session, SelectedConflict));
            CurrentText = _conflict.Current; IncomingText = _conflict.Incoming; ResultText = _conflict.Result; ConflictNote = _conflict.Note;
        }
        private async Task SaveConflict()
        {
            await Task.Run(() => _engine.SaveConflict(_session, _conflict, ResultText));
            await ShowSession();
            if (Conflicts.Count == 0) Status = "Conflicts staged. Continue to finish this commit and apply the remaining selected commits.";
        }
        private void ClearConflict() { _conflict = null; Conflicts.Clear(); SelectedConflict = null; CurrentText = ""; IncomingText = ""; ResultText = ""; ConflictNote = ""; }
    }
}
