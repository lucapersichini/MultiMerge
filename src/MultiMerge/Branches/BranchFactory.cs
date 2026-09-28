// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System.Collections.Generic;
using MultiMerge.Prism.Events;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace MultiMerge
{
	public class BranchFactory
	{
		private readonly string _sourceBranch;
		private readonly string _sourceFolder;
		private readonly ChangesetVersionSpec _changesetVersion;
		private readonly List<ChangesetBatch> _batches;
		private readonly BranchValidator _branchValidator;
		private readonly IEventAggregator _eventAggregator;

	    public BranchFactory(string sourceBranch,
			string sourceFolder,
			ChangesetVersionSpec changesetVersion,
			BranchValidator branchValidator,
			IEventAggregator eventAggregator)
			: this(sourceBranch, sourceFolder, branchValidator, eventAggregator)
		{
			_changesetVersion = changesetVersion;
		}

		public BranchFactory(string sourceBranch,
			string sourceFolder,
			List<ChangesetBatch> batches,
			BranchValidator branchValidator,
			IEventAggregator eventAggregator)
			: this(sourceBranch, sourceFolder, branchValidator, eventAggregator)
		{
			_batches = batches;
		}

		private BranchFactory(string sourceBranch,
			string sourceFolder,
			BranchValidator branchValidator,
			IEventAggregator eventAggregator)
		{
			_sourceBranch = sourceBranch;
			_sourceFolder = sourceFolder;
			_branchValidator = branchValidator;
			_eventAggregator = eventAggregator;
		}

		public MergeInfoViewModel CreateTargetBranchInfo(ItemIdentifier targetBranch, ItemIdentifier targetPath)
		{
			return CreateBranch(targetBranch.Item, targetPath.Item);
		}

		public MergeInfoViewModel CreateSourceBranch()
		{
			return CreateBranch(_sourceBranch, _sourceFolder);
		}

		private MergeInfoViewModel CreateBranch(string targetBranch, string targetPath)
		{
			var mergeInfo = new MergeInfoViewModel(_eventAggregator)
			{
				SourceBranch = _sourceBranch,
				TargetBranch = targetBranch,
				SourcePath = _sourceFolder,
				TargetPath = targetPath,
				ChangesetVersionSpec = _changesetVersion,
				Batches = _batches,
				ValidationResult = BranchValidationResult.Success,
			};

			if (_sourceBranch != targetBranch)
			{
				mergeInfo = _branchValidator.Validate(mergeInfo);
			}

			return mergeInfo;
		}
	}
}
