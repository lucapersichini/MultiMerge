// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace MultiMerge
{
    // One visible workflow; explicit choices are only shown when automatic detection is inconclusive.
    public sealed class MergeContextView : UserControl
    {
        private readonly ContentControl _workflow = new ContentControl();
        private readonly TextBlock _label = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,10,0) };
        private readonly StackPanel _choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,8,0,0) };
        private readonly Button _git = new Button { Content = "Choose Git repository", Padding = new Thickness(10,5,10,5) };
        private readonly Button _tfvc = new Button { Content = "Use connected TFVC context", Margin = new Thickness(8,0,0,0), Padding = new Thickness(10,5,10,5) };
        private readonly Button _refresh = new Button { Content = "Detect context", Padding = new Thickness(10,5,10,5) };
        private readonly FrameworkElement _gitView, _tfvcView;
        public event Action GitRequested;
        public event Action TfvcRequested;
        public event Action DetectionRequested;
        public MergeContextChoice Current { get; private set; }
        public MergeContextView(FrameworkElement gitView, FrameworkElement tfvcView)
        {
            _gitView = gitView; _tfvcView = tfvcView;
            SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            var layout = new DockPanel();
            var header = new StackPanel { Margin = new Thickness(14,10,14,4) };
            DockPanel.SetDock(header, Dock.Top);
            var row = new DockPanel();
            DockPanel.SetDock(_refresh, Dock.Right); row.Children.Add(_refresh); row.Children.Add(_label);
            header.Children.Add(row); _choices.Children.Add(_git); _choices.Children.Add(_tfvc); header.Children.Add(_choices);
            layout.Children.Add(header); layout.Children.Add(_workflow); Content = layout;
            _git.Click += (s,e) => { if (GitRequested != null) GitRequested(); };
            _tfvc.Click += (s,e) => { if (TfvcRequested != null) TfvcRequested(); };
            _refresh.Click += (s,e) => { if (DetectionRequested != null) DetectionRequested(); };
            ShowContext(MergeContextResolver.Unknown("Detecting repository context..."), false);
        }
        public bool ShowContext(MergeContextChoice choice, bool busy)
        {
            if (busy) { SetBusy(true); return false; }
            Current = choice;
            _label.Text = choice.Message;
            _workflow.Content = choice.Kind == MergeContextKind.Git ? _gitView : choice.Kind == MergeContextKind.Tfvc ? _tfvcView : null;
            _choices.Visibility = choice.Kind == MergeContextKind.Unknown ? Visibility.Visible : Visibility.Collapsed;
            SetBusy(false);
            return true;
        }
        public void SetBusy(bool busy)
        {
            _refresh.IsEnabled = _git.IsEnabled = _tfvc.IsEnabled = !busy;
            if (busy && Current != null) _label.Text = Current.Message + " · context locked while transfer is active";
            else if (Current != null) _label.Text = Current.Message;
        }
    }
}
