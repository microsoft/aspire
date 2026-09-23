// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Lifecycle;

internal sealed class DotnetBuildCommandEventingSubscriber(
    IDotnetSdkVersionProvider versionProvider) : IDistributedApplicationEventingSubscriber
{
    // Aspire.Hosting.Dotnet intentionally consumes only the public Aspire.Hosting surface. Its coordinated build
    // resources therefore use reserved internal names that core hosting can recognize without internals visibility.
    private const string CoordinatedBuildResourceName = "__dotnet-project-build";

    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeResourceStartedEvent>(ConfigureMultiThreadedBuildAsync);
        return Task.CompletedTask;
    }

    private Task ConfigureMultiThreadedBuildAsync(
        BeforeResourceStartedEvent @event,
        CancellationToken cancellationToken)
    {
        if (!IsAspireManagedDotnetBuild(@event.Resource) ||
            @event.Resource.TryGetLastAnnotation<MultiThreadedBuildConfiguredAnnotation>(out _))
        {
            return Task.CompletedTask;
        }

        var executable = (ExecutableResource)@event.Resource;
        lock (executable.Annotations)
        {
            if (executable.TryGetLastAnnotation<MultiThreadedBuildConfiguredAnnotation>(out _))
            {
                return Task.CompletedTask;
            }

            // DCP clears cached argument callback results before recreating a resource. Resolve SDK support inside
            // the callback so changes to global.json are observed when arguments are reevaluated for the next start.
            executable.Annotations.Add(new CommandLineArgsCallbackAnnotation(async context =>
            {
                var args = context.Args;
                if (args.Count < 2 ||
                    args[1] is not string buildTarget ||
                    buildTarget.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    // .NET 11 RC1 misroutes -mt when the build target is a file-based app, treating the .cs file
                    // as an MSBuild project. The forwarding fix targets .NET 12, and multithreaded file-app builds
                    // still have an open concurrency issue, so keep this optimization project-only for now.
                    // https://github.com/dotnet/sdk/pull/56120
                    // https://github.com/dotnet/sdk/issues/56238
                    return;
                }

                // DCP gathers arguments before environment variables. Resolve only the environment callbacks here so
                // the SDK probe sees the same PATH and host overrides as the build process. The later environment
                // gatherer reuses the cached annotation results instead of invoking those callbacks a second time.
                var executionConfiguration = await ExecutionConfigurationBuilder.Create(executable)
                    .WithEnvironmentVariablesConfig()
                    .BuildAsync(
                        context.ExecutionContext,
                        context.Logger,
                        context.CancellationToken)
                    .ConfigureAwait(false);
                if (executionConfiguration.Exception is { } exception)
                {
                    ExceptionDispatchInfo.Throw(exception);
                }

                var comparer = OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
                var buildEnvironment = new Dictionary<string, string>(comparer);
                foreach (var (name, value) in executionConfiguration.EnvironmentVariables)
                {
                    buildEnvironment[name] = value;
                }

                if (!await versionProvider.SupportsMultiThreadedBuildAsync(
                    executable.WorkingDirectory,
                    buildEnvironment,
                    context.CancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                args.Insert(Math.Min(2, args.Count), "-mt");
            }));
            executable.Annotations.Add(MultiThreadedBuildConfiguredAnnotation.Instance);
        }

        return Task.CompletedTask;
    }

    private static bool IsAspireManagedDotnetBuild(IResource resource)
    {
        if (resource is not ExecutableResource { Command: "dotnet" })
        {
            return false;
        }

        return resource is ProjectRebuilderResource ||
            resource.Name == CoordinatedBuildResourceName ||
            resource.Name.StartsWith($"{CoordinatedBuildResourceName}-", StringComparison.Ordinal);
    }

    private sealed class MultiThreadedBuildConfiguredAnnotation : IResourceAnnotation
    {
        public static MultiThreadedBuildConfiguredAnnotation Instance { get; } = new();
    }
}
