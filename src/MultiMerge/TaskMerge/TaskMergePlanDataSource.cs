// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    // Legge da TFVC tutto cio' che serve a TaskMergePlanner (che resta puro): i cambi dei changeset
    // del task, l'esistenza degli item nel target e i changeset di terzi tra due changeset consecutivi
    // del task sullo stesso item. Nessuna UI e nessuna modifica al workspace: si chiama fuori dal
    // thread UI e si puo' riusare da un harness.
    //
    // I dati si leggono UNA volta (GatherData) e servono poi per qualsiasi scelta di changeset del task
    // (TaskMergePlanData.CreateInput): un changeset del task non selezionato diventa, per il planner, un
    // changeset "di terzi" come quelli dei colleghi, senza altre letture da TFVC.
    public static class TaskMergePlanDataSource
    {
        // Item per chiamata batch a GetItems (richieste SOAP di dimensione ragionevole).
        private const int ItemsPerRequest = 200;

        internal static readonly IReadOnlyList<int> NoChangesets = new List<int>().AsReadOnly();

        // Ingresso del planner con TUTTI i changeset del gruppo selezionati.
        public static TaskMergePlanInput Gather(VersionControlServer vcs, TaskChangesetGroup group, string targetBranchRoot, Action<string> progress)
        {
            var data = GatherData(vcs, group, targetBranchRoot, progress);
            return data.CreateInput(data.AllChangesetIds);
        }

        // Legge cambi, dettagli dei changeset, esistenza nel target e storia degli item toccati piu'
        // volte. Un errore di TFVC esce come eccezione (il Load si ferma con il suo messaggio).
        public static TaskMergePlanData GatherData(VersionControlServer vcs, TaskChangesetGroup group, string targetBranchRoot, Action<string> progress)
        {
            if (vcs == null)
                throw new ArgumentNullException("vcs");
            if (group == null)
                throw new ArgumentNullException("group");
            if (string.IsNullOrWhiteSpace(targetBranchRoot))
                throw new ArgumentException("The target branch is required.", "targetBranchRoot");

            var report = progress ?? (text => { });
            var sourceBranch = (group.SourceBranch ?? string.Empty).Trim().TrimEnd('/');
            var targetRoot = targetBranchRoot.Trim().TrimEnd('/');
            var taskIds = (group.ChangesetIds ?? new List<int>()).Distinct().OrderBy(id => id).ToList();

            // 1. Change[] di ogni changeset (una volta: la cache del gruppo serve anche altrove)
            var changesById = ReadChanges(vcs, group, taskIds, report);

            // 2. Cambi per il planner. Per una Delete il server item del change e' il path cancellato.
            var changes = new List<TaskChangeInfo>();
            foreach (var id in taskIds)
            {
                Change[] list;
                if (!changesById.TryGetValue(id, out list) || list == null)
                    continue;
                foreach (var change in list)
                {
                    if (change == null)
                        continue;
                    var item = change.Item;
                    changes.Add(new TaskChangeInfo(
                        id,
                        item == null ? null : item.ServerItem,
                        item != null && item.ItemType == ItemType.Folder ? TaskMergeItemKind.Folder : TaskMergeItemKind.File,
                        ToChangeKind(change.ChangeType)));
                }
            }

            // 3. Dettagli dei changeset per il pannello delle caselle (data, autore, commento)
            var details = ReadChangesetDetails(vcs, taskIds, changesById, report);

            // 4. Esistenza nel target a Latest: una chiamata batch per tutti gli item del task
            var targetPaths = changes
                .Select(c => MapToTarget(sourceBranch, targetRoot, c.SourceServerItem))
                .Where(p => p != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            report(string.Format(CultureInfo.InvariantCulture, "Checking {0} item(s) in the target...", targetPaths.Count));
            var existing = QueryExistingItems(vcs, targetPaths);

            var data = new TaskMergePlanData(vcs, sourceBranch, targetRoot, taskIds, changes, details, targetPaths, existing);

            // 5. Storia degli item multi-touch: coppie di changeset consecutivi del task sull'item.
            //    Letta qui (con avanzamento) e servita dalla cache quando il planner la chiede, per
            //    qualsiasi scelta di changeset.
            data.PrefetchHistory(report);
            return data;
        }

        // ChangeType di TFVC -> TaskChangeKind. Lock non e' un cambio di contenuto (il merge non lo
        // porta) e None (valore 1 in TFVC) non e' un cambio: entrambi si ignorano. Un bit sconosciuto
        // diventa Other, che il planner rifiuta; nessun cambio riconosciuto resta None ("unknown").
        public static TaskChangeKind ToChangeKind(ChangeType changeType)
        {
            var rest = changeType & ~(ChangeType.None | ChangeType.Lock);
            var result = TaskChangeKind.None;

            result |= Map(ref rest, ChangeType.Add, TaskChangeKind.Add);
            result |= Map(ref rest, ChangeType.Edit, TaskChangeKind.Edit);
            result |= Map(ref rest, ChangeType.Delete, TaskChangeKind.Delete);
            result |= Map(ref rest, ChangeType.Encoding, TaskChangeKind.Encoding);
            result |= Map(ref rest, ChangeType.Property, TaskChangeKind.Property);
            result |= Map(ref rest, ChangeType.Rename, TaskChangeKind.Rename);
            result |= Map(ref rest, ChangeType.SourceRename, TaskChangeKind.SourceRename);
            result |= Map(ref rest, ChangeType.Undelete, TaskChangeKind.Undelete);
            result |= Map(ref rest, ChangeType.Branch, TaskChangeKind.Branch);
            result |= Map(ref rest, ChangeType.Merge, TaskChangeKind.Merge);
            result |= Map(ref rest, ChangeType.Rollback, TaskChangeKind.Rollback);

            if (rest != 0)
                result |= TaskChangeKind.Other;
            return result;
        }

        private static TaskChangeKind Map(ref ChangeType rest, ChangeType flag, TaskChangeKind kind)
        {
            if ((rest & flag) != flag)
                return TaskChangeKind.None;
            rest &= ~flag;
            return kind;
        }

        // Stessa mappatura del planner: target = radice del target + parte dell'item dopo il branch
        // sorgente (prefisso case-insensitive). Null se l'item non e' sotto il branch sorgente.
        internal static string MapToTarget(string sourceBranch, string targetRoot, string sourceItem)
        {
            if (string.IsNullOrWhiteSpace(sourceItem) || string.IsNullOrEmpty(sourceBranch))
                return null;
            var item = sourceItem.TrimEnd('/');
            if (string.Equals(item, sourceBranch, StringComparison.OrdinalIgnoreCase))
                return targetRoot;
            if (item.Length > sourceBranch.Length + 1
                && item.StartsWith(sourceBranch, StringComparison.OrdinalIgnoreCase)
                && item[sourceBranch.Length] == '/')
                return targetRoot + item.Substring(sourceBranch.Length);
            return null;
        }

        private static Dictionary<int, Change[]> ReadChanges(VersionControlServer vcs, TaskChangesetGroup group,
            List<int> taskIds, Action<string> report)
        {
            var changesById = group.ChangesByChangesetId ?? new Dictionary<int, Change[]>();
            var service = new ChangesetService(vcs);
            var missing = taskIds.Where(id => !changesById.ContainsKey(id) || changesById[id] == null).ToList();
            for (var i = 0; i < missing.Count; i++)
            {
                report(string.Format(CultureInfo.InvariantCulture, "Reading the changes of C{0} ({1}/{2})...",
                    missing[i], i + 1, missing.Count));
                changesById[missing[i]] = service.GetChanges(missing[i]);
            }
            group.ChangesByChangesetId = changesById;
            return changesById;
        }

        // Data, autore e commento di ogni changeset: solo per il pannello delle caselle. Un changeset
        // che non si riesce a leggere resta nell'elenco senza dettagli (il piano non ne dipende).
        private static List<TaskMergeChangesetInfo> ReadChangesetDetails(VersionControlServer vcs, List<int> taskIds,
            Dictionary<int, Change[]> changesById, Action<string> report)
        {
            var result = new List<TaskMergeChangesetInfo>();
            for (var i = 0; i < taskIds.Count; i++)
            {
                var id = taskIds[i];
                report(string.Format(CultureInfo.InvariantCulture, "Reading changeset C{0} ({1}/{2})...", id, i + 1, taskIds.Count));

                Change[] changes;
                if (!changesById.TryGetValue(id, out changes) || changes == null)
                    changes = new Change[0];
                var folderCount = changes.Count(c => c != null && c.Item != null && c.Item.ItemType == ItemType.Folder);
                var fileCount = changes.Count(c => c != null) - folderCount;
                try
                {
                    var changeset = vcs.GetChangeset(id, false, false);
                    result.Add(new TaskMergeChangesetInfo(id, changeset.CreationDate,
                        changeset.OwnerDisplayName ?? changeset.Owner, changeset.Comment, fileCount, folderCount));
                }
                catch (Exception)
                {
                    result.Add(new TaskMergeChangesetInfo(id, null, null, null, fileCount, folderCount));
                }
            }
            return result;
        }

        // Item del target che esistono a Latest (non cancellati), in chiamate batch. Se una chiamata
        // batch fallisce (es. un path non valido) si ripiega sulla domanda singola per quel gruppo: un
        // errore anche li' ferma il Load con il suo messaggio.
        private static HashSet<string> QueryExistingItems(VersionControlServer vcs, List<string> targetPaths)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var start = 0; start < targetPaths.Count; start += ItemsPerRequest)
            {
                var chunk = targetPaths.Skip(start).Take(ItemsPerRequest).ToList();
                ItemSet[] sets;
                try
                {
                    sets = vcs.GetItems(chunk.Select(p => new ItemSpec(p, RecursionType.None)).ToArray(),
                        VersionSpec.Latest, DeletedState.NonDeleted, ItemType.Any);
                }
                catch (Exception)
                {
                    sets = null;
                }

                if (sets == null)
                {
                    foreach (var path in chunk)
                    {
                        if (vcs.ServerItemExists(path, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.Any))
                            existing.Add(path);
                    }
                    continue;
                }

                foreach (var set in sets)
                {
                    if (set == null || set.Items == null)
                        continue;
                    foreach (var item in set.Items)
                    {
                        if (item != null && item.DeletionId == 0 && !string.IsNullOrEmpty(item.ServerItem))
                            existing.Add(item.ServerItem.TrimEnd('/'));
                    }
                }
            }
            return existing;
        }

        // Changeset che toccano l'item (solo l'item: RecursionType.None) con versionFrom <= id <=
        // versionTo. Due letture, e vale l'UNIONE: item mode (la query verificata sul campo: segue
        // l'identita' dell'item) e slot mode (segue il path, che e' cio' su cui lavora il merge per file:
        // vede anche un item cancellato e ricreato da altri nell'intervallo). Un changeset in piu' costa
        // al massimo un passo, un check-in o un blocco in piu'; uno perso porterebbe nel target la
        // modifica di un collega. Ogni lettura risolve il path alla versione pathVersion e, se non ci
        // riesce (es. quella versione cancella l'item), alla precedente. Servono ENTRAMBE: se una
        // delle due non riesce la storia e' incompleta e si restituisce null (storia non disponibile:
        // errore di piano, o problema bloccante del controllo finale), mai la sola lettura riuscita.
        internal static List<int> ReadItemHistory(VersionControlServer vcs, string item, int pathVersion,
            int versionFrom, int versionTo, out Exception error)
        {
            Exception itemError;
            Exception slotError;
            var byItem = TryReadHistory(vcs, item, pathVersion, versionFrom, versionTo, false, out itemError);
            var bySlot = TryReadHistory(vcs, item, pathVersion, versionFrom, versionTo, true, out slotError);
            if (byItem == null || bySlot == null)
            {
                var failed = byItem == null ? "item" : "path (slot)";
                var cause = byItem == null ? itemError : slotError;
                error = new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "the {0} history of {1} between C{2} and C{3} could not be read{4}",
                    failed, item, versionFrom, versionTo, cause == null ? string.Empty : ": " + cause.Message), cause);
                return null;
            }

            error = null;
            return byItem.Concat(bySlot)
                .Where(id => id >= versionFrom && id <= versionTo)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
        }

        private static List<int> TryReadHistory(VersionControlServer vcs, string item, int pathVersion,
            int versionFrom, int versionTo, bool slotMode, out Exception error)
        {
            error = null;
            foreach (var version in new[] { pathVersion, pathVersion - 1 })
            {
                if (version <= 0)
                    continue;
                try
                {
                    return vcs.QueryHistory(item, new ChangesetVersionSpec(version), 0, RecursionType.None, null,
                            new ChangesetVersionSpec(versionFrom), new ChangesetVersionSpec(versionTo), int.MaxValue,
                            false, slotMode, false, false)
                        .Cast<Changeset>()
                        .Select(c => c.ChangesetId)
                        .ToList();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }
            return null;
        }
    }

    // Dati di un changeset del task per il pannello delle caselle.
    public sealed class TaskMergeChangesetInfo
    {
        public TaskMergeChangesetInfo(int changesetId, DateTime? creationDate, string author, string comment, int fileCount, int folderCount)
        {
            ChangesetId = changesetId;
            CreationDate = creationDate;
            Author = author;
            Comment = comment;
            FileCount = fileCount;
            FolderCount = folderCount;
        }

        public int ChangesetId { get; private set; }

        // null se il changeset non si e' potuto leggere
        public DateTime? CreationDate { get; private set; }

        public string Author { get; private set; }

        public string Comment { get; private set; }

        // numero di file cambiati dal changeset
        public int FileCount { get; private set; }

        // numero di cartelle cambiate dal changeset
        public int FolderCount { get; private set; }
    }

    // Changeset di colleghi che cambiano un item tra due passi del task e che NON risultano fusi nel
    // target: il passo successivo porta le modifiche del task sopra una versione che non li contiene.
    public sealed class TaskMergeColleagueWarning
    {
        public TaskMergeColleagueWarning(string sourceItem, string targetItem, string relativePath,
            IReadOnlyList<int> notInTarget, IReadOnlyList<int> inTarget, IReadOnlyList<int> laterStepNumbers,
            bool mergeHistoryUnavailable, string message)
        {
            SourceItem = sourceItem;
            TargetItem = targetItem;
            RelativePath = relativePath;
            NotInTarget = notInTarget;
            InTarget = inTarget;
            LaterStepNumbers = laterStepNumbers;
            MergeHistoryUnavailable = mergeHistoryUnavailable;
            Message = message;
        }

        public string SourceItem { get; private set; }

        public string TargetItem { get; private set; }

        public string RelativePath { get; private set; }

        // changeset di colleghi sull'item, tra i changeset del task, non fusi nel target
        public IReadOnlyList<int> NotInTarget { get; private set; }

        // changeset di colleghi sull'item, tra i changeset del task, gia' fusi nel target
        public IReadOnlyList<int> InTarget { get; private set; }

        // passi dell'item che vengono dopo almeno uno dei changeset non fusi
        public IReadOnlyList<int> LaterStepNumbers { get; private set; }

        // la storia dei merge verso il target non si e' potuta leggere: tutti contati come non fusi
        public bool MergeHistoryUnavailable { get; private set; }

        public string Message { get; private set; }
    }

    // Dati letti da TFVC per pianificare un task: servono per ogni scelta di changeset. Le letture
    // pigre (storia non prevista, merge verso il target) sono in cache e protette da un lock: si
    // chiamano fuori dal thread UI.
    public sealed class TaskMergePlanData
    {
        private readonly VersionControlServer _vcs;
        private readonly object _sync = new object();
        private readonly HashSet<string> _queried;
        private readonly HashSet<string> _existing;
        private readonly Dictionary<string, bool> _lateAnswers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // item sorgente -> changeset del task (tutti, selezionati o no) che lo toccano, crescenti
        private readonly Dictionary<string, List<int>> _touchesByItem = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        // "ITEM|x|y" -> changeset (grezzi, di chiunque) che toccano l'item con x < id < y
        private readonly Dictionary<string, IReadOnlyList<int>> _rawHistory = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        // item sorgente -> changeset sorgente gia' fusi verso l'item target (null = non leggibile)
        private readonly Dictionary<string, HashSet<int>> _mergedIntoTarget = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _allTaskIds;

        internal TaskMergePlanData(VersionControlServer vcs, string sourceBranch, string targetBranch, List<int> taskIds,
            List<TaskChangeInfo> changes, List<TaskMergeChangesetInfo> changesets, List<string> queriedTargets, HashSet<string> existing)
        {
            _vcs = vcs;
            SourceBranch = sourceBranch;
            TargetBranch = targetBranch;
            AllChangesetIds = taskIds.AsReadOnly();
            AllChanges = changes.AsReadOnly();
            Changesets = changesets.AsReadOnly();
            _queried = new HashSet<string>(queriedTargets, StringComparer.OrdinalIgnoreCase);
            _existing = existing;
            _allTaskIds = new HashSet<int>(taskIds);

            foreach (var group in changes
                .Where(c => c.SourceServerItem != null)
                .GroupBy(c => c.SourceServerItem.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
            {
                _touchesByItem[group.Key] = group.Select(c => c.ChangesetId).Distinct().OrderBy(id => id).ToList();
            }
        }

        public string SourceBranch { get; private set; }

        public string TargetBranch { get; private set; }

        // tutti i changeset del gruppo (crescenti)
        public IReadOnlyList<int> AllChangesetIds { get; private set; }

        // cambi di TUTTI i changeset del gruppo
        public IReadOnlyList<TaskChangeInfo> AllChanges { get; private set; }

        public IReadOnlyList<TaskMergeChangesetInfo> Changesets { get; private set; }

        public bool IsTaskChangeset(int changesetId)
        {
            return _allTaskIds.Contains(changesetId);
        }

        // Ingresso del planner per i soli changeset selezionati: i non selezionati diventano changeset
        // "di terzi" (non entrano mai; se stanno tra due selezionati sullo stesso item il planner mette
        // il check-in dove serve).
        public TaskMergePlanInput CreateInput(IEnumerable<int> selectedChangesetIds)
        {
            var selected = new HashSet<int>((selectedChangesetIds ?? new int[0]).Where(id => _allTaskIds.Contains(id)));
            var ids = selected.OrderBy(id => id).ToList();
            return new TaskMergePlanInput
            {
                SourceBranch = SourceBranch,
                TargetBranch = TargetBranch,
                TaskChangesetIds = ids.AsReadOnly(),
                Changes = AllChanges.Where(c => selected.Contains(c.ChangesetId)).ToList().AsReadOnly(),
                TargetItemExists = TargetItemExists,
                ThirdPartyChangesetsBetween = (item, a, b) => ThirdPartyBetween(item, a, b, selected)
            };
        }

        // Target server path -> esiste a Latest (non cancellato). Path non previsto: domanda singola.
        public bool TargetItemExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var normalized = path.TrimEnd('/');
            if (_queried.Contains(normalized))
                return _existing.Contains(normalized);

            lock (_sync)
            {
                bool exists;
                if (!_lateAnswers.TryGetValue(normalized, out exists))
                {
                    exists = _vcs.ServerItemExists(normalized, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.Any);
                    _lateAnswers[normalized] = exists;
                }
                return exists;
            }
        }

        // Changeset che toccano l'item con a < id < b e che NON sono tra i selezionati: quelli dei
        // colleghi e quelli del task esclusi dall'utente. Se a e b sono changeset del task sull'item,
        // l'intervallo si ricompone dalle coppie consecutive gia' lette (nessuna nuova lettura).
        internal IReadOnlyList<int> ThirdPartyBetween(string item, int a, int b, HashSet<int> selected)
        {
            if (string.IsNullOrWhiteSpace(item))
                throw new ArgumentException("The item is required.", "item");
            if (a >= b)
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "Invalid changeset pair C{0}, C{1} for {2}.", a, b, item));

            var key = item.TrimEnd('/');
            var result = new SortedSet<int>();
            List<int> touches;
            if (_touchesByItem.TryGetValue(key, out touches) && touches.BinarySearch(a) >= 0 && touches.BinarySearch(b) >= 0)
            {
                var chain = touches.Where(t => t >= a && t <= b).ToList();
                for (var i = 1; i < chain.Count; i++)
                {
                    foreach (var id in RawHistory(key, chain[i - 1], chain[i]))
                        result.Add(id);
                }
                // changeset del task sull'item tra a e b: non selezionati (a e b sono consecutivi tra i
                // selezionati), quindi terzi per il planner
                for (var i = 1; i < chain.Count - 1; i++)
                    result.Add(chain[i]);
            }
            else
            {
                foreach (var id in RawHistory(key, a, b))
                    result.Add(id);
            }

            return result.Where(id => id > a && id < b && !selected.Contains(id)).ToList().AsReadOnly();
        }

        internal void PrefetchHistory(Action<string> report)
        {
            var pairs = _touchesByItem
                .SelectMany(p => Enumerable.Range(1, Math.Max(0, p.Value.Count - 1))
                    .Select(j => Tuple.Create(p.Key, p.Value[j - 1], p.Value[j])))
                .Where(t => t.Item3 - t.Item2 > 1)
                .ToList();

            for (var i = 0; i < pairs.Count; i++)
            {
                report(string.Format(CultureInfo.InvariantCulture,
                    "Reading the history of items changed more than once ({0}/{1})...", i + 1, pairs.Count));
                RawHistory(pairs[i].Item1, pairs[i].Item2, pairs[i].Item3);
            }
        }

        // Changeset grezzi (di chiunque, task compreso) che toccano l'item con x < id < y. Errore di
        // lettura: eccezione (il piano non si costruisce su una storia mancante).
        private IReadOnlyList<int> RawHistory(string item, int x, int y)
        {
            if (y - x <= 1)
                return TaskMergePlanDataSource.NoChangesets;

            var key = item.ToUpperInvariant() + "|" + x.ToString(CultureInfo.InvariantCulture) + "|" + y.ToString(CultureInfo.InvariantCulture);
            lock (_sync)
            {
                IReadOnlyList<int> cached;
                if (_rawHistory.TryGetValue(key, out cached))
                    return cached;

                Exception error;
                var ids = TaskMergePlanDataSource.ReadItemHistory(_vcs, item, y, x + 1, y - 1, out error);
                if (ids == null)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "Cannot read the history of {0} between C{1} and C{2}: {3}", item, x, y,
                        error == null ? "unknown error" : error.Message), error);

                cached = ids.Where(id => id > x && id < y).ToList().AsReadOnly();
                _rawHistory[key] = cached;
                return cached;
            }
        }

        // Per ogni item del piano cambiato da colleghi tra due changeset del task: quali di quei changeset
        // non risultano fusi nel target (storia dei merge verso l'item target). I changeset del task
        // esclusi dall'utente non contano qui: li segnala TaskMergeDependencyAnalyzer. Fuori dal thread UI.
        public IReadOnlyList<TaskMergeColleagueWarning> FindColleagueChangesetsNotInTarget(TaskMergePlan plan)
        {
            var result = new List<TaskMergeColleagueWarning>();
            if (plan == null || !plan.IsValid)
                return result.AsReadOnly();

            var partCount = plan.Parts.Count;
            foreach (var group in plan.Steps
                .Where(s => s.InterleavedThirdParty != null && s.InterleavedThirdParty.Count > 0)
                .GroupBy(s => s.SourceItem, StringComparer.OrdinalIgnoreCase))
            {
                var steps = group.OrderBy(s => s.Number).ToList();
                var first = steps[0];
                var colleagues = first.InterleavedThirdParty.Where(id => !_allTaskIds.Contains(id)).Distinct().OrderBy(id => id).ToList();
                if (colleagues.Count == 0)
                    continue;

                var merged = MergedIntoTarget(first.SourceItem, first.TargetItem);
                var unavailable = merged == null;
                var notInTarget = colleagues.Where(id => unavailable || !merged.Contains(id)).ToList();
                if (notInTarget.Count == 0)
                    continue;
                var inTarget = colleagues.Where(id => !notInTarget.Contains(id)).ToList();

                var firstMissing = notInTarget[0];
                var later = steps.Where(s => s.FromChangesetId > firstMissing).ToList();
                if (later.Count == 0)
                    continue;

                var laterText = string.Join(", ", later.Select(s => partCount > 1
                    ? string.Format(CultureInfo.InvariantCulture, "part {0} ({1})", s.Part, TaskMergeText.Range(s.FromChangesetId, s.ToChangesetId))
                    : string.Format(CultureInfo.InvariantCulture, "step {0} ({1})", s.Number, TaskMergeText.Range(s.FromChangesetId, s.ToChangesetId))));
                var message = string.Format(CultureInfo.InvariantCulture,
                    "{0}: the task changes in {1} come after colleagues' changesets not in the target ({2}){3}: the result may need them.",
                    first.RelativePath, laterText, TaskMergeText.Changesets(notInTarget),
                    unavailable ? ", the merge history of the target could not be read" : string.Empty);

                result.Add(new TaskMergeColleagueWarning(first.SourceItem, first.TargetItem, first.RelativePath,
                    notInTarget.AsReadOnly(), inTarget.AsReadOnly(), later.Select(s => s.Number).ToList().AsReadOnly(),
                    unavailable, message));
            }
            return result.AsReadOnly();
        }

        // Changeset sorgente gia' fusi verso l'item target (QueryMerges sul solo item). Null se la
        // storia dei merge non si puo' leggere (es. l'item non esiste nel target).
        private HashSet<int> MergedIntoTarget(string sourceItem, string targetItem)
        {
            lock (_sync)
            {
                HashSet<int> merged;
                if (_mergedIntoTarget.TryGetValue(sourceItem, out merged))
                    return merged;

                try
                {
                    var merges = _vcs.QueryMerges(sourceItem, VersionSpec.Latest, targetItem, VersionSpec.Latest, null, null, RecursionType.None);
                    merged = new HashSet<int>((merges ?? new ChangesetMerge[0]).Where(m => m != null).Select(m => m.SourceVersion));
                }
                catch (Exception)
                {
                    merged = null;
                }
                _mergedIntoTarget[sourceItem] = merged;
                return merged;
            }
        }
    }
}
