// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Kubernetes.Tests;

public class PipelineOutputRoutingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PublishRoutesEnvironmentArtifacts(bool multipleEnvironments, bool resourceOverride, bool customProvider)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var global = Path.Combine(workspace.Path, "global");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, global);
        if (customProvider)
        {
            builder.Services.AddSingleton<IPipelineOutputService>(new TestPipelineOutputService(workspace.Path));
        }
        var cluster = builder.AddKubernetesEnvironment("cluster");
        if (resourceOverride)
        {
            cluster.WithPipelineOutputDirectory("routed/cluster");
        }
        builder.AddContainer("kube-api", "nginx").WithComputeEnvironment(cluster);
        if (multipleEnvironments)
        {
            var compose = builder.AddDockerComposeEnvironment("compose");
            builder.AddContainer("docker-api", "redis").WithComputeEnvironment(compose);
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
        using var app = builder.Build();

        await app.RunAsync();

        if (customProvider)
        {
            Assert.False(Directory.Exists(global));
        }

        await Verify(GetGeneratedFiles(workspace.Path));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(".", false)]
    [InlineData(".", true)]
    [InlineData("routed/compose", false)]
    [InlineData("routed/compose", true)]
    public async Task PublishRoutesSingleDockerEnvironment(string? path, bool customProvider)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var global = Path.Combine(workspace.Path, "global");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, global);
        if (customProvider)
        {
            builder.Services.AddSingleton<IPipelineOutputService>(new TestPipelineOutputService(workspace.Path));
        }
        var compose = builder.AddDockerComposeEnvironment("compose");
        if (path is not null)
        {
            compose.WithPipelineOutputDirectory(path);
        }
        builder.AddContainer("docker-api", "redis");
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
        using var app = builder.Build();

        await app.RunAsync();

        await Verify(GetGeneratedFiles(workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishRoutesChildComputeEnvironmentArtifacts(bool customProvider)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var global = Path.Combine(workspace.Path, "global");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, global);
        if (customProvider)
        {
            builder.Services.AddSingleton<IPipelineOutputService>(new TestPipelineOutputService(workspace.Path));
        }
        var cluster = builder.AddKubernetesEnvironment("cluster");
        builder.AddContainer("kube-api", "nginx").WithComputeEnvironment(cluster);
        builder.AddResource(new ChildComputeEnvironment("child", cluster.Resource));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
        using var app = builder.Build();
        Assert.Equal(2, app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<IComputeEnvironmentResource>().Count());

        await app.RunAsync();

        await Verify(GetGeneratedFiles(workspace.Path));
    }

    private static string[] GetGeneratedFiles(string workspace) =>
        Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspace, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();

    private sealed class ChildComputeEnvironment(string name, IResource parent) : Resource(name), IComputeEnvironmentResource, IResourceWithParent
    {
        public IResource Parent => parent;
    }
}
