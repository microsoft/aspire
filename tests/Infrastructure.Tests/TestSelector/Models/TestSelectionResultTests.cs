// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Models;

public class TestSelectionResultTests
{
    [Fact]
    public void CriticalPath_SetsRunAllTrueAndReason()
    {
        var result = TestSelectionResult.CriticalPath("eng/Build.props", "eng/**/*.props");

        Assert.True(result.RunAllTests);
        Assert.Equal("critical_path", result.Reason);
        Assert.Equal("eng/Build.props", result.TriggerFile);
        Assert.Equal("eng/**/*.props", result.TriggerPattern);
    }

    [Fact]
    public void NoChanges_SetsRunAllFalseWithNoChangesReason()
    {
        var result = TestSelectionResult.NoChanges();

        Assert.False(result.RunAllTests);
        Assert.Equal("no_changes", result.Reason);
        Assert.Null(result.TriggerFile);
        Assert.Null(result.TriggerPattern);
    }

    [Fact]
    public void AllIgnored_IncludesIgnoredFilesList()
    {
        var ignoredFiles = new List<string> { "README.md", "docs/guide.md" };
        var result = TestSelectionResult.AllIgnored(ignoredFiles);

        Assert.False(result.RunAllTests);
        Assert.Equal("all_ignored", result.Reason);
        Assert.Equal(2, result.IgnoredFiles.Count);
        Assert.Contains("README.md", result.IgnoredFiles);
        Assert.Contains("docs/guide.md", result.IgnoredFiles);
    }

    [Fact]
    public void WithError_SetsRunAllTrueConservatively()
    {
        var result = TestSelectionResult.WithError("dotnet-affected failed");

        Assert.True(result.RunAllTests);
        Assert.Equal("error", result.Reason);
        Assert.Equal("dotnet-affected failed", result.Error);
    }

    [Fact]
    public void NonApplying_SetsRunAllFalseWithProvidedReason()
    {
        var result = TestSelectionResult.NonApplying("audit_config_only");

        Assert.False(result.RunAllTests);
        Assert.Equal("audit_config_only", result.Reason);
        Assert.Null(result.TriggerFile);
        Assert.Null(result.TriggerPattern);
    }

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var result = TestSelectionResult.CriticalPath("file.cs", "**/*.cs");
        result.Categories["integrations"] = true;
        result.AffectedTestProjects.Add("tests/Test1.csproj");

        var json = result.ToJson();

        var parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);

        Assert.True(parsed.RootElement.GetProperty("runAllTests").GetBoolean());
        Assert.Equal("critical_path", parsed.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void ToJson_OmitsNullProperties()
    {
        var result = TestSelectionResult.NoChanges();

        var json = result.ToJson();

        var parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.TryGetProperty("triggerFile", out _));
        Assert.False(parsed.RootElement.TryGetProperty("triggerPattern", out _));
        Assert.False(parsed.RootElement.TryGetProperty("error", out _));
        Assert.False(parsed.RootElement.TryGetProperty("nugetDependentTests", out _));
    }

    [Fact]
    public void ToJson_IncludesCategories()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories =
            {
                ["integrations"] = true,
                ["dashboard"] = false,
                ["cli"] = true
            }
        };

        var json = result.ToJson();
        var parsed = JsonDocument.Parse(json);

        var categories = parsed.RootElement.GetProperty("categories");
        Assert.True(categories.GetProperty("integrations").GetBoolean());
        Assert.False(categories.GetProperty("dashboard").GetBoolean());
        Assert.True(categories.GetProperty("cli").GetBoolean());
    }

    [Fact]
    public void ToJson_IncludesNuGetDependentTestsWhenSet()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            NuGetDependentTests = new NuGetDependentTestsInfo
            {
                Triggered = true,
                Reason = "Packable project affected",
                AffectedPackableProjects = ["src/Aspire.Hosting/Aspire.Hosting.csproj"],
                Projects = ["tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj"]
            }
        };

        var json = result.ToJson();
        var parsed = JsonDocument.Parse(json);

        var nugetTests = parsed.RootElement.GetProperty("nugetDependentTests");
        Assert.True(nugetTests.GetProperty("triggered").GetBoolean());
    }

    [Fact]
    public void ToJson_IsIndented()
    {
        var result = TestSelectionResult.NoChanges();
        var json = result.ToJson();

        Assert.Contains("\n", json);
        Assert.Contains("  ", json);
    }

    [Fact]
    public void WriteGitHubOutput_IncludesRunIntegrations_WhenCategoryNotPresent()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "msbuild_analysis",
            Categories = { ["extension"] = true },
            AffectedTestProjects = ["tests/Aspire.Milvus.Client.Tests/Aspire.Milvus.Client.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_integrations=true", output);
        Assert.Contains("run_extension=true", output);
    }

    [Fact]
    public void WriteGitHubOutput_IncludesRunIntegrations_WhenRunAllIsTrue()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = true,
            Reason = "critical_path",
            Categories = { ["core"] = true }
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_all=true", output);
        Assert.Contains("run_integrations=true", output);
    }

    [Fact]
    public void WriteGitHubOutput_DoesNotDuplicateRunIntegrations_WhenCategoryPresent()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories = { ["integrations"] = true },
            AffectedTestProjects = ["tests/Aspire.Milvus.Client.Tests/Aspire.Milvus.Client.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        // Should only appear once (from category loop)
        var count = output.Split('\n').Count(l => l.StartsWith("run_integrations="));
        Assert.Equal(1, count);
    }

    [Fact]
    public void WriteGitHubOutput_RunIntegrationsFalse_WhenNoProjectsAndNotRunAll()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "all_ignored",
            Categories = { ["extension"] = false }
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_integrations=false", output);
    }

    [Fact]
    public void WriteGitHubOutput_RunIntegrationsTrue_WhenCategoryFalseButProjectsExist()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "msbuild_analysis",
            Categories = { ["integrations"] = false, ["extension"] = true },
            AffectedTestProjects = ["tests/Infrastructure.Tests/Infrastructure.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_integrations=true", output);
        Assert.Contains("run_extension=true", output);
        var count = output.Split('\n').Count(l => l.StartsWith("run_integrations="));
        Assert.Equal(1, count);
    }

    [Fact]
    public void WriteGitHubOutput_RunIntegrationsTrue_WhenCategoryTrueAndProjectsExist()
    {
        // Both category triggered by paths AND projects discovered via dotnet-affected
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "msbuild_analysis",
            Categories = { ["integrations"] = true },
            AffectedTestProjects = ["tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj", "tests/Aspire.Hosting.Azure.Tests/Aspire.Hosting.Azure.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_integrations=true", output);
        var count = output.Split('\n').Count(l => l.StartsWith("run_integrations="));
        Assert.Equal(1, count);
        Assert.Contains("Aspire.Dashboard.Tests.csproj", output);
        Assert.Contains("Aspire.Hosting.Azure.Tests.csproj", output);
    }

    [Fact]
    public void WriteGitHubOutput_AffectedTestProjectsList_IncludesAllDiscoveredProjects()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "msbuild_analysis",
            Categories = { ["integrations"] = false, ["extension"] = true },
            AffectedTestProjects = ["tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj", "tests/Infrastructure.Tests/Infrastructure.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        // run_integrations should be true because there are affected projects
        Assert.Contains("run_integrations=true", output);
        Assert.Contains("affected_test_projects=", output);
        Assert.Contains("Aspire.Templates.Tests.csproj", output);
        Assert.Contains("Infrastructure.Tests.csproj", output);
    }

    [Fact]
    public void WriteGitHubOutput_RunIntegrationsFalse_WhenCategoryFalseAndNoProjects()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "msbuild_analysis",
            Categories = { ["integrations"] = false, ["extension"] = true }
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_integrations=false", output);
        Assert.Contains("run_extension=true", output);
    }

    [Fact]
    public void WriteGitHubOutput_EmitsAffectedTestProjects()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories = { ["integrations"] = true },
            AffectedTestProjects = ["tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj", "tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("affected_test_projects=", output);
        Assert.Contains("Aspire.Dashboard.Tests.csproj", output);
        Assert.Contains("Aspire.Hosting.Tests.csproj", output);
    }

    [Fact]
    public void WriteGitHubOutput_NoRemovedCategories()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories =
            {
                ["integrations"] = true,
                ["cli_e2e"] = true,
                ["extension"] = false,
                ["polyglot"] = false
            }
        };

        var output = CaptureGitHubOutput(result);

        // Should NOT contain removed categories
        Assert.DoesNotContain("run_templates=", output);
        Assert.DoesNotContain("run_endtoend=", output);
        Assert.DoesNotContain("run_playground=", output);

        // Should contain kept categories
        Assert.Contains("run_integrations=", output);
        Assert.Contains("run_cli_e2e=", output);
        Assert.Contains("run_extension=", output);
        Assert.Contains("run_polyglot=", output);
    }

    [Fact]
    public void WriteGitHubOutput_EmitsPolyglotAndExtension()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories =
            {
                ["polyglot"] = true,
                ["extension"] = true
            }
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_polyglot=true", output);
        Assert.Contains("run_extension=true", output);
    }

    [Fact]
    public void WriteGitHubOutput_AffectedTestProjects_ContainsCsprojPaths()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            AffectedTestProjects = ["tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains(".csproj", output);
        // Should NOT have trailing slash directory format
        Assert.DoesNotContain("Tests/\"", output.Replace(".csproj\"", ""));
    }

    [Fact]
    public void WriteGitHubOutput_DoesNotEmitIntegrationsProjects()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            AffectedTestProjects = ["tests/Foo.Tests/Foo.Tests.csproj"]
        };

        var output = CaptureGitHubOutput(result);

        // Should use affected_test_projects, not integrations_projects
        Assert.DoesNotContain("integrations_projects=", output);
        Assert.Contains("affected_test_projects=", output);
    }

    [Fact]
    public void WriteGitHubOutput_IncludesNuGetTestOutputs_WhenTriggered()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories = { ["integrations"] = true },
            NuGetDependentTests = new NuGetDependentTestsInfo
            {
                Triggered = true,
                Reason = "Packable project affected",
                AffectedPackableProjects = ["src/MyLib/MyLib.csproj"],
                Projects = ["tests/Aspire.Templates.Tests/", "tests/Aspire.EndToEnd.Tests/"]
            }
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_nuget_tests=true", output);
        Assert.Contains("nuget_test_projects=", output);
        Assert.Contains("tests/Aspire.Templates.Tests/", output);
        Assert.Contains("tests/Aspire.EndToEnd.Tests/", output);
    }

    [Fact]
    public void WriteGitHubOutput_NuGetTestsFalse_WhenNotTriggered()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "selective",
            Categories = { ["integrations"] = true }
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("run_nuget_tests=false", output);
        Assert.Contains("nuget_test_projects=[]", output);
    }

    [Fact]
    public void WriteGitHubOutput_UsesMultilineSyntax_ForMultilineSelectionReason()
    {
        var result = new TestSelectionResult
        {
            RunAllTests = true,
            Reason = "dotnet-affected failed: line 1\n   at Example.Frame()"
        };

        var output = CaptureGitHubOutput(result);

        Assert.Contains("selection_reason<<EOF_", output);
        Assert.Contains("dotnet-affected failed: line 1", output);
        Assert.Contains("   at Example.Frame()", output);
    }

    private static string CaptureGitHubOutput(TestSelectionResult result)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", tempFile);
            result.WriteGitHubOutput();
            return File.ReadAllText(tempFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_OUTPUT", null);
            File.Delete(tempFile);
        }
    }
}
