// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREWATCH001

using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

internal static class DotnetWatchGroupTestServices
{
    internal static (DotnetWatchRegistration Registration, DotnetWatchRoot Root) AddResource(
        IDistributedApplicationBuilder builder, string directory) =>
        AddResource(builder, directory, "service");

    internal static (DotnetWatchRegistration Registration, DotnetWatchRoot Root) AddResource(
        IDistributedApplicationBuilder builder, string directory, string name)
    {
        var path = DotnetWatchProjectGraph.Normalize(Path.Combine(directory, "Service.csproj"));
        var metadata = new DotnetProjectMetadata(path, null, name);
        var resource = builder.AddResource(new DotnetProjectResource(name, directory))
            .WithAnnotation(metadata)
            .WithLaunchToolArgs(context =>
            {
                context.Args.Add("ordinary-tool");
                context.Args.Add("--project");
                context.Args.Add(path);
            });
        var registration = new DotnetWatchRegistration(resource.Resource, metadata, null,
            resource.Resource.Annotations.Last(), "dotnet", directory);
        resource.WithArgs("--application-option", "two words").WithEnvironment("APP_SETTING", "from-model");
        var node = new DotnetWatchProjectNode(path, Path.Combine(directory, "sdk"), "dotnet",
            new Dictionary<string, string>(), [], []);
        var root = new DotnetWatchRoot(resource.Resource, metadata, null,
            new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer) { [path] = node });
        return (registration, root);
    }

    internal static DotnetWatchGroup CreateGroup(
        IDistributedApplicationBuilder builder, IServiceProvider services, DotnetWatchRegistration registration, DotnetWatchRoot root) =>
        CreateGroup(builder, services, registration, root, 1);

    internal static DotnetWatchGroup CreateGroup(
        IDistributedApplicationBuilder builder, IServiceProvider services, DotnetWatchRegistration registration, DotnetWatchRoot root, int index) =>
        new(builder, services, new(Path.Combine(builder.AppHostDirectory, "tool.dll"), "custom", 10),
            [root], [registration], "/exact/aspire", ["/exact/aspire.dll"], index);
}
