// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using Aspire.Shared.DotnetWatch;

[assembly: MetadataUpdateHandler(typeof(Aspire.Hosting.Watch.DotnetWatchMetadataUpdateHandler))]

namespace Aspire.Hosting.Watch;

/// <summary>
/// Announces real CLI-owned AppHost starts and invalidates the constructed model after metadata updates.
/// </summary>
internal static class DotnetWatchMetadataUpdateHandler
{
    private static int s_notifying;
    private static readonly Lazy<string> s_instance = new(() =>
        $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}:{ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds().ToString(CultureInfo.InvariantCulture)}");

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Only explicit CLI-owned AppHost watch generations announce startup; ordinary and IDE hosts do nothing.")]
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
            // A startup notification may race or fail while the CLI is still listening. Reannounce
            // this same process before invalidation; duplicates are benign and establish its identity.
            await NotifyStartedAsync(Environment.GetEnvironmentVariable, CancellationToken.None).ConfigureAwait(false);
            await NotifyAsync(Environment.GetEnvironmentVariable, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref s_notifying, 0);
        }
    }

    internal static Task NotifyAsync(Func<string, string?> getEnvironmentVariable, CancellationToken cancellationToken)
        => NotifyAsync(getEnvironmentVariable, DotnetWatchAppHostProtocol.ModelInvalidated, cancellationToken);

    internal static Task NotifyStartedAsync(Func<string, string?> getEnvironmentVariable, CancellationToken cancellationToken)
        => NotifyAsync(getEnvironmentVariable, DotnetWatchAppHostProtocol.HostStarted, cancellationToken);

    private static async Task NotifyAsync(Func<string, string?> getEnvironmentVariable, int kind, CancellationToken cancellationToken)
    {
        var session = getEnvironmentVariable(DotnetWatchOwnership.AppHostSessionEnvironmentVariable);
        if (!DotnetWatchOwnership.IsCliOwned(getEnvironmentVariable(DotnetWatchOwnership.OwnerEnvironmentVariable)) ||
            !string.IsNullOrEmpty(getEnvironmentVariable(KnownConfigNames.ExtensionDebugSessionId)) ||
            getEnvironmentVariable(DotnetWatchOwnership.AppHostControlEnvironmentVariable) is not { Length: > 0 } endpoint ||
            !DotnetWatchAppHostProtocol.IsSession(session))
        {
            return;
        }

        try
        {
            // Metadata updates change method bodies, not the model that already ran startup code.
            // Ask the CLI to replace the complete generation instead of assuming startup reruns.
            // https://learn.microsoft.com/dotnet/api/system.reflection.metadata.metadataupdatehandlerattribute
            // A distinct startup also detects upstream rude-edit child replacement without relying
            // on backchannel disconnects or reviving a normally stopped host.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var channel = await DotnetWatchAppHostProtocol.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            await DotnetWatchAppHostProtocol.WriteAsync(channel, new()
            {
                Version = DotnetWatchAppHostProtocol.Version,
                Session = session,
                Kind = kind,
                Instance = s_instance.Value
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            // CLI disappearance is an ordinary shutdown race. Never let a private notification
            // fault the application's metadata update or affect unowned/IDE hot reload.
        }
    }
}
