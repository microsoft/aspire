// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector;

public class CategoryMapperTests
{
    private static CategoryMapper CreateMapper()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["core"] = new CategoryConfig
            {
                Description = "Critical paths",
                TriggerPaths = ["global.json", "Directory.Build.props", "src/Aspire.Hosting/**"]
            },
            ["integrations"] = new CategoryConfig
            {
                Description = "Integration tests",
                TriggerPaths = ["src/Aspire.*/**", "src/Components/**", "tests/Aspire.*.Tests/**"],
                ExcludePaths = ["src/Aspire.Cli/**", "src/Aspire.ProjectTemplates/**"]
            },
            ["cli_e2e"] = new CategoryConfig
            {
                Description = "CLI E2E tests",
                TriggerPaths = ["src/Aspire.Cli/**", "tests/Aspire.Cli.EndToEnd.Tests/**"]
            },
            ["extension"] = new CategoryConfig
            {
                Description = "VS Code extension tests",
                TriggerPaths = ["extension/**"]
            }
        };

        return new CategoryMapper(categories);
    }

    [Theory]
    [InlineData("src/Components/Aspire.Redis/Client.cs", "integrations", true)]
    [InlineData("src/Aspire.Dashboard/Dashboard.cs", "integrations", true)]
    [InlineData("src/Aspire.Cli/Program.cs", "cli_e2e", true)]
    [InlineData("src/Aspire.Cli/Program.cs", "integrations", false)] // Excluded by excludePaths
    [InlineData("extension/package.json", "extension", true)]
    [InlineData("extension/src/index.ts", "extension", true)]
    [InlineData("global.json", "core", true)]
    [InlineData("docs/README.md", "integrations", false)]
    public void FileTriggersCategory_MatchesCorrectly(string filePath, string categoryName, bool expected)
    {
        var mapper = CreateMapper();

        var result = mapper.FileTriggersCategory(filePath, categoryName);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FileTriggersCategory_NonExistentCategory_ReturnsFalse()
    {
        var mapper = CreateMapper();

        var result = mapper.FileTriggersCategory("any/file.cs", "nonexistent");

        Assert.False(result);
    }

    [Theory]
    [InlineData("src\\Components\\Aspire.Redis\\Client.cs")]
    [InlineData("src/Components/Aspire.Redis/Client.cs")]
    public void FileTriggersCategory_NormalizesPath(string filePath)
    {
        var mapper = CreateMapper();

        var result = mapper.FileTriggersCategory(filePath, "integrations");

        Assert.True(result);
    }

    [Fact]
    public void GetCategoriesTriggeredByFiles_InitializesAllCategoriesFalse()
    {
        var mapper = CreateMapper();

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles([]);

        Assert.Equal(4, categories.Count);
        Assert.False(categories["core"]);
        Assert.False(categories["integrations"]);
        Assert.False(categories["cli_e2e"]);
        Assert.False(categories["extension"]);
        Assert.Empty(matchedFiles);
    }

    [Fact]
    public void GetCategoriesTriggeredByFiles_MarksMatchingCategoriesTrue()
    {
        var mapper = CreateMapper();

        var files = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "extension/package.json"
        };

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        Assert.True(categories["integrations"]);
        Assert.True(categories["extension"]);
        Assert.False(categories["core"]);
        Assert.False(categories["cli_e2e"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void GetCategoriesTriggeredByFiles_ExcludePathsRespected()
    {
        var mapper = CreateMapper();

        var files = new[]
        {
            "src/Aspire.Cli/Program.cs" // Matches cli_e2e, but excluded from integrations
        };

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        Assert.True(categories["cli_e2e"]);
        Assert.False(categories["integrations"]); // Excluded
        Assert.Single(matchedFiles);
    }

    [Fact]
    public void GetCategoriesTriggeredByFiles_ReturnsMatchedFiles()
    {
        var mapper = CreateMapper();

        var files = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "docs/README.md", // Doesn't match any category
            "extension/package.json"
        };

        var (_, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        Assert.Equal(2, matchedFiles.Count);
        Assert.Contains("src/Components/Aspire.Redis/Client.cs", matchedFiles);
        Assert.Contains("extension/package.json", matchedFiles);
        Assert.DoesNotContain("docs/README.md", matchedFiles);
    }

    [Fact]
    public void GetTriggeredCategories_ReturnsOnlyCategories()
    {
        var mapper = CreateMapper();

        var files = new[] { "src/Components/Aspire.Redis/Client.cs" };

        var categories = mapper.GetTriggeredCategories(files);

        Assert.Equal(4, categories.Count);
        Assert.True(categories["integrations"]);
    }

    [Fact]
    public void GetAllCategories_ReturnsAllConfiguredCategories()
    {
        var mapper = CreateMapper();

        var categories = mapper.GetAllCategories().ToList();

        Assert.Equal(4, categories.Count);
        Assert.Contains("core", categories);
        Assert.Contains("integrations", categories);
        Assert.Contains("cli_e2e", categories);
        Assert.Contains("extension", categories);
    }

    [Fact]
    public void GetCategoryConfig_ReturnsConfig()
    {
        var mapper = CreateMapper();

        var config = mapper.GetCategoryConfig("core");

        Assert.NotNull(config);
        Assert.Equal("Critical paths", config.Description);
    }

    [Fact]
    public void GetCategoryConfig_NonExistent_ReturnsNull()
    {
        var mapper = CreateMapper();

        var config = mapper.GetCategoryConfig("nonexistent");

        Assert.Null(config);
    }

    [Fact]
    public void CategoryMapper_EmptyCategories_NoMatches()
    {
        var mapper = new CategoryMapper([]);

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(["any/file.cs"]);

        Assert.Empty(categories);
        Assert.Empty(matchedFiles);
    }

    [Fact]
    public void GetCategoriesTriggeredByFiles_FileMatchesMultipleCategories()
    {
        var mapper = CreateMapper();

        // src/Aspire.Hosting/Host.cs matches both "core" and "integrations"
        var files = new[] { "src/Aspire.Hosting/Host.cs" };

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        Assert.True(categories["core"]);
        Assert.True(categories["integrations"]);
        Assert.Single(matchedFiles);
    }

    [Fact]
    public void GetCategoriesTriggeredByFiles_AllFilesMatch_AllCategoriesTriggered()
    {
        var mapper = CreateMapper();

        var files = new[]
        {
            "global.json",                          // core
            "src/Components/Aspire.Redis/Client.cs", // integrations
            "src/Aspire.Cli/Program.cs",            // cli_e2e
            "extension/package.json"                 // extension
        };

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        Assert.True(categories["core"]);
        Assert.True(categories["integrations"]);
        Assert.True(categories["cli_e2e"]);
        Assert.True(categories["extension"]);
        Assert.Equal(4, matchedFiles.Count);
    }

    #region Edge Case Tests

    [Fact]
    public void CategoryWithEmptyTriggerPaths_NeverMatches()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["empty"] = new CategoryConfig
            {
                Description = "Empty trigger paths",
                TriggerPaths = []
            }
        };

        var mapper = new CategoryMapper(categories);

        var (results, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(["any/file.cs", "src/test.cs"]);

        Assert.False(results["empty"]);
        Assert.Empty(matchedFiles);
    }

    [Fact]
    public void CategoryWithOnlyExcludePaths_NeverMatches()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["excludeOnly"] = new CategoryConfig
            {
                Description = "Only exclude paths",
                TriggerPaths = [],
                ExcludePaths = ["src/Internal/**", "src/Private/**"]
            }
        };

        var mapper = new CategoryMapper(categories);

        var (results, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(["src/Internal/File.cs", "src/Public/File.cs"]);

        Assert.False(results["excludeOnly"]);
        Assert.Empty(matchedFiles);
    }

    [Fact]
    public void OverlappingCategories_FileMatchesMultiple()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["broad"] = new CategoryConfig { TriggerPaths = ["src/**"] },
            ["specific"] = new CategoryConfig { TriggerPaths = ["src/Components/**"] },
            ["verySpecific"] = new CategoryConfig { TriggerPaths = ["src/Components/Aspire.Redis/**"] }
        };

        var mapper = new CategoryMapper(categories);

        var (results, _) = mapper.GetCategoriesTriggeredByFiles(["src/Components/Aspire.Redis/Client.cs"]);

        Assert.True(results["broad"]);
        Assert.True(results["specific"]);
        Assert.True(results["verySpecific"]);
    }

    [Fact]
    public void PatternPriority_ExcludeOverridesTrigger()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["test"] = new CategoryConfig
            {
                TriggerPaths = ["src/**"],
                ExcludePaths = ["src/Generated/**"]
            }
        };

        var mapper = new CategoryMapper(categories);

        // File in excluded path
        var result1 = mapper.FileTriggersCategory("src/Generated/Auto.cs", "test");
        // File not in excluded path
        var result2 = mapper.FileTriggersCategory("src/Manual/Code.cs", "test");

        Assert.False(result1);
        Assert.True(result2);
    }

    [Fact]
    public void GetCategoriesWithDetails_ReturnsMatchingPatterns()
    {
        var mapper = CreateMapper();

        var files = new[] { "src/Components/Aspire.Redis/Client.cs" };

        var result = mapper.GetCategoriesWithDetails(files);

        Assert.True(result.CategoryStatus["integrations"]);
        Assert.Single(result.CategoryMatches["integrations"]);
        Assert.Equal("src/Components/Aspire.Redis/Client.cs", result.CategoryMatches["integrations"][0].FilePath);
        Assert.Equal("src/Components/**", result.CategoryMatches["integrations"][0].MatchedPattern);
    }

    [Fact]
    public void GetCategoriesWithDetails_TracksAllMatchedFiles()
    {
        var mapper = CreateMapper();

        var files = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "extension/package.json",
            "docs/README.md" // Should not match
        };

        var result = mapper.GetCategoriesWithDetails(files);

        Assert.Equal(2, result.MatchedFiles.Count);
        Assert.Contains("src/Components/Aspire.Redis/Client.cs", result.MatchedFiles);
        Assert.Contains("extension/package.json", result.MatchedFiles);
        Assert.DoesNotContain("docs/README.md", result.MatchedFiles);
    }

    [Fact]
    public void SingleFileTriggersMultipleCategories_AllAreTracked()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["all"] = new CategoryConfig { TriggerPaths = ["**/*"] },
            ["csharp"] = new CategoryConfig { TriggerPaths = ["**/*.cs"] },
            ["src"] = new CategoryConfig { TriggerPaths = ["src/**"] }
        };

        var mapper = new CategoryMapper(categories);

        var (results, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(["src/Code.cs"]);

        Assert.True(results["all"]);
        Assert.True(results["csharp"]);
        Assert.True(results["src"]);
        Assert.Single(matchedFiles);
    }

    [Fact]
    public void LargeNumberOfFiles_AllProcessed()
    {
        var mapper = CreateMapper();

        var files = Enumerable.Range(0, 1000)
            .Select(i => $"src/Components/Aspire.Component{i}/File.cs")
            .ToList();

        var (categories, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        Assert.True(categories["integrations"]);
        Assert.Equal(1000, matchedFiles.Count);
    }

    [Fact]
    public void DuplicateFilesInInput_DeduplicatedInOutput()
    {
        var mapper = CreateMapper();

        var files = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "src/Components/Aspire.Redis/Client.cs",
            "src/Components/Aspire.Redis/Client.cs"
        };

        var (_, matchedFiles) = mapper.GetCategoriesTriggeredByFiles(files);

        // HashSet deduplicates
        Assert.Single(matchedFiles);
    }

    [Fact]
    public void SpecialCharactersInPath_HandledCorrectly()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["test"] = new CategoryConfig
            {
                TriggerPaths = ["src/**"]
            }
        };

        var mapper = new CategoryMapper(categories);

        var (results, matchedFiles) = mapper.GetCategoriesTriggeredByFiles([
            "src/My Project (1)/File.cs",
            "src/[Brackets]/File.cs",
            "src/Name+With+Plus/File.cs"
        ]);

        Assert.True(results["test"]);
        Assert.Equal(3, matchedFiles.Count);
    }

    [Fact]
    public void UnicodePathComponents_MatchCorrectly()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["i18n"] = new CategoryConfig
            {
                TriggerPaths = ["locales/**"]
            }
        };

        var mapper = new CategoryMapper(categories);

        var (results, matchedFiles) = mapper.GetCategoriesTriggeredByFiles([
            "locales/日本語/messages.json",
            "locales/中文/strings.json"
        ]);

        Assert.True(results["i18n"]);
        Assert.Equal(2, matchedFiles.Count);
    }

    [Fact]
    public void CategoryWithWildcardInMiddle_MatchesCorrectly()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["hosting"] = new CategoryConfig
            {
                TriggerPaths = ["src/Aspire.Hosting.*/Resources/**"]
            }
        };

        var mapper = new CategoryMapper(categories);

        Assert.True(mapper.FileTriggersCategory("src/Aspire.Hosting.Azure/Resources/bicep/main.bicep", "hosting"));
        Assert.True(mapper.FileTriggersCategory("src/Aspire.Hosting.Redis/Resources/config.json", "hosting"));
        Assert.False(mapper.FileTriggersCategory("src/Aspire.Hosting/Resources/file.txt", "hosting"));
    }

    [Fact]
    public void ExactFileMatch_WorksWithoutGlob()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["config"] = new CategoryConfig
            {
                TriggerPaths = ["global.json", "Directory.Build.props", "NuGet.config"]
            }
        };

        var mapper = new CategoryMapper(categories);

        Assert.True(mapper.FileTriggersCategory("global.json", "config"));
        Assert.True(mapper.FileTriggersCategory("Directory.Build.props", "config"));
        Assert.True(mapper.FileTriggersCategory("NuGet.config", "config"));
        Assert.False(mapper.FileTriggersCategory("src/global.json", "config")); // Different path
    }

    [Fact]
    public void ExtensionPattern_MatchesOnlySpecificExtension()
    {
        var categories = new Dictionary<string, CategoryConfig>
        {
            ["markdown"] = new CategoryConfig { TriggerPaths = ["**/*.md"] },
            ["csharp"] = new CategoryConfig { TriggerPaths = ["**/*.cs"] }
        };

        var mapper = new CategoryMapper(categories);

        Assert.True(mapper.FileTriggersCategory("docs/README.md", "markdown"));
        Assert.False(mapper.FileTriggersCategory("docs/README.md", "csharp"));
        Assert.True(mapper.FileTriggersCategory("src/Code.cs", "csharp"));
        Assert.False(mapper.FileTriggersCategory("src/Code.cs", "markdown"));
    }

    #endregion
}
