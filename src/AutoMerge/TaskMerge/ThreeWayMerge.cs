using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace AutoMerge
{
    // Classificazione di un blocco del merge a 3 vie (base = antenato comune, source = versione in
    // arrivo dal branch sorgente, target = versione attuale nel branch di destinazione).
    public enum MergeBlockKind
    {
        // I tre lati coincidono (a meno dei terminatori di riga): si tengono le righe del target.
        Unchanged,

        // Il target e' uguale alla base, il source e' cambiato: si prendono le righe del source.
        SourceOnly,

        // Il source e' uguale alla base, il target e' cambiato: si tengono le righe del target.
        TargetOnly,

        // Entrambi cambiati allo stesso modo (anche se differiscono solo nei terminatori): righe del target.
        BothSame,

        // Entrambi cambiati in modo diverso sullo stesso tratto della base: decide l'utente.
        Conflict
    }

    // Scelta dell'utente per un singolo blocco in conflitto (vedi ThreeWayMerge.ResolveRegion).
    public enum ConflictChoice
    {
        TakeSource,
        TakeTarget,

        // Prima le righe del source, poi quelle del target.
        TakeBoth
    }

    // Un blocco contiguo del risultato. Le righe conservano il loro terminatore ("\r\n", "\n", "\r");
    // l'ultima riga di un file puo' non averlo.
    public sealed class MergeBlock
    {
        public MergeBlock(MergeBlockKind kind,
            IReadOnlyList<string> baseLines,
            IReadOnlyList<string> sourceLines,
            IReadOnlyList<string> targetLines,
            IReadOnlyList<string> resultLines)
        {
            Kind = kind;
            BaseLines = baseLines ?? ThreeWayMerge.NoLines;
            SourceLines = sourceLines ?? ThreeWayMerge.NoLines;
            TargetLines = targetLines ?? ThreeWayMerge.NoLines;
            // Per i conflitti il risultato non esiste finche' l'utente non sceglie.
            ResultLines = kind == MergeBlockKind.Conflict ? null : (resultLines ?? ThreeWayMerge.NoLines);
        }

        public MergeBlockKind Kind { get; private set; }

        public IReadOnlyList<string> BaseLines { get; private set; }

        public IReadOnlyList<string> SourceLines { get; private set; }

        public IReadOnlyList<string> TargetLines { get; private set; }

        // Kind != Conflict: righe scelte in automatico; Conflict: null.
        public IReadOnlyList<string> ResultLines { get; private set; }
    }

    public sealed class ThreeWayMergeResult
    {
        public ThreeWayMergeResult(IReadOnlyList<MergeBlock> blocks, string newLine)
            : this(blocks, newLine, null, false)
        {
        }

        internal ThreeWayMergeResult(IReadOnlyList<MergeBlock> blocks, string newLine, string sourceLineTerminator, bool isApproximate)
        {
            Blocks = blocks ?? new ReadOnlyCollection<MergeBlock>(new MergeBlock[0]);
            NewLine = string.IsNullOrEmpty(newLine) ? ThreeWayMerge.DefaultNewLine : newLine;
            SourceLineTerminator = sourceLineTerminator;
            IsApproximate = isApproximate;

            var conflicts = 0;
            foreach (var block in Blocks)
            {
                if (block != null && block.Kind == MergeBlockKind.Conflict)
                    conflicts++;
            }
            ConflictCount = conflicts;
            MarkerSize = ThreeWayMerge.ChooseMarkerSize(Blocks);
        }

        public IReadOnlyList<MergeBlock> Blocks { get; private set; }

        // Numero di blocchi Conflict.
        public int ConflictCount { get; private set; }

        // A-capo prevalente del target (poi del source, poi della base; default "\r\n"): e' quello usato
        // per le righe dei marker e per non incollare due righe.
        public string NewLine { get; private set; }

        // Lunghezza dei marker scritti da BuildTextWithMarkers (7 come git, di piu' se una riga del testo
        // e' essa stessa un marker di 7 caratteri: es. "=======" sotto un titolo Markdown/RST). Le regioni
        // si cercano con FindConflictRegions(text, MarkerSize), cosi' quelle righe restano testo.
        public int MarkerSize { get; private set; }

        // True se il diff ha dovuto rinunciare all'allineamento fine su un tratto con righe in comune
        // (file enormi o modifiche massicce): qualche blocco in conflitto puo' essere piu' grande della
        // sovrapposizione vera.
        public bool IsApproximate { get; private set; }

        // Terminatore da dare alle righe prese dal source (null = lasciarle come sono): e' quello del
        // target quando il target ne usa uno solo, cosi' il risultato non mescola gli a-capo.
        internal string SourceLineTerminator { get; private set; }
    }

    // Una regione "<<<<<<< ... ======= ... >>>>>>> ..." ben formata dentro un testo.
    public sealed class ConflictRegion
    {
        public ConflictRegion(int index, int start, int length, int startLine, string sourceText, string targetText)
        {
            Index = index;
            Start = start;
            Length = length;
            StartLine = startLine;
            SourceText = sourceText ?? string.Empty;
            TargetText = targetText ?? string.Empty;
        }

        // 0-based, in ordine nel testo.
        public int Index { get; private set; }

        // Offset (in caratteri) dell'inizio della riga "<<<<<<< ...".
        public int Start { get; private set; }

        // Fino alla fine della riga ">>>>>>> ..." compreso il suo a-capo, se c'e'.
        public int Length { get; private set; }

        // Riga (0-based) del marker "<<<<<<< ...".
        public int StartLine { get; private set; }

        // Testo tra la riga "<<<<<<<" e la riga "=======" (a-capo inclusi).
        public string SourceText { get; private set; }

        // Testo tra la riga "=======" e la riga ">>>>>>>" (a-capo inclusi).
        public string TargetText { get; private set; }
    }

    // Dove sta un blocco Conflict della fusione nei due file d'origine (righe 0-based, contate come
    // ThreeWayMerge.SplitLines). Serve a portare il confronto SOURCE | TARGET sulla stessa zona del
    // blocco mostrato nel risultato.
    public sealed class ConflictLocation
    {
        public ConflictLocation(int index, int sourceStartLine, int sourceLineCount, int targetStartLine, int targetLineCount)
            : this(index, sourceStartLine, sourceLineCount, targetStartLine, targetLineCount, null, null)
        {
        }

        public ConflictLocation(int index, int sourceStartLine, int sourceLineCount, int targetStartLine, int targetLineCount,
            IReadOnlyList<string> sourceLines, IReadOnlyList<string> targetLines)
        {
            Index = index;
            SourceStartLine = sourceStartLine;
            SourceLineCount = sourceLineCount;
            TargetStartLine = targetStartLine;
            TargetLineCount = targetLineCount;
            SourceLines = sourceLines;
            TargetLines = targetLines;
        }

        // Righe del blocco nel source e nel target, con i loro terminatori (null = non note): servono a
        // OpenConflictMap per riconoscere il blocco nel risultato anche dopo modifiche al testo.
        public IReadOnlyList<string> SourceLines { get; private set; }

        public IReadOnlyList<string> TargetLines { get; private set; }

        // 0-based fra i blocchi Conflict della fusione, nell'ordine del testo.
        public int Index { get; private set; }

        // Prima riga del blocco nel source (se il blocco non ha righe nel source: la riga davanti alla
        // quale starebbero).
        public int SourceStartLine { get; private set; }

        public int SourceLineCount { get; private set; }

        // Prima riga del blocco nel target (stessa regola del source).
        public int TargetStartLine { get; private set; }

        public int TargetLineCount { get; private set; }
    }

    // Merge a 3 vie per righe (stile diff3), senza dipendenze da Visual Studio o TFVC.
    //
    // 1. Le righe si confrontano sul contenuto SENZA terminatore (ogni contenuto diventa un intero).
    // 2. Due diff base->source e base->target con Myers O(ND) in spazio lineare (bisezione sul
    //    "middle snake", come diff-match-patch), dopo il taglio di prefisso e suffisso comuni. Se la
    //    bisezione rinuncia (soglia o budget), le righe uniche in entrambi i lati fanno da ancore
    //    (come il "patience diff") prima di arrendersi a un'unica modifica (IsApproximate).
    // 3. Le righe della base allineate in entrambi i diff sono i punti fermi; tra due punti fermi c'e'
    //    un tratto instabile che si classifica confrontando i tre lati (SourceOnly, TargetOnly,
    //    BothSame, Conflict). Due modifiche diverse separate da almeno una riga ferma non confliggono;
    //    modifiche su righe adiacenti della base si' (come diff3 e git).
    // 4. Le righe prese dal source ricevono l'a-capo del target quando il target ne usa uno solo; i
    //    marker sono lunghi MarkerSize (7, o di piu' se il testo contiene gia' righe-marker).
    public static class ThreeWayMerge
    {
        public const string SourceMarkerPrefix = "<<<<<<< ";
        public const string SeparatorMarker = "=======";
        public const string TargetMarkerPrefix = ">>>>>>> ";

        // Lunghezza minima (e di default) dei marker: 7 caratteri, come git.
        public const int DefaultMarkerSize = 7;

        internal const string DefaultNewLine = "\r\n";

        // Oltre questa distanza di modifica (righe cancellate + inserite) il diff smette di cercare
        // l'allineamento ottimo e ripiega sulle righe uniche (vedi DiffRange). Il risultato resta
        // corretto: al peggio meno fine (un tratto modificato piu' grande, quindi piu' probabilita' di conflitto).
        internal const int MaxEditDistance = 10000;

        // Tetto al lavoro di un singolo diff (diagonali visitate + passi lungo i serpenti), per restare
        // sotto il secondo anche su file enormi pieni di righe ripetute. Superato il tetto si degrada
        // come sopra.
        private const long MaxDiffWork = 100000000L;

        internal static readonly IReadOnlyList<string> NoLines = new ReadOnlyCollection<string>(new string[0]);

        // Divide il testo in righe conservando i terminatori ("\r\n", "\n", "\r"). L'ultima riga puo'
        // non avere terminatore; un testo che finisce con un a-capo NON produce una riga vuota finale.
        public static IReadOnlyList<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return NoLines;

            var lines = new List<string>();
            var start = 0;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\r')
                {
                    i += (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                    lines.Add(text.Substring(start, i - start));
                    start = i;
                }
                else if (c == '\n')
                {
                    i++;
                    lines.Add(text.Substring(start, i - start));
                    start = i;
                }
                else
                {
                    i++;
                }
            }

            if (start < text.Length)
                lines.Add(text.Substring(start));

            return lines.AsReadOnly();
        }

        // Offset del primo carattere della riga lineIndex (0-based, righe contate come SplitLines:
        // "\r\n", "\n", "\r"). Oltre l'ultima riga: text.Length; lineIndex <= 0: 0.
        public static int GetLineStartOffset(string text, int lineIndex)
        {
            if (string.IsNullOrEmpty(text) || lineIndex <= 0)
                return 0;

            var line = 0;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\r')
                    i += (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                else if (c == '\n')
                    i++;
                else
                {
                    i++;
                    continue;
                }

                line++;
                if (line == lineIndex)
                    return i;
            }
            return text.Length;
        }

        public static ThreeWayMergeResult Merge(string baseText, string sourceText, string targetText)
        {
            var input = new MergeInput(SplitLines(baseText), SplitLines(sourceText), SplitLines(targetText));
            // Le righe prese dal source ricevono l'a-capo del target, se il target ne usa uno solo: un
            // file LF (es. uno script .sh) non deve ritrovarsi righe CRLF arrivate dall'altro branch.
            input.SourceLineTerminator = DetectSingleNewLine(input.TargetLines);

            bool sourceApproximate;
            bool targetApproximate;
            var sourceMatch = Diff(input.BaseIds, input.SourceIds, out sourceApproximate);
            var targetMatch = Diff(input.BaseIds, input.TargetIds, out targetApproximate);

            var baseCount = input.BaseIds.Length;
            var baseIndex = 0;
            var sourceIndex = 0;
            var targetIndex = 0;

            while (true)
            {
                // Prossimo punto fermo: riga della base allineata sia nel source sia nel target.
                var sync = baseIndex;
                while (sync < baseCount && (sourceMatch[sync] < 0 || targetMatch[sync] < 0))
                    sync++;

                if (sync == baseCount)
                {
                    // Coda finale dopo l'ultimo punto fermo.
                    AddChunk(input, baseIndex, baseCount, sourceIndex, input.SourceIds.Length, targetIndex, input.TargetIds.Length);
                    break;
                }

                var sourceSync = sourceMatch[sync];
                var targetSync = targetMatch[sync];
                AddChunk(input, baseIndex, sync, sourceIndex, sourceSync, targetIndex, targetSync);

                // Tratto stabile: righe consecutive allineate in tutti e tre i lati.
                var end = sync + 1;
                while (end < baseCount
                       && sourceMatch[end] == sourceSync + (end - sync)
                       && targetMatch[end] == targetSync + (end - sync))
                {
                    end++;
                }

                var length = end - sync;
                var targetSlice = Slice(input.TargetLines, targetSync, targetSync + length);
                input.Blocks.Add(new MergeBlock(MergeBlockKind.Unchanged,
                    Slice(input.BaseLines, sync, end),
                    Slice(input.SourceLines, sourceSync, sourceSync + length),
                    targetSlice,
                    targetSlice));

                baseIndex = end;
                sourceIndex = sourceSync + length;
                targetIndex = targetSync + length;
            }

            var newLine = DetectNewLine(input.TargetLines)
                          ?? DetectNewLine(input.SourceLines)
                          ?? DetectNewLine(input.BaseLines)
                          ?? DefaultNewLine;

            return new ThreeWayMergeResult(input.Blocks.AsReadOnly(), newLine, input.SourceLineTerminator,
                sourceApproximate || targetApproximate);
        }

        // Compone il testo: i blocchi non in conflitto con le ResultLines, ogni Conflict come
        // "<<<<<<< source / righe source / ======= / righe target / >>>>>>> target" con marker lunghi
        // result.MarkerSize. Una riga senza terminatore riceve NewLine se dopo di lei viene ancora
        // qualcosa (mai due righe incollate).
        public static string BuildTextWithMarkers(ThreeWayMergeResult result, string sourceLabel, string targetLabel)
        {
            if (result == null)
                throw new ArgumentNullException("result");

            var newLine = string.IsNullOrEmpty(result.NewLine) ? DefaultNewLine : result.NewLine;
            var markerSize = Math.Max(DefaultMarkerSize, result.MarkerSize);
            var sourceMarker = new string('<', markerSize) + " " + CleanLabel(sourceLabel) + newLine;
            var separator = new string('=', markerSize) + newLine;
            var targetMarker = new string('>', markerSize) + " " + CleanLabel(targetLabel) + newLine;

            var composer = new TextComposer(newLine);
            foreach (var block in result.Blocks)
            {
                if (block == null)
                    continue;

                if (block.Kind != MergeBlockKind.Conflict)
                {
                    composer.AppendLines(block.ResultLines);
                    continue;
                }

                composer.AppendLine(sourceMarker);
                composer.AppendLines(WithTerminator(block.SourceLines, result.SourceLineTerminator));
                composer.AppendLine(separator);
                composer.AppendLines(block.TargetLines);
                composer.AppendLine(targetMarker);
            }

            return composer.ToString();
        }

        // Marker di 7 caratteri (DefaultMarkerSize); vedi l'overload con markerSize.
        public static IReadOnlyList<ConflictRegion> FindConflictRegions(string text)
        {
            return FindConflictRegions(text, DefaultMarkerSize);
        }

        // Trova le regioni ben formate con marker lunghi markerSize: riga di markerSize '<' seguiti da uno
        // spazio, poi una riga di esattamente markerSize '=' (terminatore escluso), poi una riga di
        // markerSize '>' seguiti da uno spazio. Le righe simili ma di un'altra lunghezza sono testo.
        // Regole per i marker malformati (ignorati):
        // - un nuovo "<<<<<<< " prima della chiusura abbandona la regione aperta e ne apre un'altra;
        // - un ">>>>>>> " prima del separatore abbandona la regione aperta;
        // - una regione mai chiusa non viene restituita.
        // Un secondo "=======" dopo il separatore fa parte del testo target.
        public static IReadOnlyList<ConflictRegion> FindConflictRegions(string text, int markerSize)
        {
            if (markerSize < DefaultMarkerSize)
                throw new ArgumentOutOfRangeException("markerSize");

            var regions = new List<ConflictRegion>();
            if (string.IsNullOrEmpty(text))
                return regions.AsReadOnly();

            // 0 = fuori, 1 = parte source (dopo "<<<<<<<"), 2 = parte target (dopo "=======").
            var state = 0;
            var regionStart = 0;
            var regionLine = 0;
            var sourceStart = 0;
            var sourceEnd = 0;
            var targetStart = 0;

            var position = 0;
            var lineIndex = 0;
            while (position < text.Length)
            {
                int contentLength;
                int lineLength;
                MeasureLine(text, position, out contentLength, out lineLength);
                var lineStart = position;
                var lineEnd = position + lineLength;

                if (IsMarkerLine(text, lineStart, contentLength, '<', markerSize))
                {
                    state = 1;
                    regionStart = lineStart;
                    regionLine = lineIndex;
                    sourceStart = lineEnd;
                }
                else if (state == 1 && IsMarkerLine(text, lineStart, contentLength, '=', markerSize))
                {
                    state = 2;
                    sourceEnd = lineStart;
                    targetStart = lineEnd;
                }
                else if (state == 1 && IsMarkerLine(text, lineStart, contentLength, '>', markerSize))
                {
                    state = 0;
                }
                else if (state == 2 && IsMarkerLine(text, lineStart, contentLength, '>', markerSize))
                {
                    regions.Add(new ConflictRegion(regions.Count,
                        regionStart,
                        lineEnd - regionStart,
                        regionLine,
                        text.Substring(sourceStart, sourceEnd - sourceStart),
                        text.Substring(targetStart, lineStart - targetStart)));
                    state = 0;
                }

                position = lineEnd;
                lineIndex++;
            }

            return regions.AsReadOnly();
        }

        // Sostituisce l'intera regione (marker compresi) con il testo scelto. La regione deve venire da
        // FindConflictRegions sullo STESSO testo (altrimenti ArgumentException). Il testo inserito non
        // viene mai incollato alla riga successiva: se non finisce con un a-capo e dopo c'e' altro testo,
        // si aggiunge l'a-capo prevalente del testo.
        public static string ResolveRegion(string text, ConflictRegion region, ConflictChoice choice)
        {
            if (text == null)
                throw new ArgumentNullException("text");
            if (region == null)
                throw new ArgumentNullException("region");
            if (region.Start < 0 || region.Length < 0 || region.Start > text.Length - region.Length)
                throw new ArgumentOutOfRangeException("region", "The conflict region is outside the text.");
            // Qualsiasi lunghezza dei marker (>= 7): basta che la regione inizi con una riga di '<'.
            if (region.Length < SourceMarkerPrefix.Length
                || string.CompareOrdinal(text, region.Start, SourceMarkerPrefix, 0, DefaultMarkerSize) != 0)
                throw new ArgumentException("The conflict region does not start with a conflict marker: the text has changed.", "region");

            var newLine = DetectNewLine(text) ?? DefaultNewLine;

            string replacement;
            switch (choice)
            {
                case ConflictChoice.TakeSource:
                    replacement = region.SourceText;
                    break;
                case ConflictChoice.TakeTarget:
                    replacement = region.TargetText;
                    break;
                case ConflictChoice.TakeBoth:
                    replacement = JoinWithoutGluing(region.SourceText, region.TargetText, newLine);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("choice");
            }

            var after = region.Start + region.Length;
            var builder = new StringBuilder(text.Length + newLine.Length);
            builder.Append(text, 0, region.Start);
            builder.Append(replacement);
            if (replacement.Length > 0 && !EndsWithLineBreak(replacement) && after < text.Length)
                builder.Append(newLine);
            builder.Append(text, after, text.Length - after);
            return builder.ToString();
        }

        public static bool HasConflictMarkers(string text)
        {
            return FindConflictRegions(text).Count > 0;
        }

        // Righe di inizio (e numero di righe) di ogni blocco Conflict nel source e nel target: somma
        // delle SourceLines/TargetLines dei blocchi precedenti. I blocchi coprono i due file per intero e
        // in ordine (anche prefissi e suffissi comuni tolti dal conflitto, che sono blocchi BothSame).
        // L'elemento i corrisponde alla regione i di FindConflictRegions sul testo di BuildTextWithMarkers.
        public static IReadOnlyList<ConflictLocation> GetConflictLocations(ThreeWayMergeResult result)
        {
            if (result == null)
                throw new ArgumentNullException("result");

            var locations = new List<ConflictLocation>();
            var sourceLine = 0;
            var targetLine = 0;
            foreach (var block in result.Blocks)
            {
                if (block == null)
                    continue;

                if (block.Kind == MergeBlockKind.Conflict)
                    locations.Add(new ConflictLocation(locations.Count, sourceLine, block.SourceLines.Count,
                        targetLine, block.TargetLines.Count, block.SourceLines, block.TargetLines));

                sourceLine += block.SourceLines.Count;
                targetLine += block.TargetLines.Count;
            }
            return locations.AsReadOnly();
        }

        // ------------------------------------------------------------------------------------------
        // Classificazione dei tratti
        // ------------------------------------------------------------------------------------------

        // Tratto instabile: base [b0,b1), source [s0,s1), target [t0,t1).
        private static void AddChunk(MergeInput input, int b0, int b1, int s0, int s1, int t0, int t1)
        {
            if (b0 == b1 && s0 == s1 && t0 == t1)
                return;

            var sourceIsBase = SameContent(input.SourceIds, s0, s1, input.BaseIds, b0, b1);
            var targetIsBase = SameContent(input.TargetIds, t0, t1, input.BaseIds, b0, b1);

            if (sourceIsBase && targetIsBase)
            {
                // Contenuto uguale ma allineato diversamente (righe ripetute): nessuna modifica.
                AddBlock(input, MergeBlockKind.Unchanged, b0, b1, s0, s1, t0, t1, false);
                return;
            }

            if (targetIsBase)
            {
                AddBlock(input, MergeBlockKind.SourceOnly, b0, b1, s0, s1, t0, t1, true);
                return;
            }

            if (sourceIsBase)
            {
                AddBlock(input, MergeBlockKind.TargetOnly, b0, b1, s0, s1, t0, t1, false);
                return;
            }

            if (SameContent(input.SourceIds, s0, s1, input.TargetIds, t0, t1))
            {
                AddBlock(input, MergeBlockKind.BothSame, b0, b1, s0, s1, t0, t1, false);
                return;
            }

            // Conflitto vero. Le righe iniziali e finali identiche nei due lati si tolgono dal conflitto
            // (BothSame senza righe di base) per mostrare all'utente solo cio' che differisce davvero.
            var prefix = 0;
            while (s0 + prefix < s1 && t0 + prefix < t1 && input.SourceIds[s0 + prefix] == input.TargetIds[t0 + prefix])
                prefix++;

            var suffix = 0;
            while (s1 - suffix - 1 >= s0 + prefix && t1 - suffix - 1 >= t0 + prefix
                   && input.SourceIds[s1 - suffix - 1] == input.TargetIds[t1 - suffix - 1])
                suffix++;

            if (prefix > 0)
                AddBlock(input, MergeBlockKind.BothSame, b0, b0, s0, s0 + prefix, t0, t0 + prefix, false);

            AddBlock(input, MergeBlockKind.Conflict, b0, b1, s0 + prefix, s1 - suffix, t0 + prefix, t1 - suffix, false);

            if (suffix > 0)
                AddBlock(input, MergeBlockKind.BothSame, b1, b1, s1 - suffix, s1, t1 - suffix, t1, false);
        }

        private static void AddBlock(MergeInput input, MergeBlockKind kind,
            int b0, int b1, int s0, int s1, int t0, int t1, bool resultFromSource)
        {
            var sourceSlice = Slice(input.SourceLines, s0, s1);
            var targetSlice = Slice(input.TargetLines, t0, t1);
            IReadOnlyList<string> result = null;
            if (kind != MergeBlockKind.Conflict)
                result = resultFromSource ? WithTerminator(sourceSlice, input.SourceLineTerminator) : targetSlice;

            input.Blocks.Add(new MergeBlock(kind, Slice(input.BaseLines, b0, b1), sourceSlice, targetSlice, result));
        }

        private static bool SameContent(int[] left, int l0, int l1, int[] right, int r0, int r1)
        {
            if (l1 - l0 != r1 - r0)
                return false;

            for (var i = 0; i < l1 - l0; i++)
            {
                if (left[l0 + i] != right[r0 + i])
                    return false;
            }

            return true;
        }

        private static IReadOnlyList<string> Slice(IReadOnlyList<string> lines, int start, int end)
        {
            var count = end - start;
            if (count <= 0)
                return NoLines;

            var copy = new string[count];
            for (var i = 0; i < count; i++)
                copy[i] = lines[start + i];
            return new ReadOnlyCollection<string>(copy);
        }

        // ------------------------------------------------------------------------------------------
        // Diff (Myers O(ND), spazio lineare)
        // ------------------------------------------------------------------------------------------

        // Lo stesso diff del merge applicato a due elenchi di righe (confronto sul contenuto senza
        // terminatore): per ogni riga di a l'indice della riga di b a cui e' allineata (-1 = cancellata o
        // modificata), indici strettamente crescenti. Serve alle regole di merge (MergePolicyEngine).
        internal static int[] AlignLines(IReadOnlyList<string> a, IReadOnlyList<string> b, out bool approximate)
        {
            var input = new MergeInput(a ?? NoLines, b ?? NoLines, NoLines);
            return Diff(input.BaseIds, input.SourceIds, out approximate);
        }

        // Ritorna, per ogni riga di a, l'indice della riga di b a cui e' allineata (-1 = cancellata o
        // modificata). Gli indici allineati sono strettamente crescenti. approximate: su qualche tratto con
        // righe in comune l'allineamento fine non e' stato trovato (vedi DiffRange).
        private static int[] Diff(int[] a, int[] b, out bool approximate)
        {
            var match = new int[a.Length];
            for (var i = 0; i < match.Length; i++)
                match[i] = -1;

            var budget = new DiffBudget(MaxDiffWork);
            DiffRange(a, 0, a.Length, b, 0, b.Length, match, budget, true);
            approximate = budget.Approximate;
            return match;
        }

        // allowAnchors: se la bisezione rinuncia si puo' ripiegare sulle righe uniche (una volta sola per
        // tratto: i tratti fra due ancore non ripiegano di nuovo, cosi' la ricorsione resta corta).
        private static void DiffRange(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, int[] match, DiffBudget budget,
            bool allowAnchors)
        {
            // Prefisso e suffisso comuni: sono gli unici punti in cui si registrano righe uguali (anche
            // il "middle snake" della bisezione finisce in coda alla meta' sinistra e viene preso qui).
            while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
            {
                match[aLo] = bLo;
                aLo++;
                bLo++;
            }

            while (aLo < aHi && bLo < bHi && a[aHi - 1] == b[bHi - 1])
            {
                aHi--;
                bHi--;
                match[aHi] = bHi;
            }

            if (aLo == aHi || bLo == bHi)
                return; // solo cancellazioni o solo inserimenti

            int splitX;
            int splitY;
            if (Bisect(a, aLo, aHi, b, bLo, bHi, budget, out splitX, out splitY))
            {
                DiffRange(a, aLo, aLo + splitX, b, bLo, bLo + splitY, match, budget, allowAnchors);
                DiffRange(a, aLo + splitX, aHi, b, bLo + splitY, bHi, match, budget, allowAnchors);
                return;
            }

            // La bisezione non ha trovato il taglio. Senza righe in comune il tratto e' davvero un'unica
            // modifica (e' il caso normale di una sostituzione): nessuna perdita.
            if (!HaveCommonLine(a, aLo, aHi, b, bLo, bHi))
                return;

            // Ha rinunciato per soglia o budget (es. una riga cambiata ogni cinque su un file enorme):
            // come nel "patience diff", le righe che compaiono una sola volta in entrambi i lati fanno da
            // punti fermi, cosi' le righe rimaste uguali non finiscono dentro un'unica modifica gigante che
            // nasconderebbe (e farebbe confliggere) le modifiche piccole dell'altro lato.
            if (allowAnchors && AlignOnUniqueLines(a, aLo, aHi, b, bLo, bHi, match, budget))
                return;

            // Degrado: tutta la parte centrale e' un'unica modifica, anche se ha righe in comune.
            budget.Approximate = true;
        }

        // Allinea le righe uniche in entrambi i tratti (sottosequenza crescente piu' lunga delle loro
        // posizioni) e confronta con DiffRange i tratti in mezzo. False se non c'e' nessuna riga unica comune.
        private static bool AlignOnUniqueLines(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, int[] match, DiffBudget budget)
        {
            // Per ogni contenuto: occorrenze in a, ultima posizione in a, occorrenze in b, ultima posizione in b.
            var counts = new Dictionary<int, int[]>();
            for (var i = aLo; i < aHi; i++)
            {
                int[] entry;
                if (!counts.TryGetValue(a[i], out entry))
                {
                    entry = new int[4];
                    counts.Add(a[i], entry);
                }
                entry[0]++;
                entry[1] = i;
            }
            for (var j = bLo; j < bHi; j++)
            {
                int[] entry;
                if (counts.TryGetValue(b[j], out entry))
                {
                    entry[2]++;
                    entry[3] = j;
                }
            }

            // Candidati nell'ordine di a, con la loro posizione in b.
            var candidateA = new List<int>();
            var candidateB = new List<int>();
            for (var i = aLo; i < aHi; i++)
            {
                var entry = counts[a[i]];
                if (entry[0] == 1 && entry[2] == 1)
                {
                    candidateA.Add(i);
                    candidateB.Add(entry[3]);
                }
            }
            if (candidateA.Count == 0)
                return false;

            var previousA = aLo;
            var previousB = bLo;
            foreach (var index in LongestIncreasingSubsequence(candidateB))
            {
                var anchorA = candidateA[index];
                var anchorB = candidateB[index];
                DiffRange(a, previousA, anchorA, b, previousB, anchorB, match, budget, false);
                match[anchorA] = anchorB;
                previousA = anchorA + 1;
                previousB = anchorB + 1;
            }
            DiffRange(a, previousA, aHi, b, previousB, bHi, match, budget, false);
            return true;
        }

        // Indici (in ordine) della sottosequenza strettamente crescente piu' lunga di values
        // (patience sorting, O(k log k)).
        private static List<int> LongestIncreasingSubsequence(List<int> values)
        {
            // tails[k]: indice dell'elemento finale piu' piccolo di una sottosequenza lunga k + 1.
            var tails = new List<int>();
            var previous = new int[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                var low = 0;
                var high = tails.Count;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (values[tails[middle]] < value)
                        low = middle + 1;
                    else
                        high = middle;
                }
                previous[i] = low > 0 ? tails[low - 1] : -1;
                if (low == tails.Count)
                    tails.Add(i);
                else
                    tails[low] = i;
            }

            var result = new List<int>(tails.Count);
            for (var k = tails.Count == 0 ? -1 : tails[tails.Count - 1]; k >= 0; k = previous[k])
                result.Add(k);
            result.Reverse();
            return result;
        }

        private static bool HaveCommonLine(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi)
        {
            // Tratti piccoli (il caso frequente): confronto diretto, senza allocazioni.
            if ((long)(aHi - aLo) * (bHi - bLo) <= 256)
            {
                for (var i = aLo; i < aHi; i++)
                {
                    for (var j = bLo; j < bHi; j++)
                    {
                        if (a[i] == b[j])
                            return true;
                    }
                }
                return false;
            }

            var lines = new HashSet<int>();
            for (var i = aLo; i < aHi; i++)
                lines.Add(a[i]);
            for (var j = bLo; j < bHi; j++)
            {
                if (lines.Contains(b[j]))
                    return true;
            }
            return false;
        }

        // Cerca il "middle snake" (Myers 1986, sez. 4b) procedendo in avanti e all'indietro insieme.
        // Ritorna false se non lo trova entro MaxEditDistance o entro il budget di lavoro.
        private static bool Bisect(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, DiffBudget budget,
            out int splitX, out int splitY)
        {
            splitX = 0;
            splitY = 0;
            if (budget.Remaining <= 0)
                return false;

            var n = aHi - aLo;
            var m = bHi - bLo;
            var maxD = Math.Min((n + m + 1) / 2, (MaxEditDistance + 1) / 2);
            // Un margine di una cella per lato: gli indici usati restano in [vOffset - maxD, vOffset + maxD].
            var vOffset = maxD + 1;
            var vLength = 2 * maxD + 3;
            var v1 = new int[vLength];
            var v2 = new int[vLength];
            for (var i = 0; i < vLength; i++)
            {
                v1[i] = -1;
                v2[i] = -1;
            }
            v1[vOffset + 1] = 0;
            v2[vOffset + 1] = 0;

            var delta = n - m;
            // Con delta dispari la sovrapposizione si scopre andando avanti, con delta pari all'indietro.
            var front = delta % 2 != 0;
            var k1Start = 0;
            var k1End = 0;
            var k2Start = 0;
            var k2End = 0;

            for (var d = 0; d < maxD; d++)
            {
                if (budget.Remaining <= 0)
                    return false;

                long work = 0;

                // Passo in avanti.
                for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
                {
                    var k1Offset = vOffset + k1;
                    int x1;
                    if (k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1]))
                        x1 = v1[k1Offset + 1];
                    else
                        x1 = v1[k1Offset - 1] + 1;

                    var y1 = x1 - k1;
                    var snakeStart = x1;
                    while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                    {
                        x1++;
                        y1++;
                    }
                    work += 1 + (x1 - snakeStart);
                    v1[k1Offset] = x1;

                    if (x1 > n)
                    {
                        k1End += 2; // uscito dal bordo destro
                    }
                    else if (y1 > m)
                    {
                        k1Start += 2; // uscito dal bordo inferiore
                    }
                    else if (front)
                    {
                        var k2Offset = vOffset + delta - k1;
                        if (k2Offset >= 0 && k2Offset < vLength && v2[k2Offset] != -1)
                        {
                            var x2 = n - v2[k2Offset];
                            if (x1 >= x2)
                            {
                                splitX = x1;
                                splitY = y1;
                                return IsUsefulSplit(splitX, splitY, n, m);
                            }
                        }
                    }
                }

                // Passo all'indietro (coordinate misurate dalla fine).
                for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
                {
                    var k2Offset = vOffset + k2;
                    int x2;
                    if (k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1]))
                        x2 = v2[k2Offset + 1];
                    else
                        x2 = v2[k2Offset - 1] + 1;

                    var y2 = x2 - k2;
                    var snakeStart = x2;
                    while (x2 < n && y2 < m && a[aHi - x2 - 1] == b[bHi - y2 - 1])
                    {
                        x2++;
                        y2++;
                    }
                    work += 1 + (x2 - snakeStart);
                    v2[k2Offset] = x2;

                    if (x2 > n)
                    {
                        k2End += 2;
                    }
                    else if (y2 > m)
                    {
                        k2Start += 2;
                    }
                    else if (!front)
                    {
                        var k1Offset = vOffset + delta - k2;
                        if (k1Offset >= 0 && k1Offset < vLength && v1[k1Offset] != -1)
                        {
                            var x1 = v1[k1Offset];
                            var y1 = vOffset + x1 - k1Offset;
                            var mirroredX2 = n - x2;
                            if (x1 >= mirroredX2)
                            {
                                splitX = x1;
                                splitY = y1;
                                return IsUsefulSplit(splitX, splitY, n, m);
                            }
                        }
                    }
                }

                budget.Remaining -= work;
            }

            return false;
        }

        // Un punto di taglio deve stare nel rettangolo e far avanzare la ricorsione.
        private static bool IsUsefulSplit(int x, int y, int n, int m)
        {
            if (x < 0 || y < 0 || x > n || y > m)
                return false;
            if ((x == 0 && y == 0) || (x == n && y == m))
                return false;
            return true;
        }

        private sealed class DiffBudget
        {
            public DiffBudget(long remaining)
            {
                Remaining = remaining;
            }

            public long Remaining;

            // Qualche tratto con righe in comune e' stato trattato come un'unica modifica.
            public bool Approximate;
        }

        // ------------------------------------------------------------------------------------------
        // Righe, terminatori, a-capo
        // ------------------------------------------------------------------------------------------

        private static int TerminatorLength(string line)
        {
            var length = line.Length;
            if (length >= 2 && line[length - 2] == '\r' && line[length - 1] == '\n')
                return 2;
            if (length >= 1 && (line[length - 1] == '\n' || line[length - 1] == '\r'))
                return 1;
            return 0;
        }

        private static bool EndsWithLineBreak(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            var last = text[text.Length - 1];
            return last == '\n' || last == '\r';
        }

        private static string JoinWithoutGluing(string first, string second, string newLine)
        {
            if (string.IsNullOrEmpty(first))
                return second ?? string.Empty;
            if (string.IsNullOrEmpty(second))
                return first;
            return EndsWithLineBreak(first) ? first + second : first + newLine + second;
        }

        private static string CleanLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return string.Empty;
            // Un'etichetta su piu' righe romperebbe il marker.
            return label.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string DetectNewLine(IReadOnlyList<string> lines)
        {
            var crlf = 0;
            var lf = 0;
            var cr = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var terminator = TerminatorLength(line);
                if (terminator == 2)
                    crlf++;
                else if (terminator == 1 && line[line.Length - 1] == '\n')
                    lf++;
                else if (terminator == 1)
                    cr++;
            }

            return PickNewLine(crlf, lf, cr);
        }

        private static string DetectNewLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var crlf = 0;
            var lf = 0;
            var cr = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++;
                    }
                    else
                    {
                        cr++;
                    }
                }
                else if (c == '\n')
                {
                    lf++;
                }
            }

            return PickNewLine(crlf, lf, cr);
        }

        // In parita' vince "\r\n", poi "\n", poi "\r". Null se non c'e' nessun a-capo.
        private static string PickNewLine(int crlf, int lf, int cr)
        {
            if (crlf == 0 && lf == 0 && cr == 0)
                return null;
            if (crlf >= lf && crlf >= cr)
                return "\r\n";
            if (lf >= cr)
                return "\n";
            return "\r";
        }

        // Misura la riga che inizia in start: lunghezza del contenuto e lunghezza totale (terminatore incluso).
        private static void MeasureLine(string text, int start, out int contentLength, out int lineLength)
        {
            var i = start;
            while (i < text.Length && text[i] != '\r' && text[i] != '\n')
                i++;
            contentLength = i - start;

            if (i < text.Length)
                i += (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
            lineLength = i - start;
        }

        // Riga marker lunga size: '<' e '>' ripetuti size volte e poi uno spazio (l'etichetta e'
        // facoltativa); '=' ripetuto esattamente size volte.
        private static bool IsMarkerLine(string text, int lineStart, int contentLength, char marker, int size)
        {
            if (marker == '=')
            {
                if (contentLength != size)
                    return false;
            }
            else if (contentLength < size + 1 || text[lineStart + size] != ' ')
            {
                return false;
            }

            for (var i = 0; i < size; i++)
            {
                if (text[lineStart + i] != marker)
                    return false;
            }
            return true;
        }

        // Se la riga (terminatore escluso) e' essa stessa un marker valido di qualche lunghezza >= 7,
        // ritorna quella lunghezza; altrimenti 0.
        private static int MarkerLikeSize(string line)
        {
            var contentLength = line.Length - TerminatorLength(line);
            if (contentLength < DefaultMarkerSize)
                return 0;

            var marker = line[0];
            if (marker != '<' && marker != '=' && marker != '>')
                return 0;

            var run = 1;
            while (run < contentLength && line[run] == marker)
                run++;
            if (run < DefaultMarkerSize)
                return 0;

            return IsMarkerLine(line, 0, contentLength, marker, run) ? run : 0;
        }

        // La lunghezza piu' corta (>= 7) che nessuna riga del testo composto usa gia' come marker: con
        // quella, le righe "=======" o "<<<<<<< x" del file restano testo e le regioni si ritrovano esatte.
        internal static int ChooseMarkerSize(IReadOnlyList<MergeBlock> blocks)
        {
            HashSet<int> taken = null;
            foreach (var block in blocks)
            {
                if (block == null)
                    continue;

                var sides = block.Kind == MergeBlockKind.Conflict
                    ? new[] { block.SourceLines, block.TargetLines }
                    : new[] { block.ResultLines };
                foreach (var lines in sides)
                {
                    if (lines == null)
                        continue;
                    for (var i = 0; i < lines.Count; i++)
                    {
                        var size = MarkerLikeSize(lines[i]);
                        if (size == 0)
                            continue;
                        if (taken == null)
                            taken = new HashSet<int>();
                        taken.Add(size);
                    }
                }
            }

            var markerSize = DefaultMarkerSize;
            while (taken != null && taken.Contains(markerSize))
                markerSize++;
            return markerSize;
        }

        // Le righe con il terminatore sostituito da terminator (quelle senza terminatore restano cosi').
        // terminator null: le righe tali e quali.
        internal static IReadOnlyList<string> WithTerminator(IReadOnlyList<string> lines, string terminator)
        {
            if (terminator == null || lines == null || lines.Count == 0)
                return lines;

            string[] copy = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var length = TerminatorLength(line);
                if (length == 0
                    || (length == terminator.Length && string.CompareOrdinal(line, line.Length - length, terminator, 0, length) == 0))
                    continue;

                if (copy == null)
                {
                    copy = new string[lines.Count];
                    for (var j = 0; j < lines.Count; j++)
                        copy[j] = lines[j];
                }
                copy[i] = line.Substring(0, line.Length - length) + terminator;
            }
            return copy == null ? lines : new ReadOnlyCollection<string>(copy);
        }

        // L'unico a-capo usato dalle righe (tutte quelle che ne hanno uno); null se nessuno o se misti.
        private static string DetectSingleNewLine(IReadOnlyList<string> lines)
        {
            string found = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var length = TerminatorLength(line);
                if (length == 0)
                    continue;

                string terminator;
                if (length == 2)
                    terminator = "\r\n";
                else if (line[line.Length - 1] == '\n')
                    terminator = "\n";
                else
                    terminator = "\r";

                if (found == null)
                    found = terminator;
                else if (!string.Equals(found, terminator, StringComparison.Ordinal))
                    return null;
            }
            return found;
        }

        // ------------------------------------------------------------------------------------------
        // Stato di un merge
        // ------------------------------------------------------------------------------------------

        private sealed class MergeInput
        {
            public MergeInput(IReadOnlyList<string> baseLines, IReadOnlyList<string> sourceLines, IReadOnlyList<string> targetLines)
            {
                BaseLines = baseLines;
                SourceLines = sourceLines;
                TargetLines = targetLines;

                // Ogni contenuto di riga (senza terminatore) diventa un intero: il diff confronta interi.
                var ids = new Dictionary<string, int>(StringComparer.Ordinal);
                BaseIds = ToIds(baseLines, ids);
                SourceIds = ToIds(sourceLines, ids);
                TargetIds = ToIds(targetLines, ids);

                Blocks = new List<MergeBlock>();
            }

            // Vedi ThreeWayMergeResult.SourceLineTerminator.
            public string SourceLineTerminator { get; set; }

            public IReadOnlyList<string> BaseLines { get; private set; }

            public IReadOnlyList<string> SourceLines { get; private set; }

            public IReadOnlyList<string> TargetLines { get; private set; }

            public int[] BaseIds { get; private set; }

            public int[] SourceIds { get; private set; }

            public int[] TargetIds { get; private set; }

            public List<MergeBlock> Blocks { get; private set; }

            private static int[] ToIds(IReadOnlyList<string> lines, Dictionary<string, int> ids)
            {
                var result = new int[lines.Count];
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var content = line.Substring(0, line.Length - TerminatorLength(line));
                    int id;
                    if (!ids.TryGetValue(content, out id))
                    {
                        id = ids.Count;
                        ids.Add(content, id);
                    }
                    result[i] = id;
                }
                return result;
            }
        }

        // Accumula righe senza mai incollarle: se l'ultima riga scritta non aveva terminatore e ne arriva
        // un'altra, prima si scrive l'a-capo.
        private sealed class TextComposer
        {
            private readonly StringBuilder _builder = new StringBuilder();
            private readonly string _newLine;
            private bool _lastLineOpen;

            public TextComposer(string newLine)
            {
                _newLine = newLine;
            }

            public void AppendLine(string line)
            {
                if (string.IsNullOrEmpty(line))
                    return;

                if (_lastLineOpen)
                    _builder.Append(_newLine);

                _builder.Append(line);
                _lastLineOpen = TerminatorLength(line) == 0;
            }

            public void AppendLines(IReadOnlyList<string> lines)
            {
                if (lines == null)
                    return;

                for (var i = 0; i < lines.Count; i++)
                    AppendLine(lines[i]);
            }

            public override string ToString()
            {
                return _builder.ToString();
            }
        }
    }
}
