// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using MultiMerge.VersionControl;
using Xunit;

namespace MultiMerge.Tests
{
    public class MergeContextResolverTests
    {
        [Theory]
        [InlineData(null, false, MergeContextKind.Unknown)]
        [InlineData(VersionControlProvider.Git, false, MergeContextKind.Unknown)]
        [InlineData(VersionControlProvider.TeamFoundation, false, MergeContextKind.Unknown)]
        [InlineData(null, true, MergeContextKind.Unknown)]
        [InlineData(VersionControlProvider.Git, true, MergeContextKind.Unknown)]
        [InlineData(VersionControlProvider.TeamFoundation, true, MergeContextKind.Tfvc)]
        public void NoFolderRequiresExplicitTfvcProviderAndConnection(VersionControlProvider? provider, bool connected, MergeContextKind expected)
        {
            Assert.Equal(expected, MergeContextResolver.Resolve(provider, new string[0], new string[0], connected, false, false).Kind);
        }
        [Theory]
        [InlineData(null, MergeContextKind.Git)]
        [InlineData(VersionControlProvider.Git, MergeContextKind.Git)]
        [InlineData(VersionControlProvider.TeamFoundation, MergeContextKind.Unknown)]
        public void SingleGitRepositoryMustNotContradictProvider(VersionControlProvider? provider, MergeContextKind expected)
        {
            Assert.Equal(expected, MergeContextResolver.Resolve(provider, new[] { @"C:\repo" }, new string[0], true, true, false).Kind);
        }
        [Theory]
        [InlineData(null, true, MergeContextKind.Tfvc)]
        [InlineData(VersionControlProvider.TeamFoundation, true, MergeContextKind.Tfvc)]
        [InlineData(VersionControlProvider.Git, true, MergeContextKind.Unknown)]
        [InlineData(VersionControlProvider.TeamFoundation, false, MergeContextKind.Unknown)]
        public void TfvcMappingRequiresConnectionAndCompatibleProvider(VersionControlProvider? provider, bool connected, MergeContextKind expected)
        {
            Assert.Equal(expected, MergeContextResolver.Resolve(provider, new string[0], new[] { "workspace" }, connected, true, false).Kind);
        }
        [Fact] public void TwoGitRootsRequireAChoice()
        {
            Assert.Equal(MergeContextKind.Unknown, MergeContextResolver.Resolve(VersionControlProvider.Git, new[] { @"C:\one", @"C:\two" }, new string[0], false, true, false).Kind);
        }
        [Fact] public void DuplicateGitRootsAreOneContext()
        {
            var choice = MergeContextResolver.Resolve(null, new[] { @"C:\repo", @"c:\REPO" }, new string[0], false, true, false);
            Assert.Equal(MergeContextKind.Git, choice.Kind); Assert.Equal(@"C:\repo", choice.Location);
        }
        [Fact] public void MixedGitAndTfvcRequireAChoice()
        {
            Assert.Equal(MergeContextKind.Unknown, MergeContextResolver.Resolve(null, new[] { @"C:\repo" }, new[] { "workspace" }, true, true, false).Kind);
        }
        [Fact] public void MultipleTfvcWorkspacesRequireAChoice()
        {
            Assert.Equal(MergeContextKind.Unknown, MergeContextResolver.Resolve(null, new string[0], new[] { "one", "two" }, true, true, false).Kind);
        }
        [Fact] public void UninspectedFoldersBlockAutomaticChoice()
        {
            Assert.Equal(MergeContextKind.Unknown, MergeContextResolver.Resolve(VersionControlProvider.Git, new[] { @"C:\repo" }, new string[0], false, true, true).Kind);
        }
        [Fact] public void UnmappedSolutionAndServerConnectionAreInsufficient()
        {
            Assert.Equal(MergeContextKind.Unknown, MergeContextResolver.Resolve(VersionControlProvider.TeamFoundation, new string[0], new string[0], true, true, false).Kind);
        }
    }
}
