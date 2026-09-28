// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiMerge.Prism;
using MultiMerge.Prism.Command;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    // Risoluzione di UN conflitto TFVC dentro la scheda "Merge from Task", senza passare dalla pagina
    // Resolve Conflicts di VS.
    //
    // Conflitto di contenuto testuale: si scaricano base, source ("theirs") e target ("yours": il file
    // locale del workspace, che contiene anche le modifiche in sospeso dei batch precedenti), si fondono
    // con ThreeWayMerge e il risultato con i marker diventa ResultText. L'utente sceglie un lato per
    // ogni blocco (o modifica il testo a mano); con zero marker, Accept result scrive il testo con
    // l'encoding del target e lo passa a TFVC come AcceptMerge con MergedFileName.
    // Altri conflitti (binari, cancellazioni, nomi, file troppo grandi, encoding sconosciuti): solo le
    // azioni sul file intero (AcceptTheirs / AcceptYours).
    //
    // Threading: costruttore e comandi sul thread UI; download, lettura, fusione, scrittura e
    // ResolveConflict in Task.Run; proprieta' ed eventi aggiornati solo dopo l'await (thread UI).
    // Nessuna eccezione esce dai comandi: gli errori finiscono in ErrorMessage e nel logger.
    public sealed class ConflictResolverViewModel : BindableBase, IDisposable
    {
        public const string SourceLabel = "SOURCE (incoming)";
        public const string TargetLabel = "TARGET (current)";

        // Costanti di TFVC (Microsoft.TeamFoundation.VersionControl.Common.RepositoryConstants, verificate
        // per riflessione): EncodingBinary = -1, EncodingUnchanged = -2.
        private const int EncodingBinary = -1;
        private const int EncodingUnchanged = -2;

        // Oltre questa dimensione la TextBox WPF del risultato (tutto sul thread UI: assegnazione del testo,
        // copie a ogni commit, layout) rende la scheda a scatti: solo azioni sul file intero.
        private const long MaxEditableFileBytes = 2L * 1024 * 1024;

        private const string TempRootFolderName = "MultiMergeTask";

        private static readonly IReadOnlyList<ConflictRegion> NoRegions = new ConflictRegion[0];
        private static int _staleTempCleanupStarted;

        private readonly Workspace _workspace;
        private readonly ILogger _logger;
        private readonly Func<bool> _canAct;
        private readonly Func<ConflictResolverViewModel, string, Task> _onResolved;
        private readonly string _tempFolder;
        private readonly string _baseDescription;

        private Task _loadTask;
        private bool _loadCompleted;
        private bool _loadFailed;
        private bool _disposed;
        private bool _regionScanFailed;
        private IReadOnlyList<ConflictRegion> _regions = NoRegions;
        // Lunghezza dei marker scritti dalla fusione (ThreeWayMergeResult.MarkerSize): le regioni si
        // cercano solo con quella, cosi' le righe "=======" del file non spezzano i blocchi.
        private int _markerSize = ThreeWayMerge.DefaultMarkerSize;
        // Regioni aperte -> blocchi della fusione originale (righe nel source e nel target), per portare
        // il confronto SOURCE | TARGET sul blocco corrente. Null finche' il file non e' caricato.
        private OpenConflictMap _conflictMap;

        // Come si scrive il risultato: encoding e BOM del target (o del source se il code page del target
        // non era noto); vedi LoadContent.
        private Encoding _resultEncoding;
        private byte[] _resultBom = new byte[0];
        // File tutto LF: gli a-capo digitati nella TextBox (CRLF) tornano LF prima di scrivere.
        private bool _normalizeTypedCrLf;

        // Fusione da usare al posto di ThreeWayMerge.Merge(base, source, target) (null = quella
        // normale): la scheda la imposta per i file con righe protette dalle regole di merge, cosi'
        // l'editor parte dal risultato che tiene quelle righe del target. Vedi UseMergeOverride.
        private Func<string, string, string, ThreeWayMergeResult> _mergeOverride;
        private string _mergeOverrideNote;

        public ConflictResolverViewModel(Conflict conflict, Workspace workspace, ILogger logger, Func<bool> canAct,
            Func<ConflictResolverViewModel, string, Task> onResolved)
        {
            if (conflict == null)
                throw new ArgumentNullException("conflict");
            if (workspace == null)
                throw new ArgumentNullException("workspace");

            Conflict = conflict;
            _workspace = workspace;
            _logger = logger;
            _canAct = canAct;
            _onResolved = onResolved;

            Path = conflict.YourServerItem ?? conflict.TheirServerItem ?? conflict.TargetLocalItem ?? conflict.FileName ?? string.Empty;
            FileName = GetLastSegment(Path);
            FileExtension = GetExtension(FileName);
            _baseDescription = DescribeConflict(conflict);
            _conflictDescription = _baseDescription;
            TakeSourceFileText = DescribeTakeSource(conflict);
            KeepTargetFileText = DescribeKeepTarget(conflict);
            _tempFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), TempRootFolderName, Guid.NewGuid().ToString("N"));

            PreviousConflictCommand = new DelegateCommand(() => Navigate(-1), CanNavigate);
            NextConflictCommand = new DelegateCommand(() => Navigate(1), CanNavigate);
            TakeSourceBlockCommand = new DelegateCommand(() => TakeBlock(ConflictChoice.TakeSource), CanTakeBlock);
            TakeTargetBlockCommand = new DelegateCommand(() => TakeBlock(ConflictChoice.TakeTarget), CanTakeBlock);
            TakeBothBlockCommand = new DelegateCommand(() => TakeBlock(ConflictChoice.TakeBoth), CanTakeBlock);
            AcceptResultCommand = DelegateCommand.FromAsyncHandler(AcceptResultAsync, CanAcceptResult);
            TakeSourceFileCommand = DelegateCommand.FromAsyncHandler(() => ResolveWholeFileAsync(Resolution.AcceptTheirs), CanActOnFile);
            KeepTargetFileCommand = DelegateCommand.FromAsyncHandler(() => ResolveWholeFileAsync(Resolution.AcceptYours), CanActOnFile);
            RetryLoadCommand = DelegateCommand.FromAsyncHandler(RetryLoadAsync, CanRetryLoad);
        }

        // La vista seleziona e porta in vista la regione (dopo Previous/Next, dopo ogni scelta, al caricamento).
        public event EventHandler<ConflictRegion> NavigateRequested;

        #region Bindable properties

        public Conflict Conflict { get; private set; }

        // Server path mostrato (come nella lista conflitti).
        public string Path { get; private set; }

        // Solo il nome del file.
        public string FileName { get; private set; }

        // Estensione senza punto (per il content type del visore differenze); vuota se assente.
        public string FileExtension { get; private set; }

        // Etichette dei pulsanti sul file intero (cambiano per i conflitti di cancellazione).
        public string TakeSourceFileText { get; private set; }

        public string KeepTargetFileText { get; private set; }

        public bool IsLoading
        {
            get { return _isLoading; }
            private set
            {
                if (SetProperty(ref _isLoading, value))
                    RaiseDependentState();
            }
        }
        private bool _isLoading;

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    RaiseDependentState();
            }
        }
        private bool _isBusy;

        // True quando TFVC ha segnato il conflitto come risolto da questo view model.
        public bool IsResolved
        {
            get { return _isResolved; }
            private set
            {
                if (SetProperty(ref _isResolved, value))
                    RaiseDependentState();
            }
        }
        private bool _isResolved;

        // Conflitto di contenuto testuale fondibile: editor a 3 vie. False: solo azioni sul file intero.
        public bool IsContentConflict
        {
            get { return _isContentConflict; }
            private set
            {
                if (SetProperty(ref _isContentConflict, value))
                    RaiseDependentState();
            }
        }
        private bool _isContentConflict;

        // Cosa e' successo, in inglese (es. "Both branches changed this file").
        public string ConflictDescription
        {
            get { return _conflictDescription; }
            private set { SetProperty(ref _conflictDescription, value); }
        }
        private string _conflictDescription;

        // Avvertenza sulla fusione (es. base assente o confronto semplificato): la vista la mostra in una
        // striscia sotto l'intestazione. Null se non c'e'.
        public string ConflictNote
        {
            get { return _conflictNote; }
            private set
            {
                if (SetProperty(ref _conflictNote, value))
                    OnPropertyChanged("HasConflictNote");
            }
        }
        private string _conflictNote;

        public bool HasConflictNote
        {
            get { return !string.IsNullOrEmpty(ConflictNote); }
        }

        // Perche' il conflitto non si fonde riga per riga (card dei conflitti non di contenuto).
        public string ContentUnavailableReason
        {
            get { return _contentUnavailableReason; }
            private set { SetProperty(ref _contentUnavailableReason, value); }
        }
        private string _contentUnavailableReason;

        public string ErrorMessage
        {
            get { return _errorMessage; }
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                    RaiseDependentState();
            }
        }
        private string _errorMessage;

        // Versione in arrivo dal branch sorgente ("theirs").
        public string SourceText
        {
            get { return _sourceText; }
            private set { SetProperty(ref _sourceText, value); }
        }
        private string _sourceText;

        // Versione attuale nel target ("yours": il file locale del workspace).
        public string TargetText
        {
            get { return _targetText; }
            private set { SetProperty(ref _targetText, value); }
        }
        private string _targetText;

        public string BaseText
        {
            get { return _baseText; }
            private set { SetProperty(ref _baseText, value); }
        }
        private string _baseText;

        // Fonte di verita' del risultato. Ogni modifica (anche a mano) ricalcola regioni e contatori.
        public string ResultText
        {
            get { return _resultText; }
            set
            {
                if (_disposed || !IsContentConflict || IsResolved)
                    return;
                var text = value ?? string.Empty;
                if (string.Equals(text, _resultText, StringComparison.Ordinal))
                    return;
                SetResultTextCore(text, CurrentRegionIndex, false, false);
            }
        }
        private string _resultText;

        // Es. "utf-8 with BOM, CRLF": come verra' salvato il risultato.
        public string EncodingText
        {
            get { return _encodingText; }
            private set { SetProperty(ref _encodingText, value); }
        }
        private string _encodingText;

        // Blocchi in conflitto trovati dalla fusione (cresce se l'utente aggiunge marker a mano).
        public int TotalConflictBlocks
        {
            get { return _totalConflictBlocks; }
            private set
            {
                if (SetProperty(ref _totalConflictBlocks, value))
                    RaiseDependentState();
            }
        }
        private int _totalConflictBlocks;

        public int RemainingConflictBlocks
        {
            get { return _regions.Count; }
        }

        public int ResolvedConflictBlocks
        {
            get { return Math.Max(0, TotalConflictBlocks - RemainingConflictBlocks); }
        }

        public bool HasConflictBlocks
        {
            get { return IsContentConflict && TotalConflictBlocks > 0; }
        }

        public bool HasRemainingConflicts
        {
            get { return RemainingConflictBlocks > 0; }
        }

        // Es. "2 of 5 conflicts left" / "No conflicts left: accept the result".
        public string ConflictBlocksText
        {
            get
            {
                if (!IsContentConflict)
                    return null;
                if (IsResolved)
                    return "Resolved";
                var remaining = RemainingConflictBlocks;
                if (remaining == 0)
                    return "No conflicts left: accept the result";
                var total = Math.Max(TotalConflictBlocks, remaining);
                return string.Format(CultureInfo.InvariantCulture,
                    total == 1 ? "{0} of {1} conflict left" : "{0} of {1} conflicts left", remaining, total);
            }
        }

        // Indice 0-based della regione corrente fra quelle rimaste; -1 se non ce ne sono.
        public int CurrentRegionIndex
        {
            get { return _currentRegionIndex; }
            private set
            {
                if (SetProperty(ref _currentRegionIndex, value))
                    RaiseDependentState();
            }
        }
        private int _currentRegionIndex = -1;

        // Regione corrente nel risultato (null se non ce ne sono): la vista la seleziona e la porta in vista.
        public ConflictRegion CurrentRegion
        {
            get
            {
                var index = CurrentRegionIndex;
                return index >= 0 && index < _regions.Count ? _regions[index] : null;
            }
        }

        // Dove sta la regione corrente nel source e nel target (righe del blocco originale); null se non
        // si sa (regione modificata a mano che non si riconosce ne' per contenuto ne' per posizione).
        public ConflictLocation CurrentConflictLocation
        {
            get { return _conflictMap == null ? null : _conflictMap.Get(CurrentRegionIndex); }
        }

        // Contatore compatto dell'intestazione, es. "Conflict 3 of 8 · 5 left": il numero e' quello del
        // blocco nella fusione originale (non cambia risolvendo gli altri), "left" quelli ancora aperti.
        public string ConflictNavigatorText
        {
            get
            {
                if (!IsContentConflict)
                    return null;
                if (IsResolved)
                    return "Resolved";
                var remaining = RemainingConflictBlocks;
                if (remaining == 0)
                    return "No conflicts left";
                var total = Math.Max(TotalConflictBlocks, remaining);
                var location = CurrentConflictLocation;
                if (location != null && location.Index < total)
                    return string.Format(CultureInfo.InvariantCulture, "Conflict {0} of {1} · {2} left",
                        location.Index + 1, total, remaining);
                if (CurrentRegionIndex >= 0 && CurrentRegionIndex < remaining)
                    return string.Format(CultureInfo.InvariantCulture, "Open conflict {0} of {1}", CurrentRegionIndex + 1, remaining);
                return string.Format(CultureInfo.InvariantCulture,
                    remaining == 1 ? "{0} conflict left" : "{0} conflicts left", remaining);
            }
        }

        // Es. "Conflict 2 of 3" (fra quelli rimasti).
        public string CurrentConflictText
        {
            get
            {
                var remaining = RemainingConflictBlocks;
                if (remaining == 0)
                    return "All conflicts resolved";
                if (CurrentRegionIndex < 0 || CurrentRegionIndex >= remaining)
                    return string.Format(CultureInfo.InvariantCulture, "{0} conflict(s)", remaining);
                return string.Format(CultureInfo.InvariantCulture, "Conflict {0} of {1}", CurrentRegionIndex + 1, remaining);
            }
        }

        // Stato della vista: caricamento, editor a 3 vie, card per i conflitti non di contenuto.
        public bool ShowLoading
        {
            get { return !_disposed && (IsLoading || !_loadCompleted); }
        }

        public bool ShowEditor
        {
            get { return _loadCompleted && !IsLoading && IsContentConflict; }
        }

        public bool ShowFileCard
        {
            get { return _loadCompleted && !IsLoading && !IsContentConflict; }
        }

        public bool HasErrorMessage
        {
            get { return !string.IsNullOrEmpty(ErrorMessage); }
        }

        public bool ShowRetry
        {
            get { return _loadFailed && !IsLoading && !IsResolved; }
        }

        public bool IsResultReadOnly
        {
            get { return IsBusy || IsLoading || IsResolved || !IsContentConflict; }
        }

        // Riga di stato sotto l'editor.
        public string StatusText
        {
            get
            {
                if (IsResolved)
                    return "Resolved: TFVC marked this conflict as resolved.";
                if (IsBusy)
                    return "Saving the resolution in TFVC...";
                if (!IsContentConflict)
                    return null;
                if (_regionScanFailed)
                    return "The conflict markers could not be analysed: fix the text by hand.";
                if (RemainingConflictBlocks > 0)
                    return "Pick a side for each conflict (or edit the result by hand): Accept result unlocks when none is left.";
                return "Ready: Accept result saves this text in the target and marks the conflict as resolved.";
            }
        }

        public DelegateCommand PreviousConflictCommand { get; private set; }

        public DelegateCommand NextConflictCommand { get; private set; }

        public DelegateCommand TakeSourceBlockCommand { get; private set; }

        public DelegateCommand TakeTargetBlockCommand { get; private set; }

        public DelegateCommand TakeBothBlockCommand { get; private set; }

        // Abilitato solo con RemainingConflictBlocks == 0.
        public DelegateCommand AcceptResultCommand { get; private set; }

        // Resolution.AcceptTheirs.
        public DelegateCommand TakeSourceFileCommand { get; private set; }

        // Resolution.AcceptYours.
        public DelegateCommand KeepTargetFileCommand { get; private set; }

        // Ricarica le versioni dopo un errore di caricamento.
        public DelegateCommand RetryLoadCommand { get; private set; }

        // Per la vista (diagnostica del visore differenze).
        internal ILogger Logger
        {
            get { return _logger; }
        }

        // True se il caricamento non e' mai partito (la vista lo avvia se chi la ospita non l'ha fatto).
        internal bool HasLoadStarted
        {
            get { return _loadTask != null; }
        }

        // Workspace su cui il resolver risolve (chi lo ospita lo riusa solo sullo stesso workspace).
        internal Workspace Workspace
        {
            get { return _workspace; }
        }

        internal bool IsDisposed
        {
            get { return _disposed; }
        }

        #endregion

        #region Command guards

        public void RaiseCanExecuteChanged()
        {
            PreviousConflictCommand.RaiseCanExecuteChanged();
            NextConflictCommand.RaiseCanExecuteChanged();
            TakeSourceBlockCommand.RaiseCanExecuteChanged();
            TakeTargetBlockCommand.RaiseCanExecuteChanged();
            TakeBothBlockCommand.RaiseCanExecuteChanged();
            AcceptResultCommand.RaiseCanExecuteChanged();
            TakeSourceFileCommand.RaiseCanExecuteChanged();
            KeepTargetFileCommand.RaiseCanExecuteChanged();
            RetryLoadCommand.RaiseCanExecuteChanged();
        }

        // Regola comune a tutti i comandi: canAct() && !IsBusy && !IsLoading (e conflitto non gia' risolto).
        private bool CanActCore()
        {
            if (_disposed || IsResolved || IsBusy || IsLoading)
                return false;
            return _canAct == null || _canAct();
        }

        private bool CanNavigate()
        {
            return CanActCore() && IsContentConflict && _regions.Count > 0;
        }

        private bool CanTakeBlock()
        {
            return CanNavigate() && CurrentRegionIndex >= 0 && CurrentRegionIndex < _regions.Count;
        }

        private bool CanAcceptResult()
        {
            return CanActCore() && IsContentConflict && !_regionScanFailed && _regions.Count == 0
                && _resultText != null && _resultEncoding != null;
        }

        private bool CanActOnFile()
        {
            return CanActCore();
        }

        private bool CanRetryLoad()
        {
            return CanActCore() && _loadFailed;
        }

        private void RaiseDependentState()
        {
            OnPropertyChanged("RemainingConflictBlocks");
            OnPropertyChanged("ResolvedConflictBlocks");
            OnPropertyChanged("HasConflictBlocks");
            OnPropertyChanged("HasRemainingConflicts");
            OnPropertyChanged("ConflictBlocksText");
            OnPropertyChanged("CurrentConflictText");
            OnPropertyChanged("CurrentRegion");
            OnPropertyChanged("CurrentConflictLocation");
            OnPropertyChanged("ConflictNavigatorText");
            OnPropertyChanged("ShowLoading");
            OnPropertyChanged("ShowEditor");
            OnPropertyChanged("ShowFileCard");
            OnPropertyChanged("HasErrorMessage");
            OnPropertyChanged("ShowRetry");
            OnPropertyChanged("IsResultReadOnly");
            OnPropertyChanged("StatusText");
            // Durante la costruzione i comandi non esistono ancora.
            if (RetryLoadCommand != null)
                RaiseCanExecuteChanged();
        }

        #endregion

        #region Load

        // Il risultato iniziale dell'editor si calcola con merge(base, source, target) invece che con
        // ThreeWayMerge.Merge: serve a partire da un testo gia' preparato da chi ospita il resolver (es.
        // righe protette dalle regole di merge che restano quelle del target). note (facoltativa) va
        // nella striscia sotto l'intestazione. Solo prima del caricamento (thread UI): dopo non cambia
        // piu' nulla, e il metodo lo dice restituendo false.
        internal bool UseMergeOverride(Func<string, string, string, ThreeWayMergeResult> merge, string note)
        {
            if (_disposed || _loadTask != null)
                return false;
            _mergeOverride = merge;
            _mergeOverrideNote = note;
            return true;
        }

        // Idempotente: chiamate ripetute restituiscono lo stesso Task. Da chiamare sul thread UI.
        public Task LoadAsync()
        {
            if (_disposed)
                return Task.CompletedTask;
            if (_loadTask == null)
                _loadTask = LoadCoreAsync();
            return _loadTask;
        }

        private Task RetryLoadAsync()
        {
            if (!CanRetryLoad())
                return Task.CompletedTask;
            _loadTask = null;
            return LoadAsync();
        }

        private async Task LoadCoreAsync()
        {
            IsLoading = true;
            ErrorMessage = null;
            _loadFailed = false;
            try
            {
                var conflict = Conflict;
                var folder = _tempFolder;
                var mergeOverride = _mergeOverride;
                var overrideNote = _mergeOverrideNote;
                var result = await Task.Run(() => LoadContent(conflict, folder, mergeOverride, overrideNote));
                if (_disposed)
                    return;
                ApplyLoadResult(result);
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                IsContentConflict = false;
                ContentUnavailableReason = "The file versions could not be loaded.";
                ErrorMessage = "Cannot load the conflict: " + ex.Message;
                LogError("Conflict resolver: load failed for " + Path, ex);
            }
            finally
            {
                _loadCompleted = true;
                IsLoading = false;
                RaiseDependentState();
                if (_disposed)
                    DeleteTempFolder(_tempFolder);
            }
        }

        private void ApplyLoadResult(LoadResult result)
        {
            if (result.Note != null)
                ConflictDescription = _baseDescription + ". " + result.Note;
            ConflictNote = result.Note;

            if (!result.IsContentConflict)
            {
                ContentUnavailableReason = result.Reason;
                IsContentConflict = false;
                LogInfo(string.Format(CultureInfo.InvariantCulture, "Conflict resolver: {0} is not a line-by-line merge ({1})",
                    Path, result.Reason));
                return;
            }

            BaseText = result.BaseText;
            SourceText = result.SourceText;
            TargetText = result.TargetText;
            _resultEncoding = result.ResultEncoding;
            _resultBom = result.ResultBom ?? new byte[0];
            _normalizeTypedCrLf = result.NormalizeTypedCrLf;
            _markerSize = Math.Max(ThreeWayMerge.DefaultMarkerSize, result.MarkerSize);
            EncodingText = string.Format(CultureInfo.InvariantCulture, "{0}{1}, {2}",
                result.ResultEncoding.WebName, _resultBom.Length > 0 ? " with BOM" : string.Empty, DescribeNewLine(result.NewLine));
            ContentUnavailableReason = null;
            IsContentConflict = true;
            TotalConflictBlocks = result.ConflictCount;
            // SetResultTextCore confronta la mappa con le regioni trovate nel testo.
            _conflictMap = new OpenConflictMap(result.ConflictLocations);
            SetResultTextCore(result.ResultText, 0, false, true);

            LogInfo(string.Format(CultureInfo.InvariantCulture, "Conflict resolver: {0} loaded, {1} conflict block(s), {2}",
                Path, result.ConflictCount, EncodingText));
        }

        private sealed class LoadResult
        {
            public bool IsContentConflict;
            public string Reason;
            public string Note;
            public string BaseText;
            public string SourceText;
            public string TargetText;
            public string ResultText;
            public int ConflictCount;
            public IReadOnlyList<ConflictLocation> ConflictLocations;
            public int MarkerSize;
            public string NewLine;
            public Encoding ResultEncoding;
            public byte[] ResultBom;
            public bool NormalizeTypedCrLf;
        }

        // Testo decodificato (vedi DecodeBytes): Text/Encoding/Bom, oppure Error. Usato anche dalla
        // scheda per i file con righe protette (stessa lettura e stessa riscrittura del resolver).
        internal sealed class DecodedText
        {
            public string Text;
            public Encoding Encoding;
            public byte[] Bom;
            public string Error;
        }

        private static LoadResult NotMergeable(string reason, string note)
        {
            return new LoadResult { IsContentConflict = false, Reason = reason, Note = note };
        }

        // Thread di background. Eccezioni inattese -> LoadCoreAsync (ErrorMessage).
        // mergeOverride: fusione al posto di ThreeWayMerge.Merge (vedi UseMergeOverride); overrideNote
        // si aggiunge alla nota della striscia.
        private static LoadResult LoadContent(Conflict conflict, string folder,
            Func<string, string, string, ThreeWayMergeResult> mergeOverride, string overrideNote)
        {
            CleanupStaleTempFolders(System.IO.Path.GetDirectoryName(folder));

            var reason = GetNotMergeableReason(conflict);
            if (reason != null)
                return NotMergeable(reason, null);

            Directory.CreateDirectory(folder);
            var extension = GetTempExtension(conflict);

            var sourceFile = System.IO.Path.Combine(folder, "source" + extension);
            conflict.DownloadTheirFile(sourceFile);
            if (!File.Exists(sourceFile))
                return NotMergeable("The source version could not be downloaded.", null);

            // Il target e' il file locale del workspace: contiene cio' che c'e' davvero (comprese le
            // modifiche in sospeso dei batch gia' fusi). TFVC, con AcceptMerge + MergedFileName, sposta il
            // file fuso proprio su SourceLocalItem (verificato nell'IL di Client.ResolveLocalConflicts),
            // quindi e' il primo candidato; poi TargetLocalItem; se nessuno esiste, DownloadYourFile.
            var targetFile = FindLocalTargetFile(conflict);
            if (targetFile == null)
            {
                targetFile = System.IO.Path.Combine(folder, "target" + extension);
                conflict.DownloadYourFile(targetFile);
                if (!File.Exists(targetFile))
                    return NotMergeable("The target version was not found in the workspace and could not be downloaded.", null);
            }

            string note = null;
            string baseFile = null;
            if (conflict.IsBaseless)
            {
                note = "No common base version (baseless merge): every difference is shown as a conflict";
            }
            else
            {
                baseFile = System.IO.Path.Combine(folder, "base" + extension);
                try
                {
                    conflict.DownloadBaseFile(baseFile);
                }
                catch (Exception ex)
                {
                    note = "The common base version could not be downloaded (" + ex.Message + "): every difference is shown as a conflict";
                    baseFile = null;
                }
                if (baseFile != null && !File.Exists(baseFile))
                {
                    note = "The common base version is not available: every difference is shown as a conflict";
                    baseFile = null;
                }
            }

            var tooLarge = FindTooLargeFile(new[] { sourceFile, targetFile, baseFile });
            if (tooLarge != null)
                return NotMergeable(tooLarge, null);

            var source = ReadText(sourceFile, "source", conflict.TheirEncoding, conflict.YourEncoding);
            if (source.Error != null)
                return NotMergeable(source.Error, null);

            var target = ReadText(targetFile, "target", conflict.YourEncoding, conflict.TheirEncoding);
            if (target.Error != null)
                return NotMergeable(target.Error, null);

            var baseText = string.Empty;
            if (baseFile != null)
            {
                var decodedBase = ReadText(baseFile, "base", conflict.BaseEncoding, conflict.TheirEncoding, conflict.YourEncoding);
                if (decodedBase.Error == null)
                    baseText = decodedBase.Text;
                else
                    note = "The common base version could not be read (" + decodedBase.Error + "): every difference is shown as a conflict";
            }

            var merge = mergeOverride != null
                ? mergeOverride(baseText, source.Text, target.Text)
                : ThreeWayMerge.Merge(baseText, source.Text, target.Text);
            if (merge == null)
                throw new InvalidOperationException("The merge of the three versions returned no result.");
            var withMarkers = ThreeWayMerge.BuildTextWithMarkers(merge, SourceLabel, TargetLabel);
            if (mergeOverride != null && !string.IsNullOrEmpty(overrideNote))
                note = note == null ? overrideNote : overrideNote + ". " + note;

            if (merge.IsApproximate && merge.ConflictCount > 0)
            {
                const string approximate = "Very large change: the line-by-line comparison was simplified, so a conflict block may be larger than the real overlap";
                note = note == null ? approximate : note + ". " + approximate;
            }

            // Target solo LF: la fusione da' alle righe del source l'a-capo del target, quindi gli unici
            // CRLF nel risultato saranno quelli digitati nella TextBox (Invio = CRLF).
            var targetIsPureLf = target.Text.IndexOf("\r\n", StringComparison.Ordinal) < 0
                && target.Text.IndexOf('\n') >= 0;

            return new LoadResult
            {
                IsContentConflict = true,
                Note = note,
                BaseText = baseText,
                SourceText = source.Text,
                TargetText = target.Text,
                ResultText = withMarkers,
                ConflictCount = merge.ConflictCount,
                ConflictLocations = ThreeWayMerge.GetConflictLocations(merge),
                MarkerSize = merge.MarkerSize,
                NewLine = merge.NewLine,
                // Il risultato si scrive come il target (stesso encoding, stesso BOM). Se il code page del
                // target non era noto, ReadText ha gia' usato quello del source.
                ResultEncoding = target.Encoding,
                ResultBom = target.Bom,
                NormalizeTypedCrLf = targetIsPureLf
            };
        }

        private static string GetNotMergeableReason(Conflict conflict)
        {
            if (conflict.IsNamespaceConflict)
                return "This is a name conflict (the path is used differently in the two branches): keep one of the two versions.";
            if (HasFlag(conflict.TheirChangeType, ChangeType.Delete) || HasFlag(conflict.YourChangeType, ChangeType.Delete))
                return "One branch deleted the file, so there is no text to merge line by line.";
            if (!conflict.TheirFileExists)
                return "The source version of the file does not exist.";
            if (conflict.IsBinary)
                return "The file is binary: pick the version to keep.";
            if (!conflict.CanMergeContent)
                return "TFVC does not allow a content merge for this conflict.";
            return null;
        }

        private static string FindLocalTargetFile(Conflict conflict)
        {
            foreach (var candidate in new[] { conflict.SourceLocalItem, conflict.TargetLocalItem })
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static string FindTooLargeFile(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                if (file == null)
                    continue;
                var length = new FileInfo(file).Length;
                if (length > MaxEditableFileBytes)
                    return string.Format(CultureInfo.InvariantCulture,
                        "The file is too large for the built-in editor ({0:N0} KB): pick the version to keep.", length / 1024);
            }
            return null;
        }

        // Decodifica rispettando il BOM (che vince sul code page dichiarato) e, senza BOM, il code page
        // del conflitto; codePages dopo il primo sono i ripieghi se il primo e' sconosciuto (<= 0 ma non
        // binario, o non valido). Decoder rigoroso: byte non validi -> non fondibile, mai caratteri
        // sostituiti che cambierebbero il file alla riscrittura.
        internal static DecodedText ReadText(string file, string side, params int[] codePages)
        {
            return DecodeBytes(File.ReadAllBytes(file), side, codePages);
        }

        // Come ReadText, su byte gia' letti (es. per decodificare esattamente i byte di cui si e' presa
        // l'impronta).
        internal static DecodedText DecodeBytes(byte[] bytes, string side, params int[] codePages)
        {
            if (bytes == null)
                bytes = new byte[0];
            if (codePages == null)
                codePages = new int[0];

            int bomLength;
            var encoding = DetectBom(bytes, out bomLength);
            if (encoding == null)
            {
                if (codePages.Length > 0 && codePages[0] == EncodingBinary)
                    return new DecodedText { Error = "The " + side + " version is binary." };
                foreach (var codePage in codePages)
                {
                    if (codePage == EncodingBinary)
                        break;
                    encoding = GetStrictEncoding(codePage);
                    if (encoding != null)
                        break;
                }
                if (encoding == null)
                    return new DecodedText { Error = "The encoding of the " + side + " version is unknown." };
            }

            string text;
            try
            {
                text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
            }
            catch (DecoderFallbackException)
            {
                return new DecodedText
                {
                    Error = string.Format(CultureInfo.InvariantCulture, "The {0} version is not valid {1} text.", side, encoding.WebName)
                };
            }

            if (text.IndexOf('\0') >= 0)
                return new DecodedText { Error = "The " + side + " version looks like a binary file." };

            var bom = new byte[bomLength];
            Array.Copy(bytes, bom, bomLength);
            return new DecodedText { Text = text, Encoding = encoding, Bom = bom };
        }

        private static Encoding DetectBom(byte[] bytes, out int bomLength)
        {
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            {
                bomLength = 4;
                return new UTF32Encoding(false, false, true);
            }
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                bomLength = 4;
                return new UTF32Encoding(true, false, true);
            }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bomLength = 3;
                return new UTF8Encoding(false, true);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                bomLength = 2;
                return new UnicodeEncoding(false, false, true);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                bomLength = 2;
                return new UnicodeEncoding(true, false, true);
            }
            bomLength = 0;
            return null;
        }

        // Encoding con fallback a eccezione sia in lettura sia in scrittura; null se il code page non e'
        // utilizzabile (<= 0: binario, "invariato", cartella, sconosciuto).
        private static Encoding GetStrictEncoding(int codePage)
        {
            if (codePage <= 0)
                return null;
            try
            {
                switch (codePage)
                {
                    case 65001:
                        return new UTF8Encoding(false, true);
                    case 1200:
                        return new UnicodeEncoding(false, false, true);
                    case 1201:
                        return new UnicodeEncoding(true, false, true);
                    case 12000:
                        return new UTF32Encoding(false, false, true);
                    case 12001:
                        return new UTF32Encoding(true, false, true);
                    default:
                        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        #endregion

        #region Result text, regions and navigation

        // preferredIndex: regione da rendere corrente. wrap: oltre l'ultima si torna alla prima (dopo una
        // scelta); altrimenti si resta sull'ultima (modifica a mano).
        private void SetResultTextCore(string text, int preferredIndex, bool wrap, bool navigate)
        {
            _resultText = text ?? string.Empty;
            OnPropertyChanged("ResultText");

            try
            {
                _regions = ThreeWayMerge.FindConflictRegions(_resultText, _markerSize) ?? NoRegions;
                _regionScanFailed = false;
            }
            catch (Exception ex)
            {
                // Senza regioni affidabili Accept resta disabilitato (non si salvano marker per errore).
                _regions = NoRegions;
                _regionScanFailed = true;
                LogError("Conflict resolver: cannot scan the conflict markers of " + Path, ex);
            }

            var count = _regions.Count;
            if (count > TotalConflictBlocks)
                TotalConflictBlocks = count;
            // Ricostruita a ogni rilettura (per contenuto, poi per posizione): vedi OpenConflictMap.
            if (_conflictMap != null)
                _conflictMap.Update(_regions);

            int index;
            if (count == 0)
                index = -1;
            else if (preferredIndex < 0)
                index = 0;
            else if (preferredIndex >= count)
                index = wrap ? 0 : count - 1;
            else
                index = preferredIndex;

            _currentRegionIndex = index;
            OnPropertyChanged("CurrentRegionIndex");
            RaiseDependentState();

            if (navigate && index >= 0)
                RaiseNavigateRequested(_regions[index]);
        }

        // La vista comunica dove si trova il cursore nel risultato: se e' dentro una regione, quella
        // diventa la corrente (cosi' Take source/target/both agiscono sul blocco che l'utente guarda).
        internal void SelectRegionAt(int offset)
        {
            if (_disposed || !IsContentConflict)
                return;
            for (var i = 0; i < _regions.Count; i++)
            {
                var region = _regions[i];
                if (offset >= region.Start && offset <= region.Start + region.Length)
                {
                    CurrentRegionIndex = i;
                    return;
                }
            }
        }

        private void Navigate(int delta)
        {
            if (!CanNavigate())
                return;
            var count = _regions.Count;
            int index;
            if (CurrentRegionIndex < 0 || CurrentRegionIndex >= count)
                index = delta > 0 ? 0 : count - 1;
            else
                index = ((CurrentRegionIndex + delta) % count + count) % count;
            CurrentRegionIndex = index;
            RaiseNavigateRequested(_regions[index]);
        }

        private void TakeBlock(ConflictChoice choice)
        {
            if (!CanTakeBlock())
                return;
            var index = CurrentRegionIndex;
            var region = _regions[index];
            string newText;
            try
            {
                newText = ThreeWayMerge.ResolveRegion(_resultText, region, choice);
            }
            catch (Exception ex)
            {
                ErrorMessage = "Cannot apply the choice to this conflict: " + ex.Message;
                LogError("Conflict resolver: ResolveRegion failed for " + Path, ex);
                return;
            }
            ErrorMessage = null;
            // Il blocco della regione risolta esce dal testo: la mappa lo riprende solo se ricompare
            // (Ctrl+Z) e nessun altro blocco ha lo stesso contenuto.
            if (_conflictMap != null)
                _conflictMap.MarkResolved(index);
            // Dopo la sostituzione la regione successiva ha lo stesso indice (oltre l'ultima: la prima).
            SetResultTextCore(newText, index, true, true);
        }

        private void RaiseNavigateRequested(ConflictRegion region)
        {
            var handler = NavigateRequested;
            if (handler != null && region != null)
                handler(this, region);
        }

        private static bool ContainsOwnMarkers(string text)
        {
            return text.IndexOf(ThreeWayMerge.SourceMarkerPrefix + SourceLabel, StringComparison.Ordinal) >= 0
                || text.IndexOf(ThreeWayMerge.TargetMarkerPrefix + TargetLabel, StringComparison.Ordinal) >= 0;
        }

        #endregion

        #region Resolution

        private Task AcceptResultAsync()
        {
            if (!CanAcceptResult())
                return Task.CompletedTask;

            var text = _resultText ?? string.Empty;
            // Marker malformati (es. riga "=======" cancellata) non sono regioni ma non vanno salvati.
            if (ContainsOwnMarkers(text))
            {
                ErrorMessage = "The result still contains conflict marker lines (<<<<<<< / >>>>>>>): remove them before accepting.";
                return Task.CompletedTask;
            }
            if (_normalizeTypedCrLf)
                text = text.Replace("\r\n", "\n");

            var encoding = _resultEncoding;
            var bom = _resultBom;
            var folder = _tempFolder;
            var resultFile = System.IO.Path.Combine(folder,
                "result-" + Guid.NewGuid().ToString("N").Substring(0, 8) + GetTempExtension(Conflict));
            var description = string.Format(CultureInfo.InvariantCulture, "{0}: merged result accepted ({1})", Path,
                TotalConflictBlocks == 0
                    ? "no overlapping changes"
                    : string.Format(CultureInfo.InvariantCulture, "{0} conflict block(s) resolved in the editor", TotalConflictBlocks));

            return RunResolutionAsync(conflict =>
            {
                byte[] bytes;
                try
                {
                    bytes = EncodeResult(text, encoding, bom);
                }
                catch (EncoderFallbackException ex)
                {
                    throw new ResolutionFailedException(string.Format(CultureInfo.InvariantCulture,
                        "The result contains characters that cannot be saved as {0} (the target encoding){1}.",
                        encoding.WebName,
                        ex.CharUnknown != '\0' ? string.Format(CultureInfo.InvariantCulture, ", e.g. U+{0:X4}", (int)ex.CharUnknown) : string.Empty));
                }
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(resultFile, bytes);
                PrepareAcceptMerge(conflict, resultFile);
            }, description, "accept the merged result");
        }

        // Risoluzione AcceptMerge con il file gia' scritto resultFile (con encoding e BOM del target).
        // Verificato per riflessione (lib\18.0\Microsoft.TeamFoundation.VersionControl.Client.dll, IL
        // di Workspace.ResolveConflictsInternal, Client.ResolveLocalConflicts e ResolutionOptions):
        // - con Resolution.AcceptMerge e MergedFileName valorizzato TFVC NON rifa' la fusione del
        //   contenuto e usa il nostro file (in un workspace locale lo sposta su SourceLocalItem);
        // - UseInternalEngine conta solo se MergedFileName e' vuoto: false (default!) = lancia lo
        //   strumento di merge ESTERNO. True per non aprire mai finestre da un thread di background;
        // - AcceptMergeWithConflicts (default false) conta solo con la fusione interna: resta false;
        // - AcceptMergeEncoding va al server come encoding dell'elemento fuso: 0 (default) il
        //   getter lo trasforma in -2 = RepositoryConstants.EncodingUnchanged. Lo impostiamo in modo
        //   esplicito: il file e' scritto con l'encoding e il BOM del target, che quindi non cambia;
        // - IsEncodingOverride / IsConvertToEncoding / Encoding servono solo al motore interno
        //   (ThreeWayMerge di TFVC), che qui non gira: non si toccano.
        private static void PrepareAcceptMerge(Conflict conflict, string resultFile)
        {
            conflict.MergedFileName = resultFile;
            var options = conflict.ResolutionOptions;
            options.UseInternalEngine = true;
            options.AcceptMergeWithConflicts = false;
            options.AcceptMergeEncoding = EncodingUnchanged;
            conflict.Resolution = Resolution.AcceptMerge;
        }

        // Stessa risoluzione di "Accept result", per chi ha gia' calcolato e scritto il risultato
        // (mergedFile, con encoding e BOM del target: vedi EncodeResult). Thread di background. Item1:
        // TFVC ha segnato il conflitto come risolto; Item2: il motivo se no.
        internal static Tuple<bool, string> ResolveWithMergedFile(Workspace workspace, Conflict conflict, string mergedFile)
        {
            if (workspace == null)
                throw new ArgumentNullException("workspace");
            if (conflict == null)
                throw new ArgumentNullException("conflict");
            if (string.IsNullOrEmpty(mergedFile) || !File.Exists(mergedFile))
                return Tuple.Create(false, "The merged file to resolve the conflict with does not exist.");
            return ResolveCore(workspace, conflict, c => PrepareAcceptMerge(c, mergedFile));
        }

        private Task ResolveWholeFileAsync(Resolution resolution)
        {
            if (!CanActOnFile())
                return Task.CompletedTask;
            var description = resolution == Resolution.AcceptTheirs
                ? Path + ": took the whole source file"
                : Path + ": kept the whole target file";
            var actionText = resolution == Resolution.AcceptTheirs ? "take the source file" : "keep the target file";
            return RunResolutionAsync(conflict =>
            {
                conflict.MergedFileName = null;
                conflict.Resolution = resolution;
            }, description, actionText);
        }

        // prepare gira in background subito prima di ResolveConflict. Dopo il successo (IsResolved) si
        // chiama onResolved sul thread UI; qualsiasi errore -> ErrorMessage.
        private async Task RunResolutionAsync(Action<Conflict> prepare, string description, string actionText)
        {
            if (!CanActCore())
                return;

            IsBusy = true;
            ErrorMessage = null;
            var resolved = false;
            var conflict = Conflict;
            var workspace = _workspace;
            try
            {
                var outcome = await Task.Run(() => ResolveCore(workspace, conflict, prepare));
                resolved = outcome.Item1;
                if (!resolved)
                {
                    ErrorMessage = outcome.Item2;
                    LogInfo(string.Format(CultureInfo.InvariantCulture, "Conflict resolver: {0}: could not {1}: {2}",
                        Path, actionText, outcome.Item2));
                }
            }
            catch (ResolutionFailedException ex)
            {
                ErrorMessage = ex.Message;
                LogInfo("Conflict resolver: " + Path + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                ErrorMessage = string.Format(CultureInfo.InvariantCulture, "Cannot {0}: {1}", actionText, ex.Message);
                LogError("Conflict resolver: " + actionText + " failed for " + Path, ex);
            }
            finally
            {
                IsBusy = false;
            }

            if (_disposed)
            {
                DeleteTempFolder(_tempFolder);
                return;
            }
            if (!resolved)
                return;

            IsResolved = true;
            LogInfo("Conflict resolver: " + description);

            if (_onResolved == null)
                return;
            try
            {
                await _onResolved(this, description);
            }
            catch (Exception ex)
            {
                LogError("Conflict resolver: the post-resolution callback failed for " + Path, ex);
                if (!_disposed)
                    ErrorMessage = "The conflict is resolved, but refreshing the chain failed: " + ex.Message;
            }
        }

        // Thread di background. Item1: TFVC ha segnato il conflitto come risolto; Item2: messaggio se no.
        private static Tuple<bool, string> ResolveCore(Workspace workspace, Conflict conflict, Action<Conflict> prepare)
        {
            // Gli errori "non fatali" di ResolveConflict arrivano come evento, non come eccezione.
            var messages = new List<string>();
            ExceptionEventHandler handler = (sender, e) =>
            {
                if (e == null)
                    return;
                var message = e.Exception != null ? e.Exception.Message : (e.Failure != null ? e.Failure.Message : null);
                if (string.IsNullOrEmpty(message))
                    return;
                lock (messages)
                    messages.Add(message);
            };

            var server = workspace.VersionControlServer;
            if (server != null)
                server.NonFatalError += handler;
            try
            {
                prepare(conflict);
                workspace.ResolveConflict(conflict);
            }
            finally
            {
                if (server != null)
                    server.NonFatalError -= handler;
            }

            if (conflict.IsResolved)
                return Tuple.Create(true, (string)null);

            // Stato pulito per un altro tentativo sullo stesso oggetto.
            try
            {
                conflict.MergedFileName = null;
                conflict.Resolution = Resolution.None;
            }
            catch (Exception)
            {
                // Solo pulizia: il messaggio sotto e' quello che conta.
            }

            string detail;
            lock (messages)
                detail = messages.FirstOrDefault();
            return Tuple.Create(false, detail == null
                ? "TFVC did not mark the conflict as resolved: refresh the conflicts and try again."
                : "TFVC did not resolve the conflict: " + detail);
        }

        // Testo -> byte con l'encoding (rigoroso: carattere non rappresentabile = EncoderFallbackException)
        // e il BOM dati.
        internal static byte[] EncodeResult(string text, Encoding encoding, byte[] bom)
        {
            var body = encoding.GetBytes(text);
            if (bom == null || bom.Length == 0)
                return body;
            var bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            return bytes;
        }

        // Errore da mostrare cosi' com'e' (messaggio gia' in inglese e comprensibile).
        private sealed class ResolutionFailedException : Exception
        {
            public ResolutionFailedException(string message)
                : base(message)
            {
            }
        }

        #endregion

        #region Descriptions

        private static bool HasFlag(ChangeType value, ChangeType flag)
        {
            return (value & flag) == flag;
        }

        private static string DescribeConflict(Conflict conflict)
        {
            var theirs = conflict.TheirChangeType;
            var yours = conflict.YourChangeType;
            var sourceDeleted = HasFlag(theirs, ChangeType.Delete);
            var targetDeleted = HasFlag(yours, ChangeType.Delete);
            var sourceEdited = HasFlag(theirs, ChangeType.Edit);
            var targetEdited = HasFlag(yours, ChangeType.Edit);
            var sourceRenamed = HasFlag(theirs, ChangeType.Rename);
            var targetRenamed = HasFlag(yours, ChangeType.Rename);

            string text;
            if (conflict.IsNamespaceConflict)
                text = "Name conflict: the path is used by different items in the two branches";
            else if (sourceDeleted && targetDeleted)
                text = "Deleted in both branches";
            else if (sourceDeleted)
                text = targetEdited ? "Deleted in source, edited in target" : "Deleted in source, changed in target";
            else if (targetDeleted)
                text = sourceEdited ? "Deleted in target, edited in source" : "Deleted in target, changed in source";
            else if (!conflict.TheirFileExists)
                text = "The source version of this file is not available";
            else if (sourceRenamed && targetRenamed)
                text = "Renamed in both branches";
            else if (sourceRenamed)
                text = targetEdited ? "Renamed in source, edited in target" : "Renamed in source, changed in target";
            else if (targetRenamed)
                text = sourceEdited ? "Renamed in target, edited in source" : "Renamed in target, changed in source";
            else if (conflict.IsBinary)
                text = "Binary file changed in both branches";
            else
                text = "Both branches changed this file";

            if (conflict.IsEncodingMismatched && !conflict.IsBinary)
                text += string.Format(CultureInfo.InvariantCulture, " (different encodings: {0} in source, {1} in target)",
                    DescribeCodePage(conflict.TheirEncoding), DescribeCodePage(conflict.YourEncoding));
            return text;
        }

        private static string DescribeTakeSource(Conflict conflict)
        {
            if (HasFlag(conflict.TheirChangeType, ChangeType.Delete))
                return "Take source (delete the file)";
            return "Take whole source file";
        }

        private static string DescribeKeepTarget(Conflict conflict)
        {
            if (HasFlag(conflict.YourChangeType, ChangeType.Delete))
                return "Keep target (file stays deleted)";
            return "Keep whole target file";
        }

        private static string DescribeCodePage(int codePage)
        {
            if (codePage == EncodingBinary)
                return "binary";
            if (codePage <= 0)
                return "unknown";
            try
            {
                return Encoding.GetEncoding(codePage).WebName;
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            return codePage.ToString(CultureInfo.InvariantCulture);
        }

        private static string DescribeNewLine(string newLine)
        {
            switch (newLine)
            {
                case "\n":
                    return "LF";
                case "\r":
                    return "CR";
                default:
                    return "CRLF";
            }
        }

        private static string GetLastSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            var index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return index >= 0 && index < path.Length - 1 ? path.Substring(index + 1) : path;
        }

        // Senza System.IO.Path.GetExtension: i server path possono contenere caratteri non validi per Windows.
        private static string GetExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;
            var dot = fileName.LastIndexOf('.');
            return dot > 0 && dot < fileName.Length - 1 ? fileName.Substring(dot + 1) : string.Empty;
        }

        // Estensione "sicura" per i file temporanei (solo lettere e cifre, con il punto).
        private static string GetTempExtension(Conflict conflict)
        {
            var path = conflict.YourServerItem ?? conflict.TheirServerItem ?? conflict.TargetLocalItem ?? string.Empty;
            var extension = GetExtension(GetLastSegment(path));
            if (extension.Length == 0 || extension.Length > 16 || !extension.All(char.IsLetterOrDigit))
                return ".txt";
            return "." + extension;
        }

        #endregion

        #region Temp files, logging, dispose

        private static void DeleteTempFolder(string folder)
        {
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // Una volta per sessione: cartelle lasciate da sessioni chiuse male (piu' vecchie di due giorni).
        private static void CleanupStaleTempFolders(string root)
        {
            if (Interlocked.Exchange(ref _staleTempCleanupStarted, 1) != 0)
                return;
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    return;
                var limit = DateTime.UtcNow.AddDays(-2);
                foreach (var directory in new DirectoryInfo(root).GetDirectories())
                {
                    if (directory.LastWriteTimeUtc < limit)
                        DeleteTempFolder(directory.FullName);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void LogInfo(string message)
        {
            if (_logger != null)
                _logger.Info(message);
        }

        private void LogError(string message, Exception ex)
        {
            if (_logger != null)
                _logger.Error(message, ex);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            NavigateRequested = null;
            // Se un caricamento o una risoluzione sono in corso, la cartella si cancella alla loro fine
            // (TFVC potrebbe ancora leggere il file del risultato).
            if (!IsBusy && !IsLoading)
                DeleteTempFolder(_tempFolder);
            RaiseDependentState();
        }

        #endregion
    }
}
