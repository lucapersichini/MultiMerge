// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Text;

namespace MultiMerge
{
    // Corrispondenza fra le regioni ancora aperte nel risultato (in ordine di testo) e i blocchi
    // Conflict della fusione originale, per portare il confronto SOURCE | TARGET sul blocco corrente.
    //
    // Si ricostruisce a ogni rilettura del risultato (Update): uno stato intermedio (marker
    // malformato mentre l'utente riscrive una riga, Take seguito da Ctrl+Z, regione cancellata a mano)
    // non la spegne per sempre.
    // 1. Per contenuto: ogni regione prende il primo blocco originale non ancora usato con le stesse
    //    righe source e target, confrontate senza terminatori (nel risultato le righe del source hanno
    //    l'a-capo del target). I blocchi risolti con Take source/target/both (MarkResolved) si
    //    riprendono solo se nessun altro blocco corrisponde (es. Ctrl+Z dopo un Take). Fra regioni con
    //    lo stesso contenuto i blocchi si danno in ordine di testo.
    // 2. Per posizione: una serie di regioni modificate a mano (contenuto che non corrisponde piu' a
    //    nessun blocco) fra due regioni riconosciute prende i blocchi liberi fra quei due, se sono
    //    esattamente tanti quante le regioni.
    // 3. Le altre regioni restano senza blocco (null): meglio nessuno scorrimento del confronto che
    //    uno scorrimento sul blocco sbagliato.
    public sealed class OpenConflictMap
    {
        // Separatore fra la parte source e la parte target di una chiave: nessuna riga senza
        // terminatore contiene '\r'.
        private const char SideSeparator = '\r';

        private readonly List<ConflictLocation> _blocks;
        // Blocchi per contenuto (chiave -> indici in ordine crescente); i blocchi senza righe note non ci sono.
        private readonly Dictionary<string, List<int>> _blocksByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        // Blocchi tolti dal testo con un Take (non ancora ricomparsi).
        private readonly bool[] _resolved;
        // Blocco (indice in _blocks) di ogni regione dell'ultimo Update; -1 = nessuno.
        private int[] _regionBlocks = new int[0];

        // locations: i blocchi Conflict della fusione (ThreeWayMerge.GetConflictLocations), in ordine.
        // Poi Update con le regioni trovate nel testo.
        public OpenConflictMap(IEnumerable<ConflictLocation> locations)
        {
            _blocks = new List<ConflictLocation>();
            if (locations != null)
            {
                foreach (var location in locations)
                {
                    if (location != null)
                        _blocks.Add(location);
                }
            }
            _resolved = new bool[_blocks.Count];

            for (var i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block.SourceLines == null || block.TargetLines == null)
                    continue;
                var key = BuildKey(block.SourceLines, block.TargetLines);
                List<int> indexes;
                if (!_blocksByKey.TryGetValue(key, out indexes))
                {
                    indexes = new List<int>();
                    _blocksByKey.Add(key, indexes);
                }
                indexes.Add(i);
            }
        }

        // True se almeno una regione dell'ultimo Update ha il suo blocco.
        public bool IsAvailable
        {
            get
            {
                foreach (var block in _regionBlocks)
                {
                    if (block >= 0)
                        return true;
                }
                return false;
            }
        }

        // Regioni dell'ultimo Update.
        public int Count
        {
            get { return _regionBlocks.Length; }
        }

        // La regione regionIndex (dell'ultimo Update) sta per essere risolta con Take source/target/both:
        // il suo blocco esce dal testo e si riprende solo se il contenuto ricompare e nessun altro blocco
        // corrisponde.
        public void MarkResolved(int regionIndex)
        {
            if (regionIndex < 0 || regionIndex >= _regionBlocks.Length)
                return;
            var block = _regionBlocks[regionIndex];
            if (block >= 0)
                _resolved[block] = true;
        }

        // Il risultato e' stato riletto: regions sono le regioni ben formate, in ordine di testo.
        public void Update(IReadOnlyList<ConflictRegion> regions)
        {
            var count = regions == null ? 0 : regions.Count;
            var assigned = new int[count];
            var keys = new string[count];
            for (var i = 0; i < count; i++)
            {
                assigned[i] = -1;
                var region = regions[i];
                keys[i] = region == null ? null : BuildKey(ThreeWayMerge.SplitLines(region.SourceText), ThreeWayMerge.SplitLines(region.TargetText));
            }

            var used = new bool[_blocks.Count];
            AssignByContent(keys, assigned, used, false);
            AssignByContent(keys, assigned, used, true);
            OrderEqualContent(keys, assigned);
            AssignByPosition(assigned, used);

            // I blocchi di nuovo nel testo non sono piu' risolti.
            foreach (var block in assigned)
            {
                if (block >= 0)
                    _resolved[block] = false;
            }
            _regionBlocks = assigned;
        }

        // Il conflitto originale della regione regionIndex; null se non si sa.
        public ConflictLocation Get(int regionIndex)
        {
            if (regionIndex < 0 || regionIndex >= _regionBlocks.Length)
                return null;
            var block = _regionBlocks[regionIndex];
            return block >= 0 ? _blocks[block] : null;
        }

        // Ogni regione ancora senza blocco prende il primo blocco libero con lo stesso contenuto;
        // includeResolved: anche fra quelli tolti con un Take.
        private void AssignByContent(string[] keys, int[] assigned, bool[] used, bool includeResolved)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                List<int> candidates;
                if (assigned[i] >= 0 || keys[i] == null || !_blocksByKey.TryGetValue(keys[i], out candidates))
                    continue;
                foreach (var block in candidates)
                {
                    if (used[block] || (_resolved[block] && !includeResolved))
                        continue;
                    assigned[i] = block;
                    used[block] = true;
                    break;
                }
            }
        }

        // Regioni con lo stesso contenuto: i loro blocchi in ordine crescente, nell'ordine del testo
        // (dopo Ctrl+Z su un Take la regione ricomparsa puo' aver preso il blocco di un'altra).
        private static void OrderEqualContent(string[] keys, int[] assigned)
        {
            var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < keys.Length; i++)
            {
                if (assigned[i] < 0 || keys[i] == null)
                    continue;
                List<int> regions;
                if (!groups.TryGetValue(keys[i], out regions))
                {
                    regions = new List<int>();
                    groups.Add(keys[i], regions);
                }
                regions.Add(i);
            }

            foreach (var regions in groups.Values)
            {
                if (regions.Count < 2)
                    continue;
                var blocks = new List<int>(regions.Count);
                foreach (var region in regions)
                    blocks.Add(assigned[region]);
                blocks.Sort();
                for (var i = 0; i < regions.Count; i++)
                    assigned[regions[i]] = blocks[i];
            }
        }

        // Serie di regioni senza blocco fra due regioni riconosciute (o fra l'inizio / la fine del testo):
        // prendono in ordine i blocchi liberi (non usati, non risolti con un Take) compresi fra i due, se
        // sono esattamente tanti quante le regioni.
        private void AssignByPosition(int[] assigned, bool[] used)
        {
            var previousBlock = -1;
            var i = 0;
            while (i < assigned.Length)
            {
                if (assigned[i] >= 0)
                {
                    previousBlock = assigned[i];
                    i++;
                    continue;
                }

                var end = i;
                while (end < assigned.Length && assigned[end] < 0)
                    end++;
                var nextBlock = end < assigned.Length ? assigned[end] : _blocks.Count;

                if (nextBlock > previousBlock)
                {
                    var free = new List<int>();
                    for (var block = previousBlock + 1; block < nextBlock; block++)
                    {
                        if (!used[block] && !_resolved[block])
                            free.Add(block);
                    }
                    if (free.Count == end - i)
                    {
                        for (var k = 0; k < free.Count; k++)
                        {
                            assigned[i + k] = free[k];
                            used[free[k]] = true;
                        }
                    }
                }
                i = end;
            }
        }

        // Righe source e target senza terminatori, ciascuna seguita da '\n' (cosi' "nessuna riga" e
        // "una riga vuota" restano diverse).
        private static string BuildKey(IReadOnlyList<string> sourceLines, IReadOnlyList<string> targetLines)
        {
            var builder = new StringBuilder();
            AppendLines(builder, sourceLines);
            builder.Append(SideSeparator);
            AppendLines(builder, targetLines);
            return builder.ToString();
        }

        private static void AppendLines(StringBuilder builder, IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                var length = line.Length;
                if (length > 0 && line[length - 1] == '\n')
                    length--;
                if (length > 0 && line[length - 1] == '\r')
                    length--;
                builder.Append(line, 0, length).Append('\n');
            }
        }
    }
}
