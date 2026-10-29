// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public sealed class PipelineOutputServiceTests : IDisposable
{
    private readonly TestFileSystemService _fileSystem = new();

    [Theory]
    [InlineData(null)]
    [InlineData("publish")]
    public void GlobalOutputPreservesDefaults(string? output)
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "apphost");
        var service = CreateService(output, appHost);

        Assert.Equal(output is null ? Path.Combine(appHost, "aspire-output") : Path.GetFullPath(output), service.GetOutputDirectory());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("invalid\0path")]
    public void UnavailableAppHostFallsBackToWorkingDirectory(string? appHost)
    {
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "aspire-output"), CreateService(null, appHost).GetOutputDirectory());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("deployment/api")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../shared")]
    [InlineData("nested/../deployment.yaml")]
    public void ResourceOutputResolvesDirectoryWithoutCreatingIt(string? path)
    {
        var output = Path.Combine(_fileSystem.TempDirectory.CreateTempSubdirectory().Path, "publish");
        var service = CreateService(output);
        var resource = new ContainerResource("api");
        if (path is not null)
        {
            resource.Annotations.Add(new PipelineOutputDirectoryAnnotation(path));
        }
        var expected = Path.GetFullPath(path ?? resource.Name, output);
        Assert.False(Directory.Exists(output));

        Assert.Equal(expected, service.GetOutputDirectory(resource));
        Assert.Equal(output, service.GetOutputDirectory());
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void FullyQualifiedOverrideIsIndependentOfGlobalOutput()
    {
        var destination = Path.Combine(_fileSystem.TempDirectory.CreateTempSubdirectory().Path, "deployment.yaml");
        var resource = new ContainerResource("api");
        resource.Annotations.Add(new PipelineOutputDirectoryAnnotation(destination));
        var service = CreateService("publish");

        Assert.False(Directory.Exists(destination));
        Assert.Equal(destination, service.GetOutputDirectory(resource));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void SharedOutputDoesNotChangeTemporaryDirectoryIsolationOrLifetime()
    {
        var service = CreateService("publish");
        var first = new ContainerResource("first");
        var second = new ContainerResource("second");
        first.Annotations.Add(new PipelineOutputDirectoryAnnotation("."));
        second.Annotations.Add(new PipelineOutputDirectoryAnnotation("."));
        var temp = service.GetTempDirectory();
        Assert.True(Directory.Exists(temp));

        Assert.Equal(service.GetOutputDirectory(first), service.GetOutputDirectory(second));
        Assert.Equal(Path.Combine(temp, first.Name), service.GetTempDirectory(first));
        Assert.Equal(Path.Combine(temp, second.Name), service.GetTempDirectory(second));
        Assert.False(Directory.Exists(service.GetTempDirectory(first)));
        _fileSystem.Dispose();
        Assert.False(Directory.Exists(temp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid\0path")]
    public void InvalidOutputDirectoryIsRejected(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PipelineOutputDirectoryAnnotation(path!));
    }

    [Theory]
    [InlineData("C:relative")]
    [InlineData(@"\rooted")]
    [InlineData("/rooted")]
    public void ValidationUsesHostRootingRules(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Path.IsPathRooted(path));
            Assert.False(Path.IsPathFullyQualified(path));
            Assert.Throws<ArgumentException>(() => new PipelineOutputDirectoryAnnotation(path));
        }
        else
        {
            Assert.Equal(path, new PipelineOutputDirectoryAnnotation(path).Path);
        }
    }

    [Fact]
    public void FluentConfigurationRejectsNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PipelineOutputResourceBuilderExtensions.WithPipelineOutputDirectory<ContainerResource>(null!, "output"));
    }

    [Fact]
    public void FluentConfigurationReplacesEarlierDirectory()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var resource = builder.AddContainer("api", "unused").WithPipelineOutputDirectory("first");
        var first = Assert.Single(resource.Resource.Annotations.OfType<PipelineOutputDirectoryAnnotation>());
        Assert.Equal("first", first.Path);

        Assert.Same(resource, resource.WithPipelineOutputDirectory("second"));

        var annotation = Assert.Single(resource.Resource.Annotations.OfType<PipelineOutputDirectoryAnnotation>());
        Assert.Equal("first", first.Path);
        Assert.Equal("second", annotation.Path);
        var service = CreateService("publish");
        Assert.Equal(Path.Combine(service.GetOutputDirectory(), "second"), service.GetOutputDirectory(resource.Resource));
    }

    [Fact]
    public void ResourceOverrideUsesPostConfiguredGlobalDirectory()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddContainer("api", "unused").WithPipelineOutputDirectory("deployment/api");
        var initialOutput = Path.Combine(_fileSystem.TempDirectory.CreateTempSubdirectory().Path, "initial");
        var finalOutput = Path.Combine(_fileSystem.TempDirectory.CreateTempSubdirectory().Path, "final");
        builder.Services.Configure<PipelineOptions>(options => options.OutputPath = initialOutput);
        builder.Services.PostConfigure<PipelineOptions>(options => options.OutputPath = finalOutput);
        Assert.NotEqual(initialOutput, finalOutput);
        Assert.Equal("deployment/api", Assert.Single(resource.Resource.Annotations.OfType<PipelineOutputDirectoryAnnotation>()).Path);
        Assert.False(Directory.Exists(initialOutput));
        Assert.False(Directory.Exists(finalOutput));
        using var app = builder.Build();
        var service = app.Services.GetRequiredService<IPipelineOutputService>();

        Assert.Equal(finalOutput, service.GetOutputDirectory());
        Assert.Equal(Path.Combine(finalOutput, "deployment", "api"), service.GetOutputDirectory(resource.Resource));
        Assert.False(Directory.Exists(initialOutput));
        Assert.False(Directory.Exists(finalOutput));
    }

    public void Dispose() => _fileSystem.Dispose();

    private PipelineOutputService CreateService(string? output, string? appHost = null)
    {
        var options = Options.Create(new PipelineOptions { OutputPath = output });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppHost:Directory"] = appHost })
            .Build();

        return new PipelineOutputService(options, configuration, _fileSystem);
    }
}
