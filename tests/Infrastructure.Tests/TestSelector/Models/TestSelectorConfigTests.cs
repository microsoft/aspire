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
            "ignorePaths": ["**/*.md", "docs/**"],
            "sourceToTestMappings": [
                {
                    "source": "src/Components/{name}/**",
                    "test": "tests/{name}.Tests/",
                    "exclude": ["src/Components/Internal/**"]
                }
            ],
            "categories": {
                "core": {
                    "description": "Critical paths",

                    "triggerPaths": ["global.json", "Directory.Build.props"]
                },
                "integrations": {
                    "description": "Integration tests",
                    "triggerPaths": ["src/**"],
                    "excludePaths": ["src/Aspire.Cli/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal("https://example.com/schema.json", config.Schema);
        Assert.Equal(2, config.IgnorePaths.Count);
        Assert.Contains("**/*.md", config.IgnorePaths);
        Assert.Single(config.SourceToTestMappings);
        Assert.Equal("src/Components/{name}/**", config.SourceToTestMappings[0].Source);
        Assert.Equal("tests/{name}.Tests/", config.SourceToTestMappings[0].Test);
        Assert.Single(config.SourceToTestMappings[0].Exclude);
        Assert.Equal(2, config.Categories.Count);
        Assert.Equal(2, config.Categories["core"].TriggerPaths.Count);
    }

    [Fact]
    public void LoadFromJson_CaseInsensitivePropertyNames()
    {
        var json = """
        {
            "IGNOREPATHS": ["**/*.md"],
            "Categories": {
                "test": {
                    "TRIGGERPATHS": ["src/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.IgnorePaths);
        Assert.Single(config.Categories);
        Assert.Single(config.Categories["test"].TriggerPaths);
    }

    [Fact]
    public void LoadFromJson_ToleratesCommentsAndTrailingCommas()
    {
        var json = """
        {
            // This is a comment
            "ignorePaths": [
                "**/*.md",
                "docs/**", // trailing comma
            ],
            "categories": {},
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(2, config.IgnorePaths.Count);
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

        Assert.Empty(config.IgnorePaths);
        Assert.Empty(config.SourceToTestMappings);
        Assert.Empty(config.Categories);
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
            "categories": {
                "core": {
                    "description": "Critical paths",
                    "triggerPaths": [
                        "global.json",
                        "Directory.Build.props"
                    ]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Categories);
        var coreCategory = config.Categories["core"];
        Assert.Equal("Critical paths", coreCategory.Description);
        Assert.Equal(2, coreCategory.TriggerPaths.Count);
    }

    [Fact]
    public void LoadFromJson_CategoryWithExcludePaths_ParsesCorrectly()
    {
        var json = """
        {
            "categories": {
                "integrations": {
                    "triggerPaths": ["src/**", "tests/**"],
                    "excludePaths": ["src/Aspire.Cli/**", "tests/E2E/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var category = config.Categories["integrations"];
        Assert.Equal(2, category.TriggerPaths.Count);
        Assert.Equal(2, category.ExcludePaths.Count);
        Assert.Contains("src/Aspire.Cli/**", category.ExcludePaths);
    }

    [Fact]
    public void LoadFromJson_SourceToTestMappingsMultiple_ParsesAll()
    {
        var json = """
        {
            "sourceToTestMappings": [
                {"source": "src/Components/{name}/**", "test": "tests/{name}.Tests/"},
                {"source": "src/Aspire.Hosting.{name}/**", "test": "tests/Aspire.Hosting.{name}.Tests/"},
                {"source": "tests/{name}.Tests/**", "test": "tests/{name}.Tests/"}
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.SourceToTestMappings.Count);
        Assert.Equal("tests/{name}.Tests/", config.SourceToTestMappings[0].Test);
        Assert.Equal("tests/Aspire.Hosting.{name}.Tests/", config.SourceToTestMappings[1].Test);
    }

    [Fact]
    public void LoadFromJson_ProjectMappingWithExclude_ParsesCorrectly()
    {
        var json = """
        {
            "sourceToTestMappings": [
                {
                    "source": "src/Aspire.Hosting.{name}/**",
                    "test": "tests/Aspire.Hosting.{name}.Tests/",
                    "exclude": ["src/Aspire.Hosting.Testing/**", "src/Aspire.Hosting.Internal/**"]
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var mapping = config.SourceToTestMappings[0];
        Assert.Equal(2, mapping.Exclude.Count);
        Assert.Contains("src/Aspire.Hosting.Testing/**", mapping.Exclude);
    }

    [Fact]
    public void LoadFromJson_CategoryDefaults_AreCorrect()
    {
        var json = """
        {
            "categories": {
                "minimal": {}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var category = config.Categories["minimal"];
        Assert.Null(category.Description);
        Assert.Empty(category.TriggerPaths);
        Assert.Empty(category.ExcludePaths);
    }

    [Fact]
    public void LoadFromJson_PackageOrArchiveProducingProjects_ParsesCorrectly()
    {
        var json = """
        {
            "packageOrArchiveProducingProjects": ["eng/clipack/**", "src/Installer/**"],
            "categories": {}
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
            "ignorePaths": ["**/*.md", "", "docs/**"],
            "categories": {
                "test": {
                    "triggerPaths": ["src/**", ""]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        // Empty strings are parsed but should be handled by the evaluator
        Assert.Equal(3, config.IgnorePaths.Count);
        Assert.Contains("", config.IgnorePaths);
        Assert.Equal(2, config.Categories["test"].TriggerPaths.Count);
    }

    [Fact]
    public void LoadFromJson_ExtremelyLongPaths_AreParsed()
    {
        var longPath = "src/" + string.Join("/", Enumerable.Repeat("verylongdirectory", 50)) + "/**";

        var json = $$"""
        {
            "categories": {
                "test": {
                    "triggerPaths": ["{{longPath}}"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Categories["test"].TriggerPaths);
        Assert.Equal(longPath, config.Categories["test"].TriggerPaths[0]);
    }

    [Fact]
    public void LoadFromJson_UnicodeCharactersInPaths_AreParsed()
    {
        var json = """
        {
            "ignorePaths": ["docs/日本語/**", "src/中文/**"],
            "categories": {
                "i18n": {
                    "triggerPaths": ["src/locales/français/**", "src/locales/español/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(2, config.IgnorePaths.Count);
        Assert.Contains("docs/日本語/**", config.IgnorePaths);
        Assert.Equal(2, config.Categories["i18n"].TriggerPaths.Count);
        Assert.Contains("src/locales/français/**", config.Categories["i18n"].TriggerPaths);
    }

    [Fact]
    public void LoadFromJson_SpecialRegexCharactersInPatterns_AreParsed()
    {
        var json = """
        {
            "ignorePaths": ["file[1].txt", "test(1).md", "data+backup/**"],
            "categories": {
                "test": {
                    "triggerPaths": ["src/$special/**", "src/name^caret/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.IgnorePaths.Count);
        Assert.Contains("file[1].txt", config.IgnorePaths);
        Assert.Contains("test(1).md", config.IgnorePaths);
        Assert.Equal(2, config.Categories["test"].TriggerPaths.Count);
    }

    [Fact]
    public void LoadFromJson_CategoryWithEmptyTriggerPaths_IsValid()
    {
        var json = """
        {
            "categories": {
                "empty": {
                    "description": "Category with empty trigger paths",
                    "triggerPaths": []
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Categories);
        Assert.Empty(config.Categories["empty"].TriggerPaths);
    }

    [Fact]
    public void LoadFromJson_CategoryWithOnlyExcludePaths_IsValid()
    {
        var json = """
        {
            "categories": {
                "excludeOnly": {
                    "description": "Category with only exclude paths",
                    "excludePaths": ["src/Internal/**", "src/Private/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        var category = config.Categories["excludeOnly"];
        Assert.Empty(category.TriggerPaths);
        Assert.Equal(2, category.ExcludePaths.Count);
    }

    [Fact]
    public void LoadFromJson_ProjectMappingWithEmptyExclude_IsValid()
    {
        var json = """
        {
            "sourceToTestMappings": [
                {
                    "source": "src/Components/{name}/**",
                    "test": "tests/{name}.Tests/",
                    "exclude": []
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.SourceToTestMappings);
        Assert.Empty(config.SourceToTestMappings[0].Exclude);
    }

    [Fact]
    public void LoadFromJson_ProjectMappingWithoutExclude_DefaultsToEmpty()
    {
        var json = """
        {
            "sourceToTestMappings": [
                {
                    "source": "src/{name}/**",
                    "test": "tests/{name}.Tests/"
                }
            ]
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.SourceToTestMappings);
        Assert.Empty(config.SourceToTestMappings[0].Exclude);
    }

    [Fact]
    public void LoadFromJson_ManyCategories_AllParsed()
    {
        var json = """
        {
            "categories": {
                "cat1": {"triggerPaths": ["src/a/**"]},
                "cat2": {"triggerPaths": ["src/b/**"]},
                "cat3": {"triggerPaths": ["src/c/**"]},
                "cat4": {"triggerPaths": ["src/d/**"]},
                "cat5": {"triggerPaths": ["src/e/**"]},
                "cat6": {"triggerPaths": ["src/f/**"]},
                "cat7": {"triggerPaths": ["src/g/**"]},
                "cat8": {"triggerPaths": ["src/h/**"]},
                "cat9": {"triggerPaths": ["src/i/**"]},
                "cat10": {"triggerPaths": ["src/j/**"]}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(10, config.Categories.Count);
    }

    [Fact]
    public void LoadFromJson_MultipleCategories_AllParsed()
    {
        var json = """
        {
            "categories": {
                "core": {
                    "triggerPaths": ["global.json"]
                },
                "infra": {
                    "triggerPaths": ["Directory.Build.props"]
                },
                "normal": {
                    "triggerPaths": ["src/**"]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.Categories.Count);
        Assert.Single(config.Categories["core"].TriggerPaths);
        Assert.Single(config.Categories["infra"].TriggerPaths);
        Assert.Single(config.Categories["normal"].TriggerPaths);
    }

    [Fact]
    public void LoadFromJson_PatternWithMixedGlobAndLiteral_IsParsed()
    {
        var json = """
        {
            "categories": {
                "mixed": {
                    "triggerPaths": [
                        "src/Aspire.Hosting.*/Resources/**/*.bicep",
                        "**/*Tests.cs",
                        "specific/path/to/file.json"
                    ]
                }
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Equal(3, config.Categories["mixed"].TriggerPaths.Count);
    }

    [Fact]
    public void LoadFromJson_WhitespaceInPaths_IsPreserved()
    {
        var json = """
        {
            "ignorePaths": ["docs/My Documents/**", "src/Path With Spaces/**"],
            "categories": {}
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Contains("docs/My Documents/**", config.IgnorePaths);
        Assert.Contains("src/Path With Spaces/**", config.IgnorePaths);
    }

    [Fact]
    public void LoadFromJson_DuplicatePatternsInArray_AreAllParsed()
    {
        var json = """
        {
            "ignorePaths": ["**/*.md", "**/*.md", "docs/**", "docs/**"],
            "categories": {}
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        // Duplicates are preserved in the config (deduplication is caller's responsibility)
        Assert.Equal(4, config.IgnorePaths.Count);
    }

    [Fact]
    public void LoadFromJson_DuplicateCategoryNames_LastWins()
    {
        // JSON spec: duplicate keys use last value
        var json = """
        {
            "categories": {
                "test": {"triggerPaths": ["first/**"]},
                "test": {"triggerPaths": ["second/**"]}
            }
        }
        """;

        var config = TestSelectorConfig.LoadFromJson(json);

        Assert.Single(config.Categories);
        Assert.Contains("second/**", config.Categories["test"].TriggerPaths);
    }

    #endregion
}
