// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector;
using TestSelector.Analyzers;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Integration;

/// <summary>
/// End-to-end integration tests for the test selection feature.
/// Tests the full evaluation workflow using inline JSON configs.
/// </summary>
public class EndToEndEvaluationTests
{
    #region Full Workflow Tests

    [Fact]
    public void Evaluate_FullWorkflow_AllAnalyzers()
    {
        var configJson = """
        {
            "ignore": ["**/*.md", "docs/**", ".github/**"],
            "runEverything": ["global.json", "Directory.Build.props", "*.slnx"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**", "tests/**"],
                    "exclude": ["src/Aspire.Cli/**"]
                },
                "cli_e2e": {
                    "when": ["src/Aspire.Cli/**", "tests/Aspire.Cli.EndToEnd.Tests/**"]
                }
            },
            "mappings": [
                {"from": "src/Components/{name}/**", "to": "tests/{name}.Tests/"},
                {"from": "tests/{name}.Tests/**", "to": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);
        var categoryMapper = new CategoryMapper(config.JobCategories);
        var projectResolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[]
        {
            "src/Components/Aspire.Redis/RedisExtensions.cs",
            "tests/Aspire.Redis.Tests/RedisTests.cs",
            "README.md"
        };

        // Step 1: Filter ignored files
        var (ignoredFiles, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        Assert.Single(ignoredFiles);
        Assert.Contains("README.md", ignoredFiles);
        Assert.Equal(2, activeFiles.Count);

        // Step 2: Check for critical files
        var criticalFile = criticalDetector.FindFirstCriticalFile(activeFiles);
        Assert.Null(criticalFile.File);

        // Step 3: Map to categories
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);
        Assert.True(categories["integrations"]);
        Assert.False(categories["cli_e2e"]);
        Assert.Equal(2, matchedFiles.Count);

        // Step 4: Resolve test projects
        var testProjects = projectResolver.ResolveAllTestProjects(activeFiles);
        Assert.Contains("tests/Aspire.Redis.Tests/", testProjects);
    }

    [Fact]
    public void Evaluate_CriticalFile_TriggersAllTests()
    {
        var configJson = """
        {
            "ignore": ["**/*.md"],
            "runEverything": ["global.json", "Directory.Build.props"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);

        var changedFiles = new[] { "global.json", "src/SomeFile.cs" };

        var file = criticalDetector.FindFirstCriticalFile(changedFiles);

        Assert.Equal("global.json", file.File);
    }

    [Fact]
    public async Task Evaluate_AuditConfigOnlyChange_ReturnsNonApplyingResult()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {
                "extension": {
                    "when": ["extension/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);

        var result = await TestEvaluator.EvaluateAsync(
            config,
            ["eng/scripts/test-selection-rules.audit.json"],
            solution: "Aspire.slnx",
            fromRef: null,
            toRef: null,
            workingDir: Directory.GetCurrentDirectory(),
            ciEnvironment: "github-actions",
            verbose: false,
            nonApplyingPaths: ["eng/scripts/test-selection-rules.audit.json"]);

        Assert.False(result.RunAllTests);
        Assert.Equal("audit_config_only", result.Reason);
        Assert.Empty(result.AffectedTestProjects);
        Assert.False(result.Categories["extension"]);
        Assert.Contains("eng/scripts/test-selection-rules.audit.json", result.ChangedFiles);
    }

    #endregion

    #region Conservative Fallback Tests

    [Fact]
    public void Evaluate_UnmatchedFile_TriggersConservativeFallback()
    {
        var configJson = """
        {
            "ignore": ["**/*.md"],
            "jobCategories": {
                "known": {
                    "when": ["src/**", "tests/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var changedFiles = new[] { "some-random-file.txt" };
        var (_, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);

        var unmatchedFiles = activeFiles.Except(matchedFiles).ToList();

        Assert.Single(unmatchedFiles);
        Assert.Contains("some-random-file.txt", unmatchedFiles);
        Assert.False(categories["known"]);
    }

    [Fact]
    public void Evaluate_AllFilesIgnored_NoTestsRun()
    {
        var configJson = """
        {
            "ignore": ["**/*.md", "docs/**", ".github/**", "eng/**"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);

        var changedFiles = new[]
        {
            "README.md",
            "docs/getting-started.md",
            ".github/workflows/ci.yml",
            "eng/Version.Details.xml"
        };

        var (ignoredFiles, activeFiles) = ignoreFilter.SplitFiles(changedFiles);

        Assert.Equal(4, ignoredFiles.Count);
        Assert.Empty(activeFiles);
    }

    [Fact]
    public void Evaluate_NoChanges_NoTestsRun()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var changedFiles = Array.Empty<string>();

        var (_, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);

        Assert.Empty(activeFiles);
        Assert.Empty(matchedFiles);
        Assert.False(categories["integrations"]);
    }

    #endregion

    #region Category Exclusion Tests

    [Fact]
    public void Evaluate_ExcludePaths_PreventsCategoryMatch()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"],
                    "exclude": ["src/Aspire.Cli/**", "src/Aspire.ProjectTemplates/**"]
                },
                "cli": {
                    "when": ["src/Aspire.Cli/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var changedFiles = new[] { "src/Aspire.Cli/Program.cs" };

        var (categories, _) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.False(categories["integrations"]); // Excluded
        Assert.True(categories["cli"]);
    }

    [Fact]
    public void Evaluate_MultipleCategories_FileMatchesMultiple()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {
                "hosting": {
                    "when": ["src/Aspire.Hosting/**"]
                },
                "allsrc": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var changedFiles = new[] { "src/Aspire.Hosting/Host.cs" };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["hosting"]);
        Assert.True(categories["allsrc"]);
        Assert.Single(matchedFiles);
    }

    #endregion

    #region Project Mapping Tests

    [Fact]
    public void Evaluate_ProjectMapping_CaptureAndSubstitution()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {},
            "mappings": [
                {"from": "src/Components/{name}/**", "to": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[]
        {
            "src/Components/Aspire.Npgsql/NpgsqlExtensions.cs",
            "src/Components/Aspire.Redis/RedisExtensions.cs"
        };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Equal(2, testProjects.Count);
        Assert.Contains("tests/Aspire.Npgsql.Tests/", testProjects);
        Assert.Contains("tests/Aspire.Redis.Tests/", testProjects);
    }

    [Fact]
    public void Evaluate_ProjectMapping_ExcludePatterns()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {},
            "mappings": [
                {
                    "from": "src/Aspire.Hosting.{name}/**",
                    "to": "tests/Aspire.Hosting.{name}.Tests/",
                    "exclude": ["src/Aspire.Hosting.Testing/**"]
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[]
        {
            "src/Aspire.Hosting.Redis/RedisExtensions.cs",
            "src/Aspire.Hosting.Testing/TestBuilder.cs"
        };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Hosting.Redis.Tests/", testProjects);
        Assert.DoesNotContain("tests/Aspire.Hosting.Testing.Tests/", testProjects);
    }

    [Fact]
    public void Evaluate_ProjectMapping_MultipleMappingsMatchSameFile()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {},
            "mappings": [
                {"from": "src/**", "to": "tests/All.Tests/"},
                {"from": "src/Components/{name}/**", "to": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[] { "src/Components/Aspire.Redis/Client.cs" };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Equal(2, testProjects.Count);
        Assert.Contains("tests/All.Tests/", testProjects);
        Assert.Contains("tests/Aspire.Redis.Tests/", testProjects);
    }

    [Fact]
    public void Evaluate_ProjectMapping_PatternWithoutCaptureGroup()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {},
            "mappings": [
                {"from": "src/Dashboard/**", "to": "tests/Dashboard.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[] { "src/Dashboard/Components/Chart.cs" };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Single(testProjects);
        Assert.Contains("tests/Dashboard.Tests/", testProjects);
    }

    [Fact]
    public void Evaluate_ProjectMapping_BatchResolutionReturnsUniqueProjects()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {},
            "mappings": [
                {"from": "src/Components/{name}/**", "to": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        // Multiple files in the same component should resolve to the same test project
        var changedFiles = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "src/Components/Aspire.Redis/Extensions.cs",
            "src/Components/Aspire.Redis/Options.cs"
        };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Redis.Tests/", testProjects);
    }

    #endregion

    #region Source-to-Test Mapping Scenarios

    [Fact]
    public void Evaluate_PlaygroundChange_TriggeredViaMapping()
    {
        var configJson = """
        {
            "ignore": ["**/*.md"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": [
                {"from": "playground/**", "to": "tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var testProjects = resolver.ResolveAllTestProjects(["playground/TestShop/Foo.cs"]);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj", testProjects);
    }

    [Fact]
    public void Evaluate_TemplateContentChange_TriggeredViaMapping()
    {
        var configJson = """
        {
            "ignore": ["**/*.md"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"],
                    "exclude": ["src/Aspire.ProjectTemplates/**"]
                }
            },
            "mappings": [
                {"from": "src/Aspire.ProjectTemplates/**", "to": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var testProjects = resolver.ResolveAllTestProjects(["src/Aspire.ProjectTemplates/templates/Foo.json"]);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", testProjects);
    }

    [Fact]
    public void Evaluate_SourceToTestMapping_ResolvesToCsprojPath()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {},
            "mappings": [
                {"from": "playground/**", "to": "tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj"},
                {"from": "src/Aspire.ProjectTemplates/**", "to": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var testProjects = resolver.ResolveAllTestProjects(["playground/TestShop/Foo.cs", "src/Aspire.ProjectTemplates/templates/Bar.json"]);

        Assert.Equal(2, testProjects.Count);
        Assert.All(testProjects, p => Assert.EndsWith(".csproj", p));
    }

    #endregion

    #region Simplified Category Tests

    [Fact]
    public void Evaluate_ExtensionOnly_SetsRunExtensionTrue()
    {
        var configJson = """
        {
            "ignore": ["**/*.md"],
            "jobCategories": {
                "extension": {
                    "when": ["extension/**"]
                },
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var changedFiles = new[] { "extension/package.json" };
        var (_, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        var (categories, _) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);

        Assert.True(categories["extension"]);
        Assert.False(categories["integrations"]);
    }

    [Fact]
    public void Evaluate_RemovedCategories_NotPresent()
    {
        // Config without templates, endtoend, playground categories
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {
                "cli_e2e": { "when": ["src/Aspire.Cli/**"] },
                "extension": { "when": ["extension/**"] },
                "polyglot": { "when": [".github/workflows/polyglot-validation/**"] },
                "integrations": { "when": ["src/**"] }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var (categories, _) = categoryMapper.GetCategoriesTriggeredByFiles(["src/Aspire.Hosting.Redis/Foo.cs"]);

        Assert.DoesNotContain("templates", categories.Keys);
        Assert.DoesNotContain("endtoend", categories.Keys);
        Assert.DoesNotContain("playground", categories.Keys);
        Assert.True(categories["integrations"]);
    }

    #endregion

    #region Path Normalization Tests

    [Fact]
    public void Evaluate_PathNormalization_BackslashToForwardSlash()
    {
        var configJson = """
        {
            "ignore": [],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        // Windows-style paths should be normalized
        var changedFiles = new[] { @"src\Components\Aspire.Redis\Client.cs" };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Single(matchedFiles);
    }

    #endregion

    #region TriggerAll Category Tests

    [Fact]
    public void Evaluate_TriggerAllPaths_Detected()
    {
        var configJson = """
        {
            "ignore": [],
            "runEverything": ["global.json", "Directory.Build.props", "tests/Shared/**"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);

        // Test various critical files
        Assert.True(criticalDetector.IsCriticalFile("global.json", out _));
        Assert.True(criticalDetector.IsCriticalFile("Directory.Build.props", out _));
        Assert.True(criticalDetector.IsCriticalFile("tests/Shared/TestHelper.cs", out _));

        // Non-critical files
        Assert.False(criticalDetector.IsCriticalFile("src/SomeFile.cs", out _));
    }

    [Fact]
    public void Evaluate_TriggerAllPaths_FindFirstCriticalFile()
    {
        var configJson = """
        {
            "ignore": [],
            "runEverything": ["global.json", "*.slnx"],
            "jobCategories": {},
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);

        var result = criticalDetector.FindFirstCriticalFile(["global.json"]);

        Assert.NotNull(result.File);
        Assert.Equal("global.json", result.File);
    }

    #endregion

    #region Mixed Scenario Tests

    [Fact]
    public void Evaluate_MixedScenario_IgnoredAndActiveFiles()
    {
        var configJson = """
        {
            "ignore": ["**/*.md", "docs/**"],
            "jobCategories": {
                "cli": {
                    "when": ["src/Aspire.Cli/**"]
                },
                "extension": {
                    "when": ["extension/**"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        var changedFiles = new[]
        {
            "README.md",
            "docs/api.md",
            "src/Aspire.Cli/Program.cs",
            "extension/package.json"
        };

        var (ignoredFiles, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);

        Assert.Equal(2, ignoredFiles.Count);
        Assert.Equal(2, activeFiles.Count);
        Assert.True(categories["cli"]);
        Assert.True(categories["extension"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void Evaluate_RealWorldScenario_DashboardChange()
    {
        var configJson = """
        {
            "ignore": ["**/*.md", "docs/**", ".github/**", "eng/**"],
            "runEverything": ["global.json", "Directory.Build.props", "*.slnx", "src/Aspire.Hosting/**", "tests/Shared/**"],
            "jobCategories": {
                "integrations": {
                    "when": ["src/**", "tests/Aspire.*.Tests/**"],
                    "exclude": ["src/Aspire.Cli/**", "src/Aspire.ProjectTemplates/**"]
                },
                "cli_e2e": {
                    "when": ["src/Aspire.Cli/**", "tests/Aspire.Cli.EndToEnd.Tests/**"]
                },
                "templates": {
                    "when": ["src/Aspire.ProjectTemplates/**", "tests/Aspire.Templates.Tests/**"]
                },
                "extension": {
                    "when": ["extension/**"]
                }
            },
            "mappings": [
                {"from": "src/Components/{name}/**", "to": "tests/{name}.Tests/"},
                {"from": "src/Aspire.Dashboard/**", "to": "tests/Aspire.Dashboard.Tests/"},
                {"from": "tests/{name}.Tests/**", "to": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);
        var categoryMapper = new CategoryMapper(config.JobCategories);
        var projectResolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[]
        {
            "src/Aspire.Dashboard/Components/Layout.razor",
            "tests/Aspire.Dashboard.Tests/LayoutTests.cs"
        };

        // Step 1: Filter
        var (_, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        Assert.Equal(2, activeFiles.Count);

        // Step 2: No critical files
        var criticalFile = criticalDetector.FindFirstCriticalFile(activeFiles);
        Assert.Null(criticalFile.File);

        // Step 3: Category mapping
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);
        Assert.True(categories["integrations"]);
        Assert.False(categories["cli_e2e"]);
        Assert.False(categories["templates"]);
        Assert.False(categories["extension"]);
        Assert.Equal(2, matchedFiles.Count);

        // Step 4: Project mapping
        var testProjects = projectResolver.ResolveAllTestProjects(activeFiles);
        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Dashboard.Tests/", testProjects);
    }

    #endregion

    #region CI Trigger Pattern Coverage Tests

    /// <summary>
    /// Verifies that every file type previously covered by github-ci-trigger-patterns.txt
    /// is correctly handled by the test-selection-rules.audit.json ignorePaths.
    /// This ensures we can safely rely on the test selection system as the single
    /// source of truth for CI skip decisions.
    /// </summary>
    [Fact]
    public void Evaluate_AllCiTriggerPatterns_CoveredByIgnorePaths()
    {
        // Load the REAL audit config, which carries the broad ignore rules.
        var configPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var configJson = File.ReadAllText(configPath);
        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);
        var categoryMapper = new CategoryMapper(config.JobCategories);

        // Representative file paths for every pattern in the old github-ci-trigger-patterns.txt:
        //   eng/testing/github-ci-trigger-patterns.txt  (self)
        //   **.md                                       (all markdown, recursive)
        //   eng/pipelines/**
        //   eng/test-configuration.json
        //   .github/instructions/**
        //   .github/skills/**
        //   .github/workflows/apply-test-attributes.yml
        //   .github/workflows/backmerge-release.yml
        //   .github/workflows/backport.yml
        //   .github/workflows/dogfood-comment.yml
        //   .github/workflows/generate-api-diffs.yml
        //   .github/workflows/generate-ats-diffs.yml
        //   .github/workflows/labeler-*.yml
        //   .github/workflows/markdownlint*.yml
        //   .github/workflows/pr-review-needed.yml
        //   .github/workflows/refresh-manifests.yml
        //   .github/workflows/reproduce-flaky-tests.yml
        //   .github/workflows/specialized-test-runner.yml
        //   .github/workflows/tests-outerloop.yml
        //   .github/workflows/tests-quarantine.yml
        //   .github/workflows/update-*.yml
        var ciTriggerPatternFiles = new[]
        {
            // Self-reference
            "eng/testing/github-ci-trigger-patterns.txt",

            // Markdown at root
            "README.md",
            "SECURITY.md",

            // Markdown nested in directories (**.md pattern)
            "src/Aspire.Hosting.Redis/README.md",
            "docs/getting-started.md",
            "docs/api/overview.md",

            // Engineering pipelines
            "eng/pipelines/build.yml",
            "eng/pipelines/test/integration.yml",

            // Engineering config
            "eng/test-configuration.json",

            // GitHub instructions and skills
            ".github/instructions/xmldoc.instructions.md",
            ".github/instructions/hosting-readme.instructions.md",
            ".github/skills/cli-e2e-testing.md",
            ".github/skills/test-management.md",

            // Specific workflow files
            ".github/workflows/apply-test-attributes.yml",
            ".github/workflows/backmerge-release.yml",
            ".github/workflows/backport.yml",
            ".github/workflows/dogfood-comment.yml",
            ".github/workflows/generate-api-diffs.yml",
            ".github/workflows/generate-ats-diffs.yml",
            ".github/workflows/labeler-promote.yml",
            ".github/workflows/labeler-train.yml",
            ".github/workflows/markdownlint.yml",
            ".github/workflows/markdownlint-problem-matcher.yml",
            ".github/workflows/pr-review-needed.yml",
            ".github/workflows/refresh-manifests.yml",
            ".github/workflows/reproduce-flaky-tests.yml",
            ".github/workflows/specialized-test-runner.yml",
            ".github/workflows/tests-outerloop.yml",
            ".github/workflows/tests-quarantine.yml",
            ".github/workflows/update-baselines.yml",
            ".github/workflows/update-dependencies.yml",

            // Workflow files NOT in the old trigger patterns (ci.yml, tests.yml)
            // but still ignored by test-selection-rules because workflow changes
            // don't affect test outcomes
            ".github/workflows/ci.yml",
            ".github/workflows/tests.yml",
        };

        // ALL of these files should be ignored
        var (ignoredFiles, activeFiles) = ignoreFilter.SplitFiles(ciTriggerPatternFiles);

        Assert.Empty(activeFiles);
        Assert.Equal(ciTriggerPatternFiles.Length, ignoredFiles.Count);

        // None should be critical (trigger-all) paths
        var criticalFile = criticalDetector.FindFirstCriticalFile(ciTriggerPatternFiles);
        Assert.Null(criticalFile.File);

        // No categories should be triggered
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(Array.Empty<string>());
        Assert.Empty(matchedFiles);
        foreach (var category in categories)
        {
            Assert.False(category.Value, $"Category '{category.Key}' should not be triggered for CI-skip files");
        }
    }

    /// <summary>
    /// Verifies that doc-only PRs (the most common CI-skip scenario) produce
    /// no active files and would result in all_skipped=true.
    /// </summary>
    [Fact]
    public void Evaluate_DocOnlyPr_AllSkipped()
    {
        var configPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var configJson = File.ReadAllText(configPath);
        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);

        // Typical doc-only PR
        var changedFiles = new[]
        {
            "README.md",
            "docs/conditional-tests-run.md",
            "src/Components/Aspire.Redis/README.md",
        };

        var (_, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        Assert.Empty(activeFiles);

        var criticalFile = criticalDetector.FindFirstCriticalFile(changedFiles);
        Assert.Null(criticalFile.File);
    }

    /// <summary>
    /// Verifies that workflow-only PRs are correctly ignored since workflow
    /// changes don't affect test outcomes.
    /// </summary>
    [Fact]
    public void Evaluate_WorkflowOnlyPr_AllSkipped()
    {
        var configPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var configJson = File.ReadAllText(configPath);
        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.Ignore);

        var changedFiles = new[]
        {
            ".github/workflows/ci.yml",
            ".github/workflows/tests.yml",
            ".github/actions/check-changed-files/action.yml",
        };

        var (_, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        Assert.Empty(activeFiles);
    }

    [Fact]
    public void Evaluate_RealConfig_TemplateGateSupportFiles_MapToTemplateTests()
    {
        var configPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.json");
        var configJson = File.ReadAllText(configPath);
        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.Mappings);

        var changedFiles = new[]
        {
            "tests/Aspire.Templates.Tests/TemplateSmokeTests.cs",
            "tests/Shared/TemporaryRepo.cs",
            "tests/workloads.proj",
            "tests/Directory.Build.props",
            "Directory.Build.targets",
            "eng/Versions.props",
            "global.json",
        };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Contains("tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", testProjects);
    }

    [Fact]
    public void Evaluate_RealConfig_ComponentChanges_ActiveDoesNotIgnore_AuditMapsToIntegrations()
    {
        var activeConfigPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.json");
        var activeConfig = TestSelectorConfig.LoadFromJson(File.ReadAllText(activeConfigPath));
        var activeIgnoreFilter = new IgnorePathFilter(activeConfig.Ignore);

        var auditConfigPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var auditConfig = TestSelectorConfig.LoadFromJson(File.ReadAllText(auditConfigPath));
        var auditIgnoreFilter = new IgnorePathFilter(auditConfig.Ignore);
        var auditCategoryMapper = new CategoryMapper(auditConfig.JobCategories);

        var changedFiles = new[]
        {
            "src/Components/Aspire.Pomelo.EntityFrameworkCore.MySql/Aspire.Pomelo.EntityFrameworkCore.MySql.csproj"
        };

        // The active pilot config carries no ignore rules, so a component change is NOT ignored:
        // it stays active and, being unmapped, forces a full run rather than a silent narrow.
        var (_, activeFiles) = activeIgnoreFilter.SplitFiles(changedFiles);
        Assert.Single(activeFiles);

        // The audit config maps the same change to the integrations category for future promotion.
        var (_, auditFiles) = auditIgnoreFilter.SplitFiles(changedFiles);
        Assert.Single(auditFiles);

        var (auditCategories, matchedFiles) = auditCategoryMapper.GetCategoriesTriggeredByFiles(auditFiles);
        Assert.True(auditCategories["integrations"]);
        Assert.Single(matchedFiles);
    }

    [Fact]
    public async Task Evaluate_RealActiveConfig_TemplateSupportChanges_UseMappingsOnly()
    {
        var repoRoot = FindRepoRoot();
        var configPath = Path.Combine(repoRoot, "eng", "scripts", "test-selection-rules.json");
        var config = TestSelectorConfig.LoadFromJson(File.ReadAllText(configPath));

        var result = await TestEvaluator.EvaluateAsync(
            config,
            [
                "tests/Aspire.Templates.Tests/TemplateSmokeTests.cs",
                "tests/Shared/TemplatesTesting/EnvironmentVariables.cs"
            ],
            solution: "Aspire.slnx",
            fromRef: null,
            toRef: null,
            workingDir: repoRoot,
            ciEnvironment: "GitHub",
            verbose: false);

        Assert.False(result.RunAllTests);
        Assert.Equal("selective_mappings_only", result.Reason);
        Assert.Equal(["tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"], result.AffectedTestProjects);
        Assert.Empty(result.DotnetAffectedProjects);
        // integrations is never a key in Categories; its run_integrations boolean is derived from the
        // selected-project count in WriteGitHubOutput, and one project was selected here.
        Assert.DoesNotContain(TestSelectorConfig.IntegrationsCategory, result.Categories.Keys);
        Assert.NotEmpty(result.AffectedTestProjects);
    }

    [Fact]
    public async Task Evaluate_RealActiveConfig_MixedTemplateAndSourceChange_RunsAll()
    {
        // Regression guard for the active pilot: a PR that touches a template file *and* a
        // non-template source file must run the full suite. The non-template file is unmatched
        // and forces RunAll, so its tests are never silently dropped in favor of templates-only
        // scope. Re-introducing a catch-all ignore (or otherwise ignoring the source file) would
        // flip RunAllTests back to false and regress coverage.
        var repoRoot = FindRepoRoot();
        var configPath = Path.Combine(repoRoot, "eng", "scripts", "test-selection-rules.json");
        var config = TestSelectorConfig.LoadFromJson(File.ReadAllText(configPath));

        var result = await TestEvaluator.EvaluateAsync(
            config,
            [
                "tests/Aspire.Templates.Tests/TemplateSmokeTests.cs",
                "src/Aspire.Hosting.Redis/RedisBuilder.cs"
            ],
            solution: "Aspire.slnx",
            fromRef: null,
            toRef: null,
            workingDir: repoRoot,
            ciEnvironment: "GitHub",
            verbose: false);

        Assert.True(result.RunAllTests);
        Assert.Contains("Unmatched files", result.Reason);
    }

    [Fact]
    public async Task Evaluate_RescueRespectsCategoryExcludePaths_FileStaysIgnored()
    {
        // Regression: previously the rescue logic took the *union* of all category
        // triggerPaths without consulting per-category excludePaths. A file that
        // textually matched a category's triggerPath glob — but was excluded from
        // that category — would still be rescued from ignorePaths and then fall
        // through to fallback_unmatched / RunAll, which is strictly worse than
        // leaving it ignored. Rescue must only fire when at least one category
        // *would actually trigger* on the file (triggerPath match AND no
        // excludePath match).
        var configJson = """
        {
            "ignore": ["**/*.md"],
            "runEverything": [],
            "jobCategories": {
                "integrations": {
                    "when": ["tests/Aspire.*.Tests/**"],
                    "exclude": ["**/*.md"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);

        var result = await TestEvaluator.EvaluateAsync(
            config,
            ["tests/Aspire.Foo.Tests/README.md"],
            solution: "Aspire.slnx",
            fromRef: null,
            toRef: null,
            workingDir: Directory.GetCurrentDirectory(),
            ciEnvironment: "github-actions",
            verbose: false);

        Assert.False(result.RunAllTests);
        Assert.Equal("all_ignored", result.Reason);
        // The file stayed ignored, so nothing was selected; run_integrations is count-derived and
        // false here (integrations is never placed in Categories).
        Assert.DoesNotContain(TestSelectorConfig.IntegrationsCategory, result.Categories.Keys);
        Assert.Empty(result.AffectedTestProjects);
    }

    [Fact]
    public async Task Evaluate_RescueStillBringsBackFilesThatActuallyFireACategory()
    {
        // Inverse of the previous test: when an ignored file matches a category's
        // triggerPath AND is NOT in that category's excludePaths, it must still be
        // rescued back to active. This is the original motivating case — the
        // polyglot validation workflow file lives under .github/workflows/** (the
        // blanket workflow ignore) but is the entire trigger surface for the
        // polyglot category, so it must rescue.
        var configJson = """
        {
            "ignore": [".github/workflows/**"],
            "runEverything": [],
            "jobCategories": {
                "polyglot": {
                    "when": [".github/workflows/polyglot-validation.yml"]
                }
            },
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);

        var result = await TestEvaluator.EvaluateAsync(
            config,
            [".github/workflows/polyglot-validation.yml"],
            solution: "Aspire.slnx",
            fromRef: null,
            toRef: null,
            workingDir: Directory.GetCurrentDirectory(),
            ciEnvironment: "github-actions",
            verbose: false);

        Assert.True(result.Categories["polyglot"]);
        Assert.NotEqual("all_ignored", result.Reason);
    }

    [Fact]
    public void Evaluate_TriggerAllPaths_BareFilename_MatchesNestedPaths()
    {
        // Bare-filename rule: patterns like "Directory.Build.props" or "global.json"
        // (no path separator) match the file by name at any depth, not just at the
        // repo root. The user-facing rule intent is that touching ANY
        // Directory.Build.props (root, src/, tests/, src/Aspire.Foo/) trips
        // trigger-all. Raw FileSystemGlobbing.Matcher only matches root-level by
        // default, so the analyzers must normalize bare-filename patterns by
        // prefixing "**/" before handing them to the Matcher.
        var configJson = """
        {
            "ignore": [],
            "runEverything": ["global.json", "Directory.Build.props", "Directory.Packages.props", "*.slnx"],
            "jobCategories": {},
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.RunEverything);

        Assert.True(criticalDetector.IsCriticalFile("global.json", out _));
        Assert.True(criticalDetector.IsCriticalFile("Directory.Build.props", out _));
        Assert.True(criticalDetector.IsCriticalFile("Aspire.slnx", out _));

        Assert.True(criticalDetector.IsCriticalFile("src/Directory.Build.props", out _));
        Assert.True(criticalDetector.IsCriticalFile("tests/Directory.Build.props", out _));
        Assert.True(criticalDetector.IsCriticalFile("tests/Directory.Packages.props", out _));
        Assert.True(criticalDetector.IsCriticalFile("src/Aspire.Hosting/Directory.Build.props", out _));
        Assert.True(criticalDetector.IsCriticalFile("some/very/deep/path/global.json", out _));

        Assert.False(criticalDetector.IsCriticalFile("src/SomeFile.cs", out _));
        Assert.False(criticalDetector.IsCriticalFile("global-config.txt", out _));
    }

    [Fact]
    public void Evaluate_IgnorePaths_BareFilename_MatchesNestedPaths()
    {
        // Same bare-filename rule applies to ignorePaths: a pattern like
        // ".editorconfig" should ignore that file anywhere in the tree, not just
        // at the repo root.
        var configJson = """
        {
            "ignore": [".editorconfig", ".gitignore", "*.sln"],
            "jobCategories": {},
            "mappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var filter = new IgnorePathFilter(config.Ignore);

        Assert.True(filter.ShouldIgnore(".editorconfig"));
        Assert.True(filter.ShouldIgnore("src/.editorconfig"));
        Assert.True(filter.ShouldIgnore("tests/Aspire.Cli.Tests/.editorconfig"));
        Assert.True(filter.ShouldIgnore("Aspire.sln"));
        Assert.True(filter.ShouldIgnore("tools/Other.sln"));

        Assert.False(filter.ShouldIgnore("src/Foo.cs"));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Aspire.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repository root (Aspire.slnx)");
    }

    #endregion

    #region Matched-But-Zero Guard Tests

    // The "matched-but-zero" guard runs all tests when the integrations category matched a
    // change but dotnet-affected attributed it to no project at all (e.g. a non-MSBuild-input
    // file under a covered source area). Without it, that change would silently select zero
    // integration test projects. These tests drive CheckMatchedButZeroProjects directly because
    // forcing real dotnet-affected to return an empty set would require a non-input-file git
    // fixture, which is fragile and slow.

    private const string MatchedButZeroConfigJson = """
    {
        "ignore": [],
        "jobCategories": {
            "integrations": { "when": ["src/**"] },
            "polyglot": { "when": [".github/workflows/polyglot-validation.yml"] }
        },
        "mappings": []
    }
    """;

    [Fact]
    public void CheckMatchedButZeroProjects_IntegrationsMatchedZeroProjects_RunsAll()
    {
        var config = TestSelectorConfig.LoadFromJson(MatchedButZeroConfigJson);
        var logger = new DiagnosticLogger(false);
        var categories = new Dictionary<string, bool> { ["integrations"] = true, ["polyglot"] = false };

        var result = TestEvaluator.CheckMatchedButZeroProjects(
            logger, config, categories,
            affectedProjects: [],
            allTestProjects: [],
            activeFiles: ["src/Aspire.Hosting.Redis/non-input-asset.txt"],
            ignoredFiles: []);

        Assert.NotNull(result);
        Assert.True(result.RunAllTests);
        // integrations is never placed in Categories (its run_<name> boolean is count-derived in
        // WriteGitHubOutput); RunAllTests=true is what makes run_integrations true on this path.
        Assert.DoesNotContain(TestSelectorConfig.IntegrationsCategory, result.Categories.Keys);
        Assert.True(result.Categories["polyglot"]);
        Assert.Contains("src/Aspire.Hosting.Redis/non-input-asset.txt", result.ChangedFiles);
    }

    [Fact]
    public void CheckMatchedButZeroProjects_AffectedProjectsPresent_DoesNotRunAll()
    {
        // dotnet-affected saw the change but it resolved to no test project (e.g. it only
        // pulled in a restricted opt-out project, or a source project with no dependent
        // test). That is a deliberate outcome, not a blind spot, so the guard must not fire.
        var config = TestSelectorConfig.LoadFromJson(MatchedButZeroConfigJson);
        var logger = new DiagnosticLogger(false);
        var categories = new Dictionary<string, bool> { ["integrations"] = true, ["polyglot"] = false };

        var result = TestEvaluator.CheckMatchedButZeroProjects(
            logger, config, categories,
            affectedProjects: ["src/Aspire.Hosting.Redis/Aspire.Hosting.Redis.csproj"],
            allTestProjects: [],
            activeFiles: ["src/Aspire.Hosting.Redis/RedisBuilderExtensions.cs"],
            ignoredFiles: []);

        Assert.Null(result);
    }

    [Fact]
    public void CheckMatchedButZeroProjects_OnlyBooleanCategoryMatched_DoesNotRunAll()
    {
        // A polyglot-only change (e.g. its workflow file) runs the polyglot job via its
        // run_polyglot boolean; an empty affected-projects matrix is expected there, so the
        // guard must not over-run by forcing run-all.
        var config = TestSelectorConfig.LoadFromJson(MatchedButZeroConfigJson);
        var logger = new DiagnosticLogger(false);
        var categories = new Dictionary<string, bool> { ["integrations"] = false, ["polyglot"] = true };

        var result = TestEvaluator.CheckMatchedButZeroProjects(
            logger, config, categories,
            affectedProjects: [],
            allTestProjects: [],
            activeFiles: [".github/workflows/polyglot-validation.yml"],
            ignoredFiles: []);

        Assert.Null(result);
    }

    [Fact]
    public void CheckMatchedButZeroProjects_TestProjectsResolved_DoesNotRunAll()
    {
        var config = TestSelectorConfig.LoadFromJson(MatchedButZeroConfigJson);
        var logger = new DiagnosticLogger(false);
        var categories = new Dictionary<string, bool> { ["integrations"] = true, ["polyglot"] = false };

        var result = TestEvaluator.CheckMatchedButZeroProjects(
            logger, config, categories,
            affectedProjects: ["src/Aspire.Hosting.Redis/Aspire.Hosting.Redis.csproj"],
            allTestProjects: ["tests/Aspire.Hosting.Redis.Tests/Aspire.Hosting.Redis.Tests.csproj"],
            activeFiles: ["src/Aspire.Hosting.Redis/RedisBuilderExtensions.cs"],
            ignoredFiles: []);

        Assert.Null(result);
    }

    #endregion

    #region Edges (runtime/build couplings)

    // An edge expresses a coupling dotnet-affected cannot follow because there is no
    // ProjectReference. The motivating case is the CLI end-to-end tests, which consume a built CLI
    // archive at runtime, so a change under src/Aspire.Cli/** (or the archive build under
    // eng/clipack/**) must still select tests/Aspire.Cli.EndToEnd.Tests even though nothing
    // references it. Edges carry an optional category label; run_<category> is then a pure
    // projection over the selected set, never a parallel glob re-match (the D7 contract).

    private const string CliE2eEdgeConfigJson = """
    {
        "ignore": [],
        "runEverything": [],
        "testProjectPatterns": { "include": ["tests/**/*.Tests.csproj"], "exclude": [] },
        "mappings": [
            { "from": "src/Aspire.Cli/**", "to": "tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj" }
        ],
        "edges": [
            {
                "from": "src/Aspire.Cli/**",
                "to": "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
                "type": "runtime",
                "category": "cli_e2e"
            }
        ]
    }
    """;

    [Fact]
    public async Task EndToEnd_RuntimeEdge_SelectsCliE2E()
    {
        // The changed CLI source file is resolved by a mapping (so dotnet-affected is skipped) AND
        // fires the runtime edge. The mapped unit-test project and the edge's end-to-end project
        // must both end up in AffectedTestProjects. REVERT-RED: delete the edge and
        // Aspire.Cli.EndToEnd.Tests is silently dropped (it has no ProjectReference).
        var config = TestSelectorConfig.LoadFromJson(CliE2eEdgeConfigJson);

        var result = await TestEvaluator.EvaluateAsync(
            config,
            ["src/Aspire.Cli/Program.cs"],
            solution: "Aspire.slnx",
            fromRef: null,
            toRef: null,
            workingDir: Directory.GetCurrentDirectory(),
            ciEnvironment: "github-actions",
            verbose: false);

        Assert.False(result.RunAllTests);
        Assert.Equal("selective_mappings_only", result.Reason);
        Assert.Contains("tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj", result.AffectedTestProjects);
        Assert.Contains("tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj", result.AffectedTestProjects);
    }

    [Fact]
    public async Task RunCliE2E_TrueIff_CliE2EProjectSelected()
    {
        // The D7 contract: run_cli_e2e is a projection over the selected set, so the boolean is true
        // exactly when the cli_e2e test project is selected — they can never disagree. REVERT-RED:
        // re-deriving run_cli_e2e by re-matching selected test-project paths against a category's
        // globs (the old UpdateCategoriesFromTestProjects) lets the boolean and the matrix drift.
        const string cliE2eProject = "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj";

        var fired = await TestEvaluator.EvaluateAsync(
            TestSelectorConfig.LoadFromJson(CliE2eEdgeConfigJson),
            ["src/Aspire.Cli/Program.cs"],
            solution: "Aspire.slnx", fromRef: null, toRef: null,
            workingDir: Directory.GetCurrentDirectory(), ciEnvironment: "github-actions", verbose: false);

        Assert.Equal(fired.AffectedTestProjects.Contains(cliE2eProject), fired.Categories["cli_e2e"]);
        Assert.True(fired.Categories["cli_e2e"]);

        // A mapping-only change that does not match the edge's from-glob: the project is not
        // selected and run_cli_e2e is false, again in lock-step.
        const string mappingOnlyConfigJson = """
        {
            "ignore": [],
            "runEverything": [],
            "testProjectPatterns": { "include": ["tests/**/*.Tests.csproj"], "exclude": [] },
            "mappings": [
                { "from": "src/Aspire.Dashboard/**", "to": "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj" }
            ],
            "edges": [
                {
                    "from": "src/Aspire.Cli/**",
                    "to": "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
                    "type": "runtime",
                    "category": "cli_e2e"
                }
            ]
        }
        """;

        var notFired = await TestEvaluator.EvaluateAsync(
            TestSelectorConfig.LoadFromJson(mappingOnlyConfigJson),
            ["src/Aspire.Dashboard/DashboardWebApplication.cs"],
            solution: "Aspire.slnx", fromRef: null, toRef: null,
            workingDir: Directory.GetCurrentDirectory(), ciEnvironment: "github-actions", verbose: false);

        Assert.Equal(notFired.AffectedTestProjects.Contains(cliE2eProject), notFired.Categories["cli_e2e"]);
        Assert.False(notFired.Categories["cli_e2e"]);
    }

    [Theory]
    [InlineData("src/Aspire.Cli/Program.cs")]
    [InlineData("src/Aspire.Hosting/DistributedApplication.cs")]
    [InlineData("eng/clipack/build.proj")]
    [InlineData("tests/Aspire.Cli.EndToEnd.Tests/CliEndToEndTests.cs")]
    public void RealAuditConfig_CliE2eEdge_SelectsCliE2eTestProject(string changedFile)
    {
        // Pins the audit ruleset: a change that fires either cli_e2e edge (CLI source, hosting core,
        // the archive build, or a direct edit to the end-to-end tests) resolves the
        // Aspire.Cli.EndToEnd.Tests project through the edge. That project has no ProjectReference
        // and an inferDeps:false opt-out, so without the edge it would be silently dropped.
        var auditConfigPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var auditConfig = TestSelectorConfig.LoadFromJson(File.ReadAllText(auditConfigPath));

        var edgeResolver = new ProjectMappingResolver(auditConfig.Edges
            .Where(e => string.Equals(e.Category, "cli_e2e", StringComparison.Ordinal))
            .Select(e => new SelectionMapping { From = e.From, To = e.To, Exclude = e.Exclude }));

        var resolved = edgeResolver.ResolveAllTestProjects([changedFile]);

        Assert.Contains("tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj", resolved);
    }

    [Theory]
    [InlineData("extension/src/extension.ts", true)]
    [InlineData("src/Aspire.Hosting/DistributedApplication.cs", true)]
    [InlineData("src/Aspire.Dashboard/DashboardWebApplication.cs", false)]
    public void JobCategory_Extension_TriggersOnExtensionPathsOnly(string changedFile, bool expectedTriggered)
    {
        // Standalone job categories (extension/polyglot) keep when/exclude trigger semantics and run
        // as dedicated jobs gated by run_<category>, independent of the affected-projects matrix.
        var auditConfigPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var auditConfig = TestSelectorConfig.LoadFromJson(File.ReadAllText(auditConfigPath));

        var mapper = new CategoryMapper(auditConfig.JobCategories);
        var (categories, _) = mapper.GetCategoriesTriggeredByFiles([changedFile]);

        Assert.Equal(expectedTriggered, categories["extension"]);
    }

    #endregion
}
