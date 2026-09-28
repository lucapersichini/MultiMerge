// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MultiMerge
{
    /// <summary>
    /// Risolutore di un conflitto dentro la scheda "Merge from Task" (DataContext: ConflictResolverViewModel).
    /// </summary>
    // In alto il visore differenze di VS (SOURCE | TARGET, sola lettura), in basso il RESULT in una
    // TextBox modificabile. La TextBox non e' legata in XAML: il code-behind tiene allineati testo e
    // view model con un leggero ritardo (niente ricalcoli a ogni tasto su file grandi) e scarica le
    // modifiche in sospeso appena la TextBox perde il focus (quindi prima di ogni click sui pulsanti).
    //
    // Navigazione sincronizzata: il blocco corrente viene selezionato nel RESULT e il confronto scorre
    // alle stesse righe del source e del target (ConflictResolverViewModel.CurrentConflictLocation).
    // Tutto a layout pronto (priorita' ContextIdle, con qualche nuovo tentativo): subito dopo aver
    // assegnato il testo o reso visibile l'editor le righe non sono ancora misurate.
    public partial class ConflictResolverView : UserControl
    {
        private static readonly TimeSpan EditCommitDelay = TimeSpan.FromMilliseconds(400);

        // Tentativi di portare in vista il blocco quando la TextBox non ha ancora un layout utilizzabile.
        private const int MaxRevealAttempts = 8;

        // Proporzioni confronto / risultato scelte col divisore: valgono per tutta la sessione (ogni
        // conflitto ha una vista nuova).
        private static GridLength _savedDiffHeight = new GridLength(3, GridUnitType.Star);
        private static GridLength _savedResultHeight = new GridLength(2, GridUnitType.Star);

        private readonly DispatcherTimer _commitTimer;
        private ConflictResolverViewModel _viewModel;

        // Visore differenze (o ripiego a TextBox) e view model per cui e' stato creato.
        private EmbeddedDiffViewer _diffViewer;
        private ConflictResolverViewModel _diffOwner;

        // True mentre il code-behind scrive nella TextBox (non e' una modifica dell'utente).
        private bool _updatingResultBox;
        // True mentre il testo della TextBox passa al view model: la notifica ResultText che ne segue
        // porta lo stesso testo, quindi non si rilegge ne' si riconfronta la TextBox (file grandi).
        private bool _committingEdit;
        // Modifiche dell'utente non ancora passate al view model.
        private bool _pendingEdit;

        // Portare in vista il blocco corrente: nel RESULT (selezione + scorrimento) e/o nel confronto.
        private bool _revealResultPending;
        private bool _revealDiffPending;
        private bool _revealScheduled;
        private int _revealAttempts;
        // Blocco su cui il confronto e' stato portato l'ultima volta (si riscorre solo se cambia).
        private ConflictLocation _revealedLocation;
        // L'editor era visibile all'ultima notifica ShowEditor (il blocco si mostra al passaggio a visibile).
        private bool _editorShown;
        // Aggiornamento del segno del blocco corrente gia' in coda.
        private bool _markerUpdateScheduled;

        public ConflictResolverView()
        {
            InitializeComponent();

            DiffRow.Height = _savedDiffHeight;
            ResultRow.Height = _savedResultHeight;

            _commitTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = EditCommitDelay };
            _commitTimer.Tick += CommitTimer_Tick;

            // Il segno del blocco corrente segue lo scorrimento e le dimensioni del RESULT.
            ResultTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnResultScrollChanged));
            ResultTextBox.SizeChanged += (sender, e) => ScheduleMarkerUpdate();

            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        #region View model wiring

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                // Le modifiche in sospeso appartengono al view model precedente.
                CommitPendingEdit();
                Detach(_viewModel);
                CloseDiffViewer();
                ClearReveal();

                _viewModel = e.NewValue as ConflictResolverViewModel;
                Attach(_viewModel);

                SyncResultBoxFromViewModel();
                RefreshDiffViewer();
                UpdateEditorShown();
                ScheduleMarkerUpdate();
                StartLoadIfNeeded();
            }
            catch (Exception ex)
            {
                LogViewError("DataContext change", ex);
            }
        }

        private void Attach(ConflictResolverViewModel viewModel)
        {
            if (viewModel == null)
                return;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.NavigateRequested += OnNavigateRequested;
        }

        private void Detach(ConflictResolverViewModel viewModel)
        {
            if (viewModel == null)
                return;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.NavigateRequested -= OnNavigateRequested;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshDiffViewer();
                StartLoadIfNeeded();
                if (_revealResultPending || _revealDiffPending)
                    ScheduleReveal();
            }
            catch (Exception ex)
            {
                LogViewError("Loaded", ex);
            }
        }

        // La scheda viene nascosta, spostata o chiusa: si salva il testo e si chiude il visore (verra'
        // ricreato al prossimo Loaded).
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitPendingEdit();
                CloseDiffViewer();
            }
            catch (Exception ex)
            {
                LogViewError("Unloaded", ex);
            }
        }

        // Scheda tornata visibile: il blocco rimasto da mostrare si mostra adesso.
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible && (_revealResultPending || _revealDiffPending))
                ScheduleReveal();
        }

        // Il caricamento lo avvia di solito chi crea il view model; se non l'ha fatto, lo avvia la vista
        // (LoadAsync e' idempotente e non lancia).
        private void StartLoadIfNeeded()
        {
            var viewModel = _viewModel;
            if (IsLoaded && viewModel != null && !viewModel.HasLoadStarted)
                _ = viewModel.LoadAsync();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, _viewModel))
                return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnViewModelPropertyChanged(sender, e)));
                return;
            }

            try
            {
                switch (e.PropertyName)
                {
                    case "ResultText":
                        if (!_committingEdit)
                            SyncResultBoxFromViewModel();
                        ScheduleMarkerUpdate();
                        break;
                    case "CurrentRegion":
                        ScheduleMarkerUpdate();
                        break;
                    case "ShowEditor":
                        RefreshDiffViewer();
                        UpdateEditorShown();
                        ScheduleMarkerUpdate();
                        break;
                    case "SourceText":
                    case "TargetText":
                        RefreshDiffViewer();
                        break;
                    case "CurrentConflictLocation":
                        // Il blocco corrente e' cambiato senza navigazione (cursore dentro un altro blocco
                        // del RESULT): il confronto lo segue, il RESULT resta com'e'.
                        if (!ReferenceEquals(_viewModel.CurrentConflictLocation, _revealedLocation))
                            RequestReveal(false, true);
                        break;
                    case null:
                    case "":
                        SyncResultBoxFromViewModel();
                        RefreshDiffViewer();
                        UpdateEditorShown();
                        ScheduleMarkerUpdate();
                        break;
                }
            }
            catch (Exception ex)
            {
                LogViewError("property " + e.PropertyName, ex);
            }
        }

        // ShowEditor viene notificata a ogni cambio di stato: il blocco corrente si mostra solo quando
        // l'editor passa da nascosto a visibile (fine caricamento, oppure vista nuova per un conflitto gia'
        // caricato: tornando su un file si ritrova il blocco su cui si era).
        private void UpdateEditorShown()
        {
            var shown = _viewModel != null && _viewModel.ShowEditor;
            if (shown && !_editorShown)
                RequestReveal(true, true);
            _editorShown = shown;
        }

        #endregion

        #region Result text box

        // View model -> TextBox. Il view model vince su eventuali modifiche non ancora passate.
        private void SyncResultBoxFromViewModel()
        {
            var text = _viewModel == null ? string.Empty : (_viewModel.ResultText ?? string.Empty);
            if (string.Equals(ResultTextBox.Text, text, StringComparison.Ordinal))
                return;

            _commitTimer.Stop();
            _pendingEdit = false;
            _updatingResultBox = true;
            try
            {
                ResultTextBox.Text = text;
            }
            finally
            {
                _updatingResultBox = false;
            }
        }

        private void ResultTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingResultBox)
                return;
            _pendingEdit = true;
            _commitTimer.Stop();
            _commitTimer.Start();
            // Le posizioni del blocco valgono per il testo del view model: il segno torna dopo il commit.
            UpdateCurrentBlockMarker();
        }

        private void CommitTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                CommitPendingEdit();
            }
            catch (Exception ex)
            {
                LogViewError("commit edit", ex);
            }
        }

        private void ResultTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            try
            {
                CommitPendingEdit();
            }
            catch (Exception ex)
            {
                LogViewError("lost focus", ex);
            }
        }

        // TextBox -> view model (ricalcola regioni e contatori).
        private void CommitPendingEdit()
        {
            _commitTimer.Stop();
            if (!_pendingEdit)
                return;
            _pendingEdit = false;

            var viewModel = _viewModel;
            if (viewModel == null)
                return;
            var text = ResultTextBox.Text ?? string.Empty;
            if (!string.Equals(viewModel.ResultText, text, StringComparison.Ordinal))
            {
                _committingEdit = true;
                try
                {
                    viewModel.ResultText = text;
                }
                finally
                {
                    _committingEdit = false;
                }
            }
            viewModel.SelectRegionAt(ResultTextBox.CaretIndex);
            ScheduleMarkerUpdate();
        }

        // Il cursore dentro un blocco lo rende il blocco corrente (solo se testo e view model coincidono).
        private void ResultTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_updatingResultBox || _pendingEdit || _viewModel == null || !ResultTextBox.IsKeyboardFocusWithin)
                return;
            try
            {
                _viewModel.SelectRegionAt(ResultTextBox.CaretIndex);
            }
            catch (Exception ex)
            {
                LogViewError("selection", ex);
            }
        }

        #endregion

        #region Reveal the current conflict

        // Previous/Next, una scelta sul blocco o il caricamento: RESULT e confronto sul blocco corrente.
        private void OnNavigateRequested(object sender, ConflictRegion region)
        {
            if (!ReferenceEquals(sender, _viewModel))
                return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnNavigateRequested(sender, region)));
                return;
            }
            RequestReveal(true, true);
        }

        private void RequestReveal(bool result, bool diff)
        {
            _revealResultPending |= result;
            _revealDiffPending |= diff;
            _revealAttempts = 0;
            ScheduleReveal();
        }

        private void ClearReveal()
        {
            _revealResultPending = false;
            _revealDiffPending = false;
            _revealAttempts = 0;
            _revealedLocation = null;
            _editorShown = false;
        }

        private void ScheduleReveal()
        {
            if (_revealScheduled)
                return;
            _revealScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(RunPendingReveal));
        }

        private void RunPendingReveal()
        {
            _revealScheduled = false;
            try
            {
                var viewModel = _viewModel;
                if (viewModel == null)
                {
                    ClearReveal();
                    return;
                }
                // Editor non ancora visibile (caricamento in corso) o scheda nascosta: resta in sospeso,
                // riparte da ShowEditor, Loaded o IsVisibleChanged.
                if (!viewModel.ShowEditor || !IsLoaded || !IsVisible)
                    return;

                if (_revealResultPending && TryRevealResult(viewModel))
                    _revealResultPending = false;
                if (_revealDiffPending && TryRevealDiff(viewModel))
                    _revealDiffPending = false;

                if (!_revealResultPending && !_revealDiffPending)
                {
                    _revealAttempts = 0;
                    return;
                }
                // Layout non ancora pronto: si riprova qualche volta, poi si lascia perdere (solo comodita').
                if (++_revealAttempts <= MaxRevealAttempts)
                {
                    ScheduleReveal();
                    return;
                }
                _revealResultPending = false;
                _revealDiffPending = false;
                _revealAttempts = 0;
            }
            catch (Exception ex)
            {
                _revealResultPending = false;
                _revealDiffPending = false;
                LogViewError("reveal", ex);
            }
        }

        // True = fatto (o niente da fare); false = riprovare a layout pronto.
        private bool TryRevealResult(ConflictResolverViewModel viewModel)
        {
            var region = viewModel.CurrentRegion;
            // L'utente sta scrivendo: la selezione non si sposta sotto le sue dita.
            if (region == null || _pendingEdit)
                return true;
            var text = ResultTextBox.Text ?? string.Empty;
            // Le regioni valgono per il testo del view model: se la TextBox ha altro, niente da fare.
            if (!string.Equals(text, viewModel.ResultText ?? string.Empty, StringComparison.Ordinal))
                return true;
            if (region.Start < 0 || region.Start > text.Length)
                return true;

            var length = Math.Max(0, Math.Min(region.Length, text.Length - region.Start));
            bool done;
            _updatingResultBox = true;
            try
            {
                done = TrySelectAndReveal(ResultTextBox, region.Start, length, region.StartLine, 0.25);
            }
            finally
            {
                _updatingResultBox = false;
            }
            UpdateCurrentBlockMarker();
            return done;
        }

        private void OnResultScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateCurrentBlockMarker();
        }

        // A layout aggiornato (dopo il cambio di testo o di blocco le righe non sono ancora misurate).
        private void ScheduleMarkerUpdate()
        {
            if (_markerUpdateScheduled)
                return;
            _markerUpdateScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _markerUpdateScheduled = false;
                UpdateCurrentBlockMarker();
            }));
        }

        // Fascia (colore di selezione del tema, trasparente) + barretta arancio sulle righe del blocco
        // corrente, limitate alla parte visibile del RESULT. Nascoste se il blocco e' fuori vista, se
        // non c'e' o se la TextBox ha modifiche non ancora passate al view model.
        private void UpdateCurrentBlockMarker()
        {
            Rect block;
            bool visible;
            try
            {
                visible = TryGetCurrentBlockRect(out block);
            }
            catch (Exception ex)
            {
                visible = false;
                block = Rect.Empty;
                LogViewError("current block marker", ex);
            }

            if (!visible)
            {
                CurrentBlockBand.Visibility = Visibility.Collapsed;
                CurrentBlockMarker.Visibility = Visibility.Collapsed;
                return;
            }

            Canvas.SetLeft(CurrentBlockBand, block.Left);
            Canvas.SetTop(CurrentBlockBand, block.Top);
            CurrentBlockBand.Width = block.Width;
            CurrentBlockBand.Height = block.Height;
            CurrentBlockBand.Visibility = Visibility.Visible;

            Canvas.SetLeft(CurrentBlockMarker, block.Left);
            Canvas.SetTop(CurrentBlockMarker, block.Top);
            CurrentBlockMarker.Height = block.Height;
            CurrentBlockMarker.Visibility = Visibility.Visible;
        }

        private bool TryGetCurrentBlockRect(out Rect block)
        {
            block = Rect.Empty;
            var viewModel = _viewModel;
            if (viewModel == null || !viewModel.ShowEditor || _pendingEdit)
                return false;
            var region = viewModel.CurrentRegion;
            if (region == null)
                return false;

            var box = ResultTextBox;
            var text = box.Text ?? string.Empty;
            if (!box.IsVisible || region.Start < 0 || region.Start >= text.Length
                || !string.Equals(text, viewModel.ResultText ?? string.Empty, StringComparison.Ordinal))
                return false;
            if (box.LineCount <= 0)
                return false;

            // Area del testo (senza bordo e scrollbar) in coordinate della TextBox, che coincidono con quelle
            // del Canvas sovrapposto (stessa cella della griglia).
            var viewport = GetTextViewport(box);
            if (viewport.IsEmpty || viewport.Height <= 0)
                return false;

            // Righe tutte alte uguali (NoWrap, un solo font): posizione dalla prima riga visibile.
            var first = box.GetFirstVisibleLineIndex();
            if (first < 0)
                return false;
            var firstRect = box.GetRectFromCharacterIndex(box.GetCharacterIndexFromLineIndex(first));
            if (firstRect.IsEmpty || !(firstRect.Height > 0))
                return false;
            var lineHeight = firstRect.Height;

            var startLine = box.GetLineIndexFromCharacterIndex(region.Start);
            if (startLine < 0)
                startLine = region.StartLine;
            var lastChar = Math.Max(region.Start, Math.Min(text.Length, region.Start + region.Length) - 1);
            var endLine = box.GetLineIndexFromCharacterIndex(lastChar);
            if (endLine < startLine)
                endLine = startLine;

            var top = Math.Max(viewport.Top, firstRect.Top + (startLine - first) * lineHeight);
            var bottom = Math.Min(viewport.Bottom, firstRect.Top + (endLine - first + 1) * lineHeight);
            if (bottom <= top)
                return false;
            // Dal bordo interno (prima del padding): la barretta non tocca il primo carattere.
            var left = Math.Max(box.BorderThickness.Left, viewport.Left - box.Padding.Left);
            block = new Rect(left, top, Math.Max(0, viewport.Right - left), bottom - top);
            return true;
        }

        private static Rect GetTextViewport(TextBox box)
        {
            FrameworkElement area = null;
            var host = box.Template == null ? null : box.Template.FindName("PART_ContentHost", box) as ScrollViewer;
            if (host != null)
                area = (host.Template == null ? null : host.Template.FindName("PART_ScrollContentPresenter", host) as FrameworkElement) ?? host;
            if (area == null || !area.IsVisible)
                return new Rect(0, 0, box.ActualWidth, box.ActualHeight);
            var origin = area.TranslatePoint(new Point(0, 0), box);
            return new Rect(origin, new Size(area.ActualWidth, area.ActualHeight));
        }

        private bool TryRevealDiff(ConflictResolverViewModel viewModel)
        {
            var location = viewModel.CurrentConflictLocation;
            // Corrispondenza persa (testo cambiato a mano) o nessun blocco: il confronto resta dov'e'.
            if (location == null)
                return true;

            var viewer = _diffViewer;
            if (viewer != null)
            {
                // Il visore aspetta da solo il suo primo layout; il lato sinistro segue il destro. Offset e
                // non numero di riga: l'editor di VS conta le righe anche su U+0085, U+2028 e U+2029.
                viewer.RevealTargetOffset(ThreeWayMerge.GetLineStartOffset(viewModel.TargetText, location.TargetStartLine));
                _revealedLocation = location;
                return true;
            }

            if (FallbackDiffGrid.Visibility != Visibility.Visible)
                return true;

            // Ripiego a TextBox: ciascun lato sul suo blocco, con le righe del blocco selezionate.
            var source = TrySelectLines(FallbackSourceBox, location.SourceStartLine, location.SourceLineCount);
            var target = TrySelectLines(FallbackTargetBox, location.TargetStartLine, location.TargetLineCount);
            if (source && target)
                _revealedLocation = location;
            return source && target;
        }

        private static bool TrySelectLines(TextBox box, int startLine, int lineCount)
        {
            var text = box.Text ?? string.Empty;
            var start = ThreeWayMerge.GetLineStartOffset(text, startLine);
            var end = ThreeWayMerge.GetLineStartOffset(text, startLine + Math.Max(0, lineCount));
            return TrySelectAndReveal(box, start, Math.Max(0, end - start), startLine, 1.0 / 3);
        }

        // Seleziona [start, start + length) e porta la riga di start a circa "fraction" dell'altezza
        // visibile, con la prima riga visibile intera (lo scorrimento si ferma sul bordo di una riga).
        // False se la TextBox non ha ancora righe misurate (si riprova piu' tardi).
        private static bool TrySelectAndReveal(TextBox box, int start, int length, int lineHint, double fraction)
        {
            if (!box.IsVisible || box.ActualHeight <= 0)
                return false;

            box.Select(start, length);

            // LineCount e' -1 finche' la TextBox non e' misurata (es. editor appena reso visibile).
            if (box.LineCount <= 0)
                box.UpdateLayout();
            var lineCount = box.LineCount;
            if (lineCount <= 0)
                return false;

            var line = box.GetLineIndexFromCharacterIndex(start);
            if (line < 0)
                line = lineHint;
            line = Math.Max(0, Math.Min(line, lineCount - 1));
            box.ScrollToLine(line);

            box.UpdateLayout();
            var rect = box.GetRectFromCharacterIndex(start);
            var lineHeight = rect.Height;
            if (rect.IsEmpty || !(lineHeight > 0) || box.ViewportHeight <= 0)
                return false;

            // Righe tutte alte uguali (NoWrap, un solo font). firstLineOffset: lo scorrimento che mette la
            // riga 0 esattamente sul bordo superiore dell'area del testo; ogni riga intera sta lineHeight piu'
            // in basso.
            var viewportTop = GetTextViewport(box).Top;
            var firstLineOffset = box.VerticalOffset + rect.Top - viewportTop - line * lineHeight;
            var linesAbove = (int)Math.Floor(box.ViewportHeight * fraction / lineHeight);
            var offset = firstLineOffset + Math.Max(0, line - linesAbove) * lineHeight;
            // In fondo al testo lo scorrimento massimo non cade su un bordo di riga: si resta sull'ultimo
            // bordo che ci sta (in basso resta mezza riga vuota, in alto nessuna riga tagliata).
            var maxOffset = Math.Max(0, box.ExtentHeight - box.ViewportHeight);
            if (offset > maxOffset)
                offset = firstLineOffset + Math.Floor((maxOffset - firstLineOffset) / lineHeight) * lineHeight;
            box.ScrollToVerticalOffset(Math.Max(0, offset));
            box.ScrollToHorizontalOffset(0);
            return true;
        }

        #endregion

        #region Diff viewer

        private void RefreshDiffViewer()
        {
            var viewModel = _viewModel;
            var wanted = IsLoaded && viewModel != null && viewModel.ShowEditor;
            if (!wanted)
            {
                CloseDiffViewer();
                return;
            }
            if (ReferenceEquals(_diffOwner, viewModel))
                return;

            CloseDiffViewer();
            _diffOwner = viewModel;

            // Visore nuovo (anche dopo un Unloaded/Loaded): va riportato sul blocco corrente. Se il blocco
            // non si sa, il visore va da solo alla prima differenza.
            _revealedLocation = null;
            var location = viewModel.CurrentConflictLocation;

            string error;
            var viewer = EmbeddedTextEditors.TryCreateSideBySideDiff(viewModel.SourceText, viewModel.TargetText,
                viewModel.FileExtension, location == null, out error);
            if (viewer != null && viewer.VisualElement != null)
            {
                _diffViewer = viewer;
                DiffViewerHost.Child = viewer.VisualElement;
                DiffViewerHost.Visibility = Visibility.Visible;
                FallbackDiffGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (viewer != null)
                    viewer.Dispose();
                var logger = viewModel.Logger;
                if (logger != null)
                    logger.Info("Conflict resolver: VS difference viewer not available, using plain text boxes (" + error + ")");
                FallbackSourceBox.Text = viewModel.SourceText ?? string.Empty;
                FallbackTargetBox.Text = viewModel.TargetText ?? string.Empty;
                DiffViewerHost.Visibility = Visibility.Collapsed;
                FallbackDiffGrid.Visibility = Visibility.Visible;
            }

            if (location != null)
                RequestReveal(false, true);
        }

        private void CloseDiffViewer()
        {
            _diffOwner = null;
            _revealedLocation = null;
            DiffViewerHost.Child = null;
            var viewer = _diffViewer;
            _diffViewer = null;
            if (viewer != null)
                viewer.Dispose();
            FallbackSourceBox.Text = string.Empty;
            FallbackTargetBox.Text = string.Empty;
            FallbackDiffGrid.Visibility = Visibility.Collapsed;
            DiffViewerHost.Visibility = Visibility.Visible;
        }

        #endregion

        // Le proporzioni scelte valgono anche per i conflitti successivi.
        private void BodySplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _savedDiffHeight = DiffRow.Height;
            _savedResultHeight = ResultRow.Height;
        }

        private void LogViewError(string what, Exception ex)
        {
            var logger = _viewModel == null ? null : _viewModel.Logger;
            if (logger != null)
                logger.Error("Conflict resolver view: " + what, ex);
        }
    }
}
