using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AutoMerge
{
    // =============================================================================================
    // Regole di merge (policy) del "Merge from Task". PURO: nessuna dipendenza da TFVC o da VS.
    //
    // Due tipi di regole, generiche (nessuna conosce il contenuto di un progetto in particolare):
    // - regole per PERCORSO (MergePathRule): per gli item il cui path relativo alla radice del ramo
    //   corrisponde a un glob si decide Merge (default), Discard (il merge si registra ma resta il
    //   contenuto del target, come "tf merge /discard") o Skip (l'item non si fonde);
    // - regole per RIGA (MergeLineRule): nei file che corrispondono, le righe (o i blocchi di righe)
    //   che soddisfano una regex sono PROTETTE: nel risultato restano quelle del target, le modifiche
    //   della sorgente su di esse si scartano e si elencano (promemoria delle cose da fare a mano).
    //
    // Le regole arrivano da due documenti: quello di TEAM (file nella radice del ramo di destinazione)
    // e quello PERSONALE. Il personale vince sempre: le sue regole vengono prima e una sua regola con lo
    // stesso Id di una regola di team la SOSTITUISCE (anche solo per disattivarla).
    //
    // Principio: nel dubbio non si indovina. Una regola non valida finisce in Errors (e chi usa la
    // policy non deve applicarla); il motore, se riceve una regola non valida, lancia un'eccezione.
    // =============================================================================================

    public enum MergePolicyAction
    {
        // fondere normalmente (default)
        Merge,

        // registrare il merge ma tenere il contenuto del target (tf merge /discard)
        Discard,

        // non fondere
        Skip
    }

    public sealed class MergePathRule
    {
        // una regola nuova e' attiva (come una regola letta da JSON senza "enabled")
        public MergePathRule()
        {
            Enabled = true;
        }

        public string Id { get; set; }

        // glob sul path relativo alla radice del ramo (vedi MergePolicyEngine.GlobMatch); piu' glob
        // separati da ';' (in TFVC ';' non puo' comparire in un nome)
        public string Pattern { get; set; }

        public MergePolicyAction Action { get; set; }

        public string Description { get; set; }

        public bool Enabled { get; set; }
    }

    // Righe PROTETTE: nei file che corrispondono a FilePattern restano quelle del target.
    // Una riga e' protetta se soddisfa LinePattern, oppure se sta in un blocco: dalla riga che soddisfa
    // BlockStartPattern alla prima riga che soddisfa BlockEndPattern cercata A PARTIRE dalla riga di
    // inizio stessa (inclusa: un blocco puo' stare su una riga sola). Un blocco senza fine non e' un
    // blocco. Se BlockContainsPattern e' valorizzato, il blocco e' protetto solo se una sua riga lo
    // soddisfa. Le regex (sintassi .NET) si applicano alla riga senza terminatore e sono sensibili alle
    // maiuscole: per ignorarle si usa "(?i)" all'inizio del pattern.
    public sealed class MergeLineRule
    {
        public MergeLineRule()
        {
            Enabled = true;
        }

        public string Id { get; set; }

        public string FilePattern { get; set; }

        public string LinePattern { get; set; }

        public string BlockStartPattern { get; set; }

        public string BlockEndPattern { get; set; }

        public string BlockContainsPattern { get; set; }

        public string Description { get; set; }

        public bool Enabled { get; set; }
    }

    public sealed class MergePolicyDocument
    {
        public MergePolicyDocument()
        {
            Version = MergePolicyEngine.CurrentVersion;
            PathRules = new List<MergePathRule>();
            LineRules = new List<MergeLineRule>();
        }

        public int Version { get; set; }

        public List<MergePathRule> PathRules { get; set; }

        public List<MergeLineRule> LineRules { get; set; }
    }

    public enum MergeRuleOrigin
    {
        Team,
        Personal
    }

    public sealed class EffectivePathRule
    {
        public EffectivePathRule(MergePathRule rule, MergeRuleOrigin origin, bool overridesTeamRule)
        {
            Rule = rule;
            Origin = origin;
            OverridesTeamRule = overridesTeamRule;
        }

        public MergePathRule Rule { get; }

        public MergeRuleOrigin Origin { get; }

        // regola personale che sostituisce una regola di team con lo stesso Id
        public bool OverridesTeamRule { get; }
    }

    public sealed class EffectiveLineRule
    {
        public EffectiveLineRule(MergeLineRule rule, MergeRuleOrigin origin, bool overridesTeamRule)
        {
            Rule = rule;
            Origin = origin;
            OverridesTeamRule = overridesTeamRule;
        }

        public MergeLineRule Rule { get; }

        public MergeRuleOrigin Origin { get; }

        public bool OverridesTeamRule { get; }
    }

    // Le regole in vigore: prima le personali (nel loro ordine), poi quelle di team. Una regola personale
    // con lo stesso Id (senza distinzione di maiuscole) di una di team la sostituisce: la regola di team
    // non compare piu' (con Enabled=false la personale serve a disattivarla). Le regole disattivate
    // restano nell'elenco (per mostrarle) ma non si applicano. Errors non vuoto = la policy non va
    // applicata.
    public sealed class EffectiveMergePolicy
    {
        public EffectiveMergePolicy(MergePolicyDocument team, MergePolicyDocument personal)
        {
            var errors = new List<string>();
            MergePolicyEngine.CheckDocument(team, MergeRuleOrigin.Team, errors);
            MergePolicyEngine.CheckDocument(personal, MergeRuleOrigin.Personal, errors);

            var pathRules = Combine(
                team == null ? null : team.PathRules,
                personal == null ? null : personal.PathRules,
                r => r.Id,
                (r, origin, overrides) => new EffectivePathRule(r, origin, overrides));
            var lineRules = Combine(
                team == null ? null : team.LineRules,
                personal == null ? null : personal.LineRules,
                r => r.Id,
                (r, origin, overrides) => new EffectiveLineRule(r, origin, overrides));

            // si controllano le regole attive in vigore: una regola di team rotta ma sostituita (o
            // disattivata) da una personale non blocca nessuno
            foreach (var rule in pathRules.Where(r => r.Rule.Enabled))
                MergePolicyEngine.CheckPathRule(rule, errors);
            foreach (var rule in lineRules.Where(r => r.Rule.Enabled))
                MergePolicyEngine.CheckLineRule(rule, errors);

            PathRules = pathRules.AsReadOnly();
            LineRules = lineRules.AsReadOnly();
            Errors = errors.AsReadOnly();
        }

        public IReadOnlyList<EffectivePathRule> PathRules { get; }

        public IReadOnlyList<EffectiveLineRule> LineRules { get; }

        public IReadOnlyList<string> Errors { get; }

        private static List<TEffective> Combine<TRule, TEffective>(
            IList<TRule> team,
            IList<TRule> personal,
            Func<TRule, string> idOf,
            Func<TRule, MergeRuleOrigin, bool, TEffective> create)
            where TRule : class
        {
            var teamRules = (team ?? new TRule[0]).Where(r => r != null).ToList();
            var personalRules = (personal ?? new TRule[0]).Where(r => r != null).ToList();
            var teamIds = new HashSet<string>(teamRules.Select(r => MergePolicyEngine.NormalizeId(idOf(r))).Where(id => id != null), StringComparer.OrdinalIgnoreCase);
            var personalIds = new HashSet<string>(personalRules.Select(r => MergePolicyEngine.NormalizeId(idOf(r))).Where(id => id != null), StringComparer.OrdinalIgnoreCase);

            var result = new List<TEffective>();
            foreach (var rule in personalRules)
            {
                var id = MergePolicyEngine.NormalizeId(idOf(rule));
                result.Add(create(rule, MergeRuleOrigin.Personal, id != null && teamIds.Contains(id)));
            }

            foreach (var rule in teamRules)
            {
                var id = MergePolicyEngine.NormalizeId(idOf(rule));
                if (id != null && personalIds.Contains(id))
                    continue;
                result.Add(create(rule, MergeRuleOrigin.Team, false));
            }

            return result;
        }
    }

    // Decisione per un path: la prima regola per percorso attiva che corrisponde vince (MatchedRule null
    // = nessuna regola: Merge). LineRules: le regole per riga attive del file, solo se l'azione e' Merge
    // (con Discard o Skip il contenuto non si fonde e le righe protette non servono).
    public sealed class PathDecision
    {
        public PathDecision(MergePolicyAction action, EffectivePathRule matchedRule, IReadOnlyList<EffectiveLineRule> lineRules)
        {
            Action = action;
            MatchedRule = matchedRule;
            LineRules = lineRules ?? new List<EffectiveLineRule>().AsReadOnly();
        }

        public MergePolicyAction Action { get; }

        public EffectivePathRule MatchedRule { get; }

        public IReadOnlyList<EffectiveLineRule> LineRules { get; }
    }

    // Una modifica della sorgente su righe protette che NON e' passata (promemoria per l'utente).
    // Numeri di riga 1-based (come nell'editor). SourceText null = la sorgente cancella; TargetText null
    // = nel target non c'e' niente di corrispondente (es. la sorgente aggiunge). I testi sono le righe
    // senza terminatore, unite da "\n".
    public sealed class ProtectedLineDifference
    {
        public ProtectedLineDifference(string ruleId, string sourceText, string targetText, int? sourceLine, int? targetLine, string summary)
        {
            RuleId = ruleId;
            SourceText = sourceText;
            TargetText = targetText;
            SourceLine = sourceLine;
            TargetLine = targetLine;
            Summary = summary;
        }

        public string RuleId { get; }

        public string SourceText { get; }

        public string TargetText { get; }

        public int? SourceLine { get; }

        public int? TargetLine { get; }

        public string Summary { get; }
    }

    public sealed class ProtectedMergeResult
    {
        public ProtectedMergeResult(string neutralizedSource, ThreeWayMergeResult merge, IReadOnlyList<ProtectedLineDifference> keptTargetDifferences)
        {
            if (merge == null)
                throw new ArgumentNullException("merge");
            NeutralizedSource = neutralizedSource ?? string.Empty;
            Merge = merge;
            KeptTargetDifferences = keptTargetDifferences ?? new List<ProtectedLineDifference>().AsReadOnly();
        }

        // la sorgente senza le sue modifiche alle righe protette
        public string NeutralizedSource { get; }

        // merge a 3 vie di base, sorgente neutralizzata e target
        public ThreeWayMergeResult Merge { get; }

        public IReadOnlyList<ProtectedLineDifference> KeptTargetDifferences { get; }

        public bool HasConflicts
        {
            get { return Merge.ConflictCount > 0; }
        }
    }

    public static class MergePolicyEngine
    {
        internal const int CurrentVersion = 1;

        // tetto al tempo di una singola regex su una riga: una regex patologica non blocca il merge
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        // oltre questa dimensione (unita' x unita') l'allineamento fine dentro un tratto modificato si
        // salta: cambia solo l'ordine delle righe tenute, non quali righe si tengono
        private const long MaxHunkCells = 1000000L;

        // quante righe/violazioni al massimo nei testi
        private const int MaxShownInText = 5;

        private static readonly IReadOnlyList<ProtectedLineDifference> NoDifferences = new List<ProtectedLineDifference>().AsReadOnly();

        private static readonly IReadOnlyList<string> NoStrings = new List<string>().AsReadOnly();

        // -----------------------------------------------------------------------------------------
        // Glob
        // -----------------------------------------------------------------------------------------

        // relativePath: path relativo alla radice del ramo ("." o "" = la radice), con '/' (anche '\' e'
        // accettato), senza distinzione di maiuscole. Nel pattern:
        // - "*" = qualsiasi sequenza dentro un nome, "?" = un carattere, "**" = zero o piu' cartelle;
        // - un pattern SENZA '/' vale per il nome in qualsiasi cartella ("*.csproj" = "**/*.csproj");
        // - un pattern con '/' in mezzo (o che inizia con '/') parte dalla radice del ramo;
        // - un pattern che finisce con '/' indica una cartella: la cartella e tutto il suo contenuto;
        //   "x/**" comprende anche la cartella x stessa;
        // - piu' pattern separati da ';': basta che ne corrisponda uno.
        public static bool GlobMatch(string pattern, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            var path = NormalizeRelativePath(relativePath);
            var pathSegments = path.Length == 0 ? new string[0] : path.Split('/');
            foreach (var part in pattern.Split(';'))
            {
                var segments = GlobSegments(part);
                if (segments == null)
                    continue;
                if (MatchSegments(segments, pathSegments))
                    return true;
            }

            return false;
        }

        // null = parte vuota (non corrisponde a niente)
        private static List<string> GlobSegments(string part)
        {
            var p = (part ?? string.Empty).Trim().Replace('\\', '/');
            if (p.Length == 0)
                return null;

            var anchored = false;
            if (p.StartsWith("./", StringComparison.Ordinal))
            {
                anchored = true;
                p = p.Substring(2);
            }
            if (p.StartsWith("/", StringComparison.Ordinal))
                anchored = true;
            p = p.Trim('/');
            var folder = part.Trim().Replace('\\', '/').EndsWith("/", StringComparison.Ordinal);
            if (p.Length == 0)
                return null;

            var segments = p.Split('/').Where(s => s.Length > 0).ToList();
            if (!anchored && segments.Count == 1)
                segments.Insert(0, "**");
            if (folder)
                segments.Add("**");

            // "**" consecutivi valgono uno; "**" dentro un nome ("a**b") vale "*"
            var result = new List<string>();
            foreach (var segment in segments)
            {
                var s = segment == "**" ? segment : segment.Replace("**", "*");
                if (s == "**" && result.Count > 0 && result[result.Count - 1] == "**")
                    continue;
                result.Add(s);
            }

            return result;
        }

        private static bool MatchSegments(List<string> pattern, string[] path)
        {
            // memo[pi, si]: 0 = non calcolato, 1 = si', 2 = no
            var memo = new byte[pattern.Count + 1, path.Length + 1];
            return MatchSegments(pattern, 0, path, 0, memo);
        }

        private static bool MatchSegments(List<string> pattern, int pi, string[] path, int si, byte[,] memo)
        {
            if (memo[pi, si] != 0)
                return memo[pi, si] == 1;

            bool result;
            if (pi == pattern.Count)
            {
                result = si == path.Length;
            }
            else if (pattern[pi] == "**")
            {
                result = false;
                for (var k = si; k <= path.Length && !result; k++)
                    result = MatchSegments(pattern, pi + 1, path, k, memo);
            }
            else
            {
                result = si < path.Length
                    && WildcardMatch(pattern[pi], path[si])
                    && MatchSegments(pattern, pi + 1, path, si + 1, memo);
            }

            memo[pi, si] = (byte)(result ? 1 : 2);
            return result;
        }

        // '*' = qualsiasi sequenza, '?' = un carattere; senza distinzione di maiuscole.
        private static bool WildcardMatch(string pattern, string name)
        {
            var p = 0;
            var n = 0;
            var star = -1;
            var starName = 0;
            while (n < name.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || SameChar(pattern[p], name[n])))
                {
                    p++;
                    n++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    starName = n;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    n = ++starName;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
                p++;
            return p == pattern.Length;
        }

        private static bool SameChar(char a, char b)
        {
            return a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            var path = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path.Substring(2);
            path = path.Trim('/');
            if (path == ".")
                return string.Empty;
            while (path.Contains("//"))
                path = path.Replace("//", "/");
            return path;
        }

        // -----------------------------------------------------------------------------------------
        // Decisione per path
        // -----------------------------------------------------------------------------------------

        public static PathDecision Decide(EffectiveMergePolicy policy, string relativePath)
        {
            if (policy == null)
                return new PathDecision(MergePolicyAction.Merge, null, null);

            EffectivePathRule matched = null;
            foreach (var rule in policy.PathRules)
            {
                if (rule != null && rule.Rule != null && rule.Rule.Enabled && GlobMatch(rule.Rule.Pattern, relativePath))
                {
                    matched = rule;
                    break;
                }
            }

            var action = matched == null ? MergePolicyAction.Merge : matched.Rule.Action;
            var lineRules = action != MergePolicyAction.Merge
                ? new List<EffectiveLineRule>()
                : policy.LineRules
                    .Where(r => r != null && r.Rule != null && r.Rule.Enabled && GlobMatch(r.Rule.FilePattern, relativePath))
                    .ToList();
            return new PathDecision(action, matched, lineRules.AsReadOnly());
        }

        // -----------------------------------------------------------------------------------------
        // Merge con righe protette
        // -----------------------------------------------------------------------------------------

        // 1. "Neutralizza" la sorgente: si allinea base -> sorgente (lo stesso diff di ThreeWayMerge); le
        //    INSERZIONI di righe protette nella sorgente si tolgono, le CANCELLAZIONI di righe protette
        //    della base si annullano (la riga della base resta); una sostituzione e' cancellazione +
        //    inserzione. Una riga e' protetta secondo il testo in cui sta (la base per le cancellazioni,
        //    la sorgente per le inserzioni). Un blocco protetto toccato solo in parte si tratta per intero
        //    (mai mezzo blocco: <Reference> senza </Reference>).
        // 2. ThreeWayMerge.Merge(base, sorgente neutralizzata, target).
        // 3. Un conflitto nato solo perche' il target ha cambiato righe PROTETTE accanto (ma non sopra) a
        //    righe non protette cambiate dalla sorgente (es. il target ha le sue versioni dei pacchetti
        //    interni nella riga vicina a un pacchetto esterno aggiornato) si risolve prendendo entrambe
        //    le modifiche; se il risultato senza conflitti non conserva le righe protette del target, si
        //    torna al merge del punto 2. I conflitti fra righe non protette restano conflitti.
        // 4. Nei conflitti che restano il lato SOURCE mostra le righe protette del TARGET, non quelle della
        //    base rimaste nella sorgente neutralizzata (vedi ShowTargetProtectedLinesInConflicts): "Take
        //    source" tiene le righe protette del target; righe uguali ai due lati escono dal conflitto.
        // KeptTargetDifferences = le modifiche della sorgente su righe protette che sono state scartate.
        public static ProtectedMergeResult MergeWithProtectedLines(string baseText, string sourceText, string targetText, IReadOnlyList<MergeLineRule> rules)
        {
            var source = sourceText ?? string.Empty;
            var compiled = CompileRules(rules);
            if (compiled.Count == 0)
                return new ProtectedMergeResult(source, ThreeWayMerge.Merge(baseText, source, targetText), NoDifferences);
            var rulesById = RulesById(compiled);

            var baseLines = ThreeWayMerge.SplitLines(baseText);
            var sourceLines = ThreeWayMerge.SplitLines(source);
            var targetLines = ThreeWayMerge.SplitLines(targetText);
            var baseMap = ProtectionMap.Analyze(baseLines, compiled);
            var sourceMap = ProtectionMap.Analyze(sourceLines, compiled);
            var targetMap = ProtectionMap.Analyze(targetLines, compiled);

            var neutral = Neutralize(baseLines, sourceLines, baseMap, sourceMap);
            var merge = ThreeWayMerge.Merge(baseText, neutral.Text, targetText);

            var neutralLines = ThreeWayMerge.SplitLines(neutral.Text);
            var neutralMap = ProtectionMap.Analyze(neutralLines, compiled);
            var refined = ResolveProtectedAdjacency(merge, baseMap, neutralMap, targetMap);
            if (refined != null)
            {
                var keep = true;
                if (refined.ConflictCount == 0)
                {
                    var text = ThreeWayMerge.BuildTextWithMarkers(refined, string.Empty, string.Empty);
                    keep = CompareProtected(targetLines, ThreeWayMerge.SplitLines(text), compiled).Count == 0;
                }
                if (keep)
                    merge = refined;
            }

            // I promemoria si calcolano sui blocchi del merge con la sorgente neutralizzata (le loro righe
            // SOURCE sono quelle della sorgente neutralizzata, in ordine); poi i conflitti per l'utente.
            var differences = BuildDifferences(neutral.Changes, merge, targetMap, rulesById);
            var shown = ShowTargetProtectedLinesInConflicts(merge, neutralMap, targetMap, targetLines, compiled, rulesById);
            if (shown != null)
                merge = shown;
            return new ProtectedMergeResult(neutral.Text, merge, differences);
        }

        // Per l'audit: le righe protette del risultato devono essere identiche (contenuto, senza
        // terminatore) e nello stesso ordine a quelle del target. Vuota = nessuna violazione.
        public static IReadOnlyList<string> CompareProtectedLines(string targetText, string resultText, IReadOnlyList<MergeLineRule> rules)
        {
            var compiled = CompileRules(rules);
            if (compiled.Count == 0)
                return NoStrings;
            return CompareProtected(ThreeWayMerge.SplitLines(targetText), ThreeWayMerge.SplitLines(resultText), compiled);
        }

        private static IReadOnlyList<string> CompareProtected(IReadOnlyList<string> targetLines, IReadOnlyList<string> resultLines, List<CompiledLineRule> compiled)
        {
            var targetMap = ProtectionMap.Analyze(targetLines, compiled);
            var resultMap = ProtectionMap.Analyze(resultLines, compiled);
            var targetIndices = targetMap.ProtectedIndices();
            var resultIndices = resultMap.ProtectedIndices();
            var targetContents = targetIndices.Select(i => targetMap.Contents[i]).ToList();
            var resultContents = resultIndices.Select(i => resultMap.Contents[i]).ToList();

            if (targetContents.SequenceEqual(resultContents, StringComparer.Ordinal))
                return NoStrings;

            bool approximate;
            var match = ThreeWayMerge.AlignLines(targetContents, resultContents, out approximate);
            var violations = new List<string>();
            var ti = 0;
            var ri = 0;
            while (ti < targetContents.Count || ri < resultContents.Count)
            {
                if (ti < targetContents.Count && match[ti] == ri)
                {
                    ti++;
                    ri++;
                }
                else if (ti < targetContents.Count && match[ti] < 0)
                {
                    var line = targetIndices[ti];
                    violations.Add(PolicyText.Format("Target line {0} is protected by rule '{1}' but is missing or changed in the result: {2}",
                        line + 1, targetMap.RuleOf[line], PolicyText.Short(targetContents[ti])));
                    ti++;
                }
                else
                {
                    var line = resultIndices[ri];
                    violations.Add(PolicyText.Format("Result line {0} is protected by rule '{1}' but is not in the target: {2}",
                        line + 1, resultMap.RuleOf[line], PolicyText.Short(resultContents[ri])));
                    ri++;
                }
            }

            return violations.AsReadOnly();
        }

        // -----------------------------------------------------------------------------------------
        // Neutralizzazione
        // -----------------------------------------------------------------------------------------

        private static NeutralizedSource Neutralize(IReadOnlyList<string> baseLines, IReadOnlyList<string> sourceLines, ProtectionMap baseMap, ProtectionMap sourceMap)
        {
            bool approximate;
            var match = ThreeWayMerge.AlignLines(baseLines, sourceLines, out approximate);
            var back = new int[sourceLines.Count];
            for (var i = 0; i < back.Length; i++)
                back[i] = -1;
            for (var b = 0; b < match.Length; b++)
            {
                if (match[b] >= 0)
                    back[match[b]] = b;
            }

            ExpandAroundBlocks(match, back, baseMap, sourceMap);

            var result = new NeutralizedSource();
            var bi = 0;
            var si = 0;
            while (bi < baseLines.Count || si < sourceLines.Count)
            {
                if (bi < baseLines.Count && si < sourceLines.Count && match[bi] == si)
                {
                    result.Lines.Add(sourceLines[si]);
                    bi++;
                    si++;
                    continue;
                }

                // tratto modificato: base [bi, b1), sorgente [si, s1)
                var b1 = bi;
                while (b1 < baseLines.Count && match[b1] < 0)
                    b1++;
                var s1 = b1 < baseLines.Count ? match[b1] : sourceLines.Count;
                if (s1 < si)
                    throw new InvalidOperationException("Internal error: the line alignment is not monotonic.");

                NeutralizeHunk(baseLines, sourceLines, baseMap, sourceMap, bi, b1, si, s1, result);
                bi = b1;
                si = s1;
            }

            var newLine = DetectNewLine(sourceLines) ?? DetectNewLine(baseLines) ?? "\r\n";
            result.Text = Compose(result.Lines, newLine);
            return result;
        }

        // Un blocco protetto (della base o della sorgente) toccato solo in parte dal diff si stacca per
        // intero dall'allineamento: cosi' finisce tutto dentro un tratto modificato e si tiene o si scarta
        // intero. Si ripete finche' non cambia piu' niente (staccare righe puo' spezzare altri blocchi).
        private static void ExpandAroundBlocks(int[] match, int[] back, ProtectionMap baseMap, ProtectionMap sourceMap)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var unit in baseMap.Units)
                {
                    if (unit.End > unit.Start && Detach(match, back, unit))
                        changed = true;
                }
                foreach (var unit in sourceMap.Units)
                {
                    if (unit.End > unit.Start && Detach(back, match, unit))
                        changed = true;
                }
            }
        }

        // true se il blocco non era intatto e aveva righe allineate (ora staccate)
        private static bool Detach(int[] forward, int[] backward, ProtectedUnit unit)
        {
            var aligned = 0;
            for (var x = unit.Start; x <= unit.End; x++)
            {
                if (forward[x] >= 0)
                    aligned++;
            }

            var count = unit.End - unit.Start + 1;
            if (aligned == 0)
                return false;
            if (aligned == count && forward[unit.End] - forward[unit.Start] == unit.End - unit.Start)
                return false; // intatto

            for (var x = unit.Start; x <= unit.End; x++)
            {
                if (forward[x] >= 0)
                {
                    backward[forward[x]] = -1;
                    forward[x] = -1;
                }
            }
            return true;
        }

        private static void NeutralizeHunk(
            IReadOnlyList<string> baseLines,
            IReadOnlyList<string> sourceLines,
            ProtectionMap baseMap,
            ProtectionMap sourceMap,
            int b0,
            int b1,
            int s0,
            int s1,
            NeutralizedSource result)
        {
            var baseUnits = HunkUnits(baseMap, b0, b1);
            var sourceUnits = HunkUnits(sourceMap, s0, s1);
            var pairs = LcsPairs(baseUnits, sourceUnits);

            var i0 = 0;
            var j0 = 0;
            for (var k = 0; k <= pairs.Count; k++)
            {
                var iEnd = k < pairs.Count ? pairs[k].Key : baseUnits.Count;
                var jEnd = k < pairs.Count ? pairs[k].Value : sourceUnits.Count;

                // prima le righe protette che la sorgente cancella (restano), poi le sue inserzioni
                for (var i = i0; i < iEnd; i++)
                {
                    var unit = baseUnits[i];
                    if (!unit.Protected)
                        continue;
                    var start = result.Lines.Count;
                    AddLines(result.Lines, baseLines, unit);
                    result.Changes.Add(new DiscardedChange
                    {
                        RuleId = unit.RuleId,
                        BaseContents = UnitContents(baseMap, unit),
                        NeutralStart = start,
                        NeutralEnd = result.Lines.Count - 1
                    });
                }

                for (var j = j0; j < jEnd; j++)
                {
                    var unit = sourceUnits[j];
                    if (unit.Protected)
                    {
                        result.Changes.Add(new DiscardedChange
                        {
                            RuleId = unit.RuleId,
                            SourceContents = UnitContents(sourceMap, unit),
                            SourceStart = unit.Start
                        });
                    }
                    else
                    {
                        AddLines(result.Lines, sourceLines, unit);
                    }
                }

                if (k < pairs.Count)
                {
                    var baseUnit = baseUnits[iEnd];
                    var sourceUnit = sourceUnits[jEnd];
                    if (baseUnit.Protected)
                    {
                        var start = result.Lines.Count;
                        AddLines(result.Lines, baseLines, baseUnit);
                        var baseContents = UnitContents(baseMap, baseUnit);
                        var sourceContents = UnitContents(sourceMap, sourceUnit);
                        if (!baseContents.SequenceEqual(sourceContents, StringComparer.Ordinal))
                        {
                            result.Changes.Add(new DiscardedChange
                            {
                                RuleId = baseUnit.RuleId,
                                BaseContents = baseContents,
                                SourceContents = sourceContents,
                                SourceStart = sourceUnit.Start,
                                NeutralStart = start,
                                NeutralEnd = result.Lines.Count - 1
                            });
                        }
                    }
                    else
                    {
                        AddLines(result.Lines, sourceLines, sourceUnit);
                    }

                    i0 = iEnd + 1;
                    j0 = jEnd + 1;
                }
            }
        }

        // Le unita' di un tratto: blocchi e righe protette (intere) e righe non protette (una per una).
        private static List<HunkUnit> HunkUnits(ProtectionMap map, int from, int to)
        {
            var units = new List<HunkUnit>();
            var i = from;
            while (i < to)
            {
                var unitIndex = map.UnitOf[i];
                if (unitIndex >= 0)
                {
                    var unit = map.Units[unitIndex];
                    var end = Math.Min(unit.End, to - 1);
                    units.Add(new HunkUnit(i, end, true, unit.RuleId, "P\u0001" + unit.RuleId.ToUpperInvariant()));
                    i = end + 1;
                }
                else
                {
                    units.Add(new HunkUnit(i, i, false, null, "L\u0001" + map.RawContents[i]));
                    i++;
                }
            }
            return units;
        }

        // Allineamento delle unita' di un tratto (sottosequenza comune piu' lunga sulle chiavi: stessa
        // regola per le protette, stesso contenuto per le altre). Serve solo all'ordine delle righe tenute.
        private static List<KeyValuePair<int, int>> LcsPairs(List<HunkUnit> a, List<HunkUnit> b)
        {
            var pairs = new List<KeyValuePair<int, int>>();
            if (a.Count == 0 || b.Count == 0 || (long)a.Count * b.Count > MaxHunkCells)
                return pairs;

            // lcs[i, j] = lunghezza della LCS di a[i..] e b[j..]
            var lcs = new int[a.Count + 1, b.Count + 1];
            for (var i = a.Count - 1; i >= 0; i--)
            {
                for (var j = b.Count - 1; j >= 0; j--)
                {
                    lcs[i, j] = string.Equals(a[i].Key, b[j].Key, StringComparison.Ordinal)
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var x = 0;
            var y = 0;
            while (x < a.Count && y < b.Count)
            {
                if (string.Equals(a[x].Key, b[y].Key, StringComparison.Ordinal))
                {
                    pairs.Add(new KeyValuePair<int, int>(x, y));
                    x++;
                    y++;
                }
                else if (lcs[x + 1, y] >= lcs[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }
            return pairs;
        }

        private static void AddLines(List<string> target, IReadOnlyList<string> lines, HunkUnit unit)
        {
            for (var x = unit.Start; x <= unit.End; x++)
                target.Add(lines[x]);
        }

        private static List<string> UnitContents(ProtectionMap map, HunkUnit unit)
        {
            var contents = new List<string>();
            for (var x = unit.Start; x <= unit.End; x++)
                contents.Add(map.RawContents[x]);
            return contents;
        }

        // -----------------------------------------------------------------------------------------
        // Conflitti dovuti solo a righe protette cambiate dal target accanto a modifiche della sorgente
        // -----------------------------------------------------------------------------------------

        // null = nessun conflitto risolto
        private static ThreeWayMergeResult ResolveProtectedAdjacency(ThreeWayMergeResult merge, ProtectionMap baseMap, ProtectionMap neutralMap, ProtectionMap targetMap)
        {
            if (merge.ConflictCount == 0)
                return null;

            var blocks = new List<MergeBlock>();
            var resolved = false;
            var b = 0;
            var n = 0;
            var t = 0;
            foreach (var block in merge.Blocks)
            {
                if (block == null)
                    continue;

                List<MergeBlock> parts = null;
                if (block.Kind == MergeBlockKind.Conflict)
                    parts = TryResolveConflict(block, b, n, t, baseMap, neutralMap, targetMap, merge.SourceLineTerminator);

                if (parts != null)
                {
                    blocks.AddRange(parts);
                    resolved = true;
                }
                else
                {
                    blocks.Add(block);
                }

                b += block.BaseLines.Count;
                n += block.SourceLines.Count;
                t += block.TargetLines.Count;
            }

            if (!resolved)
                return null;
            return new ThreeWayMergeResult(blocks.AsReadOnly(), merge.NewLine, merge.SourceLineTerminator, merge.IsApproximate);
        }

        // Il conflitto si divide in sotto-blocchi SourceOnly / TargetOnly / Unchanged se: le modifiche del
        // target toccano solo righe protette (della base e del target), quelle della sorgente solo righe
        // non protette, e nessuna modifica si sovrappone all'altra (due inserzioni nello stesso punto si
        // sovrappongono: l'ordine non si puo' decidere). null = il conflitto resta.
        private static List<MergeBlock> TryResolveConflict(
            MergeBlock block,
            int b0,
            int n0,
            int t0,
            ProtectionMap baseMap,
            ProtectionMap neutralMap,
            ProtectionMap targetMap,
            string sourceLineTerminator)
        {
            var baseLines = block.BaseLines;
            var sourceLines = block.SourceLines;
            var targetLines = block.TargetLines;

            bool approximateSource;
            bool approximateTarget;
            var sourceMatch = ThreeWayMerge.AlignLines(baseLines, sourceLines, out approximateSource);
            var targetMatch = ThreeWayMerge.AlignLines(baseLines, targetLines, out approximateTarget);
            var sourceHunks = Hunks(sourceMatch, baseLines.Count, sourceLines.Count, true);
            var targetHunks = Hunks(targetMatch, baseLines.Count, targetLines.Count, false);
            if (sourceHunks.Count == 0 || targetHunks.Count == 0)
                return null;

            foreach (var h in targetHunks)
            {
                for (var k = h.B0; k < h.B1; k++)
                {
                    if (!baseMap.IsProtected(b0 + k))
                        return null;
                }
                for (var k = h.X0; k < h.X1; k++)
                {
                    if (!targetMap.IsProtected(t0 + k))
                        return null;
                }
            }

            foreach (var h in sourceHunks)
            {
                for (var k = h.B0; k < h.B1; k++)
                {
                    if (baseMap.IsProtected(b0 + k))
                        return null;
                }
                for (var k = h.X0; k < h.X1; k++)
                {
                    if (neutralMap.IsProtected(n0 + k))
                        return null;
                }
            }

            foreach (var s in sourceHunks)
            {
                foreach (var tHunk in targetHunks)
                {
                    if (Overlap(s, tHunk))
                        return null;
                }
            }

            var events = sourceHunks.Concat(targetHunks)
                .OrderBy(h => h.B0)
                .ThenBy(h => h.B1 > h.B0 ? 1 : 0)
                .ToList();

            var parts = new List<MergeBlock>();
            var bi = 0;
            var si = 0;
            var ti = 0;
            foreach (var h in events)
            {
                if (h.B0 < bi)
                    return null;
                if (!AddUnchanged(parts, baseLines, sourceLines, targetLines, sourceMatch, targetMatch, h.B0, ref bi, ref si, ref ti))
                    return null;

                var length = h.B1 - h.B0;
                if (h.FromSource)
                {
                    if (si != h.X0)
                        return null;
                    for (var k = 0; k < length; k++)
                    {
                        if (targetMatch[h.B0 + k] != ti + k)
                            return null;
                    }
                    var slice = Slice(sourceLines, h.X0, h.X1);
                    parts.Add(new MergeBlock(MergeBlockKind.SourceOnly,
                        Slice(baseLines, h.B0, h.B1), slice, Slice(targetLines, ti, ti + length),
                        ThreeWayMerge.WithTerminator(slice, sourceLineTerminator)));
                    si = h.X1;
                    ti += length;
                }
                else
                {
                    if (ti != h.X0)
                        return null;
                    for (var k = 0; k < length; k++)
                    {
                        if (sourceMatch[h.B0 + k] != si + k)
                            return null;
                    }
                    var slice = Slice(targetLines, h.X0, h.X1);
                    parts.Add(new MergeBlock(MergeBlockKind.TargetOnly,
                        Slice(baseLines, h.B0, h.B1), Slice(sourceLines, si, si + length), slice, slice));
                    ti = h.X1;
                    si += length;
                }
                bi = h.B1;
            }

            if (!AddUnchanged(parts, baseLines, sourceLines, targetLines, sourceMatch, targetMatch, baseLines.Count, ref bi, ref si, ref ti))
                return null;
            if (si != sourceLines.Count || ti != targetLines.Count)
                return null;
            return parts;
        }

        // righe della base [bi, to) allineate in tutti e tre i lati
        private static bool AddUnchanged(
            List<MergeBlock> parts,
            IReadOnlyList<string> baseLines,
            IReadOnlyList<string> sourceLines,
            IReadOnlyList<string> targetLines,
            int[] sourceMatch,
            int[] targetMatch,
            int to,
            ref int bi,
            ref int si,
            ref int ti)
        {
            var length = to - bi;
            if (length <= 0)
                return true;
            for (var k = 0; k < length; k++)
            {
                if (sourceMatch[bi + k] != si + k || targetMatch[bi + k] != ti + k)
                    return false;
            }

            var targetSlice = Slice(targetLines, ti, ti + length);
            parts.Add(new MergeBlock(MergeBlockKind.Unchanged,
                Slice(baseLines, bi, to), Slice(sourceLines, si, si + length), targetSlice, targetSlice));
            bi = to;
            si += length;
            ti += length;
            return true;
        }

        // Tratti modificati di un allineamento: base [B0, B1) sostituita da [X0, X1) dell'altro lato.
        private static List<Hunk> Hunks(int[] match, int baseCount, int otherCount, bool fromSource)
        {
            var hunks = new List<Hunk>();
            var bi = 0;
            var xi = 0;
            while (bi < baseCount || xi < otherCount)
            {
                if (bi < baseCount && match[bi] == xi)
                {
                    bi++;
                    xi++;
                    continue;
                }

                var b1 = bi;
                while (b1 < baseCount && match[b1] < 0)
                    b1++;
                var x1 = b1 < baseCount ? match[b1] : otherCount;
                if (x1 < xi)
                    return new List<Hunk>(); // allineamento incoerente: nessuna risoluzione
                hunks.Add(new Hunk(bi, b1, xi, x1, fromSource));
                bi = b1;
                xi = x1;
            }
            return hunks;
        }

        private static bool Overlap(Hunk a, Hunk b)
        {
            var aEmpty = a.B0 == a.B1;
            var bEmpty = b.B0 == b.B1;
            if (aEmpty && bEmpty)
                return a.B0 == b.B0;
            if (aEmpty)
                return b.B0 < a.B0 && a.B0 < b.B1;
            if (bEmpty)
                return a.B0 < b.B0 && b.B0 < a.B1;
            return a.B0 < b.B1 && b.B0 < a.B1;
        }

        private static IReadOnlyList<string> Slice(IReadOnlyList<string> lines, int start, int end)
        {
            var count = end - start;
            if (count <= 0)
                return new ReadOnlyCollection<string>(new string[0]);
            var copy = new string[count];
            for (var i = 0; i < count; i++)
                copy[i] = lines[start + i];
            return new ReadOnlyCollection<string>(copy);
        }

        // -----------------------------------------------------------------------------------------
        // Promemoria delle modifiche scartate
        // -----------------------------------------------------------------------------------------

        private static IReadOnlyList<ProtectedLineDifference> BuildDifferences(List<DiscardedChange> changes, ThreeWayMergeResult merge, ProtectionMap targetMap,
            Dictionary<string, CompiledLineRule> rulesById)
        {
            if (changes.Count == 0)
                return NoDifferences;

            var targetKeys = new UnitKeys(targetMap, rulesById);
            var result = new List<ProtectedLineDifference>();
            foreach (var change in changes)
            {
                // la sorgente aggiunge: il target ha gia' la stessa cosa (es. lo stesso pacchetto)?
                var targetIndices = change.BaseContents != null
                    ? TargetCounterpart(change, merge, targetMap, targetKeys, rulesById)
                    : SameUnitInTarget(change.SourceContents, change.RuleId, targetMap, targetKeys, rulesById);

                var sourceText = change.SourceContents == null ? null : string.Join("\n", change.SourceContents);
                var targetText = targetIndices == null || targetIndices.Count == 0
                    ? null
                    : string.Join("\n", targetIndices.Select(i => targetMap.RawContents[i]));
                int? sourceLine = change.SourceContents == null ? (int?)null : change.SourceStart + 1;
                int? targetLine = targetText == null ? (int?)null : targetIndices[0] + 1;

                string summary;
                var rule = PolicyText.Format("rule '{0}'", change.RuleId);
                if (change.BaseContents == null)
                {
                    // la sorgente aggiunge righe protette: non passano. Se il target non ha niente di
                    // corrispondente (es. un pacchetto interno nuovo per il task) va aggiunto a mano.
                    summary = targetText == null
                        ? PolicyText.Format("Source line {0} ({1}): the added {2} not merged: {3}. The target does not have it: if the task needs it, add it by hand with the version used in the target branch.",
                            sourceLine, rule, Plural(change.SourceContents.Count, "line was", "lines were"), PolicyText.Short(sourceText))
                        : PolicyText.Format("Source line {0} ({1}): the added {2} not merged: {3}; the target already has {4}.",
                            sourceLine, rule, Plural(change.SourceContents.Count, "line was", "lines were"), PolicyText.Short(sourceText), PolicyText.Short(targetText));
                }
                else if (change.SourceContents == null)
                {
                    // la sorgente cancella righe protette: restano quelle del target. Se il target non le ha
                    // (le ha gia' tolte) il risultato e' comunque senza: non c'e' niente da ricordare.
                    if (targetText == null)
                        continue;
                    summary = PolicyText.Format("The source removes {0} ({1}); the target keeps {2}.",
                        PolicyText.Short(string.Join("\n", change.BaseContents)), rule,
                        targetText == string.Join("\n", change.BaseContents) ? "it" : PolicyText.Short(targetText));
                }
                else
                {
                    summary = PolicyText.Format("Source line {0} ({1}): the change to {2} was not merged; {3}.",
                        sourceLine, rule, PolicyText.Short(sourceText),
                        targetText == null ? "the target has no such line" : "the target keeps " + PolicyText.Short(targetText));
                }

                result.Add(new ProtectedLineDifference(change.RuleId, sourceText, targetText, sourceLine, targetLine, summary));
            }

            return result.AsReadOnly();
        }

        private static string Plural(int count, string one, string many)
        {
            return count == 1 ? one : many;
        }

        // L'unita' protetta del target che corrisponde all'unita' della base tenuta nella sorgente
        // neutralizzata (righe NeutralStart..NeutralEnd), come righe del target; null = il target non ne ha.
        // Candidate: le unita' INTERE del target che nel risultato stanno al posto di quella della base
        // (per i blocchi Unchanged riga per riga, per gli altri tutte le righe del target del blocco).
        // Si sceglie, nell'ordine: un'unita' candidata identica a quella della base; un'unita' candidata
        // con la stessa identita' (UnitKey: es. lo stesso pacchetto con un'altra versione); un'unita' con
        // la stessa identita' nel resto del target. Mai righe di un'altra unita' o un pezzo di blocco.
        private static List<int> TargetCounterpart(DiscardedChange change, ThreeWayMergeResult merge, ProtectionMap targetMap,
            UnitKeys targetKeys, Dictionary<string, CompiledLineRule> rulesById)
        {
            var candidates = new List<int>();
            var n = 0;
            var t = 0;
            foreach (var block in merge.Blocks)
            {
                if (block == null)
                    continue;
                var nc = block.SourceLines.Count;
                var tc = block.TargetLines.Count;
                var from = Math.Max(n, change.NeutralStart);
                var to = Math.Min(n + nc - 1, change.NeutralEnd);
                if (from <= to)
                {
                    if (block.Kind == MergeBlockKind.Unchanged && nc == tc)
                    {
                        for (var x = from; x <= to; x++)
                            candidates.Add(t + (x - n));
                    }
                    else
                    {
                        for (var x = 0; x < tc; x++)
                            candidates.Add(t + x);
                    }
                }
                n += nc;
                t += tc;
            }

            var candidateSet = new HashSet<int>(candidates);
            var candidateUnits = new List<int>();
            for (var u = 0; u < targetMap.Units.Count; u++)
            {
                var unit = targetMap.Units[u];
                var whole = true;
                for (var x = unit.Start; x <= unit.End && whole; x++)
                    whole = candidateSet.Contains(x);
                if (whole)
                    candidateUnits.Add(u);
            }

            var contents = change.BaseContents;
            foreach (var u in candidateUnits)
            {
                var unit = targetMap.Units[u];
                if (unit.End - unit.Start + 1 != contents.Count)
                    continue;
                var same = true;
                for (var k = 0; k < contents.Count && same; k++)
                    same = string.Equals(targetMap.RawContents[unit.Start + k], contents[k], StringComparison.Ordinal);
                if (same)
                    return UnitLines(unit);
            }

            CompiledLineRule rule;
            rulesById.TryGetValue(change.RuleId ?? string.Empty, out rule);
            var key = UnitKey(contents, rule);
            if (key == null)
                return null;
            foreach (var u in candidateUnits)
            {
                if (string.Equals(targetKeys.Of(u), key, StringComparison.Ordinal))
                    return UnitLines(targetMap.Units[u]);
            }
            return SameUnitInTarget(contents, change.RuleId, targetMap, targetKeys, rulesById);
        }

        // La prima unita' protetta del target con la stessa identita' (UnitKey) di contents, come righe
        // del target; null = nessuna.
        private static List<int> SameUnitInTarget(IReadOnlyList<string> contents, string ruleId, ProtectionMap targetMap,
            UnitKeys targetKeys, Dictionary<string, CompiledLineRule> rulesById)
        {
            CompiledLineRule rule;
            rulesById.TryGetValue(ruleId ?? string.Empty, out rule);
            var key = UnitKey(contents, rule);
            if (key == null)
                return null;
            for (var u = 0; u < targetMap.Units.Count; u++)
            {
                if (string.Equals(targetKeys.Of(u), key, StringComparison.Ordinal))
                    return UnitLines(targetMap.Units[u]);
            }
            return null;
        }

        private static List<int> UnitLines(ProtectedUnit unit)
        {
            return Enumerable.Range(unit.Start, unit.End - unit.Start + 1).ToList();
        }

        // -----------------------------------------------------------------------------------------
        // Identita' di un'unita' protetta
        // -----------------------------------------------------------------------------------------

        // primo attributo nome="valore" (o 'valore') di una riga; il valore si ferma alla virgola
        // ("Include=\"X, Version=1.0.0.0, ...\"" -> X). Gli attributi xmlns non identificano niente.
        private static readonly Regex AttributeRegex = new Regex(
            @"(?<![\w.:\-])(?!xmlns\b)(?<name>[A-Za-z_][\w.:\-]*)\s*=\s*(?:""(?<value>[^"",]*)|'(?<value>[^',]*))",
            RegexOptions.CultureInvariant, RegexTimeout);

        // numero di versione (1, 1.2.3.4, 1.0.0-beta.2, 2.0.0+abc) non attaccato a lettere o cifre: "net48"
        // e "Framework2" non sono versioni
        private static readonly Regex VersionRegex = new Regex(
            @"(?<![0-9A-Za-z_])\d+(?:\.\d+)*(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?(?:\+[0-9A-Za-z.\-]+)?(?![0-9A-Za-z])",
            RegexOptions.CultureInvariant, RegexTimeout);

        private static Dictionary<string, CompiledLineRule> RulesById(List<CompiledLineRule> compiled)
        {
            var result = new Dictionary<string, CompiledLineRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in compiled)
            {
                if (!result.ContainsKey(rule.Id))
                    result[rule.Id] = rule;
            }
            return result;
        }

        // Identita' di un'unita' protetta, per riconoscere "la stessa cosa" in un'altra versione del file
        // (es. lo stesso pacchetto con un'altra versione). Riga che la identifica: per un blocco con
        // 'contains' la prima riga che lo soddisfa, altrimenti la prima riga con un attributo nome="valore",
        // altrimenti la prima riga. Identita' = quel primo attributo (nome e valore) o, senza attributi, la
        // riga intera; i numeri di versione diventano '#'; senza distinzione di maiuscole. null = nessuna.
        private static string UnitKey(IReadOnlyList<string> contents, CompiledLineRule rule)
        {
            if (contents == null || contents.Count == 0)
                return null;
            try
            {
                string line = null;
                Match attribute = null;
                if (rule != null && rule.Contains != null)
                {
                    line = contents.FirstOrDefault(c => rule.Contains.IsMatch(c.TrimStart('﻿')));
                    if (line != null)
                        attribute = AttributeRegex.Match(line);
                }
                if (line == null)
                {
                    foreach (var c in contents)
                    {
                        var m = AttributeRegex.Match(c);
                        if (m.Success)
                        {
                            line = c;
                            attribute = m;
                            break;
                        }
                    }
                }

                var text = attribute != null && attribute.Success
                    ? attribute.Groups["name"].Value + "=" + attribute.Groups["value"].Value.Trim()
                    : (line ?? contents[0]).TrimStart('﻿').Trim();
                return VersionRegex.Replace(text, "#").ToUpperInvariant();
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        // Identita' delle unita' di un testo, calcolate una volta sola.
        private sealed class UnitKeys
        {
            private readonly ProtectionMap _map;
            private readonly Dictionary<string, CompiledLineRule> _rulesById;
            private readonly string[] _keys;
            private readonly bool[] _done;

            public UnitKeys(ProtectionMap map, Dictionary<string, CompiledLineRule> rulesById)
            {
                _map = map;
                _rulesById = rulesById;
                _keys = new string[map.Units.Count];
                _done = new bool[map.Units.Count];
            }

            public string Of(int unitIndex)
            {
                if (!_done[unitIndex])
                {
                    var unit = _map.Units[unitIndex];
                    CompiledLineRule rule;
                    _rulesById.TryGetValue(unit.RuleId ?? string.Empty, out rule);
                    var contents = new List<string>();
                    for (var x = unit.Start; x <= unit.End; x++)
                        contents.Add(_map.RawContents[x]);
                    _keys[unitIndex] = UnitKey(contents, rule);
                    _done[unitIndex] = true;
                }
                return _keys[unitIndex];
            }
        }

        // -----------------------------------------------------------------------------------------
        // Conflitti: il lato SOURCE con le righe protette del target
        // -----------------------------------------------------------------------------------------

        // oltre questo numero di conflitti da verificare si lasciano come sono (ogni verifica rilegge il file)
        private const int MaxShownConflictChecks = 50;

        // Nei blocchi in conflitto il lato SOURCE e' la sorgente neutralizzata, cioe' ha le righe protette
        // della BASE (ne' del task ne' del target): "Take source" le rimetterebbe e il controllo finale
        // bloccherebbe il check-in. Si mostrano quelle del TARGET: ogni unita' protetta del lato SOURCE si
        // sostituisce con l'unita' del lato TARGET con la stessa identita' (UnitKey); quelle senza
        // corrispondente si tolgono; quelle del target in piu' si mettono accanto (vedi
        // SourceWithTargetUnits). Le righe non protette del SOURCE restano com'erano e nel loro ordine.
        // Poi le righe uguali in testa e in coda escono dal conflitto (BothSame, come fa il merge); se i
        // due lati diventano uguali il blocco non e' piu' un conflitto. Un blocco cambia solo se non ha
        // unita' a cavallo del bordo, ha unita' protette in almeno un lato e "Take source" su di lui
        // (target sugli altri conflitti) da' le righe protette del target.
        // null = nessun blocco cambiato (anche: scegliendo il target ovunque ci sono gia' violazioni).
        private static ThreeWayMergeResult ShowTargetProtectedLinesInConflicts(ThreeWayMergeResult merge, ProtectionMap neutralMap, ProtectionMap targetMap,
            IReadOnlyList<string> targetLines, List<CompiledLineRule> compiled, Dictionary<string, CompiledLineRule> rulesById)
        {
            if (merge.ConflictCount == 0)
                return null;

            var blocks = merge.Blocks.Where(b => b != null).ToList();
            if (CompareProtected(targetLines, ThreeWayMerge.SplitLines(ResolveAll(merge, blocks, -1, null)), compiled).Count > 0)
                return null;

            var neutralKeys = new UnitKeys(neutralMap, rulesById);
            var targetKeys = new UnitKeys(targetMap, rulesById);
            var result = new List<MergeBlock>();
            var changed = false;
            var checks = 0;
            var n = 0;
            var t = 0;
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                List<MergeBlock> parts = null;
                if (block.Kind == MergeBlockKind.Conflict && checks < MaxShownConflictChecks)
                {
                    var source = SourceWithTargetUnits(block, n, t, neutralMap, targetMap, neutralKeys, targetKeys);
                    if (source != null && !SameContents(source, block.SourceLines))
                    {
                        checks++;
                        var check = ResolveAll(merge, blocks, i, source);
                        if (CompareProtected(targetLines, ThreeWayMerge.SplitLines(check), compiled).Count == 0)
                            parts = NarrowConflict(block, source);
                    }
                }

                if (parts != null)
                {
                    result.AddRange(parts);
                    changed = true;
                }
                else
                {
                    result.Add(block);
                }
                n += block.SourceLines.Count;
                t += block.TargetLines.Count;
            }

            if (!changed)
                return null;
            var shown = new ThreeWayMergeResult(result.AsReadOnly(), merge.NewLine, merge.SourceLineTerminator, merge.IsApproximate);
            if (shown.ConflictCount == 0)
            {
                var text = ThreeWayMerge.BuildTextWithMarkers(shown, string.Empty, string.Empty);
                if (CompareProtected(targetLines, ThreeWayMerge.SplitLines(text), compiled).Count > 0)
                    return null;
            }
            return shown;
        }

        // Il lato SOURCE del conflitto (righe della sorgente neutralizzata da n0) con le unita' protette
        // del lato TARGET (righe del target da t0) al posto delle sue: le righe non protette del SOURCE
        // restano, in ordine; ogni unita' protetta del SOURCE lascia il posto all'unita' del TARGET con la
        // stessa identita' (cercata in avanti) o sparisce; un'unita' del TARGET senza corrispondente va
        // prima della prima riga che la segue nel TARGET e che c'e' anche nel SOURCE (o prima dell'unita'
        // corrispondente successiva), altrimenti in fondo. Le unita' del TARGET restano nel loro ordine.
        // null = niente da fare (nessuna unita' protetta nei due lati) o non si puo' (un'unita' a cavallo
        // del bordo del blocco).
        private static List<string> SourceWithTargetUnits(MergeBlock block, int n0, int t0, ProtectionMap neutralMap, ProtectionMap targetMap,
            UnitKeys neutralKeys, UnitKeys targetKeys)
        {
            var sourceUnits = BlockUnits(neutralMap, n0, block.SourceLines.Count);
            var targetUnits = BlockUnits(targetMap, t0, block.TargetLines.Count);
            if (sourceUnits == null || targetUnits == null || (sourceUnits.Count == 0 && targetUnits.Count == 0))
                return null;

            var lines = new List<string>();
            var next = 0;       // prossima unita' del target da mettere
            var cursor = 0;     // righe del target (locali al blocco) gia' usate come ancora
            Action<int> emitUpTo = upTo =>
            {
                for (; next < upTo; next++)
                {
                    var targetUnit = targetMap.Units[targetUnits[next]];
                    for (var x = targetUnit.Start; x <= targetUnit.End; x++)
                        lines.Add(block.TargetLines[x - t0]);
                    cursor = Math.Max(cursor, targetUnit.End - t0 + 1);
                }
            };

            // righe non protette del TARGET per contenuto (senza terminatore), in ordine
            var targetByContent = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var p = 0; p < block.TargetLines.Count; p++)
            {
                if (targetMap.IsProtected(t0 + p))
                    continue;
                var content = WithoutTerminator(block.TargetLines[p]);
                List<int> positions;
                if (!targetByContent.TryGetValue(content, out positions))
                    targetByContent[content] = positions = new List<int>();
                positions.Add(p);
            }

            var unitIndex = 0;
            var s = 0;
            while (s < block.SourceLines.Count)
            {
                if (unitIndex < sourceUnits.Count && neutralMap.Units[sourceUnits[unitIndex]].Start - n0 == s)
                {
                    var unit = neutralMap.Units[sourceUnits[unitIndex]];
                    var key = neutralKeys.Of(sourceUnits[unitIndex]);
                    if (key != null)
                    {
                        for (var j = next; j < targetUnits.Count; j++)
                        {
                            if (string.Equals(targetKeys.Of(targetUnits[j]), key, StringComparison.Ordinal))
                            {
                                emitUpTo(j + 1);
                                break;
                            }
                        }
                    }
                    unitIndex++;
                    s = unit.End - n0 + 1;
                    continue;
                }

                // riga non protetta: se c'e' anche nel TARGET (piu' avanti delle ancore gia' usate), le
                // unita' del TARGET che la precedono vanno prima
                var line = block.SourceLines[s];
                List<int> same;
                var anchor = -1;
                if (targetByContent.TryGetValue(WithoutTerminator(line), out same))
                {
                    foreach (var p in same)
                    {
                        if (p >= cursor)
                        {
                            anchor = p;
                            break;
                        }
                    }
                }
                if (anchor >= 0)
                {
                    var upTo = next;
                    while (upTo < targetUnits.Count && targetMap.Units[targetUnits[upTo]].Start - t0 < anchor)
                        upTo++;
                    emitUpTo(upTo);
                    cursor = anchor + 1;
                }
                lines.Add(line);
                s++;
            }
            emitUpTo(targetUnits.Count);
            return lines;
        }

        // Unita' protette (indici in map.Units) delle righe [offset, offset + count); null se una e' a
        // cavallo del bordo.
        private static List<int> BlockUnits(ProtectionMap map, int offset, int count)
        {
            var units = new List<int>();
            var x = offset;
            while (x < offset + count)
            {
                var u = map.UnitOf[x];
                if (u < 0)
                {
                    x++;
                    continue;
                }
                var unit = map.Units[u];
                if (unit.Start < offset || unit.End >= offset + count)
                    return null;
                units.Add(u);
                x = unit.End + 1;
            }
            return units;
        }

        // Testo con ogni conflitto risolto col target, salvo il blocco sourceIndex risolto con sourceLines.
        private static string ResolveAll(ThreeWayMergeResult merge, List<MergeBlock> blocks, int sourceIndex, IReadOnlyList<string> sourceLines)
        {
            var resolved = new List<MergeBlock>(blocks.Count);
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.Kind != MergeBlockKind.Conflict)
                {
                    resolved.Add(block);
                    continue;
                }
                var lines = i == sourceIndex ? ThreeWayMerge.WithTerminator(sourceLines, merge.SourceLineTerminator) : block.TargetLines;
                resolved.Add(new MergeBlock(MergeBlockKind.BothSame, block.BaseLines, block.SourceLines, block.TargetLines, lines));
            }
            return ThreeWayMerge.BuildTextWithMarkers(
                new ThreeWayMergeResult(resolved.AsReadOnly(), merge.NewLine, merge.SourceLineTerminator, merge.IsApproximate),
                string.Empty, string.Empty);
        }

        // Il conflitto con il nuovo lato SOURCE: righe uguali (a meno del terminatore) in testa e in coda
        // -> BothSame (righe del target); tutto uguale -> un solo BothSame con le righe della base.
        private static List<MergeBlock> NarrowConflict(MergeBlock block, List<string> source)
        {
            var target = block.TargetLines;
            var prefix = 0;
            while (prefix < source.Count && prefix < target.Count && SameContent(source[prefix], target[prefix]))
                prefix++;
            var suffix = 0;
            while (suffix < source.Count - prefix && suffix < target.Count - prefix
                   && SameContent(source[source.Count - 1 - suffix], target[target.Count - 1 - suffix]))
                suffix++;

            var sourceList = new ReadOnlyCollection<string>(source);
            var parts = new List<MergeBlock>();
            if (prefix == source.Count && prefix == target.Count)
            {
                parts.Add(new MergeBlock(MergeBlockKind.BothSame, block.BaseLines, sourceList, target, target));
                return parts;
            }

            var noLines = Slice(target, 0, 0);
            if (prefix > 0)
            {
                var same = Slice(target, 0, prefix);
                parts.Add(new MergeBlock(MergeBlockKind.BothSame, noLines, Slice(sourceList, 0, prefix), same, same));
            }
            parts.Add(new MergeBlock(MergeBlockKind.Conflict, block.BaseLines,
                Slice(sourceList, prefix, source.Count - suffix), Slice(target, prefix, target.Count - suffix), null));
            if (suffix > 0)
            {
                var same = Slice(target, target.Count - suffix, target.Count);
                parts.Add(new MergeBlock(MergeBlockKind.BothSame, noLines, Slice(sourceList, source.Count - suffix, source.Count), same, same));
            }
            return parts;
        }

        private static string WithoutTerminator(string line)
        {
            return line.Substring(0, line.Length - TerminatorLength(line));
        }

        private static bool SameContent(string a, string b)
        {
            var la = a.Length - TerminatorLength(a);
            var lb = b.Length - TerminatorLength(b);
            return la == lb && string.CompareOrdinal(a, 0, b, 0, la) == 0;
        }

        private static bool SameContents(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (!SameContent(a[i], b[i]))
                    return false;
            }
            return true;
        }

        // -----------------------------------------------------------------------------------------
        // Controllo delle azioni dei passi
        // -----------------------------------------------------------------------------------------

        // Errori (vuota = azioni coerenti):
        // - Discard di un passo il cui item non esiste nel target prima del passo (non c'e' contenuto del
        //   target da tenere: si usa Skip);
        // - Skip/Discard di una cartella aggiunta con dentro passi Merge (TFVC aggiungerebbe la cartella
        //   come semplice Add, senza legame di merge);
        // - Skip/Discard di un passo mentre un passo successivo dello stesso item e' Merge (il merge
        //   successivo avrebbe per base una versione che il target non ha).
        public static IReadOnlyList<string> ValidateStepActions(TaskMergePlan plan, Func<TaskMergeStep, MergePolicyAction> actionOf)
        {
            if (plan == null)
                throw new ArgumentNullException("plan");

            var errors = new List<string>();
            if (actionOf == null)
                return errors.AsReadOnly();

            var steps = (plan.Steps ?? new TaskMergeStep[0]).Where(s => s != null).ToList();
            var actions = new Dictionary<TaskMergeStep, MergePolicyAction>();
            foreach (var step in steps)
            {
                MergePolicyAction action;
                try
                {
                    action = actionOf(step);
                }
                catch (Exception ex)
                {
                    errors.Add(PolicyText.Format("{0}: the merge policy action could not be determined ({1}).", StepLabel(step), ex.Message));
                    action = MergePolicyAction.Merge;
                }

                if (!Enum.IsDefined(typeof(MergePolicyAction), action))
                {
                    errors.Add(PolicyText.Format("{0}: the merge policy action {1} is not valid.", StepLabel(step), (int)action));
                    action = MergePolicyAction.Merge;
                }
                actions[step] = action;
            }

            foreach (var step in steps)
            {
                var action = actions[step];
                if (action == MergePolicyAction.Merge)
                    continue;

                var item = NormalizeServerPath(step.TargetItem);
                var earlierMerged = steps.Any(s => s.Number < step.Number && SameItem(s, item) && actions[s] == MergePolicyAction.Merge);
                var existsBefore = step.TargetExists || earlierMerged;

                if (action == MergePolicyAction.Discard && !existsBefore)
                {
                    errors.Add(PolicyText.Format(
                        "{0} is set to Discard, but the item does not exist in the target: there is no target content to keep. Use Skip instead.",
                        StepLabel(step)));
                }

                if (step.Kind == TaskMergeItemKind.Folder
                    && step.Recursion == TaskMergeStepRecursion.None
                    && (step.ChangeKind & TaskChangeKind.Add) != 0
                    && !existsBefore
                    && item != null)
                {
                    var prefix = item + "/";
                    var inside = steps
                        .Where(s => s != step && actions[s] == MergePolicyAction.Merge)
                        .Where(s =>
                        {
                            var other = NormalizeServerPath(s.TargetItem);
                            return other != null && other.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                    if (inside.Count > 0)
                    {
                        errors.Add(PolicyText.Format(
                            "{0} is set to {1}, but it adds a folder that contains merged steps: {2}. Set those steps to {1} too, or merge the folder.",
                            StepLabel(step), action, StepList(inside)));
                    }
                }

                var laterMerged = steps
                    .Where(s => s.Number > step.Number && SameItem(s, item) && actions[s] == MergePolicyAction.Merge)
                    .ToList();
                if (laterMerged.Count > 0)
                {
                    errors.Add(PolicyText.Format(
                        "{0} is set to {1}, but a later step of the same item is merged: {2}. An item cannot be merged after one of its earlier steps was skipped or discarded: merge this step too, or also set the later steps to {1}.",
                        StepLabel(step), action, StepList(laterMerged)));
                }
            }

            return errors.AsReadOnly();
        }

        private static bool SameItem(TaskMergeStep step, string item)
        {
            return item != null && string.Equals(NormalizeServerPath(step.TargetItem), item, StringComparison.OrdinalIgnoreCase);
        }

        private static string StepLabel(TaskMergeStep step)
        {
            return PolicyText.Format("Step {0} ({1}, {2})", step.Number,
                string.IsNullOrEmpty(step.RelativePath) ? "." : step.RelativePath,
                step.FromChangesetId == step.ToChangesetId
                    ? PolicyText.Format("C{0}", step.FromChangesetId)
                    : PolicyText.Format("C{0}-C{1}", step.FromChangesetId, step.ToChangesetId));
        }

        private static string StepList(List<TaskMergeStep> steps)
        {
            var text = string.Join(", ", steps.Take(MaxShownInText).Select(s => PolicyText.Format("step {0} ({1})", s.Number,
                string.IsNullOrEmpty(s.RelativePath) ? "." : s.RelativePath)));
            if (steps.Count > MaxShownInText)
                text += PolicyText.Format(" and {0} more", steps.Count - MaxShownInText);
            return text;
        }

        private static string NormalizeServerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var trimmed = path.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        // -----------------------------------------------------------------------------------------
        // JSON
        // -----------------------------------------------------------------------------------------

        // Formato (nomi delle proprieta' senza distinzione di maiuscole; proprieta' sconosciute ignorate):
        // { "version": 1,
        //   "pathRules": [ { "id", "pattern", "action": "Merge|Discard|Skip", "description", "enabled" } ],
        //   "lineRules": [ { "id", "filePattern", "linePattern", "blockStartPattern", "blockEndPattern",
        //                    "blockContainsPattern", "description", "enabled" } ] }
        // "enabled" assente = true; "version" assente = 1. Testo vuoto o "null" = documento vuoto; JSON
        // non valido = FormatException con il motivo.
        public static MergePolicyDocument Parse(string json)
        {
            var text = (json ?? string.Empty).TrimStart('\uFEFF').Trim();
            if (text.Length == 0)
                return new MergePolicyDocument();

            object root;
            try
            {
                root = new JavaScriptSerializer().DeserializeObject(text);
            }
            catch (Exception ex)
            {
                throw new FormatException("The merge policy is not valid JSON: " + ex.Message, ex);
            }

            if (root == null)
                return new MergePolicyDocument();

            var obj = root as IDictionary<string, object>;
            if (obj == null)
                throw new FormatException("The merge policy must be a JSON object with \"version\", \"pathRules\" and \"lineRules\".");

            var doc = new MergePolicyDocument();
            var version = Property(obj, "version");
            if (version != null)
                doc.Version = ToInt(version, "version");

            var index = 0;
            foreach (var item in ReadArray(obj, "pathRules"))
            {
                index++;
                var rule = item as IDictionary<string, object>;
                if (rule == null)
                    throw new FormatException(PolicyText.Format("Path rule #{0} of the merge policy is not a JSON object.", index));
                var where = PolicyText.Format("path rule #{0}", index);
                doc.PathRules.Add(new MergePathRule
                {
                    Id = ReadString(rule, "id", where),
                    Pattern = ReadString(rule, "pattern", where),
                    Action = ParseAction(Property(rule, "action"), where),
                    Description = ReadString(rule, "description", where),
                    Enabled = ReadBool(rule, "enabled", where)
                });
            }

            index = 0;
            foreach (var item in ReadArray(obj, "lineRules"))
            {
                index++;
                var rule = item as IDictionary<string, object>;
                if (rule == null)
                    throw new FormatException(PolicyText.Format("Line rule #{0} of the merge policy is not a JSON object.", index));
                var where = PolicyText.Format("line rule #{0}", index);
                doc.LineRules.Add(new MergeLineRule
                {
                    Id = ReadString(rule, "id", where),
                    FilePattern = ReadString(rule, "filePattern", where),
                    LinePattern = ReadString(rule, "linePattern", where),
                    BlockStartPattern = ReadString(rule, "blockStartPattern", where),
                    BlockEndPattern = ReadString(rule, "blockEndPattern", where),
                    BlockContainsPattern = ReadString(rule, "blockContainsPattern", where),
                    Description = ReadString(rule, "description", where),
                    Enabled = ReadBool(rule, "enabled", where)
                });
            }

            return doc;
        }

        // JSON indentato (a-capo "\r\n"), leggibile e confrontabile in un file sotto controllo di versione;
        // le proprieta' null si omettono.
        public static string Serialize(MergePolicyDocument doc)
        {
            var d = doc ?? new MergePolicyDocument();
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"version\": ").Append(d.Version.ToString(CultureInfo.InvariantCulture)).Append(",\r\n");

            sb.Append("  \"pathRules\": ");
            WriteArray(sb, (d.PathRules ?? new List<MergePathRule>()).Where(r => r != null).Select(r => new List<KeyValuePair<string, object>>
            {
                Pair("id", r.Id),
                Pair("pattern", r.Pattern),
                Pair("action", r.Action.ToString()),
                Pair("description", r.Description),
                Pair("enabled", r.Enabled)
            }).ToList());
            sb.Append(",\r\n");

            sb.Append("  \"lineRules\": ");
            WriteArray(sb, (d.LineRules ?? new List<MergeLineRule>()).Where(r => r != null).Select(r => new List<KeyValuePair<string, object>>
            {
                Pair("id", r.Id),
                Pair("filePattern", r.FilePattern),
                Pair("linePattern", r.LinePattern),
                Pair("blockStartPattern", r.BlockStartPattern),
                Pair("blockEndPattern", r.BlockEndPattern),
                Pair("blockContainsPattern", r.BlockContainsPattern),
                Pair("description", r.Description),
                Pair("enabled", r.Enabled)
            }).ToList());
            sb.Append("\r\n}\r\n");
            return sb.ToString();
        }

        private static KeyValuePair<string, object> Pair(string name, object value)
        {
            return new KeyValuePair<string, object>(name, value);
        }

        private static void WriteArray(StringBuilder sb, List<List<KeyValuePair<string, object>>> items)
        {
            if (items.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            sb.Append("[\r\n");
            for (var i = 0; i < items.Count; i++)
            {
                sb.Append("    {\r\n");
                var properties = items[i].Where(p => p.Value != null).ToList();
                for (var k = 0; k < properties.Count; k++)
                {
                    sb.Append("      ");
                    WriteString(sb, properties[k].Key);
                    sb.Append(": ");
                    if (properties[k].Value is bool)
                        sb.Append((bool)properties[k].Value ? "true" : "false");
                    else
                        WriteString(sb, Convert.ToString(properties[k].Value, CultureInfo.InvariantCulture));
                    sb.Append(k < properties.Count - 1 ? ",\r\n" : "\r\n");
                }
                sb.Append(i < items.Count - 1 ? "    },\r\n" : "    }\r\n");
            }
            sb.Append("  ]");
        }

        // Solo gli escape necessari: le regex restano leggibili (JavaScriptSerializer scriverebbe
        // "<" come "<").
        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static object Property(IDictionary<string, object> obj, string name)
        {
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static IEnumerable<object> ReadArray(IDictionary<string, object> obj, string name)
        {
            var value = Property(obj, name);
            if (value == null)
                return new object[0];
            if (value is string || !(value is IEnumerable))
                throw new FormatException(PolicyText.Format("\"{0}\" in the merge policy must be a JSON array.", name));
            return ((IEnumerable)value).Cast<object>();
        }

        private static string ReadString(IDictionary<string, object> obj, string name, string where)
        {
            var value = Property(obj, name);
            if (value == null)
                return null;
            var text = value as string;
            if (text == null)
                throw new FormatException(PolicyText.Format("\"{0}\" of {1} in the merge policy must be a string.", name, where));
            return text;
        }

        private static bool ReadBool(IDictionary<string, object> obj, string name, string where)
        {
            var value = Property(obj, name);
            if (value == null)
                return true;
            if (!(value is bool))
                throw new FormatException(PolicyText.Format("\"{0}\" of {1} in the merge policy must be true or false.", name, where));
            return (bool)value;
        }

        private static int ToInt(object value, string name)
        {
            if (value is int)
                return (int)value;
            if (value is long || value is decimal || value is double)
            {
                var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (d == decimal.Truncate(d) && d >= int.MinValue && d <= int.MaxValue)
                    return (int)d;
            }
            throw new FormatException(PolicyText.Format("\"{0}\" in the merge policy must be an integer.", name));
        }

        private static MergePolicyAction ParseAction(object value, string where)
        {
            if (value == null)
                throw new FormatException(PolicyText.Format("The \"action\" of {0} in the merge policy is missing (use Merge, Discard or Skip).", where));
            var text = value as string;
            if (text != null)
            {
                foreach (MergePolicyAction action in Enum.GetValues(typeof(MergePolicyAction)))
                {
                    if (string.Equals(action.ToString(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                        return action;
                }
            }
            throw new FormatException(PolicyText.Format("The \"action\" of {0} in the merge policy is not valid: {1} (use Merge, Discard or Skip).",
                where, Convert.ToString(value, CultureInfo.InvariantCulture)));
        }

        // -----------------------------------------------------------------------------------------
        // Modelli pronti (regole normali: si possono modificare, disattivare o sostituire per Id)
        // -----------------------------------------------------------------------------------------

        // Righe protette per i pacchetti NuGet i cui Id iniziano con uno dei prefissi (letterali, senza
        // distinzione di maiuscole): packages.config, riferimenti nei progetti (.csproj/.vbproj: Reference
        // con HintPath, PackageReference, Import/Error dei target dei pacchetti), binding redirect nei
        // .config, Directory.Packages.props; anche con il tag di apertura scritto su piu' righe. Nessuna
        // regola di Discard: un pacchetto esterno aggiunto dal task passa. Nessun prefisso = documento
        // vuoto (una regola senza prefisso proteggerebbe tutto).
        public static MergePolicyDocument NuGetPreset(IEnumerable<string> packageIdPrefixes)
        {
            var doc = new MergePolicyDocument();
            var prefixes = (packageIdPrefixes ?? new string[0])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (prefixes.Count == 0)
                return doc;

            var id = "(?:" + string.Join("|", prefixes.Select(Regex.Escape)) + ")";
            var shown = string.Join(", ", prefixes);
            const string Projects = "*.csproj;*.vbproj";

            doc.LineRules.Add(LineRule("preset.nuget.packages-config", "packages.config",
                @"(?i)<package\s[^>]*\bid\s*=\s*[""']" + id,
                null, null, null,
                "packages.config: entries of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(MultiLineTagRule("preset.nuget.packages-config-multiline", "packages.config", "package",
                @"\bid\s*=\s*[""']" + id,
                "packages.config: entries of the packages " + shown + " written on more than one line keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.reference", Projects,
                null,
                @"(?i)<Reference\s[^>]*\bInclude\s*=\s*[""']" + id + @"[^>]*(?<!/)>",
                @"(?i)</Reference\s*>",
                null,
                "Project files: <Reference> blocks (with HintPath) of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.reference-line", Projects,
                @"(?i)<Reference\s[^>]*\bInclude\s*=\s*[""']" + id + @"[^>]*/>",
                null, null, null,
                "Project files: single-line <Reference .../> of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(MultiLineTagRule("preset.nuget.reference-multiline", Projects, "Reference",
                @"\bInclude\s*=\s*[""']" + id,
                "Project files: <Reference> of the packages " + shown + " whose tag is written on more than one line keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.package-reference", Projects,
                null,
                @"(?i)<PackageReference\s[^>]*\b(?:Include|Update)\s*=\s*[""']" + id + @"[^>]*(?<!/)>",
                @"(?i)</PackageReference\s*>",
                null,
                "Project files: <PackageReference> blocks (with <Version>) of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.package-reference-line", Projects,
                @"(?i)<PackageReference\s[^>]*\b(?:Include|Update)\s*=\s*[""']" + id + @"[^>]*/>",
                null, null, null,
                "Project files: single-line <PackageReference .../> of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(MultiLineTagRule("preset.nuget.package-reference-multiline", Projects, "PackageReference",
                @"\b(?:Include|Update)\s*=\s*[""']" + id,
                "Project files: <PackageReference> of the packages " + shown + " whose tag is written on more than one line (e.g. Version on its own line) keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.package-import", Projects,
                @"(?i)<(?:Import|Error)\s[^>]*[\\/]packages[\\/]" + id,
                null, null, null,
                "Project files: <Import> and <Error> lines that point to the packages folder of " + shown + " keep the target's version."));

            doc.LineRules.Add(MultiLineTagRule("preset.nuget.package-import-multiline", Projects, "(?:Import|Error)",
                @"[\\/]packages[\\/]" + id,
                "Project files: <Import> and <Error> written on more than one line that point to the packages folder of " + shown + " keep the target's version."));

            // "name" dentro il blocco (non per forza sulla riga di <assemblyIdentity>: puo' essere a capo);
            // in un <dependentAssembly> solo <assemblyIdentity> ha l'attributo name
            doc.LineRules.Add(LineRule("preset.nuget.binding-redirect", "*.config",
                null,
                @"(?i)<dependentAssembly\b[^>]*(?<!/)>",
                @"(?i)</dependentAssembly\s*>",
                @"(?i)\bname\s*=\s*[""']" + id,
                "Configuration files: binding redirects (<dependentAssembly>) of the assemblies " + shown + " keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.package-version", "Directory.Packages.props",
                @"(?i)<PackageVersion\s[^>]*\b(?:Include|Update)\s*=\s*[""']" + id + @"[^>]*/>",
                null, null, null,
                "Directory.Packages.props: <PackageVersion .../> of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(LineRule("preset.nuget.package-version-block", "Directory.Packages.props",
                null,
                @"(?i)<PackageVersion\s[^>]*\b(?:Include|Update)\s*=\s*[""']" + id + @"[^>]*(?<!/)>",
                @"(?i)</PackageVersion\s*>",
                null,
                "Directory.Packages.props: <PackageVersion> blocks of the packages " + shown + " keep the target's version."));

            doc.LineRules.Add(MultiLineTagRule("preset.nuget.package-version-multiline", "Directory.Packages.props", "PackageVersion",
                @"\b(?:Include|Update)\s*=\s*[""']" + id,
                "Directory.Packages.props: <PackageVersion> of the packages " + shown + " whose tag is written on more than one line keep the target's version."));

            return doc;
        }

        // Elemento il cui tag di apertura va a capo (attributi su piu' righe, es. Version="..." sotto
        // Include="..."): blocco dalla riga "<Tag" non chiusa alla prima riga che chiude il tag ("/>" senza
        // "<" davanti: le righe degli elementi figli cominciano con "<") o, se il tag ha un contenuto, alla
        // riga "</Tag>". Protetto solo se una sua riga soddisfa attributePattern (l'Id del pacchetto puo'
        // stare su qualsiasi riga del tag). Le forme su una riga sono delle altre regole: qui la riga
        // "<Tag" non ha ">".
        private static MergeLineRule MultiLineTagRule(string id, string filePattern, string tag, string attributePattern, string description)
        {
            return LineRule(id, filePattern,
                null,
                @"(?i)<" + tag + @"(?:\s[^>]*)?$",
                @"(?i)^[^<>]*/>|</" + tag + @"\s*>",
                @"(?i)" + attributePattern,
                description);
        }

        // Directory.Packages.props (gestione centralizzata delle versioni NuGet) in qualsiasi cartella:
        // Discard (il merge si registra, resta il file del target).
        public static MergePolicyDocument CentralPackageManagementPreset()
        {
            var doc = new MergePolicyDocument();
            doc.PathRules.Add(new MergePathRule
            {
                Id = "preset.cpm.directory-packages-props",
                Pattern = "**/Directory.Packages.props",
                Action = MergePolicyAction.Discard,
                Description = "Central package versions (Directory.Packages.props) keep the target's file.",
                Enabled = true
            });
            return doc;
        }

        // Attributi di versione dell'assembly (C# "[assembly: AssemblyVersion(...)]", VB
        // "<Assembly: AssemblyVersion(...)>", anche File/Informational): restano quelli del target.
        public static MergePolicyDocument AssemblyVersionPreset()
        {
            var doc = new MergePolicyDocument();
            doc.LineRules.Add(LineRule("preset.assembly-version", "*.cs;*.vb",
                @"(?i)^\s*[\[<]\s*assembly\s*:\s*(?:System\.Reflection\.)?Assembly(?:File|Informational)?Version(?:Attribute)?\s*\(",
                null, null, null,
                "AssemblyVersion, AssemblyFileVersion and AssemblyInformationalVersion attributes keep the target's version."));
            return doc;
        }

        private static MergeLineRule LineRule(string id, string filePattern, string linePattern, string blockStart, string blockEnd, string blockContains, string description)
        {
            return new MergeLineRule
            {
                Id = id,
                FilePattern = filePattern,
                LinePattern = linePattern,
                BlockStartPattern = blockStart,
                BlockEndPattern = blockEnd,
                BlockContainsPattern = blockContains,
                Description = description,
                Enabled = true
            };
        }

        // -----------------------------------------------------------------------------------------
        // Controlli delle regole (usati da EffectiveMergePolicy)
        // -----------------------------------------------------------------------------------------

        internal static string NormalizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            return id.Trim();
        }

        internal static void CheckDocument(MergePolicyDocument doc, MergeRuleOrigin origin, List<string> errors)
        {
            if (doc == null)
                return;

            var name = OriginText(origin);
            if (doc.Version > CurrentVersion)
            {
                errors.Add(PolicyText.Format("The {0} has version {1}: this version of AutoMerge understands version {2}. Update AutoMerge or fix the file.",
                    name, doc.Version, CurrentVersion));
            }

            CheckIds(doc.PathRules, r => r.Id, "path rule", name, errors);
            CheckIds(doc.LineRules, r => r.Id, "line rule", name, errors);
        }

        private static void CheckIds<T>(IList<T> rules, Func<T, string> idOf, string kind, string name, List<string> errors) where T : class
        {
            if (rules == null)
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule == null)
                {
                    errors.Add(PolicyText.Format("The {0} has an empty {1} (#{2}).", name, kind, i + 1));
                    continue;
                }

                var id = NormalizeId(idOf(rule));
                if (id == null)
                {
                    errors.Add(PolicyText.Format("The {0} has a {1} without an Id (#{2}).", name, kind, i + 1));
                    continue;
                }

                if (!seen.Add(id) && reported.Add(id))
                    errors.Add(PolicyText.Format("The {0} has more than one {1} with the Id '{2}'.", name, kind, id));
            }
        }

        internal static void CheckPathRule(EffectivePathRule rule, List<string> errors)
        {
            var label = RuleLabel("Path rule", rule.Rule.Id, rule.Origin);
            if (string.IsNullOrWhiteSpace(rule.Rule.Pattern))
                errors.Add(label + ": the pattern is empty.");
            else if (!IsUsableGlob(rule.Rule.Pattern))
                errors.Add(PolicyText.Format("{0}: the pattern '{1}' does not name any path.", label, rule.Rule.Pattern));

            if (!Enum.IsDefined(typeof(MergePolicyAction), rule.Rule.Action))
                errors.Add(PolicyText.Format("{0}: the action {1} is not valid (use Merge, Discard or Skip).", label, (int)rule.Rule.Action));
        }

        internal static void CheckLineRule(EffectiveLineRule rule, List<string> errors)
        {
            var label = RuleLabel("Line rule", rule.Rule.Id, rule.Origin);
            if (string.IsNullOrWhiteSpace(rule.Rule.FilePattern))
                errors.Add(label + ": the file pattern is empty.");
            else if (!IsUsableGlob(rule.Rule.FilePattern))
                errors.Add(PolicyText.Format("{0}: the file pattern '{1}' does not name any path.", label, rule.Rule.FilePattern));

            string problem;
            if (!TryCompile(rule.Rule, out problem))
                errors.Add(label + ": " + problem);
        }

        private static bool IsUsableGlob(string pattern)
        {
            return pattern.Split(';').Any(part => GlobSegments(part) != null);
        }

        private static string OriginText(MergeRuleOrigin origin)
        {
            return origin == MergeRuleOrigin.Team ? "team merge policy" : "personal merge policy";
        }

        private static string RuleLabel(string kind, string id, MergeRuleOrigin origin)
        {
            return PolicyText.Format("{0} '{1}' ({2})", kind, NormalizeId(id) ?? "(no Id)", origin == MergeRuleOrigin.Team ? "team" : "personal");
        }

        // -----------------------------------------------------------------------------------------
        // Regole compilate e righe protette di un testo
        // -----------------------------------------------------------------------------------------

        // Regole attive compilate; una regola non valida = ArgumentException (chi chiama doveva
        // controllare EffectiveMergePolicy.Errors).
        private static List<CompiledLineRule> CompileRules(IReadOnlyList<MergeLineRule> rules)
        {
            var compiled = new List<CompiledLineRule>();
            if (rules == null)
                return compiled;

            foreach (var rule in rules)
            {
                if (rule == null || !rule.Enabled)
                    continue;

                string problem;
                CompiledLineRule result;
                if (!TryCompile(rule, out problem, out result))
                    throw new ArgumentException(PolicyText.Format("Line rule '{0}': {1}", NormalizeId(rule.Id) ?? "(no Id)", problem), "rules");
                compiled.Add(result);
            }
            return compiled;
        }

        private static bool TryCompile(MergeLineRule rule, out string problem)
        {
            CompiledLineRule ignored;
            return TryCompile(rule, out problem, out ignored);
        }

        private static bool TryCompile(MergeLineRule rule, out string problem, out CompiledLineRule compiled)
        {
            compiled = null;
            var line = Blank(rule.LinePattern);
            var start = Blank(rule.BlockStartPattern);
            var end = Blank(rule.BlockEndPattern);
            var contains = Blank(rule.BlockContainsPattern);

            if (line == null && start == null && end == null)
            {
                problem = "the rule has neither a line pattern nor a block pattern.";
                return false;
            }
            if ((start == null) != (end == null))
            {
                problem = "a block needs both a start pattern and an end pattern.";
                return false;
            }
            if (contains != null && start == null)
            {
                problem = "the 'contains' pattern needs a block (start and end patterns).";
                return false;
            }

            var result = new CompiledLineRule { Id = NormalizeId(rule.Id) ?? "(no Id)" };
            if (!TryRegex(line, "line pattern", out result.Line, out problem)
                || !TryRegex(start, "block start pattern", out result.Start, out problem)
                || !TryRegex(end, "block end pattern", out result.End, out problem)
                || !TryRegex(contains, "block 'contains' pattern", out result.Contains, out problem))
                return false;

            compiled = result;
            problem = null;
            return true;
        }

        private static string Blank(string pattern)
        {
            return string.IsNullOrEmpty(pattern) ? null : pattern;
        }

        private static bool TryRegex(string pattern, string what, out Regex regex, out string problem)
        {
            regex = null;
            problem = null;
            if (pattern == null)
                return true;
            try
            {
                regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
                return true;
            }
            catch (ArgumentException ex)
            {
                problem = PolicyText.Format("the {0} is not a valid regular expression ({1}).", what, ex.Message);
                return false;
            }
        }

        private static string Compose(List<string> lines, string newLine)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                sb.Append(line);
                if (i < lines.Count - 1 && TerminatorLength(line) == 0)
                    sb.Append(newLine);
            }
            return sb.ToString();
        }

        internal static int TerminatorLength(string line)
        {
            var length = line.Length;
            if (length >= 2 && line[length - 2] == '\r' && line[length - 1] == '\n')
                return 2;
            if (length >= 1 && (line[length - 1] == '\n' || line[length - 1] == '\r'))
                return 1;
            return 0;
        }

        // a-capo prevalente ("\r\n" in parita'); null se nessuno
        private static string DetectNewLine(IReadOnlyList<string> lines)
        {
            var crlf = 0;
            var lf = 0;
            var cr = 0;
            foreach (var line in lines)
            {
                var length = TerminatorLength(line);
                if (length == 2)
                    crlf++;
                else if (length == 1 && line[line.Length - 1] == '\n')
                    lf++;
                else if (length == 1)
                    cr++;
            }
            if (crlf == 0 && lf == 0 && cr == 0)
                return null;
            if (crlf >= lf && crlf >= cr)
                return "\r\n";
            return lf >= cr ? "\n" : "\r";
        }

        private sealed class CompiledLineRule
        {
            public string Id;
            public Regex Line;
            public Regex Start;
            public Regex End;
            public Regex Contains;
        }

        // Un'unita' protetta: un blocco (o piu' blocchi sovrapposti fusi) o una riga singola; righe
        // [Start, End] incluse.
        private sealed class ProtectedUnit
        {
            public ProtectedUnit(int start, int end, string ruleId)
            {
                Start = start;
                End = end;
                RuleId = ruleId;
            }

            public int Start { get; set; }

            public int End { get; set; }

            public string RuleId { get; }
        }

        // Righe protette di un testo secondo le regole.
        private sealed class ProtectionMap
        {
            private ProtectionMap(int count)
            {
                RuleOf = new string[count];
                UnitOf = new int[count];
                for (var i = 0; i < count; i++)
                    UnitOf[i] = -1;
                Units = new List<ProtectedUnit>();
            }

            // per riga: Id della regola che la protegge (null = non protetta)
            public string[] RuleOf { get; }

            // per riga: indice in Units (-1 = non protetta)
            public int[] UnitOf { get; }

            public List<ProtectedUnit> Units { get; }

            // contenuto delle righe senza terminatore
            public string[] RawContents { get; private set; }

            // come RawContents, senza il BOM iniziale: e' il testo su cui si confrontano le regole
            public string[] Contents { get; private set; }

            public bool IsProtected(int index)
            {
                return index >= 0 && index < RuleOf.Length && RuleOf[index] != null;
            }

            public List<int> ProtectedIndices()
            {
                var result = new List<int>();
                for (var i = 0; i < RuleOf.Length; i++)
                {
                    if (RuleOf[i] != null)
                        result.Add(i);
                }
                return result;
            }

            public static ProtectionMap Analyze(IReadOnlyList<string> lines, List<CompiledLineRule> rules)
            {
                var map = new ProtectionMap(lines.Count);
                map.RawContents = new string[lines.Count];
                map.Contents = new string[lines.Count];
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var content = line.Substring(0, line.Length - TerminatorLength(line));
                    map.RawContents[i] = content;
                    map.Contents[i] = i == 0 ? content.TrimStart('\uFEFF') : content;
                }

                var lineRule = new string[lines.Count];
                var blocks = new List<ProtectedUnit>();
                foreach (var rule in rules)
                {
                    if (rule.Line != null)
                    {
                        for (var i = 0; i < lines.Count; i++)
                        {
                            if (lineRule[i] == null && rule.Line.IsMatch(map.Contents[i]))
                                lineRule[i] = rule.Id;
                        }
                    }

                    if (rule.Start != null)
                        FindBlocks(map.Contents, rule, blocks);
                }

                // blocchi sovrapposti (di regole diverse) = un'unica unita'
                var merged = new List<ProtectedUnit>();
                foreach (var block in blocks.OrderBy(b => b.Start).ThenByDescending(b => b.End))
                {
                    var last = merged.Count == 0 ? null : merged[merged.Count - 1];
                    if (last != null && block.Start <= last.End)
                        last.End = Math.Max(last.End, block.End);
                    else
                        merged.Add(new ProtectedUnit(block.Start, block.End, block.RuleId));
                }

                var next = 0;
                var index = 0;
                while (index < lines.Count)
                {
                    if (next < merged.Count && merged[next].Start == index)
                    {
                        var unit = merged[next++];
                        map.Units.Add(unit);
                        for (var x = unit.Start; x <= unit.End; x++)
                        {
                            map.RuleOf[x] = unit.RuleId;
                            map.UnitOf[x] = map.Units.Count - 1;
                        }
                        index = unit.End + 1;
                    }
                    else if (lineRule[index] != null)
                    {
                        map.Units.Add(new ProtectedUnit(index, index, lineRule[index]));
                        map.RuleOf[index] = lineRule[index];
                        map.UnitOf[index] = map.Units.Count - 1;
                        index++;
                    }
                    else
                    {
                        index++;
                    }
                }

                return map;
            }

            // Blocchi della regola: dalla riga di inizio alla prima riga di fine (cercata dalla riga di
            // inizio stessa); un inizio senza fine non e' un blocco. Protetti solo se una riga soddisfa
            // Contains (quando c'e').
            private static void FindBlocks(string[] contents, CompiledLineRule rule, List<ProtectedUnit> blocks)
            {
                // ogni regex una volta per riga (niente scansioni ripetute fino in fondo al file):
                // nextEnd[i] = prima riga >= i che chiude un blocco (-1 = nessuna);
                // containsBefore[i] = quante righe prima di i soddisfano Contains
                var count = contents.Length;
                var nextEnd = new int[count + 1];
                nextEnd[count] = -1;
                for (var i = count - 1; i >= 0; i--)
                    nextEnd[i] = rule.End.IsMatch(contents[i]) ? i : nextEnd[i + 1];

                int[] containsBefore = null;
                if (rule.Contains != null)
                {
                    containsBefore = new int[count + 1];
                    for (var i = 0; i < count; i++)
                        containsBefore[i + 1] = containsBefore[i] + (rule.Contains.IsMatch(contents[i]) ? 1 : 0);
                }

                var index = 0;
                while (index < count)
                {
                    var end = nextEnd[index];
                    if (end < 0)
                        break; // da qui in poi nessuna riga di fine: nessun altro blocco
                    if (!rule.Start.IsMatch(contents[index]))
                    {
                        index++;
                        continue;
                    }

                    if (containsBefore == null || containsBefore[end + 1] - containsBefore[index] > 0)
                        blocks.Add(new ProtectedUnit(index, end, rule.Id));
                    index = end + 1;
                }
            }
        }

        private sealed class HunkUnit
        {
            public HunkUnit(int start, int end, bool isProtected, string ruleId, string key)
            {
                Start = start;
                End = end;
                Protected = isProtected;
                RuleId = ruleId;
                Key = key;
            }

            public int Start { get; }

            public int End { get; }

            public bool Protected { get; }

            public string RuleId { get; }

            public string Key { get; }
        }

        private sealed class Hunk
        {
            public Hunk(int b0, int b1, int x0, int x1, bool fromSource)
            {
                B0 = b0;
                B1 = b1;
                X0 = x0;
                X1 = x1;
                FromSource = fromSource;
            }

            public int B0 { get; }

            public int B1 { get; }

            public int X0 { get; }

            public int X1 { get; }

            public bool FromSource { get; }
        }

        private sealed class NeutralizedSource
        {
            public NeutralizedSource()
            {
                Lines = new List<string>();
                Changes = new List<DiscardedChange>();
            }

            public List<string> Lines { get; }

            public string Text { get; set; }

            public List<DiscardedChange> Changes { get; }
        }

        // Modifica della sorgente su un'unita' protetta, scartata.
        private sealed class DiscardedChange
        {
            public DiscardedChange()
            {
                SourceStart = -1;
                NeutralStart = -1;
                NeutralEnd = -1;
            }

            public string RuleId { get; set; }

            // righe dell'unita' nella base (null = la sorgente aggiunge)
            public List<string> BaseContents { get; set; }

            // righe dell'unita' nella sorgente (null = la sorgente cancella)
            public List<string> SourceContents { get; set; }

            public int SourceStart { get; set; }

            // righe della sorgente neutralizzata che portano l'unita' della base
            public int NeutralStart { get; set; }

            public int NeutralEnd { get; set; }
        }
    }

    // Testi in inglese (formato invariante).
    internal static class PolicyText
    {
        private const int MaxShortLength = 120;

        public static string Format(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        // prima riga (senza spazi ai lati), accorciata; "(+N more lines)" se ce ne sono altre
        public static string Short(string text)
        {
            if (text == null)
                return string.Empty;
            var lines = text.Split('\n');
            var first = lines[0].Trim();
            if (first.Length > MaxShortLength)
                first = first.Substring(0, MaxShortLength - 3) + "...";
            if (lines.Length > 1)
                first += Format(" (+{0} more {1})", lines.Length - 1, lines.Length == 2 ? "line" : "lines");
            return first;
        }
    }
}
