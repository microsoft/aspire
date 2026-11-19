// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures coordinated initial builds for path-based .NET resources.
/// </summary>
internal static partial class DotnetProjectBuildCoordinator
{
    private const int MaxReportedBuildOutputLines = 20;
    private const int MaxReportedBuildOutputLineLength = 1000;
    private static readonly TimeSpan s_buildOutputReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConditionalWeakTable<IResource, BuildFailureState> s_buildFailures = new();

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CoordinatorState> s_states = new();

    internal const string BuildResourceName = "__dotnet-project-build";
    private const string DebugSessionPortConfigurationKey = "DEBUG_SESSION_PORT";
    private const string DebugSessionInfoConfigurationKey = "DEBUG_SESSION_INFO";
    private const string AspireStorePathConfigurationKey = "Aspire:Store:Path";
    private const string RestoreProjectsIndividuallyConfigurationKey = "Aspire:Dotnet:RestoreProjectsIndividually";

    public static CoordinatorState? Prepare(
        IDistributedApplicationBuilder builder,
        DotnetProjectMetadata projectMetadata)
    {
        var coordinationMode = GetBuildCoordinationMode(builder);
        // Coordinated builds are only needed in run mode. The build-only IDE compatibility path applies only to
        // project files; file-based apps retain their existing launch behavior.
        if (!builder.ExecutionContext.IsRunMode ||
            (coordinationMode == BuildCoordinationMode.BuildOnly && !IsProjectFile(projectMetadata.ProjectPath)))
        {
            return null;
        }

        var state = s_states.GetValue(
            builder,
            builder => new CoordinatorState(builder, coordinationMode));
        if (IsSupportedPath(projectMetadata.ProjectPath))
        {
            projectMetadata.SuppressBuild = state.SuppressIdeBuild;
            if (IsProjectFile(projectMetadata.ProjectPath))
            {
                projectMetadata.SetProjectPath(state.AddProject(projectMetadata.ProjectPath, projectMetadata.BuildConfiguration));
            }
            else
            {
                // The SDK folds case, but not Unicode normalization, in file-app cache keys:
                // https://github.com/dotnet/dotnet/blob/820230c386/src/sdk/src/Cli/Microsoft.DotNet.FileBasedPrograms/Sha256Hasher.cs#L27-L28
                // Keep the direct build, launch, and rebuilder on the same path spelling.
                projectMetadata.SetProjectPath(DotnetProjectBuildResource.ResolveBuildPathCasing(projectMetadata.ProjectPath));
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
        resourceBuilder.WithAnnotation(new DotnetProgramBuildCompletionAnnotation(
            (services, cancellationToken) =>
                state.WaitForBuildCompletionAsync(resourceBuilder.Resource, services, cancellationToken)));

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
        var aspireStoreRoot = builder.Configuration[AspireStorePathConfigurationKey];
        if (string.IsNullOrEmpty(aspireStoreRoot))
        {
            throw new InvalidOperationException(
                $"Could not determine the AppHost intermediate output path from '{AspireStorePathConfigurationKey}'.");
        }

        var buildDirectory = Path.Combine(aspireStoreRoot, ".aspire", "build");
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

        BeginBuildAttemptOnResourceStart(builder, buildResource);
        ReportBuildFailureOnResourceStopped(builder, buildResource);

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
        IResource buildResource,
        CancellationToken cancellationToken)
    {
        var notificationService = services.GetRequiredService<ResourceNotificationService>();
        var failureState = s_buildFailures.GetValue(buildResource, static _ => new BuildFailureState());
        while (true)
        {
            var attempt = failureState.Attempt;
            var buildEvent = await notificationService.WaitForResourceAsync(
                buildResource.Name,
                resourceEvent => IsSettledBuildSnapshot(resourceEvent.Snapshot),
                cancellationToken).ConfigureAwait(false);

            if (buildEvent.Snapshot.State?.Text != KnownResourceStates.FailedToStart &&
                buildEvent.Snapshot.ExitCode is 0)
            {
                if (failureState.IsCurrentAttempt(attempt))
                {
                    return;
                }

                continue;
            }

            var messageTask = failureState.GetFailureMessage(attempt, services, buildResource, buildEvent.Snapshot);
            if (messageTask is null)
            {
                // A start raced with the terminal-state wait. Do not cache an old snapshot as the new attempt's failure.
                continue;
            }

            var message = await messageTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new DistributedApplicationException(message);
        }
    }

    private static Action BeginBuildAttemptOnResourceStart(IDistributedApplicationBuilder builder, IResource resource)
    {
        var subscription = builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            static (@event, _) =>
            {
                s_buildFailures.GetValue(@event.Resource, static _ => new BuildFailureState()).BeginAttempt();
                return Task.CompletedTask;
            });
        return () => builder.Eventing.Unsubscribe(subscription);
    }

    // Dependents report the failure when their wait observes it, but an explicit-start resource with no dependents never
    // waits. Start the shared per-attempt report when the build itself settles so the AppHost log still gets the summary.
    private static void ReportBuildFailureOnResourceStopped(IDistributedApplicationBuilder builder, IResource resource)
    {
        builder.Eventing.Subscribe<ResourceStoppedEvent>(
            resource,
            static (@event, _) =>
            {
                var failureState = s_buildFailures.GetValue(@event.Resource, static _ => new BuildFailureState());
                // An earlier stopped handler can restart the build. The orchestrator publishes Starting before
                // BeforeResourceStartedEvent advances the attempt, so read the attempt first and then the current
                // snapshot: a restart is then either visible as a non-terminal state or the snapshot is already the
                // new attempt's result. Never attribute this event's (possibly stale) snapshot to a later attempt.
                var attempt = failureState.Attempt;
                if (@event.Services.GetRequiredService<ResourceNotificationService>()
                        .TryGetCurrentState(@event.ResourceEvent.ResourceId, out var current) &&
                    IsFailedBuildSnapshot(current.Snapshot))
                {
                    // Do not block event dispatch on reading the build output; waiters share the same cached task.
                    failureState.GetFailureMessage(attempt, @event.Services, @event.Resource, current.Snapshot);
                }

                return Task.CompletedTask;
            });
    }

    private static async Task<string> ReportBuildFailureAsync(
        IServiceProvider services,
        IResource buildResource,
        CustomResourceSnapshot snapshot)
    {
        var exitCode = snapshot.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        var message = $"The coordinated .NET build failed with exit code {exitCode}. See resource '{buildResource.Name}' for build output.";
        var buildOutput = await GetBuildFailureOutputAsync(services, buildResource).ConfigureAwait(false);
        if (buildOutput.Count > 0)
        {
            message += Environment.NewLine + string.Join(Environment.NewLine, buildOutput);
        }

        services.GetService<ILoggerFactory>()?
            .CreateLogger(typeof(DotnetProjectBuildCoordinator))
            .LogError("{Message}", message);
        return message;
    }

    private static async Task<IReadOnlyList<string>> GetBuildFailureOutputAsync(
        IServiceProvider services,
        IResource buildResource)
    {
        var resourceLoggerService = services.GetService<ResourceLoggerService>();
        if (resourceLoggerService is null)
        {
            return [];
        }

        var errors = new List<string>();
        // Stdout, stderr, startup and system streams are read concurrently, so delivery order is not chronological.
        // Keep the newest lines by their DCP timestamp, using delivery order for equal or missing timestamps.
        var tail = new SortedSet<BuildOutputLine>();
        long sequence = 0;
        var applicationStopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        readCancellation.CancelAfter(s_buildOutputReadTimeout);
        try
        {
            // Completed DCP process output is stored independently of live log forwarding; no subscriber is needed.
            // https://github.com/microsoft/dcp/blob/v0.27.2/internal/exerunners/process_executable_runner.go#L207-L220
            await foreach (var batch in resourceLoggerService.GetAllAsync(buildResource)
                .WithCancellation(readCancellation.Token)
                .ConfigureAwait(false))
            {
                foreach (var logLine in batch)
                {
                    // Stored DCP lines are prefixed with their timestamp, for example:
                    //   2026-11-02T22:12:48.9230608Z Program.cs(3,1): error CS0103: ...
                    // Lines without a parsable timestamp are treated as newer than every timestamped line.
                    DateTimeOffset? timestamp = null;
                    var content = logLine.Content;
                    if (TimestampParser.TryParseConsoleTimestamp(content, out var timestampResult))
                    {
                        content = timestampResult.Value.ModifiedText;
                        timestamp = timestampResult.Value.Timestamp;
                    }

                    content = content.TrimEnd();
                    if (content.Length == 0)
                    {
                        continue;
                    }

                    if (content.Length > MaxReportedBuildOutputLineLength)
                    {
                        content = content[..MaxReportedBuildOutputLineLength];
                    }

                    if (errors.Count < MaxReportedBuildOutputLines &&
                        MsBuildErrorRegex().IsMatch(content) &&
                        !errors.Contains(content, StringComparer.Ordinal))
                    {
                        errors.Add(content);
                    }

                    tail.Add(new BuildOutputLine(timestamp?.UtcDateTime ?? DateTime.MaxValue, sequence++, content));
                    if (tail.Count > MaxReportedBuildOutputLines)
                    {
                        tail.Remove(tail.Min);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The build output is best-effort diagnostics; report whatever was read before the timeout or shutdown.
        }
        catch (Exception ex)
        {
            // Reading the build output must never replace the build failure itself.
            services.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(DotnetProjectBuildCoordinator))
                .LogDebug(ex, "Failed to read the output of .NET build resource '{ResourceName}'.", buildResource.Name);
        }

        return errors.Count > 0 ? errors : [.. tail.Select(static line => line.Content)];
    }

    // Matches MSBuild canonical error lines such as "File.cs(1,2): error CS1002: ..." or "MSBUILD : error MSB1009: ...".
    [GeneratedRegex(@"(^|\s|:)error(\s+[A-Za-z]+\d+)?\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildErrorRegex();

    internal static bool IsSettledBuildSnapshot(CustomResourceSnapshot snapshot) =>
        snapshot.State?.Text == KnownResourceStates.FailedToStart ||
        (KnownResourceStates.TerminalStates.Contains(snapshot.State?.Text) &&
         snapshot.ExitCode is not null);

    private static bool IsFailedBuildSnapshot(CustomResourceSnapshot snapshot) =>
        IsSettledBuildSnapshot(snapshot) &&
        (snapshot.State?.Text == KnownResourceStates.FailedToStart || snapshot.ExitCode is not 0);

    private static bool IsProjectFile(string path) =>
        ProjectPathResolver.IsSupportedDotnetProjectFile(path);

    private static bool IsSupportedPath(string path) =>
        IsProjectFile(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static BuildCoordinationMode GetBuildCoordinationMode(IDistributedApplicationBuilder builder)
    {
        if (string.IsNullOrEmpty(builder.Configuration[DebugSessionPortConfigurationKey]))
        {
            return BuildCoordinationMode.BuildAndSuppress;
        }

        // A launcher without a valid capability payload cannot opt into the external-build contract. Visual Studio's
        // F5 build covers the startup project's MSBuild dependencies, but path-based project resources are
        // intentionally not ProjectReference items. Build those projects before sending the unchanged legacy
        // "project" request so Visual Studio has output to launch, while keeping IDE build suppression disabled.
        // See https://github.com/microsoft/aspire/issues/21865.
        if (!DebugSessionInfoParser.TryGetSupportedLaunchConfigurations(
                builder.Configuration[DebugSessionInfoConfigurationKey],
                out var supportedLaunchConfigurations)
            || supportedLaunchConfigurations is null)
        {
            return BuildCoordinationMode.BuildOnly;
        }

        // The IDE honors coordinated-build values and build suppression.
        if (supportedLaunchConfigurations.Contains(KnownLaunchConfigurationTypes.ProjectWithExternalBuild))
        {
            return BuildCoordinationMode.BuildAndSuppress;
        }

        // The ordinary "project" capability says that an IDE can launch a project, not that it builds the project
        // before launch. Prebuild while preserving the legacy launch payload and leaving IDE build suppression
        // disabled. An IDE without "project" support runs the resource as a process and needs the fully coordinated
        // external-build path, including suppression of the implicit `dotnet run` build.
        return supportedLaunchConfigurations.Contains(KnownLaunchConfigurationTypes.Project)
            ? BuildCoordinationMode.BuildOnly
            : BuildCoordinationMode.BuildAndSuppress;
    }

    internal enum BuildCoordinationMode
    {
        BuildOnly,
        BuildAndSuppress
    }

    internal sealed class CoordinatorState : IDisposable
    {
        private readonly IDistributedApplicationBuilder _builder;
        private readonly BuildCoordinationMode _coordinationMode;
        private readonly object _materializationLock = new();
        private readonly List<ResourceRegistration> _registrations = [];
        private readonly List<DotnetProjectBuildResource> _ownedBuildResources = [];
        private readonly List<SharedBuildEnvironment> _sharedBuildEnvironments = [];
        private readonly Dictionary<DotnetProjectResource, Action> _eagerDependencyRollbacks =
            new(ReferenceEqualityComparer.Instance);
        private bool _materialized;
        private bool _materializationInProgress;
        private bool _disposed;
        private TaskCompletionSource _materializationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CoordinatorState(IDistributedApplicationBuilder builder, BuildCoordinationMode coordinationMode)
        {
            _builder = builder;
            _coordinationMode = coordinationMode;
            builder.Services.AddSingleton(_ => this);
            builder.Pipeline.WithFinalAction(
                WellKnownPipelineSteps.BeforeStart,
                stepContext => stepContext.Services
                    .GetRequiredService<CoordinatorState>()
                    .MaterializeBuildPlan(stepContext.Model, stepContext.Services));
        }

        public bool SuppressIdeBuild => _coordinationMode == BuildCoordinationMode.BuildAndSuppress;

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

        public async Task WaitForBuildCompletionAsync(
            DotnetProjectResource resource,
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            Task materializationTask;
            lock (_materializationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                materializationTask = _materializationCompletion.Task;
            }

            // BeforeStartEvent subscribers can request build output before the BeforeStart pipeline's final
            // action creates the plan. Waiting here must not block that event or depend on application readiness.
            await materializationTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            var registration = _registrations.Single(
                registration => ReferenceEquals(registration.Resource, resource));
            if (registration.FinalBuildResource is { } finalBuildResource)
            {
                // Do not cache this wait: another command can arrive during a later build attempt.
                await WaitForSuccessfulBuildAsync(services, finalBuildResource, cancellationToken).ConfigureAwait(false);

                // The initial build barrier stays finished when the project's separate rebuilder runs.
                // Once that resource has been used, its latest attempt must also succeed before consumers
                // can use the output. Do not wait on a dormant, explicitly started rebuilder.
                var model = services.GetRequiredService<DistributedApplicationModel>();
                var notifications = services.GetRequiredService<ResourceNotificationService>();
                if (FindRebuilder(model, resource) is { } rebuilder &&
                    notifications.TryGetCurrentState(rebuilder.Name, out var rebuildEvent) &&
                    rebuildEvent.Snapshot.State?.Text is { } state &&
                    (state == KnownResourceStates.Starting ||
                     state == KnownResourceStates.Running ||
                     state == KnownResourceStates.Waiting ||
                     state == KnownResourceStates.Building ||
                     state == KnownResourceStates.Stopping ||
                     KnownResourceStates.TerminalStates.Contains(state)))
                {
                    await WaitForSuccessfulBuildAsync(services, rebuilder, cancellationToken).ConfigureAwait(false);
                }
            }
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
            lock (_materializationLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _materializationCompletion.TrySetCanceled();
            }

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
            TaskCompletionSource completion;
            lock (_materializationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                completion = _materializationCompletion;
                if (_materializationInProgress)
                {
                    return completion.Task;
                }

                _materializationInProgress = true;
            }

            // Consumers must remain able to capture this attempt while the synchronous model mutation runs.
            try
            {
                MaterializeBuildPlanCore(model, services);
            }
            catch (Exception ex)
            {
                lock (_materializationLock)
                {
                    _materializationInProgress = false;
                    if (!_disposed)
                    {
                        // Existing consumers observe this failed attempt; subsequent consumers can await a retry.
                        _materializationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    completion.TrySetException(ex);
                }

                throw;
            }

            lock (_materializationLock)
            {
                _materializationInProgress = false;
                completion.TrySetResult();
            }

            return Task.CompletedTask;
        }

        private void MaterializeBuildPlanCore(
            DistributedApplicationModel model,
            IServiceProvider services)
        {
            if (_materialized)
            {
                return;
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
                DotnetProjectHostingExtensions.ValidateBuildEnvironmentSupport(
                    unsupportedBuildEnvironmentEntry.Registration.Resource,
                    unsupportedBuildEnvironmentEntry.Metadata);
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

            if (_coordinationMode == BuildCoordinationMode.BuildOnly &&
                buildEntries.FirstOrDefault(entry => GetBuildEnvironmentCallbacks(entry).Any()) is { } customizedEntry)
            {
                throw new DistributedApplicationException(
                    $"The .NET project resource '{customizedEntry.Registration.Resource.Name}' uses " +
                    $"WithBuildEnvironment, but Aspire can use only the legacy " +
                    $"'{KnownLaunchConfigurationTypes.Project}' launch contract for the active IDE session and cannot " +
                    $"reliably launch output produced with project-specific build settings. Use an IDE that advertises " +
                    $"'{KnownLaunchConfigurationTypes.ProjectWithExternalBuild}', run the application outside this IDE, " +
                    "or remove the build customization.");
            }

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
                return;
            }

            var restoreProjectsIndividually = _builder.Configuration.GetValue<bool>(RestoreProjectsIndividuallyConfigurationKey);
            var applicationLifetime = services.GetRequiredService<IHostApplicationLifetime>();
            var primaryBuildResource = PrimaryBuildResource!;
            var originalPrimaryProjectPaths = primaryBuildResource.ProjectPaths;
            var originalPrimaryWorkingDirectory = primaryBuildResource.WorkingDirectory;
            var originalPrimaryBuildConfiguration = primaryBuildResource.BuildConfiguration;
            var originalPrimaryRestoreProjectsIndividually = primaryBuildResource.RestoreProjectsIndividually;
            var rollbackActions = new Stack<Action>();

            try
            {
                foreach (var entry in buildEntries)
                {
                    ReconcileFileAppPath(model, entry, rollbackActions);
                }

                var buildSteps = CreateBuildSteps(buildEntries);
                rollbackActions.Push(() =>
                    primaryBuildResource.ConfigureTraversalBuild(
                        originalPrimaryProjectPaths,
                        originalPrimaryWorkingDirectory,
                        originalPrimaryBuildConfiguration,
                        originalPrimaryRestoreProjectsIndividually));

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
                            step.Configuration,
                            restoreProjectsIndividually);
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
                        rollbackActions.Push(BeginBuildEnvironmentAttemptOnResourceStart(
                            _builder,
                            buildResource,
                            sharedBuildEnvironment));
                        rollbackActions.Push(ValidateMaterializedBuildCallbacks(buildResource, step.Projects));
                        rollbackActions.Push(ApplyBuildEnvironment(buildResource, sharedBuildEnvironment));
                        rollbackActions.Push(ApplyBuildProperties(buildResource, sharedBuildEnvironment));
                        rollbackActions.Push(EnsureBuildEnvironmentBeforeResourceLaunch(
                            entry.Registration.Resource,
                            sharedBuildEnvironment));
                        if (FindRebuilder(model, entry.Registration.Resource) is { } rebuilder)
                        {
                            rollbackActions.Push(BeginBuildEnvironmentAttemptOnResourceStart(
                                _builder,
                                rebuilder,
                                sharedBuildEnvironment));
                            rollbackActions.Push(ValidateMaterializedBuildCallbacks(rebuilder, step.Projects));
                            rollbackActions.Push(ApplyBuildEnvironment(rebuilder, sharedBuildEnvironment));
                            rollbackActions.Push(ApplyBuildProperties(rebuilder, sharedBuildEnvironment));
                        }
                    }

                    foreach (var entry in step.Projects)
                    {
                        if (FindRebuilder(model, entry.Registration.Resource) is { } rebuilder)
                        {
                            rollbackActions.Push(BeginBuildAttemptOnResourceStart(_builder, rebuilder));
                        }

                        if (SuppressIdeBuild)
                        {
                            var originalBuildWorkingDirectory = entry.Metadata.BuildWorkingDirectory;
                            entry.Metadata.SetBuildWorkingDirectory(step.WorkingDirectory);
                            rollbackActions.Push(() =>
                                entry.Metadata.SetBuildWorkingDirectory(originalBuildWorkingDirectory));
                        }
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
                        var originalFinalBuildResource = registration.FinalBuildResource;
                        registration.FinalBuildResource = finalBuildResource;
                        rollbackActions.Push(() =>
                            registration.FinalBuildResource = originalFinalBuildResource);

                        if (AddBuildDependency(_builder, resource, finalBuildResource) is { } rollback)
                        {
                            rollbackActions.Push(rollback);
                        }
                    }
                }

                if (_coordinationMode == BuildCoordinationMode.BuildOnly)
                {
                    foreach (var fileApp in model.Resources.OfType<DotnetProjectResource>())
                    {
                        var metadata = fileApp.Annotations.OfType<IProjectMetadata>().SingleOrDefault();
                        if (metadata is not { IsFileBasedApp: true } || !File.Exists(metadata.ProjectPath))
                        {
                            continue;
                        }

                        // Legacy IDE sessions keep file apps on their existing launch path. Order that implicit build
                        // after the coordinated project build because #:project references can share bin/obj outputs.
                        if (AddBuildDependency(_builder, fileApp, finalBuildResource) is { } rollback)
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
        }

        // Prepare resolves a file app's spelling at registration, but the file may not exist yet; then only its existing
        // ancestors are resolved. Resolve again now that it exists so the direct build, `run --file --no-build`, the
        // rebuilder, and their default working directories use one spelling and therefore one SDK cache entry.
        private static void ReconcileFileAppPath(
            DistributedApplicationModel model,
            ProjectEntry entry,
            Stack<Action> rollbackActions)
        {
            var originalPath = entry.Metadata.ProjectPath;
            if (IsProjectFile(originalPath))
            {
                return;
            }

            var resolvedPath = DotnetProjectBuildResource.ResolveBuildPathCasing(originalPath);
            if (resolvedPath.Equals(originalPath, StringComparison.Ordinal))
            {
                return;
            }

            entry.Metadata.SetProjectPath(resolvedPath);
            rollbackActions.Push(() => entry.Metadata.SetProjectPath(originalPath));

            var originalDirectory = Path.GetDirectoryName(originalPath);
            var resolvedDirectory = Path.GetDirectoryName(resolvedPath)!;
            IResource?[] launchResources = [entry.Registration.Resource, FindRebuilder(model, entry.Registration.Resource)];
            foreach (var resource in launchResources)
            {
                // Explicit or SDK-selected working directories belong to the caller; only move the registration default.
                if (resource?.Annotations.OfType<ExecutableAnnotation>().LastOrDefault() is { } executable &&
                    !executable.WorkingDirectoryExplicitlySet &&
                    string.Equals(executable.WorkingDirectory, originalDirectory, StringComparison.Ordinal))
                {
                    executable.WorkingDirectory = resolvedDirectory;
                    rollbackActions.Push(() => executable.WorkingDirectory = originalDirectory!);
                }
            }
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

        private static Action BeginBuildEnvironmentAttemptOnResourceStart(
            IDistributedApplicationBuilder builder,
            IResource target,
            SharedBuildEnvironment sharedBuildEnvironment)
        {
            var subscription = builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
                target,
                (@event, cancellationToken) => sharedBuildEnvironment.BeginBuildAttemptAsync(
                    target,
                    @event.Services.GetRequiredService<ResourceLoggerService>().GetLogger(target),
                    cancellationToken));
            return () => builder.Eventing.Unsubscribe(subscription);
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
        /// The coordinated build resource and project rebuilder must see the same build variables. External-build IDE
        /// launch configurations consume the same snapshot; legacy IDE launch configurations retain their unchanged
        /// payload. Letting each applicable consumer evaluate the callbacks would run them several times and allow
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
            private BuildEnvironmentGeneration _generation = new(precedingEvaluation: null);
            private Task _buildAttemptTail = Task.CompletedTask;
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

            public async Task BeginBuildAttemptAsync(
                IResource resource,
                ILogger logger,
                CancellationToken cancellationToken)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task precedingAttempt;
                lock (_lock)
                {
                    precedingAttempt = _buildAttemptTail;
                    _buildAttemptTail = completion.Task;
                }

                try
                {
                    await precedingAttempt.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var initialSnapshot = _notificationService.TryGetCurrentState(
                        resource.Name,
                        out var resourceEvent)
                            ? resourceEvent.Snapshot
                            : null;

                    lock (_lock)
                    {
                        // Materialization creates a pending generation so eager project or IDE configuration can evaluate
                        // the same snapshot that the initial coordinated build will use. Later starts are rebuild attempts
                        // and must observe mutable callback inputs again.
                        if (!_generation.IsClaimedByBuildAttempt)
                        {
                            _generation.IsClaimedByBuildAttempt = true;
                        }
                        else
                        {
                            _generation = new BuildEnvironmentGeneration(
                                _generation.Evaluation?.Task ?? _generation.PrecedingEvaluation)
                            {
                                IsClaimedByBuildAttempt = true,
                            };
                        }
                    }

                    _ = CompleteBuildAttemptWhenSettledAsync(
                        resource,
                        initialSnapshot,
                        completion,
                        logger);
                }
                catch
                {
                    _ = CompleteSkippedBuildAttemptAsync(precedingAttempt, completion);
                    throw;
                }
            }

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
                BuildEnvironmentGeneration generation;
                BuildEnvironmentEvaluation evaluation;
                lock (_lock)
                {
                    generation = _generation;
                    // A faulted or canceled callback evaluation is deliberately not retained so a later build or rebuild
                    // can retry a transient failure. Every attempt starts from a fresh dictionary, so a retry can never
                    // observe a half-applied environment.
                    if (generation.Evaluation is null ||
                        generation.Evaluation.IsAbandoned ||
                        generation.Evaluation.Task.IsFaulted ||
                        generation.Evaluation.Task.IsCanceled)
                    {
                        var precedingEvaluation =
                            generation.Evaluation?.Task ??
                            generation.PrecedingEvaluation;
                        var evaluationCancellationSource =
                            CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
                        generation.Evaluation = new BuildEnvironmentEvaluation(
                            EvaluateAsync(
                                generation,
                                precedingEvaluation,
                                executionContext,
                                logger,
                                evaluationCancellationSource.Token),
                            evaluationCancellationSource);
                    }

                    evaluation = generation.Evaluation;
                    evaluation.ConsumerCount++;
                }

                return WaitForEvaluationAsync(evaluation, cancellationToken);
            }

            private async Task<IReadOnlyDictionary<string, string>> WaitForEvaluationAsync(
                BuildEnvironmentEvaluation evaluation,
                CancellationToken cancellationToken)
            {
                try
                {
                    return await evaluation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseEvaluationConsumer(evaluation);
                }
            }

            private void ReleaseEvaluationConsumer(BuildEnvironmentEvaluation evaluation)
            {
                var cancelAndDispose = false;
                var dispose = false;
                lock (_lock)
                {
                    evaluation.ConsumerCount--;
                    if (evaluation.ConsumerCount == 0 && !evaluation.CleanupScheduled)
                    {
                        evaluation.CleanupScheduled = true;
                        if (evaluation.Task.IsCompleted)
                        {
                            dispose = true;
                        }
                        else
                        {
                            // The evaluation remains shared while any build, rebuild, or launch consumer needs it.
                            // Once abandoned, cancel its callback token so a later generation is not permanently
                            // serialized behind work whose initiating operation no longer exists.
                            evaluation.IsAbandoned = true;
                            cancelAndDispose = true;
                        }
                    }
                }

                if (cancelAndDispose)
                {
                    _ = CancelAndDisposeWhenSettledAsync(evaluation);
                }
                else if (dispose)
                {
                    evaluation.CancellationSource.Dispose();
                }
            }

            private static async Task CancelAndDisposeWhenSettledAsync(BuildEnvironmentEvaluation evaluation)
            {
                try
                {
                    await evaluation.CancellationSource.CancelAsync()
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await ((Task)evaluation.Task)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                finally
                {
                    evaluation.CancellationSource.Dispose();
                }
            }

            private async Task<IReadOnlyDictionary<string, string>> EvaluateAsync(
                BuildEnvironmentGeneration generation,
                Task<IReadOnlyDictionary<string, string>>? precedingEvaluation,
                DistributedApplicationExecutionContext executionContext,
                ILogger logger,
                CancellationToken cancellationToken)
            {
                if (precedingEvaluation is not null)
                {
                    // Build attempts normally cannot overlap, but the rebuilder and coordinated-build resources are
                    // distinct DCP resources. Serialize their callbacks defensively because user callbacks need not be
                    // thread-safe, while allowing a failed prior generation to be retried with new inputs.
                    await ((Task)precedingEvaluation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                cancellationToken.ThrowIfCancellationRequested();

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

                lock (_lock)
                {
                    // An older evaluation can finish after a new build attempt has started. Do not let its snapshot
                    // replace the metadata used by run-property resolution and IDE launch configuration.
                    if (ReferenceEquals(_generation, generation))
                    {
                        _entry.Metadata.SetBuildEnvironment(resolvedEnvironment);
                    }
                }

                return resolvedEnvironment;
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
                            IsSettledBuildSnapshot(resourceEvent.Snapshot),
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

            private async Task CompleteBuildAttemptWhenSettledAsync(
                IResource resource,
                CustomResourceSnapshot? initialSnapshot,
                TaskCompletionSource completion,
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
                    // Application shutdown ends every queued build attempt.
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to monitor .NET build resource '{ResourceName}' for build-attempt serialization. " +
                        "Further build attempts will remain blocked to avoid concurrent writes.",
                        resource.Name);
                    await Task.Delay(Timeout.InfiniteTimeSpan, _applicationStopping)
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                finally
                {
                    completion.TrySetResult();
                }
            }

            private static async Task CompleteSkippedBuildAttemptAsync(
                Task precedingAttempt,
                TaskCompletionSource completion)
            {
                await precedingAttempt.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                completion.TrySetResult();
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

        }

        private sealed record ProjectEntry(
            ResourceRegistration Registration,
            DotnetProjectMetadata Metadata);

        private sealed record BuildCallbackSnapshot(
            ProjectEntry Entry,
            DotnetProjectBuildEnvironmentCallbackAnnotation[] Callbacks);

        private sealed class ResourceRegistration(DotnetProjectResource resource)
        {
            public DotnetProjectResource Resource { get; } = resource;

            public DotnetProjectBuildResource? FinalBuildResource { get; set; }
        }

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

        private sealed class BuildEnvironmentGeneration(
            Task<IReadOnlyDictionary<string, string>>? precedingEvaluation)
        {
            public Task<IReadOnlyDictionary<string, string>>? PrecedingEvaluation { get; } = precedingEvaluation;

            public BuildEnvironmentEvaluation? Evaluation { get; set; }

            public bool IsClaimedByBuildAttempt { get; set; }
        }

        private sealed class BuildEnvironmentEvaluation(
            Task<IReadOnlyDictionary<string, string>> task,
            CancellationTokenSource cancellationSource)
        {
            public Task<IReadOnlyDictionary<string, string>> Task { get; } = task;

            public CancellationTokenSource CancellationSource { get; } = cancellationSource;

            public int ConsumerCount { get; set; }

            public bool IsAbandoned { get; set; }

            public bool CleanupScheduled { get; set; }
        }
    }

    private readonly record struct BuildOutputLine(DateTime Timestamp, long Sequence, string Content) : IComparable<BuildOutputLine>
    {
        public int CompareTo(BuildOutputLine other) =>
            Timestamp.CompareTo(other.Timestamp) is var result and not 0 ? result : Sequence.CompareTo(other.Sequence);
    }

    private sealed class BuildFailureState
    {
        private readonly object _lock = new();
        private long _attempt;
        private Lazy<Task<string>>? _message;

        public long Attempt
        {
            get
            {
                lock (_lock)
                {
                    return _attempt;
                }
            }
        }

        public void BeginAttempt()
        {
            lock (_lock)
            {
                _attempt++;
                _message = null;
            }
        }

        public bool IsCurrentAttempt(long attempt)
        {
            lock (_lock)
            {
                return attempt == _attempt;
            }
        }

        public Task<string>? GetFailureMessage(
            long attempt,
            IServiceProvider services,
            IResource resource,
            CustomResourceSnapshot snapshot)
        {
            lock (_lock)
            {
                if (attempt != _attempt)
                {
                    return null;
                }

                // Metadata updates replace snapshots without starting a new build.
                _message ??= new(() => ReportBuildFailureAsync(services, resource, snapshot));
                return _message.Value;
            }
        }
    }
}
