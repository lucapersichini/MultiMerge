// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MultiMerge
{
	public interface IChangesetProvider
	{
		 Task<List<ChangesetViewModel>> GetChangesets(string userLogin);
	}
}