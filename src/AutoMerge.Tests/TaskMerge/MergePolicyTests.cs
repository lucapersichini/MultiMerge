using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AutoMerge;
using Xunit;

namespace AutoMerge.Tests.TaskMerge
{
    // Regole di merge (policy): glob, decisione per path, regole effettive team/personale, JSON,
    // merge con righe protette, confronto delle righe protette, controllo delle azioni dei passi e
    // modelli pronti. Prefissi di esempio: "Contoso." e "Fabrikam.".
    public class MergePolicyTests
    {
        private const string SourceRoot = "$/Src";
        private const string TargetRoot = "$/Tgt";

        private const TaskChangeKind AddNew = TaskChangeKind.Add | TaskChangeKind.Edit | TaskChangeKind.Encoding;
        private const TaskChangeKind AddFolder = TaskChangeKind.Add | TaskChangeKind.Encoding;

        private static readonly string[] Prefixes = { "Contoso.", "Fabrikam." };

        // ------------------------------------------------------------------------------------------
        // GlobMatch
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Glob_DoubleStar_MatchesZeroOrMoreFolders()
        {
            Assert.True(MergePolicyEngine.GlobMatch("**/Directory.Packages.props", "Directory.Packages.props"));
            Assert.True(MergePolicyEngine.GlobMatch("**/Directory.Packages.props", "src/Directory.Packages.props"));
            Assert.True(MergePolicyEngine.GlobMatch("**/Directory.Packages.props", "a/b/c/Directory.Packages.props"));
            Assert.True(MergePolicyEngine.GlobMatch("src/**/test/*.cs", "src/test/a.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("src/**/test/*.cs", "src/x/y/test/a.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("**/Directory.Packages.props", "src/Directory.Packages.props.bak"));
            Assert.False(MergePolicyEngine.GlobMatch("src/**/test/*.cs", "other/test/a.cs"));
        }

        [Fact]
        public void Glob_Star_DoesNotCrossFolders()
        {
            Assert.True(MergePolicyEngine.GlobMatch("src/*.cs", "src/Program.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("src/*.cs", "src/Sub/Program.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("src/*.cs", "lib/src/Program.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("src/*", "src/any.thing"));
        }

        [Fact]
        public void Glob_QuestionMark_IsExactlyOneCharacter()
        {
            Assert.True(MergePolicyEngine.GlobMatch("file?.txt", "file1.txt"));
            Assert.False(MergePolicyEngine.GlobMatch("file?.txt", "file10.txt"));
            Assert.False(MergePolicyEngine.GlobMatch("file?.txt", "file.txt"));
            Assert.False(MergePolicyEngine.GlobMatch("a?b", "a/b"));
        }

        [Fact]
        public void Glob_NameWithoutFolder_MatchesInAnyFolder()
        {
            Assert.True(MergePolicyEngine.GlobMatch("packages.config", "packages.config"));
            Assert.True(MergePolicyEngine.GlobMatch("packages.config", "Web/packages.config"));
            Assert.True(MergePolicyEngine.GlobMatch("packages.config", "a/b/c/packages.config"));
            Assert.True(MergePolicyEngine.GlobMatch("*.csproj", "src/App/App.csproj"));
            Assert.False(MergePolicyEngine.GlobMatch("packages.config", "packages.config.old"));
            Assert.False(MergePolicyEngine.GlobMatch("packages.config", "Web/my-packages.config"));
        }

        [Fact]
        public void Glob_IsCaseInsensitive()
        {
            Assert.True(MergePolicyEngine.GlobMatch("*.CSPROJ", "src/App.csproj"));
            Assert.True(MergePolicyEngine.GlobMatch("Web/Web.config", "WEB/web.CONFIG"));
            Assert.True(MergePolicyEngine.GlobMatch("**/directory.packages.props", "Src/Directory.Packages.Props"));
        }

        [Fact]
        public void Glob_BackslashesDotsAndLeadingSlashes_AreNormalized()
        {
            Assert.True(MergePolicyEngine.GlobMatch(@"src\*.cs", "src/a.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("src/*.cs", @"src\a.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("src/*.cs", "/src/a.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("src/*.cs", "./src/a.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("src/*.cs", "src//a.cs"));
        }

        [Fact]
        public void Glob_LeadingSlash_AnchorsAtTheBranchRoot()
        {
            Assert.True(MergePolicyEngine.GlobMatch("/packages.config", "packages.config"));
            Assert.False(MergePolicyEngine.GlobMatch("/packages.config", "Web/packages.config"));
            Assert.True(MergePolicyEngine.GlobMatch("./packages.config", "packages.config"));
            Assert.False(MergePolicyEngine.GlobMatch("./packages.config", "Web/packages.config"));
        }

        [Fact]
        public void Glob_FolderPatterns_IncludeTheFolderAndItsContent()
        {
            // "x/" = la cartella x (in qualsiasi punto) e tutto il suo contenuto
            Assert.True(MergePolicyEngine.GlobMatch("bin/", "bin"));
            Assert.True(MergePolicyEngine.GlobMatch("bin/", "bin/a.dll"));
            Assert.True(MergePolicyEngine.GlobMatch("bin/", "src/App/bin/Debug/a.dll"));
            Assert.False(MergePolicyEngine.GlobMatch("bin/", "binaries/a.dll"));

            // "x/**" = la cartella x della radice, lei compresa
            Assert.True(MergePolicyEngine.GlobMatch("Legacy/**", "Legacy"));
            Assert.True(MergePolicyEngine.GlobMatch("Legacy/**", "Legacy/a/b.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("Legacy/**", "LegacyX/a.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("Legacy/**", "src/Legacy/a.cs"));
        }

        [Fact]
        public void Glob_SeveralPatternsSeparatedBySemicolons()
        {
            Assert.True(MergePolicyEngine.GlobMatch("*.csproj;*.vbproj", "a/App.vbproj"));
            Assert.True(MergePolicyEngine.GlobMatch(" *.csproj ; *.vbproj ", "App.csproj"));
            Assert.False(MergePolicyEngine.GlobMatch("*.csproj;*.vbproj", "App.fsproj"));
            Assert.False(MergePolicyEngine.GlobMatch(";;", "App.csproj"));
        }

        [Fact]
        public void Glob_EmptyPatternAndBranchRoot()
        {
            Assert.False(MergePolicyEngine.GlobMatch(null, "a.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("  ", "a.cs"));
            Assert.True(MergePolicyEngine.GlobMatch("**", "."));
            Assert.True(MergePolicyEngine.GlobMatch("**", "a/b/c.cs"));
            Assert.False(MergePolicyEngine.GlobMatch("*", "."));
            Assert.False(MergePolicyEngine.GlobMatch("*.cs", string.Empty));
        }

        // ------------------------------------------------------------------------------------------
        // Decide e regole effettive
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Decide_WithoutRules_IsMergeWithoutMatchedRule()
        {
            var decision = MergePolicyEngine.Decide(new EffectiveMergePolicy(null, null), "src/a.cs");
            var noPolicy = MergePolicyEngine.Decide(null, "src/a.cs");

            Assert.Equal(MergePolicyAction.Merge, decision.Action);
            Assert.Null(decision.MatchedRule);
            Assert.Empty(decision.LineRules);
            Assert.Equal(MergePolicyAction.Merge, noPolicy.Action);
            Assert.Null(noPolicy.MatchedRule);
        }

        [Fact]
        public void Decide_FirstMatchingRuleWins()
        {
            var team = Doc(
                PathRule("generated", "src/Generated/**", MergePolicyAction.Skip),
                PathRule("all-src", "src/**", MergePolicyAction.Discard));
            var policy = new EffectiveMergePolicy(team, null);

            var generated = MergePolicyEngine.Decide(policy, "src/Generated/Model.cs");
            var other = MergePolicyEngine.Decide(policy, "src/Program.cs");
            var outside = MergePolicyEngine.Decide(policy, "docs/readme.md");

            Assert.Equal(MergePolicyAction.Skip, generated.Action);
            Assert.Equal("generated", generated.MatchedRule.Rule.Id);
            Assert.Equal(MergePolicyAction.Discard, other.Action);
            Assert.Equal("all-src", other.MatchedRule.Rule.Id);
            Assert.Equal(MergeRuleOrigin.Team, other.MatchedRule.Origin);
            Assert.Equal(MergePolicyAction.Merge, outside.Action);
            Assert.Null(outside.MatchedRule);
        }

        [Fact]
        public void Decide_DisabledRule_IsIgnored()
        {
            var disabled = PathRule("skip-all", "**", MergePolicyAction.Skip);
            disabled.Enabled = false;
            var policy = new EffectiveMergePolicy(Doc(disabled, PathRule("discard-props", "*.props", MergePolicyAction.Discard)), null);

            Assert.Equal(MergePolicyAction.Merge, MergePolicyEngine.Decide(policy, "a.cs").Action);
            Assert.Equal(MergePolicyAction.Discard, MergePolicyEngine.Decide(policy, "Directory.Build.props").Action);
            // le regole disattivate restano nell'elenco (per mostrarle)
            Assert.Equal(2, policy.PathRules.Count);
        }

        [Fact]
        public void Decide_PersonalRulesComeBeforeTeamRules()
        {
            var team = Doc(PathRule("team-props", "*.props", MergePolicyAction.Discard));
            var personal = Doc(PathRule("my-props", "Directory.Build.props", MergePolicyAction.Skip));
            var policy = new EffectiveMergePolicy(team, personal);

            var decision = MergePolicyEngine.Decide(policy, "Directory.Build.props");

            Assert.Equal(new[] { "my-props", "team-props" }, policy.PathRules.Select(r => r.Rule.Id).ToArray());
            Assert.Equal(MergePolicyAction.Skip, decision.Action);
            Assert.Equal(MergeRuleOrigin.Personal, decision.MatchedRule.Origin);
            Assert.False(decision.MatchedRule.OverridesTeamRule);
            Assert.Equal(MergePolicyAction.Discard, MergePolicyEngine.Decide(policy, "Other.props").Action);
        }

        [Fact]
        public void Decide_PersonalRuleWithTheSameId_ReplacesTheTeamRule()
        {
            var team = Doc(
                PathRule("props", "*.props", MergePolicyAction.Discard),
                PathRule("docs", "docs/**", MergePolicyAction.Skip));
            var personal = Doc(PathRule("PROPS", "*.props", MergePolicyAction.Skip));
            var policy = new EffectiveMergePolicy(team, personal);

            Assert.Equal(new[] { "PROPS", "docs" }, policy.PathRules.Select(r => r.Rule.Id).ToArray());
            var replaced = policy.PathRules[0];
            Assert.Equal(MergeRuleOrigin.Personal, replaced.Origin);
            Assert.True(replaced.OverridesTeamRule);
            Assert.Equal(MergePolicyAction.Skip, MergePolicyEngine.Decide(policy, "a.props").Action);
            Assert.Empty(policy.Errors);
        }

        [Fact]
        public void Decide_DisabledPersonalRuleWithTheSameId_TurnsOffTheTeamRule()
        {
            var team = Doc(PathRule("props", "*.props", MergePolicyAction.Discard));
            var off = PathRule("props", "*.props", MergePolicyAction.Discard);
            off.Enabled = false;
            var policy = new EffectiveMergePolicy(team, Doc(off));

            var decision = MergePolicyEngine.Decide(policy, "Directory.Build.props");

            Assert.Equal(MergePolicyAction.Merge, decision.Action);
            Assert.Null(decision.MatchedRule);
            Assert.True(Assert.Single(policy.PathRules).OverridesTeamRule);
        }

        [Fact]
        public void Decide_LineRules_OnlyForMatchingFiles_AndOnlyWhenMerging()
        {
            var doc = MergePolicyEngine.NuGetPreset(Prefixes);
            doc.PathRules.Add(PathRule("skip-legacy", "Legacy/**", MergePolicyAction.Skip));
            var policy = new EffectiveMergePolicy(doc, null);

            var packages = MergePolicyEngine.Decide(policy, "Web/packages.config");
            var project = MergePolicyEngine.Decide(policy, "Web/Web.csproj");
            var code = MergePolicyEngine.Decide(policy, "Web/Program.cs");
            var skipped = MergePolicyEngine.Decide(policy, "Legacy/packages.config");

            // packages.config e' anche un *.config: vale pure la regola dei binding redirect
            Assert.Equal(new[] { "preset.nuget.packages-config", "preset.nuget.packages-config-multiline", "preset.nuget.binding-redirect" },
                packages.LineRules.Select(r => r.Rule.Id).ToArray());
            Assert.Contains(project.LineRules, r => r.Rule.Id == "preset.nuget.reference");
            Assert.Contains(project.LineRules, r => r.Rule.Id == "preset.nuget.package-reference-line");
            Assert.DoesNotContain(project.LineRules, r => r.Rule.Id == "preset.nuget.packages-config");
            Assert.Empty(code.LineRules);
            Assert.Equal(MergePolicyAction.Skip, skipped.Action);
            Assert.Empty(skipped.LineRules);
        }

        [Fact]
        public void Effective_LineRules_UseTheSameReplacementById()
        {
            var team = Doc();
            team.LineRules.Add(LineRule("ver", "*.txt", "^version="));
            team.LineRules.Add(LineRule("other", "*.txt", "^other="));
            var personal = Doc();
            var mine = LineRule("VER", "*.txt", "^version=");
            mine.Enabled = false;
            personal.LineRules.Add(mine);

            var policy = new EffectiveMergePolicy(team, personal);
            var decision = MergePolicyEngine.Decide(policy, "notes.txt");

            Assert.Equal(new[] { "VER", "other" }, policy.LineRules.Select(r => r.Rule.Id).ToArray());
            Assert.True(policy.LineRules[0].OverridesTeamRule);
            Assert.Equal(new[] { "other" }, decision.LineRules.Select(r => r.Rule.Id).ToArray());
        }

        // ------------------------------------------------------------------------------------------
        // Errors
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Errors_InvalidRegex_IsReported_AndTheEngineRefusesTheRule()
        {
            var team = Doc();
            team.LineRules.Add(LineRule("broken", "*.txt", "version=(unclosed"));

            var policy = new EffectiveMergePolicy(team, null);

            var error = Assert.Single(policy.Errors);
            Assert.Contains("broken", error);
            Assert.Contains("not a valid regular expression", error);
            Assert.Throws<ArgumentException>(() => MergePolicyEngine.MergeWithProtectedLines("a\n", "b\n", "a\n", team.LineRules));
            Assert.Throws<ArgumentException>(() => MergePolicyEngine.CompareProtectedLines("a\n", "b\n", team.LineRules));
        }

        [Fact]
        public void Errors_DuplicateIdsInTheSameDocument_AreReported()
        {
            var team = Doc(
                PathRule("dup", "a/**", MergePolicyAction.Skip),
                PathRule("DUP", "b/**", MergePolicyAction.Skip),
                PathRule("dup", "c/**", MergePolicyAction.Skip));
            team.LineRules.Add(LineRule("line", "*.txt", "^x"));
            team.LineRules.Add(LineRule("line", "*.txt", "^y"));

            var policy = new EffectiveMergePolicy(team, null);

            Assert.Equal(2, policy.Errors.Count);
            Assert.Contains(policy.Errors, e => e.Contains("more than one path rule") && e.IndexOf("'dup'", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(policy.Errors, e => e.Contains("more than one line rule") && e.Contains("'line'"));
        }

        [Fact]
        public void Errors_EmptyPatternsMissingIdsAndIncompleteBlocks_AreReported()
        {
            var personal = Doc(PathRule("empty", "  ", MergePolicyAction.Skip), PathRule(null, "*.cs", MergePolicyAction.Skip));
            personal.LineRules.Add(new MergeLineRule { Id = "nothing", FilePattern = "*.txt" });
            personal.LineRules.Add(new MergeLineRule { Id = "half-block", FilePattern = "*.txt", BlockStartPattern = "<a>" });
            personal.LineRules.Add(new MergeLineRule { Id = "no-file", LinePattern = "x" });
            personal.PathRules.Add(PathRule("bad-action", "*.x", (MergePolicyAction)42));

            var errors = new EffectiveMergePolicy(null, personal).Errors;

            Assert.Contains(errors, e => e.Contains("'empty'") && e.Contains("pattern is empty"));
            Assert.Contains(errors, e => e.Contains("without an Id"));
            Assert.Contains(errors, e => e.Contains("'nothing'") && e.Contains("neither a line pattern nor a block pattern"));
            Assert.Contains(errors, e => e.Contains("'half-block'") && e.Contains("both a start pattern and an end pattern"));
            Assert.Contains(errors, e => e.Contains("'no-file'") && e.Contains("file pattern is empty"));
            Assert.Contains(errors, e => e.Contains("'bad-action'") && e.Contains("not valid"));
        }

        [Fact]
        public void Errors_BrokenTeamRuleReplacedByAPersonalRule_IsNotAnError()
        {
            var team = Doc();
            team.LineRules.Add(LineRule("ver", "*.txt", "version=(unclosed"));
            var personal = Doc();
            personal.LineRules.Add(LineRule("ver", "*.txt", "^version="));

            Assert.Single(new EffectiveMergePolicy(team, null).Errors);
            Assert.Empty(new EffectiveMergePolicy(team, personal).Errors);
        }

        [Fact]
        public void Errors_DocumentFromANewerVersion_IsReported()
        {
            var team = Doc();
            team.Version = 2;

            var error = Assert.Single(new EffectiveMergePolicy(team, null).Errors);

            Assert.Contains("version 2", error);
        }

        [Fact]
        public void Errors_ThePresetsTogether_AreValid()
        {
            var team = MergePolicyEngine.NuGetPreset(Prefixes);
            team.PathRules.AddRange(MergePolicyEngine.CentralPackageManagementPreset().PathRules);
            team.LineRules.AddRange(MergePolicyEngine.AssemblyVersionPreset().LineRules);

            var policy = new EffectiveMergePolicy(team, null);

            Assert.Empty(policy.Errors);
            Assert.All(policy.LineRules, r => Assert.StartsWith("preset.", r.Rule.Id));
            Assert.All(policy.LineRules, r => Assert.True(r.Rule.Enabled));
        }

        // ------------------------------------------------------------------------------------------
        // JSON
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Json_RoundTrip_KeepsEveryField()
        {
            var doc = Doc(PathRule("cpm", "**/Directory.Packages.props", MergePolicyAction.Discard), PathRule("gen", "Generated/", MergePolicyAction.Skip));
            doc.PathRules[1].Enabled = false;
            doc.PathRules[1].Description = "Generated \"code\"\\ with\ttabs";
            doc.LineRules.AddRange(MergePolicyEngine.NuGetPreset(Prefixes).LineRules);
            doc.LineRules.Add(new MergeLineRule
            {
                Id = "block",
                FilePattern = "*.xml",
                BlockStartPattern = "<a>",
                BlockEndPattern = "</a>",
                BlockContainsPattern = string.Empty,
                Description = null,
                Enabled = false
            });

            var json = MergePolicyEngine.Serialize(doc);
            var back = MergePolicyEngine.Parse(json);

            Assert.Equal(1, back.Version);
            Assert.Equal(doc.PathRules.Count, back.PathRules.Count);
            for (var i = 0; i < doc.PathRules.Count; i++)
            {
                Assert.Equal(doc.PathRules[i].Id, back.PathRules[i].Id);
                Assert.Equal(doc.PathRules[i].Pattern, back.PathRules[i].Pattern);
                Assert.Equal(doc.PathRules[i].Action, back.PathRules[i].Action);
                Assert.Equal(doc.PathRules[i].Description, back.PathRules[i].Description);
                Assert.Equal(doc.PathRules[i].Enabled, back.PathRules[i].Enabled);
            }

            Assert.Equal(doc.LineRules.Count, back.LineRules.Count);
            for (var i = 0; i < doc.LineRules.Count; i++)
            {
                Assert.Equal(doc.LineRules[i].Id, back.LineRules[i].Id);
                Assert.Equal(doc.LineRules[i].FilePattern, back.LineRules[i].FilePattern);
                Assert.Equal(doc.LineRules[i].LinePattern, back.LineRules[i].LinePattern);
                Assert.Equal(doc.LineRules[i].BlockStartPattern, back.LineRules[i].BlockStartPattern);
                Assert.Equal(doc.LineRules[i].BlockEndPattern, back.LineRules[i].BlockEndPattern);
                Assert.Equal(doc.LineRules[i].BlockContainsPattern, back.LineRules[i].BlockContainsPattern);
                Assert.Equal(doc.LineRules[i].Description, back.LineRules[i].Description);
                Assert.Equal(doc.LineRules[i].Enabled, back.LineRules[i].Enabled);
            }

            Assert.Equal(json, MergePolicyEngine.Serialize(back));
        }

        [Fact]
        public void Json_EmptyOrNull_GivesAnEmptyDocument()
        {
            foreach (var text in new[] { null, string.Empty, "   \r\n", "null", "\uFEFF" })
            {
                var doc = MergePolicyEngine.Parse(text);
                Assert.NotNull(doc);
                Assert.Equal(1, doc.Version);
                Assert.Empty(doc.PathRules);
                Assert.Empty(doc.LineRules);
            }
        }

        [Fact]
        public void Json_Invalid_ThrowsFormatExceptionWithAClearMessage()
        {
            var garbage = Assert.Throws<FormatException>(() => MergePolicyEngine.Parse("{ \"pathRules\": [ { \"id\": "));
            var array = Assert.Throws<FormatException>(() => MergePolicyEngine.Parse("[1, 2]"));
            var action = Assert.Throws<FormatException>(() => MergePolicyEngine.Parse("{ \"pathRules\": [ { \"id\": \"x\", \"pattern\": \"*\", \"action\": \"Delete\" } ] }"));
            var enabled = Assert.Throws<FormatException>(() => MergePolicyEngine.Parse("{ \"lineRules\": [ { \"id\": \"x\", \"enabled\": \"yes\" } ] }"));
            var notArray = Assert.Throws<FormatException>(() => MergePolicyEngine.Parse("{ \"lineRules\": \"x\" }"));

            Assert.Contains("not valid JSON", garbage.Message);
            Assert.Contains("JSON object", array.Message);
            Assert.Contains("Delete", action.Message);
            Assert.Contains("Merge, Discard or Skip", action.Message);
            Assert.Contains("\"enabled\"", enabled.Message);
            Assert.Contains("array", notArray.Message);
        }

        [Fact]
        public void Json_MissingEnabledMeansEnabled_AndNamesAreCaseInsensitive()
        {
            var doc = MergePolicyEngine.Parse("\uFEFF{ \"VERSION\": 1, \"PathRules\": [ { \"Id\": \"a\", \"PATTERN\": \"*.x\", \"Action\": \"skip\", \"unknown\": 5 } ], \"lineRules\": [ { \"id\": \"b\", \"filePattern\": \"*.y\", \"linePattern\": \"^z\" } ] }");

            var path = Assert.Single(doc.PathRules);
            Assert.Equal("a", path.Id);
            Assert.Equal("*.x", path.Pattern);
            Assert.Equal(MergePolicyAction.Skip, path.Action);
            Assert.True(path.Enabled);
            var line = Assert.Single(doc.LineRules);
            Assert.True(line.Enabled);
            Assert.Null(line.BlockStartPattern);
            Assert.True(new MergePathRule().Enabled);
            Assert.True(new MergeLineRule().Enabled);
        }

        [Fact]
        public void Json_Serialize_IsIndentedAndKeepsRegexesReadable()
        {
            var json = MergePolicyEngine.Serialize(MergePolicyEngine.NuGetPreset(new[] { "Contoso." }));

            Assert.StartsWith("{\r\n  \"version\": 1,\r\n  \"pathRules\": [],\r\n  \"lineRules\": [\r\n    {\r\n      \"id\": \"preset.nuget.packages-config\",", json);
            Assert.Contains("<package\\\\s", json);
            Assert.DoesNotContain("\\u003c", json);
            Assert.Equal("{\r\n  \"version\": 1,\r\n  \"pathRules\": [],\r\n  \"lineRules\": []\r\n}\r\n", MergePolicyEngine.Serialize(null));
        }

        // ------------------------------------------------------------------------------------------
        // MergeWithProtectedLines
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Protected_SourceChangesOnlyProtectedLines_TargetUnchanged()
        {
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "2.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var targetText = PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(targetText, Text(result));
            Assert.Equal(baseText, result.NeutralizedSource);
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Contains("2.0.0", difference.SourceText);
            Assert.Contains("1.5.0", difference.TargetText);
        }

        [Fact]
        public void Protected_CodeAndProtectedLinesChangedTogether_CodePassesAndTargetLinesStay()
        {
            // il target ha la sua versione del pacchetto interno nella riga accanto al pacchetto esterno
            // aggiornato dalla sorgente: niente conflitto
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "2.0.0"), Pkg("Newtonsoft.Json", "13.0.1"));
            var targetText = PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "13.0.1")), Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Equal("preset.nuget.packages-config", difference.RuleId);
            Assert.Equal(3, difference.SourceLine);
            Assert.Equal(3, difference.TargetLine);
            Assert.Equal(Pkg("Contoso.Core", "2.0.0").Trim(), difference.SourceText.Trim());
            Assert.Equal(Pkg("Contoso.Core", "1.5.0").Trim(), difference.TargetText.Trim());
            Assert.Contains("not merged", difference.Summary);
        }

        [Fact]
        public void Protected_SourceAddsAnInternalPackage_ItDoesNotPassAndIsReported()
        {
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Fabrikam.Logging", "3.1.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(baseText, Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Contains("Fabrikam.Logging", difference.SourceText);
            Assert.Null(difference.TargetText);
            Assert.Equal(4, difference.SourceLine);
            Assert.Null(difference.TargetLine);
            Assert.Contains("added", difference.Summary);
            Assert.Contains("The target does not have it: if the task needs it, add it by hand", difference.Summary);
        }

        [Fact]
        public void Protected_SourceAddsAnInternalPackageThatTheTargetAlsoAdded_TheFollowUpNamesTheTargetOne()
        {
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Contoso.Logging", "2.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var targetText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Contoso.Logging", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.Equal(targetText, Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Equal(Pkg("Contoso.Logging", "1.5.0"), difference.TargetText);
            Assert.Equal(4, difference.TargetLine);
            Assert.Contains("the target already has", difference.Summary);
        }

        [Fact]
        public void Protected_SourceAddsAnExternalPackage_ItPasses()
        {
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"), Pkg("Serilog", "2.12.0"));
            var targetText = PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"), Pkg("Serilog", "2.12.0")), Text(result));
            Assert.Empty(result.KeptTargetDifferences);
        }

        [Fact]
        public void Protected_SourceDeletesAProtectedLine_ItStays()
        {
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Fabrikam.Data", "2.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var targetText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Fabrikam.Data", "2.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(targetText, Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Null(difference.SourceText);
            Assert.Null(difference.SourceLine);
            Assert.Contains("2.5.0", difference.TargetText);
            Assert.Equal(4, difference.TargetLine);
            Assert.Contains("removes", difference.Summary);
        }

        [Fact]
        public void Protected_SourceDeletesAnExternalPackage_ItPasses()
        {
            var baseText = PackagesConfig(Pkg("Contoso.Core", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "2.0.0"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.Equal(PackagesConfig(Pkg("Contoso.Core", "1.0.0")), Text(result));
        }

        [Fact]
        public void Protected_OldStyleProjectReferenceBlocksWithHintPath()
        {
            var baseText = OldProject(ReferenceBlock("Contoso.Core", "1.0.0"), ReferenceBlock("Newtonsoft.Json", "12.0.3"), "    <Compile Include=\"Program.cs\" />");
            var sourceText = OldProject(ReferenceBlock("Contoso.Core", "2.0.0"), ReferenceBlock("Newtonsoft.Json", "13.0.1"),
                "    <Compile Include=\"Program.cs\" />\r\n    <Compile Include=\"Helper.cs\" />");
            var targetText = OldProject(ReferenceBlock("Contoso.Core", "1.5.0"), ReferenceBlock("Newtonsoft.Json", "12.0.3"), "    <Compile Include=\"Program.cs\" />");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(OldProject(ReferenceBlock("Contoso.Core", "1.5.0"), ReferenceBlock("Newtonsoft.Json", "13.0.1"),
                "    <Compile Include=\"Program.cs\" />\r\n    <Compile Include=\"Helper.cs\" />"), Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Equal("preset.nuget.reference", difference.RuleId);
            Assert.Equal(3, difference.SourceText.Split('\n').Length);
            Assert.Contains("Contoso.Core.2.0.0", difference.SourceText);
            Assert.Contains("Contoso.Core.1.5.0", difference.TargetText);
            Assert.Empty(MergePolicyEngine.CompareProtectedLines(targetText, Text(result), NuGetRules()));
        }

        [Fact]
        public void Protected_SingleLineReferenceAndPackageImport()
        {
            var baseText = Lines(
                "<Project>",
                "  <ItemGroup>",
                "    <Reference Include=\"Contoso.Tools, Version=1.0.0.0\" />",
                "    <Reference Include=\"System.Web\" />",
                "  </ItemGroup>",
                "  <Import Project=\"..\\packages\\Contoso.Build.1.0.0\\build\\Contoso.Build.targets\" Condition=\"Exists('x')\" />",
                "</Project>");
            var sourceText = baseText.Replace("Contoso.Tools, Version=1.0.0.0", "Contoso.Tools, Version=2.0.0.0")
                .Replace("Contoso.Build.1.0.0", "Contoso.Build.2.0.0")
                .Replace("System.Web\"", "System.Web.Extensions\"");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.Equal(baseText.Replace("System.Web\"", "System.Web.Extensions\""), Text(result));
            Assert.Equal(new[] { "preset.nuget.reference-line", "preset.nuget.package-import" },
                result.KeptTargetDifferences.Select(d => d.RuleId).ToArray());
        }

        [Fact]
        public void Protected_BindingRedirectBlocks()
        {
            var baseText = WebConfig(Redirect("Contoso.Core", "1.0.0.0"), Redirect("Newtonsoft.Json", "12.0.0.0"));
            var sourceText = WebConfig(Redirect("Contoso.Core", "2.0.0.0"), Redirect("Newtonsoft.Json", "13.0.0.0"));
            var targetText = WebConfig(Redirect("Contoso.Core", "1.5.0.0"), Redirect("Newtonsoft.Json", "12.0.0.0"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(WebConfig(Redirect("Contoso.Core", "1.5.0.0"), Redirect("Newtonsoft.Json", "13.0.0.0")), Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Equal("preset.nuget.binding-redirect", difference.RuleId);
            Assert.Contains("newVersion=\"2.0.0.0\"", difference.SourceText);
            Assert.Contains("newVersion=\"1.5.0.0\"", difference.TargetText);
        }

        [Fact]
        public void Protected_ExternalReferenceReplacedByAnInternalOne_TheBlockIsNeverCutInHalf()
        {
            // la sorgente sostituisce il riferimento esterno Foo con l'interno Contoso.Foo: la riga
            // </Reference> e' uguale nei due, ma un blocco protetto si tiene o si scarta per intero
            var baseText = OldProject(ReferenceBlock("Foo", "1.0.0"), "    <Reference Include=\"System\" />", "    <Compile Include=\"Program.cs\" />");
            var sourceText = OldProject(ReferenceBlock("Contoso.Foo", "2.0.0"), "    <Reference Include=\"System\" />", "    <Compile Include=\"Program.cs\" />");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());
            var text = Text(result);

            Assert.False(result.HasConflicts);
            Assert.DoesNotContain("Contoso.Foo", text);
            Assert.DoesNotContain("Foo.1.0.0", text);
            Assert.Equal(Regex.Matches(text, "<Reference Include=\"[^\"]*\">").Count, Regex.Matches(text, "</Reference>").Count);
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Equal(3, difference.SourceText.Split('\n').Length);
            Assert.Contains("Contoso.Foo", difference.SourceText);
        }

        [Fact]
        public void Protected_PackageReferenceSingleLineAndBlock()
        {
            var baseText = SdkProject(
                "    <PackageReference Include=\"Contoso.Logging\" Version=\"1.0.0\" />",
                "    <PackageReference Include=\"Fabrikam.Data\">",
                "      <Version>3.0.0</Version>",
                "    </PackageReference>",
                "    <PackageReference Include=\"Serilog\" Version=\"2.10.0\" />");
            var sourceText = SdkProject(
                "    <PackageReference Include=\"Contoso.Logging\" Version=\"2.0.0\" />",
                "    <PackageReference Include=\"Fabrikam.Data\">",
                "      <Version>4.0.0</Version>",
                "    </PackageReference>",
                "    <PackageReference Include=\"Serilog\" Version=\"2.12.0\" />",
                "    <PackageReference Update=\"contoso.extra\" Version=\"1.0.0\" />");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(baseText.Replace("2.10.0", "2.12.0"), Text(result));
            Assert.Equal(
                new[] { "preset.nuget.package-reference-line", "preset.nuget.package-reference", "preset.nuget.package-reference-line" },
                result.KeptTargetDifferences.Select(d => d.RuleId).ToArray());
        }

        [Fact]
        public void Protected_DirectoryPackagesProps()
        {
            var baseText = Lines(
                "<Project>",
                "  <ItemGroup>",
                "    <PackageVersion Include=\"Contoso.Core\" Version=\"1.0.0\" />",
                "    <PackageVersion Include=\"xunit\" Version=\"2.4.1\" />",
                "  </ItemGroup>",
                "</Project>");
            var sourceText = Lines(
                "<Project>",
                "  <ItemGroup>",
                "    <PackageVersion Include=\"Contoso.Core\" Version=\"2.0.0\" />",
                "    <PackageVersion Include=\"xunit\" Version=\"2.4.2\" />",
                "    <PackageVersion Include=\"Fabrikam.Tools\" Version=\"1.0.0\" />",
                "    <PackageVersion Include=\"Moq\" Version=\"4.18.0\" />",
                "  </ItemGroup>",
                "</Project>");
            var targetText = baseText.Replace("\"1.0.0\"", "\"1.5.0\"");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(Lines(
                "<Project>",
                "  <ItemGroup>",
                "    <PackageVersion Include=\"Contoso.Core\" Version=\"1.5.0\" />",
                "    <PackageVersion Include=\"xunit\" Version=\"2.4.2\" />",
                "    <PackageVersion Include=\"Moq\" Version=\"4.18.0\" />",
                "  </ItemGroup>",
                "</Project>"), Text(result));
            Assert.Equal(2, result.KeptTargetDifferences.Count);
            Assert.Contains(result.KeptTargetDifferences, d => d.SourceText.Contains("Fabrikam.Tools") && d.TargetText == null);
        }

        [Fact]
        public void Protected_AssemblyVersionPreset_CSharpAndVisualBasic()
        {
            var rules = MergePolicyEngine.AssemblyVersionPreset().LineRules;
            var baseCs = Lines("using System.Reflection;", "[assembly: AssemblyTitle(\"Sample\")]", "[assembly: AssemblyVersion(\"1.0.0.0\")]", "[assembly: AssemblyFileVersion(\"1.0.0.0\")]");
            var sourceCs = Lines("using System.Reflection;", "[assembly: AssemblyTitle(\"Sample App\")]", "[assembly: AssemblyVersion(\"2.0.0.0\")]", "[assembly: AssemblyFileVersion(\"2.0.0.0\")]");
            var targetCs = baseCs.Replace("1.0.0.0", "1.5.0.0");
            var baseVb = Lines("Imports System.Reflection", "<Assembly: AssemblyInformationalVersion(\"1.0\")>", "<Assembly: AssemblyCompany(\"Sample\")>");
            var sourceVb = Lines("Imports System.Reflection", "<Assembly: AssemblyInformationalVersion(\"2.0\")>", "<Assembly: AssemblyCompany(\"Sample Ltd\")>");

            var cs = MergePolicyEngine.MergeWithProtectedLines(baseCs, sourceCs, targetCs, rules);
            var vb = MergePolicyEngine.MergeWithProtectedLines(baseVb, sourceVb, baseVb, rules);

            Assert.False(cs.HasConflicts);
            Assert.Equal(Lines("using System.Reflection;", "[assembly: AssemblyTitle(\"Sample App\")]", "[assembly: AssemblyVersion(\"1.5.0.0\")]", "[assembly: AssemblyFileVersion(\"1.5.0.0\")]"), Text(cs));
            Assert.Equal(2, cs.KeptTargetDifferences.Count);
            Assert.Equal(Lines("Imports System.Reflection", "<Assembly: AssemblyInformationalVersion(\"1.0\")>", "<Assembly: AssemblyCompany(\"Sample Ltd\")>"), Text(vb));
        }

        [Fact]
        public void FollowUps_NameTheSamePackageInTheTarget_NeverAnotherOne()
        {
            // la sorgente cambia Contoso.A e toglie Contoso.B; il target ha le sue versioni di entrambi:
            // il promemoria di Contoso.A nomina Contoso.A del target, non anche Contoso.B
            var baseText = PackagesConfig(Pkg("Contoso.A", "1.0.0"), Pkg("Contoso.B", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));
            var sourceText = PackagesConfig(Pkg("Contoso.A", "2.0.0"), Pkg("Newtonsoft.Json", "12.0.2"));
            var targetText = PackagesConfig(Pkg("Contoso.A", "5.0.0"), Pkg("Contoso.B", "5.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(PackagesConfig(Pkg("Contoso.A", "5.0.0"), Pkg("Contoso.B", "5.0.0"), Pkg("Newtonsoft.Json", "12.0.2")), Text(result));
            var changed = Assert.Single(result.KeptTargetDifferences, d => d.SourceText != null);
            Assert.Equal(Pkg("Contoso.A", "5.0.0"), changed.TargetText);
            Assert.Equal(3, changed.TargetLine);
            Assert.DoesNotContain("more line", changed.Summary);
            var removed = Assert.Single(result.KeptTargetDifferences, d => d.SourceText == null);
            Assert.Equal(Pkg("Contoso.B", "5.0.0"), removed.TargetText);
            Assert.Equal(4, removed.TargetLine);
        }

        [Fact]
        public void FollowUps_PackageThatTheTargetDoesNotHave_SaysSo()
        {
            // il target ha sostituito Contoso.A con Contoso.B: la modifica della sorgente a Contoso.A non
            // ha un corrispondente (prima il promemoria diceva "the target keeps Contoso.B")
            var baseText = PackagesConfig(Pkg("Contoso.A", "1.0.0"), Pkg("Contoso.C", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));
            var sourceText = PackagesConfig(Pkg("Contoso.A", "2.0.0"), Pkg("Contoso.C", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));
            var targetText = PackagesConfig(Pkg("Contoso.B", "5.0.0"), Pkg("Contoso.C", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.Equal(targetText, Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Null(difference.TargetText);
            Assert.Null(difference.TargetLine);
            Assert.Contains("the target has no such line", difference.Summary);
        }

        [Fact]
        public void FollowUps_ReferenceBlockThatTheTargetDoesNotHave_IsNotMatchedToPiecesOfAnotherBlock()
        {
            var compile = "    <Compile Include=\"Program.cs\" />";
            var baseText = OldProject(ReferenceBlock("Contoso.P", "1.0.0"), ReferenceBlock("Contoso.Q", "1.0.0"), compile);
            var sourceText = OldProject(ReferenceBlock("Contoso.P", "2.0.0"), ReferenceBlock("Contoso.Q", "1.0.0"), compile);
            var targetText = OldProject(ReferenceBlock("Contoso.Q", "1.5.0"), "    <Reference Include=\"System.Xml\" />", compile);

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(targetText, Text(result));
            var difference = Assert.Single(result.KeptTargetDifferences);
            Assert.Contains("Contoso.P.2.0.0", difference.SourceText);
            Assert.Null(difference.TargetText);
            Assert.Contains("the target has no such line", difference.Summary);
        }

        [Fact]
        public void Protected_PackageReferenceWrittenOnMoreThanOneLine()
        {
            // Version sulla riga dopo Include, Include sulla riga dopo il tag, tag con contenuto: la versione
            // del target resta; un pacchetto esterno scritto allo stesso modo passa
            var baseText = SdkProject(
                "    <PackageReference Include=\"Contoso.Core\"",
                "                      Version=\"1.0.0\" />",
                "    <PackageReference",
                "        Include=\"Fabrikam.Data\"",
                "        Version=\"3.0.0\" />",
                "    <PackageReference Include=\"Contoso.Tools\"",
                "                      Version=\"1.0.0\">",
                "      <PrivateAssets>all</PrivateAssets>",
                "    </PackageReference>",
                "    <PackageReference Include=\"Serilog\"",
                "                      Version=\"2.10.0\" />");
            var sourceText = baseText.Replace("\"1.0.0\"", "\"2.0.0\"").Replace("\"3.0.0\"", "\"4.0.0\"").Replace("\"2.10.0\"", "\"2.12.0\"");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(baseText.Replace("\"2.10.0\"", "\"2.12.0\""), Text(result));
            Assert.Equal(3, result.KeptTargetDifferences.Count);
            Assert.All(result.KeptTargetDifferences, d => Assert.Equal("preset.nuget.package-reference-multiline", d.RuleId));
            Assert.NotEmpty(MergePolicyEngine.CompareProtectedLines(baseText, sourceText, NuGetRules()));
        }

        [Fact]
        public void Protected_PackagesConfigEntryWrittenOnMoreThanOneLine()
        {
            var baseText = Lines("<packages>", "  <package id=\"Contoso.Core\"", "           version=\"1.0.0\" targetFramework=\"net48\" />",
                "  <package id=\"Serilog\"", "           version=\"2.10.0\" targetFramework=\"net48\" />", "</packages>");
            var sourceText = baseText.Replace("1.0.0", "2.0.0").Replace("2.10.0", "2.12.0");

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.Equal(baseText.Replace("2.10.0", "2.12.0"), Text(result));
            Assert.Equal("preset.nuget.packages-config-multiline", Assert.Single(result.KeptTargetDifferences).RuleId);
        }

        [Fact]
        public void Protected_Conflict_TheSourceSideShowsTheTargetProtectedLines_NotTheBaseOnes()
        {
            // la sorgente e il target cambiano in modo diverso lo stesso pacchetto esterno, accanto a un
            // pacchetto interno che ognuno dei due ha cambiato: il conflitto resta, ma il suo lato SOURCE ha
            // la riga interna del target (non quella della base) e "Take source" tiene le righe protette
            var baseText = PackagesConfig(Pkg("Contoso.A", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));
            var sourceText = PackagesConfig(Pkg("Contoso.A", "2.0.0"), Pkg("Newtonsoft.Json", "13.0.1"));
            var targetText = PackagesConfig(Pkg("Contoso.A", "5.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.Equal(1, result.Merge.ConflictCount);
            var conflict = Assert.Single(result.Merge.Blocks, b => b.Kind == MergeBlockKind.Conflict);
            Assert.DoesNotContain(conflict.SourceLines, l => l.Contains("Contoso.A"));
            Assert.Equal(new[] { Pkg("Newtonsoft.Json", "13.0.1") }, conflict.SourceLines.Select(l => l.TrimEnd('\r', '\n')).ToArray());
            Assert.Equal(new[] { Pkg("Newtonsoft.Json", "12.0.3") }, conflict.TargetLines.Select(l => l.TrimEnd('\r', '\n')).ToArray());
            Assert.Empty(MergePolicyEngine.CompareProtectedLines(targetText, TakeAll(result, true), NuGetRules()));
            Assert.Empty(MergePolicyEngine.CompareProtectedLines(targetText, TakeAll(result, false), NuGetRules()));
            Assert.Single(result.KeptTargetDifferences);
        }

        [Fact]
        public void Protected_ConflictOnlyOnProtectedLines_IsNotAConflict()
        {
            // entrambi aggiornano allo stesso modo il pacchetto esterno, il target anche quello interno:
            // tolte le righe protette i due lati coincidono
            var baseText = PackagesConfig(Pkg("Contoso.A", "1.0.0"), Pkg("Newtonsoft.Json", "12.0.1"));
            var sourceText = PackagesConfig(Pkg("Contoso.A", "2.0.0"), Pkg("Newtonsoft.Json", "13.0.1"));
            var targetText = PackagesConfig(Pkg("Contoso.A", "5.0.0"), Pkg("Newtonsoft.Json", "13.0.1"));

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, NuGetRules());

            Assert.False(result.HasConflicts);
            Assert.Equal(targetText, Text(result));
        }

        [Fact]
        public void Protected_RandomizedConflicts_TheSourceSideNeverShowsBaseVersionsOfProtectedLines()
        {
            // proprieta' (seme fisso): nei conflitti le righe protette del lato SOURCE sono righe del target,
            // e sia "Take source" sia "Take target" su tutti i conflitti tengono le righe protette del target
            var random = new Random(20260928);
            var ids = new[] { "Contoso.A", "Contoso.B", "Fabrikam.C", "Moq", "Newtonsoft.Json", "Serilog" };
            var rules = NuGetRules();
            var withConflicts = 0;
            for (var round = 0; round < 600; round++)
            {
                var basePackages = ids.Where(id => random.Next(4) > 0).ToDictionary(id => id, id => Version(random));
                var baseText = PackagesConfig(basePackages);
                var sourceText = PackagesConfig(Mutate(basePackages, ids, random));
                var targetText = PackagesConfig(Mutate(basePackages, ids, random));

                var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, rules);
                if (!result.HasConflicts)
                    continue;

                withConflicts++;
                var targetLines = new HashSet<string>(targetText.Split(new[] { "\r\n" }, StringSplitOptions.None));
                var context = "round " + round + ":\n" + baseText + "\n" + sourceText + "\n" + targetText + "\n" + Text(result);
                foreach (var block in result.Merge.Blocks.Where(b => b.Kind == MergeBlockKind.Conflict))
                {
                    foreach (var line in block.SourceLines.Select(l => l.TrimEnd('\r', '\n')).Where(l => l.Contains("\"Contoso.") || l.Contains("\"Fabrikam.")))
                        Assert.True(targetLines.Contains(line), context);
                }
                Assert.True(MergePolicyEngine.CompareProtectedLines(targetText, TakeAll(result, true), rules).Count == 0, context);
                Assert.True(MergePolicyEngine.CompareProtectedLines(targetText, TakeAll(result, false), rules).Count == 0, context);
            }

            Assert.True(withConflicts > 20, withConflicts.ToString());
        }

        [Fact]
        public void Protected_ConflictOnUnprotectedLines_StaysAConflict()
        {
            var rules = new[] { LineRule("ver", "*.txt", "^version=") };

            var result = MergePolicyEngine.MergeWithProtectedLines("a\nversion=1\nb\n", "a\nversion=2\nsource\n", "a\nversion=1\ntarget\n", rules);

            Assert.True(result.HasConflicts);
            Assert.Equal(1, result.Merge.ConflictCount);
            var text = ThreeWayMerge.BuildTextWithMarkers(result.Merge, "S", "T");
            Assert.Equal("a\nversion=1\n<<<<<<< S\nsource\n=======\ntarget\n>>>>>>> T\n", text);
        }

        [Fact]
        public void Protected_AdjacentChangesOfUnprotectedLines_StayAConflict()
        {
            // come diff3: modifiche diverse su righe adiacenti non protette = conflitto
            var rules = new[] { LineRule("ver", "*.txt", "^version=") };

            var result = MergePolicyEngine.MergeWithProtectedLines("a\nb\nc\n", "a\nB\nc\n", "a\nb\nC\n", rules);

            Assert.True(result.HasConflicts);
        }

        [Fact]
        public void Protected_BothSidesInsertAtTheSamePoint_StaysAConflict()
        {
            // la sorgente inserisce codice e il target una riga protetta nello stesso punto: l'ordine non
            // si puo' decidere
            var rules = new[] { LineRule("ver", "*.txt", "^version=") };

            var result = MergePolicyEngine.MergeWithProtectedLines("x\ny\n", "x\ncode\ny\n", "x\nversion=5\ny\n", rules);

            Assert.True(result.HasConflicts);
        }

        [Fact]
        public void Protected_CrlfAndBomOfTheTarget_AreKept()
        {
            var bom = "\uFEFF";
            var baseText = bom + "<packages>\r\n" + Pkg("Contoso.Core", "1.0.0") + "\r\n" + Pkg("Newtonsoft.Json", "12.0.3") + "\r\n</packages>\r\n";
            var targetText = baseText.Replace("1.0.0", "1.5.0");
            var sourceOnlyProtected = (bom + "<packages>\n" + Pkg("Contoso.Core", "2.0.0") + "\n" + Pkg("Newtonsoft.Json", "12.0.3") + "\n</packages>\n");
            var sourceWithCode = sourceOnlyProtected.Replace("12.0.3", "13.0.1");

            var onlyProtected = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceOnlyProtected, targetText, NuGetRules());
            var withCode = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceWithCode, targetText, NuGetRules());

            Assert.Equal(targetText, Text(onlyProtected));
            Assert.Equal(targetText.Replace("12.0.3", "13.0.1"), Text(withCode));
            Assert.StartsWith(bom, Text(withCode));
            Assert.DoesNotContain("\n", Text(withCode).Replace("\r\n", string.Empty));
        }

        [Fact]
        public void Protected_WithoutRules_IsAPlainMerge()
        {
            var plain = ThreeWayMerge.Merge("a\nb\n", "a\nB\n", "A\nb\n");

            var result = MergePolicyEngine.MergeWithProtectedLines("a\nb\n", "a\nB\n", "A\nb\n", new MergeLineRule[0]);
            var nullRules = MergePolicyEngine.MergeWithProtectedLines("a\nb\n", "a\nB\n", "A\nb\n", null);
            var disabled = LineRule("x", "*", "^a");
            disabled.Enabled = false;
            var disabledOnly = MergePolicyEngine.MergeWithProtectedLines("a\nb\n", "a\nB\n", "A\nb\n", new[] { disabled });

            Assert.Equal(ThreeWayMerge.BuildTextWithMarkers(plain, "S", "T"), ThreeWayMerge.BuildTextWithMarkers(result.Merge, "S", "T"));
            Assert.Equal("a\nB\n", result.NeutralizedSource);
            Assert.Empty(result.KeptTargetDifferences);
            Assert.Equal(result.Merge.ConflictCount, nullRules.Merge.ConflictCount);
            Assert.Equal(result.Merge.ConflictCount, disabledOnly.Merge.ConflictCount);
        }

        [Fact]
        public void Protected_NewFileWithoutBaseAndTarget_DropsTheProtectedLines()
        {
            // il file non c'e' ne' nella base ne' nel target: le righe protette della sorgente non passano
            var sourceText = PackagesConfig(Pkg("Contoso.Core", "2.0.0"), Pkg("Serilog", "2.12.0"));

            var result = MergePolicyEngine.MergeWithProtectedLines(string.Empty, sourceText, string.Empty, NuGetRules());

            Assert.Equal(PackagesConfig(Pkg("Serilog", "2.12.0")), Text(result));
            Assert.Single(result.KeptTargetDifferences);
        }

        [Fact]
        public void Protected_BlockWithoutEnd_IsNotABlock()
        {
            var rules = new[] { new MergeLineRule { Id = "region", FilePattern = "*.cs", BlockStartPattern = "#region Keep", BlockEndPattern = "#endregion" } };

            var closed = MergePolicyEngine.MergeWithProtectedLines("#region Keep\nx = 1\n#endregion\ny\n", "#region Keep\nx = 2\n#endregion\ny\n", "#region Keep\nx = 1\n#endregion\ny\n", rules);
            var open = MergePolicyEngine.MergeWithProtectedLines("#region Keep\nx = 1\ny\n", "#region Keep\nx = 2\ny\n", "#region Keep\nx = 1\ny\n", rules);

            Assert.Equal("#region Keep\nx = 1\n#endregion\ny\n", Text(closed));
            Assert.Equal("#region Keep\nx = 2\ny\n", Text(open));
        }

        [Fact]
        public void Protected_BlockContainsPattern_DecidesWhichBlocksAreProtected()
        {
            var rules = new[]
            {
                new MergeLineRule { Id = "blocks", FilePattern = "*.xml", BlockStartPattern = "^<item>$", BlockEndPattern = "^</item>$", BlockContainsPattern = "keep" }
            };
            var baseText = "<item>\nkeep 1\n</item>\n<item>\nfree 1\n</item>\n";
            var sourceText = "<item>\nkeep 2\n</item>\n<item>\nfree 2\n</item>\n";

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, rules);

            Assert.Equal("<item>\nkeep 1\n</item>\n<item>\nfree 2\n</item>\n", Text(result));
        }

        [Fact]
        public void Protected_RandomizedPackageLists_KeepTheTargetProtectedLines()
        {
            // proprieta' (seme fisso): senza conflitti, le righe protette del risultato sono quelle del
            // target; se il target e' la base, passano tutte e sole le righe non protette della sorgente
            var random = new Random(20260927);
            var ids = new[] { "Contoso.A", "Contoso.B", "Fabrikam.C", "Fabrikam.D", "Moq", "Newtonsoft.Json", "Serilog", "xunit" };
            var rules = NuGetRules();
            var checkedWithoutConflicts = 0;
            for (var round = 0; round < 400; round++)
            {
                var basePackages = ids.Where(id => random.Next(3) > 0).ToDictionary(id => id, id => Version(random));
                var source = Mutate(basePackages, ids, random);
                var target = random.Next(3) == 0 ? new Dictionary<string, string>(basePackages) : Mutate(basePackages, ids, random);
                var baseText = PackagesConfig(basePackages);
                var sourceText = PackagesConfig(source);
                var targetText = PackagesConfig(target);

                var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, targetText, rules);
                if (result.HasConflicts)
                    continue;

                checkedWithoutConflicts++;
                var text = Text(result);
                Assert.True(MergePolicyEngine.CompareProtectedLines(targetText, text, rules).Count == 0,
                    "round " + round + ":\n" + baseText + "\n" + sourceText + "\n" + targetText + "\n" + text);
                if (targetText == baseText)
                {
                    Func<string, string[]> external = t => t.Split('\n')
                        .Where(l => l.Contains("<package ") && !l.Contains("\"Contoso.") && !l.Contains("\"Fabrikam."))
                        .ToArray();
                    Assert.Equal(external(sourceText), external(text));
                }
            }

            Assert.True(checkedWithoutConflicts > 200, checkedWithoutConflicts.ToString());
        }

        [Fact]
        public void Protected_LargeFileWithUnclosedBlocks_IsFast()
        {
            var lines = Enumerable.Range(0, 20000).Select(i => "<dependentAssembly> line " + i).ToArray();
            var baseText = string.Join("\n", lines) + "\n";
            var sourceText = baseText.Replace("line 10000\n", "line 10000 changed\n");
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var result = MergePolicyEngine.MergeWithProtectedLines(baseText, sourceText, baseText, NuGetRules());

            Assert.Equal(sourceText, Text(result));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        }

        // ------------------------------------------------------------------------------------------
        // CompareProtectedLines
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Compare_SameProtectedLines_NoViolations_EvenIfOtherLinesDiffer()
        {
            var target = PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var result = PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "13.0.1"), Pkg("Serilog", "2.12.0")).Replace("\r\n", "\n");

            Assert.Empty(MergePolicyEngine.CompareProtectedLines(target, result, NuGetRules()));
        }

        [Fact]
        public void Compare_ChangedProtectedLine_IsAViolation()
        {
            var target = PackagesConfig(Pkg("Contoso.Core", "1.5.0"), Pkg("Newtonsoft.Json", "12.0.3"));
            var result = PackagesConfig(Pkg("Contoso.Core", "2.0.0"), Pkg("Newtonsoft.Json", "12.0.3"));

            var violations = MergePolicyEngine.CompareProtectedLines(target, result, NuGetRules());

            Assert.Equal(2, violations.Count);
            Assert.StartsWith("Target line 3 is protected by rule 'preset.nuget.packages-config'", violations[0]);
            Assert.Contains("1.5.0", violations[0]);
            Assert.StartsWith("Result line 3 is protected", violations[1]);
            Assert.Contains("2.0.0", violations[1]);
        }

        [Fact]
        public void Compare_MissingExtraAndReorderedProtectedLines_AreViolations()
        {
            var target = PackagesConfig(Pkg("Contoso.A", "1.0.0"), Pkg("Contoso.B", "1.0.0"));

            var missing = MergePolicyEngine.CompareProtectedLines(target, PackagesConfig(Pkg("Contoso.A", "1.0.0")), NuGetRules());
            var extra = MergePolicyEngine.CompareProtectedLines(target, PackagesConfig(Pkg("Contoso.A", "1.0.0"), Pkg("Contoso.B", "1.0.0"), Pkg("Fabrikam.C", "1.0.0")), NuGetRules());
            var reordered = MergePolicyEngine.CompareProtectedLines(target, PackagesConfig(Pkg("Contoso.B", "1.0.0"), Pkg("Contoso.A", "1.0.0")), NuGetRules());
            var deleted = MergePolicyEngine.CompareProtectedLines(target, string.Empty, NuGetRules());

            Assert.Contains("Contoso.B", Assert.Single(missing));
            Assert.Contains("Fabrikam.C", Assert.Single(extra));
            Assert.NotEmpty(reordered);
            Assert.Equal(2, deleted.Count);
        }

        [Fact]
        public void Compare_WithoutRules_NoViolations()
        {
            Assert.Empty(MergePolicyEngine.CompareProtectedLines("a\n", "b\n", null));
            Assert.Empty(MergePolicyEngine.CompareProtectedLines("a\n", "b\n", new MergeLineRule[0]));
            Assert.Empty(MergePolicyEngine.CompareProtectedLines(null, null, NuGetRules()));
        }

        // ------------------------------------------------------------------------------------------
        // ValidateStepActions
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void ValidateActions_AllMergeOrNoCallback_NoErrors()
        {
            var plan = Plan(new[] { Change(10, "a.cs", TaskChangeKind.Edit), Change(20, "b.cs", TaskChangeKind.Edit) }, new[] { "a.cs", "b.cs" });

            Assert.Empty(MergePolicyEngine.ValidateStepActions(plan, s => MergePolicyAction.Merge));
            Assert.Empty(MergePolicyEngine.ValidateStepActions(plan, null));
            Assert.Empty(MergePolicyEngine.ValidateStepActions(plan, s => s.RelativePath == "a.cs" ? MergePolicyAction.Discard : MergePolicyAction.Skip));
        }

        [Fact]
        public void ValidateActions_SkipOrDiscardOfAnAddedFolderWithMergedStepsInside_IsAnError()
        {
            var plan = Plan(new[]
            {
                Change(10, "NewDir", AddFolder, TaskMergeItemKind.Folder),
                Change(10, "NewDir/a.cs", AddNew),
                Change(10, "NewDir/b.cs", AddNew)
            }, new string[0]);
            Assert.True(plan.IsValid, string.Join(" ", plan.Errors));

            var skipFolder = MergePolicyEngine.ValidateStepActions(plan, s => s.RelativePath == "NewDir" ? MergePolicyAction.Skip : MergePolicyAction.Merge);
            var skipAll = MergePolicyEngine.ValidateStepActions(plan, s => MergePolicyAction.Skip);

            var error = Assert.Single(skipFolder);
            Assert.Contains("adds a folder that contains merged steps", error);
            Assert.Contains("NewDir/a.cs", error);
            Assert.Contains("NewDir/b.cs", error);
            Assert.Empty(skipAll);
        }

        [Fact]
        public void ValidateActions_SkipOrDiscardOfAStepFollowedByAMergedStepOfTheSameItem_IsAnError()
        {
            // a.cs: C10 e C30 con un terzo (C20) in mezzo -> due passi, due parti
            var plan = Plan(new[] { Change(10, "a.cs", TaskChangeKind.Edit), Change(30, "a.cs", TaskChangeKind.Edit) }, new[] { "a.cs" },
                (item, a, b) => a < 20 && 20 < b ? new[] { 20 } : new int[0]);
            Assert.Equal(2, plan.Steps.Count);
            var first = plan.Steps[0];

            var skipFirst = MergePolicyEngine.ValidateStepActions(plan, s => s == first ? MergePolicyAction.Skip : MergePolicyAction.Merge);
            var discardFirst = MergePolicyEngine.ValidateStepActions(plan, s => s == first ? MergePolicyAction.Discard : MergePolicyAction.Merge);
            var skipSecond = MergePolicyEngine.ValidateStepActions(plan, s => s == first ? MergePolicyAction.Merge : MergePolicyAction.Skip);

            Assert.Contains("later step of the same item is merged", Assert.Single(skipFirst));
            Assert.Contains("later step of the same item is merged", Assert.Single(discardFirst));
            Assert.Empty(skipSecond);
        }

        [Fact]
        public void ValidateActions_DiscardOfAnItemThatIsNotInTheTarget_IsAnError()
        {
            var plan = Plan(new[] { Change(10, "new.cs", AddNew), Change(10, "old.cs", TaskChangeKind.Edit) }, new[] { "old.cs" });

            var errors = MergePolicyEngine.ValidateStepActions(plan, s => MergePolicyAction.Discard);

            var error = Assert.Single(errors);
            Assert.Contains("new.cs", error);
            Assert.Contains("Use Skip instead", error);
            Assert.Empty(MergePolicyEngine.ValidateStepActions(plan, s => s.RelativePath == "new.cs" ? MergePolicyAction.Skip : MergePolicyAction.Discard));
        }

        [Fact]
        public void ValidateActions_CallbackThatThrows_IsAnError()
        {
            var plan = Plan(new[] { Change(10, "a.cs", TaskChangeKind.Edit) }, new[] { "a.cs" });

            var error = Assert.Single(MergePolicyEngine.ValidateStepActions(plan, s => { throw new InvalidOperationException("boom"); }));

            Assert.Contains("boom", error);
            Assert.Throws<ArgumentNullException>(() => MergePolicyEngine.ValidateStepActions(null, s => MergePolicyAction.Merge));
        }

        // ------------------------------------------------------------------------------------------
        // Modelli pronti
        // ------------------------------------------------------------------------------------------

        [Fact]
        public void Preset_NuGet_WithoutPrefixes_IsEmpty()
        {
            var none = MergePolicyEngine.NuGetPreset(null);
            var blank = MergePolicyEngine.NuGetPreset(new[] { " ", string.Empty });

            Assert.Empty(none.PathRules);
            Assert.Empty(none.LineRules);
            Assert.Empty(blank.LineRules);
        }

        [Fact]
        public void Preset_NuGet_PrefixesAreLiteralAndCaseInsensitive()
        {
            var rules = MergePolicyEngine.NuGetPreset(new[] { "Contoso.", "contoso.", "Fab+rikam" }).LineRules;
            var target = PackagesConfig(Pkg("Contoso.Core", "1.0.0"));

            // "Contoso." non vale per "ContosoX.Core" (il punto e' letterale), vale per "CONTOSO.Core"
            Assert.NotEmpty(MergePolicyEngine.CompareProtectedLines(target, PackagesConfig(Pkg("CONTOSO.Core", "2.0.0")), rules));
            Assert.Empty(MergePolicyEngine.CompareProtectedLines(PackagesConfig(Pkg("ContosoX.Core", "1.0.0")), PackagesConfig(Pkg("ContosoX.Core", "2.0.0")), rules));
            Assert.NotEmpty(MergePolicyEngine.CompareProtectedLines(PackagesConfig(Pkg("Fab+rikam.X", "1.0.0")), string.Empty, rules));
            Assert.Empty(MergePolicyEngine.CompareProtectedLines(PackagesConfig(Pkg("Fabbrikam.X", "1.0.0")), string.Empty, rules));
            Assert.All(rules, r => Assert.StartsWith("preset.nuget.", r.Id));
            Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        }

        [Fact]
        public void Preset_CentralPackageManagement_DiscardsDirectoryPackagesPropsEverywhere()
        {
            var doc = MergePolicyEngine.CentralPackageManagementPreset();
            var policy = new EffectiveMergePolicy(doc, null);

            var rule = Assert.Single(doc.PathRules);
            Assert.Equal("preset.cpm.directory-packages-props", rule.Id);
            Assert.Equal("**/Directory.Packages.props", rule.Pattern);
            Assert.Equal(MergePolicyAction.Discard, MergePolicyEngine.Decide(policy, "Directory.Packages.props").Action);
            Assert.Equal(MergePolicyAction.Discard, MergePolicyEngine.Decide(policy, "src/Directory.Packages.props").Action);
            Assert.Equal(MergePolicyAction.Merge, MergePolicyEngine.Decide(policy, "Directory.Build.props").Action);
            Assert.Empty(doc.LineRules);
        }

        [Fact]
        public void Preset_NuGet_HasNoDiscardRuleForPackagesConfig()
        {
            var doc = MergePolicyEngine.NuGetPreset(Prefixes);

            Assert.Empty(doc.PathRules);
            Assert.Equal(MergePolicyAction.Merge, MergePolicyEngine.Decide(new EffectiveMergePolicy(doc, null), "packages.config").Action);
        }

        // ------------------------------------------------------------------------------------------
        // Supporto
        // ------------------------------------------------------------------------------------------

        private static MergePolicyDocument Doc(params MergePathRule[] pathRules)
        {
            var doc = new MergePolicyDocument();
            doc.PathRules.AddRange(pathRules);
            return doc;
        }

        private static MergePathRule PathRule(string id, string pattern, MergePolicyAction action)
        {
            return new MergePathRule { Id = id, Pattern = pattern, Action = action, Enabled = true };
        }

        private static MergeLineRule LineRule(string id, string filePattern, string linePattern)
        {
            return new MergeLineRule { Id = id, FilePattern = filePattern, LinePattern = linePattern, Enabled = true };
        }

        private static List<MergeLineRule> NuGetRules()
        {
            return MergePolicyEngine.NuGetPreset(Prefixes).LineRules;
        }

        private static string Text(ProtectedMergeResult result)
        {
            return ThreeWayMerge.BuildTextWithMarkers(result.Merge, "SOURCE", "TARGET");
        }

        // ogni conflitto risolto con lo stesso lato (come "Take source"/"Take target" nel resolver)
        private static string TakeAll(ProtectedMergeResult result, bool source)
        {
            var text = Text(result);
            var regions = ThreeWayMerge.FindConflictRegions(text, result.Merge.MarkerSize);
            for (var i = regions.Count - 1; i >= 0; i--)
                text = ThreeWayMerge.ResolveRegion(text, regions[i], source ? ConflictChoice.TakeSource : ConflictChoice.TakeTarget);
            return text;
        }

        // righe con CRLF, ognuna terminata
        private static string Lines(params string[] lines)
        {
            return string.Join("\r\n", lines) + "\r\n";
        }

        private static string Pkg(string id, string version)
        {
            return "  <package id=\"" + id + "\" version=\"" + version + "\" targetFramework=\"net48\" />";
        }

        private static string PackagesConfig(params string[] packages)
        {
            var lines = new List<string> { "<?xml version=\"1.0\" encoding=\"utf-8\"?>", "<packages>" };
            lines.AddRange(packages);
            lines.Add("</packages>");
            return Lines(lines.ToArray());
        }

        private static string PackagesConfig(Dictionary<string, string> packages)
        {
            return PackagesConfig(packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => Pkg(p.Key, p.Value)).ToArray());
        }

        private static string Version(Random random)
        {
            return random.Next(1, 4) + "." + random.Next(0, 3) + ".0";
        }

        // cambia versioni, toglie e aggiunge pacchetti a caso
        private static Dictionary<string, string> Mutate(Dictionary<string, string> packages, string[] ids, Random random)
        {
            var result = new Dictionary<string, string>(packages);
            foreach (var id in ids)
            {
                var roll = random.Next(6);
                if (roll == 0 && result.ContainsKey(id))
                    result.Remove(id);
                else if (roll == 1)
                    result[id] = Version(random);
            }
            return result;
        }

        private static string ReferenceBlock(string id, string version)
        {
            return "    <Reference Include=\"" + id + ", Version=" + version + ".0, Culture=neutral, processorArchitecture=MSIL\">\r\n"
                + "      <HintPath>..\\packages\\" + id + "." + version + "\\lib\\net48\\" + id + ".dll</HintPath>\r\n"
                + "    </Reference>";
        }

        private static string OldProject(string reference1, string reference2, string compile)
        {
            return Lines(
                "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">",
                "  <ItemGroup>",
                reference1,
                reference2,
                "    <Reference Include=\"System\" />",
                "  </ItemGroup>",
                "  <ItemGroup>",
                compile,
                "  </ItemGroup>",
                "</Project>");
        }

        private static string SdkProject(params string[] items)
        {
            var lines = new List<string> { "<Project Sdk=\"Microsoft.NET.Sdk\">", "  <ItemGroup>" };
            lines.AddRange(items);
            lines.Add("  </ItemGroup>");
            lines.Add("</Project>");
            return Lines(lines.ToArray());
        }

        private static string Redirect(string name, string version)
        {
            return "      <dependentAssembly>\r\n"
                + "        <assemblyIdentity name=\"" + name + "\" publicKeyToken=\"0123456789abcdef\" culture=\"neutral\" />\r\n"
                + "        <bindingRedirect oldVersion=\"0.0.0.0-" + version + "\" newVersion=\"" + version + "\" />\r\n"
                + "      </dependentAssembly>";
        }

        private static string WebConfig(params string[] redirects)
        {
            var lines = new List<string>
            {
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<configuration>",
                "  <runtime>",
                "    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">"
            };
            lines.AddRange(redirects);
            lines.Add("    </assemblyBinding>");
            lines.Add("  </runtime>");
            lines.Add("</configuration>");
            return Lines(lines.ToArray());
        }

        private static TaskChangeInfo Change(int changesetId, string relativePath, TaskChangeKind kind, TaskMergeItemKind itemKind = TaskMergeItemKind.File)
        {
            return new TaskChangeInfo(changesetId, SourceRoot + "/" + relativePath, itemKind, kind);
        }

        private static TaskMergePlan Plan(IEnumerable<TaskChangeInfo> changes, IEnumerable<string> existing, Func<string, int, int, IReadOnlyList<int>> thirds = null)
        {
            var list = changes.ToList();
            var exists = new HashSet<string>(existing.Select(e => TargetRoot + "/" + e), StringComparer.OrdinalIgnoreCase);
            return TaskMergePlanner.Build(new TaskMergePlanInput
            {
                SourceBranch = SourceRoot,
                TargetBranch = TargetRoot,
                TaskChangesetIds = list.Select(c => c.ChangesetId).Distinct().OrderBy(id => id).ToList(),
                Changes = list,
                TargetItemExists = item => exists.Contains(item),
                ThirdPartyChangesetsBetween = thirds ?? ((item, a, b) => new int[0])
            });
        }
    }
}
