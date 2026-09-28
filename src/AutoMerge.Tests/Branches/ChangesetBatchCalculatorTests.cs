using System;
using System.Collections.Generic;
using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.Branches
{
    public class ChangesetBatchCalculatorTests
    {
        [Fact]
        public void EmptyList_ReturnsNoBatches()
        {
            var result = ChangesetBatchCalculator.CalculateBatches(new List<int>(), (a, b) => false);

            Assert.Empty(result);
        }

        [Fact]
        public void NoThirdPartyEver_ReturnsSingleBatchCoveringAll()
        {
            var ids = new List<int> { 10, 20, 30, 40 };

            var result = ChangesetBatchCalculator.CalculateBatches(ids, (a, b) => false);

            var batch = Assert.Single(result);
            Assert.Equal(10, batch.FromChangesetId);
            Assert.Equal(40, batch.ToChangesetId);
        }

        [Fact]
        public void ThirdPartyBetweenTwoElements_ReturnsTwoBatches()
        {
            var ids = new List<int> { 10, 20, 30 };

            var result = ChangesetBatchCalculator.CalculateBatches(ids, (from, to) => from == 10 && to == 20);

            Assert.Equal(2, result.Count);
            Assert.Equal(10, result[0].FromChangesetId);
            Assert.Equal(10, result[0].ToChangesetId);
            Assert.Equal(20, result[1].FromChangesetId);
            Assert.Equal(30, result[1].ToChangesetId);
        }

        [Fact]
        public void ThirdPartyBetweenEveryPair_ReturnsOneBatchPerChangeset()
        {
            var ids = new List<int> { 10, 20, 30 };

            var result = ChangesetBatchCalculator.CalculateBatches(ids, (from, to) => true);

            Assert.Equal(3, result.Count);
            Assert.Equal(10, result[0].FromChangesetId);
            Assert.Equal(10, result[0].ToChangesetId);
            Assert.Equal(20, result[1].FromChangesetId);
            Assert.Equal(20, result[1].ToChangesetId);
            Assert.Equal(30, result[2].FromChangesetId);
            Assert.Equal(30, result[2].ToChangesetId);
        }

        [Fact]
        public void SingleChangeset_ReturnsOneBatchWithFromEqualsTo()
        {
            var ids = new List<int> { 42 };

            var result = ChangesetBatchCalculator.CalculateBatches(ids, (a, b) => true);

            var batch = Assert.Single(result);
            Assert.Equal(42, batch.FromChangesetId);
            Assert.Equal(42, batch.ToChangesetId);
        }

        [Fact]
        public void OnlyConsecutivePairsAreChecked_NeverNonAdjacent()
        {
            // Regressione: l'algoritmo deve restare O(n) (un solo passaggio lineare), controllando
            // SOLO le coppie consecutive (ids[0],ids[1]), (ids[1],ids[2]), ... e mai coppie non
            // adiacenti come (ids[0],ids[2]). Un cambiamento che introducesse un controllo O(n^2)
            // (tutte le coppie) non verrebbe rilevato dai test esistenti con liste corte.
            var ids = new List<int> { 10, 20, 30, 40, 50 };
            var invokedPairs = new List<Tuple<int, int>>();

            var result = ChangesetBatchCalculator.CalculateBatches(ids, (from, to) =>
            {
                invokedPairs.Add(Tuple.Create(from, to));
                return false;
            });

            Assert.Single(result);
            Assert.Equal(ids.Count - 1, invokedPairs.Count);
            Assert.Equal(new[]
            {
                Tuple.Create(10, 20),
                Tuple.Create(20, 30),
                Tuple.Create(30, 40),
                Tuple.Create(40, 50)
            }, invokedPairs);
        }
    }
}
