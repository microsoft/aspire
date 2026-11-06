// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // RunConfiguration is experimental.
#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental.
#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchArgsOverrideAnnotation is experimental.

using System.Collections.Immutable;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
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
/// the watcher matching its resolved SDK, then runs its <c>server</c> command with its roots, hosted by the hidden
/// <c>aspire __dotnet-watch-server</c> command so that stopping it is prompt. SPIKE deviation from D7: Aspire's
/// coordinated initial build is skipped in watch runs; the watcher's own initial build is the only build, and the
/// server becomes healthy once it succeeds (a failed build waits for a fix instead of failing the run). Each service
/// instance is launched through the hidden <c>aspire __dotnet-watch-proxy</c> command, which reads the service
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

    /// <summary>Path of the CLI that implements the proxy (an executable or a framework-dependent <c>.dll</c>).</summary>
    internal const string ProxyPathConfigurationKey = "ASPIRE_DOTNET_WATCH_PROXY_PATH";

    /// <summary>Muxer that hosts a framework-dependent proxy <c>.dll</c>; defaults to <c>dotnet</c> on PATH.</summary>
    internal const string ProxyHostConfigurationKey = "ASPIRE_DOTNET_WATCH_PROXY_HOST";

    /// <summary>Proxy/host protocol the CLI implements. Must match <c>DotnetWatchRunEnvironment</c> in the CLI.</summary>
    internal const string ProxyProtocolConfigurationKey = "ASPIRE_DOTNET_WATCH_PROXY_PROTOCOL";

    /// <summary>2: <c>__dotnet-watch-server</c> host and proxy <c>--working-directory</c>, instance marker and restore list.</summary>
    internal const int RequiredProxyProtocol = 2;

    /// <summary>Process-environment variable naming the variables the proxy forwards to the service.</summary>
    internal const string ForwardedEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_FORWARD_ENV";

    /// <summary>
    /// Process-environment variable naming variables set only for the watcher (temp directory, build configuration,
    /// build-only environment). The service inherits the watcher's environment, so the proxy resets each of these to
    /// its own value (or unsets it) for the service.
    /// </summary>
    internal const string RestoreEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_RESTORE_ENV";

    /// <summary>
    /// Process-environment variable with the replica's DCP resource name. The proxy passes it to <c>dotnet run</c> so
    /// the watcher's launch and exit log lines can be attributed to one replica.
    /// </summary>
    internal const string InstanceVariable = "ASPIRE_DOTNET_WATCH_INSTANCE";

    internal const string ProxyCommandName = "__dotnet-watch-proxy";
    internal const string ServerHostCommandName = "__dotnet-watch-server";

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
    /// service's log say so. Watch requested without the CLI proxy (a plain <c>dotnet run</c> of the AppHost with
    /// <c>AppHost:Run:WatchEnabled=true</c>) fails the run instead of silently running services without hot reload.
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

        if (string.IsNullOrEmpty(builder.Configuration[ProxyPathConfigurationKey]))
        {
            OnceForBuilder(builder, () => builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
                throw new DistributedApplicationException(
                    "Hot reload was requested (AppHost:Run:WatchEnabled=true), but this AppHost wasn't started by 'aspire run --watch', " +
                    $"so .NET services can't be watched ({ProxyPathConfigurationKey} is not set). Start it with 'aspire run --watch', " +
                    "or remove AppHost:Run:WatchEnabled to run without hot reload.")));
            return false;
        }

        if (!int.TryParse(builder.Configuration[ProxyProtocolConfigurationKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocol)
            || protocol < RequiredProxyProtocol)
        {
            OnceForBuilder(builder, () => builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
                throw new DistributedApplicationException(
                    "Hot reload was requested, but this Aspire CLI is too old for this version of Aspire.Hosting.Dotnet. " +
                    "Update the Aspire CLI ('aspire update --self'), or run without --watch.")));
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
        DotnetProjectMetadata metadata)
    {
        var state = s_states.GetValue(builder, static b => new WatchState(b));
        s_projectStates.AddOrUpdate(resource.Resource, state);
        state.Configure(resource, metadata);
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
    /// <item><description>File-based apps, persistent lifetimes and custom launches can't be watched and fail the run with
    /// an error that names the opt-out.</description></item>
    /// </list>
    /// </remarks>
    internal static WatchDecision Evaluate(DotnetProjectResource resource, IProjectMetadata metadata, IEnumerable<IResource> model)
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

        if (metadata.IsFileBasedApp)
        {
            // The watch server (11.0.100-rc.2) launches `dotnet run` from the file's directory without `--file`, so a
            // file-based app exits with "Couldn't find a project to run".
            return new(WatchDecisionKind.Rejected,
                $"'{resource.Name}' is a file-based C# app ({Path.GetFileName(metadata.ProjectPath)}), which hot reload doesn't support yet.");
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
    /// Applies <see cref="Evaluate"/> to every registered project at <see cref="BeforeStartEvent"/>: excluded projects
    /// get their normal launch back and a notice in the terminal and their log; unsupported ones fail the run together.
    /// </summary>
    /// <remarks>
    /// An excluded project keeps waiting for the watch server while other projects are watched, so its own
    /// <c>dotnet run</c> build doesn't race the watcher's initial build over shared project outputs.
    /// </remarks>
    private static void ApplyEligibility(List<WatchedProject> projects, DistributedApplicationModel model, IServiceProvider services)
    {
        var errors = new List<string>();
        var excluded = new List<(WatchedProject Project, string Reason)>();
        foreach (var project in projects)
        {
            var decision = Evaluate(project.Resource, project.Metadata, model.Resources);
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
        var noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
        var loggers = services.GetRequiredService<ResourceLoggerService>();
        foreach (var (project, reason) in excluded)
        {
            var resource = project.Resource;
            var server = project.Server.Resource;

            // The server stays in the model only while it still watches a project.
            var keepServerWait = projects.Any(p => p.Server == project.Server);
            foreach (var annotation in project.Annotations)
            {
                var waitsForServer = annotation is WaitAnnotation wait && ReferenceEquals(wait.Resource, server)
                    || annotation is ResourceRelationshipAnnotation relationship && ReferenceEquals(relationship.Resource, server);
                if (!(keepServerWait && waitsForServer))
                {
                    resource.Annotations.Remove(annotation);
                }
            }

            noticeLogger.LogInformation("{Notice}", reason);
            loggers.GetLogger(resource).LogInformation("[watch] {Notice}", reason);
        }
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

    [GeneratedRegex(InstanceVariable + @"=(?<instance>[A-Za-z0-9-]+).*process id (?<pid>\d+)")]
    private static partial Regex LaunchedLineRegex();

    [GeneratedRegex(@"Process id (?<pid>\d+) ran for \d+ ?ms and exited with exit code (?<code>-?\d+)")]
    private static partial Regex ExitedLineRegex();

    private sealed class WatchedProject(DotnetProjectResource resource, DotnetProjectMetadata metadata, WatchServer server)
    {
        public DotnetProjectResource Resource { get; } = resource;
        public DotnetProjectMetadata Metadata { get; } = metadata;
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

            var proxyPath = builder.Configuration[ProxyPathConfigurationKey]!;
            if (proxyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                ProxyCommand = builder.Configuration[ProxyHostConfigurationKey] is { Length: > 0 } host ? host : "dotnet";
                ProxyPrefixArgs = [proxyPath];
            }
            else
            {
                ProxyCommand = proxyPath;
                ProxyPrefixArgs = [];
            }

            builder.Eventing.Subscribe<BeforeStartEvent>((e, ct) =>
            {
                // Resources removed or replaced after registration must not be built or watched.
                Projects.RemoveAll(p => !e.Model.Resources.Contains(p.Resource));

                // Opt-outs, run-to-completion and unsupported projects can only be decided on the final model.
                ApplyEligibility(Projects, e.Model, e.Services);

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
        public string ProxyCommand { get; }
        public string[] ProxyPrefixArgs { get; }
        public List<WatchedProject> Projects { get; } = [];

        public void Configure(IResourceBuilder<DotnetProjectResource> resource, DotnetProjectMetadata metadata)
        {
            var existing = resource.Resource.Annotations.ToHashSet();
            var projectPath = ((IProjectMetadata)metadata).ProjectPath;
            var scope = FindSdkScope(Path.GetDirectoryName(projectPath)!);
            var server = _servers.FirstOrDefault(s => s.DedicatedTo is null && string.Equals(s.SdkScope, scope, PathComparison))
                ?? AddServer(scope, dedicatedTo: null, firstProjectName: resource.Resource.Name);
            var project = new WatchedProject(resource.Resource, metadata, server);

            // Replace the SDK-selected launch tool (dotnet run) with the proxy. Program arguments contributed through
            // WithArgs (launch-profile and user args) stay in the ordinary segment after `--`.
            resource.WithLaunchToolArgs(ctx =>
            {
                var executable = ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
                executable.Command = ProxyCommand;
                foreach (var prefix in ProxyPrefixArgs)
                {
                    ctx.Args.Add(prefix);
                }

                ctx.Args.Add(ProxyCommandName);
                ctx.Args.Add("--server");
                ctx.Args.Add(project.Server.ResourcePipeName);
                ctx.Args.Add("--entrypoint");
                ctx.Args.Add(projectPath);
                if (executable.WorkingDirectoryExplicitlySet)
                {
                    // The watcher launches `dotnet run` from the project directory; RunWorkingDirectory moves the app.
                    ctx.Args.Add("--working-directory");
                    ctx.Args.Add(executable.WorkingDirectory);
                }

                ctx.Args.Add("--");
            },
            // Owning the proxy with the project's launch configuration type lets HasCustomLaunch tell, through the
            // public HasLaunchToolArgsOwnedBy, whether a later launch tool replaced it. It also matches the
            // SDK-selected tool: an IDE project launch performs the invocation itself (watch never runs under an IDE).
            ownedByLaunchConfigurationType: GetProjectLaunchConfigurationType(resource.Resource),
            showInCommandLine: true);

            // The watcher's own initial build is the build barrier in watch runs (Aspire's coordinated build is skipped).
            resource.WaitFor(server.Builder);

            // Aspire's Rebuild command stays available: it stops the service, runs `dotnet build` and starts it again,
            // and the watcher picks the fresh output back up.
            project.Annotations.AddRange(resource.Resource.Annotations.Where(a => !existing.Contains(a)));
            Projects.Add(project);

            if (resource.Resource.Annotations.OfType<DotnetProjectBuildEnvironmentCallbackAnnotation>().Any())
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
                    ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command = state.ProxyCommand;
                    foreach (var arg in state.ProxyPrefixArgs.Concat(prepared.Args))
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

                    _prepareTask = PrepareWithNoticeAsync(services, ++_generation, _monitorCts.Token, cancellationToken);
                }

                return _prepareTask;
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

        private async Task<PreparedServer> PrepareWithNoticeAsync(IServiceProvider services, int generation, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            try
            {
                return await PrepareCoreAsync(services, generation, monitorToken, cancellationToken).ConfigureAwait(false);
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

        private async Task<PreparedServer> PrepareCoreAsync(IServiceProvider services, int generation, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(Resource);
            DotnetWatchServerInfrastructure.EnsurePrivateDirectory(_state.RunDirectory);

            var roots = Roots;
            var sdk = await DotnetWatchToolResolver.ResolveSdkAsync(Path.GetDirectoryName(roots[0])!, cancellationToken).ConfigureAwait(false);
            var configuration = _state.Builder.Configuration;
            var toolDll = DotnetWatchToolResolver.ResolveToolPath(
                configuration[DotnetWatchToolResolver.ToolPathConfigurationKey],
                DotnetWatchToolResolver.GetToolsDirectory(services.GetRequiredService<DistributedApplicationOptions>()),
                sdk.SdkVersion,
                logger);
            logger.LogInformation("Watching {Count} root(s) with SDK {SdkDirectory}: {Roots}", roots.Length, sdk.SdkDirectory, string.Join(", ", roots));

            var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            // The watcher has no per-project build arguments (it hardcodes none), so build settings travel in its
            // process environment, which MSBuild reads as properties for its builds, its in-process evaluation and
            // the `dotnet run --no-build` launches. Environment properties have the lowest precedence: a project
            // that sets the property unconditionally wins, unlike the global properties of the normal build.
            // Debug is MSBuild's default, so it's left unset: anything set here is restored to the proxy's own value
            // for the services, and a variable that was originally unset comes back empty rather than unset.
            if (Projects.Select(p => p.Metadata.BuildConfiguration).FirstOrDefault(c => !string.IsNullOrEmpty(c)) is { } buildConfiguration &&
                !string.Equals(buildConfiguration, "Debug", StringComparison.OrdinalIgnoreCase))
            {
                environment["Configuration"] = buildConfiguration;

                // The watcher only attaches its hot reload agent to unoptimized builds, and the agent is what
                // applies the service environment (endpoints, connection strings) to the launched app. Release
                // defaults Optimize only when it's empty, so this keeps Release's output paths and constants.
                environment["Optimize"] = "false";
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
                ServerHostCommandName, "--", sdk.Muxer, "exec", toolDll, "server",
                "--sdk", sdk.SdkDirectory, "--server", ResourcePipeName, "--status-pipe", statusPipeName, "--verbose"
            };
            foreach (var root in roots)
            {
                args.Add("--resource");
                args.Add(root);
            }

            // The status pipe listener must exist before the watcher connects to it.
            _ = Task.Run(() => MonitorStatusAsync(services, logger, statusPipeName, monitorToken), CancellationToken.None);

            // Everything set only for the watcher must not leak into the services, which inherit its environment.
            return new PreparedServer(args, environment, [.. environment.Keys]);
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

        private async Task MonitorStatusAsync(IServiceProvider services, ILogger serverLogger, string statusPipeName, CancellationToken monitorToken)
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
                    var projects = status.projects ?? [];

                    if (status.type == DotnetWatchStatusTypes.BuildComplete)
                    {
                        if (status.success == true)
                        {
                            _lastBuildError = null;
                            _ready = true;
                        }
                        else
                        {
                            lock (_noticeGate)
                            {
                                _lastBuildError = _interpreter?.FirstError ?? "the build failed";
                            }
                        }
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

    private sealed record PreparedServer(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> WatcherOnlyNames);

#pragma warning disable IDE1006 // Naming: matches the watcher's JSON schema.
    private sealed record WatchStatus(string type, string[]? projects, bool? success, string? error, int? exitCode);
#pragma warning restore IDE1006
}
