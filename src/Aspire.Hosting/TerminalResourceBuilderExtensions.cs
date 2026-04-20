// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Configures a resource with a custom terminal socket path provider.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="socketPathProvider">
    /// A callback that returns the UDS path for the terminal session. This is called during resource
    /// initialization. Use this overload for resources that manage their own terminal host process
    /// (e.g., remote resources, SSH sessions, or custom terminal bridges).
    /// </param>
    /// <param name="configure">An optional callback to configure the terminal options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <remarks>
    /// Unlike the parameterless <see cref="WithTerminal{T}(IResourceBuilder{T}, Action{TerminalOptions}?)"/> overload,
    /// this method does not create a hidden terminal host resource. The caller is responsible for ensuring
    /// a terminal server is listening on the provided socket path and speaking the Aspire Terminal Protocol.
    /// </remarks>
    /// <example>
    /// Add terminal support with a custom socket path:
    /// <code>
    /// var agent = builder.AddResource(new MyCustomResource("agent"))
    ///     .WithTerminal(ct => Task.FromResult("/tmp/my-terminal.sock"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Func delegate parameter is not ATS-compatible.")]
    public static IResourceBuilder<T> WithTerminal<T>(
        this IResourceBuilder<T> builder,
        Func<CancellationToken, Task<string>> socketPathProvider,
        Action<TerminalOptions>? configure = null)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(socketPathProvider);

        var options = new TerminalOptions();
        configure?.Invoke(options);

        var annotation = new TerminalAnnotation
        {
            Options = options,
            SocketPathProvider = socketPathProvider
        };

        builder.WithAnnotation(annotation);

        // No hidden terminal host resource — the caller manages the terminal server.

        return builder;
    }

    private static void AddTerminalHostResource<T>(IResourceBuilder<T> builder)
        where T : IResource
    {
        var terminalHostName = $"{builder.Resource.Name}-terminal-host";
        var terminalHost = new TerminalHostResource(terminalHostName, builder.Resource);

        // Generate a UDS path for this terminal session
        var socketDir = Path.Combine(Path.GetTempPath(), "aspire-terminals");
        Directory.CreateDirectory(socketDir);
        var socketPath = Path.Combine(socketDir, $"{builder.Resource.Name}-{Environment.ProcessId}.sock");

        // Store the socket path on the terminal annotation
        var terminalAnnotation = builder.Resource.Annotations.OfType<TerminalAnnotation>().First();
        terminalAnnotation.SocketPath = socketPath;

        var terminalHostBuilder = builder.ApplicationBuilder.AddResource(terminalHost);

        var options = terminalAnnotation.Options;

        terminalHostBuilder
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TerminalHost",
                State = KnownResourceStates.NotStarted,
                Properties = [],
                IsHidden = true,
            })
            .ExcludeFromManifest();

        // Set up the terminal host lifecycle: when the resource initializes,
        // resolve the terminal host binary path and launch it via DCP.
        terminalHostBuilder.OnInitializeResource(async (resource, @event, token) =>
        {
            var dcpOptions = @event.Services.GetService<IOptions<Dcp.DcpOptions>>();
            var notification = @event.Notifications;
            var log = @event.Logger;

            var terminalHostPath = dcpOptions?.Value.TerminalHostPath;
            if (string.IsNullOrEmpty(terminalHostPath))
            {
                log.LogWarning("Terminal host binary path not configured. Terminal will not be available for resource '{ResourceName}'.", resource.Parent.Name);
                await notification.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(terminalHostPath))
            {
                log.LogWarning("Terminal host binary not found at '{Path}'. Build Aspire.TerminalHost first.", terminalHostPath);
                await notification.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);
                return;
            }

            log.LogInformation("Starting terminal host for '{ResourceName}' at {SocketPath}", resource.Parent.Name, socketPath);

            // Launch the terminal host process
            var psi = new System.Diagnostics.ProcessStartInfo(terminalHostPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                Environment =
                {
                    ["TERMINAL_SOCKET_PATH"] = socketPath,
                    ["TERMINAL_COLUMNS"] = options.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["TERMINAL_ROWS"] = options.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }
            };

            if (options.Shell is not null)
            {
                psi.Environment["TERMINAL_SHELL"] = options.Shell;
            }

            var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                log.LogError("Failed to start terminal host process");
                await notification.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);
                return;
            }

            // Forward stderr to resource logs
            _ = Task.Run(async () =>
            {
                while (!process.HasExited && !token.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is not null)
                    {
                        log.LogInformation("{Line}", line);
                    }
                }
            }, token);

            await notification.PublishUpdateAsync(resource, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Running
            }).ConfigureAwait(false);

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            await notification.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Exited,
                ExitCode = process.ExitCode
            }).ConfigureAwait(false);
        });

        // The parent resource waits for the terminal host to be started so that
        // the PTY forwarding infrastructure is in place before the process begins.
        if (builder.Resource is IResourceWithWaitSupport)
        {
            builder.WithAnnotation(new WaitAnnotation(terminalHost, WaitType.WaitUntilStarted));
        }
    }
}
