// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;

namespace MultiMerge
{
	public interface ILogger
	{
	    void Debug(string message);

	    void Debug(string message, params object[] args);

        void Info(string message);

        void Info(string message, params object[] args);

        void Error(string message);

        void Error(string message, Exception ex);
    }
}
