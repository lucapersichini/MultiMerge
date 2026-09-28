using System;
using System.Collections.Generic;

namespace AutoMerge
{
    // Stato del flusso "Merge from Task" per tutta la sessione di Visual Studio. Team Explorer ricrea
    // da zero la pagina Auto Merge e le sue sezioni quando ci si torna dal menu invece che con
    // "indietro" (es. dopo Resolve Conflicts / Pending Changes, dove il merge porta in automatico),
    // quindi lo stato del task non puo' vivere solo nei view model: si perderebbe ad ogni ritorno.
    // L'avanzamento della catena NON sta qui: viene ricavato ogni volta dalla storia dei merge sul
    // server. Accesso dal thread UI, come il resto dello stato dei view model.
    internal static class TaskMergeSession
    {
        public static string WorkItemIdText { get; set; }

        public static string TargetBranchesText { get; set; }

        public static string StatusMessage { get; set; }

        public static List<TaskChangesetGroup> Groups { get; set; }

        // Gruppo che pilota il pannello Target branches. null = nessun task attivo: il pannello segue
        // la selezione in "My recent changesets" (comportamento originale dell'estensione).
        public static TaskChangesetGroup ActiveGroup { get; set; }

        // Workspace (QualifiedName) in cui la catena ha lavorato l'ultima volta: preferita al ritorno
        // sulla pagina, invece della workspace di default.
        public static string WorkspaceName { get; set; }

        // Collection del task: i path $/ e i changeset valgono solo li'.
        public static Uri CollectionUri { get; set; }

        // Se Visual Studio e' ora collegato a un'altra collection, il task in sessione non ha piu'
        // senso (path e changeset di un altro server): si azzera tutto.
        public static void EnsureCollection(Uri currentCollectionUri)
        {
            if (CollectionUri == null || currentCollectionUri == null || CollectionUri == currentCollectionUri)
                return;

            WorkItemIdText = null;
            TargetBranchesText = null;
            StatusMessage = null;
            Groups = null;
            ActiveGroup = null;
            WorkspaceName = null;
            CollectionUri = null;
        }
    }
}
