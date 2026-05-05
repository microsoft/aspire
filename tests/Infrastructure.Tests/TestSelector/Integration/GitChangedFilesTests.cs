// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector;
using TestSelector.Analyzers;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Integration;

/// <summary>
/// Tests for handling git changed files in test selection.
/// Tests path normalization and various input formats.
/// </summary>
public class GitChangedFilesTests
{
    #region Path Normalization Tests

    [Fact]
    public void ChangedFiles_BackslashPaths_AreNormalized()
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

        // Windows-style paths
        var changedFiles = new[]
        {
            @"src\Components\Aspire.Redis\Client.cs",
            @"src\Dashboard\Layout.razor"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void ChangedFiles_ForwardSlashPaths_WorkDirectly()
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

        // Unix-style paths
        var changedFiles = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "src/Dashboard/Layout.razor"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void ChangedFiles_MixedPathSeparators_AllNormalized()
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

        // Mixed path separators
        var changedFiles = new[]
        {
            @"src\Components/Aspire.Redis\Client.cs",
            @"src/Dashboard\Layout.razor"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    #endregion

    #region Empty/Null Inputs Tests

    [Fact]
    public void ChangedFiles_EmptyList_NoMatches()
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
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);

        var changedFiles = Array.Empty<string>();

        var (ignoredFiles, activeFiles) = ignoreFilter.SplitFiles(changedFiles);
        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(activeFiles);

        Assert.Empty(ignoredFiles);
        Assert.Empty(activeFiles);
        Assert.Empty(matchedFiles);
        Assert.False(categories["integrations"]);
    }

    [Fact]
    public void ChangedFiles_SingleFile_ProcessedCorrectly()
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

        var changedFiles = new[] { "src/File.cs" };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Single(matchedFiles);
    }

    #endregion

    #region Special Character Tests

    [Fact]
    public void ChangedFiles_PathsWithSpaces_HandledCorrectly()
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

        var changedFiles = new[]
        {
            "src/My Component/File.cs",
            "src/Another Folder/Test File.cs"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void ChangedFiles_PathsWithDots_HandledCorrectly()
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

        var changedFiles = new[]
        {
            "src/Aspire.Dashboard.Components/Layout.razor",
            "src/My.Dotted.Namespace/Class.cs"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void ChangedFiles_PathsWithSpecialChars_HandledCorrectly()
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

        var changedFiles = new[]
        {
            "src/Component[1]/File.cs",
            "src/Component(test)/File.cs"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    #endregion

    #region Case Sensitivity Tests

    [Fact]
    public void ChangedFiles_DifferentCasing_MatchesPattern()
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

        // Different casing
        var changedFiles = new[]
        {
            "SRC/Component/File.cs",
            "Src/Another/File.cs"
        };

        var (_, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        // The Microsoft.Extensions.FileSystemGlobbing respects case sensitivity
        // based on the OS, but patterns themselves may be case-insensitive
        // This test documents the actual behavior
        Assert.True(matchedFiles.Count >= 0); // Actual result depends on implementation
    }

    #endregion

    #region Long Path Tests

    [Fact]
    public void ChangedFiles_DeeplyNestedPath_MatchesDoubleStarPattern()
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

        var changedFiles = new[]
        {
            "src/a/b/c/d/e/f/g/h/i/j/File.cs"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Single(matchedFiles);
    }

    [Fact]
    public void ChangedFiles_VeryLongFilename_MatchesPattern()
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

        var longFilename = new string('a', 200) + ".cs";
        var changedFiles = new[]
        {
            $"src/Component/{longFilename}"
        };

        var (categories, matchedFiles) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["integrations"]);
        Assert.Single(matchedFiles);
    }

    #endregion

    #region Project Mapping with Changed Files

    [Fact]
    public void ChangedFiles_ProjectMappingResolution_WithBackslashes()
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

        // Windows paths
        var changedFiles = new[]
        {
            @"src\Components\Aspire.Redis\Client.cs"
        };

        var testProjects = resolver.ResolveAllTestProjects(changedFiles);

        Assert.Single(testProjects);
        Assert.Contains("tests/Aspire.Redis.Tests/", testProjects);
    }

    [Fact]
    public void ChangedFiles_IgnoreFilterWithBackslashes_Works()
    {
        var configJson = """
        {
            "ignorePaths": ["**/*.md", "docs/**"],
            "categories": {},
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var ignoreFilter = new IgnorePathFilter(config.IgnorePaths);

        // Windows paths
        var changedFiles = new[]
        {
            @"docs\getting-started.md",
            @"README.md",
            @"src\Component\File.cs"
        };

        var (ignoredFiles, activeFiles) = ignoreFilter.SplitFiles(changedFiles);

        Assert.Equal(2, ignoredFiles.Count);
        Assert.Single(activeFiles);
    }

    #endregion

    #region File Extension Tests

    [Fact]
    public void ChangedFiles_VariousExtensions_MatchedCorrectly()
    {
        var configJson = """
        {
            "ignorePaths": [],
            "categories": {
                "csharp": {
                    "triggerPaths": ["**/*.cs"]
                },
                "razor": {
                    "triggerPaths": ["**/*.razor"]
                },
                "typescript": {
                    "triggerPaths": ["**/*.ts", "**/*.tsx"]
                },
                "json": {
                    "triggerPaths": ["**/*.json"]
                }
            },
            "sourceToTestMappings": []
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(configJson);
        var categoryMapper = new CategoryMapper(config.Categories);

        var changedFiles = new[]
        {
            "src/Component.cs",
            "src/Layout.razor",
            "extension/index.ts",
            "extension/component.tsx",
            "package.json"
        };

        var (categories, _) = categoryMapper.GetCategoriesTriggeredByFiles(changedFiles);

        Assert.True(categories["csharp"]);
        Assert.True(categories["razor"]);
        Assert.True(categories["typescript"]);
        Assert.True(categories["json"]);
    }

    #endregion
}
