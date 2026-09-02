// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001, ASPIREEXTENSION001, ASPIREPIPELINES001

using System.Globalization;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures coordinated initial builds for path-based .NET resources.
/// </summary>
internal static class DotnetProjectBuildCoordinator
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CoordinatorState> s_states = new();

    internal const string BuildResourceName = "__dotnet-project-build";
    private const string DebugSessionPortConfigurationKey = "DEBUG_SESSION_PORT";
    private const string DebugSessionInfoConfigurationKey = "DEBUG_SESSION_INFO";

    public static CoordinatorState? Prepare(
        IDistributedApplicationBuilder builder,
        DotnetProjectMetadata projectMetadata)
    {
        if (!builder.ExecutionContext.IsRunMode || !ShouldCoordinateBuild(builder))
        {
            return null;
        }

        var state = s_states.GetValue(builder, static builder => new CoordinatorState(builder));
        if (IsSupportedPath(projectMetadata.ProjectPath))
        {
            projectMetadata.SuppressBuild = true;
            if (IsProjectFile(projectMetadata.ProjectPath))
            {
                projectMetadata.SetProjectPath(state.AddProject(projectMetadata.ProjectPath, projectMetadata.BuildConfiguration));
            }
            else
            {
                state.EnsureBuildResource(projectMetadata.BuildConfiguration);
            }
        }

        return state;
    }

    public static void Configure(
        IResourceBuilder<DotnetProjectResource> resourceBuilder,
        CoordinatorState? state)
    {
        if (state is null)
        {
            return;
        }

        state.AddResource(resourceBuilder.Resource);

        // Preserve the eagerly visible dependency used by model tests and tooling. BeforeStart replaces
        // the build plan after all resource environment callbacks and SDK roots are known, then adds the
        // final build barrier as an additional dependency.
        state.AddEagerBuildDependencies();
    }

    private static DotnetProjectBuildResource AddBuildResource(
        IDistributedApplicationBuilder builder,
        string name,
        string? configuration)
    {
        var buildDirectory = Path.Combine(builder.AppHostDirectory, ".aspire", "build");
        var buildResource = new DotnetProjectBuildResource(
            name,
            builder.AppHostDirectory,
            buildDirectory,
            TimeProvider.System);
        buildResource.SetBuildConfiguration(configuration);
        buildResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        builder.AddResource(buildResource)
            .WithArgs(async context =>
            {
                var buildTargetPath = await buildResource.GetBuildTargetPathAsync(
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);

                context.Args.Add("build");
                context.Args.Add(buildTargetPath);

                var buildConfiguration = buildResource.BuildConfiguration;
                if (!string.IsNullOrEmpty(buildConfiguration))
                {
                    context.Args.Add("--configuration");
                    context.Args.Add(buildConfiguration);
                }
            })
            .WithIconName("CodeCsRectangle")
            .ExcludeFromManifest()
            .WithHiddenOnCompletion(0);

        return buildResource;
    }

    private static Action? AddBuildDependency(
        IDistributedApplicationBuilder builder,
        DotnetProjectResource resource,
        DotnetProjectBuildResource buildResource)
    {
        if (resource.Annotations.OfType<WaitAnnotation>().Any(
            annotation => annotation.WaitType is WaitType.WaitForCompletion &&
                          ReferenceEquals(annotation.Resource, buildResource)))
        {
            return null;
        }

        var existingAnnotations = resource.Annotations.ToHashSet(ReferenceEqualityComparer.Instance);
        builder.CreateResourceBuilder(resource)
            .WaitForCompletion(builder.CreateResourceBuilder(buildResource));
        var addedAnnotations = resource.Annotations
            .Where(annotation => !existingAnnotations.Contains(annotation))
            .ToArray();
        var subscription = builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            (@event, cancellationToken) =>
                WaitForSuccessfulBuildAsync(@event.Services, buildResource, cancellationToken));

        return () =>
        {
            builder.Eventing.Unsubscribe(subscription);
            foreach (var annotation in addedAnnotations)
            {
                resource.Annotations.Remove(annotation);
            }
        };
    }

    private static Action? AddBuildDependency(
        IDistributedApplicationBuilder builder,
        DotnetProjectBuildResource resource,
        DotnetProjectBuildResource dependency)
    {
        var existingAnnotations = resource.Annotations.ToHashSet(ReferenceEqualityComparer.Instance);
        builder.CreateResourceBuilder(resource)
            .WaitForCompletion(builder.CreateResourceBuilder(dependency));
        var addedAnnotations = resource.Annotations
            .Where(annotation => !existingAnnotations.Contains(annotation))
            .ToArray();
        var subscription = builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            (@event, cancellationToken) =>
                WaitForSuccessfulBuildAsync(@event.Services, dependency, cancellationToken));

        return () =>
        {
            builder.Eventing.Unsubscribe(subscription);
            foreach (var annotation in addedAnnotations)
            {
                resource.Annotations.Remove(annotation);
            }
        };
    }

    private static async Task WaitForSuccessfulBuildAsync(
        IServiceProvider services,
        DotnetProjectBuildResource buildResource,
        CancellationToken cancellationToken)
    {
        var notificationService = services.GetRequiredService<ResourceNotificationService>();
        var buildEvent = await notificationService.WaitForResourceAsync(
            buildResource.Name,
            resourceEvent => IsSettledBuildSnapshot(resourceEvent.Snapshot),
            cancellationToken).ConfigureAwait(false);

        if (buildEvent.Snapshot.State?.Text == KnownResourceStates.FailedToStart ||
            buildEvent.Snapshot.ExitCode is not 0)
        {
            var exitCode = buildEvent.Snapshot.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
            throw new DistributedApplicationException(
                $"The coordinated .NET build failed with exit code {exitCode}. See resource '{buildResource.Name}' for build output.");
        }
    }

    internal static bool IsSettledBuildSnapshot(CustomResourceSnapshot snapshot) =>
        snapshot.State?.Text == KnownResourceStates.FailedToStart ||
        (KnownResourceStates.TerminalStates.Contains(snapshot.State?.Text) &&
         snapshot.ExitCode is not null);

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedPath(string path) =>
        IsProjectFile(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldCoordinateBuild(IDistributedApplicationBuilder builder)
    {
        if (string.IsNullOrEmpty(builder.Configuration[DebugSessionPortConfigurationKey]))
        {
            return true;
        }

        return DebugSessionInfoParser.TryGetSupportedLaunchConfigurations(
                builder.Configuration[DebugSessionInfoConfigurationKey],
                out var supportedLaunchConfigurations)
            && supportedLaunchConfigurations?.Contains(
                KnownLaunchConfigurationTypes.ProjectWithExternalBuild) is true;
    }

    internal sealed class CoordinatorState : IDisposable
    {
        private readonly IDistributedApplicationBuilder _builder;
        private readonly List<ResourceRegistration> _registrations = [];
        private readonly List<DotnetProjectBuildResource> _ownedBuildResources = [];
        private readonly List<SharedBuildEnvironment> _sharedBuildEnvironments = [];
        private readonly Dictionary<DotnetProjectResource, Action> _eagerDependencyRollbacks =
            new(ReferenceEqualityComparer.Instance);
        private bool _materialized;
        private bool _disposed;

        public CoordinatorState(IDistributedApplicationBuilder builder)
        {
            _builder = builder;
            builder.Services.AddSingleton(_ => this);
            builder.Pipeline.WithFinalAction(
                WellKnownPipelineSteps.BeforeStart,
                stepContext => stepContext.Services
                    .GetRequiredService<CoordinatorState>()
                    .MaterializeBuildPlan(stepContext.Model, stepContext.Services));
        }

        public IReadOnlyList<DotnetProjectResource> Resources =>
            _registrations.Select(registration => registration.Resource).ToArray();

        public DotnetProjectBuildResource? PrimaryBuildResource { get; private set; }

        public string AddProject(string projectPath, string? configuration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PrimaryBuildResource ??= CreateBuildResource(configuration);
            return PrimaryBuildResource.AddProject(projectPath);
        }

        public void EnsureBuildResource(string? configuration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PrimaryBuildResource ??= CreateBuildResource(configuration);
        }

        public void AddResource(DotnetProjectResource resource)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registrations.Add(new ResourceRegistration(resource));
        }

        public void AddEagerBuildDependencies()
        {
            if (PrimaryBuildResource is not { } primaryBuildResource)
            {
                return;
            }

            foreach (var registration in _registrations)
            {
                var resource = registration.Resource;
                if (_eagerDependencyRollbacks.ContainsKey(resource) ||
                    resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is not { } metadata ||
                    !IsSupportedPath(metadata.ProjectPath))
                {
                    continue;
                }

                if (AddBuildDependency(_builder, resource, primaryBuildResource) is { } rollback)
                {
                    _eagerDependencyRollbacks.Add(resource, rollback);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RemoveEagerBuildDependencies(_registrations.Select(registration => registration.Resource));
            foreach (var buildResource in _ownedBuildResources)
            {
                buildResource.Dispose();
            }
            foreach (var sharedBuildEnvironment in _sharedBuildEnvironments)
            {
                sharedBuildEnvironment.Dispose();
            }
        }

        internal Task MaterializeBuildPlan(
            DistributedApplicationModel model,
            IServiceProvider services)
        {
            if (_materialized)
            {
                return Task.CompletedTask;
            }

            var activeResources = model.Resources.ToHashSet(ReferenceEqualityComparer.Instance);
            var activeRegistrations = _registrations
                .Where(registration => activeResources.Contains(registration.Resource))
                .ToArray();
            var inactiveResources = _registrations
                .Where(registration => !activeResources.Contains(registration.Resource))
                .Select(registration => registration.Resource)
                .ToArray();

            var resourceEntries = activeRegistrations
                .Select(registration => new ProjectEntry(
                    registration,
                    registration.Resource.Annotations.OfType<DotnetProjectMetadata>().Single()))
                .ToArray();
            var unsupportedBuildEnvironmentEntry = resourceEntries
                .FirstOrDefault(entry =>
                    !IsProjectFile(entry.Metadata.ProjectPath) &&
                    GetBuildEnvironmentCallbacks(entry).Any());
            if (unsupportedBuildEnvironmentEntry is not null)
            {
                throw new DistributedApplicationException(
                    $"The .NET resource '{unsupportedBuildEnvironmentEntry.Registration.Resource.Name}' uses " +
                    "WithBuildEnvironment, which is supported only for project files.");
            }

            var buildEntries = resourceEntries
                .Where(entry => IsSupportedPath(entry.Metadata.ProjectPath))
                .ToArray();
            var missingBuildEntries = buildEntries
                .Where(entry => !File.Exists(entry.Metadata.ProjectPath))
                .ToArray();
            buildEntries = buildEntries
                .Where(entry => File.Exists(entry.Metadata.ProjectPath))
                .ToArray();

            if (buildEntries.Length == 0)
            {
                foreach (var missingEntry in missingBuildEntries)
                {
                    // Missing paths intentionally remain on the ordinary resource-start path so the
                    // resulting dotnet error names only that resource instead of failing the shared build.
                    ConfigureMissingPathFallback(missingEntry);
                }

                RemoveEagerBuildDependencies(_registrations.Select(registration => registration.Resource));
                if (PrimaryBuildResource is { } unusedBuildResource)
                {
                    _builder.Resources.Remove(unusedBuildResource);
                    _ownedBuildResources.Remove(unusedBuildResource);
                    unusedBuildResource.Dispose();
                    PrimaryBuildResource = null;
                }

                _materialized = true;
                return Task.CompletedTask;
            }

            var buildSteps = CreateBuildSteps(buildEntries);
            var applicationLifetime = services.GetRequiredService<IHostApplicationLifetime>();
            var primaryBuildResource = PrimaryBuildResource!;
            var originalPrimaryProjectPaths = primaryBuildResource.ProjectPaths;
            var originalPrimaryWorkingDirectory = primaryBuildResource.WorkingDirectory;
            var originalPrimaryBuildConfiguration = primaryBuildResource.BuildConfiguration;
            var rollbackActions = new Stack<Action>();

            try
            {
                rollbackActions.Push(() =>
                    primaryBuildResource.ConfigureTraversalBuild(
                        originalPrimaryProjectPaths,
                        originalPrimaryWorkingDirectory,
                        originalPrimaryBuildConfiguration));

                var buildResources = new List<DotnetProjectBuildResource>(buildSteps.Count);
                for (var index = 0; index < buildSteps.Count; index++)
                {
                    var step = buildSteps[index];
                    var buildResource = index == 0
                        ? primaryBuildResource
                        : CreateBuildResource(step.Configuration, index + 1);

                    if (index > 0)
                    {
                        rollbackActions.Push(() =>
                        {
                            _builder.Resources.Remove(buildResource);
                            _ownedBuildResources.Remove(buildResource);
                            buildResource.Dispose();
                        });
                    }

                    if (step.IsTraversal)
                    {
                        buildResource.ConfigureTraversalBuild(
                            step.Projects.Select(entry => entry.Metadata.ProjectPath),
                            step.WorkingDirectory,
                            step.Configuration);
                        rollbackActions.Push(ValidateMaterializedBuildCallbacks(buildResource, step.Projects));
                    }
                    else
                    {
                        var entry = step.Projects.Single();
                        buildResource.ConfigureDirectBuild(
                            entry.Metadata.ProjectPath,
                            step.WorkingDirectory,
                            step.Configuration);

                        // One coordinator-owned evaluation feeds the coordinated build, the rebuilder, and the IDE
                        // launch configuration, so build callbacks run once and every consumer observes identical values.
                        var sharedBuildEnvironment = new SharedBuildEnvironment(
                            entry,
                            services.GetRequiredService<ResourceNotificationService>(),
                            applicationLifetime.ApplicationStopping);
                        _sharedBuildEnvironments.Add(sharedBuildEnvironment);
                        rollbackActions.Push(() =>
                        {
                            _sharedBuildEnvironments.Remove(sharedBuildEnvironment);
                            sharedBuildEnvironment.Dispose();
                        });
                        rollbackActions.Push(ValidateMaterializedBuildCallbacks(buildResource, step.Projects));
                        rollbackActions.Push(ApplyBuildEnvironment(buildResource, sharedBuildEnvironment));
                        rollbackActions.Push(ApplyBuildProperties(buildResource, sharedBuildEnvironment));
                        rollbackActions.Push(EnsureBuildEnvironmentBeforeResourceLaunch(
                            entry.Registration.Resource,
                            sharedBuildEnvironment));
                        if (FindRebuilder(model, entry.Registration.Resource) is { } rebuilder)
                        {
                            rollbackActions.Push(ValidateMaterializedBuildCallbacks(rebuilder, step.Projects));
                            rollbackActions.Push(ApplyBuildEnvironment(rebuilder, sharedBuildEnvironment));
                            rollbackActions.Push(ApplyBuildProperties(rebuilder, sharedBuildEnvironment));
                        }
                    }

                    foreach (var entry in step.Projects)
                    {
                        var originalBuildWorkingDirectory = entry.Metadata.BuildWorkingDirectory;
                        entry.Metadata.SetBuildWorkingDirectory(step.WorkingDirectory);
                        rollbackActions.Push(() =>
                            entry.Metadata.SetBuildWorkingDirectory(originalBuildWorkingDirectory));
                    }

                    buildResource.RegisterForShutdown(applicationLifetime);
                    buildResources.Add(buildResource);
                }

                for (var index = 1; index < buildResources.Count; index++)
                {
                    if (AddBuildDependency(_builder, buildResources[index], buildResources[index - 1]) is { } rollback)
                    {
                        rollbackActions.Push(rollback);
                    }
                }

                var finalBuildResource = buildResources[^1];
                foreach (var registration in activeRegistrations)
                {
                    var resource = registration.Resource;
                    if (resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is { } metadata &&
                        IsSupportedPath(metadata.ProjectPath) &&
                        File.Exists(metadata.ProjectPath))
                    {
                        if (AddBuildDependency(_builder, resource, finalBuildResource) is { } rollback)
                        {
                            rollbackActions.Push(rollback);
                        }
                    }
                }

                foreach (var missingEntry in missingBuildEntries)
                {
                    // Do this only after every throwing plan mutation has succeeded, so a failed materialization can
                    // be retried without leaving the missing resource on a partially changed launch path.
                    ConfigureMissingPathFallback(missingEntry);
                }

                RemoveEagerBuildDependencies(inactiveResources);
                RemoveEagerBuildDependencies(missingBuildEntries.Select(entry => entry.Registration.Resource));
                _materialized = true;
            }
            catch (Exception materializationException)
            {
                var rollbackExceptions = new List<Exception>();
                while (rollbackActions.TryPop(out var rollback))
                {
                    try
                    {
                        rollback();
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackExceptions.Add(rollbackException);
                    }
                }

                if (rollbackExceptions.Count > 0)
                {
                    throw new AggregateException(
                        "Coordinated .NET project build-plan materialization failed and could not be fully rolled back.",
                        [materializationException, .. rollbackExceptions]);
                }

                throw;
            }

            return Task.CompletedTask;
        }

        private void RemoveEagerBuildDependencies(IEnumerable<DotnetProjectResource> resources)
        {
            foreach (var resource in resources)
            {
                if (_eagerDependencyRollbacks.Remove(resource, out var rollback))
                {
                    rollback();
                }
            }
        }

        private static List<BuildStep> CreateBuildSteps(IEnumerable<ProjectEntry> entries)
        {
            var entryList = entries.ToArray();
            var conflictingDuplicate = entryList
                .GroupBy(entry => entry.Metadata.ProjectPath, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1 && group.Any(RequiresContextSpecificBuild));
            if (conflictingDuplicate is not null)
            {
                var resourceNames = string.Join(
                    ", ",
                    conflictingDuplicate.Select(entry => $"'{entry.Registration.Resource.Name}'"));
                throw new DistributedApplicationException(
                    $"The .NET project '{conflictingDuplicate.Key}' is registered multiple times by resources {resourceNames}, " +
                    "and at least one registration has a project-specific build environment. The coordinated build cannot " +
                    "produce distinct outputs for the same project path.");
            }

            var steps = new List<BuildStep>();
            var traversalSteps = new Dictionary<BuildContext, BuildStep>();

            foreach (var entry in entryList)
            {
                // Resource.WorkingDirectory is the launched process's cwd and can be overridden independently.
                // SDK and global.json discovery must start at the project directory.
                var projectDirectory = Path.GetDirectoryName(entry.Metadata.ProjectPath)!;
                if (!IsProjectFile(entry.Metadata.ProjectPath))
                {
                    // File-based app artifacts are isolated, but their #:project references can share bin/obj
                    // directories. Direct build steps are serialized so those references never build concurrently.
                    steps.Add(BuildStep.CreateDirect(entry, projectDirectory));
                    continue;
                }

                if (RequiresContextSpecificBuild(entry))
                {
                    steps.Add(BuildStep.CreateDirect(entry, projectDirectory));
                    continue;
                }

                var globalJsonPath = FindNearestGlobalJson(projectDirectory);
                var context = new BuildContext(globalJsonPath, entry.Metadata.BuildConfiguration);
                if (!traversalSteps.TryGetValue(context, out var step))
                {
                    var workingDirectory = globalJsonPath is null
                        ? projectDirectory
                        : Path.GetDirectoryName(globalJsonPath)!;
                    step = BuildStep.CreateTraversal(entry, workingDirectory);
                    traversalSteps.Add(context, step);
                    steps.Add(step);
                }
                else
                {
                    step.Projects.Add(entry);
                }
            }

            return steps;
        }

        private static void ConfigureMissingPathFallback(ProjectEntry entry)
        {
            entry.Metadata.SuppressBuild = false;
            foreach (var annotation in entry.Registration.Resource.Annotations
                .OfType<SupportsDebuggingAnnotation>()
                .Where(annotation =>
                    annotation.LaunchConfigurationType == KnownLaunchConfigurationTypes.ProjectWithExternalBuild)
                .ToArray())
            {
                // The external-build debug annotation and launch-tool ownership were selected before file existence
                // was checked. Removing that capability makes the launch-tool callback emit the complete `dotnet run`
                // fallback instead of producing a legacy project configuration under the external-build capability.
                entry.Registration.Resource.Annotations.Remove(annotation);
            }
        }

        private DotnetProjectBuildResource CreateBuildResource(string? configuration, int? ordinal = null)
        {
            var name = ordinal is null
                ? BuildResourceName
                : $"{BuildResourceName}-{ordinal.Value.ToString(CultureInfo.InvariantCulture)}";
            var buildResource = AddBuildResource(
                _builder,
                name,
                configuration);
            _ownedBuildResources.Add(buildResource);
            return buildResource;
        }

        /// <summary>
        /// Copies the coordinated build environment onto a resource that performs the build (the coordinated build
        /// resource or the project rebuilder).
        /// </summary>
        private static Action ApplyBuildEnvironment(IResource target, SharedBuildEnvironment sharedBuildEnvironment)
        {
            var annotation = new EnvironmentCallbackAnnotation(sharedBuildEnvironment.ApplyBuildEnvironmentAsync);
            target.Annotations.Add(annotation);
            return () => target.Annotations.Remove(annotation);
        }

        private static Action EnsureBuildEnvironmentBeforeResourceLaunch(
            IResource target,
            SharedBuildEnvironment sharedBuildEnvironment)
        {
            // DCP resolves executable resources concurrently. This callback participates in the project resource's
            // own configuration path so launch metadata cannot be created before the coordinated build environment
            // is available, while deliberately leaving the runtime environment unchanged.
            var annotation = new EnvironmentCallbackAnnotation(sharedBuildEnvironment.EnsureEvaluatedAsync);
            target.Annotations.Add(annotation);
            return () => target.Annotations.Remove(annotation);
        }

        private static Action ApplyBuildProperties(IResource target, SharedBuildEnvironment sharedBuildEnvironment)
        {
            // Environment variables enter MSBuild as low-precedence properties. Also pass them as global properties
            // through a protected response file so the build, TargetPath query, and `dotnet run --no-build` agree when
            // the project assigns the same property without exposing its value in the process command line.
            var annotation = new CommandLineArgsCallbackAnnotation(sharedBuildEnvironment.ApplyBuildPropertiesAsync);
            target.Annotations.Add(annotation);
            return () =>
            {
                target.Annotations.Remove(annotation);
                sharedBuildEnvironment.ReleaseResponseFile(target);
            };
        }

        private static Action ValidateMaterializedBuildCallbacks(
            IResource target,
            IEnumerable<ProjectEntry> entries)
        {
            var snapshots = entries
                .Select(entry => new BuildCallbackSnapshot(
                    entry,
                    GetBuildEnvironmentCallbacks(entry).ToArray()))
                .ToArray();
            void Validate(EnvironmentCallbackContext _)
            {
                foreach (var snapshot in snapshots)
                {
                    var currentCallbacks = GetBuildEnvironmentCallbacks(snapshot.Entry);
                    if (!currentCallbacks.SequenceEqual(
                        snapshot.Callbacks,
                        ReferenceEqualityComparer.Instance))
                    {
                        throw new DistributedApplicationException(
                            $"The build environment of .NET project resource '{snapshot.Entry.Registration.Resource.Name}' " +
                            "changed after the coordinated build plan was materialized. Configure build-affecting " +
                            "environment variables with WithBuildEnvironment while constructing the AppHost or in a " +
                            "pipeline step required by BeforeStart; do not add or remove them after materialization.");
                    }
                }
            }

            var annotation = new EnvironmentCallbackAnnotation(Validate);
            target.Annotations.Add(annotation);

            return () => target.Annotations.Remove(annotation);
        }

        private static IResource? FindRebuilder(
            DistributedApplicationModel model,
            DotnetProjectResource resource) =>
            model.Resources
                .SingleOrDefault(candidate =>
                    candidate.Name == $"{resource.Name}-rebuilder" &&
                    candidate is IResourceWithParent<IResource> parent &&
                    ReferenceEquals(parent.Parent, resource));

        private static string? FindNearestGlobalJson(string workingDirectory)
        {
            var physicalWorkingDirectory = PathNormalizer.ResolveSymlinks(Path.GetFullPath(workingDirectory));
            for (var directory = new DirectoryInfo(physicalWorkingDirectory); directory is not null; directory = directory.Parent)
            {
                var globalJsonPath = Path.Combine(directory.FullName, "global.json");
                if (File.Exists(globalJsonPath))
                {
                    return PathNormalizer.ResolveToFilesystemPath(globalJsonPath);
                }
            }

            return null;
        }

        private static bool RequiresContextSpecificBuild(ProjectEntry entry)
        {
            return GetBuildEnvironmentCallbacks(entry).Any();
        }

        private static IEnumerable<DotnetProjectBuildEnvironmentCallbackAnnotation> GetBuildEnvironmentCallbacks(
            ProjectEntry entry) =>
            entry.Registration.Resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>();

        private readonly record struct BuildContext(string? GlobalJsonPath, string? Configuration);

        /// <summary>
        /// Owns the single evaluation of a project's build-only environment callbacks.
        /// </summary>
        /// <remarks>
        /// The coordinated build resource, the project rebuilder, and the IDE launch configuration must all see the
        /// same build variables. Letting each consumer evaluate the callbacks would run them several times and allow
        /// whichever consumer evaluates first to decide the build inputs.
        /// </remarks>
        private sealed class SharedBuildEnvironment : IDisposable
        {
            private readonly ProjectEntry _entry;
            private readonly ResourceNotificationService _notificationService;
            private readonly CancellationToken _applicationStopping;
            private readonly object _lock = new();
            private readonly object _responseFilesLock = new();
            private readonly Dictionary<IResource, MsBuildResponseFile> _responseFiles =
                new(ReferenceEqualityComparer.Instance);
            private Task<IReadOnlyDictionary<string, string>>? _evaluation;
            private bool _disposed;

            public SharedBuildEnvironment(
                ProjectEntry entry,
                ResourceNotificationService notificationService,
                CancellationToken applicationStopping)
            {
                _entry = entry;
                _notificationService = notificationService;
                _applicationStopping = applicationStopping;
                Callbacks = GetBuildEnvironmentCallbacks(entry).ToArray();
            }

            public DotnetProjectResource Resource => _entry.Registration.Resource;

            /// <summary>
            /// Gets the project callbacks that contribute to the coordinated build, in registration order.
            /// </summary>
            /// <remarks>
            /// The build resource validates this materialized snapshot before evaluating it, so a later mutation cannot
            /// silently produce output that differs from the project launch environment.
            /// </remarks>
            public IReadOnlyList<DotnetProjectBuildEnvironmentCallbackAnnotation> Callbacks { get; }

            /// <summary>
            /// Applies the complete coordinated build environment to the resource that runs the build.
            /// </summary>
            public async Task ApplyBuildEnvironmentAsync(EnvironmentCallbackContext context)
            {
                var evaluation = await EvaluateOnceAsync(
                    context.ExecutionContext,
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);
                foreach (var (name, value) in evaluation)
                {
                    context.EnvironmentVariables[name] = value;
                }
            }

            public async Task EnsureEvaluatedAsync(EnvironmentCallbackContext context)
            {
                _ = await EvaluateOnceAsync(
                    context.ExecutionContext,
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);
            }

            public async Task ApplyBuildPropertiesAsync(CommandLineArgsCallbackContext context)
            {
                var evaluation = await EvaluateOnceAsync(
                    context.ExecutionContext,
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);

                var responseFile = await DotnetProjectBuildEnvironment.CreateResponseFileAsync(
                    evaluation,
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);
                if (responseFile is null)
                {
                    return;
                }

                try
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var initialSnapshot = _notificationService.TryGetCurrentState(
                        context.Resource.Name,
                        out var resourceEvent)
                            ? resourceEvent.Snapshot
                            : null;
                    context.Args.Add(responseFile.Argument);
                    ReplaceResponseFile(context.Resource, responseFile);
                    _ = ReleaseResponseFileWhenSettledAsync(
                        context.Resource,
                        responseFile,
                        initialSnapshot,
                        context.Logger);
                    responseFile = null;
                }
                finally
                {
                    responseFile?.Dispose();
                }
            }

            public void ReleaseResponseFile(IResource resource)
            {
                MsBuildResponseFile? responseFile;
                lock (_responseFilesLock)
                {
                    _responseFiles.Remove(resource, out responseFile);
                }

                responseFile?.Dispose();
            }

            public void Dispose()
            {
                MsBuildResponseFile[] responseFiles;
                lock (_responseFilesLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    responseFiles = [.. _responseFiles.Values];
                    _responseFiles.Clear();
                }

                foreach (var responseFile in responseFiles)
                {
                    responseFile.Dispose();
                }
            }

            private Task<IReadOnlyDictionary<string, string>> EvaluateOnceAsync(
                DistributedApplicationExecutionContext executionContext,
                ILogger logger,
                CancellationToken cancellationToken)
            {
                Task<IReadOnlyDictionary<string, string>> evaluation;
                lock (_lock)
                {
                    // A faulted or canceled callback evaluation is deliberately not retained so a later build or rebuild
                    // can retry a transient failure. Every attempt starts from a fresh dictionary, so a retry can never
                    // observe a half-applied environment.
                    if (_evaluation is null || _evaluation.IsFaulted || _evaluation.IsCanceled)
                    {
                        _evaluation = EvaluateAsync(executionContext, logger, _applicationStopping);
                    }

                    evaluation = _evaluation;
                }

                // The shared work belongs to the application lifetime. Each consumer can cancel only its own wait,
                // without canceling the evaluation for concurrent build, rebuild, or launch consumers.
                return evaluation.WaitAsync(cancellationToken);
            }

            private async Task ReleaseResponseFileWhenSettledAsync(
                IResource resource,
                MsBuildResponseFile responseFile,
                CustomResourceSnapshot? initialSnapshot,
                ILogger logger)
            {
                try
                {
                    await _notificationService.WaitForResourceAsync(
                        resource.Name,
                        resourceEvent =>
                            !ReferenceEquals(resourceEvent.Snapshot, initialSnapshot) &&
                            (resourceEvent.Snapshot.State?.Text == KnownResourceStates.FailedToStart ||
                             KnownResourceStates.TerminalStates.Contains(resourceEvent.Snapshot.State?.Text)),
                        _applicationStopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_applicationStopping.IsCancellationRequested)
                {
                    // Application shutdown disposes the response file in the finally block.
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Failed to monitor .NET build resource '{ResourceName}' for MSBuild response-file cleanup.",
                        resource.Name);
                }
                finally
                {
                    ReleaseResponseFile(resource, responseFile);
                }
            }

            private void ReleaseResponseFile(IResource resource, MsBuildResponseFile expectedResponseFile)
            {
                MsBuildResponseFile? responseFile = null;
                lock (_responseFilesLock)
                {
                    if (_responseFiles.TryGetValue(resource, out var currentResponseFile) &&
                        ReferenceEquals(currentResponseFile, expectedResponseFile))
                    {
                        _responseFiles.Remove(resource);
                        responseFile = currentResponseFile;
                    }
                }

                responseFile?.Dispose();
            }

            private void ReplaceResponseFile(IResource resource, MsBuildResponseFile responseFile)
            {
                MsBuildResponseFile? previousResponseFile;
                lock (_responseFilesLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _responseFiles.Remove(resource, out previousResponseFile);
                    _responseFiles.Add(resource, responseFile);
                }

                previousResponseFile?.Dispose();
            }

            private async Task<IReadOnlyDictionary<string, string>> EvaluateAsync(
                DistributedApplicationExecutionContext executionContext,
                ILogger logger,
                CancellationToken cancellationToken)
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var environment = new Dictionary<string, object>(comparer);
                foreach (var callback in Callbacks)
                {
                    var callbackContext = new EnvironmentCallbackContext(
                        executionContext,
                        Resource,
                        environment,
                        cancellationToken)
                    {
                        Logger = logger,
                    };
                    await callback.Callback(callbackContext).ConfigureAwait(false);
                }

                var resolvedEnvironment = new Dictionary<string, string>(environment.Count, comparer);
                foreach (var (name, value) in environment)
                {
                    if (value is not string stringValue)
                    {
                        throw new DistributedApplicationException(
                            $"The build environment variable '{name}' for .NET project resource '{Resource.Name}' " +
                            $"has unsupported value type '{value?.GetType().Name ?? "null"}'. Build environment values must be strings.");
                    }

                    resolvedEnvironment[name] = stringValue;
                }

                _entry.Metadata.SetBuildEnvironment(resolvedEnvironment);
                return resolvedEnvironment;
            }
        }

        private sealed record ProjectEntry(
            ResourceRegistration Registration,
            DotnetProjectMetadata Metadata);

        private sealed record BuildCallbackSnapshot(
            ProjectEntry Entry,
            DotnetProjectBuildEnvironmentCallbackAnnotation[] Callbacks);

        private sealed record ResourceRegistration(DotnetProjectResource Resource);

        private sealed class BuildStep
        {
            private BuildStep(
                bool isTraversal,
                string workingDirectory,
                string? configuration,
                List<ProjectEntry> projects)
            {
                IsTraversal = isTraversal;
                WorkingDirectory = workingDirectory;
                Configuration = configuration;
                Projects = projects;
            }

            public bool IsTraversal { get; }

            public string WorkingDirectory { get; }

            public string? Configuration { get; }

            public List<ProjectEntry> Projects { get; }

            public static BuildStep CreateTraversal(ProjectEntry entry, string workingDirectory) =>
                new(
                    isTraversal: true,
                    workingDirectory,
                    entry.Metadata.BuildConfiguration,
                    [entry]);

            public static BuildStep CreateDirect(ProjectEntry entry, string workingDirectory) =>
                new(
                    isTraversal: false,
                    workingDirectory,
                    entry.Metadata.BuildConfiguration,
                    [entry]);
        }
    }
}
