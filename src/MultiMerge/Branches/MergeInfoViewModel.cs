// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using System.Collections.Generic;
using MultiMerge.Events;
using MultiMerge.Prism.Events;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
	public class MergeInfoViewModel
	{
		private readonly IEventAggregator _eventAggregator;
		internal bool _checked;

		public MergeInfoViewModel(IEventAggregator eventAggregator)
		{
			_eventAggregator = eventAggregator;
		}

		public bool Checked
		{
			get
			{
				return _checked;
			}
			set
			{
				_checked = value;
				_eventAggregator.GetEvent<BranchSelectedChangedEvent>().Publish(this);
			}
		}

		public string SourcePath { get; set; }

		public string TargetPath { get; set; }

		public string SourceBranch { get; set; }

		public string TargetBranch { get; set; }

		public ChangesetVersionSpec ChangesetVersionSpec { get; set; }

		public List<ChangesetBatch> Batches { get; set; }   // null in modalità "singolo changeset" (comportamento invariato)

		// Flusso Task, solo per il badge: batch i cui changeset non risultano ancora tutti fusi nel
		// target secondo la storia dei merge sul server (TrackMerges), ricalcolato ad ogni refresh.
		// NON decide da dove riparte Merge: la catena riparte sempre dal primo batch e TFVC salta da
		// solo, file per file, cio' che e' gia' fuso (un changeset puo' risultare "fuso" anche se ne
		// era stata portata solo una parte dei file, e saltarlo perderebbe il resto).
		public int RemainingBatches { get; set; }

		// Numero 1-based del batch su cui l'ultimo Merge si e' fermato per conflitti (0 = nessuno),
		// usato solo per il commento di check-in del merge parziale.
		public int StoppedAtBatchNumber { get; set; }

		public string DisplayBranchName
		{
			get
			{
				return BranchHelper.GetShortBranchName(TargetBranch);
			}
		}

		public BranchValidationResult ValidationResult { get; set; }

		public string ValidationMessage { get; set; }

		public bool IsSourceBranch
		{
			get
			{
				return string.Equals(SourceBranch, TargetBranch, StringComparison.OrdinalIgnoreCase);
			}
		}

		// Testo tipo "6/20 remaining" per il flusso Task: quanti batch mancano su quanti totali, cosi'
		// l'utente vede subito a che punto e' la catena; "all merged" quando la storia sul server li da'
		// tutti per fusi (Merge resta comunque cliccabile, vedi BranchValidator.ValidateItem). Vuoto per
		// il flusso a changeset singolo (Batches == null) e per la riga del branch sorgente.
		public string BatchProgressText
		{
			get
			{
				if (Batches == null || Batches.Count == 0 || IsSourceBranch)
					return null;

				return RemainingBatches > 0
					? string.Format("{0}/{1} remaining", RemainingBatches, Batches.Count)
					: "all merged";
			}
		}
	}
}
