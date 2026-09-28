using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.TaskMerge
{
    public class ThreeWayMergeTests
    {
        // ------------------------------------------------------------------------------------------
        // SplitLines
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void SplitLines_KeepsEveryKindOfTerminator()
        {
            var lines = ThreeWayMerge.SplitLines("a\r\nb\nc\rd");

            Assert.Equal(new[] { "a\r\n", "b\n", "c\r", "d" }, lines.ToArray());
        }

        [Fact]
        public void SplitLines_TrailingNewLine_DoesNotAddAnEmptyLine()
        {
            var lines = ThreeWayMerge.SplitLines("a\n\nb\n");

            Assert.Equal(new[] { "a\n", "\n", "b\n" }, lines.ToArray());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void SplitLines_NullOrEmpty_ReturnsNoLines(string text)
        {
            Assert.Empty(ThreeWayMerge.SplitLines(text));
        }

        // ------------------------------------------------------------------------------------------
        // Merge: classificazione dei blocchi
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void NoChanges_ReturnsSingleUnchangedBlock()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nb\nc\n", "a\nb\nc\n");

            var block = Assert.Single(result.Blocks);
            Assert.Equal(MergeBlockKind.Unchanged, block.Kind);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\nb\nc\n", Merged(result));
        }

        [Fact]
        public void OnlySourceChanged_TakesSource()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nB\nc\n", "a\nb\nc\n");

            Assert.Equal(new[] { MergeBlockKind.Unchanged, MergeBlockKind.SourceOnly, MergeBlockKind.Unchanged }, Kinds(result));
            Assert.Equal(new[] { "B\n" }, result.Blocks[1].ResultLines.ToArray());
            Assert.Equal(new[] { "b\n" }, result.Blocks[1].BaseLines.ToArray());
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\nB\nc\n", Merged(result));
        }

        [Fact]
        public void OnlyTargetChanged_KeepsTarget()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nb\nc\n", "a\nT\nc\n");

            Assert.Equal(new[] { MergeBlockKind.Unchanged, MergeBlockKind.TargetOnly, MergeBlockKind.Unchanged }, Kinds(result));
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\nT\nc\n", Merged(result));
        }

        [Fact]
        public void SameChangeOnBothSides_IsBothSame()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nX\nc\n", "a\nX\nc\n");

            Assert.Equal(new[] { MergeBlockKind.Unchanged, MergeBlockKind.BothSame, MergeBlockKind.Unchanged }, Kinds(result));
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\nX\nc\n", Merged(result));
        }

        [Fact]
        public void OverlappingChanges_AreAConflict()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nS\nc\n", "a\nT\nc\n");

            Assert.Equal(new[] { MergeBlockKind.Unchanged, MergeBlockKind.Conflict, MergeBlockKind.Unchanged }, Kinds(result));
            Assert.Equal(1, result.ConflictCount);

            var conflict = result.Blocks[1];
            Assert.Null(conflict.ResultLines);
            Assert.Equal(new[] { "b\n" }, conflict.BaseLines.ToArray());
            Assert.Equal(new[] { "S\n" }, conflict.SourceLines.ToArray());
            Assert.Equal(new[] { "T\n" }, conflict.TargetLines.ToArray());

            Assert.Equal("a\n<<<<<<< src\nS\n=======\nT\n>>>>>>> tgt\nc\n",
                ThreeWayMerge.BuildTextWithMarkers(result, "src", "tgt"));
        }

        [Fact]
        public void TwoDifferentInsertionsAtTheSamePoint_AreAConflict()
        {
            var result = ThreeWayMerge.Merge("a\nc\n", "a\nX\nc\n", "a\nY\nc\n");

            Assert.Equal(1, result.ConflictCount);
            var conflict = result.Blocks.Single(b => b.Kind == MergeBlockKind.Conflict);
            Assert.Empty(conflict.BaseLines);
            Assert.Equal(new[] { "X\n" }, conflict.SourceLines.ToArray());
            Assert.Equal(new[] { "Y\n" }, conflict.TargetLines.ToArray());
        }

        [Fact]
        public void SameInsertionAtTheSamePoint_IsBothSame()
        {
            var result = ThreeWayMerge.Merge("a\nc\n", "a\nb\nc\n", "a\nb\nc\n");

            Assert.Equal(new[] { MergeBlockKind.Unchanged, MergeBlockKind.BothSame, MergeBlockKind.Unchanged }, Kinds(result));
            Assert.Equal("a\nb\nc\n", Merged(result));
        }

        [Fact]
        public void InsertionsAtDifferentPoints_MergeCleanly()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nX\nb\nc\n", "a\nb\nY\nc\n");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal(new[]
            {
                MergeBlockKind.Unchanged, MergeBlockKind.SourceOnly, MergeBlockKind.Unchanged,
                MergeBlockKind.TargetOnly, MergeBlockKind.Unchanged
            }, Kinds(result));
            Assert.Equal("a\nX\nb\nY\nc\n", Merged(result));
        }

        [Fact]
        public void DeletionVersusModification_IsAConflict()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nB\nc\n", "a\nc\n");

            Assert.Equal(1, result.ConflictCount);
            var conflict = result.Blocks.Single(b => b.Kind == MergeBlockKind.Conflict);
            Assert.Equal(new[] { "b\n" }, conflict.BaseLines.ToArray());
            Assert.Equal(new[] { "B\n" }, conflict.SourceLines.ToArray());
            Assert.Empty(conflict.TargetLines);
            Assert.Equal("a\n<<<<<<< S\nB\n=======\n>>>>>>> T\nc\n",
                ThreeWayMerge.BuildTextWithMarkers(result, "S", "T"));
        }

        [Fact]
        public void DeletionOnOneSideOnly_IsApplied()
        {
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\nc\n", "a\nb\nc\nd\n");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\nc\nd\n", Merged(result));
        }

        [Fact]
        public void ChangesSeparatedByUnchangedLines_AreAlignedByTheDiff()
        {
            // Il source cambia le righe 2 e 6, il target la 4: il diff deve riconoscere 3, 4 e 5 come
            // righe ferme del source (bisezione di Myers), altrimenti tutto il tratto 2-6 confliggerebbe.
            var result = ThreeWayMerge.Merge(
                "1\n2\n3\n4\n5\n6\n7\n8\n9\n",
                "1\nA\n3\n4\n5\nB\n7\n8\n9\n",
                "1\n2\n3\nF\n5\n6\n7\n8\n9\n");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("1\nA\n3\nF\n5\nB\n7\n8\n9\n", Merged(result));
        }

        // ------------------------------------------------------------------------------------------
        // Terminatori di riga
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void CrLfLines_ArePreserved()
        {
            var result = ThreeWayMerge.Merge(
                "a\r\nb\r\nc\r\n",
                "a\r\nB\r\nc\r\n",
                "a\r\nb\r\nc\r\nd\r\n");

            Assert.Equal("\r\n", result.NewLine);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\r\nB\r\nc\r\nd\r\n", Merged(result));
        }

        [Fact]
        public void SourceLines_TakeTheLineEndingOfAConsistentTarget()
        {
            // Il source e' passato a LF e ha cambiato "b": il target usa solo CRLF, quindi anche la riga
            // presa dal source diventa CRLF (niente file con a-capo misti).
            var result = ThreeWayMerge.Merge("a\r\nb\r\nc\r\n", "a\nB\nc\n", "a\r\nb\r\nc\r\n");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\r\nB\r\nc\r\n", Merged(result));
            // I blocchi conservano le righe originali del source.
            Assert.Equal(new[] { "B\n" }, result.Blocks[1].SourceLines.ToArray());
        }

        [Fact]
        public void SourceLines_KeepTheirTerminators_WhenTheTargetIsAlreadyMixed()
        {
            var result = ThreeWayMerge.Merge("a\r\nb\r\nc\r\n", "a\nB\nc\n", "a\r\nb\r\nc\n");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\r\nB\nc\n", Merged(result));
        }

        [Fact]
        public void ConflictSourceLines_TakeTheLineEndingOfAnLfTarget()
        {
            // Target LF (es. uno script .sh), source salvato con CRLF: marker e righe del source in LF.
            var result = ThreeWayMerge.Merge("a\nb\nc\n", "a\r\nS1\r\nS2\r\nc\r\n", "a\nT\nc\n");

            var text = ThreeWayMerge.BuildTextWithMarkers(result, "S", "T");

            Assert.Equal("a\n<<<<<<< S\nS1\nS2\n=======\nT\n>>>>>>> T\nc\n", text);
            var region = Assert.Single(ThreeWayMerge.FindConflictRegions(text, result.MarkerSize));
            Assert.Equal("a\nS1\nS2\nT\nc\n", ThreeWayMerge.ResolveRegion(text, region, ConflictChoice.TakeBoth));
        }

        [Fact]
        public void OnlyTerminatorsDiffer_BetweenTheTwoChanges_IsBothSame()
        {
            var result = ThreeWayMerge.Merge("a\nold\nc\n", "a\nnew\nc\n", "a\nnew\r\nc\n");

            Assert.Equal(new[] { MergeBlockKind.Unchanged, MergeBlockKind.BothSame, MergeBlockKind.Unchanged }, Kinds(result));
            Assert.Equal(new[] { "new\r\n" }, result.Blocks[1].ResultLines.ToArray());
            Assert.Equal(0, result.ConflictCount);
        }

        [Fact]
        public void OnlyTerminatorsDiffer_FromTheBase_IsUnchanged()
        {
            var result = ThreeWayMerge.Merge("a\nb\n", "a\r\nb\r\n", "a\nb\n");

            var block = Assert.Single(result.Blocks);
            Assert.Equal(MergeBlockKind.Unchanged, block.Kind);
            Assert.Equal("a\nb\n", Merged(result));
        }

        [Fact]
        public void LastLineWithoutNewLine_AtTheEnd_StaysWithoutNewLine()
        {
            var result = ThreeWayMerge.Merge("a\nb", "a\nB", "a\nb");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("a\nB", Merged(result));
        }

        [Fact]
        public void LastLineWithoutNewLine_InTheMiddle_GetsANewLine()
        {
            // "b" senza a-capo e' l'ultima riga del target, ma il source aggiunge "c" dopo di lei.
            var result = ThreeWayMerge.Merge("a\nb", "a\nb\nc", "a\nb");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("b", result.Blocks[0].ResultLines[1]);
            Assert.Equal("a\nb\nc", Merged(result));
        }

        [Fact]
        public void Markers_NeverGlueUnterminatedLines()
        {
            var result = ThreeWayMerge.Merge("a\nb", "a\nc", "a\nd");

            var text = ThreeWayMerge.BuildTextWithMarkers(result, "S", "T");

            Assert.Equal("a\n<<<<<<< S\nc\n=======\nd\n>>>>>>> T\n", text);
            var region = Assert.Single(ThreeWayMerge.FindConflictRegions(text));
            Assert.Equal("c\n", region.SourceText);
            Assert.Equal("d\n", region.TargetText);
        }

        [Fact]
        public void NewLine_IsTheTargetOneThenSourceThenBase()
        {
            Assert.Equal("\r\n", ThreeWayMerge.Merge("a\n", "a\n", "a\r\nb\r\nc\n").NewLine);
            Assert.Equal("\n", ThreeWayMerge.Merge("", "x\n", "y").NewLine);
            Assert.Equal("\r", ThreeWayMerge.Merge("a\r", "x", "y").NewLine);
            Assert.Equal("\r\n", ThreeWayMerge.Merge("a", "b", "c").NewLine);
        }

        // ------------------------------------------------------------------------------------------
        // File vuoti e add/add
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void EmptyBase_SameAdditionOnBothSides_IsBothSame()
        {
            var result = ThreeWayMerge.Merge("", "x\ny\n", "x\ny\n");

            var block = Assert.Single(result.Blocks);
            Assert.Equal(MergeBlockKind.BothSame, block.Kind);
            Assert.Empty(block.BaseLines);
            Assert.Equal("x\ny\n", Merged(result));
        }

        [Fact]
        public void EmptyBase_AdditionOnOneSideOnly_IsTaken()
        {
            var result = ThreeWayMerge.Merge(null, "x\n", null);

            var block = Assert.Single(result.Blocks);
            Assert.Equal(MergeBlockKind.SourceOnly, block.Kind);
            Assert.Equal("x\n", Merged(result));
        }

        [Fact]
        public void EmptyBase_DifferentAdditions_KeepCommonLinesOutOfTheConflict()
        {
            var result = ThreeWayMerge.Merge("", "x\ny\nz\n", "x\nw\nz\n");

            Assert.Equal(new[] { MergeBlockKind.BothSame, MergeBlockKind.Conflict, MergeBlockKind.BothSame }, Kinds(result));
            Assert.Equal(1, result.ConflictCount);
            Assert.Equal("x\n<<<<<<< S\ny\n=======\nw\n>>>>>>> T\nz\n",
                ThreeWayMerge.BuildTextWithMarkers(result, "S", "T"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void AllFilesEmpty_ReturnsNoBlocks(string text)
        {
            var result = ThreeWayMerge.Merge(text, text, text);

            Assert.Empty(result.Blocks);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal("\r\n", result.NewLine);
            Assert.Equal(string.Empty, Merged(result));
        }

        // ------------------------------------------------------------------------------------------
        // Marker: composizione, ricerca, risoluzione
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void BuildThenFind_RoundTripsEveryConflict()
        {
            var result = ThreeWayMerge.Merge(
                "a\r\nb\r\nc\r\nd\r\ne\r\n",
                "a\r\nB1\r\nc\r\nD1\r\ne\r\n",
                "a\r\nB2\r\nc\r\nD2\r\ne\r\n");

            var text = ThreeWayMerge.BuildTextWithMarkers(result, "Source", "Target");
            var regions = ThreeWayMerge.FindConflictRegions(text);

            Assert.Equal(2, result.ConflictCount);
            Assert.Equal(result.ConflictCount, regions.Count);
            Assert.True(ThreeWayMerge.HasConflictMarkers(text));

            Assert.Equal(0, regions[0].Index);
            Assert.Equal(1, regions[0].StartLine);
            Assert.Equal("a\r\n".Length, regions[0].Start);
            Assert.Equal("<<<<<<< Source\r\nB1\r\n=======\r\nB2\r\n>>>>>>> Target\r\n",
                text.Substring(regions[0].Start, regions[0].Length));
            Assert.Equal("B1\r\n", regions[0].SourceText);
            Assert.Equal("B2\r\n", regions[0].TargetText);

            Assert.Equal(1, regions[1].Index);
            Assert.Equal(7, regions[1].StartLine);
            Assert.Equal("D1\r\n", regions[1].SourceText);
            Assert.Equal("D2\r\n", regions[1].TargetText);
        }

        [Theory]
        [InlineData(ConflictChoice.TakeSource, "a\nX\nc\n")]
        [InlineData(ConflictChoice.TakeTarget, "a\nY\nc\n")]
        [InlineData(ConflictChoice.TakeBoth, "a\nX\nY\nc\n")]
        public void ResolveRegion_ReplacesTheWholeRegion(ConflictChoice choice, string expected)
        {
            const string text = "a\n<<<<<<< S\nX\n=======\nY\n>>>>>>> T\nc\n";
            var region = Assert.Single(ThreeWayMerge.FindConflictRegions(text));

            var resolved = ThreeWayMerge.ResolveRegion(text, region, choice);

            Assert.Equal(expected, resolved);
            Assert.False(ThreeWayMerge.HasConflictMarkers(resolved));
        }

        [Fact]
        public void ResolveRegion_OneAtATime_LeavesNoMarkers()
        {
            var result = ThreeWayMerge.Merge(
                "a\r\nb\r\nc\r\nd\r\ne\r\n",
                "a\r\nB1\r\nc\r\nD1\r\ne\r\n",
                "a\r\nB2\r\nc\r\nD2\r\ne\r\n");
            var text = ThreeWayMerge.BuildTextWithMarkers(result, "Source", "Target");

            text = ThreeWayMerge.ResolveRegion(text, ThreeWayMerge.FindConflictRegions(text)[0], ConflictChoice.TakeTarget);
            var remaining = Assert.Single(ThreeWayMerge.FindConflictRegions(text));
            Assert.Equal(0, remaining.Index);
            Assert.Equal("D1\r\n", remaining.SourceText);

            text = ThreeWayMerge.ResolveRegion(text, remaining, ConflictChoice.TakeSource);

            Assert.Equal("a\r\nB2\r\nc\r\nD1\r\ne\r\n", text);
            Assert.False(ThreeWayMerge.HasConflictMarkers(text));
        }

        [Fact]
        public void ResolveRegion_NeverGluesLines()
        {
            // Regione costruita a mano con lati senza a-capo finale: il risultato non deve incollare
            // "X" a "Y" ne' "Y" alla riga che segue.
            const string markers = "<<<<<<< S\nX\n=======\nY\n>>>>>>> T\n";
            const string text = markers + "rest\n";
            var region = new ConflictRegion(0, 0, markers.Length, 0, "X", "Y");

            Assert.Equal("X\nY\nrest\n", ThreeWayMerge.ResolveRegion(text, region, ConflictChoice.TakeBoth));
            Assert.Equal("Y\nrest\n", ThreeWayMerge.ResolveRegion(text, region, ConflictChoice.TakeTarget));
        }

        [Fact]
        public void ResolveRegion_RegionFromAnotherText_Throws()
        {
            var region = new ConflictRegion(0, 0, 10, 0, "a\n", "b\n");

            Assert.Throws<ArgumentException>(() =>
                ThreeWayMerge.ResolveRegion("no markers in this text\n", region, ConflictChoice.TakeSource));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<<<<<<< S\nX\n>>>>>>> T\n")]                    // manca il separatore
        [InlineData("<<<<<<< S\nX\n=======\nY\n")]                    // mai chiusa
        [InlineData("X\n=======\nY\n>>>>>>> T\n")]                    // manca l'apertura
        [InlineData("<<<<<<<S\nX\n=======\nY\n>>>>>>> T\n")]          // apertura senza spazio
        [InlineData("<<<<<<< S\nX\n======= \nY\n>>>>>>> T\n")]        // separatore non esatto
        [InlineData("<<<<<<< S\nX\n=======\nY\n>>>>>>>T\n")]          // chiusura senza spazio
        [InlineData(">>>>>>> T\n=======\n<<<<<<< S\n")]               // ordine rovesciato
        public void MalformedMarkers_AreIgnored(string text)
        {
            Assert.Empty(ThreeWayMerge.FindConflictRegions(text));
            Assert.False(ThreeWayMerge.HasConflictMarkers(text));
        }

        [Fact]
        public void UnclosedRegionFollowedByAValidOne_OnlyTheValidOneIsFound()
        {
            const string text = "<<<<<<< A\nX\n<<<<<<< S\nP\n=======\nQ\n=======\nR\n>>>>>>> T";

            var region = Assert.Single(ThreeWayMerge.FindConflictRegions(text));

            Assert.Equal(text.IndexOf("<<<<<<< S", StringComparison.Ordinal), region.Start);
            Assert.Equal(2, region.StartLine);
            Assert.Equal(text.Length - region.Start, region.Length); // ultima riga senza a-capo
            Assert.Equal("P\n", region.SourceText);
            Assert.Equal("Q\n=======\nR\n", region.TargetText); // il secondo separatore e' testo del target
        }

        [Fact]
        public void SeparatorLineInsideAConflict_IsKeptAsText()
        {
            // Titolo Markdown/RST con una sottolineatura di 7 "=" dentro il blocco in conflitto: i marker
            // si allungano, e ogni scelta prende esattamente le righe del suo lato.
            var result = ThreeWayMerge.Merge("Intro\nold\n", "Intro\nExample\n=======\nnew body\n", "Intro\nchanged here\n");

            Assert.Equal(8, result.MarkerSize);
            var text = ThreeWayMerge.BuildTextWithMarkers(result, "S", "T");
            Assert.Equal("Intro\n<<<<<<<< S\nExample\n=======\nnew body\n========\nchanged here\n>>>>>>>> T\n", text);

            var region = Assert.Single(ThreeWayMerge.FindConflictRegions(text, result.MarkerSize));
            Assert.Equal("Example\n=======\nnew body\n", region.SourceText);
            Assert.Equal("changed here\n", region.TargetText);

            Assert.Equal("Intro\nExample\n=======\nnew body\n", ThreeWayMerge.ResolveRegion(text, region, ConflictChoice.TakeSource));
            Assert.Equal("Intro\nchanged here\n", ThreeWayMerge.ResolveRegion(text, region, ConflictChoice.TakeTarget));
            Assert.Equal("Intro\nExample\n=======\nnew body\nchanged here\n",
                ThreeWayMerge.ResolveRegion(text, region, ConflictChoice.TakeBoth));
        }

        [Fact]
        public void MarkerLinesInUnchangedText_AreNotConflicts()
        {
            // Un documento che spiega i marker di git (testo invariato) e una modifica pulita altrove.
            const string doc = "How to read a conflict:\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> branch\nend\n";
            var result = ThreeWayMerge.Merge(doc + "x\n", doc + "X\n", doc + "x\n");

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal(8, result.MarkerSize);
            var text = Merged(result);
            Assert.Equal(doc + "X\n", text);
            Assert.Empty(ThreeWayMerge.FindConflictRegions(text, result.MarkerSize));
        }

        [Fact]
        public void MarkerSize_SkipsEveryLengthAlreadyUsedByTheText()
        {
            var result = ThreeWayMerge.Merge("a\n", "=======\n<<<<<<<< x\n>>>>>>>>> y\n", "b\n");

            Assert.Equal(10, result.MarkerSize);
            var text = Merged(result);
            var region = Assert.Single(ThreeWayMerge.FindConflictRegions(text, result.MarkerSize));
            Assert.Equal("=======\n<<<<<<<< x\n>>>>>>>>> y\n", region.SourceText);
            Assert.Equal("b\n", region.TargetText);
        }

        // ------------------------------------------------------------------------------------------
        // Prestazioni e degrado
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void MassiveEditsOnOneSide_DoNotHideASmallEditOnTheOther()
        {
            // Il source cambia una riga ogni cinque (distanza di modifica ben oltre la soglia, es. una
            // riformattazione), il target cambia una sola riga in mezzo: la bisezione rinuncia, ma le
            // righe uniche rimaste uguali fanno da ancore e la fusione e' pulita.
            const int count = 30000;
            var baseLines = Enumerable.Range(0, count).Select(i => Line("line", i)).ToArray();
            var sourceLines = (string[])baseLines.Clone();
            for (var i = 0; i < count; i += 5)
                sourceLines[i] = Line("source", i);
            var targetLines = (string[])baseLines.Clone();
            targetLines[15002] = Line("target", 15002);
            var expectedLines = (string[])sourceLines.Clone();
            expectedLines[15002] = targetLines[15002];

            var stopwatch = Stopwatch.StartNew();
            var result = ThreeWayMerge.Merge(Join(baseLines, "\n"), Join(sourceLines, "\n"), Join(targetLines, "\n"));
            stopwatch.Stop();

            Assert.Equal(0, result.ConflictCount);
            Assert.False(result.IsApproximate);
            Assert.Equal(Join(expectedLines, "\n"), Merged(result));
            Assert.True(stopwatch.ElapsedMilliseconds < 5000,
                string.Format(CultureInfo.InvariantCulture, "Merge took {0} ms", stopwatch.ElapsedMilliseconds));
        }

        [Fact]
        public void DegradedDiffWithCommonLines_IsReportedAsApproximate()
        {
            // Solo righe ripetute in comune (nessuna ancora unica) e oltre 10.000 inserimenti.
            var baseLines = Enumerable.Repeat("same", 11000).ToArray();
            var sourceLines = Enumerable.Range(0, 11000).SelectMany(i => new[] { "same", Line("new", i) }).ToArray();

            var result = ThreeWayMerge.Merge(Join(baseLines, "\n"), Join(sourceLines, "\n"), Join(baseLines, "\n"));

            Assert.True(result.IsApproximate);
            Assert.Equal(0, result.ConflictCount);
            Assert.Equal(Join(sourceLines, "\n"), Merged(result));
        }

        [Fact]
        public void OrdinaryMerges_AreNotApproximate()
        {
            Assert.False(ThreeWayMerge.Merge("a\nb\nc\n", "a\nS\nc\n", "a\nT\nc\n").IsApproximate);
            Assert.False(ThreeWayMerge.Merge("a\nb\n", "x\ny\n", "a\nb\n").IsApproximate);
        }

        [Fact]
        public void LargeFileWithFewSparseChanges_IsFastAndClean()
        {
            const int count = 20000;
            var baseLines = Enumerable.Range(0, count).Select(i => Line("line", i)).ToArray();
            var sourceLines = (string[])baseLines.Clone();
            var targetLines = (string[])baseLines.Clone();
            var expectedLines = (string[])baseLines.Clone();
            foreach (var i in new[] { 1000, 5000, 9000, 13000, 17000 })
            {
                sourceLines[i] = Line("source", i);
                expectedLines[i] = sourceLines[i];
            }
            foreach (var i in new[] { 3000, 7000, 11000, 15000, 19000 })
            {
                targetLines[i] = Line("target", i);
                expectedLines[i] = targetLines[i];
            }

            var stopwatch = Stopwatch.StartNew();
            var result = ThreeWayMerge.Merge(Join(baseLines, "\r\n"), Join(sourceLines, "\r\n"), Join(targetLines, "\r\n"));
            var merged = ThreeWayMerge.BuildTextWithMarkers(result, "source", "target");
            stopwatch.Stop();

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal(Join(expectedLines, "\r\n"), merged);
            Assert.True(stopwatch.ElapsedMilliseconds < 2000,
                string.Format(CultureInfo.InvariantCulture, "Merge took {0} ms", stopwatch.ElapsedMilliseconds));
        }

        [Fact]
        public void CompletelyDifferentLargeFiles_DegradeQuicklyToOneConflict()
        {
            const int count = 20000;
            var baseText = Join(Enumerable.Range(0, count).Select(i => Line("base", i)), "\n");
            var sourceText = Join(Enumerable.Range(0, count).Select(i => Line("source", i)), "\n");
            var targetText = Join(Enumerable.Range(0, count).Select(i => Line("target", i)), "\n");

            var stopwatch = Stopwatch.StartNew();
            var result = ThreeWayMerge.Merge(baseText, sourceText, targetText);
            stopwatch.Stop();

            var block = Assert.Single(result.Blocks);
            Assert.Equal(MergeBlockKind.Conflict, block.Kind);
            Assert.Equal(count, block.SourceLines.Count);
            Assert.True(stopwatch.ElapsedMilliseconds < 2000,
                string.Format(CultureInfo.InvariantCulture, "Merge took {0} ms", stopwatch.ElapsedMilliseconds));
        }

        [Fact]
        public void HugeInsertionBeyondTheThreshold_StillMergesCleanlyElsewhere()
        {
            // Il source cambia la riga 10 e inserisce 12.000 righe dopo la 499 (distanza oltre la soglia:
            // il diff degrada su quel tratto); il target cambia la riga 900, fuori dal tratto degradato.
            var baseLines = Enumerable.Range(0, 1000).Select(i => Line("line", i)).ToList();
            var sourceLines = new List<string>(baseLines);
            sourceLines[10] = Line("source", 10);
            sourceLines.InsertRange(500, Enumerable.Range(0, 12000).Select(i => Line("inserted", i)));
            var targetLines = new List<string>(baseLines);
            targetLines[900] = Line("target", 900);
            var expectedLines = new List<string>(sourceLines);
            expectedLines[900 + 12000] = Line("target", 900);

            var stopwatch = Stopwatch.StartNew();
            var result = ThreeWayMerge.Merge(Join(baseLines, "\n"), Join(sourceLines, "\n"), Join(targetLines, "\n"));
            stopwatch.Stop();

            Assert.Equal(0, result.ConflictCount);
            Assert.Equal(Join(expectedLines, "\n"), Merged(result));
            Assert.True(stopwatch.ElapsedMilliseconds < 2000,
                string.Format(CultureInfo.InvariantCulture, "Merge took {0} ms", stopwatch.ElapsedMilliseconds));
        }

        // ------------------------------------------------------------------------------------------
        // Supporto
        // ------------------------------------------------------------------------------------------

        private static string Merged(ThreeWayMergeResult result)
        {
            return ThreeWayMerge.BuildTextWithMarkers(result, "source", "target");
        }

        private static MergeBlockKind[] Kinds(ThreeWayMergeResult result)
        {
            return result.Blocks.Select(b => b.Kind).ToArray();
        }

        private static string Line(string prefix, int index)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", prefix, index);
        }

        // Ogni riga seguita dal terminatore (anche l'ultima).
        private static string Join(IEnumerable<string> lines, string newLine)
        {
            return string.Concat(lines.Select(l => l + newLine));
        }
    }
}
