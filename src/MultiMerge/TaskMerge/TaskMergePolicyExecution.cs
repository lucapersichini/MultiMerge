// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
    // =============================================================================================
    // Esecuzione delle regole di merge (MergePolicy) nella catena "Merge from Task": la parte che
    // tocca TFVC e il disco. Le decisioni (quale azione, quali righe protette) le prende il motore puro
    // MergePolicyEngine; qui si leggono i file, si scarica cio' che serve, si scrive il risultato e si
    // verifica. Nessuna UI: tutto si chiama fuori dal thread UI.
    // =============================================================================================

    // Impronte del contenuto dei file locali (per verificare che nulla cambi tra un controllo e l'uso).
    internal static class TaskMergeFileContent
    {
        // Impronte "speciali": item assente, cartella.
        public const string Missing = "<missing>";
        public const string Folder = "<folder>";

        private const string TempRootFolderName = "MultiMergeTask";

        // Impronta di cio' che c'e' adesso in localPath: SHA-256 del file, Folder o Missing.
        public static string Fingerprint(string localPath)
        {
            if (string.IsNullOrEmpty(localPath))
                return Missing;
            if (File.Exists(localPath))
                return Fingerprint(File.ReadAllBytes(localPath));
            if (Directory.Exists(localPath))
                return Folder;
            return Missing;
        }

        public static string Fingerprint(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? new byte[0]);
                var text = new StringBuilder("sha256:", 7 + hash.Length * 2);
                foreach (var b in hash)
                    text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        // Differenze tra le impronte registrate e il disco adesso (vuoto = identico).
        public static IReadOnlyList<string> CompareWithDisk(IReadOnlyDictionary<string, string> fingerprints)
        {
            var differences = new List<string>();
            if (fingerprints == null)
                return differences.AsReadOnly();

            foreach (var pair in fingerprints.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                string now;
                try
                {
                    now = Fingerprint(pair.Key);
                }
                catch (Exception ex)
                {
                    differences.Add(pair.Key + ": the file could not be read again (" + ex.Message + ").");
                    continue;
                }
                if (!string.Equals(now, pair.Value, StringComparison.Ordinal))
                    differences.Add(string.Format(CultureInfo.InvariantCulture, "{0}: the content changed after the final check (was {1}, now {2}).",
                        pair.Key, Describe(pair.Value), Describe(now)));
            }
            return differences.AsReadOnly();
        }

        // "missing", "folder", "file 3fa2c1d09e4b..."
        public static string Describe(string fingerprint)
        {
            if (fingerprint == null || fingerprint == Missing)
                return "missing";
            if (fingerprint == Folder)
                return "a folder";
            var hex = fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ? fingerprint.Substring(7) : fingerprint;
            return "file " + (hex.Length > 12 ? hex.Substring(0, 12) : hex);
        }

        // Cartella temporanea nuova sotto %TEMP%\MultiMergeTask (quelle vecchie di sessioni chiuse male le
        // pulisce il resolver dei conflitti).
        public static string NewTempFolder(string purpose)
        {
            return Path.Combine(Path.GetTempPath(), TempRootFolderName, purpose + "-" + Guid.NewGuid().ToString("N"));
        }

        public static void DeleteFolder(string folder)
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

        // Estensione "sicura" per i file temporanei (come il resolver): solo lettere e cifre.
        public static string TempExtension(string serverItem)
        {
            var name = serverItem ?? string.Empty;
            var slash = name.LastIndexOf('/');
            if (slash >= 0)
                name = name.Substring(slash + 1);
            var dot = name.LastIndexOf('.');
            var extension = dot > 0 && dot < name.Length - 1 ? name.Substring(dot + 1) : string.Empty;
            if (extension.Length == 0 || extension.Length > 16 || !extension.All(char.IsLetterOrDigit))
                return ".txt";
            return "." + extension;
        }
    }

    // Fotografia del contenuto locale di un item del target (file, cartella, o cartella con tutti gli
    // item sotto controllo di versione per i passi con ricorsione Full): serve a verificare che un
    // Discard (tf merge /discard) non abbia cambiato nulla, subito dopo il merge e di nuovo nel
    // controllo finale. I file non sotto controllo di versione (es. bin/obj) non contano: un merge non
    // li tocca, e una build nel frattempo non deve sembrare un Discard che ha cambiato il contenuto.
    internal sealed class TaskMergeContentSnapshot
    {
        private readonly IReadOnlyList<string> _paths;

        private TaskMergeContentSnapshot(string serverItem, string localPath, bool recursive, IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, string> entries, string error)
        {
            ServerItem = serverItem;
            LocalPath = localPath;
            Recursive = recursive;
            _paths = paths ?? new List<string>().AsReadOnly();
            Entries = entries ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Error = error;
        }

        public string ServerItem { get; private set; }

        public string LocalPath { get; private set; }

        public bool Recursive { get; private set; }

        // path locale -> impronta (TaskMergeFileContent.Fingerprint)
        public IReadOnlyDictionary<string, string> Entries { get; private set; }

        // Non null: la fotografia non e' stata possibile (item non mappato, errore di lettura).
        public string Error { get; private set; }

        public static TaskMergeContentSnapshot Capture(Workspace workspace, string serverItem, bool recursive)
        {
            var paths = new List<string>();
            string local;
            try
            {
                local = workspace.TryGetLocalItemForServerItem(serverItem);
                if (string.IsNullOrEmpty(local))
                    return new TaskMergeContentSnapshot(serverItem, null, recursive, null, null, "the item is not mapped in the workspace");
                paths.Add(local);

                // Cartella con ricorsione Full: tutti gli item sotto controllo di versione che il
                // workspace ha su disco.
                if (recursive && Directory.Exists(local))
                {
                    var items = workspace.GetExtendedItems(new[] { new ItemSpec(serverItem, RecursionType.Full) },
                        DeletedState.NonDeleted, ItemType.Any);
                    if (items != null && items.Length > 0 && items[0] != null)
                    {
                        foreach (var item in items[0])
                        {
                            if (item != null && !string.IsNullOrEmpty(item.LocalItem))
                                paths.Add(item.LocalItem);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new TaskMergeContentSnapshot(serverItem, null, recursive, null, null, "the items of the target cannot be read: " + ex.Message);
            }

            return CapturePaths(serverItem, local, recursive,
                paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly());
        }

        // Nuova fotografia degli stessi path.
        public TaskMergeContentSnapshot Recapture()
        {
            if (string.IsNullOrEmpty(LocalPath))
                return new TaskMergeContentSnapshot(ServerItem, null, Recursive, null, null, Error ?? "the item is not mapped in the workspace");
            return CapturePaths(ServerItem, LocalPath, Recursive, _paths);
        }

        private static TaskMergeContentSnapshot CapturePaths(string serverItem, string local, bool recursive, IReadOnlyList<string> paths)
        {
            try
            {
                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                    entries[path] = TaskMergeFileContent.Fingerprint(path);
                return new TaskMergeContentSnapshot(serverItem, local, recursive, paths, entries, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new TaskMergeContentSnapshot(serverItem, local, recursive, paths, null, "the local content cannot be read: " + ex.Message);
            }
        }

        // Differenze tra questa fotografia (prima) e now (dopo). Vuoto = identiche.
        public IReadOnlyList<string> Differences(TaskMergeContentSnapshot now)
        {
            var differences = new List<string>();
            if (Error != null)
            {
                differences.Add("The content before the merge is not known (" + Error + ").");
                return differences.AsReadOnly();
            }
            if (now == null || now.Error != null)
            {
                differences.Add("The content after the merge cannot be read (" + (now == null ? "unknown error" : now.Error) + ").");
                return differences.AsReadOnly();
            }

            var paths = Entries.Keys.Union(now.Entries.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                string before;
                string after;
                Entries.TryGetValue(path, out before);
                now.Entries.TryGetValue(path, out after);
                before = before ?? TaskMergeFileContent.Missing;
                after = after ?? TaskMergeFileContent.Missing;
                if (!string.Equals(before, after, StringComparison.Ordinal))
                    differences.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1} before the merge, {2} now.",
                        path, TaskMergeFileContent.Describe(before), TaskMergeFileContent.Describe(after)));
            }
            return differences.AsReadOnly();
        }
    }

    // Come fondere un passo Merge di un file con righe protette (vedi TaskMergeProtectedLines.Prepare).
    internal enum ProtectedMergeMode
    {
        // Merge normale di TFVC: il task non tocca righe protette (o il file non ne ha).
        Normal,

        // File nuovo nel target: si porta intero; dopo il merge si guarda se contiene righe protette
        // (promemoria "new file contains protected lines").
        NewFile,

        // Il task cambia righe protette: il risultato e' quello calcolato qui (sorgente neutralizzata).
        OwnResult,

        // Non si puo' fondere in sicurezza tenendo le righe protette: stop PRIMA del merge (nulla in
        // sospeso), con il motivo in Message.
        Stop
    }

    internal sealed class ProtectedMergePreparation
    {
        public ProtectedMergeMode Mode { get; set; }

        // Stop: il motivo; negli altri casi una nota per il registro.
        public string Message { get; set; }

        public string TargetLocalPath { get; set; }

        // OwnResult: la fusione con le righe protette.
        public ProtectedMergeResult Result { get; set; }

        // OwnResult senza conflitti: i byte da scrivere (encoding, BOM e a-capo del target).
        public byte[] ResultBytes { get; set; }

        public static ProtectedMergePreparation Stop(string reason)
        {
            return new ProtectedMergePreparation { Mode = ProtectedMergeMode.Stop, Message = reason };
        }
    }

    // Esito di una risoluzione del conflitto con il nostro risultato.
    internal sealed class ProtectedResolveOutcome
    {
        // TFVC ha segnato il conflitto come risolto.
        public bool Resolved { get; set; }

        // Risolto e il file del workspace e' esattamente il risultato calcolato.
        public bool Verified { get; set; }

        public string Message { get; set; }
    }

    internal static class TaskMergeProtectedLines
    {
        // Oltre questa dimensione un file con righe protette non si fonde qui (lettura e diff in memoria).
        private const long MaxFileBytes = 16L * 1024 * 1024;

        // Code page "sconosciuto" (nessun ripiego): il BOM, se c'e', decide comunque.
        private const int UnknownCodePage = 0;

        // Prima del merge di un passo Merge (file) con regole di righe protette. tfvcExpectsConflicts:
        // l'anteprima di TFVC ha dato conflitti su questo passo. Thread di background; nessuna modifica al
        // workspace. Un errore imprevisto diventa Stop (mai un merge "alla cieca").
        public static ProtectedMergePreparation Prepare(Workspace workspace, TaskMergeStep step,
            IReadOnlyList<MergeLineRule> rules, bool tfvcExpectsConflicts, string tempFolder)
        {
            try
            {
                return PrepareCore(workspace, step, rules, tfvcExpectsConflicts, tempFolder);
            }
            catch (Exception ex)
            {
                return ProtectedMergePreparation.Stop("the versions needed to keep its protected lines could not be read (" + ex.Message + ")");
            }
        }

        private static ProtectedMergePreparation PrepareCore(Workspace workspace, TaskMergeStep step,
            IReadOnlyList<MergeLineRule> rules, bool tfvcExpectsConflicts, string tempFolder)
        {
            var local = workspace.TryGetLocalItemForServerItem(step.TargetItem);
            if (string.IsNullOrEmpty(local))
                return ProtectedMergePreparation.Stop("the target file is not mapped in the workspace");

            // "Nuovo nel target" si decide adesso sul server (a Latest), come il controllo finale (R6), e non
            // solo dal piano: TargetExists dice se l'item c'era al Load, ma un item aggiunto dal task in una
            // parte gia' archiviata esiste quando arriva un suo passo di una parte successiva, e quel passo
            // deve tenere le righe protette come per ogni file esistente.
            var vcs = workspace.VersionControlServer;
            if (!step.TargetExists && !vcs.ServerItemExists(step.TargetItem, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.File))
                return new ProtectedMergePreparation
                {
                    Mode = ProtectedMergeMode.NewFile,
                    TargetLocalPath = local,
                    Message = "new file in the target: it is merged whole, then checked for protected lines"
                };

            // Target: il file locale, cioe' la versione su cui TFVC fonde.
            if (!File.Exists(local))
                return ProtectedMergePreparation.Stop("the target file is not in the workspace (" + local + "): get the latest version of the target first");
            if (new FileInfo(local).Length > MaxFileBytes)
                return ProtectedMergePreparation.Stop("the file is too large to keep its protected lines");

            var targetCodePage = vcs.GetItem(step.TargetItem, VersionSpec.Latest).Encoding;
            var deleted = (step.ChangeKind & TaskChangeKind.Delete) != 0;

            Item sourceItem = null;
            if (!deleted)
            {
                sourceItem = GetFileItem(vcs, step.SourceItem, step.ToChangesetId);
                if (sourceItem == null)
                    return ProtectedMergePreparation.Stop(string.Format(CultureInfo.InvariantCulture,
                        "the source version (C{0}) is not a file that can be read", step.ToChangesetId));
            }

            var target = ConflictResolverViewModel.ReadText(local, "target", targetCodePage,
                sourceItem == null ? UnknownCodePage : sourceItem.Encoding);
            if (target.Error != null)
                return ProtectedMergePreparation.Stop("its protected lines cannot be checked: " + TrimPeriod(target.Error));

            if (deleted)
            {
                var lost = MergePolicyEngine.CompareProtectedLines(target.Text, string.Empty, rules);
                if (lost == null || lost.Count > 0)
                    return ProtectedMergePreparation.Stop("the task deletes this file, but the target version has protected lines that must be kept"
                        + (lost != null && lost.Count > 0 ? " (" + TrimPeriod(lost[0]) + ")" : string.Empty));
                return new ProtectedMergePreparation
                {
                    Mode = ProtectedMergeMode.Normal,
                    TargetLocalPath = local,
                    Message = "the task deletes the file; the target version has no protected lines"
                };
            }

            // Sorgente alla versione "to" e base del merge [from..to] = sorgente alla versione from-1.
            if (sourceItem.ContentLength > MaxFileBytes)
                return ProtectedMergePreparation.Stop("the file is too large to keep its protected lines");
            var baseItem = GetFileItem(vcs, step.SourceItem, step.FromChangesetId - 1);
            if (baseItem == null)
                return ProtectedMergePreparation.Stop(string.Format(CultureInfo.InvariantCulture,
                    "the base version of the source (C{0}, before the first task changeset) cannot be read", step.FromChangesetId - 1));
            if (baseItem.ContentLength > MaxFileBytes)
                return ProtectedMergePreparation.Stop("the file is too large to keep its protected lines");

            Directory.CreateDirectory(tempFolder);
            var extension = TaskMergeFileContent.TempExtension(step.TargetItem);
            var sourceFile = Path.Combine(tempFolder, "source-" + step.Number.ToString(CultureInfo.InvariantCulture) + extension);
            var baseFile = Path.Combine(tempFolder, "base-" + step.Number.ToString(CultureInfo.InvariantCulture) + extension);
            sourceItem.DownloadFile(sourceFile);
            baseItem.DownloadFile(baseFile);
            if (!File.Exists(sourceFile) || !File.Exists(baseFile))
                return ProtectedMergePreparation.Stop("the source versions could not be downloaded");

            var source = ConflictResolverViewModel.ReadText(sourceFile, "source", sourceItem.Encoding, targetCodePage);
            if (source.Error != null)
                return ProtectedMergePreparation.Stop("its protected lines cannot be checked: " + TrimPeriod(source.Error));
            var baseText = ConflictResolverViewModel.ReadText(baseFile, "base", baseItem.Encoding, sourceItem.Encoding, targetCodePage);
            if (baseText.Error != null)
                return ProtectedMergePreparation.Stop("its protected lines cannot be checked: " + TrimPeriod(baseText.Error));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText.Text, source.Text, target.Text, rules);
            if (result == null || result.Merge == null)
                return ProtectedMergePreparation.Stop("the merge that keeps its protected lines gave no result");

            var touchesProtected = !string.Equals(result.NeutralizedSource, source.Text, StringComparison.Ordinal)
                || (result.KeptTargetDifferences != null && result.KeptTargetDifferences.Count > 0);
            var encodingChange = (step.ChangeKind & TaskChangeKind.Encoding) != 0;
            if (!touchesProtected)
            {
                // Il task non tocca righe protette: la fusione di TFVC non puo' cambiarle (prende righe
                // solo da sorgente e target, e la sorgente le lascia come nella base). Merge normale (il
                // controllo finale lo verifica comunque), salvo un conflitto che TFVC vedra' e che il merge
                // con le righe protette risolve (es. righe protette del target accanto a righe cambiate
                // dal task): li' si usa il nostro risultato.
                if (!tfvcExpectsConflicts || result.HasConflicts || encodingChange)
                    return new ProtectedMergePreparation
                    {
                        Mode = ProtectedMergeMode.Normal,
                        TargetLocalPath = local,
                        Message = "the task does not change protected lines"
                    };
            }
            else if (encodingChange)
            {
                return ProtectedMergePreparation.Stop("the task changes both the encoding of the file and some protected lines");
            }
            else if (result.HasConflicts && !tfvcExpectsConflicts)
            {
                // TFVC non vedra' un conflitto, ma il risultato che tiene le righe protette ne ha: non c'e'
                // un conflitto di TFVC in cui farlo risolvere, quindi ci si ferma prima del merge.
                return ProtectedMergePreparation.Stop(string.Format(CultureInfo.InvariantCulture,
                    "TFVC would merge it without conflicts, but keeping its protected lines leaves {0} to resolve by hand",
                    TaskMergeText.Count(result.Merge.ConflictCount, "overlapping change")));
            }

            byte[] bytes = null;
            if (!result.HasConflicts)
            {
                var text = ThreeWayMerge.BuildTextWithMarkers(result.Merge, ConflictResolverViewModel.SourceLabel, ConflictResolverViewModel.TargetLabel);
                try
                {
                    bytes = ConflictResolverViewModel.EncodeResult(text, target.Encoding, target.Bom);
                }
                catch (EncoderFallbackException)
                {
                    return ProtectedMergePreparation.Stop("the merged result contains characters that cannot be saved as "
                        + target.Encoding.WebName + " (the target encoding)");
                }
            }

            return new ProtectedMergePreparation
            {
                Mode = ProtectedMergeMode.OwnResult,
                TargetLocalPath = local,
                Result = result,
                ResultBytes = bytes,
                Message = !touchesProtected
                    ? "the task does not change protected lines; the conflict TFVC expects is resolved by the merge that keeps them"
                    : result.HasConflicts
                        ? string.Format(CultureInfo.InvariantCulture, "the task changes protected lines: the merge keeps the target's ones and leaves {0} to resolve",
                            TaskMergeText.Count(result.Merge.ConflictCount, "conflict"))
                        : "the task changes protected lines: the merge keeps the target's ones"
            };
        }

        // Item file alla versione changeset (null se non c'e' o non e' un file).
        private static Item GetFileItem(VersionControlServer vcs, string serverItem, int changesetId)
        {
            if (changesetId <= 0)
                return null;
            Item item;
            try
            {
                item = vcs.GetItem(serverItem, new ChangesetVersionSpec(changesetId), DeletedState.NonDeleted);
            }
            catch (Exception ex) when (IsItemNotFound(ex))
            {
                return null;
            }
            return item != null && item.ItemType == ItemType.File ? item : null;
        }

        private static bool IsItemNotFound(Exception ex)
        {
            for (var type = ex == null ? null : ex.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name == "ItemNotFoundException" || type.Name == "ItemNotMappedException")
                    return true;
            }
            return false;
        }

        // TFVC ha dato conflitto sul file e il nostro risultato non ne ha: si risolve con AcceptMerge e il
        // nostro file (come "Accept result" del resolver), poi si verifica che il file del workspace sia
        // esattamente il risultato. Thread di background.
        public static ProtectedResolveOutcome ResolveConflictWithResult(Workspace workspace, TaskMergeStep step, byte[] resultBytes, string tempFolder)
        {
            var conflicts = workspace.QueryConflicts(new[] { step.TargetItem }, false) ?? new Conflict[0];
            var mine = conflicts
                .Where(c => c != null && string.Equals((c.YourServerItem ?? string.Empty).TrimEnd('/'), step.TargetItem.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mine.Count != 1)
                return new ProtectedResolveOutcome
                {
                    Message = mine.Count == 0 ? "TFVC reports no conflict on the file" : "TFVC reports more than one conflict on the file"
                };

            var conflict = mine[0];
            if (conflict.IsNamespaceConflict || conflict.IsBinary || !conflict.CanMergeContent)
                return new ProtectedResolveOutcome { Message = "the conflict is not a text merge" };

            Directory.CreateDirectory(tempFolder);
            var file = Path.Combine(tempFolder, "result-" + step.Number.ToString(CultureInfo.InvariantCulture) + TaskMergeFileContent.TempExtension(step.TargetItem));
            File.WriteAllBytes(file, resultBytes);

            var outcome = ConflictResolverViewModel.ResolveWithMergedFile(workspace, conflict, file);
            if (!outcome.Item1)
                return new ProtectedResolveOutcome { Message = outcome.Item2 };

            var local = workspace.TryGetLocalItemForServerItem(step.TargetItem);
            var written = !string.IsNullOrEmpty(local) && File.Exists(local) ? File.ReadAllBytes(local) : null;
            if (!TaskMergeFileContent.SameBytes(written, resultBytes))
                return new ProtectedResolveOutcome
                {
                    Resolved = true,
                    Message = "TFVC marked the conflict as resolved, but the file in the workspace is not the computed result"
                };
            return new ProtectedResolveOutcome { Resolved = true, Verified = true };
        }

        // TFVC ha fuso il file senza conflitti (con le modifiche del task alle righe protette): si scrive
        // il nostro risultato nel file in sospeso e si rilegge. null = fatto e verificato; altrimenti il
        // motivo. Thread di background.
        public static string WriteResult(Workspace workspace, TaskMergeStep step, byte[] resultBytes)
        {
            var pending = workspace.GetPendingChanges(step.TargetItem, RecursionType.None) ?? new PendingChange[0];
            var change = pending.FirstOrDefault(p => p != null
                && string.Equals((p.ServerItem ?? string.Empty).TrimEnd('/'), step.TargetItem.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (change == null || !change.IsMerge)
                return "TFVC did not pend a merge on the file";

            var local = change.LocalItem ?? workspace.TryGetLocalItemForServerItem(step.TargetItem);
            if (string.IsNullOrEmpty(local) || !File.Exists(local))
                return "the merged file is not in the workspace";

            var current = File.ReadAllBytes(local);
            if (TaskMergeFileContent.SameBytes(current, resultBytes))
                return null;
            if (!change.IsEdit)
                return "TFVC did not pend an edit on the file, so the result that keeps the protected lines cannot be saved";
            if ((File.GetAttributes(local) & FileAttributes.ReadOnly) != 0)
                return "the merged file is read-only in the workspace";

            File.WriteAllBytes(local, resultBytes);
            var written = File.ReadAllBytes(local);
            return TaskMergeFileContent.SameBytes(written, resultBytes)
                ? null
                : "the file read back after writing is not the computed result";
        }

        // Dopo il merge di un file nuovo nel target: righe protette dentro? null = nessuna; altrimenti la
        // nota per il promemoria. Thread di background; non lancia.
        public static string DescribeNewFile(Workspace workspace, TaskMergeStep step, IReadOnlyList<MergeLineRule> rules)
        {
            try
            {
                var pending = workspace.GetPendingChanges(step.TargetItem, RecursionType.None) ?? new PendingChange[0];
                var change = pending.FirstOrDefault(p => p != null
                    && string.Equals((p.ServerItem ?? string.Empty).TrimEnd('/'), step.TargetItem.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                var local = change != null && !string.IsNullOrEmpty(change.LocalItem)
                    ? change.LocalItem
                    : workspace.TryGetLocalItemForServerItem(step.TargetItem);
                if (string.IsNullOrEmpty(local) || !File.Exists(local))
                    return "new file with rules for protected lines, not found in the workspace after the merge: review it";

                var text = ConflictResolverViewModel.ReadText(local, "new", change == null ? UnknownCodePage : change.Encoding);
                if (text.Error != null)
                    return "new file with rules for protected lines that cannot be read as text (" + TrimPeriod(text.Error) + "): review it";

                var found = MergePolicyEngine.CompareProtectedLines(string.Empty, text.Text, rules);
                if (found == null)
                    return "new file with rules for protected lines that could not be checked: review it";
                return found.Count == 0
                    ? null
                    : "new file that contains protected lines, merged whole from the source: review them";
            }
            catch (Exception ex)
            {
                return "new file with rules for protected lines that could not be checked (" + ex.Message + "): review it";
            }
        }

        private static string TrimPeriod(string text)
        {
            return string.IsNullOrEmpty(text) ? text : text.TrimEnd('.', ' ');
        }
    }
}
