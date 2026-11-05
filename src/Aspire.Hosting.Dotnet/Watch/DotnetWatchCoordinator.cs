// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // RunConfiguration is experimental.
#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental.
#pragma warning disable ASPIREPROJECTS001 // DotnetProgramBuildCompletionAnnotation is experimental.

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
/// resource is registered with one per-AppHost <see cref="DotnetWatchServerResource"/>. The server waits for the
/// existing coordinated initial build (D7), acquires the pinned watcher, then runs its <c>server</c> command with all
/// watched roots. Each service instance is launched through the hidden <c>aspire __dotnet-watch-proxy</c> command,
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

    /// <summary>Process-environment variable naming the variables the proxy forwards to the service.</summary>
    internal const string ForwardedEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_FORWARD_ENV";

    internal const string ProxyCommandName = "__dotnet-watch-proxy";

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, WatchState> s_states = new();

    /// <summary>Whether <c>AddDotnetProject</c> should route this run through the watch server.</summary>
    internal static bool IsWatchRun(IDistributedApplicationBuilder builder) =>
        builder.ExecutionContext.IsRunMode && builder.ExecutionContext.RunConfiguration.WatchEnabled;

    internal static void Configure(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<DotnetProjectResource> resource,
        DotnetProjectMetadata metadata)
    {
        // D8: IDE-owned runs keep IDE-native change tracking. TODO(spike): surface this as a diagnostic in the dashboard.
        if (!string.IsNullOrEmpty(builder.Configuration["DEBUG_SESSION_PORT"]))
        {
            return;
        }

        var state = s_states.GetValue(builder, static b => new WatchState(b));
        state.Add(resource.Resource, metadata);

        // Replace the SDK-selected launch tool (dotnet run / coordinated output) with the proxy. Program arguments
        // contributed through WithArgs (launch-profile and user args) stay in the ordinary segment after `--`.
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

        resource.WaitFor(state.Server);
    }

    private sealed class WatchState
    {
        private readonly List<(DotnetProjectResource Resource, DotnetProjectMetadata Metadata)> _projects = [];
        private readonly IDistributedApplicationBuilder _builder;
        private Task<PreparedServer>? _prepareTask;
        private volatile bool _ready;
        private readonly string? _runDirectory;

        public WatchState(IDistributedApplicationBuilder builder)
        {
            _builder = builder;
            var id = Guid.NewGuid().ToString("N")[..12];
            if (OperatingSystem.IsWindows())
            {
                ResourcePipeName = $"aspire-watch-{id}";
                StatusPipeName = $"aspire-watch-status-{id}";
            }
            else
            {
                // On Unix .NET named pipes are domain sockets under $TMPDIR unless the name is rooted. Use rooted,
                // short paths so the watcher's TMPDIR can be redirected (see PrepareCoreAsync) without moving them.
                _runDirectory = Path.Combine(Path.GetTempPath(), $"aspw-{id}");
                ResourcePipeName = Path.Combine(_runDirectory, "res.sock");
                StatusPipeName = Path.Combine(_runDirectory, "status.sock");
            }

            var proxyPath = builder.Configuration[ProxyPathConfigurationKey];
            if (string.IsNullOrEmpty(proxyPath))
            {
                // TODO(spike): Session 7 owns the UX. Plain `dotnet run` with AppHost:Run:WatchEnabled lands here.
                throw new DistributedApplicationException(
                    $"Service watch was requested (AppHost:Run:WatchEnabled=true) but no proxy CLI was provided. " +
                    $"Run the AppHost with 'aspire run --watch', or set {ProxyPathConfigurationKey} to the aspire CLI.");
            }

            if (proxyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                ProxyCommand = "dotnet";
                ProxyPrefixArgs = [proxyPath];
            }
            else
            {
                ProxyCommand = proxyPath;
                ProxyPrefixArgs = [];
            }

            var healthCheckKey = $"{ServerResourceName}_initial_build";
            builder.Services.AddHealthChecks().AddCheck(healthCheckKey, () => _ready
                ? HealthCheckResult.Healthy("Watcher finished its initial build.")
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
                .WithEnvironment(async ctx =>
                {
                    var prepared = await PrepareAsync(ctx.ExecutionContext.Services, ctx.CancellationToken).ConfigureAwait(false);
                    foreach (var (key, value) in prepared.Environment)
                    {
                        ctx.EnvironmentVariables[key] = value;
                    }
                });

            // The forwarded-name list must be computed after every user/integration environment callback, so it is
            // appended once the model is final.
            builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
            {
                foreach (var (resource, _) in _projects)
                {
                    resource.Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
                    {
                        ctx.EnvironmentVariables[ForwardedEnvironmentNamesVariable] =
                            string.Join(';', ctx.EnvironmentVariables.Keys.Where(k => k != ForwardedEnvironmentNamesVariable));
                    }));
                }

                return Task.CompletedTask;
            });
        }

        public IResourceBuilder<DotnetWatchServerResource> Server { get; }
        public string ResourcePipeName { get; }
        public string StatusPipeName { get; }
        public string ProxyCommand { get; }
        public string[] ProxyPrefixArgs { get; }

        public void Add(DotnetProjectResource resource, DotnetProjectMetadata metadata) => _projects.Add((resource, metadata));

        private Task<PreparedServer> PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            // TODO(spike): a restart of the server resource reuses this result and the status listener.
            lock (_projects)
            {
                return _prepareTask ??= PrepareCoreAsync(services, cancellationToken);
            }
        }

        private async Task<PreparedServer> PrepareCoreAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(Server.Resource);

            // D7: keep Aspire's coordinated initial-build barrier; the watcher then performs its own (duplicate) build.
            foreach (var (resource, _) in _projects)
            {
                foreach (var completion in resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>())
                {
                    await completion.Callback(services, cancellationToken).ConfigureAwait(false);
                }
            }

            var toolDll = await DotnetWatchToolResolver.EnsureToolAsync(
                _builder.Configuration[DotnetWatchToolResolver.ToolPathConfigurationKey], logger, cancellationToken).ConfigureAwait(false);

            // TODO(spike): one SDK per server. Projects that select different SDKs (different global.json) should be
            // rejected or split into multiple servers; the spike uses the first project's SDK.
            var roots = _projects.Select(p => ((IProjectMetadata)p.Metadata).ProjectPath).Distinct(StringComparer.Ordinal).ToArray();
            var sdk = await DotnetWatchToolResolver.ResolveSdkAsync(Path.GetDirectoryName(roots[0])!, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Watching {Count} root(s) with SDK {SdkDirectory}: {Roots}", roots.Length, sdk.SdkDirectory, string.Join(", ", roots));

            var args = new List<string> { "exec" };
            var environment = new Dictionary<string, string>();

            // HACK(spike): with more than one root, watcher 10.0.403 builds a temporary .slnx created via
            // Path.GetTempFileName() and runs `dotnet build` with the temp directory as working directory, so the
            // muxer selects the SDK from the temp dir's global.json scope rather than --sdk, which fails with
            // "Build FAILED, 0 errors" when they differ. Redirect the watcher's TMPDIR to a per-run directory with a
            // global.json that pins the resolved SDK. TODO: Windows; upstream fix in the watcher.
            if (_runDirectory is not null)
            {
                // TODO(spike): delete on shutdown.
                Directory.CreateDirectory(_runDirectory);
            }

            if (_runDirectory is not null && roots.Length > 1)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(_runDirectory, "global.json"),
                    $$"""{ "sdk": { "version": "{{Path.GetFileName(sdk.SdkDirectory)}}", "rollForward": "disable", "allowPrerelease": true } }""",
                    cancellationToken).ConfigureAwait(false);
                environment["TMPDIR"] = _runDirectory;
            }

            // HACK(spike): the pinned tool targets net10.0 and loads MSBuild from --sdk. An SDK 11 (e.g. this repo's
            // pinned 11.0 RC) cannot be loaded on the 10.0 runtime, so roll the tool forward to the SDK's runtime.
            // This is NOT a supported configuration of the pinned tool; see plan section 3.1.
            if (sdk.SdkVersion.Major > 10)
            {
                logger.LogWarning("SDK {Version} is newer than the watcher's net10.0 runtime; rolling the watcher forward (unsupported).", sdk.SdkVersion);
                args.Add("--roll-forward");
                args.Add("LatestMajor");
                if (sdk.IsPrerelease)
                {
                    environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
                }
            }

            args.AddRange([toolDll, "server", "--sdk", sdk.SdkDirectory, "--server", ResourcePipeName, "--status-pipe", StatusPipeName]);
            foreach (var root in roots)
            {
                args.Add("--resource");
                args.Add(root);
            }

            // The status pipe listener must exist before the watcher connects to it.
            _ = Task.Run(() => MonitorStatusAsync(services, logger), CancellationToken.None);

            return new PreparedServer(sdk.Muxer, args, environment);
        }

        private async Task MonitorStatusAsync(IServiceProvider services, ILogger serverLogger)
        {
            var loggers = services.GetRequiredService<ResourceLoggerService>();
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var lifetime = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();

            try
            {
                var pipe = new NamedPipeServerStream(
                    StatusPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using var pipeScope = pipe.ConfigureAwait(false);
                await pipe.WaitForConnectionAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(lifetime.ApplicationStopping).ConfigureAwait(false) is { } line)
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

                    serverLogger.LogInformation("[status] {Line}", line);

                    if (status.type == "build_complete")
                    {
                        if (status.success == true)
                        {
                            _ready = true;
                        }
                        else
                        {
                            serverLogger.LogError("Watcher build failed for {Projects}. Fix the error and save to retry.", string.Join(", ", status.projects ?? []));
                        }
                    }

                    // Status events identify project paths only (plan 3.3), so fan out to every resource with that path.
                    foreach (var path in status.projects ?? [])
                    {
                        foreach (var (resource, metadata) in _projects.Where(p => string.Equals(((IProjectMetadata)p.Metadata).ProjectPath, path, StringComparison.Ordinal)))
                        {
                            var message = status.type switch
                            {
                                "hot_reload_applied" => "🔥 Hot reload applied",
                                "restarting" => "🔄 Restarting (rude edit or restart request)",
                                "building" => "⌚ Watcher building",
                                "build_complete" => status.success == true ? "✅ Watcher build succeeded" : "❌ Watcher build failed",
                                "process_started" => "▶️ Process started by watcher",
                                "process_exited" => $"⏹️ Process exited with code {status.exitCode}",
                                _ => status.type
                            };
                            loggers.GetLogger(resource).LogInformation("[dotnet-watch] {Message}", message);

                            if (status.type is "hot_reload_applied" or "restarting")
                            {
                                var stamp = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                                await notifications.PublishUpdateAsync(resource, s => s with
                                {
                                    Properties = [
                                        .. s.Properties.Where(p => p.Name != "dotnet-watch.last-event"),
                                        new ResourcePropertySnapshot("dotnet-watch.last-event", $"{status.type} @ {stamp}")]
                                }).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                serverLogger.LogError(ex, "Watch status monitor failed.");
            }
        }
    }

    private sealed record PreparedServer(string Muxer, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Environment);

#pragma warning disable IDE1006 // Naming: matches the watcher's JSON schema.
    private sealed record WatchStatus(string type, string[]? projects, bool? success, string? error, int? exitCode);
#pragma warning restore IDE1006
}
