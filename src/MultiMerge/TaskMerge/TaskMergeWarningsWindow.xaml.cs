// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace MultiMerge
{
    public partial class TaskMergeWarningsWindow : Window
    {
        private readonly TaskCompletionSource<bool> _confirmation =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _confirmed;

        public TaskMergeWarningsWindow(int workItemId, IReadOnlyList<string> warnings)
        {
            if (warnings == null)
                throw new ArgumentNullException("warnings");
            InitializeComponent();
            SummaryText.Text = string.Format(CultureInfo.InvariantCulture, "Work item #{0}: {1}",
                workItemId, TaskMergeText.Count(warnings.Count, "warning"));
            WarningsText.Text = string.Join("\n\n", warnings.Select((warning, index) =>
                (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + warning));
            Closed += (sender, args) => _confirmation.TrySetResult(_confirmed);
            Loaded += (sender, args) => CancelButton.Focus();
        }

        public Task<bool> Confirmation { get { return _confirmation.Task; } }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        }
    }
}