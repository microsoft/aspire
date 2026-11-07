// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;

namespace Aspire.Tests.Utils;

internal sealed class DotnetWatchTestDirectory : IDisposable
{
    internal DotnetWatchTestDirectory()
    {
        // Relative test endpoints fit AF_UNIX's small byte limit even in long worktree paths.
        // All peers run in this process; no test changes the process working directory.
        RelativePath = $".w{Guid.NewGuid():N}"[..10];
        Directory = DirectoryHelper.CreateWithOwnerOnlyPermissions(RelativePath);
    }

    internal string RelativePath { get; }
    internal DirectoryInfo Directory { get; }

    public void Dispose() => Directory.Delete(recursive: true);
}

internal sealed class DotnetWatchAppHostTestChannel : IAsyncDisposable
{
    private readonly DotnetWatchTestDirectory _directory = new();
    private readonly NamedPipeServerStream? _pipe;
    private readonly Socket? _socket;

    internal DotnetWatchAppHostTestChannel()
    {
        Session = Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows())
        {
            Endpoint = $"ah{Session}";
            _pipe = new NamedPipeServerStream(Endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        else
        {
            Endpoint = Path.Combine(_directory.RelativePath, "n");
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            SocketPermissionHelper.Bind(_socket, Endpoint);
            _socket.Listen(1);
        }
    }

    internal string Endpoint { get; }
    internal string Session { get; }

    internal async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { } pipe)
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }

        Socket socket;
        if (OperatingSystem.IsMacOS() && Environment.Version.Major == 10)
        {
            // NET10's async Accept can fail with "Destination is too short" while copying an
            // unnamed Unix peer address into the short relative endpoint's SocketAddress.
            // The test process can have an overlong binary-directory cwd, so an absolute
            // endpoint is not an alternative. Synchronous Accept avoids that copy; disposing
            // the listener on cancellation keeps the test-only blocking worker bounded.
            // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.cs
            using var stopAccept = cancellationToken.Register(static state => ((Socket)state!).Dispose(), _socket);
            socket = await Task.Run(() => _socket!.Accept(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            socket = await _socket!.AcceptAsync(cancellationToken).ConfigureAwait(false);
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    internal static async Task SendAsync(string endpoint, DotnetWatchAppHostNotification message, CancellationToken cancellationToken)
    {
        await using var client = await DotnetWatchAppHostProtocol.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await DotnetWatchAppHostProtocol.WriteAsync(client, message, cancellationToken).ConfigureAwait(false);
        await WaitForDisconnectAsync(client, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SendFrameAsync(string endpoint, byte[] frame, CancellationToken cancellationToken)
    {
        await using var client = await DotnetWatchAppHostProtocol.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await client.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);
        await WaitForDisconnectAsync(client, cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));

        return frame;
    }

    private static async Task WaitForDisconnectAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            // EOF (or Windows' broken-pipe result) is a processing barrier: the generation has
            // finished handling this connection, so negative assertions need no timing delays.
            _ = await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is { } pipe)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        _socket?.Dispose();
        _directory.Dispose();
    }
}
