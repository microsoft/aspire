// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Structural verifier for the repository-local code review skill that reviews test-trigger-map
/// changes (<c>.github/skills/code-review-test-trigger-map</c>).
///
/// These tests check *packaging and corpus structure only*: that Copilot code review can discover
/// and load the skill (path layout, YAML frontmatter, activation triggers named in the description)
/// and that the evaluation corpus is complete and well formed. They deliberately measure nothing
/// about review quality — that is the manual semantic loop documented in
/// <c>.github/skills/code-review-test-trigger-map/evals/README.md</c>.
/// </summary>
public sealed class ReviewSkillTests
{
    private const string SkillName = "code-review-test-trigger-map";
    private static readonly string s_skillDirectory = Path.Combine(RepoRoot.Path, ".github", "skills", SkillName);
    private static readonly string s_skillFile = Path.Combine(s_skillDirectory, "SKILL.md");
    private static readonly string s_casesFile = Path.Combine(s_skillDirectory, "evals", "cases.json");

    [Fact]
    public void SkillFileExistsAtTheDiscoverablePath()
    {
        // Copilot code review discovers repository skills at .github/skills/<name>/SKILL.md, so the
        // directory name, the file name, and the frontmatter `name` all have to agree.
        Assert.True(File.Exists(s_skillFile), $"Expected skill file at {s_skillFile}");
        Assert.True(File.Exists(Path.Combine(s_skillDirectory, "evals", "README.md")),
            "The skill's evaluation plan (evals/README.md) is missing.");
    }

    [Fact]
    public void FrontmatterNameMatchesTheSkillDirectory()
    {
        var frontmatter = ReadFrontmatter();

        Assert.Equal(SkillName, frontmatter["name"]);
    }

    [Theory]
    // The activation triggers from the skill's own applicability list. Copilot selects a skill from
    // its description, so a trigger that is not named here can never activate the skill.
    [InlineData("eng/github-ci/test-trigger-map.yml")]
    [InlineData("eng/github-ci/ci-skip-entirely-patterns.txt")]
    [InlineData("tools/SelectTests/**")]
    [InlineData("run_*")]
    [InlineData("reusable workflow")]
    [InlineData("test project")]
    public void DescriptionNamesTheActivationTrigger(string trigger)
    {
        var description = ReadFrontmatter()["description"];

        Assert.Contains(trigger, description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkillBodyPointsAtTheAuthoritativeDocsInsteadOfRestatingThem()
    {
        // The skill is a review procedure, not a copy of the handbook: the map contract lives in
        // docs/ci and must not drift into the skill body.
        var body = File.ReadAllText(s_skillFile);

        Assert.Contains("docs/ci/test-trigger-map.md", body, StringComparison.Ordinal);
        Assert.Contains("docs/ci/test-trigger-selector-design.md", body, StringComparison.Ordinal);
        Assert.Contains("tests/Infrastructure.Tests/TestTriggerMap/", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BroadInstructionsDelegateToTheSkillInsteadOfDuplicatingIt()
    {
        var agents = File.ReadAllText(Path.Combine(RepoRoot.Path, "AGENTS.md"));

        Assert.Contains($".github/skills/{SkillName}/SKILL.md", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillFilesAreRoutedToInfrastructureTests()
    {
        // Dogfooding: this test project reads the skill and its corpus, so the map must route
        // .github/skills/** here, and the prefilter must carve it out (its **.md pattern would
        // otherwise drop SKILL.md before either layer runs).
        var map = TestTriggerMap.Load(RepoRoot.Path);
        const string casesPath = ".github/skills/" + SkillName + "/evals/cases.json";

        var targets = map.PathRules
            .Where(rule => rule.Paths.Any(path => TestTriggerMap.GlobMatches(path, casesPath)))
            .SelectMany(rule => rule.Targets)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("test:Infrastructure.Tests", targets);
        Assert.Contains(".github/skills/**", map.Prefilter!.KeepRouted);
    }

    [Fact]
    public void EveryEvaluationCaseIsCompleteAndWellFormed()
    {
        var corpus = LoadCorpus();

        Assert.Equal(1, corpus.Version);
        Assert.Equal(SkillName, corpus.Skill);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evalCase in corpus.Cases)
        {
            Assert.True(ids.Add(evalCase.Id), $"Duplicate case id: {evalCase.Id}");
            Assert.False(string.IsNullOrWhiteSpace(evalCase.Title), $"Case '{evalCase.Id}' has no title.");
            Assert.False(string.IsNullOrWhiteSpace(evalCase.Category), $"Case '{evalCase.Id}' has no category.");
            Assert.False(string.IsNullOrWhiteSpace(evalCase.DiffSummary), $"Case '{evalCase.Id}' has no diff_summary.");
            Assert.Contains(evalCase.Kind, new[] { "positive", "true-negative" });
            Assert.NotEmpty(evalCase.ChangedFiles);

            foreach (var finding in evalCase.ExpectedFindings)
            {
                Assert.False(string.IsNullOrWhiteSpace(finding.Id), $"Case '{evalCase.Id}' has a finding without an id.");
                Assert.False(string.IsNullOrWhiteSpace(finding.Summary), $"Finding '{finding.Id}' has no summary.");

                // Every seeded defect must state what evidence a passing review comment has to carry,
                // otherwise the case cannot be scored against the skill's evidence requirement.
                Assert.NotEmpty(finding.EvidenceRequired);
            }

            if (evalCase.Kind == "positive")
            {
                Assert.NotEmpty(evalCase.ExpectedFindings);
            }
            else
            {
                Assert.Empty(evalCase.ExpectedFindings);
                Assert.NotEmpty(evalCase.ExpectedNonFindings);
            }
        }
    }

    [Fact]
    public void CorpusCoversTheDefectClassesTheSkillExistsFor()
    {
        var corpus = LoadCorpus();

        // Both defects from PR #19939 — the motivating cases — must stay in the corpus.
        var pr19939 = corpus.Cases
            .Where(c => c.Source is not null && c.Source.EndsWith("/pull/19939", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, pr19939.Count);
        Assert.Contains(pr19939, c => c.Category == "skip-pattern-glob-semantics");
        Assert.Contains(pr19939, c => c.Category == "route-removal-and-weak-regression-test");

        // A true negative guards the most likely false positive: asking for a manual map rule for a
        // file the Aspire.slnx ProjectGraph already owns.
        Assert.Contains(corpus.Cases, c => c.Kind == "true-negative" && c.Category == "layer-1-ownership");

        // Workflow/run-gate wiring is the other half of the map contract.
        Assert.Contains(corpus.Cases, c => c.Category == "workflow-run-gate");
    }

    private static EvalCorpus LoadCorpus()
    {
        using var stream = File.OpenRead(s_casesFile);
        var corpus = JsonSerializer.Deserialize<EvalCorpus>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

        return corpus ?? throw new InvalidOperationException($"Could not parse {s_casesFile}");
    }

    private static Dictionary<string, string> ReadFrontmatter()
    {
        var text = File.ReadAllText(s_skillFile).ReplaceLineEndings("\n");

        // SKILL.md frontmatter: a YAML mapping delimited by '---' lines at the very top of the file.
        //   ---
        //   name: code-review-test-trigger-map
        //   description: "Review pull requests that ..."
        //   ---
        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        Assert.True(end > 0, "SKILL.md has no closing frontmatter delimiter.");

        var yaml = text[4..end];
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();

        var frontmatter = deserializer.Deserialize<Dictionary<string, string>>(yaml);
        Assert.NotNull(frontmatter);
        Assert.Contains("name", frontmatter);
        Assert.Contains("description", frontmatter);

        return frontmatter;
    }

    private sealed class EvalCorpus
    {
        public int Version { get; set; }
        public string Skill { get; set; } = "";
        public List<EvalCase> Cases { get; set; } = new();
    }

    private sealed class EvalCase
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Category { get; set; } = "";
        public string? Source { get; set; }
        public List<string> ChangedFiles { get; set; } = new();
        public string DiffSummary { get; set; } = "";
        public List<ExpectedFinding> ExpectedFindings { get; set; } = new();
        public List<string> ExpectedNonFindings { get; set; } = new();
    }

    private sealed class ExpectedFinding
    {
        public string Id { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<string> EvidenceRequired { get; set; } = new();
    }
}
