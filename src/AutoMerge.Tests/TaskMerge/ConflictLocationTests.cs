using System;
using System.Collections.Generic;
using System.Linq;
using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.TaskMerge
{
    // Righe dei blocchi in conflitto nel source e nel target (per sincronizzare il confronto con il
    // blocco corrente del risultato) e corrispondenza regioni aperte -> conflitti originali.
    public class ConflictLocationTests
    {
        // ------------------------------------------------------------------------------------------
        // ThreeWayMerge.GetConflictLocations
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void GetConflictLocations_NoConflicts_ReturnsEmpty()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nB\nc\n", "a\nb\nc\n");

            Assert.Empty(ThreeWayMerge.GetConflictLocations(result));
        }

        [Fact]
        public void GetConflictLocations_SingleConflict_SameLineOnBothSides()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nS\nc\n", "a\nT\nc\n");

            var location = Assert.Single(ThreeWayMerge.GetConflictLocations(result));
            Assert.Equal(0, location.Index);
            Assert.Equal(1, location.SourceStartLine);
            Assert.Equal(1, location.SourceLineCount);
            Assert.Equal(1, location.TargetStartLine);
            Assert.Equal(1, location.TargetLineCount);
        }

        [Fact]
        public void GetConflictLocations_LinesAddedOnOneSide_ShiftOnlyThatSide()
        {
            // Primo conflitto sulla riga 2; il source aggiunge due righe dopo la 3 (non in conflitto);
            // secondo conflitto sulla riga 6, che nel source e' scesa di due righe.
            const string baseText = "1\n2\n3\n4\n5\n6\n7\n";
            const string source = "1\nS2\n3\nnew-a\nnew-b\n4\n5\nS6\n7\n";
            const string target = "1\nT2\n3\n4\n5\nT6\n7\n";

            var locations = ThreeWayMerge.GetConflictLocations(ThreeWayMerge.Merge(baseText, source, target));

            Assert.Equal(2, locations.Count);
            Assert.Equal(new[] { 1, 1, 1, 1 }, Describe(locations[0]));
            Assert.Equal(new[] { 7, 1, 5, 1 }, Describe(locations[1]));
            Assert.Equal(1, locations[1].Index);
        }

        [Fact]
        public void GetConflictLocations_CommonPrefixIsNotPartOfTheConflict()
        {
            // "same" e' uguale nei due lati: la fusione lo toglie dal conflitto (blocco BothSame).
            var result = ThreeWayMerge.Merge("a\nx\nz\n", "a\nsame\nS\nz\n", "a\nsame\nT\nz\n");

            var location = Assert.Single(ThreeWayMerge.GetConflictLocations(result));
            Assert.Equal(new[] { 2, 1, 2, 1 }, Describe(location));
        }

        [Fact]
        public void GetConflictLocations_SideWithoutLines_PointsWhereTheLinesWouldBe()
        {
            // Il source cancella "b", il target la modifica: nel source il blocco e' vuoto.
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nc\n", "a\nB\nc\n");

            var location = Assert.Single(ThreeWayMerge.GetConflictLocations(result));
            Assert.Equal(new[] { 1, 0, 1, 1 }, Describe(location));
        }

        [Fact]
        public void GetConflictLocations_MatchTheRegionsOfTheTextWithMarkers()
        {
            const string baseText = "h1\r\nh2\r\nk1\r\nk2\r\nk3\r\nm1\r\nm2\r\nend\r\n";
            const string source = "h1\r\nH2-source\r\nk1\r\nk2\r\nk3\r\nadded\r\nm1\r\nM2-source\r\nend\r\n";
            const string target = "h1\r\nH2-target\r\nH2-target-bis\r\nk1\r\nk2\r\nk3\r\nm1\r\nM2-target\r\nend\r\n";

            var merge = ThreeWayMerge.Merge(baseText, source, target);
            var text = ThreeWayMerge.BuildTextWithMarkers(merge, "SOURCE", "TARGET");
            var regions = ThreeWayMerge.FindConflictRegions(text, merge.MarkerSize);
            var locations = ThreeWayMerge.GetConflictLocations(merge);

            Assert.Equal(merge.ConflictCount, locations.Count);
            Assert.Equal(regions.Count, locations.Count);
            var sourceLines = ThreeWayMerge.SplitLines(source);
            var targetLines = ThreeWayMerge.SplitLines(target);
            for (var i = 0; i < regions.Count; i++)
            {
                Assert.Equal(Join(sourceLines, locations[i].SourceStartLine, locations[i].SourceLineCount), regions[i].SourceText);
                Assert.Equal(Join(targetLines, locations[i].TargetStartLine, locations[i].TargetLineCount), regions[i].TargetText);
            }
        }

        // ------------------------------------------------------------------------------------------
        // ThreeWayMerge.GetLineStartOffset
        // ------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 3)]
        [InlineData(2, 5)]
        [InlineData(3, 7)]
        [InlineData(4, 8)]
        [InlineData(10, 8)]
        [InlineData(-1, 0)]
        public void GetLineStartOffset_CountsEveryKindOfTerminator(int line, int expected)
        {
            Assert.Equal(expected, ThreeWayMerge.GetLineStartOffset("a\r\nb\nc\rd", line));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void GetLineStartOffset_NoText_IsZero(string text)
        {
            Assert.Equal(0, ThreeWayMerge.GetLineStartOffset(text, 3));
        }

        // ------------------------------------------------------------------------------------------
        // OpenConflictMap
        // ------------------------------------------------------------------------------------------

        // Blocchi senza righe note (solo posizione): regioni e blocchi si accoppiano in ordine quando
        // il numero torna.
        [Fact]
        public void Map_WithoutContent_SameNumberOfRegions_IsPositional()
        {
            var map = new OpenConflictMap(Locations(3));
            map.Update(Regions("a", "b", "c"));

            Assert.True(map.IsAvailable);
            Assert.Equal(new[] { 0, 1, 2 }, Indexes(map, 3));
            Assert.Null(map.Get(3));
            Assert.Null(map.Get(-1));
        }

        [Fact]
        public void Map_WithoutContent_ResolvedRegionLeavesTheList_OthersKeepTheirOrder()
        {
            var map = new OpenConflictMap(Locations(4));
            map.Update(Regions("a", "b", "c", "d"));

            map.MarkResolved(1);
            map.Update(Regions("a", "c", "d"));

            Assert.True(map.IsAvailable);
            Assert.Equal(new[] { 0, 2, 3 }, Indexes(map, 3));
        }

        [Fact]
        public void Map_WithoutContent_CountMismatch_IsUnknownUntilTheCountComesBack()
        {
            var map = new OpenConflictMap(Locations(3));
            map.Update(Regions("a", "b", "c"));

            map.Update(Regions("a", "b"));   // una regione sparita a mano: quale, non si sa
            Assert.False(map.IsAvailable);
            Assert.Null(map.Get(0));

            map.Update(Regions("a", "b", "c"));   // il conto torna: di nuovo in ordine
            Assert.Equal(new[] { 0, 1, 2 }, Indexes(map, 3));
        }

        [Fact]
        public void Map_WithoutLocations_IsNotAvailable()
        {
            var map = new OpenConflictMap(null);
            map.Update(Regions());

            Assert.False(map.IsAvailable);
            Assert.Null(map.Get(0));

            map.Update(null);
            Assert.Equal(0, map.Count);
        }

        [Fact]
        public void Map_MarkResolvedOutsideTheList_IsIgnored()
        {
            var map = new OpenConflictMap(Locations(2));
            map.Update(Regions("a", "b"));

            map.MarkResolved(5);
            map.MarkResolved(-1);
            map.Update(Regions("a", "b"));

            Assert.Equal(new[] { 0, 1 }, Indexes(map, 2));
        }

        [Fact]
        public void Map_FollowsResolveRegion_OnARealMerge()
        {
            var fixture = new MapFixture();
            Assert.Equal(3, fixture.Regions.Count);

            // Take target sul blocco centrale: restano il primo e il terzo conflitto originale.
            fixture.Take(1, ConflictChoice.TakeTarget);

            Assert.True(fixture.Map.IsAvailable);
            Assert.Equal(new[] { 0, 2 }, Indexes(fixture.Map, 2));
            Assert.Equal(5, fixture.Map.Get(1).TargetStartLine);
            Assert.Equal("S6\n", fixture.Regions[1].SourceText);
        }

        // Una regione rotta mentre l'utente riscrive la riga "=======" (stato intermedio passato al view
        // model dopo la pausa): le altre restano agganciate e, a testo ripristinato, torna anche lei.
        [Fact]
        public void Map_MalformedRegionWhileTyping_DoesNotLoseTheOthers()
        {
            var fixture = new MapFixture();
            var original = fixture.Text;

            var separator = fixture.Regions[1].Start + fixture.Text.Substring(fixture.Regions[1].Start).IndexOf("=======", StringComparison.Ordinal);
            fixture.SetText(original.Remove(separator, 3));   // "===="
            Assert.Equal(2, fixture.Regions.Count);
            Assert.Equal(new[] { 0, 2 }, Indexes(fixture.Map, 2));

            fixture.SetText(original);
            Assert.Equal(new[] { 0, 1, 2 }, Indexes(fixture.Map, 3));
        }

        // Take source e poi Ctrl+Z nel RESULT: la regione ricompare e ritrova il suo blocco.
        [Fact]
        public void Map_TakeThenUndo_FindsTheBlockAgain()
        {
            var fixture = new MapFixture();
            var original = fixture.Text;

            fixture.Take(0, ConflictChoice.TakeSource);
            Assert.Equal(new[] { 1, 2 }, Indexes(fixture.Map, 2));

            fixture.SetText(original);
            Assert.Equal(new[] { 0, 1, 2 }, Indexes(fixture.Map, 3));

            // Il blocco ricomparso e' di nuovo aperto: un altro Take lo toglie come prima.
            fixture.Take(0, ConflictChoice.TakeTarget);
            Assert.Equal(new[] { 1, 2 }, Indexes(fixture.Map, 2));
        }

        // Righe cambiate a mano dentro una regione (marker ancora presenti): il contenuto non torna
        // piu', ma la regione sta fra due regioni riconosciute e c'e' un solo blocco libero fra loro.
        [Fact]
        public void Map_RegionEditedByHand_KeepsItsBlockByPosition()
        {
            var fixture = new MapFixture();

            fixture.SetText(fixture.Text.Replace("T4\n", "T4 edited by hand\n"));

            Assert.Equal(3, fixture.Regions.Count);
            Assert.Equal(new[] { 0, 1, 2 }, Indexes(fixture.Map, 3));
        }

        // Una regione cancellata a mano (senza Take) e un'altra modificata: fra le due regioni
        // riconosciute ci sono due blocchi liberi per una regione sola, quindi quella resta senza blocco.
        [Fact]
        public void Map_AmbiguousHandEdit_LeavesOnlyThatRegionUnknown()
        {
            var fixture = new MapFixture();
            var region = fixture.Regions[0];
            var text = fixture.Text.Remove(region.Start, region.Length).Replace("S4\n", "S4 edited\n");

            fixture.SetText(text);

            Assert.Equal(2, fixture.Regions.Count);
            Assert.Null(fixture.Map.Get(0));
            Assert.Equal(2, fixture.Map.Get(1).Index);
        }

        // Due blocchi con lo stesso contenuto: dopo Take sul primo, la regione rimasta e' il secondo;
        // dopo Ctrl+Z tornano nell'ordine del testo.
        [Fact]
        public void Map_DuplicateBlocks_FollowTakeAndUndo()
        {
            const string baseText = "a\nx\nb\nx\nc\n";
            const string source = "a\nS\nb\nS\nc\n";
            const string target = "a\nT\nb\nT\nc\n";
            var fixture = new MapFixture(baseText, source, target);
            var original = fixture.Text;
            Assert.Equal(new[] { 0, 1 }, Indexes(fixture.Map, 2));

            fixture.Take(0, ConflictChoice.TakeBoth);
            Assert.Equal(new[] { 1 }, Indexes(fixture.Map, 1));
            Assert.Equal(3, fixture.Map.Get(0).TargetStartLine);

            fixture.SetText(original);
            Assert.Equal(new[] { 0, 1 }, Indexes(fixture.Map, 2));
        }

        // Il risultato ha le righe del source con l'a-capo del target: il confronto ignora i terminatori.
        [Fact]
        public void Map_MatchesRegardlessOfLineTerminators()
        {
            const string baseText = "a\r\nb\r\nc\r\nd\r\ne\r\n";
            const string source = "a\nS1\nc\nS2\ne\n";
            const string target = "a\r\nT1\r\nc\r\nT2\r\ne\r\n";
            var fixture = new MapFixture(baseText, source, target);
            Assert.Contains("S1\r\n", fixture.Text);

            // Il primo blocco sparisce a mano (senza Take): il secondo si riconosce dal contenuto.
            var region = fixture.Regions[0];
            fixture.SetText(fixture.Text.Remove(region.Start, region.Length));

            Assert.Equal(new[] { 1 }, Indexes(fixture.Map, 1));
        }

        private sealed class MapFixture
        {
            public MapFixture()
                : this("1\n2\n3\n4\n5\n6\n7\n", "1\nS2\n3\nS4\n5\nS6\n7\n", "1\nT2\n3\nT4\n5\nT6\n7\n")
            {
            }

            public MapFixture(string baseText, string source, string target)
            {
                Merge = ThreeWayMerge.Merge(baseText, source, target);
                Map = new OpenConflictMap(ThreeWayMerge.GetConflictLocations(Merge));
                SetText(ThreeWayMerge.BuildTextWithMarkers(Merge, "SOURCE", "TARGET"));
            }

            public ThreeWayMergeResult Merge { get; private set; }

            public OpenConflictMap Map { get; private set; }

            public string Text { get; private set; }

            public IReadOnlyList<ConflictRegion> Regions { get; private set; }

            // Come ConflictResolverViewModel.SetResultTextCore.
            public void SetText(string text)
            {
                Text = text;
                Regions = ThreeWayMerge.FindConflictRegions(text, Merge.MarkerSize);
                Map.Update(Regions);
            }

            // Come ConflictResolverViewModel.TakeBlock.
            public void Take(int regionIndex, ConflictChoice choice)
            {
                var text = ThreeWayMerge.ResolveRegion(Text, Regions[regionIndex], choice);
                Map.MarkResolved(regionIndex);
                SetText(text);
            }
        }

        private static int[] Indexes(OpenConflictMap map, int count)
        {
            return Enumerable.Range(0, count).Select(i => map.Get(i) == null ? -1 : map.Get(i).Index).ToArray();
        }

        private static int[] Describe(ConflictLocation location)
        {
            return new[] { location.SourceStartLine, location.SourceLineCount, location.TargetStartLine, location.TargetLineCount };
        }

        private static string Join(IReadOnlyList<string> lines, int start, int count)
        {
            return string.Concat(lines.Skip(start).Take(count));
        }

        private static IEnumerable<ConflictLocation> Locations(int count)
        {
            return Enumerable.Range(0, count).Select(i => new ConflictLocation(i, i * 10, 1, i * 10, 1)).ToList();
        }

        // Regioni finte con contenuto diverso (i blocchi di Locations non hanno righe: contano solo le posizioni).
        private static IReadOnlyList<ConflictRegion> Regions(params string[] names)
        {
            return names.Select((name, i) => new ConflictRegion(i, i * 100, 10, i * 5, name + "-source\n", name + "-target\n")).ToList();
        }
    }
}
