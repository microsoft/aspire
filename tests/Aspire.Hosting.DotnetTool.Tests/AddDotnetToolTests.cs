// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.DotnetTool.Tests;

#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
public class AddDotnetToolTests
{
    [Fact]
    public void AddDotnetToolAddsResourceWithCorrectName()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef");

        Assert.Equal("mytool", tool.Resource.Name);
        Assert.IsType<DotnetToolResource>(tool.Resource);
    }

    [Fact]
    public void AddDotnetToolAddsToolAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef");

        var annotation = Assert.Single(tool.Resource.Annotations.OfType<DotnetToolAnnotation>());
        Assert.Equal("dotnet-ef", annotation.PackageId);
    }

    [Fact]
    public void AddDotnetToolThrowsWhenPackageIdIsNull()
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.AddDotnetTool("mytool", null!));
    }

    [Fact]
    public void AddDotnetToolThrowsWhenPackageIdIsEmpty()
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddDotnetTool("mytool", ""));
    }

    [Fact]
    public void AddDotnetToolThrowsWhenPackageIdIsWhitespace()
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddDotnetTool("mytool", "   "));
    }

    [Fact]
    public void AddDotnetToolSetsIconName()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef");

        var annotation = Assert.Single(tool.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Equal("Toolbox", annotation.IconName);
    }

    [Fact]
    public async Task AddDotnetToolWithDefaultSettingsGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithVersionGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolVersion("10.0.0");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--version", arg),
            arg => Assert.Equal("10.0.0", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithPrereleaseGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolPrerelease();

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--prerelease", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithCustomSourceGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolSource("https://custom.nuget.org/v3/index.json");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--add-source", arg),
            arg => Assert.Equal("https://custom.nuget.org/v3/index.json", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithMultipleSourcesGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolSource("https://source1.nuget.org/v3/index.json")
            .WithToolSource("https://source2.nuget.org/v3/index.json");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--add-source", arg),
            arg => Assert.Equal("https://source1.nuget.org/v3/index.json", arg),
            arg => Assert.Equal("--add-source", arg),
            arg => Assert.Equal("https://source2.nuget.org/v3/index.json", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithIgnoreExistingFeedsUsesSourceInsteadOfAddSource()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolSource("https://custom.nuget.org/v3/index.json")
            .WithToolIgnoreExistingFeeds();

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--source", arg),
            arg => Assert.Equal("https://custom.nuget.org/v3/index.json", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithIgnoreFailedSourcesGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolIgnoreFailedSources();

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--ignore-failed-sources", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithAdditionalArgsPassedThrough()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithArgs("database", "update");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg),
            arg => Assert.Equal("database", arg),
            arg => Assert.Equal("update", arg)
        );
    }

    [Fact]
    public async Task AddDotnetToolWithComplexConfigurationGeneratesCorrectArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolVersion("9.0.1")
            .WithToolSource("https://custom.nuget.org/v3/index.json")
            .WithToolIgnoreFailedSources()
            .WithArgs("database", "update");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        Assert.Collection(args,
            arg => Assert.Equal("tool", arg),
            arg => Assert.Equal("exec", arg),
            arg => Assert.Equal("dotnet-ef", arg),
            arg => Assert.Equal("--add-source", arg),
            arg => Assert.Equal("https://custom.nuget.org/v3/index.json", arg),
            arg => Assert.Equal("--ignore-failed-sources", arg),
            arg => Assert.Equal("--version", arg),
            arg => Assert.Equal("9.0.1", arg),
            arg => Assert.Equal("--yes", arg),
            arg => Assert.Equal("--", arg),
            arg => Assert.Equal("database", arg),
            arg => Assert.Equal("update", arg)
        );
    }

    [Fact]
    public void WithPackageVersionSetsVersionInAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolVersion("10.0.*");

        var annotation = tool.Resource.Annotations.OfType<DotnetToolAnnotation>().Single();
        Assert.Equal("10.0.*", annotation.Version);
    }

    [Fact]
    public void WithPackagePrereleaseSetsPreReleaseInAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolPrerelease();

        var annotation = tool.Resource.Annotations.OfType<DotnetToolAnnotation>().Single();
        Assert.True(annotation.Prerelease);
    }

    [Fact]
    public void WithPackageSourceAddsSourceToAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolSource("https://custom.nuget.org/v3/index.json");

        var annotation = tool.Resource.Annotations.OfType<DotnetToolAnnotation>().Single();
        Assert.Single(annotation.Sources);
        Assert.Equal("https://custom.nuget.org/v3/index.json", annotation.Sources[0]);
    }

    [Fact]
    public void WithPackageIgnoreExistingFeedsSetsIgnoreExistingFeedsInAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolIgnoreExistingFeeds();

        var annotation = tool.Resource.Annotations.OfType<DotnetToolAnnotation>().Single();
        Assert.True(annotation.IgnoreExistingFeeds);
    }

    [Fact]
    public void WithPackageIgnoreFailedSourcesSetsIgnoreFailedSourcesInAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef")
            .WithToolIgnoreFailedSources();

        var annotation = tool.Resource.Annotations.OfType<DotnetToolAnnotation>().Single();
        Assert.True(annotation.IgnoreFailedSources);
    }

    [Fact]
    public async Task RemovingToolAnnotationResultsInNoArgs()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("mytool", "dotnet-ef");

        // Remove the annotation to simulate it being removed
        var annotation = tool.Resource.Annotations.OfType<DotnetToolAnnotation>().Single();
        tool.Resource.Annotations.Remove(annotation);

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(tool.Resource).DefaultTimeout();

        // Should be empty since annotation was removed
        Assert.Empty(args);
    }

    [Fact]
    public async Task AddDotnetToolGeneratesCorrectManifest()
    {
        var builder = DistributedApplication.CreateBuilder();
        var tool = builder.AddDotnetTool("ef-tool", "dotnet-ef")
            .WithToolVersion("10.0.0")
            .WithArgs("--version");

        using var app = builder.Build();

        var manifest = await ManifestUtils.GetManifest(tool.Resource).DefaultTimeout();

        var expectedManifest =
        """
        {
          "type": "executable.v0",
          "workingDirectory": ".",
          "command": "dotnet",
          "args": [
            "tool",
            "exec",
            "dotnet-ef",
            "--version",
            "10.0.0",
            "--yes",
            "--",
            "--version"
          ]
        }
        """;

        Assert.Equal(expectedManifest, manifest.ToString());
    }

    [Theory]
    [InlineData("11.1.0", true)]
    [InlineData("10.0.0", true)]
    [InlineData("9.0.999", false)]
    public void ValidateDotnetSdkVersion_ValidatesVersionCorrectly(string versionString, bool isAllowed)
    {
        var version = Version.Parse(versionString);

        if (isAllowed)
        {
            DotnetToolResourceExtensions.ValidateDotnetSdkVersion(version, "");
        }
        else
        {
            Assert.Throws<DistributedApplicationException>(() =>
                DotnetToolResourceExtensions.ValidateDotnetSdkVersion(version, ""));
        }
    }

    [Fact]
    public void ValidateDotnetSdkVersion_WithNullVersion_DoesNotThrow()
    {
        // Should not throw - null is treated as "unable to determine version"
        DotnetToolResourceExtensions.ValidateDotnetSdkVersion(null, "");
    }
}
