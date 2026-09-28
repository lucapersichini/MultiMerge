// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MultiMerge
{
    /// <summary>
    /// Vista della scheda "Merge from Task" (DataContext: TaskMergeViewModel).
    /// </summary>
    public partial class TaskMergeView : UserControl
    {
        // Intestazione compatta (catena in corso o conflitti aperti) con le caselle riaperte da "Edit".
        // Stato solo di vista: il view model non ne sa nulla.
        public static readonly DependencyProperty IsHeaderEditorOpenProperty =
            DependencyProperty.Register("IsHeaderEditorOpen", typeof(bool), typeof(TaskMergeView),
                new FrameworkPropertyMetadata(false));

        public TaskMergeView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        public bool IsHeaderEditorOpen
        {
            get { return (bool)GetValue(IsHeaderEditorOpenProperty); }
            set { SetValue(IsHeaderEditorOpenProperty, value); }
        }

        private void TaskChangesetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var viewModel = DataContext as TaskMergeViewModel;
            if (viewModel != null)
                viewModel.RaiseChangesetRowSelectionCommands();
        }

        // Righe dei passi delle parti non ancora eseguibili (A1): il clic non le seleziona (restano
        // "non interattive"), ma il mouse le raggiunge e i loro tooltip si vedono. Fa eccezione il menu
        // della colonna "Policy": l'azione di un passo di una parte successiva si deve poter cambiare
        // (es. Skip di tutti i passi dello stesso item), prima che la catena ci arrivi.
        private void LockedStep_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as FrameworkElement;
            var step = item == null ? null : item.DataContext as TaskMergeStepViewModel;
            if (step != null && step.IsLocked && !IsInsideComboBox(e.OriginalSource as DependencyObject, item))
                e.Handled = true;
        }

        private static bool IsInsideComboBox(DependencyObject element, DependencyObject stop)
        {
            for (var current = element; current != null && !ReferenceEquals(current, stop); current = GetParent(current))
            {
                if (current is ComboBox)
                    return true;
            }
            return false;
        }

        // Albero visuale, con ripiego su quello logico per gli elementi di contenuto (es. Run).
        private static DependencyObject GetParent(DependencyObject element)
        {
            if (element is System.Windows.Media.Visual || element is System.Windows.Media.Media3D.Visual3D)
                return System.Windows.Media.VisualTreeHelper.GetParent(element);
            return LogicalTreeHelper.GetParent(element);
        }

        // Il registro segue sempre l'ultima riga.
        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox != null)
                textBox.ScrollToEnd();
        }

        // Il view model e' statico (vive quanto la sessione di VS): l'aggancio e' debole, cosi' non
        // tiene in vita la vista se la tool window venisse ricreata.
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var oldViewModel = e.OldValue as INotifyPropertyChanged;
            if (oldViewModel != null)
            {
                PropertyChangedEventManager.RemoveHandler(oldViewModel, OnViewModelPropertyChanged, "ActiveResolver");
                PropertyChangedEventManager.RemoveHandler(oldViewModel, OnViewModelPropertyChanged, "IsCompactHeader");
            }

            var newViewModel = e.NewValue as INotifyPropertyChanged;
            if (newViewModel != null)
            {
                PropertyChangedEventManager.AddHandler(newViewModel, OnViewModelPropertyChanged, "ActiveResolver");
                PropertyChangedEventManager.AddHandler(newViewModel, OnViewModelPropertyChanged, "IsCompactHeader");
            }

            ShowActiveResolver();
            CloseHeaderEditorIfExpanded();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsCompactHeader")
            {
                CloseHeaderEditorIfExpanded();
                return;
            }
            ShowActiveResolver();
        }

        // Fuori dalla modalita' compatta le caselle sono sempre visibili: "Edit" riparte chiuso la
        // prossima volta che l'intestazione si compatta.
        private void CloseHeaderEditorIfExpanded()
        {
            var viewModel = DataContext as TaskMergeViewModel;
            if (viewModel == null || !viewModel.IsCompactHeader)
                IsHeaderEditorOpen = false;
        }

        // Una vista nuova per ogni resolver, invece di un DataTemplate: con lo stesso DataType il
        // ContentPresenter di WPF riuserebbe la stessa ConflictResolverView cambiandole solo il
        // DataContext, e gli editor di testo incorporati di un conflitto passerebbero al successivo.
        // Cosi' ogni conflitto ha i suoi editor, e quelli del precedente escono dall'albero visuale
        // (Unloaded) insieme alla sua vista.
        private void ShowActiveResolver()
        {
            var viewModel = DataContext as TaskMergeViewModel;
            var resolver = viewModel == null ? null : viewModel.ActiveResolver;

            var current = ResolverHost.Content as FrameworkElement;
            if (current == null && resolver == null)
                return;
            if (current != null && ReferenceEquals(current.DataContext, resolver))
                return;

            // Staccare subito la vista uscente dal suo resolver: scarica nel view model le modifiche non
            // ancora passate (il resolver resta vivo e si ritrova tornando su quel conflitto) e toglie
            // gli handler, cosi' la vista vecchia non resta agganciata al resolver.
            if (current != null)
                current.DataContext = null;

            ResolverHost.Content = resolver == null ? null : new ConflictResolverView { DataContext = resolver };
        }
    }
}
