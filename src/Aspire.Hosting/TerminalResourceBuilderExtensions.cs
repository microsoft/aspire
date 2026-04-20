// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring interactive terminal support on resources.
/// </summary>
public static class TerminalResourceBuilderExtensions
{
    /// <summary>
    /// Configures a resource to have an interactive terminal session.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">An optional callback to configure the terminal options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <remarks>
    /// <para>
    /// When a resource is configured with <c>.WithTerminal()</c>, the orchestrator allocates a pseudo-terminal (PTY)
    /// for the resource's process and makes it available for interactive access. The terminal session can be accessed
    /// from the Aspire Dashboard's "Terminal" tab or via the <c>aspire terminal</c> CLI command.
    /// </para>
    /// <para>
    /// A hidden terminal host resource is created automatically to manage the terminal state using Hex1b.
    /// The parent resource will wait for the terminal host to be ready before starting.
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
    /// Add terminal support with custom dimensions:
    /// <code>
    /// var agent = builder.AddExecutable("agent", "my-agent", ".")
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

        var options = new TerminalOptions();
        configure?.Invoke(options);

        var annotation = new TerminalAnnotation
        {
            Options = options
        };

        builder.WithAnnotation(annotation);

        AddTerminalHostResource(builder);

        return builder;
    }

    private static void AddTerminalHostResource<T>(IResourceBuilder<T> builder)
        where T : IResource
    {
        var terminalHostName = $"{builder.Resource.Name}-terminal-host";
        var terminalHost = new TerminalHostResource(terminalHostName, builder.Resource);

        var terminalHostBuilder = builder.ApplicationBuilder.AddResource(terminalHost);

        terminalHostBuilder
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TerminalHost",
                State = KnownResourceStates.NotStarted,
                Properties = [],
                IsHidden = true,
            })
            .ExcludeFromManifest();

        // The parent resource waits for the terminal host to be started so that
        // the PTY forwarding infrastructure is in place before the process begins.
        if (builder.Resource is IResourceWithWaitSupport)
        {
            builder.WithAnnotation(new WaitAnnotation(terminalHost, WaitType.WaitUntilStarted));
        }
    }
}
