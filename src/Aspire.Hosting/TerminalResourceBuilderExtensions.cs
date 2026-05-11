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
    /// <strong>Call <c>WithReplicas(...)</c> before <c>WithTerminal()</c></strong>; if the
    /// replica count changes after this call, only the first <c>N</c> replicas (where <c>N</c>
    /// was the count at <c>WithTerminal()</c> time) will have an attachable terminal.
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
    /// Add terminal support with custom dimensions to a multi-replica resource:
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
    [AspireExportIgnore(Reason = "Action<TerminalOptions> delegate parameter is not ATS-compatible.")]
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

        var replicaCount = builder.Resource.Annotations.OfType<ReplicaAnnotation>().LastOrDefault()?.Replicas ?? 1;
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
            var terminalHostName = $"{builder.Resource.Name}-terminalhost-{i.ToString(CultureInfo.InvariantCulture)}";
            var terminalHost = new TerminalHostResource(terminalHostName, builder.Resource, layout);
            terminalHosts[i] = terminalHost;
        }

        builder.WithAnnotation(new TerminalAnnotation(terminalHosts, options));

        // Register and configure each per-replica host. Capture-by-value semantics matter
        // here: each iteration creates its own `host` and `replicaIndex` locals so the
        // WithArgs callback closes over the right host even though it runs lazily later.
        foreach (var terminalHost in terminalHosts)
        {
            var host = terminalHost;
            var terminalHostBuilder = builder.ApplicationBuilder.AddResource(host);

            terminalHostBuilder
                .WithInitialState(new CustomResourceSnapshot
                {
                    ResourceType = "TerminalHost",
                    State = KnownResourceStates.NotStarted,
                    Properties = [],
                    IsHidden = true,
                })
                .ExcludeFromManifest()
                .WithArgs(context =>
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
                });
        }

        // The target waits until each host has started so its viewer-facing UDS listener
        // is bound before any consumer (Dashboard or CLI) tries to connect. Phase 2 will
        // switch this to WaitUntilHealthy once each host implements a real health probe.
        if (builder.Resource is IResourceWithWaitSupport)
        {
            foreach (var terminalHost in terminalHosts)
            {
                builder.WithAnnotation(new WaitAnnotation(terminalHost, WaitType.WaitUntilStarted));
            }
        }

        return builder;
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

