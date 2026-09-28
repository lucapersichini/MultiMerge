// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Windows.Input;
using Microsoft.TeamFoundation.Controls;

namespace MultiMerge
{
    internal class Notification
    {
        public string Message { get; set; }

        public NotificationType NotificationType { get; set; }

        public ICommand Command { get; set; }
    }
}
