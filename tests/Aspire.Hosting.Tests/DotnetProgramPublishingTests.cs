// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests;

public class DotnetProgramPublishingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ProjectResourceIsConfiguredForDotnetProgramPublishing()
    {
        var resource = new ProjectResource("project");

        Assert.IsAssignableFrom<IDotnetProgramResource>(resource);
        Assert.True(resource.SupportsDotnetProgramPublishing());
        Assert.Single(resource.Annotations.OfType<DotnetProgramPublishingAnnotation>());
        Assert.Single(resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Single(resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Single(resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
    }

    [Fact]
    public void WithDotnetProgramPublishingRequiresProjectMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new TestDotnetProgramResource("program"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            resource.WithDotnetProgramPublishing();
        });

        Assert.Equal(
            $"Resource 'program' does not carry an {nameof(IProjectMetadata)} annotation.",
            exception.Message);
        Assert.False(resource.Resource.SupportsDotnetProgramPublishing());
    }

    [Fact]
    public void WithDotnetProgramPublishingRequiresComputeResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new NonComputeDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            resource.WithDotnetProgramPublishing();
        });

        Assert.Equal(
            $"Resource 'program' must implement {nameof(IComputeResource)} to use .NET SDK publishing.",
            exception.Message);
        Assert.False(resource.Resource.SupportsDotnetProgramPublishing());
    }

    [Fact]
    public void WithDotnetProgramPublishingIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"));

        resource.WithDotnetProgramPublishing();
        resource.WithDotnetProgramPublishing();

        Assert.True(resource.Resource.SupportsDotnetProgramPublishing());
        Assert.Single(resource.Resource.Annotations.OfType<DotnetProgramPublishingAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
    }

    [Fact]
    public void GetProjectMetadataValidatesDuplicateMetadata()
    {
        var resource = new TestDotnetProgramResource("program");
        resource.Annotations.Add(new TestProjectMetadata("first.csproj"));
        resource.Annotations.Add(new TestProjectMetadata("second.csproj"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            resource.GetProjectMetadata();
        });

        Assert.Contains("carries more than one IProjectMetadata annotation", exception.Message);
    }

    [Fact]
    public async Task ConfiguredProgramUsesProjectManifestWithoutEvaluatingLaunchToolArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var launchToolCallbackInvoked = false;
        Action<CommandLineArgsCallbackContext> launchToolCallback = context =>
        {
            launchToolCallbackInvoked = true;
            context.Args.Add("run");
            context.Args.Add("--project");
            context.Args.Add(projectPath);
        };
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithDotnetProgramPublishing()
            .WithLaunchToolArgs(launchToolCallback)
            .WithArgs("application-argument");

        var manifest = await ManifestUtils.GetManifest(
            resource.Resource,
            workspace.WorkspaceRoot.FullName);

        Assert.False(launchToolCallbackInvoked);
        Assert.Equal("project.v0", manifest["type"]?.GetValue<string>());
        Assert.Equal("program.csproj", manifest["path"]?.GetValue<string>());
        Assert.Equal(
            ["application-argument"],
            manifest["args"]?.AsArray().Select(static value => value!.GetValue<string>()));
    }

    [Fact]
    public void ConfiguredProgramParticipatesInComputeAndBuildSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"))
            .WithDotnetProgramPublishing();
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Contains(resource.Resource, model.GetComputeResources());
        Assert.Contains(resource.Resource, model.GetBuildResources());
        Assert.True(resource.Resource.RequiresImageBuild());
    }

    [Fact]
    public void DotnetProgramReplicasProduceDistinctDcpInstances()
    {
        var resource = new TestDotnetProgramResource("program");
        resource.Annotations.Add(new ReplicaAnnotation(3));
        var nameGenerator = new DcpNameGenerator(
            new ConfigurationBuilder().Build(),
            Options.Create(new DcpOptions()));

        nameGenerator.EnsureDcpInstancesPopulated(resource);

        Assert.True(resource.TryGetInstances(out var instances));
        Assert.Equal(3, instances.Length);
        Assert.Equal([0, 1, 2], instances.Select(static instance => instance.Index));
    }

    private sealed class TestDotnetProgramResource(string name) :
        Resource(name),
        IDotnetProgramResource,
        IComputeResource,
        IResourceWithArgs;

    private sealed class NonComputeDotnetProgramResource(string name) : Resource(name), IDotnetProgramResource;

    private sealed class TestProjectMetadata(string projectPath) : IProjectMetadata
    {
        public string ProjectPath { get; } = projectPath;
    }
}
