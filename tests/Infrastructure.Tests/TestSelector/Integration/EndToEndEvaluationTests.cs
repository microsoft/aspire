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
            "ignorePaths": ["**/*.md", "docs/**", ".github/**"],
            "triggerAllPaths": ["global.json", "Directory.Build.props", "*.slnx"],
            "categories": {
                "integrations": {
                    "description": "Integration tests",
                    "triggerPaths": ["src/**", "tests/**"],
                    "excludePaths": ["src/Aspire.Cli/**"]
                },
                "cli_e2e": {
                    "description": "CLI end-to-end tests",
                    "triggerPaths": ["src/Aspire.Cli/**", "tests/Aspire.Cli.EndToEnd.Tests/**"]
                }
            },
            "sourceToTestMappings": [
                {"source": "src/Components/{name}/**", "test": "tests/{name}.Tests/"},
                {"source": "tests/{name}.Tests/**", "test": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);
        var categoryMapper = new CategoryMapper(config.Categories);
        var projectResolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": ["**/*.md"],
            "triggerAllPaths": ["global.json", "Directory.Build.props"],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);

        var changedFiles = new[] { "global.json", "src/SomeFile.cs" };

        var file = criticalDetector.FindFirstCriticalFile(changedFiles);

        Assert.Equal("global.json", file.File);
    }

    [Fact]
    public async Task Evaluate_AuditConfigOnlyChange_ReturnsNonApplyingResult()
    {
        var configJson = """
        {
            "ignorePaths": [],
            "categories": {
                "extension": {
                    "triggerPaths": ["extension/**"]
                }
            },
            "sourceToTestMappings": []
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
            "ignorePaths": ["**/*.md"],
            "categories": {
                "known": {
                    "triggerPaths": ["src/**", "tests/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": ["**/*.md", "docs/**", ".github/**", "eng/**"],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);

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
            "ignorePaths": [],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": [],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"],
                    "excludePaths": ["src/Aspire.Cli/**", "src/Aspire.ProjectTemplates/**"]
                },
                "cli": {
                    "triggerPaths": ["src/Aspire.Cli/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": [],
            "categories": {
                "hosting": {
                    "triggerPaths": ["src/Aspire.Hosting/**"]
                },
                "allsrc": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": [],
            "categories": {},
            "sourceToTestMappings": [
                {"source": "src/Components/{name}/**", "test": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": [],
            "categories": {},
            "sourceToTestMappings": [
                {
                    "source": "src/Aspire.Hosting.{name}/**",
                    "test": "tests/Aspire.Hosting.{name}.Tests/",
                    "exclude": ["src/Aspire.Hosting.Testing/**"]
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": [],
            "categories": {},
            "sourceToTestMappings": [
                {"source": "src/**", "test": "tests/All.Tests/"},
                {"source": "src/Components/{name}/**", "test": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": [],
            "categories": {},
            "sourceToTestMappings": [
                {"source": "src/Dashboard/**", "test": "tests/Dashboard.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": [],
            "categories": {},
            "sourceToTestMappings": [
                {"source": "src/Components/{name}/**", "test": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": ["**/*.md"],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": [
                {"source": "playground/**", "test": "tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

        var testProjects = resolver.ResolveAllTestProjects(["playground/TestShop/Foo.cs"]);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj", testProjects);
    }

    [Fact]
    public void Evaluate_TemplateContentChange_TriggeredViaMapping()
    {
        var configJson = """
        {
            "ignorePaths": ["**/*.md"],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"],
                    "excludePaths": ["src/Aspire.ProjectTemplates/**"]
                }
            },
            "sourceToTestMappings": [
                {"source": "src/Aspire.ProjectTemplates/**", "test": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

        var testProjects = resolver.ResolveAllTestProjects(["src/Aspire.ProjectTemplates/templates/Foo.json"]);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", testProjects);
    }

    [Fact]
    public void Evaluate_SourceToTestMapping_ResolvesToCsprojPath()
    {
        var configJson = """
        {
            "ignorePaths": [],
            "categories": {},
            "sourceToTestMappings": [
                {"source": "playground/**", "test": "tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj"},
                {"source": "src/Aspire.ProjectTemplates/**", "test": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
            "ignorePaths": ["**/*.md"],
            "categories": {
                "extension": {
                    "triggerPaths": ["extension/**"]
                },
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": [],
            "categories": {
                "cli_e2e": { "triggerPaths": ["src/Aspire.Cli/**"] },
                "extension": { "triggerPaths": ["extension/**"] },
                "polyglot": { "triggerPaths": [".github/workflows/polyglot-validation/**"] },
                "integrations": { "triggerPaths": ["src/**"] }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": [],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": [],
            "triggerAllPaths": ["global.json", "Directory.Build.props", "tests/Shared/**"],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);

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
            "ignorePaths": [],
            "triggerAllPaths": ["global.json", "*.slnx"],
            "categories": {},
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);

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
            "ignorePaths": ["**/*.md", "docs/**"],
            "categories": {
                "cli": {
                    "triggerPaths": ["src/Aspire.Cli/**"]
                },
                "extension": {
                    "triggerPaths": ["extension/**"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var categoryMapper = new CategoryMapper(config.Categories);

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
            "ignorePaths": ["**/*.md", "docs/**", ".github/**", "eng/**"],
            "triggerAllPaths": ["global.json", "Directory.Build.props", "*.slnx", "src/Aspire.Hosting/**", "tests/Shared/**"],
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**", "tests/Aspire.*.Tests/**"],
                    "excludePaths": ["src/Aspire.Cli/**", "src/Aspire.ProjectTemplates/**"]
                },
                "cli_e2e": {
                    "triggerPaths": ["src/Aspire.Cli/**", "tests/Aspire.Cli.EndToEnd.Tests/**"]
                },
                "templates": {
                    "triggerPaths": ["src/Aspire.ProjectTemplates/**", "tests/Aspire.Templates.Tests/**"]
                },
                "extension": {
                    "triggerPaths": ["extension/**"]
                }
            },
            "sourceToTestMappings": [
                {"source": "src/Components/{name}/**", "test": "tests/{name}.Tests/"},
                {"source": "src/Aspire.Dashboard/**", "test": "tests/Aspire.Dashboard.Tests/"},
                {"source": "tests/{name}.Tests/**", "test": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);
        var categoryMapper = new CategoryMapper(config.Categories);
        var projectResolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
    /// is correctly handled by the test-selection-rules.json ignorePaths.
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
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);
        var categoryMapper = new CategoryMapper(config.Categories);

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
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);
        var criticalDetector = new CriticalFileDetector(config.TriggerAllPaths);

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
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);

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
        var resolver = new ProjectMappingResolver(config.SourceToTestMappings);

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
    public void Evaluate_RealConfig_ComponentChanges_AreAuditOnly()
    {
        var activeConfigPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.json");
        var activeConfig = TestSelectorConfig.LoadFromJson(File.ReadAllText(activeConfigPath));
        var activeIgnoreFilter = new IgnorePathFilter(activeConfig.IgnorePaths);

        var auditConfigPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "test-selection-rules.audit.json");
        var auditConfig = TestSelectorConfig.LoadFromJson(File.ReadAllText(auditConfigPath));
        var auditIgnoreFilter = new IgnorePathFilter(auditConfig.IgnorePaths);
        var auditCategoryMapper = new CategoryMapper(auditConfig.Categories);

        var changedFiles = new[]
        {
            "src/Components/Aspire.Pomelo.EntityFrameworkCore.MySql/Aspire.Pomelo.EntityFrameworkCore.MySql.csproj"
        };

        var (_, activeFiles) = activeIgnoreFilter.SplitFiles(changedFiles);
        Assert.Empty(activeFiles);

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
                ".github/workflows/tests.yml",
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
        Assert.True(result.Categories["integrations"]);
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
}
