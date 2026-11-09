// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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

    private static readonly ConditionalWeakTable<IResource, Lifecycle> s_lifecycles = new();

    /// <summary>
    /// Hides the server and replaces Start/Stop/Restart with a single <c>start</c> command that is only offered
    /// while the server isn't running, so it can be recovered (<c>aspire resource dotnet-watch start</c>) but not
    /// stopped out from under the watched services.
    /// </summary>
    internal static IResourceBuilder<T> AsWatchSystemResource<T>(this IResourceBuilder<T> server) where T : IResource
    {
        server.WithHidden().ExcludeFromManifest();
        var lifecycle = s_lifecycles.GetValue(server.Resource, _ => new());

        // Hidden resources don't draw attention when they fail, so say so where the user is looking: the terminal
        // notices and the log of every service that waits on the server.
        server.ApplicationBuilder.Eventing.Subscribe<ResourceStoppedEvent>(server.Resource, (e, _) =>
        {
            if (e.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested ||
                Interlocked.Exchange(ref lifecycle.InternalStop, 0) != 0)
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

        foreach (var name in new[] { KnownResourceCommands.StartCommand, KnownResourceCommands.StopCommand, KnownResourceCommands.RestartCommand })
        {
#pragma warning disable ASPIREWATCH001 // Lifecycle-command overrides are experimental.
            server.WithAnnotation(new ResourceLifecycleCommandOverrideAnnotation(name, original =>
            {
                if (original.Name == KnownResourceCommands.StartCommand)
                {
                    lifecycle.Start = original;
                }
                else if (original.Name == KnownResourceCommands.StopCommand)
                {
                    lifecycle.Stop = original;
                }

                return DotnetWatchMember.CloneCommand(original, _ => ResourceCommandState.Hidden,
                    _ => Task.FromResult(CommandResults.Failure(
                        "The watch run owns this watcher. Restart the complete watch run instead of controlling the server directly.")));
            }));
#pragma warning restore ASPIREWATCH001
        }

        return server;
    }

    internal static Task<ExecuteCommandResult> StartOwnedAsync(IResource server, IServiceProvider services, CancellationToken cancellationToken) =>
        ExecuteOwnedAsync(server, services, cancellationToken, start: true);

    internal static Task<ExecuteCommandResult> StopOwnedAsync(IResource server, IServiceProvider services, CancellationToken cancellationToken) =>
        ExecuteOwnedAsync(server, services, cancellationToken, start: false);

    private static Task<ExecuteCommandResult> ExecuteOwnedAsync(IResource server, IServiceProvider services, CancellationToken cancellationToken, bool start)
    {
        if (!s_lifecycles.TryGetValue(server, out var lifecycle) || (start ? lifecycle.Start : lifecycle.Stop) is not { } command)
        {
            return Task.FromResult(CommandResults.Failure($"The original watcher lifecycle callback for '{server.Name}' is unavailable."));
        }

#pragma warning disable ASPIREWATCH001 // Owned lifecycle commands need the current DCP instance identity.
        var instances = services.GetRequiredService<ResourceNotificationService>().GetCurrentStates(server);
#pragma warning restore ASPIREWATCH001
        if (instances.Count != 1)
        {
            return Task.FromResult(CommandResults.Failure(
                $"Expected one current watcher instance for '{server.Name}', but found {instances.Count}. Restart the watch run."));
        }

        if (!start)
        {
            Interlocked.Exchange(ref lifecycle.InternalStop, 1);
        }

        return command.ExecuteCommand(new ExecuteCommandContext
        {
            // Built-in callbacks address DCP instances, not logical model names.
            ResourceName = instances[0].ResourceId,
            Services = services,
            CancellationToken = cancellationToken,
            Logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(server),
            Arguments = new([])
        });
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
            "❌ The hot reload watcher '{1}' stopped{0}. Edits aren't applied. Restart the complete watch run; this group cannot downgrade to ordinary execution after build handoff.",
            exitCode is { } code ? string.Format(CultureInfo.InvariantCulture, " (exit code {0})", code) : "",
            serverName);

    internal static IEnumerable<IResource> GetDependents(DistributedApplicationModel model, IResource server) =>
        model.Resources.Where(r => r.Annotations.OfType<WaitAnnotation>().Any(w => ReferenceEquals(w.Resource, server)));

    internal static bool IsStopped(string? state) =>
        state is null || KnownResourceStates.TerminalStates.Contains(state) || state == KnownResourceStates.NotStarted || state == "Unknown";

    private static readonly HashSet<string> s_createdDirectories = new(StringComparer.Ordinal);

    internal static string CreatePrivateDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("aspw-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory.FullName, PrivateDirectoryMode);
        }
        lock (s_createdDirectories)
        {
            s_createdDirectories.Add(directory.FullName);
        }
        return directory.FullName;
    }

    internal static string CreatePrivateDirectory(string parentDirectory, string name)
    {
        var path = Path.Combine(parentDirectory, name);
        EnsurePrivateDirectory(path);
        return path;
    }

    internal static void ValidateCoreFxPipePath(string tempDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var maximumBytes = OperatingSystem.IsMacOS() ? 104 : 108;
        var candidate = Path.Combine(tempDirectory, "CoreFxPipe_" + new string('0', 32));
        var bytes = Encoding.UTF8.GetByteCount(candidate);
        if (bytes > maximumBytes)
        {
            throw new DistributedApplicationException(
                $"The watcher's temporary directory '{tempDirectory}' leaves {bytes} bytes for its CoreFx pipe path, " +
                $"exceeding this platform's {maximumBytes}-byte Unix socket limit.");
        }
    }

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

    private sealed class Lifecycle
    {
        public ResourceCommandAnnotation? Start;
        public ResourceCommandAnnotation? Stop;
        public int InternalStop;
    }
}
