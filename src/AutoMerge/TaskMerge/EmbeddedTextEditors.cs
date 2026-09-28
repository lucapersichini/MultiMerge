using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace AutoMerge
{
    // Ospita il visore differenze dell'editor di VS (lo stesso del confronto file) dentro la nostra
    // scheda: due buffer in memoria, affiancati, entrambi in sola lettura. Tutto sul thread UI.
    //
    // Il risultato NON usa un editor di VS: la digitazione nei text view ospitati in una tool window
    // richiede il routing dei comandi OLE (IOleCommandTarget); la TextBox WPF e' affidabile.
    internal static class EmbeddedTextEditors
    {
        private const string PlainTextContentType = "text";

        // Opzioni del visore, per NOME. Verificate per riflessione sugli assembly dell'editor di VS 18
        // (Common7\IDE\CommonExtensions\Microsoft\Editor): Microsoft.VisualStudio.Text.UI.dll,
        // classi DefaultTextViewHostOptions (TextViewHost/*, EditingState/*, EnableStickyScroll) e
        // DifferenceViewerOptions (Diff/View/*). Per nome e non con i campi tipizzati perche' alcune
        // esistono solo in VS 18 e non nel pacchetto NuGet 17.14 con cui si compila
        // (EditingState/LineColCharMargin, EditingState/SelectionInfoMargin, EditingState/EncodingMargin,
        // TextViewHost/IncludeBottomMargin); le altre ci sono in entrambi. Un nome che l'editor non
        // conosce (versioni piu' vecchie) fa lanciare SetOptionValue: si ignora quella sola chiave.
        //
        // Barra inferiore di ogni lato: resta solo la scrollbar orizzontale (TextViewHost/IncludeBottomMargin
        // NON si spegne: toglierebbe anche quella).
        private static readonly KeyValuePair<string, object>[] ViewHostOptions =
        {
            // "No issues found" (indicatore di salute del file).
            Option("TextViewHost/FileHealthIndicator", false),
            // "Ln: 56, Ch: 1" (VS 18) e "Ln 56 Col 1" (VS 17).
            Option("EditingState/LineColCharMargin", false),
            Option("EditingState/RowColMargin", false),
            // Selezioni multiple / informazioni sulla selezione.
            Option("EditingState/SelectionStateMargin", false),
            Option("EditingState/SelectionInfoMargin", false),
            // INS / OVR.
            Option("EditingState/InsertModeMargin", false),
            // TABS / SPACES.
            Option("EditingState/IndentationCharacterMargin", false),
            // CRLF / LF / MIXED.
            Option("EditingState/LineEndingMargin", false),
            // Encoding del documento.
            Option("EditingState/EncodingMargin", false),
            // Contenitore dei margini "editing state" (nome dell'opzione cosi' com'e' nell'editor).
            Option("EditingState/EditingStateEndingMargin", false),
            // Zoom (100%).
            Option("TextViewHost/ZoomControl", false),

            // Margini a sinistra: numeri di riga si', il resto no.
            Option("TextViewHost/LineNumberMargin", true),
            Option("TextViewHost/GlyphMargin", false),
            Option("TextViewHost/SuggestionMargin", false),
            Option("TextViewHost/OutliningMargin", false),
            Option("TextViewHost/ChangeTracking", false),
            // Scrollbar verticale (con la mappa delle differenze) sempre visibile.
            Option("TextViewHost/VerticalScrollBar", true),
            // Le righe "appiccicate" in alto ruberebbero spazio a un riquadro gia' basso.
            Option("EnableStickyScroll", false),

            // I due lati scorrono insieme.
            Option("Diff/View/SynchronizeSideBySideViews", true)
        };

        private const string ScrollToFirstDiffOption = "Diff/View/ScrollToFirstDiff";

        private static KeyValuePair<string, object> Option(string name, object value)
        {
            return new KeyValuePair<string, object>(name, value);
        }

        // Visore affiancato (sinistra = leftText, destra = rightText) oppure null con error valorizzato:
        // in quel caso la vista ripiega su due TextBox in sola lettura. scrollToFirstDiff: false quando
        // chi lo ospita porta lui il visore sul blocco corrente (RevealTargetOffset).
        public static EmbeddedDiffViewer TryCreateSideBySideDiff(string leftText, string rightText, string fileExtension,
            bool scrollToFirstDiff, out string error)
        {
            error = null;
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
                if (componentModel == null)
                {
                    error = "SComponentModel service not available";
                    return null;
                }

                var bufferFactory = componentModel.GetService<ITextBufferFactoryService>();
                var contentTypes = componentModel.GetService<IContentTypeRegistryService>();
                var extensions = componentModel.GetService<IFileExtensionRegistryService>();
                var differenceBufferFactory = componentModel.GetService<IDifferenceBufferFactoryService>();
                var differenceViewerFactory = componentModel.GetService<IWpfDifferenceViewerFactoryService>();
                var optionsFactory = componentModel.GetService<IEditorOptionsFactoryService>();
                if (bufferFactory == null || contentTypes == null || differenceBufferFactory == null || differenceViewerFactory == null)
                {
                    error = "editor services not available";
                    return null;
                }

                var plainText = contentTypes.GetContentType(PlainTextContentType);
                var contentType = ResolveContentType(contentTypes, extensions, fileExtension, plainText);
                try
                {
                    return Create(bufferFactory, differenceBufferFactory, differenceViewerFactory, optionsFactory,
                        leftText, rightText, contentType, scrollToFirstDiff);
                }
                catch (Exception) when (contentType != plainText && plainText != null)
                {
                    // Qualche content type (servizi di linguaggio) potrebbe non gradire buffer senza file:
                    // si riprova in testo semplice prima di rinunciare al visore.
                    return Create(bufferFactory, differenceBufferFactory, differenceViewerFactory, optionsFactory,
                        leftText, rightText, plainText, scrollToFirstDiff);
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static EmbeddedDiffViewer Create(ITextBufferFactoryService bufferFactory,
            IDifferenceBufferFactoryService differenceBufferFactory, IWpfDifferenceViewerFactoryService differenceViewerFactory,
            IEditorOptionsFactoryService optionsFactory, string leftText, string rightText, IContentType contentType,
            bool scrollToFirstDiff)
        {
            var left = bufferFactory.CreateTextBuffer(leftText ?? string.Empty, contentType);
            var right = bufferFactory.CreateTextBuffer(rightText ?? string.Empty, contentType);

            // disableEditing + proiezioni in sola lettura di entrambi i buffer: nessuno puo' modificarli.
            var options = new StringDifferenceOptions(StringDifferenceTypes.Line | StringDifferenceTypes.Word, 0, false);
            var differenceBuffer = differenceBufferFactory.CreateDifferenceBuffer(left, right, options,
                disableEditing: true, wrapLeftBuffer: true, wrapRightBuffer: true);

            // Opzioni "genitore" del visore, impostate PRIMA di creare le viste: i margini nascono gia'
            // spenti (niente barra inferiore che compare e poi sparisce) e non si crea la vista inline.
            IEditorOptions parentOptions = null;
            if (optionsFactory != null)
            {
                try
                {
                    parentOptions = optionsFactory.CreateOptions();
                    TrySet(parentOptions, DifferenceViewerOptions.ViewModeId, DifferenceViewMode.SideBySide);
                    ApplyViewOptions(parentOptions, scrollToFirstDiff);
                }
                catch (Exception)
                {
                    parentOptions = null;
                }
            }

            IWpfDifferenceViewer viewer = null;
            try
            {
                viewer = parentOptions != null
                    ? differenceViewerFactory.CreateDifferenceView(differenceBuffer, parentOptions)
                    : differenceViewerFactory.CreateDifferenceView(differenceBuffer);
                viewer.ViewMode = DifferenceViewMode.SideBySide;
                // Anche sulle viste: qualche listener di creazione puo' averle impostate in locale.
                ConfigureView(viewer.LeftView, scrollToFirstDiff);
                ConfigureView(viewer.RightView, scrollToFirstDiff);
                // InlineView crea la vista inline se non esiste (IDifferenceViewer2.InlineViewExists): in
                // modalita' affiancata non si vede, quindi la si configura solo se c'e' gia'.
                var viewer2 = viewer as IDifferenceViewer2;
                if (viewer2 != null && viewer2.InlineViewExists)
                    ConfigureView(viewer.InlineView, scrollToFirstDiff);
                return new EmbeddedDiffViewer(viewer, right);
            }
            catch (Exception)
            {
                if (viewer != null)
                    EmbeddedDiffViewer.CloseQuietly(viewer);
                throw;
            }
        }

        // Sola lettura e margini essenziali (vedi ViewHostOptions).
        private static void ConfigureView(ITextView view, bool scrollToFirstDiff)
        {
            if (view == null)
                return;
            var options = view.Options;
            TrySet(options, DefaultTextViewOptions.ViewProhibitUserInputId, true);
            ApplyViewOptions(options, scrollToFirstDiff);
        }

        private static void ApplyViewOptions(IEditorOptions options, bool scrollToFirstDiff)
        {
            foreach (var option in ViewHostOptions)
                TrySet(options, option.Key, option.Value);
            TrySet(options, ScrollToFirstDiffOption, scrollToFirstDiff);
        }

        private static void TrySet<T>(IEditorOptions options, EditorOptionKey<T> key, T value)
        {
            try
            {
                options.SetOptionValue(key, value);
            }
            catch (Exception)
            {
                // Opzione non disponibile in questa versione dell'editor: solo estetica.
            }
        }

        private static void TrySet(IEditorOptions options, string optionId, object value)
        {
            try
            {
                options.SetOptionValue(optionId, value);
            }
            catch (Exception)
            {
                // Opzione non definita in questa versione dell'editor: solo estetica.
            }
        }

        // Content type dall'estensione del file (senza punto), altrimenti "text".
        private static IContentType ResolveContentType(IContentTypeRegistryService contentTypes,
            IFileExtensionRegistryService extensions, string fileExtension, IContentType plainText)
        {
            var extension = (fileExtension ?? string.Empty).Trim().TrimStart('.');
            if (extension.Length == 0 || extensions == null)
                return plainText;
            try
            {
                var contentType = extensions.GetContentTypeForExtension(extension);
                if (contentType == null || contentType == contentTypes.UnknownContentType)
                    return plainText;
                return contentType;
            }
            catch (Exception)
            {
                return plainText;
            }
        }
    }

    // Un visore differenze creato da EmbeddedTextEditors: VisualElement va messo nell'albero visuale,
    // Dispose lo chiude (da chiamare sul thread UI, dopo averlo tolto dall'albero).
    internal sealed class EmbeddedDiffViewer : IDisposable
    {
        private IWpfDifferenceViewer _viewer;
        // Buffer creato dal testo del TARGET (quello che la vista destra mostra, eventualmente attraverso
        // la proiezione del visore): gli offset calcolati sul testo valgono qui.
        private ITextBuffer _targetBuffer;
        // Offset (in caratteri del testo del TARGET) da portare in vista appena la vista ha un layout;
        // -1 = nessuno.
        private int _pendingTargetOffset = -1;
        private IWpfTextView _layoutSource;
        private bool _revealQueued;

        internal EmbeddedDiffViewer(IWpfDifferenceViewer viewer, ITextBuffer targetBuffer)
        {
            _viewer = viewer;
            _targetBuffer = targetBuffer;
            VisualElement = viewer.VisualElement;
        }

        public FrameworkElement VisualElement { get; private set; }

        // Porta la riga che contiene targetOffset (offset nel testo del TARGET da cui e' nato il visore) a
        // circa un terzo dell'altezza del riquadro; il SOURCE segue da solo (viste affiancate
        // sincronizzate). Un offset e non un numero di riga: l'editor di VS va a capo anche su U+0085,
        // U+2028 e U+2029, ThreeWayMerge.SplitLines no. Se la vista non ha ancora un layout (appena
        // creata), lo fa al primo LayoutChanged.
        public void RevealTargetOffset(int targetOffset)
        {
            if (_viewer == null || targetOffset < 0)
                return;
            _pendingTargetOffset = targetOffset;
            if (!TryRevealPending())
                WaitForLayout();
        }

        private bool TryRevealPending()
        {
            var viewer = _viewer;
            if (_pendingTargetOffset < 0 || viewer == null || viewer.IsClosed)
                return true;
            try
            {
                var view = viewer.RightView;
                if (view == null || view.IsClosed)
                {
                    _pendingTargetOffset = -1;
                    return true;
                }
                var height = view.ViewportHeight;
                if (view.InLayout || !(height > 0) || !view.VisualElement.IsVisible)
                    return false;

                var point = MapToView(view, _pendingTargetOffset);
                _pendingTargetOffset = -1;
                // Distanza dal bordo in righe intere: la prima riga visibile non resta tagliata a meta'.
                var lineHeight = view.LineHeight;
                var distance = lineHeight > 0 ? Math.Floor(height / 3 / lineHeight) * lineHeight : height / 3;
                view.DisplayTextLineContainingBufferPosition(point, distance, ViewRelativePosition.Top);
                return true;
            }
            catch (Exception)
            {
                // Solo comodita' visiva: il visore resta dove si trova.
                _pendingTargetOffset = -1;
                return true;
            }
        }

        // Offset nel buffer del TARGET -> punto nello snapshot della vista destra (la proiezione del
        // visore conserva gli offset; il grafo dei buffer lo garantisce anche se non fosse cosi').
        private SnapshotPoint MapToView(IWpfTextView view, int targetOffset)
        {
            var viewSnapshot = view.TextSnapshot;
            var buffer = _targetBuffer;
            if (buffer != null && view.BufferGraph != null)
            {
                var targetSnapshot = buffer.CurrentSnapshot;
                var position = new SnapshotPoint(targetSnapshot, Math.Min(targetOffset, targetSnapshot.Length));
                var mapped = view.BufferGraph.MapUpToSnapshot(position, PointTrackingMode.Positive, PositionAffinity.Successor, viewSnapshot);
                if (mapped.HasValue)
                    return mapped.Value;
            }
            return new SnapshotPoint(viewSnapshot, Math.Min(targetOffset, viewSnapshot.Length));
        }

        private void WaitForLayout()
        {
            var viewer = _viewer;
            if (viewer == null || _layoutSource != null)
                return;
            var view = viewer.RightView;
            if (view == null || view.IsClosed)
                return;
            _layoutSource = view;
            view.LayoutChanged += OnLayoutChanged;
        }

        private void StopWaitingForLayout()
        {
            var view = _layoutSource;
            _layoutSource = null;
            if (view != null)
                view.LayoutChanged -= OnLayoutChanged;
        }

        // Dentro LayoutChanged il visore sta ancora finendo il layout: si scorre subito dopo.
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_revealQueued)
                return;
            _revealQueued = true;
            var element = VisualElement;
            var dispatcher = element != null ? element.Dispatcher : Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _revealQueued = false;
                if (TryRevealPending())
                    StopWaitingForLayout();
            }));
        }

        public void Dispose()
        {
            StopWaitingForLayout();
            _pendingTargetOffset = -1;
            _targetBuffer = null;
            var viewer = _viewer;
            _viewer = null;
            VisualElement = null;
            if (viewer != null)
                CloseQuietly(viewer);
        }

        internal static void CloseQuietly(IWpfDifferenceViewer viewer)
        {
            try
            {
                if (!viewer.IsClosed)
                    viewer.Close();
            }
            catch (Exception)
            {
                // Chiusura best effort: il visore non e' piu' mostrato comunque.
            }
        }
    }
}
