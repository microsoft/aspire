// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Dcp;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public sealed class DcpSocketTests
{
    [Fact]
    public async Task LoggingSocket_RestrictsPermissionsBeforeListening()
    {
        var root = Directory.CreateTempSubdirectory("aspire-dcp");
        try
        {
            var directory = root.FullName;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

            var socketPath = Path.Combine(directory, "output.sock");
            using var listener = DcpHost.CreateLoggingSocket(socketPath);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(directory));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(socketPath));
            }

            listener.Listen();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            using var accepted = await listener.AcceptAsync(timeout.Token);
            Assert.True(accepted.Connected);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
