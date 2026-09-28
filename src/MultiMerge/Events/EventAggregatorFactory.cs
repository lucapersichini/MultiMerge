// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using MultiMerge.Prism.Events;

namespace MultiMerge.Events
{
	internal static class EventAggregatorFactory
	{
		private static readonly IEventAggregator _eventAggregator = new EventAggregator();

		public static IEventAggregator Get()
		{
			return _eventAggregator;
		}
	}
}
