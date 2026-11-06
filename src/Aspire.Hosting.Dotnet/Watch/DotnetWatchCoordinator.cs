// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // RunConfiguration is experimental.
#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental.
#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchArgsOverrideAnnotation is experimental.

using System.Collections.Immutable;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// The hidden, infrastructure-only watch server that hosts <c>Microsoft.DotNet.HotReload.Watch.Aspire server</c>
/// for every watched .NET project in one AppHost run.
/// </summary>
internal sealed class DotnetWatchServerResource(string name, string workingDirectory)
    : ExecutableResource(name, "dotnet", workingDirectory);

/// <summary>
/// SPIKE: Aspire-controlled C# service watch (plan Sessions 6/6a hosting side).
/// </summary>
/// <remarks>
/// <para>
/// When <c>ExecutionContext.RunConfiguration.WatchEnabled</c> is set in run mode, every <c>AddDotnetProject</c>
/// resource is registered with one per-AppHost <see cref="DotnetWatchServerResource"/>. The server acquires the
/// watcher matching the resolved SDK, then runs its <c>server</c> command with all watched roots. SPIKE deviation from
/// D7: Aspire's coordinated initial build is skipped in watch runs; the watcher's own initial build is the only build,
/// and the server becomes healthy once it succeeds (a failed build waits for a fix instead of failing the run). Each service instance is launched through the hidden <c>aspire __dotnet-watch-proxy</c> command,
/// which reads the service environment from its own process environment (never <c>-e</c> arguments) and sends it
/// over the watcher's resource pipe.
/// </para>
/// <para>
/// Known spike shortcuts are marked <c>TODO(spike)</c>/<c>HACK(spike)</c>.
/// </para>
/// </remarks>
internal static class DotnetWatchCoordinator
{
    internal const string ServerResourceName = "dotnet-watch";

    /// <summary>Path of the CLI that implements the proxy (an executable or a framework-dependent <c>.dll</c>).</summary>
    internal const string ProxyPathConfigurationKey = "ASPIRE_DOTNET_WATCH_PROXY_PATH";

    /// <summary>Muxer that hosts a framework-dependent proxy <c>.dll</c>; defaults to <c>dotnet</c> on PATH.</summary>
    internal const string ProxyHostConfigurationKey = "ASPIRE_DOTNET_WATCH_PROXY_HOST";

    /// <summary>Process-environment variable naming the variables the proxy forwards to the service.</summary>
    internal const string ForwardedEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_FORWARD_ENV";

    internal const string ProxyCommandName = "__dotnet-watch-proxy";

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

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, WatchState> s_states = new();
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, object> s_warned = new();

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
        var existing = resource.Resource.Annotations.ToHashSet();

        // Replace the SDK-selected launch tool (dotnet run) with the proxy. Program arguments contributed through
        // WithArgs (launch-profile and user args) stay in the ordinary segment after `--`.
        resource.WithLaunchToolArgs(ctx =>
        {
            var executable = ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
            executable.Command = state.ProxyCommand;
            foreach (var prefix in state.ProxyPrefixArgs)
            {
                ctx.Args.Add(prefix);
            }

            ctx.Args.Add(ProxyCommandName);
            ctx.Args.Add("--server");
            ctx.Args.Add(state.ResourcePipeName);
            ctx.Args.Add("--entrypoint");
            ctx.Args.Add(((IProjectMetadata)metadata).ProjectPath);
            ctx.Args.Add("--");
        },
        // Owning the proxy with the project's launch configuration type lets HasCustomLaunch tell, through the public
        // HasLaunchToolArgsOwnedBy, whether a later launch tool replaced it. It also matches the SDK-selected tool: an
        // IDE project launch performs the invocation itself (watch never runs under an IDE).
        ownedByLaunchConfigurationType: GetProjectLaunchConfigurationType(resource.Resource),
        showInCommandLine: true);

        // The watcher's own initial build is the build barrier in watch runs (Aspire's coordinated build is skipped).
        resource.WaitFor(state.Server);

        // Aspire's Rebuild command stays available: it stops the service, runs `dotnet build` and starts it again, and
        // the watcher picks the fresh output back up. That's the escape hatch for edits the watcher can't apply on its
        // own, such as a new PackageReference (the watcher reloads the project but doesn't rebuild or restart it).

        state.Add(resource.Resource, metadata, resource.Resource.Annotations.Where(a => !existing.Contains(a)).ToArray());
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
    private static void ApplyEligibility(
        List<(DotnetProjectResource Resource, DotnetProjectMetadata Metadata)> projects,
        Dictionary<DotnetProjectResource, IResourceAnnotation[]> watchAnnotations,
        IResource server,
        DistributedApplicationModel model,
        IServiceProvider services)
    {
        var errors = new List<string>();
        var excluded = new List<(DotnetProjectResource Resource, string Reason)>();
        foreach (var (resource, metadata) in projects)
        {
            var decision = Evaluate(resource, metadata, model.Resources);
            if (decision.Kind == WatchDecisionKind.Rejected)
            {
                errors.Add(decision.Reason!);
            }
            else if (decision.Kind == WatchDecisionKind.Excluded)
            {
                excluded.Add((resource, decision.Reason!));
            }
        }

        if (errors.Count > 0)
        {
            throw new DistributedApplicationException(
                "'aspire run --watch' can't hot reload these .NET projects:" + Environment.NewLine +
                string.Concat(errors.Select(e => $"  - {e}{Environment.NewLine}")) +
                "Call WithWatch(false) on a project to run it without hot reload.");
        }

        projects.RemoveAll(p => excluded.Any(x => ReferenceEquals(x.Resource, p.Resource)));
        var keepServerWait = projects.Count > 0;
        var noticeLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);
        var loggers = services.GetRequiredService<ResourceLoggerService>();
        foreach (var (resource, reason) in excluded)
        {
            foreach (var annotation in watchAnnotations[resource])
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

    private sealed class WatchState
    {
        private readonly List<(DotnetProjectResource Resource, DotnetProjectMetadata Metadata)> _projects = [];
        private readonly Dictionary<DotnetProjectResource, IResourceAnnotation[]> _watchAnnotations = [];
        private readonly IDistributedApplicationBuilder _builder;
        private readonly string _runDirectory;
        private readonly string _id;
        private readonly object _gate = new();
        private Task<PreparedServer>? _prepareTask;
        private CancellationTokenSource? _monitorCts;
        private int _generation;
        private volatile bool _ready;
        private volatile string? _lastBuildError;
        private readonly object _noticeGate = new();
        private DotnetWatchEventInterpreter? _interpreter;
        private bool _flushScheduled;

        public WatchState(IDistributedApplicationBuilder builder)
        {
            _builder = builder;
            _id = Guid.NewGuid().ToString("N")[..12];

            // Holds the Unix sockets and, for multi-root runs, the watcher's TMPDIR. Deleted when the AppHost stops.
            // Kept short: Unix socket paths are limited to ~108 characters.
            _runDirectory = Path.Combine(Path.GetTempPath(), $"aspw-{_id}");
            ResourcePipeName = OperatingSystem.IsWindows() ? $"aspire-watch-{_id}" : Path.Combine(_runDirectory, "res.sock");

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

            var healthCheckKey = $"{ServerResourceName}_initial_build";
            builder.Services.AddHealthChecks().AddCheck(healthCheckKey, () =>
                _ready ? HealthCheckResult.Healthy("Watcher finished its initial build.")
                : _lastBuildError is { } error ? HealthCheckResult.Unhealthy($"Initial build failed: {error}. Fix the code and save; the watcher rebuilds automatically.")
                : HealthCheckResult.Unhealthy("Waiting for the watcher's initial build."));

            var server = new DotnetWatchServerResource(ServerResourceName, builder.AppHostDirectory);
            Server = builder.AddResource(server)
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
                .WithEnvironment(ctx =>
                {
                    // HACK(spike): with more than one root, the watcher builds a temporary .slnx created via
                    // Path.GetTempFileName() and runs `dotnet build` with the temp directory as working directory, so
                    // the muxer selects the SDK from the temp dir's global.json scope rather than --sdk ("Build FAILED,
                    // 0 errors" when they differ). Redirect the watcher's TMPDIR to the run directory, which gets a
                    // global.json pinning the resolved SDK (see PrepareCoreAsync). TODO: Windows; upstream fix.
                    if (!OperatingSystem.IsWindows() && Roots.Length > 1)
                    {
                        ctx.EnvironmentVariables["TMPDIR"] = _runDirectory;
                    }
                });

            // Each (re)start of the server gets a fresh prepare (new status pipe + listener, re-resolved SDK).
            builder.Eventing.Subscribe<ResourceStoppedEvent>(Server.Resource, (_, _) =>
            {
                Invalidate();
                return Task.CompletedTask;
            });

            builder.Eventing.Subscribe<BeforeStartEvent>((e, ct) =>
            {
                // Resources removed or replaced after registration must not be built or watched.
                _projects.RemoveAll(p => !e.Model.Resources.Contains(p.Resource));

                // Opt-outs, run-to-completion and unsupported projects can only be decided on the final model.
                ApplyEligibility(_projects, _watchAnnotations, Server.Resource, e.Model, e.Services);

                if (_projects.Count == 0)
                {
                    e.Model.Resources.Remove(Server.Resource);
                    return Task.CompletedTask;
                }

                // The forwarded-name list must be computed after every user/integration environment callback, so it
                // is appended once the model is final.
                foreach (var (resource, _) in _projects)
                {
                    resource.Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
                    {
                        ctx.EnvironmentVariables[ForwardedEnvironmentNamesVariable] =
                            string.Join(';', ctx.EnvironmentVariables.Keys.Where(k => k != ForwardedEnvironmentNamesVariable));
                    }));
                }

                var lifetime = e.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
                lifetime.ApplicationStopped.Register(CleanupRunDirectory);
                _ = Task.Run(() => TailServerLogsAsync(e.Services, lifetime.ApplicationStopping), CancellationToken.None);
                return Task.CompletedTask;
            });
        }

        public IResourceBuilder<DotnetWatchServerResource> Server { get; }
        public string ResourcePipeName { get; }
        public string ProxyCommand { get; }
        public string[] ProxyPrefixArgs { get; }

        private string[] Roots => _projects.Select(p => ((IProjectMetadata)p.Metadata).ProjectPath).Distinct(StringComparer.Ordinal).ToArray();

        public void Add(DotnetProjectResource resource, DotnetProjectMetadata metadata, IResourceAnnotation[] watchAnnotations)
        {
            _projects.Add((resource, metadata));
            _watchAnnotations[resource] = watchAnnotations;
        }

        private Task<PreparedServer> PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_prepareTask is null)
                {
                    _monitorCts?.Cancel();
                    _monitorCts = new CancellationTokenSource();
                    _ready = false;
                    _lastBuildError = null;
                    lock (_noticeGate)
                    {
                        _interpreter = new DotnetWatchEventInterpreter(_projects.Select(p => (((IProjectMetadata)p.Metadata).ProjectPath, p.Resource.Name)), Server.Resource.WorkingDirectory);
                    }

                    _prepareTask = PrepareWithNoticeAsync(services, ++_generation, _monitorCts.Token, cancellationToken);
                }

                return _prepareTask;
            }
        }

        private void Invalidate()
        {
            lock (_gate)
            {
                _prepareTask = null;
                _monitorCts?.Cancel();
                _ready = false;
            }
        }

        private void CleanupRunDirectory()
        {
            Invalidate();
            try
            {
                if (Directory.Exists(_runDirectory))
                {
                    Directory.Delete(_runDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; the directory lives under the temp path.
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
                // Forget the failed attempt so restarting the dotnet-watch resource retries (e.g. once the network is back),
                // and say so in the terminal: otherwise the services just sit in "Waiting" with the reason buried in a log.
                lock (_gate)
                {
                    _prepareTask = null;
                }

                services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogError(
                    "❌ Hot reload couldn't start: {Message} Watched services wait until it does; after fixing the problem, start '{Server}' from the dashboard ('aspire resource {Server} start').",
                    ex.Message, ServerResourceName, ServerResourceName);
                throw;
            }
        }

        private async Task<PreparedServer> PrepareCoreAsync(IServiceProvider services, int generation, CancellationToken monitorToken, CancellationToken cancellationToken)
        {
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(Server.Resource);
            DotnetWatchServerInfrastructure.EnsurePrivateDirectory(_runDirectory);

            // TODO(spike): one SDK per server. Projects that select different SDKs (different global.json) should be
            // rejected or split into multiple servers; the spike uses the first project's SDK.
            var roots = Roots;
            var sdk = await DotnetWatchToolResolver.ResolveSdkAsync(Path.GetDirectoryName(roots[0])!, cancellationToken).ConfigureAwait(false);
            var toolVersion = DotnetWatchToolResolver.SelectToolVersion(sdk.SdkVersion, _builder.Configuration[DotnetWatchToolResolver.ToolVersionConfigurationKey]);
            var toolDll = await DotnetWatchToolResolver.EnsureToolAsync(
                _builder.Configuration[DotnetWatchToolResolver.ToolPathConfigurationKey], toolVersion, logger, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Watching {Count} root(s) with SDK {SdkDirectory}: {Roots}", roots.Length, sdk.SdkDirectory, string.Join(", ", roots));

            if (!OperatingSystem.IsWindows() && roots.Length > 1)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(_runDirectory, "global.json"),
                    $$"""{ "sdk": { "version": "{{sdk.SdkVersion}}", "rollForward": "disable", "allowPrerelease": true } }""",
                    cancellationToken).ConfigureAwait(false);
            }

            var statusPipeName = OperatingSystem.IsWindows()
                ? $"aspire-watch-status-{_id}-{generation}"
                : Path.Combine(_runDirectory, $"status{generation}.sock");

            var args = new List<string> { "exec", toolDll, "server", "--sdk", sdk.SdkDirectory, "--server", ResourcePipeName, "--status-pipe", statusPipeName };
            foreach (var root in roots)
            {
                args.Add("--resource");
                args.Add(root);
            }

            // The status pipe listener must exist before the watcher connects to it.
            _ = Task.Run(() => MonitorStatusAsync(services, logger, statusPipeName, monitorToken), CancellationToken.None);

            return new PreparedServer(sdk.Muxer, args);
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

                    // Status events identify project paths only (plan 3.3), so fan out to every resource with that path.
                    foreach (var path in projects)
                    {
                        foreach (var (resource, _) in _projects.Where(p => string.Equals(((IProjectMetadata)p.Metadata).ProjectPath, path, StringComparison.Ordinal)))
                        {
                            await PublishWatchEventAsync(notifications, resource, status).ConfigureAwait(false);
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

        /// <summary>
        /// Feeds the watcher's console output (build and compiler errors, changed files, apply times) to the
        /// interpreter; the status protocol doesn't carry those details.
        /// </summary>
        private async Task TailServerLogsAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            try
            {
                var loggers = services.GetRequiredService<ResourceLoggerService>();
                await foreach (var batch in loggers.WatchAsync(Server.Resource).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    foreach (var line in batch)
                    {
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
            foreach (var notice in notices)
            {
#pragma warning disable CA2254 // The notice text is the message; it has no structured parameters.
                noticeLogger.Log(notice.Level, NoticeEventId(notice.Kind), notice.Message);
#pragma warning restore CA2254

                // Before the first successful build every service is waiting on the server, so tell all of them why.
                var targets = notice.Kind == WatchNoticeKind.BuildFailed && !_ready
                    ? _projects
                    : _projects.Where(p => notice.ProjectPaths.Contains(((IProjectMetadata)p.Metadata).ProjectPath, StringComparer.Ordinal)).ToList();
                foreach (var (resource, _) in targets)
                {
                    loggers.GetLogger(resource).Log(notice.Level, "[watch] {Message}", notice.Message);
                }
            }
        }

        private static async Task PublishWatchEventAsync(ResourceNotificationService notifications, DotnetProjectResource resource, WatchStatus status)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            switch (status.type)
            {
                case DotnetWatchStatusTypes.HotReloadApplied or DotnetWatchStatusTypes.Restarting:
                    await notifications.PublishUpdateAsync(resource, s => s with
                    {
                        Properties = WithProperty(s.Properties, "dotnet-watch.last-event", $"{status.type} @ {stamp}")
                    }).ConfigureAwait(false);
                    break;

                // The resource pipe carries no exit status and the watcher keeps the proxy connected so it can
                // relaunch the app on the next change. Surface the app's exit code on the resource instead.
                case DotnetWatchStatusTypes.ProcessExited:
                    await notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot(CrashedState, KnownResourceStateStyles.Error),
                        ExitCode = status.exitCode,
                        Properties = WithProperty(s.Properties, "dotnet-watch.last-event", $"process_exited({status.exitCode}) @ {stamp}")
                    }).ConfigureAwait(false);
                    break;

                case DotnetWatchStatusTypes.ProcessStarted:
                    await notifications.PublishUpdateAsync(resource, s => s.State?.Text == CrashedState
                        ? s with { State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success), ExitCode = null }
                        : s).ConfigureAwait(false);
                    break;
            }
        }

        private static ImmutableArray<ResourcePropertySnapshot> WithProperty(ImmutableArray<ResourcePropertySnapshot> properties, string name, string value) =>
            [.. properties.Where(p => p.Name != name), new ResourcePropertySnapshot(name, value)];
    }

    private sealed record PreparedServer(string Muxer, IReadOnlyList<string> Args);

#pragma warning disable IDE1006 // Naming: matches the watcher's JSON schema.
    private sealed record WatchStatus(string type, string[]? projects, bool? success, string? error, int? exitCode);
#pragma warning restore IDE1006
}
