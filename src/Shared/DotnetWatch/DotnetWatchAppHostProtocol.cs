// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Shared.DotnetWatch;

/// <summary>
/// Identifies a notification from one real AppHost process in a CLI-owned generation.
/// </summary>
internal sealed class DotnetWatchAppHostNotification
{
    public required int Version { get; init; }
    public required string Session { get; init; }
    public required int Kind { get; init; }
    public required string Instance { get; init; }
}

/// <summary>
/// Provides bounded framing for the private AppHost generation channel, not the service proxy protocol.
/// </summary>
internal static class DotnetWatchAppHostProtocol
{
    internal const int Version = 1;
    internal const int MaximumMessageBytes = 4096;
    internal const int ModelInvalidated = 0;
    internal const int HostStarted = 1;

    internal static async Task WriteAsync(Stream stream, DotnetWatchAppHostNotification message, CancellationToken cancellationToken)
    {
        Validate(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, DotnetWatchAppHostJsonContext.Default.DotnetWatchAppHostNotification);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The AppHost watch notification exceeds the supported size.");
        }

        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<DotnetWatchAppHostNotification> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Private framing is a four-byte little-endian length followed by one UTF-8 JSON object:
        // {"Version":1,"Session":"<32 hex digits>","Kind":0,"Instance":"1234:567890"}.
        // Instance is PID plus a stable process-start identity, not just a reusable PID.
        var length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaximumMessageBytes)
        {
            throw new InvalidDataException("Invalid AppHost watch notification length.");
        }

        var payload = new byte[count];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
                document.RootElement.EnumerateObject().Count())
        {
            throw new InvalidDataException("The AppHost watch notification must be an object without duplicate fields.");
        }

        var message = JsonSerializer.Deserialize(payload, DotnetWatchAppHostJsonContext.Default.DotnetWatchAppHostNotification)
            ?? throw new InvalidDataException("The AppHost watch notification is empty.");
        Validate(message);

        return message;
    }

    internal static bool IsSession([NotNullWhen(true)] string? session)
        => session is { Length: 32 } && Guid.TryParseExact(session, "N", out _);

    internal static bool IsInstance(string? instance) =>
        TryParseInstance(instance, out _, out _);

    internal static bool TryParseInstance(
        string? instance,
        out int processId,
        out long startTimeUnixMilliseconds)
    {
        processId = 0;
        startTimeUnixMilliseconds = 0;
        if (instance is not { Length: > 0 and <= 64 })
        {
            return false;
        }

        var separator = instance.IndexOf(':');
        return separator > 0 &&
            int.TryParse(instance.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0 &&
            long.TryParse(instance.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out startTimeUnixMilliseconds) &&
            startTimeUnixMilliseconds > 0;
    }

    internal static async Task<Stream> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void Validate(DotnetWatchAppHostNotification message)
    {
        if (message.Version != Version ||
            message.Kind is not (ModelInvalidated or HostStarted) ||
            !IsSession(message.Session) ||
            !IsInstance(message.Instance))
        {
            throw new InvalidDataException("Invalid AppHost watch notification version, kind, or identity.");
        }
    }
}

[JsonSerializable(typeof(DotnetWatchAppHostNotification))]
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8)]
internal sealed partial class DotnetWatchAppHostJsonContext : JsonSerializerContext;
