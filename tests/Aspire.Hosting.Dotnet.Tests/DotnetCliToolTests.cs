// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias HostingDotnet;

#pragma warning disable ASPIREDOTNETCLITOOL001, ASPIREDOTNETTOOL, ASPIRECOMMAND001
#pragma warning disable CS0618 // Coexistence with legacy APIs is part of the compatibility contract.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetSdkProbeResult = HostingDotnet::Aspire.Hosting.Utils.DotnetSdkProbeResult;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetCliToolTests
{
    [Fact]
    public async Task LegacyAndCanonicalFactoriesAndFluentMethodsCoexist()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var legacy = builder.AddDotnetTool("legacy", "old.package").WithToolVersion("1.0");
        var canonical = builder.AddDotnetCliTool("canonical", "new.package")
            .WithToolPackage("updated.package")
            .WithToolVersion("2.0")
            .WithToolPrerelease()
            .WithToolSource("https://example.invalid/first")
            .WithToolSource("https://example.invalid/second")
            .WithToolIgnoreExistingFeeds()
            .WithToolIgnoreFailedSources()
            .WithArgs("application-arg");

        Assert.Equal("1.0", Assert.Single(legacy.Resource.Annotations.OfType<DotnetToolAnnotation>()).Version);
        Assert.Equal("Aspire.Hosting.Dotnet", canonical.Resource.GetType().Assembly.GetName().Name);
        Assert.Same(canonical, canonical.WithToolVersion("2.0"));
        Assert.Equal(
            ["tool", "exec", "updated.package", "--source", "https://example.invalid/first",
             "--source", "https://example.invalid/second", "--ignore-failed-sources",
             "--version", "2.0", "--yes", "--", "application-arg"],
            await ArgumentEvaluator.GetArgumentListAsync(canonical.Resource));
    }

    [Fact]
    public async Task RemovingConfigurationPreservesToolKindAndApplicationArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var tool = builder.AddDotnetCliTool("tool", "package").WithArgs("application-arg");
        tool.Resource.Annotations.Remove(Assert.Single(tool.Resource.Annotations.OfType<DotnetCliToolAnnotation>()));

        Assert.Equal(["application-arg"], await ArgumentEvaluator.GetArgumentListAsync(tool.Resource));
        Assert.Equal("Tool", tool.Resource.ResourceTypeDisplayName);
        Assert.Empty(tool.Resource.CreateSnapshotProperties());
    }

    [Fact]
    public async Task DefaultAndPrereleaseArgumentsRetainAcquisitionBoundary()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var defaults = builder.AddDotnetCliTool("default", "package").WithToolIgnoreExistingFeeds();
        var prerelease = builder.AddDotnetCliTool("prerelease", "package")
            .WithToolPrerelease()
            .WithToolSource("https://example.invalid/feed")
            .WithArgs("--version");

        Assert.Equal(["tool", "exec", "package", "--yes", "--"],
            await ArgumentEvaluator.GetArgumentListAsync(defaults.Resource));
        Assert.Equal(
            ["tool", "exec", "package", "--add-source", "https://example.invalid/feed",
             "--prerelease", "--yes", "--", "--version"],
            await ArgumentEvaluator.GetArgumentListAsync(prerelease.Resource));
    }

    [Fact]
    public async Task CustomResourceUsesTheCanonicalConfigurationAndClassifier()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = new CustomCliToolResource("custom", "package");
        var tool = builder.AddDotnetCliTool(resource).WithToolVersion("1.2.3");

        Assert.Same(resource, tool.Resource);
        Assert.Equal("Tool", resource.ResourceTypeDisplayName);
        Assert.Equal(
            ["tool", "exec", "package", "--version", "1.2.3", "--yes", "--"],
            await ArgumentEvaluator.GetArgumentListAsync(resource));
    }

    [Fact]
    public void MetadataUsesTheExistingTranslatedLabels()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
            var tool = new DotnetCliToolResource("tool", "package");
            tool.ToolConfiguration!.Version = "1.2.3";
            Assert.Collection(tool.CreateSnapshotProperties(),
                property =>
                {
                    Assert.Equal("tool.package", property.Name);
                    Assert.Equal("Package d\u2019outils", property.DisplayName);
                    Assert.True(property.IsHighlighted);
                    Assert.Equal(0, property.SortOrder);
                },
                property =>
                {
                    Assert.Equal("tool.version", property.Name);
                    Assert.Equal("Version de l\u2019outil", property.DisplayName);
                    Assert.Equal("1.2.3", property.Value);
                    Assert.Equal(1, property.SortOrder);
                },
                property =>
                {
                    Assert.Equal(CustomResourceKnownProperties.Source, property.Name);
                    Assert.Equal("package", property.Value);
                });
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public async Task ToolRetainsExecutableManifestPublishing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var tool = builder.AddDotnetCliTool("tool", "package").WithToolVersion("1.2.3").WithArgs("--help");
        var manifest = await ManifestUtils.GetManifest(tool.Resource);

        await Verify(manifest.ToString(), "json");
    }

    [Theory]
    [InlineData("9.0.100", false)]
    [InlineData("10.0.100-preview.1", true)]
    [InlineData(null, true)]
    public async Task PrivateSdkAdapterValidatesTheSelectedSdk(string? version, bool expectedValid)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var process = new TestProcessDisposable();
        var provider = new DotnetCliToolSdkVersionProvider(
            (executable, directory, environment) =>
            {
                Assert.Equal("dotnet", executable);
                Assert.Equal(Path.GetFullPath(builder.AppHostDirectory), directory);
                Assert.Equal("true", environment["DOTNET_NOLOGO"]);
                return (Task.FromResult(new DotnetSdkProbeResult(0, version is null ? [] : [version])), process);
            },
            NullLogger<DotnetCliToolSdkVersionProvider>.Instance,
            TestContext.Current.CancellationToken);
        builder.Services.AddSingleton(provider);
        var tool = builder.AddDotnetCliTool("tool", "package");
        tool.WithWorkingDirectory(builder.AppHostDirectory);
        using var app = builder.Build();

        var command = Assert.Single(tool.Resource.Annotations.OfType<RequiredCommandAnnotation>());
        var result = await command.ValidationCallback!(
            new RequiredCommandValidationContext("dotnet", app.Services, TestContext.Current.CancellationToken));
        Assert.Equal(expectedValid, result.IsValid);
        Assert.Equal(1, process.DisposeCallCount);
    }

    private sealed class CustomCliToolResource(string name, string packageId) : DotnetCliToolResource(name, packageId);
}
