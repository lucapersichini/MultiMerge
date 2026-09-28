using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.Branches
{
    public class TaskTargetPathMapperTests
    {
        [Fact]
        public void TopFolderIsSourceBranch_ReturnsTargetRoot()
        {
            var result = TaskTargetPathMapper.MapToTarget("$/Main", "$/Main", "$/Rel/b1");

            Assert.Equal("$/Rel/b1", result);
        }

        [Fact]
        public void TopFolderIsSubfolder_KeepsRelativePathUnderTarget()
        {
            var result = TaskTargetPathMapper.MapToTarget("$/Main", "$/Main/Web/Rest", "$/Rel/b1");

            Assert.Equal("$/Rel/b1/Web/Rest", result);
        }

        [Fact]
        public void DifferentCasingAndTrailingSlash_StillMapped()
        {
            var result = TaskTargetPathMapper.MapToTarget("$/MAIN/", "$/Main/Web/Rest", "$/Rel/b1/");

            Assert.Equal("$/Rel/b1/Web/Rest", result);
        }

        [Fact]
        public void SiblingWithSamePrefix_IsNotTreatedAsSubfolder()
        {
            // "$/Main2" non e' sotto "$/Main": niente mappatura parziale sul prefisso del nome.
            var result = TaskTargetPathMapper.MapToTarget("$/Main", "$/Main2/Web", "$/Rel/b1");

            Assert.Equal("$/Rel/b1", result);
        }
    }
}
