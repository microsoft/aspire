// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Models;

public class TestSelectorConfigTests
{
    [Fact]
    public void LoadFromJson_ValidConfig_ParsesAllProperties()
    {
        var json = """
        {
            "$schema": "https://example.com/schema.json",
            "ignore": ["**/*.md", "docs/**"],
            "mappings": [
                {
                    "from": "src/Components/{name}/**",
                    "to": "tests/{name}.Tests/",
                    "exclude": ["src/Components/Internal/**"]
                }
            ],
            "jobCategories": {
                "core": {

                    "when": ["global.json", "Directory.Build.props"]
                },
                "integrations": {
                    "when": ["src/**"],
                    "exclude": ["src/Aspire.Cli/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal("https://example.com/schema.json", config.Schema);
        Assert.Equal(2, config.Ignore.Count);
        Assert.Contains("**/*.md", config.Ignore);
        Assert.Single(config.Mappings);
        Assert.Single(config.Mappings[0].From);
        Assert.Equal("src/Components/{name}/**", config.Mappings[0].From[0]);
        Assert.Equal("tests/{name}.Tests/", config.Mappings[0].To);
        Assert.Single(config.Mappings[0].Exclude);
        Assert.Equal(2, config.JobCategories.Count);
        Assert.Equal(2, config.JobCategories["core"].When.Count);
    }

    [Fact]
    public void LoadFromJson_CaseInsensitivePropertyNames()
    {
        var json = """
        {
            "IGNORE": ["**/*.md"],
            "JOBCATEGORIES": {
                "test": {
                    "WHEN": ["src/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Ignore);
        Assert.Single(config.JobCategories);
        Assert.Single(config.JobCategories["test"].When);
    }

    [Fact]
    public void LoadFromJson_ToleratesCommentsAndTrailingCommas()
    {
        var json = """
        {
            // This is a comment
            "ignore": [
                "**/*.md",
                "docs/**", // trailing comma
            ],
            "jobCategories": {},
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(2, config.Ignore.Count);
    }

    [Fact]
    public void LoadFromJson_InvalidJson_ThrowsException()
    {
        var json = "{ invalid json }";

        Assert.ThrowsAny<Exception>(() => TestSelectorConfig.LoadFromJson(json));
    }

    [Fact]
    public void LoadFromJson_EmptyObject_ReturnsDefaults()
    {
        var json = "{}";

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Empty(config.Ignore);
        Assert.Empty(config.Mappings);
        Assert.Empty(config.JobCategories);
        Assert.Empty(config.PackageOrArchiveProducingProjects);
        Assert.Null(config.Schema);
    }

    [Fact]
    public void LoadFromJson_NullJson_ThrowsException()
    {
        Assert.ThrowsAny<Exception>(() => TestSelectorConfig.LoadFromJson(null!));
    }

    [Fact]
    public void LoadFromJson_CategoryWithTriggerPaths_ParsesCorrectly()
    {
        var json = """
        {
            "jobCategories": {
                "core": {
                    "when": [
                        "global.json",
                        "Directory.Build.props"
                    ]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.JobCategories);
        var coreCategory = config.JobCategories["core"];
        Assert.Equal(2, coreCategory.When.Count);
    }

    [Fact]
    public void LoadFromJson_CategoryWithExcludePaths_ParsesCorrectly()
    {
        var json = """
        {
            "jobCategories": {
                "integrations": {
                    "when": ["src/**", "tests/**"],
                    "exclude": ["src/Aspire.Cli/**", "tests/E2E/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var category = config.JobCategories["integrations"];
        Assert.Equal(2, category.When.Count);
        Assert.Equal(2, category.Exclude.Count);
        Assert.Contains("src/Aspire.Cli/**", category.Exclude);
    }

    [Fact]
    public void LoadFromJson_MappingsMultiple_ParsesAll()
    {
        var json = """
        {
            "mappings": [
                {"from": "src/Components/{name}/**", "to": "tests/{name}.Tests/"},
                {"from": "src/Aspire.Hosting.{name}/**", "to": "tests/Aspire.Hosting.{name}.Tests/"},
                {"from": "tests/{name}.Tests/**", "to": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.Mappings.Count);
        Assert.Equal("tests/{name}.Tests/", config.Mappings[0].To);
        Assert.Equal("tests/Aspire.Hosting.{name}.Tests/", config.Mappings[1].To);
    }

    [Fact]
    public void LoadFromJson_SourceAsArray_CollectsAllPatterns()
    {
        // The 'source' field accepts either a single string or an array of strings.
        // Use the array form to collapse N entries that map to the same test project
        // into a single mapping. Both shapes must produce identical resolution behavior.
        var json = """
        {
            "mappings": [
                {
                    "from": [
                        "eng/Publishing.props",
                        "eng/Signing.props",
                        "eng/scripts/pack-cli-npm-package.ps1"
                    ],
                    "to": "tests/Infrastructure.Tests/Infrastructure.Tests.csproj"
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Mappings);
        Assert.Equal(3, config.Mappings[0].From.Count);
        Assert.Equal("eng/Publishing.props", config.Mappings[0].From[0]);
        Assert.Equal("eng/Signing.props", config.Mappings[0].From[1]);
        Assert.Equal("eng/scripts/pack-cli-npm-package.ps1", config.Mappings[0].From[2]);
        Assert.Equal("tests/Infrastructure.Tests/Infrastructure.Tests.csproj", config.Mappings[0].To);
    }

    [Fact]
    public void LoadFromJson_SourceAsString_NormalizesToSingleElementList()
    {
        var json = """
        {
            "mappings": [
                {"from": "src/Aspire.ProjectTemplates/**", "to": "tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Mappings);
        Assert.Single(config.Mappings[0].From);
        Assert.Equal("src/Aspire.ProjectTemplates/**", config.Mappings[0].From[0]);
    }

    [Fact]
    public void LoadFromJson_ProjectMappingWithExclude_ParsesCorrectly()
    {
        var json = """
        {
            "mappings": [
                {
                    "from": "src/Aspire.Hosting.{name}/**",
                    "to": "tests/Aspire.Hosting.{name}.Tests/",
                    "exclude": ["src/Aspire.Hosting.Testing/**", "src/Aspire.Hosting.Internal/**"]
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var mapping = config.Mappings[0];
        Assert.Equal(2, mapping.Exclude.Count);
        Assert.Contains("src/Aspire.Hosting.Testing/**", mapping.Exclude);
    }

    [Fact]
    public void LoadFromJson_CategoryDefaults_AreCorrect()
    {
        var json = """
        {
            "jobCategories": {
                "minimal": {}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var category = config.JobCategories["minimal"];
        Assert.Empty(category.When);
        Assert.Empty(category.Exclude);
    }

    [Fact]
    public void LoadFromJson_PackageOrArchiveProducingProjects_ParsesCorrectly()
    {
        var json = """
        {
            "packageOrArchiveProducingProjects": ["eng/clipack/**", "src/Installer/**"],
            "jobCategories": {}
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(2, config.PackageOrArchiveProducingProjects.Count);
        Assert.Contains("eng/clipack/**", config.PackageOrArchiveProducingProjects);
        Assert.Contains("src/Installer/**", config.PackageOrArchiveProducingProjects);
    }

    #region Edge Case Tests

    [Fact]
    public void LoadFromJson_EmptyPatternsInArray_AreParsed()
    {
        var json = """
        {
            "ignore": ["**/*.md", "", "docs/**"],
            "jobCategories": {
                "test": {
                    "when": ["src/**", ""]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        // Ignore is a plain list, so empty entries survive parsing (the evaluator guards against
        // them). When/exclude globs use StringOrStringArrayConverter, which drops empty patterns at
        // parse time — an empty glob is never a meaningful trigger and could only over-match.
        Assert.Equal(3, config.Ignore.Count);
        Assert.Contains("", config.Ignore);
        Assert.Equal(["src/**"], config.JobCategories["test"].When);
    }

    [Fact]
    public void LoadFromJson_ExtremelyLongPaths_AreParsed()
    {
        var longPath = "src/" + string.Join("/", Enumerable.Repeat("verylongdirectory", 50)) + "/**";

        var json = $$"""
        {
            "jobCategories": {
                "test": {
                    "when": ["{{longPath}}"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.JobCategories["test"].When);
        Assert.Equal(longPath, config.JobCategories["test"].When[0]);
    }

    [Fact]
    public void LoadFromJson_UnicodeCharactersInPaths_AreParsed()
    {
        var json = """
        {
            "ignore": ["docs/日本語/**", "src/中文/**"],
            "jobCategories": {
                "i18n": {
                    "when": ["src/locales/français/**", "src/locales/español/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(2, config.Ignore.Count);
        Assert.Contains("docs/日本語/**", config.Ignore);
        Assert.Equal(2, config.JobCategories["i18n"].When.Count);
        Assert.Contains("src/locales/français/**", config.JobCategories["i18n"].When);
    }

    [Fact]
    public void LoadFromJson_SpecialRegexCharactersInPatterns_AreParsed()
    {
        var json = """
        {
            "ignore": ["file[1].txt", "test(1).md", "data+backup/**"],
            "jobCategories": {
                "test": {
                    "when": ["src/$special/**", "src/name^caret/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.Ignore.Count);
        Assert.Contains("file[1].txt", config.Ignore);
        Assert.Contains("test(1).md", config.Ignore);
        Assert.Equal(2, config.JobCategories["test"].When.Count);
    }

    [Fact]
    public void LoadFromJson_CategoryWithEmptyTriggerPaths_IsValid()
    {
        var json = """
        {
            "jobCategories": {
                "empty": {
                    "when": []
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.JobCategories);
        Assert.Empty(config.JobCategories["empty"].When);
    }

    [Fact]
    public void LoadFromJson_CategoryWithOnlyExcludePaths_IsValid()
    {
        var json = """
        {
            "jobCategories": {
                "excludeOnly": {
                    "exclude": ["src/Internal/**", "src/Private/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var category = config.JobCategories["excludeOnly"];
        Assert.Empty(category.When);
        Assert.Equal(2, category.Exclude.Count);
    }

    [Fact]
    public void LoadFromJson_ProjectMappingWithEmptyExclude_IsValid()
    {
        var json = """
        {
            "mappings": [
                {
                    "from": "src/Components/{name}/**",
                    "to": "tests/{name}.Tests/",
                    "exclude": []
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Mappings);
        Assert.Empty(config.Mappings[0].Exclude);
    }

    [Fact]
    public void LoadFromJson_ProjectMappingWithoutExclude_DefaultsToEmpty()
    {
        var json = """
        {
            "mappings": [
                {
                    "from": "src/{name}/**",
                    "to": "tests/{name}.Tests/"
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Mappings);
        Assert.Empty(config.Mappings[0].Exclude);
    }

    [Fact]
    public void LoadFromJson_ManyCategories_AllParsed()
    {
        var json = """
        {
            "jobCategories": {
                "cat1": {"when": ["src/a/**"]},
                "cat2": {"when": ["src/b/**"]},
                "cat3": {"when": ["src/c/**"]},
                "cat4": {"when": ["src/d/**"]},
                "cat5": {"when": ["src/e/**"]},
                "cat6": {"when": ["src/f/**"]},
                "cat7": {"when": ["src/g/**"]},
                "cat8": {"when": ["src/h/**"]},
                "cat9": {"when": ["src/i/**"]},
                "cat10": {"when": ["src/j/**"]}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(10, config.JobCategories.Count);
    }

    [Fact]
    public void LoadFromJson_MultipleCategories_AllParsed()
    {
        var json = """
        {
            "jobCategories": {
                "core": {
                    "when": ["global.json"]
                },
                "infra": {
                    "when": ["Directory.Build.props"]
                },
                "normal": {
                    "when": ["src/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.JobCategories.Count);
        Assert.Single(config.JobCategories["core"].When);
        Assert.Single(config.JobCategories["infra"].When);
        Assert.Single(config.JobCategories["normal"].When);
    }

    [Fact]
    public void LoadFromJson_PatternWithMixedGlobAndLiteral_IsParsed()
    {
        var json = """
        {
            "jobCategories": {
                "mixed": {
                    "when": [
                        "src/Aspire.Hosting.*/Resources/**/*.bicep",
                        "**/*Tests.cs",
                        "specific/path/to/file.json"
                    ]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.JobCategories["mixed"].When.Count);
    }

    [Fact]
    public void LoadFromJson_WhitespaceInPaths_IsPreserved()
    {
        var json = """
        {
            "ignore": ["docs/My Documents/**", "src/Path With Spaces/**"],
            "jobCategories": {}
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Contains("docs/My Documents/**", config.Ignore);
        Assert.Contains("src/Path With Spaces/**", config.Ignore);
    }

    [Fact]
    public void LoadFromJson_DuplicatePatternsInArray_AreAllParsed()
    {
        var json = """
        {
            "ignore": ["**/*.md", "**/*.md", "docs/**", "docs/**"],
            "jobCategories": {}
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        // Duplicates are preserved in the config (deduplication is caller's responsibility)
        Assert.Equal(4, config.Ignore.Count);
    }

    [Fact]
    public void LoadFromJson_DuplicateCategoryNames_LastWins()
    {
        // JSON spec: duplicate keys use last value
        var json = """
        {
            "jobCategories": {
                "test": {"when": ["first/**"]},
                "test": {"when": ["second/**"]}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.JobCategories);
        Assert.Contains("second/**", config.JobCategories["test"].When);
    }

    [Fact]
    public void LoadFromJson_RunEverything_ParsesCorrectly()
    {
        var json = """
        {
            "runEverything": ["global.json", "Directory.Build.props", "eng/**"]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.RunEverything.Count);
        Assert.Contains("global.json", config.RunEverything);
    }

    [Fact]
    public void LoadFromJson_Edges_ParsesAllFields()
    {
        var json = """
        {
            "edges": [
                {
                    "from": ["src/Aspire.Cli/**", "eng/clipack/**"],
                    "to": "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
                    "type": "runtime",
                    "category": "cli_e2e",
                    "exclude": ["**/api/*.txt"]
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Edges);
        var edge = config.Edges[0];
        Assert.Equal(2, edge.From.Count);
        Assert.Equal("src/Aspire.Cli/**", edge.From[0]);
        Assert.Equal("tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj", edge.To);
        Assert.Equal("runtime", edge.Type);
        Assert.Equal("cli_e2e", edge.Category);
        Assert.Single(edge.Exclude);
    }

    [Fact]
    public void LoadFromJson_EdgeDefaults_AreCorrect()
    {
        // 'type' defaults to "build"; 'category' is optional (null when absent).
        var json = """
        {
            "edges": [
                {"from": "tests/Aspire.Cli.EndToEnd.Tests/**", "to": "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var edge = config.Edges[0];
        Assert.Equal("build", edge.Type);
        Assert.Null(edge.Category);
        Assert.Single(edge.From);
        Assert.Empty(edge.Exclude);
    }

    [Fact]
    public void LoadFromJson_InferDeps_ParsesPathToBoolMap()
    {
        var json = """
        {
            "inferDeps": {
                "tests/Aspire.Acquisition.Tests/Aspire.Acquisition.Tests.csproj": false,
                "tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj": true
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(2, config.InferDeps.Count);
        Assert.False(config.InferDeps["tests/Aspire.Acquisition.Tests/Aspire.Acquisition.Tests.csproj"]);
        Assert.True(config.InferDeps["tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj"]);
    }

    [Fact]
    public void GetSuppressedTestProjects_ReturnsOnlyFalseKeys_Sorted()
    {
        // Only inferDeps:false projects opt out of broad sweeps; true/absent entries are no-ops.
        // Keys are listed out of order to prove the result is sorted.
        var json = """
        {
            "inferDeps": {
                "tests/Zeta.Tests/Zeta.Tests.csproj": false,
                "tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj": true,
                "tests/Alpha.Tests/Alpha.Tests.csproj": false
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(
            ["tests/Alpha.Tests/Alpha.Tests.csproj", "tests/Zeta.Tests/Zeta.Tests.csproj"],
            config.GetSuppressedTestProjects());
    }

    [Fact]
    public void GetSuppressedTestProjects_Empty_WhenNoFalseEntries()
    {
        var config = TestSelectorConfig.LoadFromJson("""{"inferDeps": {"tests/A/A.csproj": true}}""");

        Assert.Empty(config.GetSuppressedTestProjects());
    }

    [Fact]
    public void LoadFromJson_JobCategoryWhenAsString_NormalizesToSingleElementList()
    {
        // 'when' accepts either a single string or an array of strings (same converter as mapping/edge 'from').
        var json = """
        {
            "jobCategories": {
                "extension": {"when": "extension/**"}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.JobCategories["extension"].When);
        Assert.Equal("extension/**", config.JobCategories["extension"].When[0]);
    }

    [Fact]
    public void LoadFromJson_EmptyObject_NewCollectionsDefaultEmpty()
    {
        var config = TestSelectorConfig.LoadFromJson("{}");

        Assert.Empty(config.Edges);
        Assert.Empty(config.InferDeps);
        Assert.Empty(config.JobCategories);
        Assert.Empty(config.RunEverything);
    }

    #endregion
}
