// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using Aspire.Shared;

[assembly: MetadataUpdateHandler(typeof(Aspire.Hosting.Watch.DotnetWatchMetadataUpdateHandler))]

namespace Aspire.Hosting.Watch;

/// <summary>
/// Invalidates the CLI-owned C# AppHost model after a metadata update.
/// </summary>
internal static class DotnetWatchMetadataUpdateHandler
{
    private static int s_notifying;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Only explicit CLI-owned package-host watch generations announce startup; ordinary and IDE hosts do nothing.")]
    internal static void Initialize() => _ = NotifyStartedAsync(Environment.GetEnvironmentVariable, CancellationToken.None);

    internal static void UpdateApplication(Type[]? updatedTypes)
    {
        _ = updatedTypes;
        if (Interlocked.CompareExchange(ref s_notifying, 1, 0) == 0)
        {
            _ = NotifyAndResetAsync();
        }
    }

    private static async Task NotifyAndResetAsync()
    {
        try
        {
            await NotifyAsync(Environment.GetEnvironmentVariable, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref s_notifying, 0);
        }
    }

    internal static Task NotifyAsync(Func<string, string?> getEnvironmentVariable, CancellationToken cancellationToken)
        => NotifyAsync(getEnvironmentVariable, DotnetWatchHostProtocol.ModelInvalidated, cancellationToken);

    internal static Task NotifyStartedAsync(Func<string, string?> getEnvironmentVariable, CancellationToken cancellationToken)
        => NotifyAsync(getEnvironmentVariable, DotnetWatchHostProtocol.HostStarted, cancellationToken);

    private static async Task NotifyAsync(Func<string, string?> getEnvironmentVariable, int kind, CancellationToken cancellationToken)
    {
        var enabled = getEnvironmentVariable(DotnetWatchConfiguration.WatchEnabled.Replace(":", "__", StringComparison.Ordinal));
        if (getEnvironmentVariable(DotnetWatchConfiguration.LaunchOwner) != DotnetWatchConfiguration.OwnerValue ||
            !bool.TryParse(enabled, out var watchEnabled) || !watchEnabled ||
            getEnvironmentVariable(DotnetWatchConfiguration.HostControlPipe) is not { Length: > 0 } pipeName ||
            getEnvironmentVariable(DotnetWatchConfiguration.HostSession) is not { Length: > 0 } session)
        {
            return;
        }

        try
        {
            // Metadata updates change method bodies, not the already-created application model.
            // Notify the CLI instead of exiting: the pinned wrapper can wait forever after its
            // child exits without another edit. The CLI replaces the entire owned generation.
            // https://learn.microsoft.com/dotnet/api/system.reflection.metadata.metadataupdatehandlerattribute
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            string? instance = null;
            if (kind == DotnetWatchHostProtocol.HostStarted)
            {
                // Rude edits can make the upstream watcher restart its child without invoking a
                // metadata handler. Announce real starts, not exits, so the CLI can replace that
                // generation and refresh its backchannel without reviving a normally stopped host.
                // Process identity also makes duplicate loads in another AssemblyLoadContext benign.
                using var process = Process.GetCurrentProcess();
                instance = $"{process.Id.ToString(CultureInfo.InvariantCulture)}:{process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}";
            }
            await DotnetWatchHostProtocol.WriteAsync(pipe, new() { Session = session, Kind = kind, Instance = instance }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or ArgumentException)
        {
            // A disappearing CLI is a normal shutdown race. Never turn a metadata update into
            // an unhandled exception or alter ordinary/IDE metadata-update behavior.
        }
    }
}
