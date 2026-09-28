using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AutoMerge
{
    // Tipo di un cambio in un changeset del task (specchio "puro" di ChangeType di TFVC: il planner
    // non dipende dagli assembly di TFS). Supportati: Add, Edit, Delete, Encoding, Property e le loro
    // combinazioni; tutto il resto rende il piano non valido (vedi TaskMergePlanner.SupportedKinds).
    [Flags]
    public enum TaskChangeKind
    {
        None = 0,
        Add = 1,
        Edit = 2,
        Delete = 4,
        Encoding = 8,
        Property = 16,
        Rename = 32,
        SourceRename = 64,
        Undelete = 128,
        Branch = 256,
        Merge = 512,
        Rollback = 1024,
        Other = 2048
    }

    public enum TaskMergeItemKind
    {
        File,
        Folder
    }

    public enum TaskMergeStepRecursion
    {
        // solo l'item (file, cartella aggiunta o modificata)
        None,

        // l'item e tutto il suo contenuto (cartella cancellata)
        Full
    }

    // Un cambio di un changeset del task su un item del branch sorgente.
    public sealed class TaskChangeInfo
    {
        public TaskChangeInfo(int changesetId, string sourceServerItem, TaskMergeItemKind kind, TaskChangeKind changeKind)
        {
            ChangesetId = changesetId;
            SourceServerItem = sourceServerItem;
            Kind = kind;
            ChangeKind = changeKind;
        }

        public int ChangesetId { get; }

        public string SourceServerItem { get; }

        public TaskMergeItemKind Kind { get; }

        public TaskChangeKind ChangeKind { get; }
    }

    // Ingresso del planner: tutto cio' che serve arriva gia' letto da TFVC (o tramite i due callback),
    // cosi' il planner resta puro e testabile.
    public sealed class TaskMergePlanInput
    {
        // es. "$/ContosoMain"
        public string SourceBranch { get; set; }

        // radice del target, es. "$/Build/ContosoMain/rel1.0_new"
        public string TargetBranch { get; set; }

        public IReadOnlyList<int> TaskChangesetIds { get; set; }

        public IReadOnlyList<TaskChangeInfo> Changes { get; set; }

        // target server path -> esiste a Latest (non cancellato)
        public Func<string, bool> TargetItemExists { get; set; }

        // (sourceItem, a, b) -> changeset NON del task che toccano l'item con a < id < b
        public Func<string, int, int, IReadOnlyList<int>> ThirdPartyChangesetsBetween { get; set; }
    }

    // Un passo del piano: UN merge di UN item (file o cartella) su un intervallo [From..To].
    public sealed class TaskMergeStep
    {
        internal TaskMergeStep(
            int number,
            int part,
            string sourceItem,
            string targetItem,
            string relativePath,
            TaskMergeItemKind kind,
            TaskChangeKind changeKind,
            IReadOnlyList<int> taskChangesetIds,
            bool targetExists,
            TaskMergeStepRecursion recursion,
            IReadOnlyList<int> interleavedThirdParty)
        {
            Number = number;
            Part = part;
            SourceItem = sourceItem;
            TargetItem = targetItem;
            RelativePath = relativePath;
            Kind = kind;
            ChangeKind = changeKind;
            TaskChangesetIds = taskChangesetIds;
            FromChangesetId = taskChangesetIds[0];
            ToChangesetId = taskChangesetIds[taskChangesetIds.Count - 1];
            TargetExists = targetExists;
            Recursion = recursion;
            InterleavedThirdParty = interleavedThirdParty;
        }

        // 1-based, in ordine di esecuzione su tutto il piano
        public int Number { get; }

        // 1-based
        public int Part { get; }

        public string SourceItem { get; }

        public string TargetItem { get; }

        // path relativo alla radice del target ("." per la radice stessa)
        public string RelativePath { get; }

        public TaskMergeItemKind Kind { get; }

        // OR dei cambi del task nel passo
        public TaskChangeKind ChangeKind { get; }

        public int FromChangesetId { get; }

        public int ToChangesetId { get; }

        // changeset del task che toccano l'item dentro il passo (crescenti)
        public IReadOnlyList<int> TaskChangesetIds { get; }

        public bool TargetExists { get; }

        public TaskMergeStepRecursion Recursion { get; }

        // terzi sull'item tra i changeset del task dell'INTERO task (vuoto se nessuno)
        public IReadOnlyList<int> InterleavedThirdParty { get; }
    }

    // Una parte: finestra cronologica contigua di changeset del task, chiusa da un check-in.
    public sealed class TaskMergePart
    {
        internal TaskMergePart(int number, IReadOnlyList<int> taskChangesetIds, IReadOnlyList<TaskMergeStep> steps, string endReason)
        {
            Number = number;
            TaskChangesetIds = taskChangesetIds;
            FromChangesetId = taskChangesetIds[0];
            ToChangesetId = taskChangesetIds[taskChangesetIds.Count - 1];
            Steps = steps;
            EndReason = endReason;
        }

        public int Number { get; }

        public int FromChangesetId { get; }

        public int ToChangesetId { get; }

        public IReadOnlyList<int> TaskChangesetIds { get; }

        public IReadOnlyList<TaskMergeStep> Steps { get; }

        // perche' la parte finisce con un check-in (null per l'ultima)
        public string EndReason { get; }
    }

    public sealed class TaskMergePlan
    {
        internal TaskMergePlan(
            IReadOnlyList<TaskMergePart> parts,
            IReadOnlyList<string> errors,
            IReadOnlyList<string> warnings)
        {
            Parts = parts;
            Steps = parts.SelectMany(p => p.Steps).ToList().AsReadOnly();
            Errors = errors;
            Warnings = warnings;
        }

        public IReadOnlyList<TaskMergePart> Parts { get; }

        // tutti i passi, in ordine di esecuzione
        public IReadOnlyList<TaskMergeStep> Steps { get; }

        public IReadOnlyList<string> Errors { get; }

        public IReadOnlyList<string> Warnings { get; }

        // = Parts.Count - 1 (0 per un piano vuoto/non valido)
        public int CheckpointCount
        {
            get { return Parts.Count > 0 ? Parts.Count - 1 : 0; }
        }

        public bool IsValid
        {
            get { return Errors.Count == 0; }
        }
    }

    // Planner PER FILE del "Merge from Task" (puro: nessuna dipendenza da TFVC/VS).
    //
    // Fatti TFVC su cui si regge:
    // - per un item che ESISTE nel target, TFVC tiene UN solo intervallo di merge in sospeso: un
    //   nuovo merge sull'item e' accettato solo se il suo intervallo comprende quello gia' in
    //   sospeso, altrimenti TF203015 (ChangeAlreadyPendingException);
    // - un item NUOVO nel target (pending Branch+Merge) accetta merge successivi anche non contigui
    //   (ma il CONTENUTO di un tale merge non e' verificato: il piano non ci si appoggia);
    // - se una chiamata Merge ha anche un solo failure non mette in sospeso nulla;
    // - il merge di un item assente nel target e' un Branch dell'intero item (tutta la sua storia);
    //   le cartelle madri assenti le aggiunge TFVC come semplici Add, senza legame di merge.
    //
    // Algoritmo:
    // 1. per ogni item toccato dal task: i changeset del task che lo toccano (crescenti); per ogni
    //    coppia CONSECUTIVA (a,b) di un item multi-touch si leggono i changeset di terzi sull'item
    //    con a < id < b: se ce ne sono la coppia e' "interleaved";
    // 2. controlli sul target (errori di piano, Start disabilitato):
    //    - un item che NON esiste nel target e il cui primo cambio del task non e' un Add (e che il
    //      task non cancella): il merge lo porterebbe con un Branch dell'intero item alla versione
    //      del passo, compresi i changeset di altri precedenti al task;
    //    - una cartella madre di un item nuovo nel target che non esiste nel target e che nessun
    //      passo precedente crea: TFVC la aggiungerebbe come semplice Add, senza legame di merge;
    //    una cartella che esiste gia' nel target e che il task si limita ad aggiungere (Add e/o
    //    Encoding) non ha nulla da fondere: nessun passo, un avviso (il suo merge fallirebbe con
    //    NoMergeRelationshipException se il target l'ha creata senza legame di merge);
    // 3. vincoli di check-in: un vincolo (ia, ib] (indici nell'elenco ordinato dei changeset del
    //    task) chiede un check-in "prima di ib" in un punto k con ia < k <= ib. Nascono da:
    //    - OGNI coppia interleaved, anche di item nuovi nel target: che TFVC accetti un secondo merge
    //      non contiguo su un Branch in sospeso e' verificato, cosa ci metta dentro no; con il check-in
    //      in mezzo il secondo merge e' un merge normale su un item esistente (storia esatta);
    //    - discendenti di una cartella cancellata (merge ricorsivo Full su [d..d]) che esistono nel
    //      target (o vi sono stati consegnati da un check-in precedente: vincolo condizionato) e hanno
    //      un cambio del task prima di d nella stessa parte: il loro pending [a..d] non sarebbe
    //      compreso da [d..d];
    // 4. i punti di taglio: numero minimo con il greedy sull'estremo destro (stabbing), ognuno
    //    SUBITO PRIMA del changeset b (il taglio piu' tardivo possibile);
    // 5. parti = finestre cronologiche contigue tra i tagli; dentro una parte, per ogni item, un
    //    passo per ogni sequenza massimale di changeset del task senza terzi in mezzo (per il punto 3
    //    al massimo UN passo per item e per parte);
    // 6. ordine: cartelle non cancellate (profondita' crescente, None), file (path, poi
    //    intervallo), cartelle cancellate (profondita' decrescente, Full);
    // 7. verifica finale indipendente degli invarianti: se fallisce il piano non e' valido.
    public static class TaskMergePlanner
    {
        // cambi che il merge per file sa riprodurre
        private const TaskChangeKind SupportedKinds =
            TaskChangeKind.Add | TaskChangeKind.Edit | TaskChangeKind.Delete | TaskChangeKind.Encoding | TaskChangeKind.Property;

        // quante voci al massimo nel testo di EndReason (il resto e' "and N more")
        private const int MaxReasonsInText = 10;

        public static TaskMergePlan Build(TaskMergePlanInput input)
        {
            if (input == null)
                throw new ArgumentNullException("input");
            if (input.TargetItemExists == null)
                throw new ArgumentException("TargetItemExists is required.", "input");
            if (input.ThirdPartyChangesetsBetween == null)
                throw new ArgumentException("ThirdPartyChangesetsBetween is required.", "input");

            var errors = new List<string>();
            var warnings = new List<string>();

            // ---------------------------------------------------------------------------------
            // Branch
            // ---------------------------------------------------------------------------------
            var source = NormalizeBranch(input.SourceBranch);
            var target = NormalizeBranch(input.TargetBranch);
            if (source == null)
                errors.Add("The source branch is not set.");
            if (target == null)
                errors.Add("The target branch is not set.");
            if (source != null && target != null && string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                errors.Add(Format("The source and the target branch are the same ({0}).", source));
            if (source == null || target == null)
                return Invalid(errors, warnings);

            // ---------------------------------------------------------------------------------
            // Changeset del task: ordinati crescenti e deduplicati
            // ---------------------------------------------------------------------------------
            var taskIds = (input.TaskChangesetIds ?? new int[0]).Distinct().OrderBy(id => id).ToList();
            foreach (var id in taskIds.Where(id => id <= 0))
                errors.Add(Format("Invalid changeset id {0}.", id));
            if (taskIds.Count == 0)
                errors.Add("There are no changesets to merge.");

            var indexOf = new Dictionary<int, int>();
            for (var i = 0; i < taskIds.Count; i++)
                indexOf[taskIds[i]] = i;

            // ---------------------------------------------------------------------------------
            // Cambi -> item (chiave case-insensitive, come TFVC)
            // ---------------------------------------------------------------------------------
            var items = new Dictionary<string, ItemDraft>(StringComparer.OrdinalIgnoreCase);
            var changesetsWithEntries = new HashSet<int>();
            var changes = input.Changes ?? new TaskChangeInfo[0];
            foreach (var change in changes)
            {
                if (change == null)
                {
                    errors.Add("The list of changes contains an empty entry.");
                    continue;
                }

                changesetsWithEntries.Add(change.ChangesetId);
                var item = NormalizeItem(change.SourceServerItem);
                if (item == null)
                {
                    errors.Add(Format("C{0}: a change has no server path.", change.ChangesetId));
                    continue;
                }

                int index;
                if (!indexOf.TryGetValue(change.ChangesetId, out index))
                {
                    errors.Add(Format("C{0} {1}: the changeset is not one of the task changesets.", change.ChangesetId, item));
                    continue;
                }

                if (change.ChangeKind == TaskChangeKind.None)
                {
                    errors.Add(Format("C{0} {1}: the change type is unknown.", change.ChangesetId, item));
                    continue;
                }

                var unsupported = change.ChangeKind & ~SupportedKinds;
                if (unsupported != TaskChangeKind.None)
                {
                    errors.Add(Format("C{0} {1}: {2} is not supported.", change.ChangesetId, item, unsupported));
                    continue;
                }

                string relative;
                if (!TryGetRelativePath(source, item, out relative))
                {
                    errors.Add(Format("C{0} {1}: the item is not under the source branch {2}.", change.ChangesetId, item, source));
                    continue;
                }

                ItemDraft draft;
                if (!items.TryGetValue(item, out draft))
                {
                    draft = new ItemDraft(change.Kind);
                    items.Add(item, draft);
                }
                else if (draft.Kind != change.Kind)
                {
                    draft.KindConflict = true;
                }

                draft.AddTouch(index, change.ChangeKind, item, relative);
            }

            foreach (var id in taskIds.Where(id => !changesetsWithEntries.Contains(id)))
                warnings.Add(Format("C{0} has no changes to merge; it is skipped.", id));

            var orderedItems = items.Values
                .OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.RelativePath, StringComparer.Ordinal)
                .ToList();

            // controlli strutturali per item
            foreach (var draft in orderedItems)
                CheckItemStructure(draft, taskIds, errors);

            if (errors.Count == 0 && orderedItems.Count == 0)
                errors.Add("The task changesets contain no changes to merge.");
            if (errors.Count > 0)
                return Invalid(errors, warnings);

            // ---------------------------------------------------------------------------------
            // Target e storia (solo coppie consecutive degli item multi-touch)
            // ---------------------------------------------------------------------------------
            foreach (var draft in orderedItems)
            {
                draft.TargetItem = draft.RelativePath.Length == 0 ? target : target + "/" + draft.RelativePath;
                draft.TargetExists = input.TargetItemExists(draft.TargetItem);

                // Cartella gia' nel target che il task si limita ad aggiungere: niente da fondere (una
                // cartella non ha contenuto), nessun passo e nessuna storia da leggere.
                if (draft.Kind == TaskMergeItemKind.Folder
                    && !draft.IsDeleted
                    && draft.TargetExists
                    && (draft.AllKinds & ~(TaskChangeKind.Add | TaskChangeKind.Encoding)) == TaskChangeKind.None)
                {
                    draft.NothingToMerge = true;
                    warnings.Add(Format(
                        "Folder {0} already exists in the target and the task only adds it ({1}): nothing to merge, no step.",
                        draft.RelativePath, JoinIds(draft.Indices.Select(i => taskIds[i]))));
                    continue;
                }

                // Item assente nel target che il task non crea: il merge sarebbe un Branch dell'intero
                // item alla versione del passo, con dentro i changeset di altri precedenti al task.
                // Un item che il task cancella non porta contenuto nel target (al piu' non fa nulla).
                if (!draft.TargetExists && !draft.IsDeleted && (draft.KindAt(0) & TaskChangeKind.Add) == 0)
                {
                    errors.Add(Format(
                        "{0} does not exist in the target and the task does not add it (its first task change is {1} in C{2}): "
                        + "merging it would branch the whole {3} from the source, including changesets that are not in the task. "
                        + "Merge it into the target first, or select the changeset that adds it.",
                        draft.RelativePath.Length == 0 ? "The source branch root" : draft.RelativePath,
                        draft.KindAt(0), taskIds[draft.Indices[0]],
                        draft.Kind == TaskMergeItemKind.Folder ? "folder" : "file"));
                }

                var allThirds = new SortedSet<int>();
                for (var j = 1; j < draft.Indices.Count; j++)
                {
                    var a = taskIds[draft.Indices[j - 1]];
                    var b = taskIds[draft.Indices[j]];
                    var raw = input.ThirdPartyChangesetsBetween(draft.SourceItem, a, b);
                    if (raw == null)
                    {
                        errors.Add(Format("{0}: the history between C{1} and C{2} could not be read.", draft.SourceItem, a, b));
                        continue;
                    }

                    // difesa: solo changeset strettamente tra a e b e non del task
                    var thirds = raw.Where(id => id > a && id < b && !indexOf.ContainsKey(id))
                        .Distinct().OrderBy(id => id).ToList();
                    draft.PairThirds[j] = thirds;
                    foreach (var id in thirds)
                        allThirds.Add(id);
                }

                draft.AllThirds = allThirds.ToList().AsReadOnly();
            }

            CheckParentFolders(orderedItems, target, input.TargetItemExists, errors);

            if (errors.Count > 0)
                return Invalid(errors, warnings);

            var itemsToMerge = orderedItems.Where(d => !d.NothingToMerge).ToList();

            // ---------------------------------------------------------------------------------
            // Vincoli e punti di check-in
            // ---------------------------------------------------------------------------------
            var constraints = BuildConstraints(itemsToMerge, taskIds);
            var cuts = new List<int>();
            var cutReasons = new List<List<string>>();
            var byEnd = constraints.GroupBy(c => c.EndIndex).ToDictionary(g => g.Key, g => g.ToList());

            // lastCut = 0: nessun taglio (un taglio k vale "prima dell'indice k", quindi k >= 1)
            var lastCut = 0;
            for (var k = 1; k < taskIds.Count; k++)
            {
                List<Constraint> ending;
                if (!byEnd.TryGetValue(k, out ending))
                    continue;

                // non ancora "trafitto" (nessun taglio in (start, k)) e attivo: incondizionato (coppie
                // interleaved), oppure l'item esiste nel target, oppure e' stato consegnato da un
                // check-in dopo il suo primo cambio
                var triggered = ending
                    .Where(c => lastCut <= c.StartIndex
                        && (c.Unconditional || c.Item.TargetExists || lastCut > c.Item.Indices[0]))
                    .ToList();
                if (triggered.Count == 0)
                    continue;

                cuts.Add(k);
                cutReasons.Add(triggered.Select(c => c.Reason).Distinct().ToList());
                lastCut = k;
            }

            // ---------------------------------------------------------------------------------
            // Parti e passi
            // ---------------------------------------------------------------------------------
            var boundaries = new List<int> { 0 };
            boundaries.AddRange(cuts);
            boundaries.Add(taskIds.Count);

            var partCount = boundaries.Count - 1;
            var parts = new List<TaskMergePart>();
            var number = 0;
            for (var p = 0; p < partCount; p++)
            {
                var start = boundaries[p];
                var end = boundaries[p + 1];
                var drafts = new List<StepDraft>();
                foreach (var item in itemsToMerge)
                    AddStepsOfItem(item, start, end, drafts);

                var ordered = drafts.Where(s => s.Group == 0)
                        .OrderBy(s => s.Item.Depth)
                        .ThenBy(s => s.Item.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(s => s.Item.RelativePath, StringComparer.Ordinal)
                        .ThenBy(s => s.FromIndex)
                    .Concat(drafts.Where(s => s.Group == 1)
                        .OrderBy(s => s.Item.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(s => s.Item.RelativePath, StringComparer.Ordinal)
                        .ThenBy(s => s.FromIndex))
                    .Concat(drafts.Where(s => s.Group == 2)
                        .OrderByDescending(s => s.Item.Depth)
                        .ThenBy(s => s.Item.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(s => s.Item.RelativePath, StringComparer.Ordinal)
                        .ThenBy(s => s.FromIndex))
                    .ToList();

                var steps = new List<TaskMergeStep>();
                foreach (var s in ordered)
                {
                    number++;
                    var ids = s.Indices.Select(i => taskIds[i]).ToList().AsReadOnly();
                    steps.Add(new TaskMergeStep(
                        number,
                        p + 1,
                        s.Item.SourceItem,
                        s.Item.TargetItem,
                        s.Item.RelativePath.Length == 0 ? "." : s.Item.RelativePath,
                        s.Item.Kind,
                        s.ChangeKind,
                        ids,
                        s.Item.TargetExists,
                        s.Group == 2 ? TaskMergeStepRecursion.Full : TaskMergeStepRecursion.None,
                        s.Item.AllThirds));
                }

                string endReason = null;
                if (p < partCount - 1)
                    endReason = DescribeCut(taskIds[cuts[p]], cutReasons[p]);

                var window = taskIds.GetRange(start, end - start).AsReadOnly();
                parts.Add(new TaskMergePart(p + 1, window, steps.AsReadOnly(), endReason));
            }

            // ---------------------------------------------------------------------------------
            // Verifica indipendente degli invarianti (un piano sbagliato non deve partire)
            // ---------------------------------------------------------------------------------
            Verify(orderedItems, taskIds, parts, boundaries, errors);
            if (errors.Count > 0)
                return Invalid(errors, warnings);

            return new TaskMergePlan(parts.AsReadOnly(), errors.AsReadOnly(), warnings.AsReadOnly());
        }

        // ---------------------------------------------------------------------------------------
        // Controlli strutturali
        // ---------------------------------------------------------------------------------------

        private static void CheckItemStructure(ItemDraft draft, List<int> taskIds, List<string> errors)
        {
            if (draft.KindConflict)
            {
                errors.Add(Format("{0}: the path is both a file and a folder in the task changesets.", draft.SourceItem));
                return;
            }

            if (draft.RelativePath.Length == 0 && (draft.AllKinds & (TaskChangeKind.Add | TaskChangeKind.Delete)) != 0)
            {
                errors.Add(Format("{0}: adding or deleting the source branch root is not supported.", draft.SourceItem));
                return;
            }

            // un Delete deve essere l'ultimo cambio dell'item: un cambio dopo una cancellazione e'
            // un altro item con lo stesso path (ricreato), che il merge per path non sa riprodurre
            for (var j = 0; j < draft.Indices.Count - 1; j++)
            {
                if ((draft.KindAt(j) & TaskChangeKind.Delete) != 0)
                {
                    errors.Add(Format(
                        "{0}: deleted in C{1} and changed again in C{2}; this is not supported.",
                        draft.SourceItem, taskIds[draft.Indices[j]], taskIds[draft.Indices[j + 1]]));
                    return;
                }
            }

            // una cartella cancellata si fonde con ricorsione Full sul solo changeset della
            // cancellazione: altri cambi del task sulla stessa cartella non sono supportati
            if (draft.Kind == TaskMergeItemKind.Folder && draft.IsDeleted && draft.Indices.Count > 1)
            {
                errors.Add(Format(
                    "{0}: the folder is deleted in C{1} and also changed in {2}; this is not supported.",
                    draft.SourceItem,
                    taskIds[draft.Indices[draft.Indices.Count - 1]],
                    string.Join(", ", draft.Indices.Take(draft.Indices.Count - 1).Select(i => "C" + taskIds[i]))));
            }
        }

        // Cartelle madri degli item nuovi nel target: ognuna deve esistere nel target, oppure essere
        // una cartella del piano (non cancellata) che un passo crea prima dell'item (primo cambio non
        // successivo al suo; le sue madri le controlla il giro di quella cartella). Altrimenti TFVC,
        // fondendo l'item, la aggiungerebbe come semplice Add senza legame di merge: il controllo
        // finale bloccherebbe il check-in solo a merge fatti, e un merge successivo di quella
        // cartella fallirebbe (NoMergeRelationshipException). La radice del target non si controlla.
        private static void CheckParentFolders(List<ItemDraft> items, string target, Func<string, bool> targetItemExists, List<string> errors)
        {
            var folders = items.Where(i => i.Kind == TaskMergeItemKind.Folder)
                .ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);
            var existsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var missing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var missingOrder = new List<string>();

            foreach (var item in items)
            {
                if (item.TargetExists || item.IsDeleted || item.RelativePath.Length == 0)
                    continue;

                var parent = ParentOf(item.RelativePath);
                while (parent.Length > 0)
                {
                    ItemDraft folder;
                    if (folders.TryGetValue(parent, out folder))
                    {
                        if (folder.TargetExists)
                            break;
                        if (!folder.IsDeleted && folder.Indices[0] <= item.Indices[0])
                            break;
                    }
                    else
                    {
                        bool exists;
                        if (!existsCache.TryGetValue(parent, out exists))
                        {
                            exists = targetItemExists(target + "/" + parent);
                            existsCache[parent] = exists;
                        }
                        if (exists)
                            break;
                    }

                    List<string> children;
                    if (!missing.TryGetValue(parent, out children))
                    {
                        children = new List<string>();
                        missing.Add(parent, children);
                        missingOrder.Add(parent);
                    }
                    children.Add(item.RelativePath);
                    break;
                }
            }

            foreach (var folder in missingOrder)
            {
                var children = missing[folder];
                errors.Add(Format(
                    "Folder {0} does not exist in the target and no step of the plan creates it: merging {1} would make TFVC add the folder "
                    + "without a merge link. Merge the folder into the target first, or select the changeset that adds it.",
                    folder,
                    children.Count == 1 ? children[0] : Format("{0} and {1} more item(s)", children[0], children.Count - 1)));
            }
        }

        private static string ParentOf(string relativePath)
        {
            var slash = relativePath.LastIndexOf('/');
            return slash < 0 ? string.Empty : relativePath.Substring(0, slash);
        }

        // ---------------------------------------------------------------------------------------
        // Vincoli di check-in
        // ---------------------------------------------------------------------------------------

        private static List<Constraint> BuildConstraints(List<ItemDraft> orderedItems, List<int> taskIds)
        {
            var result = new List<Constraint>();

            // coppie interleaved
            foreach (var item in orderedItems)
            {
                for (var j = 1; j < item.Indices.Count; j++)
                {
                    List<int> thirds;
                    if (!item.PairThirds.TryGetValue(j, out thirds) || thirds.Count == 0)
                        continue;

                    var a = taskIds[item.Indices[j - 1]];
                    var b = taskIds[item.Indices[j]];
                    result.Add(new Constraint(
                        item,
                        item.Indices[j - 1],
                        item.Indices[j],
                        Format("{0} is changed by other changesets between C{1} and C{2} ({3})",
                            item.RelativePath, a, b, JoinIds(thirds)),
                        true));
                }
            }

            // discendenti di cartelle cancellate con cambi del task precedenti alla cancellazione
            foreach (var folder in orderedItems.Where(i => i.Kind == TaskMergeItemKind.Folder && i.IsDeleted))
            {
                var deleteIndex = folder.Indices[folder.Indices.Count - 1];
                var prefix = folder.RelativePath + "/";
                foreach (var item in orderedItems)
                {
                    if (ReferenceEquals(item, folder))
                        continue;
                    var under = folder.RelativePath.Length == 0
                        || item.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    if (!under)
                        continue;

                    var before = item.Indices.Where(i => i < deleteIndex).ToList();
                    if (before.Count == 0)
                        continue;

                    var last = before[before.Count - 1];
                    result.Add(new Constraint(
                        item,
                        last,
                        deleteIndex,
                        Format("{0} (changed in C{1}) is inside folder {2}, deleted in C{3}",
                            item.RelativePath, taskIds[last], folder.RelativePath, taskIds[deleteIndex]),
                        false));
                }
            }

            return result;
        }

        private static string DescribeCut(int cutChangesetId, List<string> reasons)
        {
            var sb = new StringBuilder();
            sb.Append(Format("Check-in required before C{0}: ", cutChangesetId));
            sb.Append(string.Join("; ", reasons.Take(MaxReasonsInText)));
            if (reasons.Count > MaxReasonsInText)
                sb.Append(Format("; and {0} more", reasons.Count - MaxReasonsInText));
            sb.Append('.');
            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------------
        // Passi
        // ---------------------------------------------------------------------------------------

        // Passi di un item nella finestra [start, end): una sequenza massimale di changeset del task
        // senza terzi in mezzo diventa un passo [primo..ultimo].
        private static void AddStepsOfItem(ItemDraft item, int start, int end, List<StepDraft> drafts)
        {
            StepDraft current = null;
            for (var j = 0; j < item.Indices.Count; j++)
            {
                var index = item.Indices[j];
                if (index < start || index >= end)
                    continue;

                List<int> thirds;
                var interleavedWithPrevious = j > 0 && item.PairThirds.TryGetValue(j, out thirds) && thirds.Count > 0;
                if (current == null || interleavedWithPrevious)
                {
                    current = new StepDraft(item, GroupOf(item));
                    drafts.Add(current);
                }

                current.Indices.Add(index);
                current.ChangeKind |= item.KindAt(j);
            }
        }

        // 0 = cartella non cancellata, 1 = file, 2 = cartella cancellata
        private static int GroupOf(ItemDraft item)
        {
            if (item.Kind == TaskMergeItemKind.File)
                return 1;
            return item.IsDeleted ? 2 : 0;
        }

        // ---------------------------------------------------------------------------------------
        // Verifica degli invarianti sul piano costruito
        // ---------------------------------------------------------------------------------------

        private static void Verify(List<ItemDraft> items, List<int> taskIds, List<TaskMergePart> parts, List<int> boundaries, List<string> errors)
        {
            var problems = new List<string>();
            var stepsByItem = parts.SelectMany(p => p.Steps)
                .GroupBy(s => s.SourceItem, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // numerazione continua e parti coerenti
            var expected = 1;
            foreach (var part in parts)
            {
                foreach (var step in part.Steps)
                {
                    if (step.Number != expected || step.Part != part.Number)
                        problems.Add(Format("step numbering ({0})", step.RelativePath));
                    expected++;
                    if (step.FromChangesetId < part.FromChangesetId || step.ToChangesetId > part.ToChangesetId)
                        problems.Add(Format("step {0} outside part {1}", step.Number, part.Number));
                }
            }

            foreach (var item in items)
            {
                List<TaskMergeStep> steps;
                if (item.NothingToMerge)
                {
                    // cartella gia' nel target, solo aggiunta dal task: nessun passo per costruzione
                    if (stepsByItem.ContainsKey(item.SourceItem) || !item.TargetExists || item.Kind != TaskMergeItemKind.Folder)
                        problems.Add(Format("{0}: a folder with nothing to merge has steps", item.RelativePath));
                    continue;
                }

                if (!stepsByItem.TryGetValue(item.SourceItem, out steps))
                {
                    problems.Add(Format("{0} has no steps", item.RelativePath));
                    continue;
                }

                // ogni changeset del task sull'item e' in esattamente un passo
                var covered = steps.SelectMany(s => s.TaskChangesetIds).OrderBy(id => id).ToList();
                var touched = item.Indices.Select(i => taskIds[i]).ToList();
                if (!covered.SequenceEqual(touched))
                    problems.Add(Format("{0}: changesets not covered exactly once", item.RelativePath));

                // nessun passo contiene terzi tra due suoi changeset consecutivi, e tra i due c'e'
                // sempre un check-in (parti diverse), anche per un item nuovo nel target
                for (var j = 1; j < item.Indices.Count; j++)
                {
                    List<int> thirds;
                    if (!item.PairThirds.TryGetValue(j, out thirds) || thirds.Count == 0)
                        continue;
                    var a = taskIds[item.Indices[j - 1]];
                    var b = taskIds[item.Indices[j]];
                    if (steps.Any(s => s.TaskChangesetIds.Contains(a) && s.TaskChangesetIds.Contains(b)))
                        problems.Add(Format("{0}: a step spans other changesets", item.RelativePath));
                    var stepA = steps.FirstOrDefault(s => s.TaskChangesetIds.Contains(a));
                    var stepB = steps.FirstOrDefault(s => s.TaskChangesetIds.Contains(b));
                    if (stepA == null || stepB == null || stepA.Part >= stepB.Part)
                        problems.Add(Format("{0}: no check-in between C{1} and C{2}", item.RelativePath, a, b));
                }

                // ogni item ha al massimo un passo per parte (TFVC tiene un solo intervallo in sospeso)
                foreach (var part in parts)
                {
                    if (steps.Count(s => s.Part == part.Number) > 1)
                        problems.Add(Format("{0}: more than one step in part {1}", item.RelativePath, part.Number));
                }
            }

            // cartelle cancellate: nessun discendente esistente con un pending che [d..d] non comprende
            foreach (var part in parts)
            {
                foreach (var folderStep in part.Steps.Where(s => s.Recursion == TaskMergeStepRecursion.Full))
                {
                    var prefix = folderStep.RelativePath == "." ? string.Empty : folderStep.RelativePath + "/";
                    foreach (var step in part.Steps)
                    {
                        if (ReferenceEquals(step, folderStep))
                            continue;
                        if (!step.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var item = items.First(i => string.Equals(i.SourceItem, step.SourceItem, StringComparison.OrdinalIgnoreCase));
                        var existing = item.TargetExists || item.Indices[0] < boundaries[part.Number - 1];
                        if (existing && step.FromChangesetId < folderStep.FromChangesetId)
                            problems.Add(Format("{0}: pending change incompatible with the deletion of {1}", step.RelativePath, folderStep.RelativePath));
                    }
                }
            }

            foreach (var problem in problems.Distinct())
                errors.Add("Internal planning error: " + problem + ".");
        }

        // ---------------------------------------------------------------------------------------
        // Path
        // ---------------------------------------------------------------------------------------

        private static string NormalizeBranch(string branch)
        {
            if (branch == null)
                return null;
            var trimmed = branch.Trim().TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string NormalizeItem(string item)
        {
            if (string.IsNullOrWhiteSpace(item))
                return null;
            var trimmed = item.TrimEnd('/');
            return trimmed.Length == 0 ? null : trimmed;
        }

        // relative = parte dopo la radice del branch, senza '/' iniziale ("" per la radice)
        private static bool TryGetRelativePath(string branch, string item, out string relative)
        {
            relative = null;
            if (string.Equals(item, branch, StringComparison.OrdinalIgnoreCase))
            {
                relative = string.Empty;
                return true;
            }

            if (item.Length > branch.Length + 1
                && item.StartsWith(branch, StringComparison.OrdinalIgnoreCase)
                && item[branch.Length] == '/')
            {
                relative = item.Substring(branch.Length + 1);
                return true;
            }

            return false;
        }

        // ---------------------------------------------------------------------------------------
        // Varie
        // ---------------------------------------------------------------------------------------

        private static TaskMergePlan Invalid(List<string> errors, List<string> warnings)
        {
            return new TaskMergePlan(new List<TaskMergePart>().AsReadOnly(), errors.AsReadOnly(), warnings.AsReadOnly());
        }

        private static string JoinIds(IEnumerable<int> ids)
        {
            return string.Join(", ", ids.Select(id => "C" + id.ToString(CultureInfo.InvariantCulture)));
        }

        private static string Format(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }

        // Stato di lavoro di un item durante la costruzione del piano.
        private sealed class ItemDraft
        {
            private readonly SortedDictionary<int, TaskChangeKind> _touches = new SortedDictionary<int, TaskChangeKind>();
            private int _pathIndex = -1;

            public ItemDraft(TaskMergeItemKind kind)
            {
                Kind = kind;
                PairThirds = new Dictionary<int, List<int>>();
                AllThirds = new List<int>().AsReadOnly();
            }

            public TaskMergeItemKind Kind { get; }

            public bool KindConflict { get; set; }

            // casing del changeset piu' recente che tocca l'item
            public string SourceItem { get; private set; }

            public string RelativePath { get; private set; }

            public string TargetItem { get; set; }

            public bool TargetExists { get; set; }

            // cartella gia' nel target che il task si limita ad aggiungere: nessun passo
            public bool NothingToMerge { get; set; }

            // indici (nell'elenco ordinato dei changeset del task) dei changeset che toccano l'item
            public List<int> Indices { get; private set; }

            // j -> terzi tra Indices[j-1] e Indices[j]
            public Dictionary<int, List<int>> PairThirds { get; }

            public IReadOnlyList<int> AllThirds { get; set; }

            public TaskChangeKind AllKinds
            {
                get
                {
                    var all = TaskChangeKind.None;
                    foreach (var kind in _touches.Values)
                        all |= kind;
                    return all;
                }
            }

            public bool IsDeleted
            {
                get { return (AllKinds & TaskChangeKind.Delete) != 0; }
            }

            public int Depth
            {
                get { return RelativePath.Length == 0 ? 0 : RelativePath.Count(c => c == '/') + 1; }
            }

            public TaskChangeKind KindAt(int j)
            {
                return _touches[Indices[j]];
            }

            public void AddTouch(int index, TaskChangeKind kind, string item, string relative)
            {
                TaskChangeKind existing;
                _touches[index] = _touches.TryGetValue(index, out existing) ? existing | kind : kind;
                Indices = _touches.Keys.ToList();

                if (index > _pathIndex)
                {
                    _pathIndex = index;
                    SourceItem = item;
                    RelativePath = relative;
                }
            }
        }

        private sealed class StepDraft
        {
            public StepDraft(ItemDraft item, int group)
            {
                Item = item;
                Group = group;
                Indices = new List<int>();
            }

            public ItemDraft Item { get; }

            public int Group { get; }

            public List<int> Indices { get; }

            public int FromIndex
            {
                get { return Indices[0]; }
            }

            public TaskChangeKind ChangeKind { get; set; }
        }

        // Vincolo di check-in: serve un taglio k con StartIndex < k <= EndIndex. Unconditional: vale
        // sempre (coppie interleaved); altrimenti solo se l'item esiste nel target al momento.
        private sealed class Constraint
        {
            public Constraint(ItemDraft item, int startIndex, int endIndex, string reason, bool unconditional)
            {
                Item = item;
                StartIndex = startIndex;
                EndIndex = endIndex;
                Reason = reason;
                Unconditional = unconditional;
            }

            public bool Unconditional { get; }

            public ItemDraft Item { get; }

            public int StartIndex { get; }

            public int EndIndex { get; }

            public string Reason { get; }
        }
    }
}
