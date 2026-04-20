// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Terminals.AppHost;

/// <summary>
/// A custom resource that represents an interactive terminal session backed by
/// the Terminals.TerminalHost process. The terminal host runs Hex1b with a PTY
/// shell and serves the Aspire Terminal Protocol over a Unix domain socket.
/// </summary>
public sealed class TerminalDemoResource(string name) : Resource(name), IResourceWithWaitSupport;

/// <summary>
/// Extension methods for adding terminal demo resources to the application builder.
/// </summary>
public static class TerminalDemoExtensions
{
    /// <summary>
    /// Adds a terminal demo resource that launches a Hex1b-hosted shell session.
    /// The terminal host process receives the UDS path via environment variable
    /// and implements the Aspire Terminal Protocol for client connections.
    /// </summary>
    [AspireExportIgnore(Reason = "Playground-only terminal demo resource; not part of the supported ATS surface.")]
    public static IResourceBuilder<TerminalDemoResource> AddTerminalDemo(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        var resource = new TerminalDemoResource(name);

        // Generate a UDS path for this terminal session
        var socketDir = Path.Combine(Path.GetTempPath(), "aspire-terminals");
        Directory.CreateDirectory(socketDir);
        var socketPath = Path.Combine(socketDir, $"{name}-{Environment.ProcessId}.sock");

        // Find the terminal host project executable
        var terminalHostProject = FindTerminalHostProject();

        var resourceBuilder = builder.AddResource(resource)
            .ExcludeFromManifest()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "TerminalDemo",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.NotStarted,
                Properties =
                [
                    new(CustomResourceKnownProperties.Source, "Terminal Demo"),
                    new("terminal.socketPath", socketPath)
                ]
            })
            .WithTerminal();

        // Use OnInitializeResource to launch the terminal host process and manage its lifecycle.
        // This replaces what DCP would normally do (PTY allocation + UDS forwarding).
        resourceBuilder.OnInitializeResource(async (resource, @event, token) =>
        {
            var log = @event.Logger;
            var notification = @event.Notifications;
            var eventing = @event.Eventing;
            var services = @event.Services;

            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services), token);

            log.LogInformation("Starting terminal host: {Project}", terminalHostProject);
            log.LogInformation("Terminal socket: {SocketPath}", socketPath);

            // Store the socket path in the terminal annotation
            if (resource.Annotations.OfType<TerminalAnnotation>().FirstOrDefault() is { } terminalAnnotation)
            {
                terminalAnnotation.SocketPath = socketPath;
            }

            // Launch the terminal host as a child process
            var psi = new ProcessStartInfo("dotnet", ["run", "--project", terminalHostProject])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["TERMINAL_SOCKET_PATH"] = socketPath,
                    ["TERMINAL_COLUMNS"] = "120",
                    ["TERMINAL_ROWS"] = "30",
                }
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                await notification.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                });
                return;
            }

            // Forward stderr to resource logs
            _ = Task.Run(async () =>
            {
                while (!process.HasExited && !token.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(token);
                    if (line is not null)
                    {
                        log.LogInformation("{Line}", line);
                    }
                }
            }, token);

            await notification.PublishUpdateAsync(resource, s => s with
            {
                StartTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Running,
                Properties =
                [
                    .. s.Properties,
                    new("terminal.pid", process.Id.ToString(CultureInfo.InvariantCulture))
                ]
            });

            try
            {
                await process.WaitForExitAsync(token);
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
            });
        });

        return resourceBuilder;
    }

    private static string FindTerminalHostProject()
    {
        // Walk up from the AppHost's directory to find the sibling TerminalHost project
        var appHostDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(appHostDir);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "playground", "Terminals", "Terminals.TerminalHost", "Terminals.TerminalHost.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Also check sibling directory (for when running from bin output)
            var sibling = Path.Combine(current.FullName, "Terminals.TerminalHost", "Terminals.TerminalHost.csproj");
            if (File.Exists(sibling))
            {
                return sibling;
            }

            current = current.Parent;
        }

        // Fallback: assume relative path from working directory
        return Path.GetFullPath(Path.Combine("..", "Terminals.TerminalHost", "Terminals.TerminalHost.csproj"));
    }
}
