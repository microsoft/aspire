// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // RunConfiguration is experimental.
#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental.
#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchArgsOverrideAnnotation is experimental.

using System.Globalization;
using System.IO.Hashing;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// A hidden, infrastructure-only watch server that hosts <c>Microsoft.DotNet.HotReload.Watch.Aspire server</c> for a
/// group of watched .NET projects in one AppHost run.
/// </summary>
internal sealed class DotnetWatchServerResource(string name, string workingDirectory)
    : ExecutableResource(name, "dotnet", workingDirectory);

/// <summary>
/// Coordinates evaluated build ownership, contained service watchers, and their resource proxies.
/// </summary>
/// <remarks>
/// <para>
/// The AppHost's resource default and final per-resource overrides select compatible <c>AddDotnetProject</c>
/// resources for a <see cref="DotnetWatchServerResource"/>. Preflight evaluates SDKs, build contexts,
/// references, and output domains before handing build ownership to compatible watcher groups. Shared outputs belong
/// to one compatible watcher or retain ordinary build ownership; custom contexts and file apps can require dedicated
/// groups. The bundled watch host supervises the pinned watcher, whose initial build is the only service build.
/// A failed source build waits for a fix instead of launching stale output. Each service
/// instance is launched through the watch host's <c>proxy</c> command, which reads the service
/// environment from its own process environment (never <c>-e</c> arguments) and sends it over the watcher's resource
/// pipe.
/// </para>
/// </remarks>
internal static class DotnetWatchCoordinator
{
    internal const string ServerResourceName = "dotnet-watch";

    internal const string ForwardedEnvironmentNamesVariable = DotnetWatchHostProtocol.ForwardedEnvironmentNamesVariable;
    internal const string RestoreEnvironmentNamesVariable = DotnetWatchHostProtocol.RestoreEnvironmentNamesVariable;

    internal const string CrashedState = "Crashed";

    /// <summary>
    /// Exact logger category for intentional, EventId-tagged resource-watch notices.
    /// </summary>
    internal const string NoticeLoggerCategory = "Aspire.Hosting.Dotnet.Watch";

    /// <summary>
    /// The event id of a notice logged to <see cref="NoticeLoggerCategory"/>: <see cref="NoticeEventIdBase"/> plus the
    /// <see cref="WatchNoticeKind"/>, which the AppHost log backchannel carries to the CLI alongside the text.
    /// </summary>
    internal const int NoticeEventIdBase = 7100;

    internal static EventId NoticeEventId(WatchNoticeKind kind) => new(NoticeEventIdBase + (int)kind, $"WatchNotice.{kind}");

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, WatchState> s_states = new();
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, object> s_warned = new();

    // Keyed by resource: IResourceBuilder.ApplicationBuilder can be a different wrapper than the builder Configure saw.
    private static readonly ConditionalWeakTable<DotnetProjectResource, WatchState> s_projectStates = new();
    private static readonly ConditionalWeakTable<DotnetProjectResource, WatchRegistration> s_registrations = new();

    /// <summary>Temp-directory variables of the platform (what <see cref="Path.GetTempPath"/> reads).</summary>
    internal static string[] GetTempVariables(bool windows) => windows ? ["TMP", "TEMP"] : ["TMPDIR"];

    /// <summary>Whether watch was requested for this run (<c>AppHost:Run:WatchEnabled</c> in run mode).</summary>
    internal static bool IsWatchRun(IDistributedApplicationBuilder builder) =>
        builder.ExecutionContext.IsRunMode && builder.ExecutionContext.RunConfiguration.WatchEnabled;

    /// <summary>
    /// Whether <c>AddDotnetProject</c> should route this run through the watch server.
    /// </summary>
    /// <remarks>
    /// Under an IDE-owned debug session the IDE owns change tracking: no service is watched, and the terminal and each
    /// service's log say so. Nothing else is required of the launcher: <c>aspire run --watch</c> and a plain
    /// <c>dotnet run</c> of the AppHost with <c>AppHost:Run:WatchEnabled=true</c> behave the same.
    /// </remarks>
    internal static bool ShouldWatch(IDistributedApplicationBuilder builder)
    {
        return IsWatchRun(builder) && CanWatch(builder);
    }

    private static bool CanWatch(IDistributedApplicationBuilder builder)
    {
        if (!builder.ExecutionContext.IsRunMode ||
            builder.Configuration[ResourceWatch.SuppressionVariable] is ResourceWatch.Discovery or ResourceWatch.Native)
        {
            return false;
        }

        if (IsIdeOwned(builder))
        {
            OnceForBuilder(builder, () => builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
            {
                e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogWarning(
                    NoticeEventId(WatchNoticeKind.Excluded),
                    "Resource watching is handled by the IDE; this run does not start service watchers.");
                return Task.CompletedTask;
            }));
            return false;
        }

        return true;
    }

    private static bool IsIdeOwned(IDistributedApplicationBuilder builder) =>
        !string.IsNullOrEmpty(builder.Configuration["ASPIRE_EXTENSION_DEBUG_SESSION_ID"]) ||
        (!DotnetWatchOwnership.IsCliOwned(builder.Configuration[DotnetWatchOwnership.OwnerEnvironmentVariable]) &&
            (!string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_PORT"]) ||
                !string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_INFO"]) ||
                !string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_RUN_MODE"])));

    /// <summary>Per-resource variant of <see cref="ShouldWatch(IDistributedApplicationBuilder)"/>.</summary>
    /// <remarks>
    /// Whether the resource is actually watched is decided once the model is final (see <see cref="Evaluate"/>), because
    /// <c>WithWatch</c>, <c>WaitForCompletion</c>, lifetimes and launch tools are configured after <c>AddDotnetProject</c>.
    /// </remarks>
    internal static bool ShouldWatch(IDistributedApplicationBuilder builder, string resourceName)
    {
        var watch = ShouldWatch(builder);
        if (!watch && IsWatchRun(builder) && IsIdeOwned(builder))
        {
            builder.Eventing.Subscribe<BeforeStartEvent>((e, cancellationToken) =>
            {
                if (e.Model.Resources.FirstOrDefault(r => r.Name == resourceName) is { } resource)
                {
                    e.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource).LogInformation(
                        "[watch] Hot reload is handled by the IDE debug session; 'aspire run --watch' doesn't watch this service.");
                }

                return Task.CompletedTask;
            });
        }

        return watch;
    }

    private static void OnceForBuilder(IDistributedApplicationBuilder builder, Action action)
    {
        if (s_warned.TryGetValue(builder, out _))
        {
            return;
        }

        s_warned.Add(builder, new object());
        action();
    }

    internal static void Register(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> resource,
        DotnetProjectMetadata metadata,
        DotnetProjectBuildCoordinator.CoordinatorState? buildCoordinator)
    {
        s_registrations.AddOrUpdate(resource.Resource, new(builder, resource, metadata, buildCoordinator));
        if (builder.ExecutionContext.IsRunMode)
        {
            builder.Eventing.Subscribe<BeforeStartEvent>((e, cancellationToken) =>
            {
                if (e.Model.Resources.Contains(resource.Resource) && !s_projectStates.TryGetValue(resource.Resource, out _))
                {
                    var reason = builder.Configuration[ResourceWatch.SuppressionVariable] switch
                    {
                        ResourceWatch.NoBuild => "Resource watching is disabled by --no-build.",
                        ResourceWatch.Discovery => "Resource watching is unavailable during discovery.",
                        ResourceWatch.Native => "Resource watching is unavailable with the native server.",
                        _ when IsIdeOwned(builder) =>
                            "Resource watching is handled by the IDE.",
                        _ when resource.Resource.Annotations.OfType<DotnetProjectWatchAnnotation>().LastOrDefault()?.Enabled == false =>
                            "Resource watching is disabled by WithWatch(false).",
                        _ => "The AppHost resource-watch default is disabled."
                    };
                    ProjectExcludedStatus(resource.Resource, reason);
                    e.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource.Resource)
                        .LogInformation("[watch] {Reason}", reason);
                }

                return Task.CompletedTask;
            });
        }
        if (ShouldWatch(builder, resource.Resource.Name))
        {
            Enable(resource);
        }
    }

    internal static void Enable(IResourceBuilder<DotnetProjectResource> resource)
    {
        if (!CanWatch(resource.ApplicationBuilder) || s_projectStates.TryGetValue(resource.Resource, out _))
        {
            return;
        }

        if (!s_registrations.TryGetValue(resource.Resource, out var registration))
        {
            throw new DistributedApplicationException(
                $"Resource '{resource.Resource.Name}' must be registered with AddDotnetProject before watch can be required.");
        }

        var state = s_states.GetValue(registration.Builder, static b => new WatchState(b));
        s_projectStates.AddOrUpdate(resource.Resource, state);
        state.Configure(registration.Resource, registration.Metadata, registration.BuildCoordinator);
    }

    /// <summary>
    /// Called by <c>WithBuildEnvironment</c>: build-only variables must reach the watcher's builds of this project and
    /// no other, so the project moves to a watch server of its own.
    /// </summary>
    internal static void OnBuildEnvironmentAdded(IResourceBuilder<DotnetProjectResource> resource)
    {
        if (s_projectStates.TryGetValue(resource.Resource, out var state))
        {
            state.UseDedicatedServer(resource);
        }
    }

    /// <summary>Test hook: reports a watcher <c>build_complete</c> status to the server that watches <paramref name="resource"/>.</summary>
    internal static Task ReportBuildCompleteForTestingAsync(
        DotnetProjectResource resource, bool success, IServiceProvider services, CancellationToken cancellationToken)
    {
        return GetWatchedProjectForTesting(resource).Server.OnBuildCompleteForTestingAsync(
            success, services, cancellationToken);
    }

    /// <summary>Test hook: proves a successful build event does not wait behind a restart awaiting <c>process_started</c>.</summary>
    internal static Task ReportBuildCompleteWhileRestartLockedForTestingAsync(
        DotnetProjectResource resource, IServiceProvider services, CancellationToken cancellationToken) =>
        GetWatchedProjectForTesting(resource).Server.OnBuildCompleteWhileRestartLockedForTestingAsync(
            services, cancellationToken);

    /// <summary>Test hook: reports a build-input edit and waits for the resulting guarded maintenance pass.</summary>
    internal static Task ReportProjectFileChangeForTestingAsync(
        DotnetProjectResource resource, string path, IServiceProvider services, CancellationToken cancellationToken) =>
        GetWatchedProjectForTesting(resource).Server.OnProjectFileChangeForTestingAsync(
            path, services, cancellationToken);

    private static WatchedProject GetWatchedProjectForTesting(DotnetProjectResource resource)
    {
        if (!s_projectStates.TryGetValue(resource, out var state) ||
            state.Projects.Find(project => ReferenceEquals(project.Resource, resource)) is not { } project)
        {
            throw new InvalidOperationException($"'{resource.Name}' isn't watched.");
        }

        return project;
    }

    private static string? GetProjectLaunchConfigurationType(IResource resource) =>
        resource.Annotations.OfType<SupportsDebuggingAnnotation>().LastOrDefault()?.LaunchConfigurationType;

    /// <summary>
    /// Whether something other than the watch coordinator owns this resource's launch (for example, the Blazor
    /// gateway runs the Gateway CLI tool, MAUI overrides the <c>dotnet</c> arguments). Such resources can't be
    /// launched through the watch proxy.
    /// </summary>
    internal static bool HasCustomLaunch(IResource resource)
    {
        if (resource.Annotations.OfType<ProjectLaunchArgsOverrideAnnotation>().Any())
        {
            return true;
        }

        // The last launch tool wins. The watch proxy is owned by the project launch configuration type (see Configure),
        // so any launch tool declared after it, which replaced it, isn't.
        return resource.Annotations.OfType<SupportsDebuggingAnnotation>().LastOrDefault() is { } debugSupport
            && !resource.HasLaunchToolArgsOwnedBy(debugSupport);
    }

    /// <summary>
    /// Decides whether a registered project is watched, once the model is final.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>WithWatch(false)</c> always wins: the project runs without hot reload, and says so.</description></item>
    /// <item><description>A project that another resource waits on with <c>WaitForCompletion</c> runs to completion. The watcher
    /// relaunches a watched service when it exits instead of letting it complete (#9756), so it's excluded, visibly; with an
    /// explicit <c>WithWatch(true)</c> the run fails instead.</description></item>
    /// <item><description>Persistent lifetimes and custom launches can't be watched and fail the run with
    /// an error that names the opt-out.</description></item>
    /// </list>
    /// </remarks>
    internal static WatchDecision Evaluate(DotnetProjectResource resource, IEnumerable<IResource> model)
    {
        var requested = resource.Annotations.OfType<DotnetProjectWatchAnnotation>().LastOrDefault()?.Enabled;
        if (requested == false)
        {
            return new(WatchDecisionKind.Excluded, $"'{resource.Name}' runs without hot reload (WithWatch(false)).");
        }

        var awaitedBy = model
            .Where(r => !ReferenceEquals(r, resource) && r.Annotations.OfType<WaitAnnotation>().Any(w =>
                ReferenceEquals(w.Resource, resource) && w.WaitType == WaitType.WaitForCompletion))
            .Select(r => $"'{r.Name}'")
            .ToArray();
        if (awaitedBy.Length > 0)
        {
            var waiters = string.Join(", ", awaitedBy);
            return requested == true
                ? new(WatchDecisionKind.Rejected,
                    $"'{resource.Name}' requires hot reload (WithWatch(true)), but {waiters} waits for it to complete (WaitForCompletion), " +
                    "and a watched service is relaunched instead of completing. Remove WithWatch(true) to run it once without hot reload.")
                : new(WatchDecisionKind.Excluded,
                    $"'{resource.Name}' runs without hot reload because {waiters} waits for it to complete (WaitForCompletion); " +
                    "a watched service would be relaunched instead of completing.");
        }

        if (HasPersistentLifetime(resource, []))
        {
            return new(requested == true ? WatchDecisionKind.Rejected : WatchDecisionKind.Excluded,
                $"'{resource.Name}' has a persistent lifetime, but a watched service belongs to this run's watch server and can't outlive it.");
        }

        if (HasCustomLaunch(resource))
        {
            return new(requested == true ? WatchDecisionKind.Rejected : WatchDecisionKind.Excluded,
                $"'{resource.Name}' uses a custom launch (WithLaunchToolArgs or ProjectLaunchArgsOverrideAnnotation), which the watcher can't run.");
        }

        return new(WatchDecisionKind.Watched, null);
    }

#pragma warning disable ASPIREPERSISTENCE001 // PersistenceAnnotation is experimental.
    private static bool HasPersistentLifetime(IResource resource, HashSet<IResource> visited)
    {
        if (!visited.Add(resource))
        {
            return false;
        }

        if (resource.Annotations.OfType<PersistenceAnnotation>().LastOrDefault() is { } persistence)
        {
            return persistence.Mode switch
            {
                PersistenceMode.Persistent or PersistenceMode.ParentProcess => true,
                PersistenceMode.Resource => persistence.SourceResource is { } source && HasPersistentLifetime(source, visited),
                _ => false
            };
        }

        return resource.Annotations.OfType<ContainerLifetimeAnnotation>().LastOrDefault()?.Lifetime == ContainerLifetime.Persistent;
    }
#pragma warning restore ASPIREPERSISTENCE001

    /// <summary>
    /// Applies <see cref="Evaluate"/> to every registered project at <see cref="BeforeStartEvent"/>: watched projects
    /// leave Aspire's coordinated build (the watcher builds them), excluded projects get their normal launch and
    /// coordinated build back and a notice in the terminal and their log; unsupported ones fail the run together.
    /// </summary>
    /// <remarks>
    /// This runs before the coordinated build plan is created (the BeforeStart pipeline step follows
    /// <see cref="BeforeStartEvent"/>). The watch servers start only after the coordinated build of the excluded projects
    /// has finished (see <see cref="WatchServer"/>), so the two builds don't race over shared project outputs.
    /// </remarks>
    private static List<WatchedProject> ApplyEligibility(
        List<WatchedProject> projects, DistributedApplicationModel model, IServiceProvider services,
        IDistributedApplicationBuilder builder)
    {
        var errors = new List<string>();
        var excluded = new List<(WatchedProject Project, string Reason)>();
        foreach (var project in projects)
        {
            var requested = project.Resource.Annotations.OfType<DotnetProjectWatchAnnotation>().LastOrDefault()?.Enabled;
            var decision = builder.Configuration[ResourceWatch.SuppressionVariable] == ResourceWatch.NoBuild
                ? new WatchDecision(requested == true ? WatchDecisionKind.Rejected : WatchDecisionKind.Excluded,
                    $"'{project.Resource.Name}' cannot be watched with --no-build.")
                : requested is null && !builder.ExecutionContext.RunConfiguration.WatchEnabled
                    ? new WatchDecision(WatchDecisionKind.Excluded,
                        $"'{project.Resource.Name}' runs without hot reload because the AppHost resource-watch default is disabled.")
                    : Evaluate(project.Resource, model.Resources);
            if (decision.Kind == WatchDecisionKind.Rejected)
            {
                errors.Add(decision.Reason!);
            }
            else if (decision.Kind == WatchDecisionKind.Excluded)
            {
                excluded.Add((project, decision.Reason!));
            }
        }

        if (errors.Count > 0)
        {
            throw new DistributedApplicationException(
                "Required resource watching cannot be satisfied for these .NET projects:" + Environment.NewLine +
                string.Concat(errors.Select(e => $"  - {e}{Environment.NewLine}")) +
                "Call WithWatch(false) on a project to run it without hot reload.");
        }

        projects.RemoveAll(p => excluded.Any(x => ReferenceEquals(x.Project, p)));

        var noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
        var loggers = services.GetRequiredService<ResourceLoggerService>();
        foreach (var (project, reason) in excluded)
        {
            foreach (var annotation in project.Annotations)
            {
                project.Resource.Annotations.Remove(annotation);
            }

            noticeLogger.LogInformation(NoticeEventId(WatchNoticeKind.Excluded), "{Notice}", reason);
            loggers.GetLogger(project.Resource).LogInformation("[watch] {Notice}", reason);
            ProjectExcludedStatus(project.Resource, reason);
        }

        return excluded.Select(x => x.Project).ToList();
    }

    internal enum WatchDecisionKind
    {
        Watched,
        Excluded,
        Rejected
    }

    internal readonly record struct WatchDecision(WatchDecisionKind Kind, string? Reason);

    /// <summary>
    /// The directory of the <c>global.json</c> that governs SDK selection for <paramref name="directory"/>, or
    /// <see langword="null"/> when none does. Projects with different scopes may resolve different SDKs, and a watcher
    /// process loads exactly one.
    /// </summary>
    internal static string? FindSdkScope(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static readonly TimeSpan s_projectFileDebounce = TimeSpan.FromSeconds(1);

    private static void ProjectExcludedStatus(DotnetProjectResource resource, string reason)
    {
        var previous = resource.Annotations.OfType<ResourceSnapshotProjectionAnnotation>().LastOrDefault();
        resource.Annotations.Add(new ResourceSnapshotProjectionAnnotation((id, snapshot) =>
            DotnetWatchMember.WithWatchStatus(previous is null ? snapshot : previous.Projection(id, snapshot),
                ResourceWatch.Excluded, reason, resource.Name)));
    }

    internal static bool PathsEqual(string a, string b) =>
        DotnetWatchProjectGraph.PathComparer.Equals(
            DotnetWatchProjectGraph.Normalize(a), DotnetWatchProjectGraph.Normalize(b));

    private sealed record WatchRegistration(
        IDistributedApplicationBuilder Builder,
        IResourceBuilder<DotnetProjectResource> Resource,
        DotnetProjectMetadata Metadata,
        DotnetProjectBuildCoordinator.CoordinatorState? BuildCoordinator);

    private sealed class WatchedProject(
        DotnetProjectResource resource,
        DotnetProjectMetadata metadata,
        DotnetProjectBuildCoordinator.CoordinatorState? buildCoordinator,
        WatchServer server)
    {
        public DotnetProjectResource Resource { get; } = resource;
        public DotnetProjectMetadata Metadata { get; } = metadata;

        /// <summary>Aspire's coordinated build, which builds the project instead of the watcher if it isn't watched.</summary>
        public DotnetProjectBuildCoordinator.CoordinatorState? BuildCoordinator { get; } = buildCoordinator;
        public string ProjectPath => ((IProjectMetadata)Metadata).ProjectPath;

        /// <summary>Annotations the coordinator added (removed again if something else takes over the launch).</summary>
        public List<IResourceAnnotation> Annotations { get; } = [];

        public WatchServer Server { get; set; } = server;
        public DotnetWatchMember? Member { get; set; }
        public DotnetWatchProjectGraphResult? OrdinaryGraph { get; set; }
        public DotnetWatchProjectGraphResult? Graph { get; set; }
        public IReadOnlyDictionary<string, string> BuildEnvironment { get; set; } = new Dictionary<string, string>();
        public DotnetWatchResolvedPayload? Payload { get; set; }
    }

    private sealed class WatchState : IDisposable, IAsyncDisposable, IHostedLifecycleService
    {
        private readonly List<WatchServer> _servers = [];

        public WatchState(IDistributedApplicationBuilder builder)
        {
            Builder = builder;
            Id = Guid.NewGuid().ToString("N")[..12];
            builder.Services.TryAddSingleton<IDotnetWatchPreflight, DotnetWatchPreflight>();

            // Holds the Unix sockets and redirected temp directories. Deleted when the AppHost stops. Kept short:
            // Unix socket paths are limited to ~104 (macOS) / 108 (Linux) characters.
            builder.Services.AddSingleton(_ => this);
            // Run()/RunAsync() stop the host without disposing its services. Stop owned
            // watchers before DCP's StopAsync tears down their commands and notifications.
            builder.Services.AddHostedService(services => services.GetRequiredService<WatchState>());
            builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, step =>
            {
                if (Projects.Count == 0)
                {
                    return Task.CompletedTask;
                }
                foreach (var (resource, metadata) in _metadata)
                {
                    if (DotnetProjectBuildCoordinator.CoordinatorState.FindRebuilder(step.Model, resource) is not { } rebuilder)
                    {
                        continue;
                    }
                    // Register after build-plan materialization so the coordinator begins
                    // the rebuilder's attempt before this guard consumes its cached inputs.
                    builder.Eventing.Subscribe<BeforeResourceStartedEvent>(rebuilder, async (e, cancellationToken) =>
                    {
                        if (Projects.Any(project => ReferenceEquals(project.Resource, resource)))
                        {
                            throw new DistributedApplicationException(DotnetWatchMember.RebuildExplanation);
                        }
                        if (resource is DotnetProjectResource dotnet &&
                            ExcludedProjects.FirstOrDefault(project => ReferenceEquals(project.Resource, resource))?.BuildCoordinator is { } coordinator)
                        {
                            await coordinator.EnsureBuildEnvironmentEvaluatedAsync(dotnet, rebuilder, e.Services, cancellationToken).ConfigureAwait(false);
                        }
                        CaptureOrdinaryBuildEnvironment(resource, metadata);
                        await ValidateOrdinaryBuildAsync(resource, metadata, e.Services, cancellationToken).ConfigureAwait(false);
                    });
                }
                foreach (var build in step.Model.Resources.OfType<DotnetProjectBuildResource>())
                {
                    var entries = _metadata.Where(pair => build.ProjectPaths.Any(path => PathsEqual(path, pair.Value.ProjectPath)))
                        .ToArray();
                    builder.Eventing.Subscribe<BeforeResourceStartedEvent>(build, async (e, cancellationToken) =>
                    {
                        foreach (var (resource, metadata) in entries)
                        {
                            if (resource is DotnetProjectResource dotnet &&
                                ExcludedProjects.FirstOrDefault(project => ReferenceEquals(project.Resource, resource))?.BuildCoordinator is { } coordinator)
                            {
                                await coordinator.EnsureBuildEnvironmentEvaluatedAsync(dotnet, build, e.Services, cancellationToken).ConfigureAwait(false);
                            }
                            // Preflight can inspect callbacks before plan materialization. Check
                            // the actual cached attempt before an ordinary initial build can write.
                            CaptureOrdinaryBuildEnvironment(resource, metadata);
                            await ValidateOrdinaryBuildAsync(resource, metadata, e.Services, cancellationToken).ConfigureAwait(false);
                        }
                    });
                }
                return Task.CompletedTask;
            });

            builder.Eventing.Subscribe<BeforeStartEvent>(async (e, ct) =>
            {
                _ = e.Services.GetRequiredService<WatchState>();
                _logger = e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DotnetWatchCoordinator).FullName!);
                // Resources removed or replaced after registration must not be built or watched.
                Projects.RemoveAll(p => !e.Model.Resources.Contains(p.Resource));

                // Opt-outs, run-to-completion and unsupported projects can only be decided on the final model.
                ExcludedProjects = ApplyEligibility(Projects, e.Model, e.Services, Builder);
                await PreflightAsync(e.Model, e.Services, ct).ConfigureAwait(false);

                // Preflight is the last ordinary-fallback boundary. Once removed, the watcher
                // owns the sole initial build and no infrastructure failure may downgrade it.
                foreach (var project in Projects)
                {
                    ConfigureLaunch(project);
                    project.BuildCoordinator?.RemoveResource(project.Resource);
                    project.Server.ConfigureLifecycle(project);
                }

                var active = new List<WatchServer>();
                foreach (var server in _servers)
                {
                    if (Projects.Any(p => p.Server == server))
                    {
                        active.Add(server);
                    }
                    else
                    {
                        e.Model.Resources.Remove(server.Resource);
                    }
                }

                if (active.Count == 0)
                {
                    return;
                }

                // The forwarded-name list must be computed after every user/integration environment callback, so it
                // is appended once the model is final.
                foreach (var project in Projects)
                {
                    var watched = project;
                    watched.Resource.Annotations.Add(new EnvironmentCallbackAnnotation(async ctx =>
                    {
                        foreach (var name in DotnetWatchOwnership.PrivateEnvironmentVariables)
                        {
                            ctx.EnvironmentVariables.Remove(name);
                        }
                        ctx.EnvironmentVariables.Remove(DotnetWatchHostProtocol.CleanupMarkerVariable);
                        ctx.EnvironmentVariables[ForwardedEnvironmentNamesVariable] = string.Join(';', ctx.EnvironmentVariables.Keys
                            .Where(k => k is not (ForwardedEnvironmentNamesVariable or RestoreEnvironmentNamesVariable)));

                        var prepared = await watched.Server.PrepareAsync(ctx.ExecutionContext.Services, ctx.CancellationToken).ConfigureAwait(false);
                        ctx.EnvironmentVariables[RestoreEnvironmentNamesVariable] = string.Join(';', prepared.WatcherOnlyNames);
                    }));
                }

                var lifetime = e.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
                foreach (var server in active)
                {
                    server.Start(e.Services, lifetime.ApplicationStopping);
                }

            });
        }

        public IDistributedApplicationBuilder Builder { get; }
        public string Id { get; }
        private string? _runDirectory;
        private string? _shortTempRoot;
        private ILogger? _logger;
        public string RunDirectory => _runDirectory ?? Path.Combine(Path.GetTempPath(), $"aspw-{Id}");

        /// <summary>
        /// Path of the watch host, which proxies each replica and hosts the watcher. It ships in this package, so it's
        /// found from the AppHost's metadata alone (no SDK resolution) and the same for every server.
        /// </summary>
        public string GetWatchHost() =>
            _watchHost ??= DotnetWatchToolResolver.ResolveWatchHostPath(
                DotnetWatchToolResolver.GetWatchHostDirectory(Builder.AppHostAssembly));

        private string? _watchHost;
        public List<WatchedProject> Projects { get; } = [];

        /// <summary>Registered projects that run without hot reload, built by Aspire's coordinated build.</summary>
        public IReadOnlyList<WatchedProject> ExcludedProjects { get; private set; } = [];

        private readonly Dictionary<IResource, DotnetWatchProjectGraphResult> _evaluatedRoots =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IResource, IProjectMetadata> _metadata = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IResource, IReadOnlyDictionary<string, string>> _buildEnvironments = new(ReferenceEqualityComparer.Instance);

        private async Task PreflightAsync(DistributedApplicationModel model, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (Projects.Count == 0)
            {
                return;
            }
            _runDirectory ??= DotnetWatchServerInfrastructure.CreatePrivateDirectory();

            var evaluator = services.GetRequiredService<IDotnetWatchPreflight>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DotnetWatchCoordinator).FullName!);
            var reasons = new Dictionary<WatchedProject, string>();
            var unknown = new List<string>();

            // A watcher has one process-wide build context. Distinct configurations need
            // separate servers even when their SDK scopes happen to be identical.
            foreach (var server in _servers.ToArray())
            {
                var group = Projects.Where(project => project.Server == server).ToArray();
                if (group.Length == 0)
                {
                    continue;
                }
                var firstConfiguration = group[0].Metadata.BuildConfiguration ?? "Debug";
                foreach (var project in group.Skip(1))
                {
                    if (!string.Equals(firstConfiguration, project.Metadata.BuildConfiguration ?? "Debug", StringComparison.OrdinalIgnoreCase))
                    {
                        UseDedicatedServer(Builder.CreateResourceBuilder(project.Resource));
                    }
                }
            }

            foreach (var resource in model.Resources)
            {
                if (resource.Annotations.OfType<IProjectMetadata>().SingleOrDefault() is not { } metadata)
                {
                    continue;
                }
                _metadata[resource] = metadata;
                var project = Projects.FirstOrDefault(candidate => ReferenceEquals(candidate.Resource, resource));
                var configuration = metadata is DotnetProjectMetadata dotnet ? dotnet.BuildConfiguration : null;
                try
                {
                    var buildEnvironment = resource is DotnetProjectResource dotnetResource
                        ? await WatchServer.EvaluateBuildEnvironmentAsync(dotnetResource, services, logger, cancellationToken).ConfigureAwait(false)
                        : metadata.BuildEnvironment;
                    _buildEnvironments[resource] = buildEnvironment;
                    var ordinary = await evaluator.EvaluateAsync(metadata.ProjectPath, configuration, buildEnvironment,
                        globalProperties: true, GetOrdinaryWorkingDirectory(resource, metadata), cancellationToken).ConfigureAwait(false);
                    _evaluatedRoots[resource] = ordinary;
                    if (project is null)
                    {
                        if (ExcludedProjects.FirstOrDefault(candidate => ReferenceEquals(candidate.Resource, resource)) is { } excluded)
                        {
                            excluded.OrdinaryGraph = ordinary;
                        }
                        continue;
                    }
                    project.OrdinaryGraph = ordinary;

                    var watcherEnvironment = GetBuildProperties([project.Metadata]);
                    foreach (var (name, value) in buildEnvironment)
                    {
                        watcherEnvironment[name] = value;
                    }
                    var graph = await evaluator.EvaluateAsync(metadata.ProjectPath, configuration, watcherEnvironment,
                        globalProperties: false, Builder.AppHostDirectory, cancellationToken).ConfigureAwait(false);
                    project.Graph = graph;
                    project.BuildEnvironment = watcherEnvironment;
                    if (GetCompatibilityReason(project, ordinary, graph, buildEnvironment) is { } incompatible)
                    {
                        reasons[project] = incompatible;
                        continue;
                    }

                    project.Payload = await evaluator.ResolvePayloadAsync(graph, Builder, logger, cancellationToken).ConfigureAwait(false);
                }
                catch (DistributedApplicationException exception)
                {
                    if (project is null)
                    {
                        unknown.Add($"Build isolation for ordinary resource '{resource.Name}' is unknown: {exception.Message}");
                    }
                    else
                    {
                        reasons[project] = exception.Message;
                        if (!_evaluatedRoots.ContainsKey(resource))
                        {
                            unknown.Add($"Build isolation for '{resource.Name}' is unknown.");
                        }
                    }
                }
            }

            if (unknown.Count > 0)
            {
                // Unknown outputs cannot be proved independent by directory heuristics.
                foreach (var project in Projects)
                {
                    reasons.TryAdd(project, string.Join(" ", unknown));
                }
            }

            bool ownershipChanged;
            do
            {
                ApplyFinalOwnerGraphs(reasons);
                ownershipChanged = false;
                foreach (var component in GetOutputComponents())
                {
                    var watched = Projects.Where(project =>
                        !reasons.ContainsKey(project) && component.Contains(project.Resource)).ToArray();
                    if (watched.Length == 0)
                    {
                        continue;
                    }

                    var ordinary = component.Where(resource =>
                        watched.All(project => !ReferenceEquals(project.Resource, resource))).ToArray();
                    var reason = ordinary.Length > 0
                        ? DescribeOwnershipConflict(component, watched, ordinary, reasons)
                        : watched.Skip(1).Any(project => !SameBuildContext(watched[0], project))
                            ? $"Resources {FormatResourceNames(watched.Select(project => project.Resource))} share build outputs but require incompatible SDK or build-environment contexts."
                            : null;
                    if (reason is not null)
                    {
                        foreach (var project in watched)
                        {
                            ownershipChanged |= reasons.TryAdd(project, reason);
                        }
                    }
                }
            }
            while (ownershipChanged);

            ApplyFinalOwnerGraphs(reasons);
            foreach (var component in GetOutputComponents())
            {
                var watched = Projects.Where(project =>
                    !reasons.ContainsKey(project) && component.Contains(project.Resource)).ToArray();
                if (watched.Length > 1)
                {
                    // Shared references and duplicate entry points belong to one watcher,
                    // while every logical resource/replica retains its own proxy connection.
                    foreach (var project in watched.Skip(1))
                    {
                        MoveToServer(project, watched[0].Server);
                    }
                }
            }

            foreach (var server in _servers)
            {
                var group = Projects.Where(project => project.Server == server && !reasons.ContainsKey(project)).ToArray();
                if (group.Length == 0)
                {
                    continue;
                }
                var multipleRoots = group.Select(project => DotnetWatchProjectGraph.Normalize(project.ProjectPath))
                    .Distinct(DotnetWatchProjectGraph.PathComparer).Count() > 1;
                if (multipleRoots)
                {
                    try
                    {
                        var defaultTempGraph = await evaluator.EvaluateAsync(
                            group[0].ProjectPath,
                            group[0].Metadata.BuildConfiguration,
                            group[0].BuildEnvironment,
                            globalProperties: false,
                            Path.GetTempPath(),
                            cancellationToken).ConfigureAwait(false);
                        if (DotnetWatchProjectGraph.Compare(group[0].Graph!, defaultTempGraph).Kind !=
                            DotnetWatchGraphComparisonKind.Equivalent)
                        {
                            server.TempDirectory = CreateSdkTempDirectory(server, group[0].Graph!.SdkVersion);
                        }
                    }
                    catch (DistributedApplicationException)
                    {
                        server.TempDirectory = CreateSdkTempDirectory(server, group[0].Graph!.SdkVersion);
                    }
                }
                foreach (var project in group)
                {
                    var buildDirectory = server.TempDirectory ??
                        (multipleRoots ? Path.GetTempPath() : Path.GetDirectoryName(project.ProjectPath)!);
                    try
                    {
                        var buildGraph = await evaluator.EvaluateAsync(project.ProjectPath, project.Metadata.BuildConfiguration,
                            project.BuildEnvironment, globalProperties: false, buildDirectory, cancellationToken).ConfigureAwait(false);
                        var comparison = DotnetWatchProjectGraph.Compare(project.Graph!, buildGraph);
                        if (comparison.Kind != DotnetWatchGraphComparisonKind.Equivalent)
                        {
                            foreach (var affected in group)
                            {
                                reasons.TryAdd(affected,
                                    $"The watcher initial-build and evaluation contexts differ ({comparison.Kind}): {comparison.Describe()}.");
                            }
                        }
                    }
                    catch (DistributedApplicationException exception)
                    {
                        foreach (var affected in group)
                        {
                            reasons.TryAdd(affected, exception.Message);
                        }
                    }
                }
            }

            var required = reasons.Where(pair =>
                pair.Key.Resource.Annotations.OfType<DotnetProjectWatchAnnotation>().LastOrDefault()?.Enabled == true).ToArray();
            foreach (var (project, reason) in reasons)
            {
                logger.LogWarning("Resource '{Resource}' cannot be watched: {Reason}", project.Resource.Name, reason);
                services.GetRequiredService<ResourceLoggerService>().GetLogger(project.Resource)
                    .LogWarning("[watch] {Reason}", reason);
            }
            if (required.Length > 0)
            {
                throw new DistributedApplicationException("Explicit WithWatch(true) could not be satisfied during preflight:" +
                    Environment.NewLine + FormatResourceNames(required.Select(pair => pair.Key.Resource)) +
                    ". See the affected resource logs for SDK, runtime, or build-ownership details.");
            }
            foreach (var project in reasons.Keys)
            {
                Projects.Remove(project);
                foreach (var annotation in project.Annotations)
                {
                    project.Resource.Annotations.Remove(annotation);
                }
                ProjectExcludedStatus(project.Resource,
                    "The SDK, runtime, or build context is incompatible with watching. See this resource's logs.");
            }
            if (reasons.Count > 0)
            {
                services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogWarning(
                    NoticeEventId(WatchNoticeKind.Excluded),
                    "Resources {Resources} run without watching because their SDK, runtime, or build context is incompatible. " +
                    "Ordinary build ownership is retained; see their resource logs for details.",
                    FormatResourceNames(reasons.Keys.Select(project => project.Resource)));
            }
            ExcludedProjects = [.. ExcludedProjects, .. reasons.Keys];
            if (Projects.Count > 0)
            {
                foreach (var (resource, metadata) in _metadata.Where(pair =>
                    Projects.All(project => !ReferenceEquals(project.Resource, pair.Key))))
                {
                    resource.Annotations.Add(new ResourceLifecycleCommandOverrideAnnotation(KnownResourceCommands.RebuildCommand,
                        original => DotnetWatchMember.CloneCommand(original, original.UpdateState, async context =>
                        {
                            try
                            {
                                await ValidateOrdinaryBuildAsync(resource, metadata, context.Services, context.CancellationToken).ConfigureAwait(false);
                            }
                            catch (DistributedApplicationException exception)
                            {
                                context.Logger.LogError(exception, "Ordinary rebuild conflicts with active watch ownership.");
                                return CommandResults.Failure(exception.Message);
                            }
                            return await original.ExecuteCommand(context).ConfigureAwait(false);
                        })));
                }
            }
        }

        private void ApplyFinalOwnerGraphs(IReadOnlyDictionary<WatchedProject, string> reasons)
        {
            foreach (var project in Projects)
            {
                if (project.OrdinaryGraph is null)
                {
                    continue;
                }

                _evaluatedRoots[project.Resource] = reasons.ContainsKey(project)
                    ? project.OrdinaryGraph
                    : project.Graph ?? project.OrdinaryGraph;
            }
        }

        private List<List<IResource>> GetOutputComponents()
        {
            var components = new List<List<IResource>>();
            var remaining = new HashSet<IResource>(_evaluatedRoots.Keys, ReferenceEqualityComparer.Instance);
            while (remaining.Count > 0)
            {
                var component = new List<IResource> { remaining.First() };
                remaining.Remove(component[0]);
                for (var index = 0; index < component.Count; index++)
                {
                    foreach (var candidate in remaining.ToArray())
                    {
                        if (DotnetWatchProjectGraph.SharesOutputs(
                            _evaluatedRoots[component[index]], _evaluatedRoots[candidate]))
                        {
                            component.Add(candidate);
                            remaining.Remove(candidate);
                        }
                    }
                }
                components.Add(component);
            }

            return components;
        }

        private string DescribeOwnershipConflict(
            IReadOnlyList<IResource> component,
            IReadOnlyList<WatchedProject> watched,
            IReadOnlyList<IResource> ordinary,
            IReadOnlyDictionary<WatchedProject, string> reasons)
        {
            var sharedProjects = watched
                .SelectMany(project => ordinary.SelectMany(resource =>
                    DotnetWatchProjectGraph.SharedProjects(_evaluatedRoots[project.Resource], _evaluatedRoots[resource])))
                .Distinct(DotnetWatchProjectGraph.PathComparer)
                .ToArray();
            var affected = FormatResourceNames(component);
            var ownership = sharedProjects.Length > 0
                ? $"share physical project{(sharedProjects.Length == 1 ? "" : "s")} {string.Join(", ", sharedProjects.Select(path => $"'{path}'"))}"
                : "have overlapping evaluated output or intermediate directories";
            var rejectedDetails = ordinary
                .Select(resource => Projects.FirstOrDefault(project => ReferenceEquals(project.Resource, resource)))
                .OfType<WatchedProject>()
                .Where(reasons.ContainsKey)
                .Select(project => $" '{project.Resource.Name}' retained ordinary ownership because {reasons[project]}")
                .ToArray();
            return $"Resources {affected} {ownership}; automatic watch builds and ordinary builds cannot own them concurrently." +
                string.Concat(rejectedDetails);
        }

        private static string FormatResourceNames(IEnumerable<IResource> resources) =>
            string.Join(", ", resources.Select(resource => $"'{resource.Name}'"));

        private static string GetOrdinaryWorkingDirectory(IResource resource, IProjectMetadata metadata)
        {
            if (!string.IsNullOrEmpty(metadata.BuildWorkingDirectory))
            {
                return metadata.BuildWorkingDirectory;
            }
            var directory = Path.GetDirectoryName(metadata.ProjectPath)!;
            return !metadata.IsFileBasedApp && !resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>().Any()
                ? FindSdkScope(directory) ?? directory : directory;
        }

        private string CreateSdkTempDirectory(WatchServer server, string sdkVersion)
        {
            var root = OperatingSystem.IsWindows()
                ? RunDirectory
                : _shortTempRoot ??= DotnetWatchServerInfrastructure.CreatePrivateDirectory(
                    "/tmp", $"aspw-{Id}");
            var directory = Path.Combine(root, $"t{_servers.IndexOf(server)}");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, DotnetWatchServerInfrastructure.PrivateDirectoryMode);
            }
            File.WriteAllText(Path.Combine(directory, "global.json"),
                $$"""{ "sdk": { "version": "{{sdkVersion}}", "rollForward": "disable", "allowPrerelease": true } }""");
            DotnetWatchServerInfrastructure.ValidateCoreFxPipePath(directory);
            return directory;
        }

        private readonly object _inventoryGate = new();

        public string[] GuardedBuildInputs(WatchServer server)
        {
            lock (_inventoryGate)
            {
                return [.. _evaluatedRoots.Where(pair =>
                    Projects.All(project => !ReferenceEquals(project.Resource, pair.Key)) ||
                    Projects.Any(project => project.Server == server && ReferenceEquals(project.Resource, pair.Key)))
                    .SelectMany(pair => pair.Value.Projects.Values)
                    .SelectMany(node => node.Imports.Append(node.Path)).Distinct(DotnetWatchProjectGraph.PathComparer)];
            }
        }

        public List<WatchedProject> GetExcludedBuildBarriers(WatchServer server)
        {
            lock (_inventoryGate)
            {
                var watchedGraphs = Projects.Where(project => project.Server == server)
                    .Select(project => project.Graph!)
                    .ToArray();
                return ExcludedProjects.Where(project =>
                    project.BuildCoordinator is not null &&
                    project.OrdinaryGraph is { } ordinary &&
                    watchedGraphs.Any(graph => DotnetWatchProjectGraph.SharesOutputs(graph, ordinary)))
                    .ToList();
            }
        }

        private async Task<Dictionary<IResource, DotnetWatchProjectGraphResult>> EvaluateOrdinaryRootsAsync(
            IServiceProvider services, CancellationToken cancellationToken)
        {
            var evaluator = services.GetRequiredService<IDotnetWatchPreflight>();
            var results = new Dictionary<IResource, DotnetWatchProjectGraphResult>(ReferenceEqualityComparer.Instance);
            foreach (var (resource, metadata) in _metadata.Where(pair =>
                Projects.All(project => !ReferenceEquals(project.Resource, pair.Key))))
            {
                results[resource] = await evaluator.EvaluateAsync(metadata.ProjectPath,
                    metadata is DotnetProjectMetadata dotnet ? dotnet.BuildConfiguration : null,
                    GetOrdinaryBuildEnvironment(resource, metadata), globalProperties: true,
                    GetOrdinaryWorkingDirectory(resource, metadata), cancellationToken).ConfigureAwait(false);
            }
            return results;
        }

        private void CaptureOrdinaryBuildEnvironment(IResource resource, IProjectMetadata metadata)
        {
            lock (_inventoryGate)
            {
                _buildEnvironments[resource] = metadata.BuildEnvironment;
            }
        }

        private IReadOnlyDictionary<string, string> GetOrdinaryBuildEnvironment(IResource resource, IProjectMetadata metadata)
        {
            lock (_inventoryGate)
            {
                return _buildEnvironments.GetValueOrDefault(resource) ?? metadata.BuildEnvironment;
            }
        }

        private async Task ValidateOrdinaryBuildAsync(
            IResource resource, IProjectMetadata metadata, IServiceProvider services, CancellationToken cancellationToken)
        {
            var evaluator = services.GetRequiredService<IDotnetWatchPreflight>();
            // Watch groups revalidate after their first successful restore and whenever an observed input changes.
            // Re-evaluating an unrelated group here can race that restore while it creates generated NuGet imports,
            // making an independent ordinary build incorrectly fence the watcher.
            var current = await evaluator.EvaluateAsync(metadata.ProjectPath,
                metadata is DotnetProjectMetadata dotnet ? dotnet.BuildConfiguration : null,
                GetOrdinaryBuildEnvironment(resource, metadata), globalProperties: true,
                GetOrdinaryWorkingDirectory(resource, metadata), cancellationToken).ConfigureAwait(false);
            lock (_inventoryGate)
            {
                foreach (var project in Projects)
                {
                    if (DotnetWatchProjectGraph.SharesOutputs(current, project.Graph!))
                    {
                        var reason = $"Ordinary build '{resource.Name}' now overlaps watched output ownership for '{project.Resource.Name}'. Restart the complete watch run.";
                        project.Server.RequestFailure(reason);
                        throw new DotnetWatchIncompatibleGraphException(reason);
                    }
                }
                _evaluatedRoots[resource] = current;
            }
        }

        public async Task<bool> RevalidateAsync(
            WatchServer server, IServiceProvider services, bool commit, bool evaluateCallbacks, CancellationToken cancellationToken)
        {
            var evaluator = services.GetRequiredService<IDotnetWatchPreflight>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(server.Resource);
            var ordinary = await EvaluateOrdinaryRootsAsync(services, cancellationToken).ConfigureAwait(false);
            var replacements = new Dictionary<WatchedProject, DotnetWatchProjectGraphResult>();
            var structureChanged = false;
            foreach (var project in Projects.Where(project => project.Server == server))
            {
                var environment = new Dictionary<string, string>(project.BuildEnvironment, StringComparer.OrdinalIgnoreCase);
                if (evaluateCallbacks)
                {
                    environment = GetBuildProperties([project.Metadata]);
                    foreach (var (name, value) in await WatchServer.EvaluateBuildEnvironmentAsync(
                        project.Resource, services, logger, cancellationToken).ConfigureAwait(false))
                    {
                        environment[name] = value;
                    }
                }
                if (environment.Count != project.BuildEnvironment.Count ||
                    environment.Any(pair => !project.BuildEnvironment.TryGetValue(pair.Key, out var value) || value != pair.Value))
                {
                    throw new DotnetWatchIncompatibleGraphException(
                        "The watch build environment changed. Restart the complete watch run.");
                }
                DotnetWatchProjectGraphResult current;
                DotnetWatchGraphComparison comparison;
                for (var attempt = 0; ; attempt++)
                {
                    current = await evaluator.EvaluateAsync(project.ProjectPath, project.Metadata.BuildConfiguration,
                        environment, globalProperties: false, Builder.AppHostDirectory, cancellationToken).ConfigureAwait(false);
                    comparison = DotnetWatchProjectGraph.Compare(project.Graph!, current);
                    if (comparison.Kind == DotnetWatchGraphComparisonKind.Incompatible)
                    {
                        throw new DotnetWatchIncompatibleGraphException(
                            $"The observed watch graph became incompatible: {comparison.Describe()}. Restart the complete watch run.");
                    }
                    var build = await evaluator.EvaluateAsync(project.ProjectPath, project.Metadata.BuildConfiguration, environment,
                        globalProperties: false, server.TempDirectory ?? Path.GetDirectoryName(project.ProjectPath)!, cancellationToken).ConfigureAwait(false);
                    var buildComparison = DotnetWatchProjectGraph.Compare(current, build);
                    if (buildComparison.Kind == DotnetWatchGraphComparisonKind.Equivalent)
                    {
                        break;
                    }

                    // The active watcher can restore a newly discovered project between these evaluations:
                    // the first graph omits obj/<project>.nuget.g.props/targets while the second includes them.
                    // Retry one complete pair after a refresh-only mismatch so ownership is decided from one
                    // settled inventory. Incompatible context differences are never retried.
                    if (attempt == 0 && buildComparison.Kind == DotnetWatchGraphComparisonKind.RefreshRequired)
                    {
                        logger.LogDebug(
                            "Watch evaluation inventory changed while comparing build contexts for {Project}; retrying once.",
                            project.ProjectPath);
                        continue;
                    }

                    throw new DotnetWatchIncompatibleGraphException(
                        $"The refreshed watch evaluation and build no longer agree ({buildComparison.Kind}): {buildComparison.Describe()}. Restart the watch run.");
                }
                _ = await evaluator.ResolvePayloadAsync(current, Builder, logger, cancellationToken).ConfigureAwait(false);
                structureChanged |= comparison.Kind == DotnetWatchGraphComparisonKind.RefreshRequired;
                replacements.Add(project, current);
            }

            lock (_inventoryGate)
            {
                var observations = new Dictionary<IResource, DotnetWatchProjectGraphResult>(_evaluatedRoots, ReferenceEqualityComparer.Instance);
                foreach (var (resource, graph) in ordinary)
                {
                    observations[resource] = graph;
                }
                foreach (var (project, current) in replacements)
                {
                    foreach (var (resource, existing) in observations)
                    {
                        if (Projects.Any(candidate => candidate.Server == server && ReferenceEquals(candidate.Resource, resource)))
                        {
                            continue;
                        }
                        if (DotnetWatchProjectGraph.SharesOutputs(current, existing))
                        {
                            var shared = DotnetWatchProjectGraph.SharedProjects(current, existing);
                            var detail = shared.Length == 0
                                ? "overlapping evaluated output or intermediate directories"
                                : $"shared physical project{(shared.Length == 1 ? "" : "s")} {string.Join(", ", shared.Select(path => $"'{path}'"))}";
                            throw new DotnetWatchIncompatibleGraphException(
                                $"Refreshed inputs for '{project.Resource.Name}' overlap '{resource.Name}' through {detail}. Restart the complete watch run.");
                        }
                    }
                }
                if (commit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var (resource, graph) in ordinary)
                    {
                        _evaluatedRoots[resource] = graph;
                    }
                    foreach (var (project, current) in replacements)
                    {
                        project.Graph = current;
                        _evaluatedRoots[project.Resource] = current;
                    }
                }
            }
            return structureChanged;
        }

        private static bool SameBuildContext(WatchedProject left, WatchedProject right) =>
            left.Graph is { } first && right.Graph is { } second &&
            PathsEqual(first.SdkDirectory, second.SdkDirectory) && PathsEqual(first.Muxer, second.Muxer) &&
            first.SdkVersion == second.SdkVersion &&
            left.BuildEnvironment.Count == right.BuildEnvironment.Count &&
            left.BuildEnvironment.All(pair => right.BuildEnvironment.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private static string? GetCompatibilityReason(
            WatchedProject project, DotnetWatchProjectGraphResult ordinary, DotnetWatchProjectGraphResult watcher,
            IReadOnlyDictionary<string, string> buildEnvironment)
        {
            var extension = Path.GetExtension(project.ProjectPath);
            if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return "The bundled service watcher supports C# projects and file apps; this language retains ordinary build and launch.";
            }
            var path = DotnetWatchProjectGraph.Normalize(project.ProjectPath);
            if (!watcher.Projects.TryGetValue(path, out var root) || !ordinary.Projects.ContainsKey(path))
            {
                return "SDK evaluation did not return the physical entry point.";
            }
            var properties = root.Properties;
            if (buildEnvironment.Keys.Any(name => name.StartsWith("DEBUG_SESSION_", StringComparison.OrdinalIgnoreCase) ||
                DotnetWatchOwnership.PrivateEnvironmentVariables.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                return "Build environment callbacks cannot configure reserved AppHost watch/ownership variables.";
            }
            if (!string.IsNullOrEmpty(properties.GetValueOrDefault("TargetFrameworks")) &&
                string.IsNullOrEmpty(properties.GetValueOrDefault("TargetFramework")))
            {
                return "The watcher cannot select one target framework of a multi-targeted runnable project. Set TargetFramework explicitly for the watch build.";
            }
            if (properties.GetValueOrDefault("TargetFrameworkIdentifier") != ".NETCoreApp" ||
                properties.GetValueOrDefault("OutputType") is not ("Exe" or "WinExe"))
            {
                return "The evaluated project is not a managed .NET executable supported by the watch agent.";
            }
            if (!Version.TryParse(properties.GetValueOrDefault("TargetFrameworkVersion")?.TrimStart('v'), out var targetVersion) ||
                targetVersion.Major < 6)
            {
                return "The selected target framework is older than the managed watch agent.";
            }
            if (properties.GetValueOrDefault("Optimize")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
                properties.GetValueOrDefault("StartupHookSupport")?.Equals("false", StringComparison.OrdinalIgnoreCase) == true ||
                properties.GetValueOrDefault("MetadataUpdaterSupport")?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "The evaluated build disables the watch agent; watcher environment adaptations cannot override those project properties.";
            }
            var comparison = DotnetWatchProjectGraph.Compare(
                ordinary,
                watcher,
                ((IProjectMetadata)project.Metadata).IsFileBasedApp ? path : null);
            if (comparison.Kind != DotnetWatchGraphComparisonKind.Equivalent)
            {
                return $"The ordinary and watcher build graphs differ ({comparison.Kind}): {comparison.Describe()}.";
            }
            return null;
        }

        private void MoveToServer(WatchedProject project, WatchServer server)
        {
            if (project.Server == server)
            {
                return;
            }
            var previous = project.Server;
            if (project.Annotations.Count == 0)
            {
                project.Server = server;
                return;
            }
            foreach (var annotation in project.Resource.Annotations
                .Where(annotation => annotation is WaitAnnotation wait && wait.Resource == previous.Resource ||
                    annotation is ResourceRelationshipAnnotation relationship && relationship.Resource == previous.Resource).ToArray())
            {
                project.Resource.Annotations.Remove(annotation);
                project.Annotations.Remove(annotation);
            }
            var existing = project.Resource.Annotations.ToHashSet();
            Builder.CreateResourceBuilder(project.Resource).WaitFor(server.Builder);
            project.Annotations.AddRange(project.Resource.Annotations.Where(annotation => !existing.Contains(annotation)));
            project.Server = server;
        }

        public void Configure(
            IResourceBuilder<DotnetProjectResource> resource,
            DotnetProjectMetadata metadata,
            DotnetProjectBuildCoordinator.CoordinatorState? buildCoordinator)
        {
            var projectPath = ((IProjectMetadata)metadata).ProjectPath;
            var scope = FindSdkScope(Path.GetDirectoryName(projectPath)!);
            var server = _servers.FirstOrDefault(s => s.DedicatedTo is null && string.Equals(s.SdkScope, scope, PathComparison) &&
                Projects.Where(project => project.Server == s).All(project =>
                    string.Equals(project.Metadata.BuildConfiguration ?? "Debug", metadata.BuildConfiguration ?? "Debug", StringComparison.OrdinalIgnoreCase)))
                ?? AddServer(scope, dedicatedTo: null, firstProjectName: resource.Resource.Name);
            var project = new WatchedProject(resource.Resource, metadata, buildCoordinator, server);
            Projects.Add(project);

            if (resource.Resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>().Any() ||
                ((IProjectMetadata)metadata).IsFileBasedApp)
            {
                UseDedicatedServer(resource);
            }
        }

        private void ConfigureLaunch(WatchedProject project)
        {
            var resource = Builder.CreateResourceBuilder(project.Resource);
            var projectPath = project.ProjectPath;
            var server = project.Server;
            var existing = resource.Resource.Annotations.ToHashSet();

            // Replace the SDK-selected launch tool (dotnet run) with the watch host's proxy, run by the same `dotnet`
            // command that would have run `dotnet run`. Program arguments contributed through WithArgs (launch-profile
            // and user args) stay in the ordinary segment after `--`.
            resource.WithLaunchToolArgs(ctx =>
            {
                var executable = ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
                ctx.Args.Add("exec");
                ctx.Args.Add(GetWatchHost());
                ctx.Args.Add(DotnetWatchHostProtocol.ProxyCommand);
                ctx.Args.Add(DotnetWatchHostProtocol.ServerOption);
                ctx.Args.Add(project.Server.ResourcePipeName);
                ctx.Args.Add(DotnetWatchHostProtocol.EntryPointOption);
                ctx.Args.Add(project.Server.GetEntryPoint(projectPath));
                ctx.Args.Add(DotnetWatchHostProtocol.LaunchGateOption);
                ctx.Args.Add(project.Server.GetLaunchMarkerPath(projectPath));
                if (executable.WorkingDirectoryExplicitlySet)
                {
                    // The watcher launches `dotnet run` from the project directory; RunWorkingDirectory moves the app.
                    ctx.Args.Add(DotnetWatchHostProtocol.WorkingDirectoryOption);
                    ctx.Args.Add(executable.WorkingDirectory);
                }

                ctx.Args.Add("--");
            },
            // Owning the proxy with the project's launch configuration type lets HasCustomLaunch tell, through the
            // public HasLaunchToolArgsOwnedBy, whether a later launch tool replaced it. It also matches the
            // SDK-selected tool: an IDE project launch performs the invocation itself (watch never runs under an IDE).
            ownedByLaunchConfigurationType: GetProjectLaunchConfigurationType(resource.Resource),
            showInCommandLine: true);

            // The watcher's own initial build is the build barrier in watch runs (the project leaves Aspire's coordinated
            // build once it's known to be watched).
            resource.WaitFor(server.Builder);

            // Consumers of the build output that don't wait for the service, such as EF startup operations that run
            // `dotnet-ef --no-build` (and that the service itself may wait on), wait for the watcher's successful build.
            // Last one wins, so this replaces the coordinated build's barrier.
            resource.WithAnnotation(new DotnetProgramBuildCompletionAnnotation(
                (_, cancellationToken) => project.Server.WaitForSuccessfulBuildAsync(cancellationToken)));

            project.Annotations.AddRange(resource.Resource.Annotations.Where(a => !existing.Contains(a)));
        }

        public void UseDedicatedServer(IResourceBuilder<DotnetProjectResource> resource)
        {
            if (Projects.Find(p => p.Resource == resource.Resource) is not { } project || project.Server.DedicatedTo == project.Resource)
            {
                return;
            }

            var previous = project.Server;
            var server = AddServer(previous.SdkScope, dedicatedTo: project.Resource, firstProjectName: project.Resource.Name);

            // Before final eligibility, reserving a dedicated server must not change the ordinary launch or waits.
            if (project.Annotations.Count == 0)
            {
                project.Server = server;
                return;
            }

            // Move the build barrier (WaitFor and its relationship) from the shared server to the dedicated one.
            foreach (var annotation in resource.Resource.Annotations
                .Where(a => a is WaitAnnotation w && w.Resource == previous.Resource || a is ResourceRelationshipAnnotation r && r.Resource == previous.Resource)
                .ToList())
            {
                resource.Resource.Annotations.Remove(annotation);
                project.Annotations.Remove(annotation);
            }

            var existing = resource.Resource.Annotations.ToHashSet();
            resource.WaitFor(server.Builder);
            project.Annotations.AddRange(resource.Resource.Annotations.Where(a => !existing.Contains(a)));
            project.Server = server;
        }

        private WatchServer AddServer(string? scope, DotnetProjectResource? dedicatedTo, string firstProjectName)
        {
            var name = _servers.Count == 0 ? ServerResourceName : $"{ServerResourceName}-{firstProjectName}";
            var unique = name;
            for (var i = 2; Builder.Resources.Any(r => string.Equals(r.Name, unique, StringComparison.OrdinalIgnoreCase)); i++)
            {
                unique = $"{name}-{i}";
            }

            var server = new WatchServer(this, unique, _servers.Count, scope, dedicatedTo);
            _servers.Add(server);
            return server;
        }

        private void CleanupRunDirectory()
        {
            try
            {
                if (Directory.Exists(RunDirectory))
                {
                    Directory.Delete(RunDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Could not remove the owned watch run directory {Directory}.", RunDirectory);
            }
        }

        private readonly object _disposeGate = new();
        private Task? _disposeTask;

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync() => new(GetShutdownTask(stopOwnedResources: false, CancellationToken.None));

        Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) =>
            GetShutdownTask(stopOwnedResources: true, cancellationToken);

        private Task GetShutdownTask(bool stopOwnedResources, CancellationToken cancellationToken)
        {
            lock (_disposeGate)
            {
                return _disposeTask ??= DisposeCoreAsync(stopOwnedResources, cancellationToken);
            }
        }

        private async Task DisposeCoreAsync(bool stopOwnedResources, CancellationToken cancellationToken)
        {
            await Task.WhenAll(_servers.Select(server => server.ShutdownAsync(stopOwnedResources, cancellationToken))).ConfigureAwait(false);
            if (_servers.All(server => server.CleanupAcknowledged))
            {
                if (_runDirectory is not null)
                {
                    CleanupRunDirectory();
                }
                if (_shortTempRoot is not null && Directory.Exists(_shortTempRoot))
                {
                    Directory.Delete(_shortTempRoot, recursive: true);
                }
            }
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed class WatchServer
    {
        // How long the Restart command waits for the watcher to relaunch the service.
        private static readonly TimeSpan s_restartTimeout = TimeSpan.FromMinutes(1);

        private readonly WatchState _state;
        private readonly int _index;
        private readonly object _gate = new();
        private Task<PreparedServer>? _prepareTask;
        private readonly HashSet<Task<PreparedServer>> _preparations = [];
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;
        private Task? _resourceMonitorTask;
        private ILogger? _lifecycleLogger;
        private Task? _maintenanceTask;
        private CancellationToken _stopping;
        private volatile bool _fenced;
        private volatile string? _failure;
        private int _failureScheduled;
        private readonly object _maintenanceGate = new();
        private readonly HashSet<string> _pendingChanges = new(StringComparer.Ordinal);
        private PreparedServer? _prepared;
        private Dictionary<DotnetWatchMember, string[]>? _pendingResume;
        private bool _resumeRequested;
        private Task? _failureTask;
        private CancellationTokenSource? _shutdown;
        private Task? _shutdownTask;
        private int _launchAttempted;
        private int _cleanupAcknowledged = 1;
        private volatile bool _shuttingDown;
        private ResourceNotificationService? _notifications;
        private ILogger? _noticeLogger;
        private ILogger? _serverLogger;
        private DotnetWatchProjectFileWatcher? _projectFileWatcher;
        // Launch-tool arguments can be evaluated before the server's environment/arguments prepare it.
        // Start at the first real generation so every proxy and the server agree before either callback runs.
        private int _generation = 1;
        private volatile bool _ready;
        private volatile string? _lastBuildError;

        // Completed by the watcher's first successful build of the current server run; re-armed when the server restarts.
        private TaskCompletionSource _successfulBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _noticeGate = new();
        private DotnetWatchEventInterpreter? _interpreter;

        // The control pipe of the current watcher run; replaced with every prepare.
        private DotnetWatchControlPipeServer? _control;

        // Restart commands waiting for the watcher to relaunch their project (process_started).
        private readonly List<(string ProjectPath, TaskCompletionSource Started)> _pendingRestarts = [];

        public WatchServer(WatchState state, string name, int index, string? sdkScope, DotnetProjectResource? dedicatedTo)
        {
            _state = state;
            _index = index;
            SdkScope = sdkScope;
            DedicatedTo = dedicatedTo;
            var builder = state.Builder;
            var healthCheckKey = $"{name}_initial_build";
            builder.Services.AddHealthChecks().AddCheck(healthCheckKey, () =>
                _ready ? HealthCheckResult.Healthy("Watcher finished its initial build.")
                : _lastBuildError is { } error ? HealthCheckResult.Unhealthy($"Initial build failed: {error}. Fix the code and save; the watcher rebuilds automatically.")
                : HealthCheckResult.Unhealthy("Waiting for the watcher's initial build."));

            Builder = builder.AddResource(new DotnetWatchServerResource(name, builder.AppHostDirectory))
                .WithIconName("ArrowSync")
                .AsWatchSystemResource()
                .WithHealthCheck(healthCheckKey)
                .WithArgs(async ctx =>
                {
                    var prepared = await PrepareAsync(ctx.ExecutionContext.Services, ctx.CancellationToken).ConfigureAwait(false);
                    ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command = prepared.Muxer;
                    foreach (var arg in prepared.Args)
                    {
                        ctx.Args.Add(arg);
                    }
                })
                .WithEnvironment(async ctx =>
                {
                    var prepared = await PrepareAsync(ctx.ExecutionContext.Services, ctx.CancellationToken).ConfigureAwait(false);
                    foreach (var (name, value) in prepared.Environment)
                    {
                        ctx.EnvironmentVariables[name] = value;
                    }
                });
            builder.Eventing.Subscribe<BeforeResourceStartedEvent>(Builder.Resource, (_, _) =>
            {
                Volatile.Write(ref _launchAttempted, 1);
                return Task.CompletedTask;
            });

            // Each (re)start of the server gets a fresh prepare (new status pipe + listener, re-resolved SDK).
            builder.Eventing.Subscribe<ResourceStoppedEvent>(Builder.Resource, (_, _) =>
            {
                Invalidate();
                if (Interlocked.Exchange(ref _stoppedForRestart, null) is { } stopped)
                {
                    stopped.TrySetResult();
                }
                else if (!_stopping.IsCancellationRequested)
                {
                    RequestFailure("The watcher owner stopped unexpectedly. Restart the complete watch run.");
                }
                return Task.CompletedTask;
            });
        }

        public IResourceBuilder<DotnetWatchServerResource> Builder { get; }
        public DotnetWatchServerResource Resource => Builder.Resource;
        public string ResourcePipeName => OperatingSystem.IsWindows()
            ? $"aspire-watch-{_state.Id}-{_index}"
            : Path.Combine(_state.RunDirectory, $"res{_index}.sock");
        public string? SdkScope { get; }
        public DotnetProjectResource? DedicatedTo { get; }
        public string? TempDirectory { get; set; }

        private List<WatchedProject> Projects => _state.Projects.Where(p => p.Server == this).ToList();

        private string[] Roots => Projects
            .GroupBy(project => DotnetWatchProjectGraph.Normalize(project.ProjectPath), DotnetWatchProjectGraph.PathComparer)
            .Select(group => group.First().ProjectPath).ToArray();

        public string GetEntryPoint(string path) => Projects.First(project => PathsEqual(project.ProjectPath, path)).ProjectPath;

        /// <summary>
        /// The file that tells a project's proxies (<c>--launch-gate</c>) that the project launched once in the current
        /// server generation. Until it exists, the proxies launch the project one at a time: the watcher hands a
        /// concurrent launch of the same web project its browser refresh server before the first launch has started
        /// it, and that launch fails ("Server not started", dotnet/sdk BrowserRefreshServerFactory).
        /// </summary>
        public string GetLaunchMarkerPath(string projectPath) => GetLaunchMarkerPath(projectPath, Volatile.Read(ref _generation));

        private string GetLaunchMarkerPath(string projectPath, int generation)
        {
            var identity = DotnetWatchProjectGraph.Normalize(projectPath);
            if (!OperatingSystem.IsLinux())
            {
                identity = identity.ToUpperInvariant();
            }
            return Path.Combine(_state.RunDirectory,
                $"launched{_index}-{generation}-{XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(identity)):X16}");
        }

        public void Start(IServiceProvider services, CancellationToken stopping)
        {
            _services = services;
            _notifications = services.GetRequiredService<ResourceNotificationService>();
            _noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
            _lifecycleLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DotnetWatchCoordinator));
            _serverLogger = services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource);
            var notifications = _notifications;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _stopping = _shutdown.Token;
            DotnetWatchProjectFileWatcher? projectFileWatcher = null;
            projectFileWatcher = new DotnetWatchProjectFileWatcher(BuildInputs, changed =>
            {
                if (stopping.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    OnProjectFilesChanged(services, changed);
                }
                catch (ObjectDisposedException)
                {
                    // The AppHost was disposed without stopping (ApplicationStopping never fired).
                    projectFileWatcher?.Dispose();
                }
            }, s_projectFileDebounce, exception =>
                _serverLogger.LogWarning(exception, "Build-input polling failed; the next poll will retry."));
            projectFileWatcher.Start();
            _projectFileWatcher = projectFileWatcher;
            _stopping.Register(() =>
            {
                _fenced = true;
                _projectFileWatcher?.Dispose();
                _monitorCts?.Cancel();
                _successfulBuild.TrySetCanceled(_stopping);
                SettleRestartWaiters(new OperationCanceledException(_stopping));
            });
            _resourceMonitorTask = Task.Run(async () =>
            {
                try
                {
                    _stopping.ThrowIfCancellationRequested();
                    await foreach (var update in notifications.WatchAsync(_stopping).ConfigureAwait(false))
                    {
                        if (Projects.FirstOrDefault(project => ReferenceEquals(project.Resource, update.Resource))?.Member is not { } member)
                        {
                            continue;
                        }
                        member.ObserveTransport(update.ResourceId, update.Snapshot);
                        var state = update.Snapshot.Properties.FirstOrDefault(property =>
                            property.Name == DotnetWatchMember.TransportStateProperty)?.Value as string;
                        if (!_fenced && !member.IsManuallyStopped(update.ResourceId) &&
                            !member.IsCoordinatorStopping(update.ResourceId) &&
                            !member.IsCoordinatorStarting(update.ResourceId) &&
                            state is not null && KnownResourceStates.TerminalStates.Contains(state))
                        {
                            RequestFailure($"The resource proxy '{update.ResourceId}' stopped unexpectedly. Restart the watch run.");
                        }
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
            }, CancellationToken.None);

        }

        private IServiceProvider? _services;

        private string[] BuildInputs => _state.GuardedBuildInputs(this);
        private string[] OwnedBuildInputs =>
            [.. Projects.SelectMany(project => project.Graph!.Projects.Values)
                .SelectMany(node => node.Imports.Append(node.Path)).Distinct(DotnetWatchProjectGraph.PathComparer)];

        public void ConfigureLifecycle(WatchedProject project)
        {
            var member = new DotnetWatchMember(project.Resource, () => _ready, () => _fenced, () => _failure,
                () => Projects.Where(candidate => PathsEqual(candidate.ProjectPath, project.ProjectPath))
                    .Sum(candidate => candidate.Resource.GetReplicaCount()) == 1,
                context => RestartAsync(project, context),
                (original, context) => StopAsync(project, original, context));
            project.Member = member;
            member.Configure();
            _state.Builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
                project.Resource,
                (_, cancellationToken) => WaitForSuccessfulBuildAsync(cancellationToken));
        }

        public Task<PreparedServer> PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_shuttingDown, this);
                if (_prepareTask is null)
                {
                    _monitorCts?.Cancel();
                    _monitorCts = new CancellationTokenSource();
                    _ready = false;
                    _lastBuildError = null;
                    lock (_noticeGate)
                    {
                        _interpreter = new DotnetWatchEventInterpreter(
                            Projects.Select(p => (p.ProjectPath, p.Resource.Name)),
                            Resource.Name);
                    }

                    if (_successfulBuild.Task.IsCompleted)
                    {
                        _successfulBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    _prepareTask = PrepareWithNoticeAsync(services, _generation, _successfulBuild, _monitorCts.Token, cancellationToken);
                    _preparations.RemoveWhere(task => task.IsCompleted);
                    _preparations.Add(_prepareTask);
                }

                return _prepareTask;
            }
        }

        /// <summary>
        /// Completes once the watcher has built its projects successfully, independently of whether any service has
        /// started. A failed build keeps waiting for a fix, as the services do. After the server restarts, this waits
        /// for the restarted watcher's build.
        /// </summary>
        public Task WaitForSuccessfulBuildAsync(CancellationToken cancellationToken)
        {
            Task successfulBuild;
            lock (_gate)
            {
                successfulBuild = _successfulBuild.Task;
            }

            return successfulBuild.WaitAsync(cancellationToken);
        }

        internal Task OnBuildCompleteForTestingAsync(bool success, IServiceProvider services, CancellationToken cancellationToken)
        {
            TaskCompletionSource successfulBuild;
            lock (_gate)
            {
                successfulBuild = _successfulBuild;
            }

            return OnBuildCompleteAsync(success, error: null, successfulBuild, services, cancellationToken);
        }

        internal async Task OnBuildCompleteWhileRestartLockedForTestingAsync(
            IServiceProvider services, CancellationToken cancellationToken)
        {
            await _restartLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await OnBuildCompleteForTestingAsync(
                    success: true, services, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _restartLock.Release();
            }
        }

        /// <summary>Records a <c>build_complete</c> status from the watcher run that <paramref name="successfulBuild"/> belongs to.</summary>
        private async Task OnBuildCompleteAsync(
            bool success, string? error, TaskCompletionSource successfulBuild,
            IServiceProvider services, CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(successfulBuild, _successfulBuild) || _failure is not null)
            {
                return;
            }
            if (success)
            {
                if (!ReferenceEquals(successfulBuild, _successfulBuild) || _failure is not null)
                {
                    return;
                }
                lock (_maintenanceGate)
                {
                    if (_pendingChanges.Count != 0)
                    {
                        return;
                    }
                }
                try
                {
                    if (!successfulBuild.Task.IsCompleted)
                    {
                        // The watcher's initial restore can create active NuGet imports that did not
                        // exist at preflight. Capture their evaluated inventory before releasing build
                        // consumers, without introducing another build or restarting an unrelated group.
                        await _state.RevalidateAsync(this, services, commit: true,
                            evaluateCallbacks: false, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        RearmObservation(services);
                    }
                    lock (_maintenanceGate)
                    {
                        if (_pendingChanges.Count != 0 || _failure is not null)
                        {
                            return;
                        }
                    }
                    _lastBuildError = null;
                    _ready = true;
                    successfulBuild.TrySetResult();
                    if (_pendingResume is not null)
                    {
                        ScheduleMaintenance(resume: true);
                    }
                }
                catch (DistributedApplicationException exception)
                {
                    RequestFailure(WithRestartInstruction(
                        $"Initial watch build inventory validation failed: {exception.Message}",
                        "Restart the complete watch run."));
                    throw;
                }
            }
            else
            {
                _lastBuildError = error ?? "the build failed";
            }
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                if (_shuttingDown)
                {
                    return;
                }
                _prepareTask = null;
                _monitorCts?.Cancel();
                _ready = false;
                ++_generation;
            }
        }

        private async Task<PreparedServer> PrepareWithNoticeAsync(IServiceProvider services, int generation, TaskCompletionSource successfulBuild, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(monitorToken, cancellationToken, _stopping);
            try
            {
                return await PrepareCoreAsync(services, generation, successfulBuild, monitorToken, startup.Token).ConfigureAwait(false);
            }
            catch (DistributedApplicationException ex)
            {
                RequestFailure($"Hot reload could not start after build handoff: {ex.Message} Restart the watch run.");
                throw;
            }
        }

        private async Task<PreparedServer> PrepareCoreAsync(IServiceProvider services, int generation, TaskCompletionSource successfulBuild, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource);

            // Projects that aren't watched are built by Aspire's coordinated build. Let it finish first so the watcher's
            // build doesn't race it over shared project outputs.
            var excluded = _state.GetExcludedBuildBarriers(this);
            if (excluded.Count > 0)
            {
                logger.LogInformation("Waiting for the coordinated build of the projects that aren't watched: {Projects}", string.Join(", ", excluded.Select(p => p.Resource.Name)));
                foreach (var project in excluded)
                {
                    await project.BuildCoordinator!.WaitForBuildCompletionAsync(project.Resource, services, cancellationToken).ConfigureAwait(false);
                }
            }

            DotnetWatchServerInfrastructure.EnsurePrivateDirectory(_state.RunDirectory);
            await _state.RevalidateAsync(this, services, commit: true, evaluateCallbacks: false, cancellationToken).ConfigureAwait(false);

            var roots = Roots;
            var first = Projects[0];
            var sdk = first.Graph ?? throw new DistributedApplicationException("The watcher has no validated preflight SDK/graph.");
            var payload = first.Payload ?? throw new DistributedApplicationException("The watcher has no validated preflight payload.");
            var toolDll = payload.ToolPath;

            // The watch host hosts the watcher (and proxies each replica). It targets net10.0 and rolls forward to newer
            // majors, so the muxer that runs the watcher runs it too.
            var watchHost = payload.WatchHostPath;
            logger.LogInformation("Watching {Count} root(s) with SDK {SdkDirectory}: {Roots}", roots.Length, sdk.SdkDirectory, string.Join(", ", roots));

            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var (name, value) in first.BuildEnvironment)
            {
                environment[name] = value;
            }

            if (TempDirectory is { } tempDirectory)
            {
                // TODO(upstream dotnet/sdk#51311): with more than one root the watcher builds a temporary .slnx from
                // Path.GetTempFileName() with the temp directory as working directory, so the muxer selects the SDK
                // from the temp directory's global.json scope instead of --sdk ("Build FAILED, 0 errors" when they
                // differ). Only when they do differ, point the watcher's temp directory at a private directory with a
                // global.json pinning the resolved SDK. Services get their own temp directory back (restore list).
                foreach (var name in GetTempVariables(OperatingSystem.IsWindows()))
                {
                    environment[name] = tempDirectory;
                }
                logger.LogInformation("The watcher's multi-project build uses the evaluated SDK-isolated directory {TempDirectory}.", tempDirectory);
            }

            var statusPipeName = OperatingSystem.IsWindows()
                ? $"aspire-watch-status-{_state.Id}-{_index}-{generation}"
                : Path.Combine(_state.RunDirectory, $"status{_index}-{generation}.sock");

            var controlPipeName = OperatingSystem.IsWindows()
                ? $"aspire-watch-control-{_state.Id}-{_index}-{generation}"
                : Path.Combine(_state.RunDirectory, $"control{_index}-{generation}.sock");

            var args = new List<string>
            {
                "exec", watchHost, DotnetWatchHostProtocol.ServerCommand, "--", sdk.Muxer, "exec", toolDll, "server",
                "--sdk", sdk.SdkDirectory, "--server", ResourcePipeName, "--status-pipe", statusPipeName, "--control-pipe", controlPipeName
            };
            foreach (var root in roots)
            {
                args.Add("--resource");
                args.Add(root);
            }

            // The pipe listeners must exist before the watcher connects to them.
            var control = new DotnetWatchControlPipeServer(controlPipeName,
                exception => RequestFailure($"Watch control transport failed: {exception.Message} Restart the watch run."));
            control.Start();
            if (Interlocked.Exchange(ref _control, control) is { } previous)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            // Resolve dependencies before yielding, rather than scheduling service resolution
            // that can race disposal of an AppHost which was built but never started.
            _monitorTask = MonitorStatusAsync(services, logger, statusPipeName, successfulBuild, generation, monitorToken);

            // Everything set only for the watcher must not leak into the services, which inherit its environment.
            foreach (var name in DotnetWatchOwnership.PrivateEnvironmentVariables)
            {
                environment[name] = "";
            }
            if (DotnetWatchOwnership.IsCliOwned(_state.Builder.Configuration[DotnetWatchOwnership.OwnerEnvironmentVariable]))
            {
                // Only CLI-owned watcher children discard the execution-service settings
                // injected by upstream host. The AppHost wrapper and real IDE path retain them.
                foreach (var name in new[] { "DEBUG_SESSION_PORT", "DEBUG_SESSION_TOKEN", "DEBUG_SESSION_INFO",
                    "DEBUG_SESSION_SERVER_URI", "DEBUG_SESSION_SERVER_CERTIFICATE" })
                {
                    environment[name] = "";
                }
            }
            var cleanupMarker = Path.Combine(_state.RunDirectory, $"cleanup{_index}-{generation}");
            environment[DotnetWatchHostProtocol.CleanupMarkerVariable] = cleanupMarker;
            var restored = environment.Keys.Where(name => !name.StartsWith("DEBUG_SESSION_", StringComparison.Ordinal) &&
                !DotnetWatchOwnership.PrivateEnvironmentVariables.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                name != DotnetWatchHostProtocol.CleanupMarkerVariable).ToArray();
            var prepared = new PreparedServer(sdk.Muxer, watchHost, args, environment, restored, cleanupMarker);
            _prepared = prepared;
            Volatile.Write(ref _cleanupAcknowledged, 0);
            return prepared;
        }

        public static async Task<IReadOnlyDictionary<string, string>> EvaluateBuildEnvironmentAsync(
            DotnetProjectResource resource, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var environment = new Dictionary<string, object>(comparer);
            var executionContext = services.GetRequiredService<DistributedApplicationExecutionContext>();
            foreach (var callback in resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>())
            {
                await callback.Callback(new EnvironmentCallbackContext(executionContext, resource, environment, cancellationToken) { Logger = logger }).ConfigureAwait(false);
            }

            var resolved = new Dictionary<string, string>(comparer);
            foreach (var (name, value) in environment)
            {
                resolved[name] = value as string ?? throw new DistributedApplicationException(
                    $"The build environment variable '{name}' for .NET project resource '{resource.Name}' " +
                    $"has unsupported value type '{value?.GetType().Name ?? "null"}'. Build environment values must be strings.");
            }

            return resolved;
        }

        private async Task MonitorStatusAsync(IServiceProvider services, ILogger serverLogger, string statusPipeName, TaskCompletionSource successfulBuild, int generation, CancellationToken monitorToken)
        {
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var diagnostics = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DotnetWatchCoordinator).FullName!);
            var lifetime = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping, monitorToken);

            try
            {
                var pipe = new NamedPipeServerStream(
                    statusPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                DotnetWatchServerInfrastructure.RestrictSocketToCurrentUser(statusPipeName);
                await using var pipeScope = pipe.ConfigureAwait(false);
                await pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                await foreach (var status in DotnetWatchStatusReader.ReadAsync(pipe, cts.Token).ConfigureAwait(false))
                {
                    if (cts.IsCancellationRequested || generation != Volatile.Read(ref _generation))
                    {
                        continue;
                    }

                    diagnostics.LogDebug("dotnet-watch status: {Type}", status.Type);
                    if (!string.IsNullOrWhiteSpace(status.Error))
                    {
                        serverLogger.LogError("Watcher {Status} diagnostic: {Error}", status.Type, status.Error);
                    }
                    var projects = ToEntryPointPaths(status.Projects ?? []);
                    if (status.Type is DotnetWatchStatusTypes.ProcessStarted or DotnetWatchStatusTypes.ProcessExited &&
                        projects.Any(path => !Projects.Any(project => PathsEqual(project.ProjectPath, path))))
                    {
                        throw new InvalidDataException("The watcher reported a process for an unknown entry point.");
                    }

                    if (status.Type == DotnetWatchStatusTypes.BuildComplete)
                    {
                        if (status.Success != true && _ready && RebuildFileBasedAppIfStuck(services, status.Projects ?? []))
                        {
                            continue;
                        }

                        await OnBuildCompleteAsync(status.Success == true, DotnetWatchEventInterpreter.FailureDetail(Resource.Name),
                            successfulBuild, services, cts.Token).ConfigureAwait(false);
                    }

                    IReadOnlyList<WatchNotice> notices;
                    lock (_noticeGate)
                    {
                        notices = _interpreter?.OnStatus(status.Type, projects, status.Success, status.ExitCode, status.Error) ?? [];
                    }

                    EmitNotices(services, notices);

                    foreach (var path in projects.Distinct(DotnetWatchProjectGraph.PathComparer))
                    {
                        if (status.Type == DotnetWatchStatusTypes.ProcessStarted)
                        {
                            File.WriteAllBytes(GetLaunchMarkerPath(path, generation), []);
                            CompletePendingRestarts(path);
                        }

                        foreach (var target in Projects.Where(p => PathsEqual(p.ProjectPath, path)))
                        {
                            await PublishWatchEventAsync(notifications, target, status).ConfigureAwait(false);
                        }
                    }
                    if (projects.Length == 0 && status.Type is DotnetWatchStatusTypes.Building or DotnetWatchStatusTypes.BuildComplete)
                    {
                        foreach (var project in Projects)
                        {
                            await PublishWatchEventAsync(notifications, project, status).ConfigureAwait(false);
                        }
                    }
                }

                if (!cts.IsCancellationRequested)
                {
                    RequestFailure("The watcher closed its status pipe unexpectedly. Application outcomes are unknown; restart the watch run.");
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                serverLogger.LogError(ex, "Watch status monitor failed.");
                RequestFailure($"Watch status transport failed: {ex.Message} Restart the watch run.");
            }
        }

        private async Task<ExecuteCommandResult> RestartAsync(WatchedProject project, ExecuteCommandContext context)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _stopping);
            await _restartLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
            TaskCompletionSource? started = null;
            try
            {
                if (_fenced || _failure is not null || !_ready || Volatile.Read(ref _control) is not { } control)
                {
                    return CommandResults.Failure(_failure ?? "The watcher is not ready or is changing. Restart the watch run.");
                }
                _fenced = true;
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_pendingRestarts)
                {
                    _pendingRestarts.Add((project.ProjectPath, started));
                }
                await project.Member!.PublishAsync(context.Services.GetRequiredService<ResourceNotificationService>()).ConfigureAwait(false);
                if (!await control.SendRebuildAsync([project.ProjectPath], cancellation.Token).ConfigureAwait(false))
                {
                    context.Logger.LogError("The hot reload control transport for '{Server}' is not accepting the restart.", Resource.Name);
                    return CommandResults.Failure($"The hot reload watcher '{Resource.Name}' isn't accepting commands.");
                }

                await started.Task.WaitAsync(s_restartTimeout, cancellation.Token).ConfigureAwait(false);
                return new ExecuteCommandResult { Success = true, Message = $"Restarted '{project.Resource.Name}'." };
            }
            catch (TimeoutException)
            {
                return new ExecuteCommandResult { Success = false, Message = $"The watcher didn't relaunch '{project.Resource.Name}' within {s_restartTimeout.TotalSeconds:0} seconds; see the 'dotnet-watch' resource logs." };
            }
            catch (Exception exception) when (exception is IOException or DistributedApplicationException)
            {
                context.Logger.LogError(exception, "The watched restart did not complete.");
                return CommandResults.Failure(exception.Message);
            }
            finally
            {
                if (started is not null)
                {
                    lock (_pendingRestarts)
                    {
                        _pendingRestarts.RemoveAll(r => r.Started == started);
                    }
                }
                lock (_maintenanceGate)
                {
                    _fenced = _failure is not null || _pendingChanges.Count != 0 || _stopping.IsCancellationRequested;
                }
                _restartLock.Release();
                if (!_stopping.IsCancellationRequested)
                {
                    await project.Member!.PublishAsync(context.Services.GetRequiredService<ResourceNotificationService>()).ConfigureAwait(false);
                }
            }
        }

        private async Task<ExecuteCommandResult> StopAsync(
            WatchedProject project, ResourceCommandAnnotation original, ExecuteCommandContext context)
        {
            lock (_pendingRestarts)
            {
                foreach (var (_, pending) in _pendingRestarts.Where(pending => PathsEqual(pending.ProjectPath, project.ProjectPath)))
                {
                    pending.TrySetException(new DistributedApplicationException("The watched instance was manually stopped during restart."));
                }
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _stopping);
            await _restartLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                return await original.ExecuteCommand(context).ConfigureAwait(false);
            }
            finally
            {
                _restartLock.Release();
            }
        }

        private void CompletePendingRestarts(string projectPath)
        {
            lock (_pendingRestarts)
            {
                foreach (var (path, started) in _pendingRestarts)
                {
                    if (PathsEqual(path, projectPath))
                    {
                        started.TrySetResult();
                    }
                }
            }
        }

        private void OnProjectFilesChanged(IServiceProvider services, IReadOnlyList<string> changed)
        {
            if (_stopping.IsCancellationRequested || _failure is not null)
            {
                return;
            }

            var roots = Roots;
            EmitNotices(services, [new WatchNotice(WatchNoticeKind.ProjectFileChanged, LogLevel.Information,
                $"📦 {string.Join(", ", changed.Select(Path.GetFileName))} changed.", roots)]);
            lock (_maintenanceGate)
            {
                _pendingChanges.UnionWith(changed);
            }
            ScheduleMaintenance(resume: false);
        }

        internal async Task OnProjectFileChangeForTestingAsync(
            string path, IServiceProvider services, CancellationToken cancellationToken)
        {
            OnProjectFilesChanged(services, [path]);
            Task? maintenance;
            lock (_maintenanceGate)
            {
                maintenance = _maintenanceTask;
            }
            if (maintenance is not null)
            {
                await maintenance.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private TaskCompletionSource? _stoppedForRestart;
        private bool _forceRefresh;

        /// <summary>
        /// Stops this server's services, restarts the watcher and starts the services again. The watcher re-evaluates
        /// a changed project file but doesn't rebuild it, so a new package never reaches the service, and building
        /// outside the watcher isn't enough: it keeps its compilation baselines and replays its hot reload delta
        /// history to every process it launches (after removing a package an earlier edit used, the replayed deltas
        /// applied to a matching assembly and the service aborted with MethodAccessException). A fresh watcher restores
        /// and builds the projects as its initial build; the services wait for it as they do at startup.
        /// </summary>
        private void ScheduleMaintenance(bool resume)
        {
            lock (_maintenanceGate)
            {
                if (_stopping.IsCancellationRequested || _failure is not null)
                {
                    return;
                }
                _fenced = true;
                _resumeRequested |= resume;
                if (_maintenanceTask is null)
                {
                    _maintenanceTask = Task.Run(MaintenanceLoopAsync, CancellationToken.None);
                }
            }
        }

        private async Task MaintenanceLoopAsync()
        {
            var services = _services!;
            try
            {
                while (!_stopping.IsCancellationRequested && _failure is null)
                {
                    string[] changes;
                    bool force;
                    bool resume;
                    lock (_maintenanceGate)
                    {
                        changes = [.. _pendingChanges];
                        _pendingChanges.Clear();
                        force = _forceRefresh;
                        _forceRefresh = false;
                        resume = _resumeRequested;
                        _resumeRequested = false;
                        if (changes.Length == 0 && !force && !resume)
                        {
                            _maintenanceTask = null;
                            _fenced = _pendingResume is not null && !_ready;
                            return;
                        }
                    }
                    await _restartLock.WaitAsync(_stopping).ConfigureAwait(false);
                    try
                    {
                        if (changes.Length != 0 || force)
                        {
                            var owned = OwnedBuildInputs;
                            var ownedBuildChange = changes.Any(path => owned.Contains(path, DotnetWatchProjectGraph.PathComparer) &&
                                !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                            var structureChanged = await _state.RevalidateAsync(this, services, commit: false, evaluateCallbacks: true, _stopping).ConfigureAwait(false);
                            if (force || ownedBuildChange || structureChanged)
                            {
                                await RefreshAsync(services, _stopping).ConfigureAwait(false);
                            }
                            else
                            {
                                await _state.RevalidateAsync(this, services, commit: true, evaluateCallbacks: false, _stopping).ConfigureAwait(false);
                                RearmObservation(services);
                            }
                        }
                        else if (resume)
                        {
                            await ResumeAsync(services, _stopping).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _restartLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (DotnetWatchIncompatibleGraphException exception)
            {
                services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource)
                    .LogError(exception, "Guarded watch refresh failed.");
                RequestFailure(WithRestartInstruction(
                    $"Guarded watch refresh failed: {exception.Message}",
                    "Restart the complete watch run."));
            }
            catch (DistributedApplicationException exception)
            {
                services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource)
                    .LogWarning(exception, "Build-input evaluation failed; retaining the last successful watch graph until another edit.");
                var loggers = services.GetRequiredService<ResourceLoggerService>();
                foreach (var project in Projects)
                {
                    loggers.GetLogger(project.Resource).LogWarning(
                        "[watch] Build-input evaluation failed: {Message} Running services and the last successful graph were preserved; save another edit to retry.",
                        exception.Message);
                }
                lock (_maintenanceGate)
                {
                    _maintenanceTask = null;
                    _fenced = _pendingResume is not null && !_ready;
                }
            }
            catch (Exception exception)
            {
                services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource)
                    .LogError(exception, "Guarded watch refresh failed.");
                RequestFailure(WithRestartInstruction(
                    $"Guarded watch refresh failed: {exception.Message}",
                    "Restart the complete watch run."));
            }
            finally
            {
                lock (_maintenanceGate)
                {
                    if (_failure is not null || _stopping.IsCancellationRequested)
                    {
                        _maintenanceTask = null;
                    }
                }
            }
        }

        private async Task RefreshAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var members = Projects.Select(project => project.Member!).ToArray();
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            _pendingResume ??= members.ToDictionary(member => member, member => member.GetTransportInstances(notifications, intendedOnly: true));
            EmitNotices(services, [new WatchNotice(WatchNoticeKind.Restarting, LogLevel.Information,
                $"Restarting {string.Join(", ", Projects.Select(project => project.Resource.Name))} after guarded build-input validation.",
                Roots)]);
            foreach (var member in members)
            {
                foreach (var id in member.GetTransportInstances(notifications, intendedOnly: false))
                {
                    RequireSuccess(await member.StopOwnedAsync(id, services, cancellationToken).ConfigureAwait(false));
                }
            }
            await StopServerAndConfirmCleanupAsync(services, cancellationToken).ConfigureAwait(false);
            await _state.RevalidateAsync(this, services, commit: true, evaluateCallbacks: false, cancellationToken).ConfigureAwait(false);
            foreach (var member in members)
            {
                member.BeginRefresh();
            }
            RearmObservation(services);
            RequireSuccess(await DotnetWatchServerInfrastructure.StartOwnedAsync(Resource, services, cancellationToken).ConfigureAwait(false));
            // A failed source build can wait indefinitely for a fix without blocking this
            // operation queue. The next current-generation successful build schedules resume.
            if (_ready)
            {
                ScheduleMaintenance(resume: true);
            }
        }

        private void RearmObservation(IServiceProvider services)
        {
            var replacement = new DotnetWatchProjectFileWatcher(BuildInputs,
                changed => OnProjectFilesChanged(services, changed), s_projectFileDebounce,
                exception => services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource)
                    .LogWarning(exception, "Build-input polling failed; the next poll will retry."));
            try
            {
                replacement.Start();
            }
            catch
            {
                replacement.Dispose();
                throw;
            }
            var previous = Interlocked.Exchange(ref _projectFileWatcher, replacement);
            // Overlap observation while replacing the inventory, then transfer edits
            // still in the old debounce window instead of disposing them silently.
            if (previous?.DisposeAndGetPendingChanges() is { Length: > 0 } pending)
            {
                OnProjectFilesChanged(services, pending);
            }
        }

        public void RequestRefresh(IServiceProvider services)
        {
            lock (_maintenanceGate)
            {
                _forceRefresh = true;
            }
            OnProjectFilesChanged(services, Roots);
        }

        private async Task ResumeAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            if (!_ready || _pendingResume is not { } pending)
            {
                return;
            }
            lock (_maintenanceGate)
            {
                if (_pendingChanges.Count != 0)
                {
                    return;
                }
            }
            foreach (var (member, ids) in pending)
            {
                foreach (var id in ids.Where(id => !member.IsManuallyStopped(id)))
                {
                    RequireSuccess(await member.StartOwnedAsync(id, services, cancellationToken).ConfigureAwait(false));
                }
            }
            _pendingResume = null;
        }

        private async Task StopServerAndConfirmCleanupAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var prepared = _prepared;
            await StopServerAsync(services, waitForStoppedEvent: true, cancellationToken).ConfigureAwait(false);
            if (prepared is not null &&
                !await WaitForContainedCleanupAsync(prepared, cancellationToken).ConfigureAwait(false))
            {
                throw new DistributedApplicationException("Contained watcher cleanup was not acknowledged; replacement is fenced. Restart the watch run.");
            }
            if (Interlocked.Exchange(ref _control, null) is { } control)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task StopServerAsync(IServiceProvider services, bool waitForStoppedEvent, CancellationToken cancellationToken)
        {
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var instances = notifications.GetCurrentStates(Resource);
            if (instances.Count > 1)
            {
                throw new DistributedApplicationException(
                    $"Expected one current watcher instance for '{Resource.Name}', but found {instances.Count}. Restart the watch run.");
            }
            if (instances.Count == 1 && !DotnetWatchServerInfrastructure.IsStopped(instances[0].Snapshot.State?.Text))
            {
                // An owned stop closes the status pipe before DCP publishes ResourceStopped.
                // Cancel its reader first so expected EOF cannot fence the replacement run.
                lock (_gate)
                {
                    if (!_shuttingDown)
                    {
                        _monitorCts?.Cancel();
                    }
                }
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _stoppedForRestart, stopped);
                try
                {
                    RequireSuccess(await DotnetWatchServerInfrastructure.StopOwnedAsync(Resource, services, cancellationToken).ConfigureAwait(false));
                    if (waitForStoppedEvent)
                    {
                        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _stoppedForRestart, null);
                }
            }
            else
            {
                Invalidate();
            }
            if (_monitorTask is { } monitor)
            {
                await monitor.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        private static void RequireSuccess(ExecuteCommandResult result)
        {
            if (!result.Success)
            {
                throw new DistributedApplicationException(result.Message ?? "The watch lifecycle operation failed.");
            }
        }

        private async Task<bool> WaitForContainedCleanupAsync(PreparedServer prepared, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    try
                    {
                        if (File.Exists(prepared.CleanupMarker) &&
                            await File.ReadAllTextAsync(prepared.CleanupMarker, timeout.Token).ConfigureAwait(false) == "contained-cleanup-complete")
                        {
                            Volatile.Write(ref _cleanupAcknowledged, 1);
                            Volatile.Write(ref _launchAttempted, 0);
                            return true;
                        }
                    }
                    catch (IOException)
                    {
                        // The WatchHost can still be replacing or flushing the marker.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Retry until the bounded cleanup deadline in case ownership is still settling.
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                return false;
            }
        }

        private static string WithRestartInstruction(string message, string instruction) =>
            message.Contains(instruction, StringComparison.Ordinal) ? message : $"{message} {instruction}";

        private void SettleRestartWaiters(Exception exception)
        {
            lock (_pendingRestarts)
            {
                foreach (var (_, pending) in _pendingRestarts)
                {
                    pending.TrySetException(exception);
                }
            }
        }

        public void RequestFailure(string reason)
        {
            lock (_gate)
            {
                if (_shuttingDown || _stopping.IsCancellationRequested || Interlocked.Exchange(ref _failureScheduled, 1) != 0)
                {
                    return;
                }
                var summary = $"Watch group '{Resource.Name}' failed. See the '{Resource.Name}' resource logs for details. " +
                    "Fix the cause and restart the complete Aspire run.";
                _failure = summary;
                _fenced = true;
                _ready = false;
                _successfulBuild.TrySetException(new DistributedApplicationException(summary));
                SettleRestartWaiters(new DistributedApplicationException(summary));
                _monitorCts?.Cancel();
                if (_services is not { } services || _notifications is not { } notifications)
                {
                    return;
                }
                _serverLogger!.LogError("Watch group failed: {Reason}", reason);
                _noticeLogger!.LogError(NoticeEventId(WatchNoticeKind.InfrastructureFailed), "{Notice}", summary);
                _failureTask = Task.Run(async () =>
                {
                    var entered = false;
                    try
                    {
                        await _restartLock.WaitAsync(_stopping).ConfigureAwait(false);
                        entered = true;
                        _stopping.ThrowIfCancellationRequested();
                        foreach (var project in Projects)
                        {
                            var member = project.Member!;
                            await member.PublishAsync(notifications).ConfigureAwait(false);
                            foreach (var id in member.GetTransportInstances(notifications, intendedOnly: false))
                            {
                                RequireSuccess(await member.StopOwnedAsync(id, services, _stopping).ConfigureAwait(false));
                            }
                        }
                        await StopServerAndConfirmCleanupAsync(services, _stopping).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        _serverLogger!.LogError(exception, "Failed to confirm cleanup of failed watch group. It remains fenced.");
                    }
                    finally
                    {
                        if (entered)
                        {
                            _restartLock.Release();
                        }
                    }
                }, CancellationToken.None);
            }
        }

        public Task ShutdownAsync(bool stopOwnedResources, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _shuttingDown = true;
                return _shutdownTask ??= ShutdownCoreAsync(stopOwnedResources, cancellationToken);
            }
        }

        private async Task ShutdownCoreAsync(bool stopOwnedResources, CancellationToken cancellationToken)
        {
            var logger = _lifecycleLogger;
            logger?.LogDebug("Stopping watch group {Resource}.", Resource.Name);
            _fenced = true;
            _shutdown?.Cancel();
            _monitorCts?.Cancel();
            _successfulBuild.TrySetCanceled(_stopping);
            SettleRestartWaiters(new OperationCanceledException("The watch run is stopping."));
            _projectFileWatcher?.Dispose();
            Task<PreparedServer>[] preparing;
            lock (_gate)
            {
                preparing = [.. _preparations.Where(task => !task.IsCompleted)];
            }
            try
            {
                // Invalidation can retire a preparation before it becomes _prepared.
                // Join those operations before disposing their services or run directory.
                await Task.WhenAll(preparing).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            foreach (var (name, work) in new[]
            {
                ("status monitoring", _monitorTask),
                ("resource monitoring", _resourceMonitorTask),
                ("build-input refresh", _maintenanceTask),
                ("failure cleanup", _failureTask)
            })
            {
                if (work is not null)
                {
                    logger?.LogDebug("Joining {Work} for stopping watch group {Resource}.", name, Resource.Name);
                    await work.ConfigureAwait(false);
                }
            }
            var prepared = _prepared;
            if (stopOwnedResources && Volatile.Read(ref _launchAttempted) != 0 &&
                _services is { } services && _notifications is { } notifications)
            {
                foreach (var project in Projects)
                {
                    var member = project.Member!;
                    foreach (var id in member.GetTransportInstances(notifications, intendedOnly: false))
                    {
                        logger?.LogDebug("Stopping owned watch transport {ResourceId}.", id);
                        RequireSuccess(await member.StopOwnedAsync(id, services, cancellationToken).ConfigureAwait(false));
                    }
                }
                logger?.LogDebug("Stopping owned watcher server {Resource}.", Resource.Name);
                // Replacement must observe DCP's stopped event before reusing the resource.
                // Final shutdown does not reuse it, and can race that event's publisher;
                // the post-containment acknowledgement below is its cleanup proof.
                await StopServerAsync(services, waitForStoppedEvent: false, cancellationToken).ConfigureAwait(false);
            }
            if (Interlocked.Exchange(ref _control, null) is { } control)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
            if (Volatile.Read(ref _launchAttempted) != 0 && prepared is not null &&
                !await WaitForContainedCleanupAsync(prepared, cancellationToken).ConfigureAwait(false))
            {
                _serverLogger?.LogWarning(
                    "Contained watcher cleanup was not acknowledged before shutdown; preserving the owned watch run directory {Directory}.",
                    _state.RunDirectory);
            }
            else if (Volatile.Read(ref _launchAttempted) == 0)
            {
                Volatile.Write(ref _cleanupAcknowledged, 1);
            }
            _monitorCts?.Dispose();
            _monitorCts = null;
            _shutdown?.Dispose();
            _shutdown = null;
            logger?.LogDebug("Watch group {Resource} stopped; contained cleanup acknowledged: {Acknowledged}.",
                Resource.Name, CleanupAcknowledged);
        }

        public bool CleanupAcknowledged => Volatile.Read(ref _cleanupAcknowledged) != 0;

        // The watcher names a file-based app by its virtual project path (app.cs.csproj); the model has the .cs file.
        private string[] ToEntryPointPaths(string[] paths) =>
            [.. paths.Select(path => ToFileBasedProject(path) is { } project ? project.ProjectPath : path)];

        private WatchedProject? ToFileBasedProject(string path) =>
            path.EndsWith(".cs.csproj", StringComparison.OrdinalIgnoreCase)
                ? Projects.FirstOrDefault(p => PathsEqual(p.ProjectPath, path[..^".csproj".Length]))
                : null;

        /// <summary>
        /// The watcher (10.0.x, and SDK main at the time of writing) rebuilds a restarted file-based app through its
        /// virtual project path (<c>dotnet build ticker.cs.csproj</c>), which fails (MSB1009) and leaves the app
        /// stopped; its <c>build_complete</c> names that path. A fresh watcher builds the .cs file itself as its initial
        /// build, so restart the app's (dedicated) watcher instead, which also reports a genuine compile error.
        /// </summary>
        /// <returns>Whether the failure was handled (and its notice suppressed).</returns>
        private bool RebuildFileBasedAppIfStuck(IServiceProvider services, string[] rawProjects)
        {
            var fileBased = rawProjects.Select(ToFileBasedProject).ToArray();
            if (fileBased.Length == 0 || fileBased.Any(p => p is null))
            {
                return false;
            }

            lock (_maintenanceGate)
            {
                _forceRefresh = true;
            }
            OnProjectFilesChanged(services, [.. fileBased.OfType<WatchedProject>().Select(project => project.ProjectPath)]);

            return true;
        }

        private void EmitNotices(IServiceProvider services, IReadOnlyList<WatchNotice> notices)
        {
            if (notices.Count == 0)
            {
                return;
            }

            var noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
            var loggers = services.GetRequiredService<ResourceLoggerService>();
            var projects = Projects;
            foreach (var notice in notices)
            {
#pragma warning disable CA2254 // The notice text is the message; it has no structured parameters.
                noticeLogger.Log(notice.Level, NoticeEventId(notice.Kind), notice.Message);
#pragma warning restore CA2254

                // Before the first successful build every service is waiting on the server, so tell all of them why.
                var targets = notice.Kind == WatchNoticeKind.BuildFailed && !_ready
                    ? projects
                    : projects.Where(project => notice.ProjectPaths.Any(path => PathsEqual(path, project.ProjectPath))).ToList();
                foreach (var target in targets)
                {
                    loggers.GetLogger(target.Resource).Log(notice.Level, "[watch] {Message}", notice.Message);
                }
            }
        }

        /// <summary>
        /// Mirrors a status event on the project resource. The events name project paths only, so a launch or crash is
        /// shown on the resource's state when it has a single replica; with several replicas the watcher doesn't say
        /// which one, and only the last-event property changes.
        /// </summary>
        private static async Task PublishWatchEventAsync(ResourceNotificationService notifications, WatchedProject project, DotnetWatchStatus status)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var lastEvent = status.Type == DotnetWatchStatusTypes.ProcessExited
                ? $"process_exited({status.ExitCode}) @ {stamp}" : $"{status.Type} @ {stamp}";
            project.Member!.Observe(status.Type, status.Success, status.ExitCode, lastEvent);
            await project.Member.PublishAsync(notifications).ConfigureAwait(false);
        }
    }

    /// <summary>MSBuild properties the watcher's process environment carries for a server's projects.</summary>
    /// <remarks>
    /// The watcher has no per-project build arguments (it hardcodes none), so build settings travel in its process
    /// environment, which MSBuild reads as properties for its builds, its in-process evaluation and the
    /// <c>dotnet run --no-build</c> launches. Environment properties have the lowest precedence: a project that sets the
    /// property unconditionally wins, unlike the global properties of the normal build. Debug is MSBuild's default, so
    /// it's left unset: anything set here is restored to the proxy's own value for the services, and a variable that was
    /// originally unset comes back empty rather than unset.
    /// </remarks>
    internal static Dictionary<string, string> GetBuildProperties(IEnumerable<DotnetProjectMetadata> projects)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = projects.ToList();
        if (metadata.Select(m => m.BuildConfiguration).FirstOrDefault(c => !string.IsNullOrEmpty(c)) is { } buildConfiguration &&
            !string.Equals(buildConfiguration, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            properties["Configuration"] = buildConfiguration;

            // The watcher only attaches its hot reload agent to unoptimized builds, and the agent is what applies the
            // service environment (endpoints, connection strings) to the launched app. Release defaults Optimize only
            // when it's empty, so this keeps Release's output paths and constants.
            properties["Optimize"] = "false";
        }

        if (metadata.Any(m => ((IProjectMetadata)m).IsFileBasedApp))
        {
            // File-based apps default to PublishAot, whose trimming defaults turn off startup hooks and metadata updates
            // in the runtimeconfig. The watcher's agent is a startup hook, so on .NET 10 every edit fails with ENC0097
            // (.NET 11 turns both back on for Debug through runtimeconfig.dev.json).
            properties["StartupHookSupport"] = "true";
            properties["MetadataUpdaterSupport"] = "true";
        }

        return properties;
    }

    /// <param name="Muxer">The <c>dotnet</c> that runs the watcher and the watch host's server command.</param>
    /// <param name="WatchHost">Path of <c>Aspire.Hosting.Dotnet.WatchHost.dll</c>.</param>
    /// <param name="Args">Server resource arguments: the watch host's <c>server</c> command wrapping the watcher.</param>
    /// <param name="Environment">Environment of the watcher.</param>
    /// <param name="WatcherOnlyNames">Variables the watcher strips from launched services.</param>
    /// <param name="CleanupMarker">Private confirmation of contained cleanup for this generation.</param>
    private sealed record PreparedServer(string Muxer, string WatchHost, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> WatcherOnlyNames, string CleanupMarker);
}
