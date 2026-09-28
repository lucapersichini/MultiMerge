// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace MultiMerge
{
    [Export(typeof(ILogger))]
    public class VsLogger : LoggerBase
    {
        private readonly IVsOutputWindowPane _pane;

        [ImportingConstructor]
        public VsLogger(IServiceProvider sp)
        {
            var window = (IVsOutputWindow)sp.GetService(typeof(SVsOutputWindow));

            var generalPaneGuid = VSConstants.OutputWindowPaneGuid.GeneralPane_guid;

            // If the pane does not yet exist, create it
            var hr = window.GetPane(ref generalPaneGuid, out _pane);
            if (ErrorHandler.Failed(hr))
                ErrorHandler.ThrowOnFailure(window.CreatePane(ref generalPaneGuid, "MultiMerge", 1, 0));

            ErrorHandler.ThrowOnFailure(window.GetPane(ref generalPaneGuid, out _pane));
        }

        protected override void WriteMessage(string message)
        {
            ErrorHandler.ThrowOnFailure(_pane.OutputStringThreadSafe(message));
        }
    }
}
