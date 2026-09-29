using System.Windows.Controls;
namespace MultiMerge
{
    public partial class GitMergeView : UserControl
    {
        public GitMergeView() { InitializeComponent(); }
        private void CommitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var model = DataContext as GitMergeViewModel;
            if (model != null) model.Changed();
        }
    }
}