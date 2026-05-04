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
    /// (PTY) per replica and a hidden <see cref="TerminalHostResource"/> bridges the PTY traffic
    /// over Hex1b's HMP v1 protocol. The terminal session can be accessed from the Aspire
    /// Dashboard's terminal page or via the <c>aspire terminal</c> CLI command.
    /// </para>
    /// <para>
    /// The set of socket paths used to wire DCP, the host, and viewers together is sized from
    /// the target resource's <see cref="ReplicaAnnotation"/>. <strong>Call
    /// <c>WithReplicas(...)</c> before <c>WithTerminal()</c></strong>; if the replica count
    /// changes after this call, only the first <c>N</c> replicas (where <c>N</c> was the count
    /// at <c>WithTerminal()</c> time) will have an attachable terminal.
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
        var layout = CreateTerminalHostLayout(replicaCount);

        var terminalHostName = $"{builder.Resource.Name}-terminalhost";
        var terminalHost = new TerminalHostResource(terminalHostName, builder.Resource, layout);

        builder.WithAnnotation(new TerminalAnnotation(terminalHost, options));

        var terminalHostBuilder = builder.ApplicationBuilder.AddResource(terminalHost);

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
                context.Args.Add("--replica-count");
                context.Args.Add(layout.ReplicaCount.ToString(CultureInfo.InvariantCulture));

                foreach (var path in layout.ProducerUdsPaths)
                {
                    context.Args.Add("--producer-uds");
                    context.Args.Add(path);
                }

                foreach (var path in layout.ConsumerUdsPaths)
                {
                    context.Args.Add("--consumer-uds");
                    context.Args.Add(path);
                }

                context.Args.Add("--control-uds");
                context.Args.Add(layout.ControlUdsPath);

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

        // The target waits until the host has started so its viewer-facing UDS listeners are
        // bound before any consumer (Dashboard or CLI) tries to connect. Phase 2 will switch
        // this to WaitUntilHealthy once the host implements a real health probe.
        if (builder.Resource is IResourceWithWaitSupport)
        {
            builder.WithAnnotation(new WaitAnnotation(terminalHost, WaitType.WaitUntilStarted));
        }

        return builder;
    }

    private static TerminalHostLayout CreateTerminalHostLayout(int replicaCount)
    {
        if (replicaCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaCount), replicaCount, "Replica count must be at least 1.");
        }

        // Use Directory.CreateTempSubdirectory so we get a securely-created, unique directory
        // for each AppHost run. The guid in the directory name protects against collisions.
        var baseDir = Directory.CreateTempSubdirectory("aspire-term-").FullName;
        Directory.CreateDirectory(Path.Combine(baseDir, "dcp"));
        Directory.CreateDirectory(Path.Combine(baseDir, "host"));

        var producerPaths = new string[replicaCount];
        var consumerPaths = new string[replicaCount];

        for (var i = 0; i < replicaCount; i++)
        {
            producerPaths[i] = Path.Combine(baseDir, "dcp", $"r{i.ToString(CultureInfo.InvariantCulture)}.sock");
            consumerPaths[i] = Path.Combine(baseDir, "host", $"r{i.ToString(CultureInfo.InvariantCulture)}.sock");
        }

        var controlPath = Path.Combine(baseDir, "control.sock");

        return new TerminalHostLayout(baseDir, producerPaths, consumerPaths, controlPath);
    }
}
