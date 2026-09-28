using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace AutoMerge
{
	public class BranchValidator
	{
		private readonly Workspace _workspace;
		private readonly IEnumerable<ExtendedMerge> _trackMerges;
		private readonly IReadOnlyCollection<int> _requiredChangesetIds;

		public BranchValidator(Workspace workspace, IEnumerable<ExtendedMerge> trackMerges)
			: this(workspace, trackMerges, null)
		{
		}

		// requiredChangesetIds: non null = flusso Task (changeset del gruppo). In quel flusso la riga
		// non viene mai disabilitata come "Already merged" (vedi ValidateItem) e, con modifiche locali e
		// conflitti aperti, il messaggio dice di risolvere e fare check-in prima di continuare.
		// null (default, flusso a changeset singolo): comportamento invariato di prima.
		public BranchValidator(Workspace workspace, IEnumerable<ExtendedMerge> trackMerges,
			IReadOnlyCollection<int> requiredChangesetIds)
		{
			_workspace = workspace;
			_trackMerges = trackMerges;
			_requiredChangesetIds = requiredChangesetIds;
		}

		public MergeInfoViewModel Validate(MergeInfoViewModel branchInfo)
		{
			branchInfo.ValidationResult = ValidateItem(_workspace, branchInfo, _trackMerges);
			branchInfo.ValidationMessage = ToMessage(branchInfo.ValidationResult);

			return branchInfo;
		}

		// Changeset (tra quelli passati a TrackMerges) che la storia dei merge sul server da' come gia'
		// fusi da sourcePath a targetPath, o viceversa. Usato per "Already merged" (flusso a changeset
		// singolo) e per il badge del flusso Task (BranchesViewModel.ApplyChainProgress).
		// OrdinalIgnoreCase: targetPath puo' essere scritto a mano dall'utente con un casing diverso da
		// quello canonico restituito dal server.
		internal static HashSet<int> GetMergedChangesetIds(string sourcePath, string targetPath, IEnumerable<ExtendedMerge> trackMerges)
		{
			if (trackMerges == null)
				return new HashSet<int>();

			return new HashSet<int>(trackMerges
				.Where(m =>
					(string.Equals(m.TargetItem.Item, sourcePath, StringComparison.OrdinalIgnoreCase) && string.Equals(m.SourceItem.Item.ServerItem, targetPath, StringComparison.OrdinalIgnoreCase))
					|| (string.Equals(m.TargetItem.Item, targetPath, StringComparison.OrdinalIgnoreCase) && string.Equals(m.SourceItem.Item.ServerItem, sourcePath, StringComparison.OrdinalIgnoreCase)))
				.Select(m => m.SourceChangeset.ChangesetId));
		}

		private BranchValidationResult ValidateItem(Workspace workspace, MergeInfoViewModel mergeInfoViewModel, IEnumerable<ExtendedMerge> trackMerges)
		{
			var result = BranchValidationResult.Success;

			// Solo flusso a changeset singolo. Nel flusso Task la storia dei merge da' un changeset come
			// "fuso" anche se ne e' stata portata solo una parte dei file (es. dopo uno stop per failures
			// e il check-in del parziale): disabilitare la riga impedirebbe di completare la catena. Li'
			// la storia alimenta solo il badge ("all merged" / "X/Y remaining"); un Merge rilanciato
			// quando davvero non resta nulla finisce semplicemente in "Nothing merged".
			if (result == BranchValidationResult.Success && _requiredChangesetIds == null)
			{
				var isMerged = IsMerged(mergeInfoViewModel.SourcePath, mergeInfoViewModel.TargetPath, trackMerges);
				if (isMerged)
					result = BranchValidationResult.AlreadyMerged;
			}

			if (result == BranchValidationResult.Success)
			{
				var userHasAccess = UserHasAccess(workspace.VersionControlServer, mergeInfoViewModel.TargetPath);
				if (!userHasAccess)
					result = BranchValidationResult.NoAccess;
			}

			if (result == BranchValidationResult.Success)
			{
				var isMapped = IsMapped(workspace, mergeInfoViewModel.TargetPath);
				if (!isMapped)
					result = BranchValidationResult.BranchNotMapped;
			}

			if (result == BranchValidationResult.Success)
			{
				// Target sempre pulito prima di fondere, anche nel flusso Task quando si riprende una catena
				// fermata da conflitti: si riparte solo dopo il check-in (o l'annullamento) di quanto gia'
				// fatto. Cosi' la catena non mescola mai lavoro non correlato e l'avanzamento si puo'
				// ricavare in modo affidabile dalla storia dei merge sul server.
				var hasLocalChanges = HasLocalChanges(workspace, mergeInfoViewModel.TargetPath);
				if (hasLocalChanges)
				{
					// Flusso Task: se ci sono ancora conflitti aperti il messaggio dice cosa fare dopo.
					result = _requiredChangesetIds != null
						&& !workspace.QueryConflicts(new[] { mergeInfoViewModel.TargetPath }, true).IsNullOrEmpty()
						? BranchValidationResult.HasUnresolvedConflicts
						: BranchValidationResult.ItemHasLocalChanges;
				}
			}
			return result;
		}

		private static bool IsMerged(string sourcePath, string targetPath, IEnumerable<ExtendedMerge> trackMerges)
		{
			return GetMergedChangesetIds(sourcePath, targetPath, trackMerges).Count > 0;
		}

		private static bool UserHasAccess(VersionControlServer versionControlServer, string targetPath)
		{
			var permissions = versionControlServer.GetEffectivePermissions(versionControlServer.AuthorizedUser, targetPath);

			if (permissions == null || permissions.Length < 4)
				return false;

			return permissions.Contains("Read")
				&& permissions.Contains("PendChange")
				&& permissions.Contains("Checkin")
				&& permissions.Contains("Merge");
		}

		private static bool IsMapped(Workspace workspace, string targetItem)
		{
			return workspace.IsServerPathMapped(targetItem);
		}

		private static bool HasLocalChanges(Workspace workspace, string targetPath)
		{
			return workspace.GetPendingChangesEnumerable(targetPath, RecursionType.Full).Any();
		}

		private static string ToMessage(BranchValidationResult validationResult)
		{
			switch (validationResult)
			{
				case BranchValidationResult.Success:
					return null;
				case BranchValidationResult.AlreadyMerged:
					return "Already merged";
				case BranchValidationResult.BranchNotMapped:
					return "Branch not mapped";
				case BranchValidationResult.ItemHasLocalChanges:
					return "Folder has local changes. Check-in or undo it";
				case BranchValidationResult.NoAccess:
					return "You have not rights for edit";
				case BranchValidationResult.HasUnresolvedConflicts:
					return "Resolve conflicts and check in, then Merge to continue";
				default:
					return "Unknown error";
			}
		}
	}
}
