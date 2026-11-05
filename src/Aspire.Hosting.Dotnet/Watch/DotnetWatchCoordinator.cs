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

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, WatchState> s_states = new();
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, object> s_warned = new();

    /// <summary>Whether watch was requested for this run (<c>AppHost:Run:WatchEnabled</c> in run mode).</summary>
    internal static bool IsWatchRun(IDistributedApplicationBuilder builder) =>
        builder.ExecutionContext.IsRunMode && builder.ExecutionContext.RunConfiguration.WatchEnabled;

    /// <summary>
    /// Whether <c>AddDotnetProject</c> should route this run through the watch server. Watch requested without the
    /// CLI proxy (plain <c>dotnet run</c> of the AppHost) or under an IDE debug session falls back to a normal run
    /// with a warning instead of failing.
    /// </summary>
    internal static bool ShouldWatch(IDistributedApplicationBuilder builder)
    {
        if (!IsWatchRun(builder))
        {
            return false;
        }

        // D8: IDE-owned runs keep IDE-native change tracking.
        if (!string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_PORT"]))
        {
            WarnOnce(builder, "Service watch is disabled because the AppHost is running under an IDE debug session; the IDE owns change tracking.");
            return false;
        }

        if (string.IsNullOrEmpty(builder.Configuration[ProxyPathConfigurationKey]))
        {
            // TODO(spike): Session 7 owns the UX.
            WarnOnce(builder, $"Service watch was requested (AppHost:Run:WatchEnabled=true), but the AppHost was not started by 'aspire run --watch' ({ProxyPathConfigurationKey} is not set). Services run without watch.");
            return false;
        }

        return true;
    }

    /// <summary>Per-resource variant of <see cref="ShouldWatch(IDistributedApplicationBuilder)"/>.</summary>
    internal static bool ShouldWatch(IDistributedApplicationBuilder builder, string resourceName, IProjectMetadata metadata)
    {
        if (!ShouldWatch(builder))
        {
            return false;
        }

        if (metadata.IsFileBasedApp)
        {
            // TODO(spike): the watch server (11.0.100-rc.2) launches `dotnet run` from the file's directory without
            // `--file`, so a file-based app exits with "Couldn't find a project to run". Run it normally until it can.
            builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
            {
                e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogWarning(
                    "'{Resource}' is a file-based C# app ({File}); hot reload doesn't support those yet, so it runs without watch. Restart it to pick up changes.",
                    resourceName, Path.GetFileName(metadata.ProjectPath));
                return Task.CompletedTask;
            });
            return false;
        }

        return true;
    }

    private static void WarnOnce(IDistributedApplicationBuilder builder, string message)
    {
        if (s_warned.TryGetValue(builder, out _))
        {
            return;
        }

        s_warned.Add(builder, new object());
        builder.Eventing.Subscribe<BeforeStartEvent>((e, _) =>
        {
            e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory).LogWarning("{Message}", message);
            return Task.CompletedTask;
        });
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
        }, showInCommandLine: true);

        // The watcher's own initial build is the build barrier in watch runs (Aspire's coordinated build is skipped).
        resource.WaitFor(state.Server);

        // Aspire's Rebuild command stays available: it stops the service, runs `dotnet build` and starts it again, and
        // the watcher picks the fresh output back up. That's the escape hatch for edits the watcher can't apply on its
        // own, such as a new PackageReference (the watcher reloads the project but doesn't rebuild or restart it).

        state.Add(resource.Resource, metadata, resource.Resource.Annotations.Where(a => !existing.Contains(a)).ToArray());
    }

    /// <summary>
    /// Whether something other than the watch coordinator owns this resource's launch (for example, the Blazor
    /// gateway runs the Gateway CLI tool). Such resources can't be launched through the watch proxy.
    /// </summary>
    internal static bool HasCustomLaunch(IResource resource, IReadOnlyCollection<IResourceAnnotation> watchAnnotations)
    {
        // HACK(spike): the launch-tool and forced-process annotations are internal to Aspire.Hosting.
        static bool IsLaunchTool(IResourceAnnotation a) => a.GetType().Name == "LaunchToolArgsCallbackAnnotation";

        if (resource.Annotations.Any(a => a is ProjectLaunchArgsOverrideAnnotation || a.GetType().Name == "ForceProcessExecutionAnnotation"))
        {
            return true;
        }

        // The last launch tool wins: the SDK-selected `dotnet run` comes before the watch proxy; anything after it
        // replaced the proxy.
        var annotations = resource.Annotations.ToList();
        var watchIndex = annotations.FindLastIndex(a => IsLaunchTool(a) && watchAnnotations.Contains(a));
        return watchIndex >= 0 && annotations.Skip(watchIndex + 1).Any(IsLaunchTool);
    }

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
                .ExcludeFromManifest()
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
                var noticeLogger = e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(NoticeLoggerCategory);

                // Resources removed or replaced after registration must not be built or watched.
                _projects.RemoveAll(p => !e.Model.Resources.Contains(p.Resource));

                foreach (var project in _projects.ToArray())
                {
                    var added = _watchAnnotations[project.Resource];
                    if (HasCustomLaunch(project.Resource, added))
                    {
                        // TODO(spike): watch custom launch tools (Blazor gateway) once the watcher can launch them.
                        foreach (var annotation in added)
                        {
                            project.Resource.Annotations.Remove(annotation);
                        }

                        _projects.Remove(project);
                        noticeLogger.LogWarning(
                            "'{Resource}' uses a custom launch tool (for example, the Blazor gateway) and isn't watched; restart it to pick up changes.",
                            project.Resource.Name);
                    }
                }

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
                        _interpreter = new DotnetWatchEventInterpreter(_projects.Select(p => (((IProjectMetadata)p.Metadata).ProjectPath, p.Resource.Name)));
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
            Directory.CreateDirectory(_runDirectory);

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

                    if (status.type == "build_complete")
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
                noticeLogger.Log(notice.Level, notice.Message);
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
                case "hot_reload_applied" or "restarting":
                    await notifications.PublishUpdateAsync(resource, s => s with
                    {
                        Properties = WithProperty(s.Properties, "dotnet-watch.last-event", $"{status.type} @ {stamp}")
                    }).ConfigureAwait(false);
                    break;

                // The resource pipe carries no exit status and the watcher keeps the proxy connected so it can
                // relaunch the app on the next change. Surface the app's exit code on the resource instead.
                case "process_exited":
                    await notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = new ResourceStateSnapshot(CrashedState, KnownResourceStateStyles.Error),
                        ExitCode = status.exitCode,
                        Properties = WithProperty(s.Properties, "dotnet-watch.last-event", $"process_exited({status.exitCode}) @ {stamp}")
                    }).ConfigureAwait(false);
                    break;

                case "process_started":
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
