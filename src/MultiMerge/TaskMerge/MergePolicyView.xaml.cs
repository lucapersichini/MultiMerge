// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Windows.Controls;

namespace MultiMerge
{
    /// <summary>
    /// Vista della scheda "Merge Policies" (DataContext: MergePolicyViewModel). Nessuna logica qui: tutto
    /// il comportamento e' nel view model, i colori vengono dal tema di VS.
    /// </summary>
    public partial class MergePolicyView : UserControl
    {
        public MergePolicyView()
        {
            InitializeComponent();
        }
    }
}
