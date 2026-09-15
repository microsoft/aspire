// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Aspire.TestUtilities;
using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Go.Tests;

public class GoTransportTests
{
    [Fact]
    [RequiresTools(["go"])]
    public async Task GeneratedTransport_SupportsDuplexCommunication()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var process = new Process();
        try
        {
            var files = new AtsGoCodeGenerator().GenerateDistributedApplication(new AtsContext
            {
                Capabilities = [],
                HandleTypes = [],
                EnumTypes = [],
                DtoTypes = []
            });
            foreach (var (name, content) in files)
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name), content, timeout.Token);
            }

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "transport_test.go"), """
                package aspire

                import (
                    "io"
                    "os"
                    "testing"
                    "time"
                )

                func TestDuplexConnection(t *testing.T) {
                    conn, err := openConnection(os.Getenv("TEST_SOCKET_PATH"), 5*time.Second)
                    if err != nil {
                        t.Fatal(err)
                    }
                    defer conn.Close()

                    // Deadline support proves the Windows handle is pollable rather than
                    // synchronous, without relying on which I/O goroutine runs first.
                    deadlines, ok := conn.(interface { SetDeadline(time.Time) error })
                    if !ok {
                        t.Fatal("connection does not support deadlines")
                    }
                    if err := deadlines.SetDeadline(time.Now().Add(10*time.Second)); err != nil {
                        t.Fatal(err)
                    }

                    received := make(chan error, 1)
                    go func() {
                        response := make([]byte, 4)
                        _, err := io.ReadFull(conn, response)
                        if err == nil && string(response) != "pong" {
                            t.Errorf("expected pong, got %q", response)
                        }
                        received <- err
                    }()
                    if _, err := conn.Write([]byte("ping")); err != nil {
                        t.Fatal(err)
                    }
                    if err := <-received; err != nil {
                        t.Fatal(err)
                    }

                    // Closing a connection must release a pending background read.
                    go func() {
                        _, err := conn.Read(make([]byte, 1))
                        received <- err
                    }()
                    if err := conn.Close(); err != nil {
                        t.Fatal(err)
                    }
                    select {
                    case err := <-received:
                        if err == nil {
                            t.Fatal("expected the read to fail after close")
                        }
                    case <-time.After(5*time.Second):
                        t.Fatal("close did not unblock the reader")
                    }
                }
                """, timeout.Token);

            var socketPath = OperatingSystem.IsWindows()
                ? $"aspire-go-{Guid.NewGuid():N}"
                : Path.Combine(directory.FullName, "socket");
            using var pipe = OperatingSystem.IsWindows()
                ? new NamedPipeServerStream(socketPath, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
                : null;
            using var socket = OperatingSystem.IsWindows()
                ? null
                : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            if (socket is not null)
            {
                socket.Bind(new UnixDomainSocketEndPoint(socketPath));
                socket.Listen(1);
            }

            process.StartInfo = new ProcessStartInfo("go")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "test", "-timeout", "30s", "." },
                Environment = { ["TEST_SOCKET_PATH"] = socketPath }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var exchange = ExchangeAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, $"Go transport test failed:{Environment.NewLine}{await stdout}{await stderr}");
                await exchange;
            }
            finally
            {
                await timeout.CancelAsync();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            async Task ExchangeAsync()
            {
                using var stream = pipe is not null
                    ? await AcceptPipeAsync()
                    : new NetworkStream(await socket!.AcceptAsync(timeout.Token), ownsSocket: true);
                var request = new byte[4];
                await stream.ReadExactlyAsync(request, timeout.Token);
                Assert.Equal("ping"u8.ToArray(), request);
                await stream.WriteAsync("pong"u8.ToArray(), timeout.Token);

                // Keep the server connected until the client closes its pending read.
                Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
            }

            async Task<Stream> AcceptPipeAsync()
            {
                await pipe!.WaitForConnectionAsync(timeout.Token);
                return pipe;
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
