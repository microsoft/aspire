// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Dcp.Process;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserHostTests
{
    [Fact]
    public async Task OwnedBrowserHost_StartAsync_UsesPipeTransportAndDeletesEndpointMetadata()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var browserExecutable = Path.Combine(userDataDirectory.FullName, "browser");
            await File.WriteAllTextAsync(browserExecutable, string.Empty);
            var devToolsActivePortPath = Path.Combine(userDataDirectory.FullName, "DevToolsActivePort");
            await File.WriteAllTextAsync(devToolsActivePortPath, "12345\n/devtools/browser/stale");

            var identity = new BrowserHostIdentity(browserExecutable, userDataDirectory.FullName);
            await BrowserEndpointDiscovery.WriteAsync(
                identity,
                profileDirectoryName: "Profile 1",
                new Uri("ws://127.0.0.1:9/devtools/browser/stale"),
                Environment.ProcessId,
                CancellationToken.None);

            FakePipeBrowserProcess? fakeProcess = null;
            IReadOnlyList<string>? capturedArguments = null;
            var host = await OwnedBrowserHost.StartAsync(
                identity,
                browserDisplayName: "Test Browser",
                BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory.FullName, profileDirectoryName: "Profile 1"),
                NullLogger<BrowserLogsSessionManager>.Instance,
                TimeProvider.System,
                CancellationToken.None,
                startPipeBrowserProcess: (executablePath, arguments) =>
                {
                    Assert.Equal(browserExecutable, executablePath);
                    capturedArguments = [.. arguments];
                    fakeProcess = new FakePipeBrowserProcess();
                    return fakeProcess;
                });

            try
            {
                Assert.Null(host.DebugEndpoint);
                Assert.Equal(4242, host.ProcessId);
                Assert.False(File.Exists(devToolsActivePortPath));
                Assert.False(File.Exists(BrowserEndpointDiscovery.GetEndpointMetadataFilePath(userDataDirectory.FullName)));

                Assert.NotNull(capturedArguments);
                Assert.Contains($"--user-data-dir={userDataDirectory.FullName}", capturedArguments);
                Assert.Contains("--profile-directory=Profile 1", capturedArguments);
                Assert.Contains("about:blank", capturedArguments);
                Assert.DoesNotContain(capturedArguments, static argument => argument.StartsWith("--remote-debugging-address=", StringComparison.Ordinal));
                Assert.DoesNotContain(capturedArguments, static argument => argument.StartsWith("--remote-debugging-port=", StringComparison.Ordinal));

                await using var connection = await host.CreateCdpConnectionAsync(
                    static _ => ValueTask.CompletedTask,
                    NullLogger<BrowserLogsSessionManager>.Instance,
                    CancellationToken.None);

                var enableDiscoveryTask = connection.EnableTargetDiscoveryAsync(CancellationToken.None);
                using var command = JsonDocument.Parse(await fakeProcess!.ReadFrameAsync().DefaultTimeout());
                Assert.Equal(BrowserLogsCdpProtocol.TargetSetDiscoverTargetsMethod, command.RootElement.GetProperty("method").GetString());

                var responseFrame = "{\"id\":" + command.RootElement.GetProperty("id").GetInt64() + ",\"result\":{}}";
                await fakeProcess.SendFrameAsync(responseFrame).DefaultTimeout();
                await enableDiscoveryTask.DefaultTimeout();
            }
            finally
            {
                await host.DisposeAsync();
            }

            Assert.True(fakeProcess?.Disposed is true);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    private sealed class FakePipeBrowserProcess : IBrowserLogsPipeBrowserProcess
    {
        private readonly Pipe _appToBrowser = new();
        private readonly Stream _browserInput;
        private readonly Stream _browserOutput;
        private readonly Stream _browserRead;
        private readonly Stream _browserWrite;
        private readonly Pipe _browserToApp = new();
        private readonly TaskCompletionSource<ProcessResult> _processCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakePipeBrowserProcess()
        {
            _browserInput = _appToBrowser.Writer.AsStream();
            _browserRead = _appToBrowser.Reader.AsStream();
            _browserOutput = _browserToApp.Reader.AsStream();
            _browserWrite = _browserToApp.Writer.AsStream();
        }

        public int ProcessId => 4242;

        public Stream BrowserOutput => _browserOutput;

        public Stream BrowserInput => _browserInput;

        public Task<ProcessResult> ProcessTask => _processCompletion.Task;

        public bool Disposed { get; private set; }

        public async Task<byte[]> ReadFrameAsync()
        {
            using var frame = new MemoryStream();
            var oneByte = new byte[1];

            while (true)
            {
                var read = await _browserRead.ReadAsync(oneByte);
                if (read == 0)
                {
                    throw new EndOfStreamException("The host pipe closed before a CDP frame was written.");
                }

                if (oneByte[0] == 0)
                {
                    return frame.ToArray();
                }

                frame.WriteByte(oneByte[0]);
            }
        }

        public async Task SendFrameAsync(string frame)
        {
            await _browserWrite.WriteAsync(Encoding.UTF8.GetBytes(frame));
            await _browserWrite.WriteAsync(new byte[] { 0 });
            await _browserWrite.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Disposed)
            {
                return;
            }

            Disposed = true;
            _processCompletion.TrySetResult(new ProcessResult(0));

            await _browserInput.DisposeAsync();
            await _browserOutput.DisposeAsync();
            await _browserRead.DisposeAsync();
            await _browserWrite.DisposeAsync();
        }
    }
}
