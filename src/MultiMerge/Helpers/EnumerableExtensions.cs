// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.Generic;
using System.Linq;

namespace MultiMerge
{
	internal static class EnumerableExtensions
	{
		public static bool IsNullOrEmpty<T>(this IEnumerable<T> items)
		{
			return items == null || !items.Any();
		}
	}
}