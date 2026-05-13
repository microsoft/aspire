// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring interactive terminal support on resources.
/// </summary>
public static class TerminalResourceBuilderExtensions
{
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
    /// (PTY) per replica and one hidden <see cref="TerminalHostResource"/> per replica bridges
    /// the PTY traffic over Hex1b's HMP v1 protocol. The terminal session can be accessed from
    /// the Aspire Dashboard's terminal page or via the <c>aspire terminal</c> CLI command.
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
    [AspireExportIgnore(Reason = "Polyglot app hosts use the parameterless withTerminal dispatcher export.")]
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

        var parent = builder.Resource;
        var appBuilder = builder.ApplicationBuilder;

        // Subscribe directly on the IDistributedApplicationEventing rather than registering an
        // IDistributedApplicationEventingSubscriber: subscriptions registered during the builder
        // phase fire in registration order ahead of DI-registered subscribers (which only attach
        // their callbacks during DistributedApplication.RunApplicationAsync). That ordering is
        // important — TerminalHostEventingSubscriber resolves each host's binary path by
        // iterating model.Resources.OfType<TerminalHostResource>(), so the hosts MUST already
        // be in the model by the time it runs.
        appBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            MaterializeTerminalHosts(@event, parent, annotation, options);
            return Task.CompletedTask;
        });

        return builder;
    }

    /// <summary>
    /// Polyglot dispatcher for <see cref="WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/>.
    /// Exposed to non-C# AppHosts via ATS as <c>withTerminal</c> — they cannot pass a
    /// C# <see cref="Action{T}"/>, so this overload simply applies the defaults from
    /// <see cref="TerminalOptions"/> (120×30, default shell). Polyglot AppHosts that need
    /// to customise columns/rows/shell can fall back to per-resource environment variables
    /// or wait for a future overload that accepts a DTO.
    /// </summary>
    [AspireExport("withTerminal", Description = "Adds an interactive terminal session to a resource using the default terminal options.")]
    internal static IResourceBuilder<T> WithTerminalForPolyglot<T>(this IResourceBuilder<T> builder)
        where T : IResource
        => builder.WithTerminal();

    /// <summary>
    /// Reads the parent's final <see cref="ReplicaAnnotation"/> and creates one
    /// <see cref="TerminalHostResource"/> per replica. Idempotent — re-firing
    /// <see cref="BeforeStartEvent"/> (e.g. from a test) is a no-op once the
    /// <paramref name="annotation"/> is initialized.
    /// </summary>
    private static void MaterializeTerminalHosts(
        BeforeStartEvent @event,
        IResource parent,
        TerminalAnnotation annotation,
        TerminalOptions options)
    {
        if (annotation.IsInitialized)
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

        // One temp base dir per parent: per-replica hosts get sub-directories beneath it
        // (`{base}/{i}/...`) so the AppHost can clean up every host's sockets with a single
        // recursive delete when the run ends.
        var baseDir = Directory.CreateTempSubdirectory("aspire-term-").FullName;

        var terminalHosts = new TerminalHostResource[replicaCount];
        for (var i = 0; i < replicaCount; i++)
        {
            var layout = CreateTerminalHostLayout(baseDir, i);
            var terminalHostName = $"{parent.Name}-terminalhost-{i.ToString(CultureInfo.InvariantCulture)}";
            var terminalHost = new TerminalHostResource(terminalHostName, parent, layout);

            ConfigureTerminalHostAnnotations(terminalHost, options);
            @event.Model.Resources.Add(terminalHost);

            terminalHosts[i] = terminalHost;
        }

        // The target waits until each host has started so its viewer-facing UDS listener
        // is bound before any consumer (Dashboard or CLI) tries to connect. Phase 2 will
        // switch this to WaitUntilHealthy once each host implements a real health probe.
        if (parent is IResourceWithWaitSupport)
        {
            for (var i = 0; i < terminalHosts.Length; i++)
            {
                parent.Annotations.Add(new WaitAnnotation(terminalHosts[i], WaitType.WaitUntilStarted));
            }
        }

        annotation.Initialize(baseDir, terminalHosts);
    }

    private static void ConfigureTerminalHostAnnotations(TerminalHostResource host, TerminalOptions options)
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
            IsHidden = true,
        }));

        host.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);

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

            if (!string.IsNullOrEmpty(options.Shell))
            {
                context.Args.Add("--shell");
                context.Args.Add(options.Shell);
            }

            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Builds the per-replica UDS triple for a single terminal host. Sockets live under a
    /// per-replica sub-directory (<c>{baseDir}/{replicaIndex}/</c>) so per-replica hosts of
    /// the same parent get unique paths while still sharing the parent's <paramref name="baseDir"/>
    /// (which makes cleanup a single recursive delete).
    /// </summary>
    private static TerminalHostLayout CreateTerminalHostLayout(string baseDir, int replicaIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDir);
        ArgumentOutOfRangeException.ThrowIfNegative(replicaIndex);

        var replicaDir = Path.Combine(baseDir, replicaIndex.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(replicaDir);

        var producerPath = Path.Combine(replicaDir, "dcp.sock");
        var consumerPath = Path.Combine(replicaDir, "host.sock");
        var controlPath = Path.Combine(replicaDir, "control.sock");

        return new TerminalHostLayout(baseDir, replicaIndex, producerPath, consumerPath, controlPath);
    }
}
