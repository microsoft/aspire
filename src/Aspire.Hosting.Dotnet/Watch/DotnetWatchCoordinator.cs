// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // RunConfiguration is experimental.
#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental.
#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchArgsOverrideAnnotation is experimental.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// A hidden, infrastructure-only watch server that hosts <c>Microsoft.DotNet.HotReload.Watch.Aspire server</c> for a
/// group of watched .NET projects in one AppHost run.
/// </summary>
internal sealed class DotnetWatchServerResource(string name, string workingDirectory)
    : ExecutableResource(name, "dotnet", workingDirectory);

/// <summary>
/// SPIKE: Aspire-controlled C# service watch (plan Sessions 6/6a hosting side).
/// </summary>
/// <remarks>
/// <para>
/// When <c>ExecutionContext.RunConfiguration.WatchEnabled</c> is set in run mode, every <c>AddDotnetProject</c>
/// resource is registered with a <see cref="DotnetWatchServerResource"/>. Projects share a server unless they need a
/// different watcher process: projects under a different <c>global.json</c> (which can select a different SDK) get
/// their own server, and so does every project with <c>WithBuildEnvironment</c> (the watcher builds with its own
/// process environment, so build-only variables can't be scoped to one project of a shared server). A server acquires
/// the watcher matching its resolved SDK, then runs its <c>server</c> command with its roots, hosted by the watch host
/// (<c>Aspire.Hosting.Dotnet.WatchHost.dll server</c>, shipped in this package) so that stopping it is prompt. SPIKE deviation from D7: Aspire's
/// coordinated initial build is skipped in watch runs; the watcher's own initial build is the only build, and the
/// server becomes healthy once it succeeds (a failed build waits for a fix instead of failing the run). Each service
/// instance is launched through the watch host's <c>proxy</c> command, which reads the service
/// environment from its own process environment (never <c>-e</c> arguments) and sends it over the watcher's resource
/// pipe.
/// </para>
/// <para>
/// Known spike shortcuts are marked <c>TODO(spike)</c>/<c>HACK(spike)</c>.
/// </para>
/// </remarks>
internal static partial class DotnetWatchCoordinator
{
    internal const string ServerResourceName = "dotnet-watch";

    internal const string ForwardedEnvironmentNamesVariable = DotnetWatchHostProtocol.ForwardedEnvironmentNamesVariable;
    internal const string RestoreEnvironmentNamesVariable = DotnetWatchHostProtocol.RestoreEnvironmentNamesVariable;
    internal const string InstanceVariable = DotnetWatchHostProtocol.InstanceVariable;

    internal const string CrashedState = "Crashed";

    /// <summary>
    /// Logger category for concise, user-facing watch notices. <c>aspire run --watch</c> prints AppHost log entries in
    /// this category (and its sub-categories) to the terminal.
    /// </summary>
    internal const string NoticeLoggerCategory = "Aspire.Hosting.Dotnet.Watch";

    /// <summary>
    /// The event id of a notice logged to <see cref="NoticeLoggerCategory"/>: <see cref="NoticeEventIdBase"/> plus the
    /// <see cref="WatchNoticeKind"/>, which the AppHost log backchannel carries to the CLI alongside the text.
    /// </summary>
    internal const int NoticeEventIdBase = 7100;

    internal static EventId NoticeEventId(WatchNoticeKind kind) => new(NoticeEventIdBase + (int)kind, $"WatchNotice.{kind}");

    // DCP expands this per replica to the replica's unique name suffix (the OTEL service.instance.id), so
    // "<resource>-<suffix>" is the replica's resource id.
    private const string InstanceSuffixTemplate = "{{- index .Annotations \"otel-service-instance-id\" -}}";

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, WatchState> s_states = new();
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, object> s_warned = new();

    // Keyed by resource: IResourceBuilder.ApplicationBuilder can be a different wrapper than the builder Configure saw.
    private static readonly ConditionalWeakTable<DotnetProjectResource, WatchState> s_projectStates = new();

    /// <summary>Temp-directory variables of the platform (what <see cref="Path.GetTempPath"/> reads).</summary>
    internal static string[] GetTempVariables(bool windows) => windows ? ["TMP", "TEMP"] : ["TMPDIR"];

    /// <summary>Whether watch was requested for this run (<c>AppHost:Run:WatchEnabled</c> in run mode).</summary>
    internal static bool IsWatchRun(IDistributedApplicationBuilder builder) =>
        builder.ExecutionContext.IsRunMode && builder.ExecutionContext.RunConfiguration.WatchEnabled;

    /// <summary>
    /// Whether <c>AddDotnetProject</c> should route this run through the watch server.
    /// </summary>
    /// <remarks>
    /// Under an IDE debug session the IDE owns change tracking (D8): no service is watched, and the terminal and each
    /// service's log say so. Nothing else is required of the launcher: <c>aspire run --watch</c> and a plain
    /// <c>dotnet run</c> of the AppHost with <c>AppHost:Run:WatchEnabled=true</c> behave the same.
    /// </remarks>
    internal static bool ShouldWatch(IDistributedApplicationBuilder builder)
    {
        if (!IsWatchRun(builder))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_PORT"]))
        {
            OnceForBuilder(builder, () => builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
            {
                e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogWarning(
                    "Hot reload of .NET services is handled by the IDE: the AppHost is running under an IDE debug session, so 'aspire run --watch' doesn't watch them.");
                return Task.CompletedTask;
            }));
            return false;
        }

        return true;
    }

    /// <summary>Per-resource variant of <see cref="ShouldWatch(IDistributedApplicationBuilder)"/>.</summary>
    /// <remarks>
    /// Whether the resource is actually watched is decided once the model is final (see <see cref="Evaluate"/>), because
    /// <c>WithWatch</c>, <c>WaitForCompletion</c>, lifetimes and launch tools are configured after <c>AddDotnetProject</c>.
    /// </remarks>
    internal static bool ShouldWatch(IDistributedApplicationBuilder builder, string resourceName)
    {
        var watch = ShouldWatch(builder);
        if (!watch && IsWatchRun(builder) && !string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_PORT"]))
        {
            builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
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

    internal static void Configure(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> resource,
        DotnetProjectMetadata metadata,
        DotnetProjectBuildCoordinator.CoordinatorState? buildCoordinator)
    {
        var state = s_states.GetValue(builder, static b => new WatchState(b));
        s_projectStates.AddOrUpdate(resource.Resource, state);
        state.Configure(resource, metadata, buildCoordinator);
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
    internal static void ReportBuildCompleteForTesting(DotnetProjectResource resource, bool success)
    {
        if (!s_projectStates.TryGetValue(resource, out var state) || state.Projects.Find(p => p.Resource == resource) is not { } project)
        {
            throw new InvalidOperationException($"'{resource.Name}' isn't watched.");
        }

        project.Server.OnBuildCompleteForTesting(success);
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
            return new(WatchDecisionKind.Rejected,
                $"'{resource.Name}' has a persistent lifetime, but a watched service belongs to this run's watch server and can't outlive it.");
        }

        if (HasCustomLaunch(resource))
        {
            return new(WatchDecisionKind.Rejected,
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
    private static List<WatchedProject> ApplyEligibility(List<WatchedProject> projects, DistributedApplicationModel model, IServiceProvider services)
    {
        var errors = new List<string>();
        var excluded = new List<(WatchedProject Project, string Reason)>();
        foreach (var project in projects)
        {
            var decision = Evaluate(project.Resource, model.Resources);
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
                "'aspire run --watch' can't hot reload these .NET projects:" + Environment.NewLine +
                string.Concat(errors.Select(e => $"  - {e}{Environment.NewLine}")) +
                "Call WithWatch(false) on a project to run it without hot reload.");
        }

        projects.RemoveAll(p => excluded.Any(x => ReferenceEquals(x.Project, p)));

        // The watcher builds the watched projects; Aspire's coordinated build keeps only the excluded ones.
        foreach (var project in projects)
        {
            project.BuildCoordinator?.RemoveResource(project.Resource);
        }

        var noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
        var loggers = services.GetRequiredService<ResourceLoggerService>();
        foreach (var (project, reason) in excluded)
        {
            foreach (var annotation in project.Annotations)
            {
                project.Resource.Annotations.Remove(annotation);
            }

            noticeLogger.LogInformation("{Notice}", reason);
            loggers.GetLogger(project.Resource).LogInformation("[watch] {Notice}", reason);
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

    /// <summary>Parses the watcher's verbose "Launched ... process id N" line for a proxy-marked launch.</summary>
    internal static bool TryParseLaunchedLine(string line, out int processId, out string instance)
    {
        processId = 0;
        instance = string.Empty;
        if (LaunchedLineRegex().Match(line) is not { Success: true } match)
        {
            return false;
        }

        instance = match.Groups["instance"].Value;
        return int.TryParse(match.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
    }

    /// <summary>Parses the watcher's verbose "Process id N ran for Xms and exited with exit code C" line.</summary>
    internal static bool TryParseExitedLine(string line, out int processId, out int exitCode)
    {
        processId = exitCode = 0;
        return ExitedLineRegex().Match(line) is { Success: true } match
            && int.TryParse(match.Groups["pid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId)
            && int.TryParse(match.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out exitCode);
    }

    /// <summary>Parses MSBuild's "Switch: path/app.cs.csproj" line that follows MSB1009 for a file-based app's virtual project.</summary>
    internal static bool TryParseVirtualProjectBuildFailure(string line, [NotNullWhen(true)] out string? entryPoint)
    {
        entryPoint = VirtualProjectSwitchRegex().Match(line) is { Success: true } match ? match.Groups["path"].Value : null;
        return entryPoint is not null;
    }

    private static readonly TimeSpan s_referenceRebuildDebounce = TimeSpan.FromSeconds(1);

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"Switch: (?<path>\S.*\.cs)\.csproj\s*$")]
    private static partial Regex VirtualProjectSwitchRegex();

    [GeneratedRegex(InstanceVariable + @"=(?<instance>[A-Za-z0-9-]+).*process id (?<pid>\d+)")]
    private static partial Regex LaunchedLineRegex();

    [GeneratedRegex(@"Process id (?<pid>\d+) ran for \d+ ?ms and exited with exit code (?<code>-?\d+)")]
    private static partial Regex ExitedLineRegex();

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
    }

    private sealed class WatchState
    {
        private readonly List<WatchServer> _servers = [];

        public WatchState(IDistributedApplicationBuilder builder)
        {
            Builder = builder;
            Id = Guid.NewGuid().ToString("N")[..12];

            // Holds the Unix sockets and redirected temp directories. Deleted when the AppHost stops. Kept short:
            // Unix socket paths are limited to ~104 (macOS) / 108 (Linux) characters.
            RunDirectory = Path.Combine(Path.GetTempPath(), $"aspw-{Id}");

            builder.Eventing.Subscribe<BeforeStartEvent>((e, ct) =>
            {
                // Resources removed or replaced after registration must not be built or watched.
                Projects.RemoveAll(p => !e.Model.Resources.Contains(p.Resource));

                // Opt-outs, run-to-completion and unsupported projects can only be decided on the final model.
                ExcludedProjects = ApplyEligibility(Projects, e.Model, e.Services);

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
                    return Task.CompletedTask;
                }

                // The forwarded-name list must be computed after every user/integration environment callback, so it
                // is appended once the model is final.
                foreach (var project in Projects)
                {
                    var watched = project;
                    watched.Resource.Annotations.Add(new EnvironmentCallbackAnnotation(async ctx =>
                    {
                        ctx.EnvironmentVariables[ForwardedEnvironmentNamesVariable] = string.Join(';', ctx.EnvironmentVariables.Keys
                            .Where(k => k is not (ForwardedEnvironmentNamesVariable or RestoreEnvironmentNamesVariable or InstanceVariable)));

                        var prepared = await watched.Server.PrepareAsync(ctx.ExecutionContext.Services, ctx.CancellationToken).ConfigureAwait(false);
                        ctx.EnvironmentVariables[RestoreEnvironmentNamesVariable] = string.Join(';', prepared.WatcherOnlyNames);
                        ctx.EnvironmentVariables[InstanceVariable] = $"{watched.Resource.Name}-{InstanceSuffixTemplate}";
                    }));
                }

                var lifetime = e.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
                lifetime.ApplicationStopped.Register(CleanupRunDirectory);
                foreach (var server in active)
                {
                    server.Start(e.Services, lifetime.ApplicationStopping);
                }

                return Task.CompletedTask;
            });
        }

        public IDistributedApplicationBuilder Builder { get; }
        public string Id { get; }
        public string RunDirectory { get; }

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

        public void Configure(
            IResourceBuilder<DotnetProjectResource> resource,
            DotnetProjectMetadata metadata,
            DotnetProjectBuildCoordinator.CoordinatorState? buildCoordinator)
        {
            var existing = resource.Resource.Annotations.ToHashSet();
            var projectPath = ((IProjectMetadata)metadata).ProjectPath;
            var scope = FindSdkScope(Path.GetDirectoryName(projectPath)!);
            var server = _servers.FirstOrDefault(s => s.DedicatedTo is null && string.Equals(s.SdkScope, scope, PathComparison))
                ?? AddServer(scope, dedicatedTo: null, firstProjectName: resource.Resource.Name);
            var project = new WatchedProject(resource.Resource, metadata, buildCoordinator, server);

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
                ctx.Args.Add(projectPath);
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

            // Aspire's Rebuild command stays available: it stops the service, runs `dotnet build` and starts it again,
            // and the watcher picks the fresh output back up.
            project.Annotations.AddRange(resource.Resource.Annotations.Where(a => !existing.Contains(a)));
            Projects.Add(project);

            // A file-based app gets its own watcher: on any project file change the watcher (rc.2) restores every root
            // through its virtual project path (app.cs.csproj), which fails, and then crashes on the next file event.
            if (resource.Resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>().Any() ||
                ((IProjectMetadata)metadata).IsFileBasedApp)
            {
                UseDedicatedServer(resource);
            }
        }

        public void UseDedicatedServer(IResourceBuilder<DotnetProjectResource> resource)
        {
            if (Projects.Find(p => p.Resource == resource.Resource) is not { } project || project.Server.DedicatedTo == project.Resource)
            {
                return;
            }

            var previous = project.Server;
            var server = AddServer(previous.SdkScope, dedicatedTo: project.Resource, firstProjectName: project.Resource.Name);

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
            foreach (var server in _servers)
            {
                server.Invalidate();
            }

            try
            {
                if (Directory.Exists(RunDirectory))
                {
                    Directory.Delete(RunDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; the directory lives under the temp path.
            }
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed class WatchServer
    {
        // How long a status event waits for the matching verbose log line (DCP streams logs with some delay).
        private static readonly TimeSpan s_correlationTimeout = TimeSpan.FromSeconds(3);

        private readonly WatchState _state;
        private readonly int _index;
        private readonly object _gate = new();
        private Task<PreparedServer>? _prepareTask;
        private CancellationTokenSource? _monitorCts;
        private int _generation;
        private volatile bool _ready;
        private volatile string? _lastBuildError;

        // Completed by the watcher's first successful build of the current server run; re-armed when the server restarts.
        private TaskCompletionSource _successfulBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _noticeGate = new();
        private DotnetWatchEventInterpreter? _interpreter;
        private bool _flushScheduled;

        // Per-replica attribution of launches and exits, fed by the watcher's verbose log lines.
        private readonly object _replicaGate = new();
        private readonly Dictionary<int, string> _instanceByProcessId = [];
        private readonly List<ReplicaEvent> _launches = [];
        private readonly List<ReplicaEvent> _exits = [];

        public WatchServer(WatchState state, string name, int index, string? sdkScope, DotnetProjectResource? dedicatedTo)
        {
            _state = state;
            _index = index;
            SdkScope = sdkScope;
            DedicatedTo = dedicatedTo;
            ResourcePipeName = OperatingSystem.IsWindows()
                ? $"aspire-watch-{state.Id}-{index}"
                : Path.Combine(state.RunDirectory, $"res{index}.sock");

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

            // Each (re)start of the server gets a fresh prepare (new status pipe + listener, re-resolved SDK).
            builder.Eventing.Subscribe<ResourceStoppedEvent>(Builder.Resource, (_, _) =>
            {
                Invalidate();
                Interlocked.Exchange(ref _stoppedForRestart, null)?.TrySetResult();
                return Task.CompletedTask;
            });
        }

        public IResourceBuilder<DotnetWatchServerResource> Builder { get; }
        public DotnetWatchServerResource Resource => Builder.Resource;
        public string ResourcePipeName { get; }
        public string? SdkScope { get; }
        public DotnetProjectResource? DedicatedTo { get; }

        private List<WatchedProject> Projects => _state.Projects.Where(p => p.Server == this).ToList();

        private string[] Roots => Projects.Select(p => p.ProjectPath).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>
        /// The file that tells a project's proxies (<c>--launch-gate</c>) that the project launched once in the current
        /// server generation. Until it exists, the proxies launch the project one at a time: the watcher hands a
        /// concurrent launch of the same web project its browser refresh server before the first launch has started
        /// it, and that launch fails ("Server not started", dotnet/sdk BrowserRefreshServerFactory).
        /// </summary>
        public string GetLaunchMarkerPath(string projectPath) => GetLaunchMarkerPath(projectPath, Volatile.Read(ref _generation));

        private string GetLaunchMarkerPath(string projectPath, int generation) => Path.Combine(
            _state.RunDirectory,
            $"launched{_index}-{generation}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectPath)), 0, 8)}");

        public void Start(IServiceProvider services, CancellationToken stopping)
        {
            _ = Task.Run(() => TailServerLogsAsync(services, stopping), CancellationToken.None);
        }

        public Task<PreparedServer> PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_prepareTask is null)
                {
                    _monitorCts?.Cancel();
                    _monitorCts = new CancellationTokenSource();
                    _ready = false;
                    _lastBuildError = null;
                    var referencedDirectories = Projects.Select(p => p.ProjectPath).Distinct(StringComparer.Ordinal)
                        .ToDictionary(p => p, DotnetWatchEventInterpreter.ReadReferencedDirectories, StringComparer.Ordinal);
                    lock (_noticeGate)
                    {
                        _interpreter = new DotnetWatchEventInterpreter(Projects.Select(p => (p.ProjectPath, p.Resource.Name)), Resource.WorkingDirectory, referencedDirectories);
                    }

                    if (_successfulBuild.Task.IsCompleted)
                    {
                        _successfulBuild = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    _prepareTask = PrepareWithNoticeAsync(services, ++_generation, _successfulBuild, _monitorCts.Token, cancellationToken);
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

        internal void OnBuildCompleteForTesting(bool success)
        {
            TaskCompletionSource successfulBuild;
            lock (_gate)
            {
                successfulBuild = _successfulBuild;
            }

            OnBuildComplete(success, error: null, successfulBuild);
        }

        /// <summary>Records a <c>build_complete</c> status from the watcher run that <paramref name="successfulBuild"/> belongs to.</summary>
        internal void OnBuildComplete(bool success, string? error, TaskCompletionSource successfulBuild)
        {
            if (success)
            {
                _lastBuildError = null;
                _ready = true;
                successfulBuild.TrySetResult();
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
                _prepareTask = null;
                _monitorCts?.Cancel();
                _ready = false;
            }
        }

        private async Task<PreparedServer> PrepareWithNoticeAsync(IServiceProvider services, int generation, TaskCompletionSource successfulBuild, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            try
            {
                return await PrepareCoreAsync(services, generation, successfulBuild, monitorToken, cancellationToken).ConfigureAwait(false);
            }
            catch (DistributedApplicationException ex)
            {
                // Forget the failed attempt so restarting the server resource retries (e.g. after selecting a supported SDK),
                // and say so in the terminal: otherwise the services just sit in "Waiting" with the reason buried in a log.
                lock (_gate)
                {
                    _prepareTask = null;
                }

                services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogError(
                    "❌ Hot reload couldn't start: {Message} Watched services wait until it does; after fixing the problem, start '{Server}' from the dashboard ('aspire resource {Server} start').",
                    ex.Message, Resource.Name, Resource.Name);
                throw;
            }
        }

        private async Task<PreparedServer> PrepareCoreAsync(IServiceProvider services, int generation, TaskCompletionSource successfulBuild, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource);

            // Projects that aren't watched are built by Aspire's coordinated build. Let it finish first so the watcher's
            // build doesn't race it over shared project outputs.
            var excluded = _state.ExcludedProjects.Where(p => p.BuildCoordinator is not null).ToList();
            if (excluded.Count > 0)
            {
                logger.LogInformation("Waiting for the coordinated build of the projects that aren't watched: {Projects}", string.Join(", ", excluded.Select(p => p.Resource.Name)));
                foreach (var project in excluded)
                {
                    await project.BuildCoordinator!.WaitForBuildCompletionAsync(project.Resource, services, cancellationToken).ConfigureAwait(false);
                }
            }

            DotnetWatchServerInfrastructure.EnsurePrivateDirectory(_state.RunDirectory);

            var roots = Roots;
            var sdk = await DotnetWatchToolResolver.ResolveSdkAsync(Path.GetDirectoryName(roots[0])!, cancellationToken).ConfigureAwait(false);
            var configuration = _state.Builder.Configuration;
            var toolDll = DotnetWatchToolResolver.ResolveToolPath(
                configuration[DotnetWatchToolResolver.ToolPathConfigurationKey],
                DotnetWatchToolResolver.GetToolsDirectory(services.GetRequiredService<DistributedApplicationOptions>()),
                sdk.SdkVersion,
                logger);
            await DotnetWatchToolResolver.EnsureRuntimeAvailableAsync(toolDll, sdk.Muxer, sdk.SdkVersion, cancellationToken).ConfigureAwait(false);

            // The watch host hosts the watcher (and proxies each replica). It targets net10.0 and rolls forward to newer
            // majors, so the muxer that runs the watcher runs it too.
            var watchHost = _state.GetWatchHost();
            logger.LogInformation("Watching {Count} root(s) with SDK {SdkDirectory}: {Roots}", roots.Length, sdk.SdkDirectory, string.Join(", ", roots));

            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var (name, value) in GetBuildProperties(Projects.Select(p => p.Metadata)))
            {
                environment[name] = value;
            }

            if (DedicatedTo is { } owner)
            {
                foreach (var (name, value) in await EvaluateBuildEnvironmentAsync(owner, services, logger, cancellationToken).ConfigureAwait(false))
                {
                    environment[name] = value;
                }

                logger.LogInformation("Build environment for '{Resource}': {Names}", owner.Name, string.Join(", ", environment.Keys));
            }

            if (roots.Length > 1)
            {
                // TODO(upstream dotnet/sdk#51311): with more than one root the watcher builds a temporary .slnx from
                // Path.GetTempFileName() with the temp directory as working directory, so the muxer selects the SDK
                // from the temp directory's global.json scope instead of --sdk ("Build FAILED, 0 errors" when they
                // differ). Only when they do differ, point the watcher's temp directory at a private directory with a
                // global.json pinning the resolved SDK. Services get their own temp directory back (restore list).
                var tempSdk = await DotnetWatchToolResolver.TryGetSdkVersionAsync(sdk.Muxer, Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(tempSdk, sdk.SdkVersion, StringComparison.OrdinalIgnoreCase))
                {
                    var tempDirectory = Path.Combine(_state.RunDirectory, $"tmp{_index}");
                    Directory.CreateDirectory(tempDirectory);
                    await File.WriteAllTextAsync(
                        Path.Combine(tempDirectory, "global.json"),
                        $$"""{ "sdk": { "version": "{{sdk.SdkVersion}}", "rollForward": "disable", "allowPrerelease": true } }""",
                        cancellationToken).ConfigureAwait(false);
                    foreach (var name in GetTempVariables(OperatingSystem.IsWindows()))
                    {
                        environment[name] = tempDirectory;
                    }

                    logger.LogInformation(
                        "The temp directory selects SDK {TempSdk} rather than {Sdk}; the watcher's multi-project build uses {TempDirectory} instead.",
                        tempSdk ?? "(none)", sdk.SdkVersion, tempDirectory);
                }
            }

            var statusPipeName = OperatingSystem.IsWindows()
                ? $"aspire-watch-status-{_state.Id}-{_index}-{generation}"
                : Path.Combine(_state.RunDirectory, $"status{_index}-{generation}.sock");

            // --verbose adds the watcher's "Launched ... process id" / "Process id ... exited" lines, which attribute
            // launches and crashes to replicas.
            var args = new List<string>
            {
                "exec", watchHost, DotnetWatchHostProtocol.ServerCommand, "--", sdk.Muxer, "exec", toolDll, "server",
                "--sdk", sdk.SdkDirectory, "--server", ResourcePipeName, "--status-pipe", statusPipeName, "--verbose"
            };
            foreach (var root in roots)
            {
                args.Add("--resource");
                args.Add(root);
            }

            // The status pipe listener must exist before the watcher connects to it.
            _ = Task.Run(() => MonitorStatusAsync(services, logger, statusPipeName, successfulBuild, generation, monitorToken), CancellationToken.None);

            // Everything set only for the watcher must not leak into the services, which inherit its environment.
            return new PreparedServer(sdk.Muxer, watchHost, args, environment, [.. environment.Keys]);
        }

        private static async Task<IReadOnlyDictionary<string, string>> EvaluateBuildEnvironmentAsync(
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
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
                {
                    WatchStatus? status;
                    try
                    {
                        status = JsonSerializer.Deserialize<WatchStatus>(line);
                    }
                    catch (JsonException)
                    {
                        serverLogger.LogWarning("Unrecognized watch status: {Line}", line);
                        continue;
                    }

                    if (status is null)
                    {
                        continue;
                    }

                    diagnostics.LogDebug("dotnet-watch status: {Line}", line);
                    var projects = ToEntryPointPaths(status.projects ?? []);

                    if (status.type == DotnetWatchStatusTypes.BuildComplete)
                    {
                        string? error;
                        lock (_noticeGate)
                        {
                            error = _interpreter?.FirstError;
                        }

                        OnBuildComplete(status.success == true, error, successfulBuild);
                    }

                    IReadOnlyList<WatchNotice> notices;
                    lock (_noticeGate)
                    {
                        notices = _interpreter?.OnStatus(status.type, projects, status.success, status.exitCode) ?? [];
                    }

                    EmitNotices(services, notices);

                    // Status events identify project paths only (plan 3.3). Launches and exits are attributed to a
                    // replica through the verbose log lines; everything else applies to every resource with the path.
                    foreach (var path in projects)
                    {
                        if (status.type == DotnetWatchStatusTypes.ProcessStarted)
                        {
                            File.WriteAllBytes(GetLaunchMarkerPath(path, generation), []);
                        }

                        var targets = Projects.Where(p => string.Equals(p.ProjectPath, path, StringComparison.Ordinal)).ToList();
                        if (targets.Count == 0)
                        {
                            continue;
                        }

                        if (status.type is DotnetWatchStatusTypes.ProcessExited or DotnetWatchStatusTypes.ProcessStarted)
                        {
                            _ = Task.Run(() => PublishReplicaEventAsync(notifications, targets, status, cts.Token), CancellationToken.None);
                        }
                        else
                        {
                            foreach (var target in targets)
                            {
                                await PublishWatchEventAsync(notifications, target.Resource, resourceId: null, status).ConfigureAwait(false);
                            }
                        }
                    }
                }

                serverLogger.LogInformation("Watcher closed the status pipe.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                serverLogger.LogError(ex, "Watch status monitor failed.");
            }
        }

        private async Task PublishReplicaEventAsync(ResourceNotificationService notifications, List<WatchedProject> targets, WatchStatus status, CancellationToken cancellationToken)
        {
            try
            {
                var events = status.type == DotnetWatchStatusTypes.ProcessExited ? _exits : _launches;
                var deadline = DateTime.UtcNow + s_correlationTimeout;
                while (true)
                {
                    lock (_replicaGate)
                    {
                        foreach (var candidate in events)
                        {
                            if (candidate.Consumed || (status.type == DotnetWatchStatusTypes.ProcessExited && candidate.ExitCode != status.exitCode))
                            {
                                continue;
                            }

                            if (notifications.TryGetCurrentState(candidate.Instance, out var current)
                                && targets.Find(t => t.Resource == current.Resource) is { } target)
                            {
                                candidate.Consumed = true;
                                _ = PublishWatchEventAsync(notifications, target.Resource, candidate.Instance, status);
                                return;
                            }
                        }
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        break;
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                // No matching log line (for example, a renamed replica): fall back to every replica with the path.
                foreach (var target in targets)
                {
                    await PublishWatchEventAsync(notifications, target.Resource, resourceId: null, status).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private readonly HashSet<DotnetProjectResource> _pendingReferenceRebuilds = [];
        private int _referenceRebuildGeneration;
        private readonly SemaphoreSlim _restartLock = new(1, 1);
        private TaskCompletionSource? _stoppedForRestart;

        /// <summary>
        /// The watcher reloads a changed project file but doesn't restore or rebuild it, so new package or project
        /// references wouldn't reach the service. Restarts the watcher with its services once saves settle.
        /// </summary>
        private void ScheduleReferenceRebuild(IServiceProvider services, IEnumerable<WatchedProject> projects)
        {
            int generation;
            lock (_pendingReferenceRebuilds)
            {
                foreach (var project in projects)
                {
                    _pendingReferenceRebuilds.Add(project.Resource);
                }

                if (_pendingReferenceRebuilds.Count == 0)
                {
                    return;
                }

                generation = ++_referenceRebuildGeneration;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(s_referenceRebuildDebounce).ConfigureAwait(false);
                lock (_pendingReferenceRebuilds)
                {
                    if (generation != _referenceRebuildGeneration)
                    {
                        return;
                    }

                    _pendingReferenceRebuilds.Clear();
                }

                await RestartWithServicesAsync(services, "to apply the package and project reference changes").ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Stops this server's services, restarts the watcher and starts the services again. Building outside the
        /// watcher (Aspire's Rebuild) isn't enough: the running watcher keeps its compilation baselines and replays its
        /// hot reload delta history to every process it launches. After removing a package whose API an earlier edit
        /// used, the rebuilt assembly matched the original (same MVID), the replayed deltas applied and the service
        /// aborted at startup (MethodAccessException). A fresh watcher restores and builds the projects as its initial
        /// build; the services wait for it as they do at startup.
        /// </summary>
        private async Task RestartWithServicesAsync(IServiceProvider services, string reason)
        {
            await _restartLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var commands = services.GetRequiredService<ResourceCommandService>();
                var resources = Projects.Select(p => p.Resource).Distinct().ToArray();
                EmitNotices(services, [new WatchNotice(WatchNoticeKind.Restarting, LogLevel.Information,
                    $"♻️ Restarting {string.Join(", ", resources.Select(r => r.Name))} with a fresh watcher {reason}.",
                    [.. Projects.Select(p => p.ProjectPath).Distinct(StringComparer.Ordinal)])]);

                foreach (var resource in resources)
                {
                    await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StopCommand).ConfigureAwait(false);
                }

                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _stoppedForRestart, stopped);
                var result = await commands.ExecuteCommandAsync(Resource, DotnetWatchServerInfrastructure.StopForRestartCommand).ConfigureAwait(false);
                if (result.Success)
                {
                    // Start only after this server's stop handling (Invalidate) ran, so the new start gets a fresh prepare.
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    await commands.ExecuteCommandAsync(Resource, KnownResourceCommands.StartCommand).ConfigureAwait(false);
                }

                foreach (var resource in resources)
                {
                    await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogWarning(
                    "⚠️ Couldn't restart the hot reload watcher '{Server}': {Message}", Resource.Name, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _stoppedForRestart, null);
                _restartLock.Release();
            }
        }

        // The watcher names a file-based app by its virtual project path (app.cs.csproj); the model has the .cs file.
        private string[] ToEntryPointPaths(string[] paths) =>
            [.. paths.Select(path => path.EndsWith(".cs.csproj", StringComparison.OrdinalIgnoreCase) &&
                Projects.FirstOrDefault(p => PathsEqual(p.ProjectPath, path[..^".csproj".Length])) is { } project
                    ? project.ProjectPath
                    : path)];

        private readonly ConcurrentDictionary<string, byte> _fileBasedRebuilds = new(StringComparer.Ordinal);

        private void OnReplicaLogLine(string line)
        {
            if (TryParseLaunchedLine(line, out var processId, out var instance))
            {
                lock (_replicaGate)
                {
                    _instanceByProcessId[processId] = instance;
                    Record(_launches, new ReplicaEvent(instance, ExitCode: null));
                }
            }
            else if (TryParseExitedLine(line, out processId, out var exitCode))
            {
                lock (_replicaGate)
                {
                    if (_instanceByProcessId.Remove(processId, out var exitedInstance))
                    {
                        Record(_exits, new ReplicaEvent(exitedInstance, exitCode));
                    }
                }
            }

            static void Record(List<ReplicaEvent> events, ReplicaEvent e)
            {
                events.RemoveAll(x => x.Consumed || DateTime.UtcNow - x.At > TimeSpan.FromMinutes(1));
                events.Add(e);
            }
        }

        /// <summary>
        /// The watcher (rc.2, and SDK main at the time of writing) rebuilds a restarted file-based app through its virtual
        /// project path (<c>dotnet build ticker.cs.csproj</c>), which fails with MSB1009 and leaves the app stopped. A fresh
        /// watcher builds the .cs file itself as its initial build, so restart the app's (dedicated) watcher instead.
        /// </summary>
        private void RebuildFileBasedAppIfStuck(IServiceProvider services, string line)
        {
            if (!TryParseVirtualProjectBuildFailure(line, out var entryPoint))
            {
                return;
            }

            foreach (var project in Projects.Where(p => ((IProjectMetadata)p.Metadata).IsFileBasedApp && PathsEqual(p.ProjectPath, entryPoint)))
            {
                if (!_fileBasedRebuilds.TryAdd(project.Resource.Name, 0))
                {
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // File-based apps have a dedicated server, so this restarts only this app.
                        await RestartWithServicesAsync(services, "to rebuild the file-based app").ConfigureAwait(false);
                    }
                    finally
                    {
                        _fileBasedRebuilds.TryRemove(project.Resource.Name, out _);
                    }
                });
            }
        }

        /// <summary>
        /// Feeds the watcher's console output (build and compiler errors, changed files, apply times) to the
        /// interpreter; the status protocol doesn't carry those details.
        /// </summary>
        private async Task TailServerLogsAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            try
            {
                var loggers = services.GetRequiredService<ResourceLoggerService>();
                await foreach (var batch in loggers.WatchAsync(Resource).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    foreach (var line in batch)
                    {
                        OnReplicaLogLine(line.Content);
                        RebuildFileBasedAppIfStuck(services, line.Content);

                        IReadOnlyList<WatchNotice> notices;
                        lock (_noticeGate)
                        {
                            notices = _interpreter?.OnLogLine(line.Content) ?? [];
                            if (_interpreter?.FirstError is { } error && !_ready)
                            {
                                _lastBuildError = error;
                            }
                        }

                        EmitNotices(services, notices);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void EmitNotices(IServiceProvider services, IReadOnlyList<WatchNotice> notices)
        {
            lock (_noticeGate)
            {
                // A failure can wait for its first error line, which arrives through DCP's log stream after the status
                // event. Don't hold the notice back for long if the line never comes.
                if (_interpreter is { HasPending: true } interpreter && !_flushScheduled)
                {
                    _flushScheduled = true;
                    _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                    {
                        IReadOnlyList<WatchNotice> flushed;
                        lock (_noticeGate)
                        {
                            _flushScheduled = false;
                            flushed = ReferenceEquals(_interpreter, interpreter) ? interpreter.FlushPending() : [];
                        }

                        EmitNotices(services, flushed);
                    }, TaskScheduler.Default);
                }
            }

            if (notices.Count == 0)
            {
                return;
            }

            var noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
            var loggers = services.GetRequiredService<ResourceLoggerService>();
            var projects = Projects;
            foreach (var notice in notices)
            {
                if (notice.Kind == WatchNoticeKind.ProjectFileChanged)
                {
                    ScheduleReferenceRebuild(services, projects.Where(p => notice.ProjectPaths.Any(path => PathsEqual(p.ProjectPath, path))));
                }

                // The Rebuild command replaces the watcher's failed rebuild of a restarted file-based app; see RebuildFileBasedAppIfStuck.
                if (notice.Kind == WatchNoticeKind.BuildFailed && notice.ProjectPaths.Count > 0 &&
                    notice.ProjectPaths.All(path => projects.Any(p => _fileBasedRebuilds.ContainsKey(p.Resource.Name) && PathsEqual(p.ProjectPath, path))))
                {
                    continue;
                }

#pragma warning disable CA2254 // The notice text is the message; it has no structured parameters.
                noticeLogger.Log(notice.Level, NoticeEventId(notice.Kind), notice.Message);
#pragma warning restore CA2254

                // Before the first successful build every service is waiting on the server, so tell all of them why.
                var targets = notice.Kind == WatchNoticeKind.BuildFailed && !_ready
                    ? projects
                    : projects.Where(p => notice.ProjectPaths.Contains(p.ProjectPath, StringComparer.Ordinal)).ToList();
                foreach (var target in targets)
                {
                    loggers.GetLogger(target.Resource).Log(notice.Level, "[watch] {Message}", notice.Message);
                }
            }
        }

        private static async Task PublishWatchEventAsync(ResourceNotificationService notifications, DotnetProjectResource resource, string? resourceId, WatchStatus status)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Func<CustomResourceSnapshot, CustomResourceSnapshot>? update = status.type switch
            {
                DotnetWatchStatusTypes.HotReloadApplied or DotnetWatchStatusTypes.Restarting => s => s with
                {
                    Properties = WithProperty(s.Properties, "dotnet-watch.last-event", $"{status.type} @ {stamp}")
                },

                // The resource pipe carries no exit status and the watcher keeps the proxy connected so it can
                // relaunch the app on the next change. Surface the app's exit code on the replica instead.
                DotnetWatchStatusTypes.ProcessExited => s => s with
                {
                    State = new ResourceStateSnapshot(CrashedState, KnownResourceStateStyles.Error),
                    ExitCode = status.exitCode,
                    Properties = WithProperty(s.Properties, "dotnet-watch.last-event", $"process_exited({status.exitCode}) @ {stamp}")
                },

                DotnetWatchStatusTypes.ProcessStarted => s => s.State?.Text == CrashedState
                    ? s with { State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success), ExitCode = null }
                    : s,

                _ => null
            };

            if (update is null)
            {
                return;
            }

            if (resourceId is null)
            {
                await notifications.PublishUpdateAsync(resource, update).ConfigureAwait(false);
            }
            else
            {
                await notifications.PublishUpdateAsync(resource, resourceId, update).ConfigureAwait(false);
            }
        }

        private static ImmutableArray<ResourcePropertySnapshot> WithProperty(ImmutableArray<ResourcePropertySnapshot> properties, string name, string value) =>
            [.. properties.Where(p => p.Name != name), new ResourcePropertySnapshot(name, value)];
    }

    private sealed record ReplicaEvent(string Instance, int? ExitCode)
    {
        public DateTime At { get; } = DateTime.UtcNow;
        public bool Consumed { get; set; }
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
    private sealed record PreparedServer(string Muxer, string WatchHost, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> WatcherOnlyNames);

#pragma warning disable IDE1006 // Naming: matches the watcher's JSON schema.
    private sealed record WatchStatus(string type, string[]? projects, bool? success, string? error, int? exitCode);
#pragma warning restore IDE1006
}
