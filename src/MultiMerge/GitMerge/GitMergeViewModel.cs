// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MultiMerge.Prism;
using MultiMerge.Prism.Command;

namespace MultiMerge
{
    public sealed class GitCommitRow : BindableBase
    {
        private bool _selected;
        private readonly Action _changed;
        public GitCommitRow(GitCommitInfo commit, Action changed) { Commit = commit; _changed = changed; }
        public GitCommitInfo Commit { get; private set; }
        public bool IsSelected { get { return _selected; } set { if (SetProperty(ref _selected, value)) _changed(); } }
    }

    public sealed class GitMergeViewModel : BindableBase
    {
        private readonly GitTransferEngine _engine = new GitTransferEngine();
        private GitTransferPlan _plan;
        private GitTransferSession _session;
        private GitConflictContent _conflict;
        private string _root, _loadedSource, _loadedTarget;
        private string _repositoryPath = "", _source = "", _target = "", _status = "Enter a Git repository folder and press Load repository.";
        private string _selectedConflict, _current = "", _incoming = "", _result = "", _note = "", _log = "";
        private bool _busy;
        public GitMergeViewModel()
        {
            Branches = new ObservableCollection<string>(); Commits = new ObservableCollection<GitCommitRow>();
            Steps = new ObservableCollection<GitTransferStep>(); Warnings = new ObservableCollection<string>(); Conflicts = new ObservableCollection<string>();
            LoadRepositoryCommand = DelegateCommand.FromAsyncHandler(() => Run(LoadRepository), () => CanEditInputs && !string.IsNullOrWhiteSpace(RepositoryPath));
            LoadCommitsCommand = DelegateCommand.FromAsyncHandler(() => Run(LoadCommits), () => CanEditInputs && _root != null && Source != Target);
            UpdatePlanCommand = DelegateCommand.FromAsyncHandler(() => Run(UpdatePlan), () => CanEditInputs && InputsLoaded && Commits.Any(c => c.IsSelected));
            ApplyCommand = DelegateCommand.FromAsyncHandler(() => Run(Apply), () => CanEditInputs && _session == null && _plan != null && _plan.IsValid && !IsPlanStale);
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
        public string Status { get { return _status; } private set { SetProperty(ref _status, value); } }
        public string LogText { get { return _log; } private set { SetProperty(ref _log, value); } }
        public bool IsBusy { get { return _busy; } private set { if (SetProperty(ref _busy, value)) Changed(); } }
        public bool IsActive { get { return _session != null && _session.IsActive; } }
        public bool CanLeave { get { return !IsBusy && !IsActive; } }
        public bool CanEditInputs { get { return CanLeave; } }
        public bool CanEditResult { get { return !IsBusy && IsActive && _conflict != null && _conflict.CanEdit; } }
        private bool InputsLoaded { get { return _root != null && string.Equals(RepositoryPath.TrimEnd('\\','/'), _root.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase) && Source == _loadedSource && Target == _loadedTarget; } }
        public bool IsPlanStale { get { return _plan != null && (!InputsLoaded || !Commits.Where(c => c.IsSelected).Select(c => c.Commit.Sha).SequenceEqual(_plan.Steps.Select(s => s.Commit.Sha))); } }
        public string SelectionSummary { get { return Commits.Count(c => c.IsSelected) + " of " + Commits.Count + " commits selected" + (IsPlanStale ? " · selection changed: Update plan before applying" : ""); } }
        public string SelectedConflict { get { return _selectedConflict; } set { if (SetProperty(ref _selectedConflict, value)) Changed(); } }
        public string CurrentText { get { return _current; } private set { SetProperty(ref _current, value); } }
        public string IncomingText { get { return _incoming; } private set { SetProperty(ref _incoming, value); } }
        public string ResultText { get { return _result; } set { SetProperty(ref _result, value); } }
        public string ConflictNote { get { return _note; } private set { SetProperty(ref _note, value); } }
        public DelegateCommand LoadRepositoryCommand { get; private set; }
        public DelegateCommand LoadCommitsCommand { get; private set; }
        public DelegateCommand UpdatePlanCommand { get; private set; }
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
            foreach (var command in new[] { LoadRepositoryCommand, LoadCommitsCommand, UpdatePlanCommand, ApplyCommand, ContinueCommand, AbortCommand, OpenConflictCommand, SaveConflictCommand, TakeCurrentCommand, TakeIncomingCommand })
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
            _plan = null; _session = null; Commits.Clear(); Steps.Clear(); Warnings.Clear(); ClearConflict();
            Status = "Choose source and target branches, then Load commits.";
        }
        private async Task LoadCommits()
        {
            if (!string.Equals(RepositoryPath, _root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Load the repository folder again.");
            var commits = await Task.Run(() => _engine.LoadCommits(_root, Source, Target));
            _loadedSource = Source; _loadedTarget = Target; _plan = null; _session = null;
            Commits.Clear(); foreach (var commit in commits) Commits.Add(new GitCommitRow(commit, Changed));
            Steps.Clear(); Warnings.Clear(); ClearConflict();
            Status = "Loaded " + commits.Count + " commits (up to the latest 200). Select commits, then Update plan.";
        }
        private async Task UpdatePlan()
        {
            var ids = Commits.Where(c => c.IsSelected).Select(c => c.Commit.Sha).ToList();
            Status = "Previewing selected commits in an isolated checkout...";
            _plan = await Task.Run(() => _engine.Preview(_root, Source, Target, ids));
            _session = null; ClearConflict();
            ShowPlan(); Status = _plan.IsValid ? "Preview ready. Review warnings and Apply selected commits." : "The preview has unsupported operations or errors. Review the plan.";
        }
        private void ShowPlan()
        {
            Steps.Clear(); if (_plan != null) foreach (var step in _plan.Steps) Steps.Add(step);
            Warnings.Clear(); if (_plan != null) foreach (var warning in _plan.Warnings) Warnings.Add(warning);
        }
        private async Task Apply()
        {
            if (IsPlanStale || _plan == null) throw new InvalidOperationException("Update the plan first.");
            var messages = new List<string> { "Apply " + _plan.Steps.Count + " selected commits from " + Source + " to " + Target + ". The target branch will be checked out. Commits stay local until you push. Build and test the result: Git cannot prove that all code dependencies are included." };
            messages.AddRange(_plan.Warnings);
            var confirmation = new TaskMergeWarningsWindow(0, messages);
            confirmation.Show();
            if (!await confirmation.Confirmation) { Status = "Apply cancelled: no changes were made."; return; }
            Status = "Applying selected commits to " + Target + "...";
            _session = await Task.Run(() => _engine.Apply(_plan));
            await ShowSession();
        }
        private async Task Continue() { await Task.Run(() => _engine.Continue(_session)); await ShowSession(); }
        private async Task Abort() { await Task.Run(() => _engine.Abort(_session)); await ShowSession(); }
        private async Task ShowSession()
        {
            ShowPlan(); ClearConflict();
            foreach (var path in _session.Conflicts) Conflicts.Add(path);
            Status = _session.Message;
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