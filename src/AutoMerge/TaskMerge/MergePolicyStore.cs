using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace AutoMerge
{
    // Dove vivono le regole di merge (MergePolicyDocument) e come si leggono/scrivono:
    // - file di TEAM nella radice del ramo di destinazione ("<radice>/.automerge-policy.json"), letto dal
    //   server a Latest: vale per chi fonde verso quel ramo. Si modifica solo con una modifica in sospeso
    //   nel workspace (PendAdd/PendEdit): MAI un check-in da qui, lo fa l'utente da Pending Changes;
    // - file PERSONALE (%APPDATA%\AutoMerge\merge-policy.personal.json): vale per tutti i rami e VINCE
    //   SEMPRE su quello di team (regola personale con lo stesso Id = sostituzione, vedi EffectiveMergePolicy).
    //
    // Sicurezza: un file che esiste ma non si puo' leggere (rete, JSON non valido, byte non UTF-8) non
    // viene mai trattato come "nessuna regola": LoadTeam/LoadPersonal restituiscono null CON un errore, e
    // LoadEffective lancia un'eccezione, cosi' chi fonde si ferma invece di fondere senza le regole.
    //
    // Threading: LoadTeam, SaveTeam e LoadEffective chiamano TFVC: vanno chiamati fuori dal thread UI
    // (Task.Run). PolicyChanged e' sollevato sempre sul thread UI di VS (se c'e' un'applicazione WPF).
    public static class MergePolicyStore
    {
        public const string TeamFileName = ".automerge-policy.json";

        private const string PersonalFolderName = "AutoMerge";
        private const string PersonalFileName = "merge-policy.personal.json";

        // Sollevato dopo ogni salvataggio (personale o di team), sul thread UI.
        public static event EventHandler PolicyChanged;

        // %APPDATA%\AutoMerge\merge-policy.personal.json
        public static string PersonalFilePath
        {
            get
            {
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(roaming, PersonalFolderName), PersonalFileName);
            }
        }

        // Radice del ramo normalizzata ("$/P/Main": '/' come separatore, senza '/' finale); null se il testo
        // non e' un percorso server.
        public static string NormalizeBranchRoot(string targetBranchRoot)
        {
            if (string.IsNullOrWhiteSpace(targetBranchRoot))
                return null;

            var path = targetBranchRoot.Trim().Replace('\\', '/');
            if (path == "$")
                path = "$/";
            if (!path.StartsWith("$/", StringComparison.Ordinal))
                return null;
            while (path.Length > 2 && path.EndsWith("/", StringComparison.Ordinal))
                path = path.Substring(0, path.Length - 1);
            return path;
        }

        // Percorso server del file di team: "<radice del ramo>/.automerge-policy.json".
        public static string TeamFileServerPath(string targetBranchRoot)
        {
            var root = NormalizeBranchRoot(targetBranchRoot);
            if (root == null)
                throw new ArgumentException("The target branch must be a server path such as $/Project/Main.", "targetBranchRoot");
            return root.EndsWith("/", StringComparison.Ordinal) ? root + TeamFileName : root + "/" + TeamFileName;
        }

        // Legge il file di team dal server a Latest. null se il file non esiste (error null) o se non si
        // puo' leggere (error valorizzato). Chiamata TFVC: fuori dal thread UI.
        public static MergePolicyDocument LoadTeam(VersionControlServer vcs, string targetBranchRoot, out string error)
        {
            int changesetId;
            return LoadTeam(vcs, targetBranchRoot, out changesetId, out error);
        }

        // Come sopra, e in piu' il changeset della versione letta (0 se il file non esiste).
        public static MergePolicyDocument LoadTeam(VersionControlServer vcs, string targetBranchRoot, out int changesetId, out string error)
        {
            changesetId = 0;
            error = null;

            if (vcs == null)
            {
                error = "Not connected to version control: the team policy file cannot be read.";
                return null;
            }

            string serverPath;
            try
            {
                serverPath = TeamFileServerPath(targetBranchRoot);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }

            string text;
            int version;
            try
            {
                if (!vcs.ServerItemExists(serverPath, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.File))
                    return null;

                var item = vcs.GetItem(serverPath, VersionSpec.Latest, DeletedState.NonDeleted, true);
                version = item.ChangesetId;
                using (var stream = item.DownloadFile())
                    text = ReadText(stream);
            }
            catch (Exception ex)
            {
                error = "Cannot read the team policy file " + serverPath + ": " + ex.Message;
                return null;
            }

            try
            {
                var document = ParseDocument(text);
                changesetId = version;
                return document;
            }
            catch (Exception ex)
            {
                error = "The team policy file " + serverPath + " (changeset " + version + ") is not valid: " + ex.Message;
                return null;
            }
        }

        // Legge il file personale. null se non esiste (error null) o se non si puo' leggere (error valorizzato).
        public static MergePolicyDocument LoadPersonal(out string error)
        {
            error = null;
            var path = PersonalFilePath;
            try
            {
                if (!File.Exists(path))
                    return null;
                return ParseDocument(ReadTextFile(path));
            }
            catch (Exception ex)
            {
                error = "The personal policy file " + path + " cannot be read: " + ex.Message;
                return null;
            }
        }

        // Testo grezzo del file personale (null se non esiste): serve alla scheda per accorgersi di una
        // modifica fatta fuori (editor, altra istanza di VS) prima di sovrascriverla.
        public static string ReadPersonalText()
        {
            return ReadLocalText(PersonalFilePath);
        }

        // Scrive il file personale (prima in un file temporaneo, poi lo sostituisce) e solleva PolicyChanged.
        public static void SavePersonal(MergePolicyDocument doc)
        {
            if (doc == null)
                throw new ArgumentNullException("doc");

            // Serializzare prima di toccare il disco: un errore qui non lascia file a meta'.
            var text = MergePolicyEngine.Serialize(doc);
            var path = PersonalFilePath;
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            var temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null);
                }
                catch (IOException)
                {
                    // File system senza Replace atomico: copia e poi togli il temporaneo.
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, path);
            }

            RaisePolicyChanged();
        }

        // Scrive il file di team nel percorso locale mappato del workspace e mette in sospeso l'aggiunta
        // (PendAdd) o la modifica (PendEdit). MAI check-in: il file va rivisto e archiviato da Pending
        // Changes. Ritorna il messaggio per l'utente; lancia un'eccezione (senza scrivere nulla, salvo
        // dove indicato) se il ramo non e' mappato, se il file ha un'altra modifica in sospeso o se TFVC
        // non accetta la modifica. Chiamata TFVC: fuori dal thread UI.
        public static string SaveTeam(Workspace workspace, string targetBranchRoot, MergePolicyDocument doc)
        {
            if (workspace == null)
                throw new InvalidOperationException("No workspace: the team policy file can only be written in a workspace that maps the target branch.");
            if (doc == null)
                throw new ArgumentNullException("doc");

            var serverPath = TeamFileServerPath(targetBranchRoot);
            var localPath = workspace.TryGetLocalItemForServerItem(serverPath);
            if (string.IsNullOrEmpty(localPath))
                throw new InvalidOperationException("The target branch is not mapped (or is cloaked) in workspace '" + workspace.Name + "': the team policy file cannot be written.");

            // Serializzare prima di toccare workspace e disco.
            var text = MergePolicyEngine.Serialize(doc);

            string action;
            var pending = FindPendingChange(workspace, serverPath);
            if (pending != null)
            {
                if (!pending.IsAdd && !pending.IsEdit)
                    throw new InvalidOperationException("The team policy file already has a pending " + pending.ChangeTypeName
                        + " in workspace '" + workspace.Name + "': check it in or undo it, then reload the policies.");

                // Aggiunta o modifica gia' in sospeso: basta riscrivere il file.
                WriteTeamFile(localPath, text);
                action = pending.IsAdd ? "the pending add was updated" : "the pending edit was updated";
            }
            else if (workspace.VersionControlServer.ServerItemExists(serverPath, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.File))
            {
                // La modifica parte dall'ultima versione del file: si prende Latest del solo file, e solo
                // se TFVC la da' senza errori si mette in sospeso la modifica.
                var status = workspace.Get(new GetRequest(serverPath, RecursionType.None, VersionSpec.Latest), GetOptions.None);
                if (status.NumFailures > 0 || status.NumConflicts > 0)
                    throw new InvalidOperationException("Get latest of the team policy file failed (" + status.NumFailures + " failures, "
                        + status.NumConflicts + " conflicts): nothing was written.");

                workspace.PendEdit(serverPath);
                pending = FindPendingChange(workspace, serverPath);
                if (pending == null || !pending.IsEdit)
                    throw new InvalidOperationException("TFVC did not pend an edit of " + serverPath + " in workspace '" + workspace.Name + "': nothing was written.");

                WriteTeamFile(localPath, text);
                action = "a pending edit was created";
            }
            else
            {
                var folder = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                WriteTeamFile(localPath, text);

                workspace.PendAdd(localPath);
                pending = FindPendingChange(workspace, serverPath);
                if (pending == null || !pending.IsAdd)
                    throw new InvalidOperationException("The file was written to " + localPath + " but TFVC did not pend the add in workspace '"
                        + workspace.Name + "' (is it excluded by a .tfignore?). Add it from Pending Changes or delete it.");
                action = "a pending add was created";
            }

            // Verifica: sul disco c'e' esattamente il testo salvato.
            if (!string.Equals(ReadTextFile(localPath), text, StringComparison.Ordinal))
                throw new InvalidOperationException("The team policy file " + localPath + " does not contain what was saved: check it before checking it in.");

            RaisePolicyChanged();

            return "Team policy file saved to " + localPath + ": " + action + " in workspace '" + workspace.Name
                + "'. Nothing was checked in: review and check in the file from Pending Changes to share the rules. Until then, merges use the version in the branch. "
                + PendingTeamFileBlocksMergeText;
        }

        // Il file di team in sospeso sta sotto il ramo di destinazione e non e' un merge: Merge from Task
        // non fonde e non archivia sopra modifiche che non sono sue (controllo finale R1).
        public const string PendingTeamFileBlocksMergeText =
            "While this change is pending, Merge from Task on this branch stops (Start, Continue and the check-in accept only the chain's own merges): check the file in on its own, or undo it, before merging.";

        // true se serverItem e' un file di team delle regole di merge (nome TeamFileName, in qualsiasi
        // cartella: chi chiama sa gia' che sta sotto il ramo).
        public static bool IsTeamFile(string serverItem)
        {
            if (string.IsNullOrEmpty(serverItem))
                return false;
            var path = serverItem.Trim().TrimEnd('/');
            var slash = path.LastIndexOf('/');
            return string.Equals(slash >= 0 ? path.Substring(slash + 1) : path, TeamFileName, StringComparison.OrdinalIgnoreCase);
        }

        // Changeset dell'ultima versione del file di team sul server (0 se non esiste). Serve a chi lo
        // modifica per accorgersi che un collega l'ha cambiato dopo la lettura. Chiamata TFVC.
        public static int GetTeamFileChangeset(VersionControlServer vcs, string targetBranchRoot)
        {
            if (vcs == null)
                throw new ArgumentNullException("vcs");
            var serverPath = TeamFileServerPath(targetBranchRoot);
            if (!vcs.ServerItemExists(serverPath, VersionSpec.Latest, DeletedState.NonDeleted, ItemType.File))
                return 0;
            return vcs.GetItem(serverPath, VersionSpec.Latest, DeletedState.NonDeleted, false).ChangesetId;
        }

        // Stato del file di team nel workspace: percorso locale mappato, modifica in sospeso e, se e' in
        // sospeso un'aggiunta o una modifica, il contenuto locale (quello che verra' archiviato). Chiamata
        // TFVC: fuori dal thread UI. workspace null = solo il percorso server.
        public static MergePolicyTeamFileState GetTeamFileState(Workspace workspace, string targetBranchRoot)
        {
            var state = new MergePolicyTeamFileState { ServerPath = TeamFileServerPath(targetBranchRoot) };
            if (workspace == null)
                return state;

            state.WorkspaceName = workspace.Name;
            state.LocalPath = workspace.TryGetLocalItemForServerItem(state.ServerPath);
            if (string.IsNullOrEmpty(state.LocalPath))
            {
                state.LocalPath = null;
                return state;
            }

            var pending = FindPendingChange(workspace, state.ServerPath);
            if (pending == null)
                return state;

            state.PendingChangeName = pending.ChangeTypeName;
            state.PendingIsAddOrEdit = pending.IsAdd || pending.IsEdit;
            if (!state.PendingIsAddOrEdit)
                return state;

            try
            {
                if (!File.Exists(state.LocalPath))
                {
                    state.PendingError = "The team policy file has a pending " + pending.ChangeTypeName + " but " + state.LocalPath + " does not exist.";
                    return state;
                }
                state.PendingText = ReadTextFile(state.LocalPath);
                state.PendingDocument = ParseDocument(state.PendingText);
            }
            catch (Exception ex)
            {
                state.PendingDocument = null;
                state.PendingError = "The pending team policy file " + state.LocalPath + " is not valid: " + ex.Message;
            }
            return state;
        }

        // Testo del file locale (null se non esiste): per accorgersi di una modifica fatta fuori dalla scheda.
        public static string ReadLocalText(string localPath)
        {
            return !string.IsNullOrEmpty(localPath) && File.Exists(localPath) ? ReadTextFile(localPath) : null;
        }

        // Regole effettive per un ramo di destinazione: file di team (server, Latest) + file personale.
        // Lancia InvalidOperationException se uno dei due file esiste ma non si puo' leggere: fondere
        // ignorando regole che ci sono non e' sicuro. description: da dove vengono le regole (per il registro).
        public static EffectiveMergePolicy LoadEffective(VersionControlServer vcs, string targetBranchRoot, out string description)
        {
            description = null;

            int teamChangeset;
            string teamError;
            var team = LoadTeam(vcs, targetBranchRoot, out teamChangeset, out teamError);
            if (teamError != null)
                throw new InvalidOperationException("Merge policies: " + teamError);

            string personalError;
            var personal = LoadPersonal(out personalError);
            if (personalError != null)
                throw new InvalidOperationException("Merge policies: " + personalError);

            var effective = new EffectiveMergePolicy(team, personal);

            var text = new StringBuilder();
            var serverPath = TeamFileServerPath(targetBranchRoot);
            if (team == null)
                text.Append("Team: no " + TeamFileName + " in " + NormalizeBranchRoot(targetBranchRoot));
            else
                text.Append("Team: " + serverPath + " at changeset " + teamChangeset + " (" + DescribeCounts(team) + ")");
            text.Append("; Personal: ");
            text.Append(personal == null ? "none" : PersonalFilePath + " (" + DescribeCounts(personal) + ")");
            var errors = effective.Errors == null ? 0 : effective.Errors.Count;
            if (errors > 0)
                text.Append("; " + errors + (errors == 1 ? " error" : " errors"));
            description = text.ToString();

            return effective;
        }

        // "3 path rules, 1 line rule"
        public static string DescribeCounts(MergePolicyDocument doc)
        {
            var paths = doc == null || doc.PathRules == null ? 0 : doc.PathRules.Count;
            var lines = doc == null || doc.LineRules == null ? 0 : doc.LineRules.Count;
            return paths + (paths == 1 ? " path rule, " : " path rules, ") + lines + (lines == 1 ? " line rule" : " line rules");
        }

        // Liste mai null dopo la lettura (un file "{}" e' un documento vuoto valido).
        private static MergePolicyDocument ParseDocument(string text)
        {
            var document = MergePolicyEngine.Parse(text) ?? new MergePolicyDocument();
            if (document.PathRules == null)
                document.PathRules = new List<MergePathRule>();
            if (document.LineRules == null)
                document.LineRules = new List<MergeLineRule>();
            return document;
        }

        // Testo UTF-8 (o UTF-16/32 con BOM). Byte non validi in UTF-8 = eccezione: un file letto male non
        // deve diventare regole diverse, ne' essere riscritto storpiato.
        private static string ReadText(Stream stream)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                return reader.ReadToEnd();
        }

        private static string ReadTextFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return ReadText(stream);
        }

        // UTF-8 con BOM per un file nuovo (TFVC ne riconosce la codifica all'aggiunta); per un file esistente
        // si tiene la presenza o l'assenza del BOM.
        private static void WriteTeamFile(string localPath, string text)
        {
            var withBom = true;
            if (File.Exists(localPath))
            {
                var head = new byte[3];
                int read;
                using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    read = stream.Read(head, 0, 3);
                withBom = read == 0 || (read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF);

                // Workspace server: dopo PendEdit il file e' scrivibile; se non lo fosse, meglio fermarsi
                // che togliere l'attributo di sola lettura a un file che TFVC non considera in modifica.
                if ((File.GetAttributes(localPath) & FileAttributes.ReadOnly) != 0)
                    throw new InvalidOperationException("The local file " + localPath + " is read-only: TFVC has not made it writable, nothing was written.");
            }
            File.WriteAllText(localPath, text, new UTF8Encoding(withBom));
        }

        private static PendingChange FindPendingChange(Workspace workspace, string serverPath)
        {
            var changes = workspace.GetPendingChanges(serverPath, RecursionType.None);
            return changes == null
                ? null
                : changes.FirstOrDefault(c => string.Equals(c.ServerItem, serverPath, StringComparison.OrdinalIgnoreCase))
                    ?? changes.FirstOrDefault();
        }

        // Sul thread UI se c'e' un'applicazione WPF (VS); un gestore che fallisce non ferma gli altri.
        private static void RaisePolicyChanged()
        {
            var handlers = PolicyChanged;
            if (handlers == null)
                return;

            Action raise = () =>
            {
                foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
                {
                    try
                    {
                        handler(null, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError("AutoMerge: MergePolicyStore.PolicyChanged handler failed: " + ex);
                    }
                }
            };

            var application = System.Windows.Application.Current;
            var dispatcher = application == null ? null : application.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(raise);
            else
                raise();
        }
    }

    // Stato del file di team in un workspace (MergePolicyStore.GetTeamFileState).
    public sealed class MergePolicyTeamFileState
    {
        public string ServerPath { get; set; }
        public string WorkspaceName { get; set; }
        // Percorso locale mappato; null se il ramo non e' mappato (o e' nascosto) nel workspace.
        public string LocalPath { get; set; }
        // Modifica in sospeso sul file (null = nessuna), es. "add", "edit", "delete".
        public string PendingChangeName { get; set; }
        public bool PendingIsAddOrEdit { get; set; }
        // Contenuto locale di un'aggiunta/modifica in sospeso (quello che verra' archiviato).
        public string PendingText { get; set; }
        public MergePolicyDocument PendingDocument { get; set; }
        public string PendingError { get; set; }
    }
}
