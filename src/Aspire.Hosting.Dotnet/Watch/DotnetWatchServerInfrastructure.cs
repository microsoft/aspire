// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// Makes the watch server look and behave like other Aspire infrastructure (the dashboard, terminal hosts and
/// rebuilders): hidden by default, out of the manifest, and without the Stop/Restart lifecycle commands. Also owns the
/// private run directory that holds the watcher's Unix sockets.
/// </summary>
internal static class DotnetWatchServerInfrastructure
{
    /// <summary>Owner-only permissions (0700) for the run directory.</summary>
    internal const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // HACK(spike): Aspire.Hosting has no public way to opt a resource out of its lifecycle commands; these are the
    // internal types the dashboard resource and the project rebuilders use. TODO: a public system-resource API.
    private const string ExcludeLifecycleCommandsAnnotationTypeName = "Aspire.Hosting.ApplicationModel.ExcludeLifecycleCommandsAnnotation";
    private const string ApplicationOrchestratorTypeName = "Aspire.Hosting.Orchestrator.ApplicationOrchestrator";

    /// <summary>
    /// Hides the server and replaces Start/Stop/Restart with a single <c>start</c> command that is only offered
    /// while the server isn't running, so it can be recovered (<c>aspire resource dotnet-watch start</c>) but not
    /// stopped out from under the watched services.
    /// </summary>
    internal static IResourceBuilder<T> AsWatchSystemResource<T>(this IResourceBuilder<T> server) where T : IResource
    {
        server.WithHidden().ExcludeFromManifest();

        // Hidden resources don't draw attention when they fail, so say so where the user is looking: the terminal
        // notices and the log of every service that waits on the server.
        server.ApplicationBuilder.Eventing.Subscribe<ResourceStoppedEvent>(server.Resource, (e, _) =>
        {
            if (e.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var message = FormatStoppedNotice(server.Resource.Name, e.ResourceEvent.Snapshot.ExitCode);
            e.Services.GetRequiredService<ILoggerFactory>().CreateLogger(DotnetWatchCoordinator.NoticeLoggerCategory).LogError("{Message}", message);

            var loggers = e.Services.GetRequiredService<ResourceLoggerService>();
            foreach (var dependent in GetDependents(e.Services.GetRequiredService<DistributedApplicationModel>(), server.Resource))
            {
                loggers.GetLogger(dependent).LogError("[watch] {Message}", message);
            }

            return Task.CompletedTask;
        });

        var hostingAssembly = typeof(ResourceCommandAnnotation).Assembly;
        if (hostingAssembly.GetType(ExcludeLifecycleCommandsAnnotationTypeName) is not { } excludeType
            || hostingAssembly.GetType(ApplicationOrchestratorTypeName) is not { } orchestratorType
            || orchestratorType.GetMethod("StartResourceAsync", BindingFlags.Public | BindingFlags.Instance, [typeof(string), typeof(CancellationToken)]) is not { } startMethod
            || Activator.CreateInstance(excludeType, nonPublic: true) is not IResourceAnnotation excludeAnnotation)
        {
            // Keep the built-in lifecycle commands rather than leaving the server without a way to start again.
            return server;
        }

        server.WithAnnotation(excludeAnnotation);
        server.WithCommand(
            KnownResourceCommands.StartCommand,
            "Start",
            async context =>
            {
                var orchestrator = context.Services.GetRequiredService(orchestratorType);
                await ((Task)startMethod.Invoke(orchestrator, [context.ResourceName, context.CancellationToken])!).ConfigureAwait(false);
                return CommandResults.Success();
            },
            new CommandOptions
            {
                Description = "Start the hot reload watcher. Watched services wait for it before they start.",
                IconName = "Play",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true,
                UpdateState = context => IsStopped(context.ResourceSnapshot.State?.Text) ? ResourceCommandState.Enabled : ResourceCommandState.Hidden,
            });

        return server;
    }

    /// <summary>
    /// Makes a Unix socket created by <see cref="System.IO.Pipes.NamedPipeServerStream"/> owner-only (0600). With
    /// <c>CurrentUserOnly</c>, .NET 10 rejects connections from other users but leaves the socket file at the umask
    /// default (0755); newer runtimes chmod it themselves. The run directory is 0700 as well.
    /// </summary>
    internal static void RestrictSocketToCurrentUser(string socketPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static string FormatStoppedNotice(string serverName, int? exitCode) =>
        string.Format(CultureInfo.InvariantCulture,
            "❌ The hot reload watcher stopped{0}. Edits aren't applied, and watched services can't (re)start until it runs again. Start it again with 'aspire resource {1} start' or from the dashboard (show hidden resources).",
            exitCode is { } code ? string.Format(CultureInfo.InvariantCulture, " (exit code {0})", code) : "",
            serverName);

    internal static IEnumerable<IResource> GetDependents(DistributedApplicationModel model, IResource server) =>
        model.Resources.Where(r => r.Annotations.OfType<WaitAnnotation>().Any(w => ReferenceEquals(w.Resource, server)));

    internal static bool IsStopped(string? state) =>
        state is null || KnownResourceStates.TerminalStates.Contains(state) || state == KnownResourceStates.NotStarted || state == "Unknown";

    private static readonly HashSet<string> s_createdDirectories = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates <paramref name="path"/> accessible only by the current user (0700 on Unix). A directory this process
    /// didn't create is rejected, so a directory planted at the (random) path by someone else is never reused; later
    /// calls (server restarts) re-assert the mode on the directory created earlier.
    /// </summary>
    internal static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The pipes are named pipes (not files) and the per-user temp directory already has an owner-only ACL.
            Directory.CreateDirectory(path);
            return;
        }

        // Servers sharing a run directory are prepared concurrently, so the ownership check, creation and
        // registration happen under one lock; otherwise a second caller could see the first caller's directory
        // before it's registered and reject it as planted.
        lock (s_createdDirectories)
        {
            if (!s_createdDirectories.Contains(path) && Path.Exists(path))
            {
                throw new DistributedApplicationException(string.Format(CultureInfo.InvariantCulture,
                    "The watch run directory '{0}' already exists. Remove it and try again.", path));
            }

            if (new DirectoryInfo(path).LinkTarget is not null)
            {
                throw new DistributedApplicationException(string.Format(CultureInfo.InvariantCulture,
                    "The watch run directory '{0}' is a symbolic link.", path));
            }

            try
            {
                Directory.CreateDirectory(path, PrivateDirectoryMode);

                // CreateDirectory leaves an existing directory's mode alone, and the umask only ever removes bits. This
                // also fails if someone else won a race to create the directory, because only the owner can chmod it.
                File.SetUnixFileMode(path, PrivateDirectoryMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DistributedApplicationException(string.Format(CultureInfo.InvariantCulture,
                    "Couldn't create the private watch run directory '{0}': {1}", path, ex.Message), ex);
            }

            s_createdDirectories.Add(path);
        }
    }
}
