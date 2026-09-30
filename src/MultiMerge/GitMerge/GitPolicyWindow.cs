// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MultiMerge
{
    // Git policies use the existing TFVC schema, with the team file committed on the target branch.
    public sealed class GitPolicyWindow : Window
    {
        private readonly string _root, _target, _teamPath, _personalPath;
        private readonly TextBox _team = new TextBox(), _personal = new TextBox();
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 7, 12, 7) };
        private string _initialTeamLocal, _initialPersonal;
        private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(37, 37, 38));
        private static readonly Brush Light = Brushes.WhiteSmoke;
        private static string Sample { get { return MergePolicyEngine.Serialize(new MergePolicyDocument()); } }

        public GitPolicyWindow(string root, string target)
        {
            _root = new GitTransferEngine().Inspect(root).Root;
            _target = target;
            _teamPath = Path.Combine(_root, GitTransferEngine.GitTeamPolicyFile);
            _personalPath = GitTransferEngine.GitPersonalPolicyFile;
            Title = "Git merge policies · " + target;
            Width = 1050; Height = 700; MinWidth = 700; MinHeight = 500;
            Background = Dark; Foreground = Light;
            var outer = new DockPanel();
            var intro = new TextBlock {
                Text = "Team rules are stored as .automerge-policy.json on the target branch. Save and commit that file before Update plan. Personal rules apply only on this computer and override team rules with the same ID.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12)
            };
            DockPanel.SetDock(intro, Dock.Top); outer.Children.Add(intro);
            var footer = new StackPanel { Orientation = Orientation.Vertical };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 0) };
            var validate = new Button { Content = "Validate both", Padding = new Thickness(10,5,10,5) };
            var saveTeam = new Button { Content = "Save team file", Margin = new Thickness(8,0,0,0), Padding = new Thickness(10,5,10,5) };
            var savePersonal = new Button { Content = "Save personal file", Margin = new Thickness(8,0,0,0), Padding = new Thickness(10,5,10,5) };
            buttons.Children.Add(validate); buttons.Children.Add(saveTeam); buttons.Children.Add(savePersonal);
            footer.Children.Add(buttons); footer.Children.Add(_status); DockPanel.SetDock(footer, Dock.Bottom); outer.Children.Add(footer);
            var columns = new Grid { Margin = new Thickness(12) };
            columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.ColumnDefinitions.Add(new ColumnDefinition());
            columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); columns.RowDefinitions.Add(new RowDefinition());
            var teamLabel = new TextBlock { Text = "Team file · " + GitTransferEngine.GitTeamPolicyFile, Margin = new Thickness(0,0,8,8) };
            var personalLabel = new TextBlock { Text = "Personal file · " + _personalPath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8,0,0,8) };
            Grid.SetColumn(personalLabel, 1); columns.Children.Add(teamLabel); columns.Children.Add(personalLabel);
            Prepare(_team); Prepare(_personal);
            _team.Margin = new Thickness(0,0,6,0); _personal.Margin = new Thickness(6,0,0,0);
            Grid.SetRow(_team, 1); Grid.SetRow(_personal, 1); Grid.SetColumn(_personal, 1);
            columns.Children.Add(_team); columns.Children.Add(_personal); outer.Children.Add(columns); Content = outer;
            _initialTeamLocal = File.Exists(_teamPath) ? File.ReadAllText(_teamPath, Encoding.UTF8) : null;
            _initialPersonal = File.Exists(_personalPath) ? File.ReadAllText(_personalPath, Encoding.UTF8) : null;
            var committed = GitCli.Run(_root, true, "show", "refs/heads/" + _target + ":" + GitTransferEngine.GitTeamPolicyFile);
            var onTarget = GitCli.Run(_root, false, "branch", "--show-current").Output.Trim() == _target;
            _team.Text = onTarget && _initialTeamLocal != null ? _initialTeamLocal :
                committed.Code == 0 ? committed.Output : Sample;
            _personal.Text = _initialPersonal ?? Sample;
            _status.Text = "Validate before saving. The transfer reads the committed team version and the current personal file.";
            validate.Click += (s,e) => Validate();
            saveTeam.Click += (s,e) => SaveTeam();
            savePersonal.Click += (s,e) => SavePersonal();
        }

        private static void Prepare(TextBox box)
        {
            box.AcceptsReturn = true; box.AcceptsTab = true; box.FontFamily = new FontFamily("Consolas");
            box.FontSize = 12; box.TextWrapping = TextWrapping.NoWrap; box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.Background = Dark; box.Foreground = Light;
        }

        private bool Validate()
        {
            try
            {
                var effective = new EffectiveMergePolicy(MergePolicyEngine.Parse(_team.Text), MergePolicyEngine.Parse(_personal.Text));
                if (effective.Errors.Count != 0) throw new InvalidOperationException(string.Join("; ", effective.Errors));
                _status.Text = "Valid: " + effective.PathRules.Count + " path rule(s), " +
                    effective.LineRules.Count + " protected-line rule(s).";
                return true;
            }
            catch (Exception ex) { _status.Text = "Invalid policy: " + ex.Message; return false; }
        }

        private void SaveTeam()
        {
            if (!Validate()) return;
            try
            {
                if (GitCli.Run(_root, false, "branch", "--show-current").Output.Trim() != _target)
                    throw new InvalidOperationException("Check out the target branch before saving its team file.");
                var existing = File.Exists(_teamPath) ? File.ReadAllText(_teamPath, Encoding.UTF8) : null;
                if (existing != _initialTeamLocal)
                    throw new InvalidOperationException("The team file changed outside this window; reopen it before saving.");
                File.WriteAllText(_teamPath, _team.Text, new UTF8Encoding(false));
                _initialTeamLocal = _team.Text;
                _status.Text = "Team file saved as a pending Git change. Commit it on " + _target + ", then Update plan.";
            }
            catch (Exception ex) { _status.Text = "Team file was not saved: " + ex.Message; }
        }

        private void SavePersonal()
        {
            if (!Validate()) return;
            try
            {
                var existing = File.Exists(_personalPath) ? File.ReadAllText(_personalPath, Encoding.UTF8) : null;
                if (existing != _initialPersonal)
                    throw new InvalidOperationException("The personal file changed outside this window; reopen it before saving.");
                Directory.CreateDirectory(Path.GetDirectoryName(_personalPath));
                var temporary = _personalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, _personal.Text, new UTF8Encoding(false));
                    if (File.Exists(_personalPath)) File.Replace(temporary, _personalPath, null);
                    else File.Move(temporary, _personalPath);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                _initialPersonal = _personal.Text;
                _status.Text = "Personal policy saved. Use Update plan to preview its effect.";
            }
            catch (Exception ex) { _status.Text = "Personal file was not saved: " + ex.Message; }
        }
    }
}
