// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001, ASPIREWATCH001, ASPIREPERSISTENCE001

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed record DotnetWatchRegistration(
    DotnetProjectResource Resource,
    DotnetProjectMetadata Metadata,
    DotnetProjectBuildCoordinator.CoordinatorState? Builds,
    IResourceAnnotation Launch,
    string Command,
    string WorkingDirectory);

internal sealed class DotnetWatchCoordinator : IHostedService, IAsyncDisposable
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, DotnetWatchCoordinator> s_states = new();
    private readonly IDistributedApplicationBuilder _builder;
    private readonly List<DotnetWatchRegistration> _registrations = [];
    private readonly List<DotnetWatchGroup> _groups = [];
    private bool _prepared;
    private bool _disposed;

    private DotnetWatchCoordinator(IDistributedApplicationBuilder builder)
    {
        _builder = builder;
        builder.Services.AddSingleton(_ => this);
        builder.Services.AddHostedService(services => services.GetRequiredService<DotnetWatchCoordinator>());
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, context =>
            PrepareAsync(context.Model, context.Services, context.CancellationToken));
    }

    internal static void Register(
        IResourceBuilder<DotnetProjectResource> resource,
        DotnetProjectMetadata metadata,
        DotnetProjectBuildCoordinator.CoordinatorState? builds)
    {
        var builder = resource.ApplicationBuilder;
        if (!builder.ExecutionContext.IsRunMode ||
            !builder.ExecutionContext.RunConfiguration.WatchEnabled ||
            !DotnetWatchConfiguration.IsCliOwned(builder.Configuration))
        {
            return;
        }

        var executable = resource.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
        s_states.GetValue(builder, static builder => new(builder))._registrations.Add(
            new(resource.Resource, metadata, builds, resource.Resource.Annotations.Last(),
                executable.Command, executable.WorkingDirectory));
    }

    internal async Task PrepareAsync(
        DistributedApplicationModel model, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (_prepared)
        {
            return;
        }
        _prepared = true;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<DotnetWatchCoordinator>();
        var active = model.Resources.ToHashSet(ReferenceEqualityComparer.Instance);
        var registrations = _registrations.Where(registration => active.Contains(registration.Resource)).ToArray();
        if (registrations.Length == 0)
        {
            return;
        }
        foreach (var builds in registrations.Select(registration => registration.Builds).OfType<DotnetProjectBuildCoordinator.CoordinatorState>().Distinct())
        {
            await builds.MaterializeBuildPlan(model, services).ConfigureAwait(false);
        }

        var roots = model.Resources
            .Select(resource => (Resource: resource, Metadata: resource.Annotations.OfType<IProjectMetadata>().LastOrDefault()))
            .Where(item => item.Metadata is not null)
            .Select(item => new DotnetWatchRoot(
                item.Resource, item.Metadata!, GetRegistrationReason(item.Resource, registrations, model),
                new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer)))
            .ToArray();

        var executable = _builder.Configuration[DotnetWatchConfiguration.CliExecutable];
        var arguments = _builder.Configuration[DotnetWatchConfiguration.CliArguments];
        if (string.IsNullOrEmpty(executable) || arguments is null)
        {
            ExcludeAll("The exact invoking CLI specification is unavailable.");
            return;
        }

        try
        {
            var prefix = JsonSerializer.Deserialize(arguments, DotnetWatchJsonContext.Default.StringArray)
                ?? throw new DotnetWatchToolException("The invoking CLI argument prefix is invalid.");
            if (prefix.Any(argument => argument is null))
            {
                throw new DotnetWatchToolException("The invoking CLI argument prefix contains a null argument.");
            }
            if (registrations.Any(registration => !ReferenceEquals(
                registration.Resource.Annotations.OfType<IProjectMetadata>().LastOrDefault(), registration.Metadata)))
            {
                ExcludeAll("Project metadata was replaced after registration; build output isolation could not be established.");
                return;
            }
            if (roots.All(root => root.Reason is not null))
            {
                foreach (var root in roots)
                {
                    Exclude(root, root.Reason!);
                }
                return;
            }
            if (roots.Any(root => root.Resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>().Any()))
            {
                ExcludeAll("Custom build environments can change project references and outputs; safe watch isolation could not be established.");
                return;
            }

            var evaluated = new List<DotnetWatchRoot>();
            foreach (var root in roots)
            {
                var configuration = root.Metadata is DotnetProjectMetadata metadata ? metadata.BuildConfiguration : null;
                var graph = await DotnetWatchProjectGraph.EvaluateGraphAsync(root.Metadata, configuration, cancellationToken).ConfigureAwait(false);
                evaluated.Add(root with { Projects = graph });
            }
            roots = [.. evaluated];
            var resolved = _builder.Configuration[DotnetWatchConfiguration.ToolPath];
            var tool = string.IsNullOrEmpty(resolved)
                ? await DotnetWatchToolManager.EnsureFromEnvironmentAsync(
                    _builder.AppHostDirectory, "dotnet", message => logger.LogInformation("{Message}", message),
                    cancellationToken).ConfigureAwait(false)
                : DotnetWatchToolManager.ValidatePayload(
                    resolved, _builder.Configuration[DotnetWatchConfiguration.ToolVersion] ?? "custom");
            roots = roots.Select(root => root with
            {
                Reason = DotnetWatchProjectGraph.GetCompatibilityReason(root, tool)
            }).ToArray();
            var plan = DotnetWatchProjectGraph.Partition(roots);
            foreach (var root in plan.Excluded)
            {
                Exclude(root, root.Reason!);
            }
            foreach (var groupRoots in plan.Groups)
            {
                var members = groupRoots.Select(root => registrations.Single(registration => ReferenceEquals(registration.Resource, root.Resource))).ToArray();
                var group = new DotnetWatchGroup(_builder, services, tool, groupRoots, members, executable, prefix, _groups.Count + 1);
                _groups.Add(group);
                group.Configure(model);
            }
            var watched = plan.Groups.Sum(group => group.Length);
            logger.LogWarning(
                "Experimental .NET watch coverage: {Watched}/{Total} resources in {Groups} isolated build groups. " +
                "Process state is last-observed; the selected protocol does not report successful exits. " +
                "After manually stopping a watched resource, restart the watch run to start it again safely.",
                watched, roots.Length, _groups.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DotnetWatchToolException or IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            foreach (var group in _groups)
            {
                await group.AbortSetupAsync(exception.Message).ConfigureAwait(false);
            }
            ExcludeAll($"Watch initialization failed: {exception.Message}");
        }

        void ExcludeAll(string reason)
        {
            foreach (var root in roots)
            {
                Exclude(root, reason);
            }
        }

        void Exclude(DotnetWatchRoot root, string reason)
        {
            logger.LogWarning("Resource '{ResourceName}' is not watched: {Reason} Running normally.", root.Resource.Name, reason);
            services.GetRequiredService<ResourceLoggerService>().GetLogger(root.Resource)
                .LogWarning("Experimental .NET watch is unavailable: {Reason} Running normally.", reason);
            AddCoverageProjection(root.Resource, snapshot => SetCoverage(snapshot, false, reason));
        }
    }

    private static string? GetRegistrationReason(
        IResource resource, DotnetWatchRegistration[] registrations, DistributedApplicationModel model)
    {
        var registration = registrations.SingleOrDefault(item => ReferenceEquals(item.Resource, resource));
        if (registration is null)
        {
            return "This resource does not use the integration-owned V2 launch.";
        }
        if (resource.Annotations.LastOrDefault(annotation => annotation.GetType() == registration.Launch.GetType()) is not { } last ||
            !ReferenceEquals(last, registration.Launch) ||
            resource.Annotations.OfType<ExecutableAnnotation>().Last().Command != registration.Command ||
            resource.Annotations.OfType<ExecutableAnnotation>().Last().WorkingDirectory != registration.WorkingDirectory)
        {
            return "A custom launch tool or working directory owns this resource.";
        }
        var extension = Path.GetExtension(registration.Metadata.ProjectPath);
        if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return "Only C# managed services are supported by this watch preview.";
        }
        if (resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>().Any())
        {
            return "Custom lifecycle ownership prevents the watcher from guaranteeing cleanup.";
        }
        if (resource.Annotations.OfType<PersistenceAnnotation>().LastOrDefault() is { Mode: not PersistenceMode.Session })
        {
            return "Persistent resources cannot be owned by this run-scoped watcher.";
        }
        if (model.Resources.SelectMany(item => item.Annotations.OfType<WaitAnnotation>())
            .Any(wait => ReferenceEquals(wait.Resource, resource) && wait.WaitType == WaitType.WaitForCompletion))
        {
            return "Completion-sensitive resources require reliable application exit reporting, which this watch protocol does not provide.";
        }

        return null;
    }

    internal static void AddCoverageProjection(IResource resource, Func<CustomResourceSnapshot, CustomResourceSnapshot> projection)
    {
        var previous = resource.Annotations.OfType<ResourceSnapshotProjectionAnnotation>().LastOrDefault();
        resource.Annotations.Add(new ResourceSnapshotProjectionAnnotation(
            (id, snapshot) => projection(previous is null ? snapshot : previous.Projection(id, snapshot))));
    }

    internal static CustomResourceSnapshot SetCoverage(CustomResourceSnapshot snapshot, bool enabled, string reason) =>
        snapshot with
        {
            Properties = [.. snapshot.Properties.Where(property => property.Name is not ("watch.mode" or "watch.reason")),
                new("watch.mode", enabled ? "experimental" : "ordinary") { IsHighlighted = true },
                new("watch.reason", reason)]
        };

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var group in _groups)
        {
            await group.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var group in _groups)
        {
            await group.DisposeAsync().ConfigureAwait(false);
        }
    }
}
