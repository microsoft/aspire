// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring interactive terminal support on resources.
/// </summary>
public static class TerminalResourceBuilderExtensions
{
    private const string TerminalExperimentalDiagnosticId = "ASPIRETERMINAL001";

    /// <summary>
    /// Configures a resource to expose an interactive terminal session.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">An optional callback to configure the terminal options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <remarks>
    /// <para>
    /// When a resource is configured with <c>.WithTerminal()</c>, DCP allocates a pseudo-terminal
    /// (PTY) per replica and a hidden terminal host process bridges the PTY traffic over Hex1b's
    /// HMP v1 protocol. The terminal session can be accessed from the Aspire Dashboard's terminal
    /// page or via the <c>aspire terminal</c> CLI command.
    /// </para>
    /// <para>
    /// One terminal host process is spawned per parent replica (e.g. <c>WithReplicas(3).WithTerminal()</c>
    /// → 3 terminal host processes named <c>{parent}-terminalhost-0</c> .. <c>{parent}-terminalhost-2</c>).
    /// The order of <c>WithReplicas(...)</c> and <c>WithTerminal()</c> does not matter: the per-replica
    /// hosts are materialized during <see cref="BeforeStartEvent"/> after the model is fully built,
    /// so the final replica count is always honoured.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add terminal support to an executable resource:
    /// <code>
    /// var agent = builder.AddExecutable("agent", "my-agent", ".")
    ///     .WithTerminal();
    /// </code>
    /// </example>
    /// <example>
    /// Add terminal support with custom dimensions to a multi-replica resource. The order of
    /// <c>WithReplicas</c> and <c>WithTerminal</c> does not matter:
    /// <code>
    /// var agent = builder.AddExecutable("agent", "my-agent", ".")
    ///     .WithReplicas(3)
    ///     .WithTerminal(options =>
    ///     {
    ///         options.Columns = 200;
    ///         options.Rows = 50;
    ///     });
    /// </code>
    /// </example>
    [Experimental(TerminalExperimentalDiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the parameterless withTerminal dispatcher export.")]
    public static IResourceBuilder<T> WithTerminal<T>(this IResourceBuilder<T> builder, Action<TerminalOptions>? configure = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.Annotations.OfType<TerminalAnnotation>().Any())
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' already has a terminal configured. Call WithTerminal() only once per resource.");
        }

        var options = new TerminalOptions();
        configure?.Invoke(options);

        // Annotation is added eagerly so consumers (DCP creators, dashboard data, backchannel)
        // can detect "this resource has a terminal" the moment WithTerminal() returns. The
        // per-replica hosts inside it are populated later, during BeforeStartEvent — the model
        // (including any subsequent WithReplicas calls) is fully built by then, so the final
        // replica count is always honoured even if WithTerminal() ran before WithReplicas().
        var annotation = new TerminalAnnotation(options);
        builder.WithAnnotation(annotation);

        // DCP cannot currently run a process under the debugger and a PTY at the same time.
        // Prefer a working terminal over IDE execution until both can be combined:
        // https://github.com/microsoft/dcp/issues/189
        builder.WithAnnotation(new ForceProcessExecutionAnnotation());

        var parent = builder.Resource;
        var appBuilder = builder.ApplicationBuilder;
        appBuilder.Services.TryAddSingleton<TerminalHostOrphanCleanupService>();
        appBuilder.Services.TryAddEventingSubscriber<TerminalHostOrphanCleanupEventingSubscriber>();

        // Subscribe directly on the IDistributedApplicationEventing rather than registering an
        // IDistributedApplicationEventingSubscriber: subscriptions registered during the builder
        // phase fire in registration order ahead of DI-registered subscribers (which only attach
        // their callbacks during DistributedApplication.RunApplicationAsync). That ordering is
        // important — TerminalHostEventingSubscriber resolves each host's binary path by
        // iterating model.Resources.OfType<TerminalHostResource>(), so the hosts MUST already
        // be in the model by the time it runs.
        appBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
            MaterializeTerminalHostsAsync(@event, parent, annotation, options, cancellationToken));

        return builder;
    }

    /// <summary>
    /// Polyglot dispatcher for <see cref="WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>.
    /// Exposed to non-C# AppHosts via ATS as <c>withTerminal</c> — they cannot pass a
    /// C# <see cref="Action{T}"/>, so this overload simply applies the defaults from
    /// <see cref="TerminalOptions"/> (120×30). Polyglot AppHosts that need to customise
    /// the terminal dimensions can wait for a future overload that accepts a DTO.
    /// </summary>
    /// <ats-summary>Adds an interactive terminal session to a resource using the default terminal options.</ats-summary>
    [AspireExport("withTerminal")]
    internal static IResourceBuilder<T> WithTerminalForPolyglot<T>(this IResourceBuilder<T> builder)
        where T : IResource
#pragma warning disable ASPIRETERMINAL001 // Internal dispatcher into the experimental API.
        => builder.WithTerminal();
#pragma warning restore ASPIRETERMINAL001

#pragma warning disable ASPIRETERMINAL001 // Internal implementation of the experimental terminal configuration API.

    /// <summary>
    /// Reads the parent's final <see cref="ReplicaAnnotation"/> and creates one
    /// <see cref="TerminalHostResource"/> per replica. Idempotent — re-firing
    /// <see cref="BeforeStartEvent"/> (e.g. from a test) is a no-op once the
    /// <paramref name="annotation"/> is initialized.
    /// </summary>
    private static async Task MaterializeTerminalHostsAsync(
        BeforeStartEvent @event,
        IResource parent,
        TerminalAnnotation annotation,
        TerminalOptions options,
        CancellationToken cancellationToken)
    {
        if (annotation.IsInitialized)
        {
            return;
        }

        var executionContext = @event.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        if (!executionContext.IsRunMode)
        {
            return;
        }

        // ReplicaAnnotation may have been added before OR after WithTerminal — that's
        // exactly why this code runs at BeforeStartEvent time. The model is locked down
        // by now, so LastOrDefault() reflects the final WithReplicas(N) call.
        var replicaCount = parent.Annotations.OfType<ReplicaAnnotation>().LastOrDefault()?.Replicas ?? 1;
        if (replicaCount < 1)
        {
            replicaCount = 1;
        }

        // All per-replica terminal-host files live flat under ~/.aspire/trmnl/, with
        // a random per-run replica id. This:
        //  - matches the repo's convention for per-user runtime state (cf. ~/.aspire/cli/bch,
        //    ~/.aspire/dev-certs, ~/.aspire/deployments)
        //  - avoids dropping UDS sockets in the global /tmp on Linux where different distros
        //    treat /tmp permissions differently
        //  - keeps absolute paths short enough to fit sun_path (104 bytes on macOS)
        //  - lets external tools enumerate by listing {trmnlDir}/{id}.metadata.json
        //  - prevents an old child or AppHost cleanup from touching a replacement run's sockets.
        var configuration = @event.Services.GetRequiredService<IConfiguration>();
        var appHostPath = configuration["AppHost:FilePath"] ?? configuration["AppHost:Path"];
        if (string.IsNullOrEmpty(appHostPath))
        {
            throw new InvalidOperationException(
                "Cannot materialize terminal hosts: AppHost:FilePath / AppHost:Path is not set in configuration.");
        }

        var trmnlDirectory = configuration[TerminalHostPaths.DirectoryOverrideConfigName];
        if (string.IsNullOrEmpty(trmnlDirectory))
        {
            var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(homeDirectory);
        }

        // 0700 on Unix so other local users cannot enumerate which terminals exist on
        // this machine. On Windows the user-profile ACLs (per-user by default) make this
        // a no-op; CreateDirectory is idempotent.
        Directory.CreateDirectory(trmnlDirectory);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    trmnlDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Best-effort: directory may already have stricter perms or be on a filesystem
                // that does not support chmod (e.g. some FAT-formatted home dirs). Per-socket
                // 0600 in TerminalHostControlListener still protects each endpoint.
            }
        }

        var terminalHosts = new TerminalHostResource[replicaCount];
        var replicaIds = new string[replicaCount];
        var metadataLogger = @event.Services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.WithTerminal");
        var appHostPid = Environment.ProcessId;
        var appHostProcessIdentity = ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds();
        var appHostProcessScopeId = TerminalHostOrphanCleanupService.GetCurrentProcessScopeId();
        var appHostBootId = TerminalHostOrphanCleanupService.GetCurrentBootId();
        var createdAtUtc = DateTime.UtcNow;

        for (var i = 0; i < replicaCount; i++)
        {
            var replicaId = TerminalHostPaths.CreateReplicaId();
            var layout = CreateTerminalHostLayout(trmnlDirectory, replicaId, i);
            var terminalHostName = $"{parent.Name}-terminalhost-{i.ToString(CultureInfo.InvariantCulture)}";
            var terminalHost = new TerminalHostResource(terminalHostName, parent, layout);

            ConfigureTerminalHostAnnotations(
                terminalHost,
                options,
                appHostPid,
                appHostProcessIdentity);

            // Wire OTLP env vars onto each terminal host so it can ship logs/traces/metrics to
            // the dashboard. Without this, terminal host failures (DCP never dials, control
            // socket bind fails, replica recycles in a loop) are only visible by attaching a
            // debugger — there is no other log sink: the host explicitly does not write to
            // stderr because DCP captures stderr into the consumer's resource log stream
            // and any host-generated bytes would corrupt that view.
            //
            // AddOtlpEnvironment also injects OTEL_RESOURCE_ATTRIBUTES=service.instance.id=...,
            // which combined with DCP's OTEL_SERVICE_NAME annotation gives each replica's
            // host process a unique identity in the dashboard. We do not pin a protocol here
            // (gRPC vs HTTP/protobuf); the env-callback picks the dashboard's preferred
            // protocol and the host's composite `UseOtlpExporter()` honours
            // OTEL_EXPORTER_OTLP_PROTOCOL — same path every ServiceDefaults-wired Aspire
            // project takes.
            OtlpConfigurationExtensions.AddOtlpEnvironment(terminalHost, configuration, @event.Services.GetRequiredService<IHostEnvironment>());

            // Propagate ASPIRE_TERMINAL_HOST_LOG_LEVEL from the AppHost process so playground/dev
            // can dial up host verbosity (e.g. "Debug") from launchSettings.json without code
            // changes. The terminal host itself reads this env var and applies it to
            // ILoggingBuilder.SetMinimumLevel; only relevant when OTLP is wired so the resulting
            // log records reach the dashboard.
            var hostLogLevel = Environment.GetEnvironmentVariable("ASPIRE_TERMINAL_HOST_LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(hostLogLevel))
            {
                terminalHost.Annotations.Add(new EnvironmentCallbackAnnotation(ctx =>
                {
                    ctx.EnvironmentVariables["ASPIRE_TERMINAL_HOST_LOG_LEVEL"] = hostLogLevel;
                }));
            }

            @event.Model.Resources.Add(terminalHost);

            // Write the sidecar BEFORE the host process starts so external discovery tools
            // (CLI `aspire terminal ps`, dashboard) can see the terminal as soon as DCP
            // begins spawning hosts — without waiting for the host to come up and bind its
            // control socket. The host process never reads its own sidecar; the AppHost is
            // the sole writer.
            await WriteMetadataSidecarAsync(
                layout.MetadataPath,
                new TerminalHostMetadata
                {
                    ReplicaId = replicaId,
                    ResourceName = parent.Name,
                    ReplicaIndex = i,
                    AppHostPath = appHostPath,
                    AppHostPid = appHostPid,
                    AppHostProcessIdentity = appHostProcessIdentity,
                    AppHostProcessScopeId = appHostProcessScopeId,
                    AppHostBootId = appHostBootId,
                    CreatedAtUtc = createdAtUtc,
                    Columns = options.Columns,
                    Rows = options.Rows,
                    ControlSocketPath = layout.ControlUdsPath,
                    ConsumerSocketPath = layout.ConsumerUdsPath,
                },
                metadataLogger,
                cancellationToken).ConfigureAwait(false);

            terminalHosts[i] = terminalHost;
            replicaIds[i] = replicaId;
        }

        // The terminal-host children unlink their sockets on graceful shutdown, while this
        // app-scoped backstop removes metadata and exact-path artifacts left by abrupt exits.
        // The singleton applies one bounded ApplicationStopped wait across every terminal resource.
        @event.Services.GetRequiredService<TerminalHostOrphanCleanupService>()
            .RegisterReplicaArtifacts(trmnlDirectory, replicaIds);

        // The target waits until each host has started so its viewer-facing UDS listener
        // is bound before any consumer (Dashboard or CLI) tries to connect. A follow-up
        // pass will switch this to WaitUntilHealthy once each host implements a real
        // health probe.
        if (parent is IResourceWithWaitSupport)
        {
            for (var i = 0; i < terminalHosts.Length; i++)
            {
                parent.Annotations.Add(new WaitAnnotation(terminalHosts[i], WaitType.WaitUntilStarted));
            }
        }

        annotation.Initialize(terminalHosts);
    }

    private static async Task WriteMetadataSidecarAsync(
        string metadataPath,
        TerminalHostMetadata metadata,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var temporaryMetadataPath = TerminalHostPaths.GetMetadataTemporaryPath(metadataPath);
        try
        {
            // Indented for human inspection: the file is small (<1 KiB) and is expected to
            // be `cat`-ed by users debugging terminal-host issues. Performance is irrelevant.
            var json = JsonSerializer.Serialize(metadata, s_metadataSerializerOptions);

            // Write and chmod a sibling temporary file before atomically replacing the sidecar.
            // A crash during serialization can then leave only an undiscoverable .tmp file, never
            // a truncated metadata document that permanently blocks orphan recovery.
            using (var fs = new FileStream(
                temporaryMetadataPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(temporaryMetadataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        // Filesystem may not support chmod (e.g. FAT). The parent dir is 0700
                        // so the file is still unreachable by other users.
                        logger?.LogDebug(ex, "Failed to chmod terminal host metadata file '{Path}'.", temporaryMetadataPath);
                    }
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await fs.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryMetadataPath, metadataPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Per-run paths cannot collide with another AppHost. A missing sidecar only degrades
            // external discovery and crash recovery for this terminal.
            logger?.LogDebug(ex, "Failed to write terminal host metadata sidecar '{Path}'.", metadataPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryMetadataPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(ex, "Failed to delete temporary terminal host metadata file '{Path}'.", temporaryMetadataPath);
            }
        }
    }

    private static readonly JsonSerializerOptions s_metadataSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static void ConfigureTerminalHostAnnotations(
        TerminalHostResource host,
        TerminalOptions options,
        int appHostPid,
        long appHostProcessIdentity)
    {
        // Equivalent to the previous WithInitialState(...).ExcludeFromManifest().WithArgs(...) chain
        // but we can't go through IResourceBuilder<T> here — we're running mid-event without an
        // IDistributedApplicationBuilder reference, and creating one against the already-built
        // application is not supported. Adding the annotations directly produces an identical
        // resource state (each helper above is just sugar over Annotations.Add).
        host.Annotations.Add(new ResourceSnapshotAnnotation(new CustomResourceSnapshot
        {
            ResourceType = "TerminalHost",
            State = KnownResourceStates.NotStarted,
            Properties = [],
            // Hidden by default — terminal hosts are an implementation detail of
            // WithTerminal(). Users opt in to seeing them via
            // TerminalOptions.ShowTerminalHost = true when diagnosing host startup /
            // recycle / DCP-connectivity problems.
            IsHidden = !options.ShowTerminalHost,
        }));

        host.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);

        host.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables[KnownConfigNames.TerminalHostParentProcessId] =
                appHostPid.ToString(CultureInfo.InvariantCulture);
            context.EnvironmentVariables[KnownConfigNames.TerminalHostParentProcessStartedStable] =
                appHostProcessIdentity.ToString(CultureInfo.InvariantCulture);
        }));

        host.Annotations.Add(new CommandLineArgsCallbackAnnotation(context =>
        {
            context.Args.Add("--producer-uds");
            context.Args.Add(host.Layout.ProducerUdsPath);

            context.Args.Add("--consumer-uds");
            context.Args.Add(host.Layout.ConsumerUdsPath);

            context.Args.Add("--control-uds");
            context.Args.Add(host.Layout.ControlUdsPath);

            context.Args.Add("--columns");
            context.Args.Add(options.Columns.ToString(CultureInfo.InvariantCulture));

            context.Args.Add("--rows");
            context.Args.Add(options.Rows.ToString(CultureInfo.InvariantCulture));

            return Task.CompletedTask;
        }));
    }

#pragma warning restore ASPIRETERMINAL001

    /// <summary>
    /// Builds the per-replica UDS triple + metadata path for a single terminal host. All
    /// four files live flat under the configured terminal artifact directory and share the same
    /// <paramref name="replicaId"/> filename prefix.
    /// </summary>
    private static TerminalHostLayout CreateTerminalHostLayout(string trmnlDirectory, string replicaId, int replicaIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(trmnlDirectory);
        ArgumentException.ThrowIfNullOrEmpty(replicaId);
        ArgumentOutOfRangeException.ThrowIfNegative(replicaIndex);

        var producerPath = TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ProducerSockPurpose);
        var consumerPath = TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ConsumerSockPurpose);
        var controlPath = TerminalHostPaths.GetSocketPath(trmnlDirectory, replicaId, TerminalHostPaths.ControlSockPurpose);
        var metadataPath = TerminalHostPaths.GetMetadataPath(trmnlDirectory, replicaId);

        return new TerminalHostLayout(replicaId, replicaIndex, producerPath, consumerPath, controlPath, metadataPath);
    }
}
