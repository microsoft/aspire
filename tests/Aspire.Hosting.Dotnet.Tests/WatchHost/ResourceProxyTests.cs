// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias WatchHost;

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using WatchHost::Aspire.Hosting.Dotnet.WatchHost;
using WatchHost::Aspire.Shared.DotnetWatch;

namespace Aspire.Hosting.Dotnet.Tests.WatchHost;

// The watch host's `proxy` command (docs/plans/project-v2-csharpprogram-watch.md).
public class ResourceProxyTests
{
    [Fact]
    public void ParsesServerEntryPointAndApplicationArguments()
    {
        string[] args = ["--server", "/tmp/aspw/res.sock", "--entrypoint", "/src/Api/Api.csproj", "--", "--urls", "http://localhost:5000", "--"];

        Assert.True(ResourceProxy.TryParse(args, out var options, out var error));
        Assert.Null(error);
        Assert.Equal("/tmp/aspw/res.sock", options.Server);
        Assert.Equal("/src/Api/Api.csproj", options.EntryPoint);
        Assert.Null(options.WorkingDirectory);
        Assert.Null(options.LaunchGate);
        Assert.Equal(["--urls", "http://localhost:5000", "--"], options.AppArgs);
    }

    [Theory]
    [InlineData(new[] { "--server", "s" }, "--server and --entrypoint are required.")]
    [InlineData(new[] { "--entrypoint", "e" }, "--server and --entrypoint are required.")]
    [InlineData(new[] { "--server", "s", "--entrypoint", "e", "--bogus" }, "unexpected argument '--bogus'.")]
    public void RejectsInvalidArguments(string[] args, string expectedError)
    {
        Assert.False(ResourceProxy.TryParse(args, out _, out var error));
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void ForwardsOnlyTheNamedEnvironmentVariables()
    {
        var environment = new Dictionary<string, string>
        {
            ["GREETING_SOURCE"] = "apphost",
            ["ASPNETCORE_URLS"] = "http://localhost:5380",
            ["PATH"] = "/usr/bin",
            ["SECRET_FROM_PARENT_SHELL"] = "nope",
        };

        var forwarded = ResourceProxy.CollectForwardedEnvironment(
            "GREETING_SOURCE;ASPNETCORE_URLS;;MISSING", name => environment.GetValueOrDefault(name));

        Assert.Equal(2, forwarded.Count);
        Assert.Equal("apphost", forwarded["GREETING_SOURCE"]);
        Assert.Equal("http://localhost:5380", forwarded["ASPNETCORE_URLS"]);
        Assert.Empty(ResourceProxy.CollectForwardedEnvironment(null, name => environment.GetValueOrDefault(name)));
    }

    [Fact]
    public void ForwardsCertificateSettingsAddedAfterEnvironmentCallbacks()
    {
        var environment = new Dictionary<string, string>
        {
            ["SSL_CERT_DIR"] = "/certs",
            ["Kestrel__Certificates__Default__Path"] = "/private/a.crt",
            ["ASPNETCORE_Kestrel__Certificates__Default__KeyPath"] = "/private/a.key",
            ["HOME"] = "/root",
        };

        var forwarded = ResourceProxy.CollectForwardedEnvironment(
            "GREETING_SOURCE", name => environment.GetValueOrDefault(name), environment.Keys);

        Assert.Equal(["ASPNETCORE_Kestrel__Certificates__Default__KeyPath", "Kestrel__Certificates__Default__Path", "SSL_CERT_DIR"], forwarded.Keys.Order());
    }

    [Fact]
    public void EmptyApplicationArgumentsStayEmpty()
    {
        var json = ResourceProxy.CreateRequestJson("/src/Api/Api.csproj", [], [], new Dictionary<string, string>());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("ApplicationArguments").GetArrayLength());
    }

    [Fact]
    public void RequestJsonMatchesTheWatcherProtocol()
    {
        var json = ResourceProxy.CreateRequestJson(
            "/src/Api/Api.csproj", [], ["--flag", "value with spaces"], new Dictionary<string, string> { ["A"] = "1" });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("/src/Api/Api.csproj", root.GetProperty("EntryPoint").GetString());
        Assert.Equal(["--", "--flag", "value with spaces"], root.GetProperty("ApplicationArguments").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("1", root.GetProperty("EnvironmentVariables").GetProperty("A").GetString());

        // Optional<string?> on the watcher side: the object form with HasValue=false means "no launch profile".
        var launchProfile = root.GetProperty("LaunchProfileName");
        Assert.False(launchProfile.GetProperty("HasValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, launchProfile.GetProperty("Value").ValueKind);
    }

    [Fact]
    public void ParsesWorkingDirectoryAndLaunchGate()
    {
        string[] args = ["--server", "s", "--entrypoint", "/src/Api/Api.csproj", "--launch-gate", "/tmp/aspw/launched0-1-AB", "--working-directory", "/data/run dir", "--"];

        Assert.True(ResourceProxy.TryParse(args, out var options, out _));
        Assert.Equal("/data/run dir", options.WorkingDirectory);
        Assert.Equal("/tmp/aspw/launched0-1-AB", options.LaunchGate);
        Assert.Empty(options.AppArgs);
    }

    [Fact]
    public void RunOptionsCarryWorkingDirectoryAndReplicaMarkerBeforeApplicationArguments()
    {
        var options = ResourceProxy.GetRunOptions("/src/Api/Api.csproj", "/data/a;b,c%d", "api-abcd1234");

        Assert.Equal(["--project", "/src/Api/Api.csproj", "-p:RunWorkingDirectory=/data/a%3Bb%2Cc%25d", "-e", "ASPIRE_DOTNET_WATCH_INSTANCE=api-abcd1234"], options);

        var json = ResourceProxy.CreateRequestJson("/src/Api/Api.csproj", options, ["--urls", "x"], new Dictionary<string, string>());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["--project", "/src/Api/Api.csproj", "-p:RunWorkingDirectory=/data/a%3Bb%2Cc%25d", "-e", "ASPIRE_DOTNET_WATCH_INSTANCE=api-abcd1234", "--", "--urls", "x"],
            document.RootElement.GetProperty("ApplicationArguments").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api-{{- index .Annotations \"otel-service-instance-id\" -}}")]
    public void UnexpandedOrMissingReplicaMarkerIsOmitted(string? instance)
    {
        Assert.Equal(["--project", "/src/Api/Api.csproj"], ResourceProxy.GetRunOptions("/src/Api/Api.csproj", null, instance));
    }

    [Theory]
    [InlineData("/src/Api/Api.csproj")]
    [InlineData("/src/Api/Api.FSPROJ")]
    [InlineData("/src/Api/Api.vbproj")]
    public void RunOptionsSelectTheProjectExplicitly(string entryPoint)
    {
        // The watcher runs `dotnet run --no-build` in the project's directory; without --project a directory with a
        // second project fails with "Specify which project file to use".
        var options = ResourceProxy.GetRunOptions(entryPoint, null, null);

        Assert.Equal(["--project", entryPoint], options);
    }

    [Fact]
    public void RunOptionsDoNotSelectANonProjectEntryPoint()
    {
        Assert.Empty(ResourceProxy.GetRunOptions("/src/App/app.cs", null, null));
    }

    [Fact]
    public void WatcherOnlyVariablesAreRestoredOrUnsetForTheService()
    {
        var proxyEnvironment = new Dictionary<string, string> { ["TMPDIR"] = "/tmp/user", ["Configuration"] = "from-shell" };
        var environment = new Dictionary<string, string> { ["Configuration"] = "from-app-model" };

        ResourceProxy.AddRestoredEnvironment(environment, "TMPDIR;Configuration;BUILD_ONLY;", name => proxyEnvironment.GetValueOrDefault(name));

        Assert.Equal("/tmp/user", environment["TMPDIR"]);
        Assert.Equal("from-app-model", environment["Configuration"]);
        Assert.Equal("", environment["BUILD_ONLY"]);
    }

    [Fact]
    public void StopSignalsAreSupportedOnEveryPlatform()
    {
        // PosixSignalRegistration maps these to console control events on Windows (Ctrl+C, Ctrl+Break, close, shutdown).
        Assert.Equal([PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP], ShutdownSignal.Signals);
        using var signal = new ShutdownSignal();
        Assert.False(signal.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(70_000)]
    public async Task StringFramingRoundTrips(int length)
    {
        var value = new string('é', length);
        using var stream = new MemoryStream();

        await ResourceProxy.WriteStringAsync(stream, value, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(value, await ResourceProxy.ReadStringAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadingATruncatedFrameThrows()
    {
        using var stream = new MemoryStream([0x05, (byte)'a']);
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => ResourceProxy.ReadStringAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ForwardsOutputUntilTheServerDisconnects()
    {
        using var testPipe = TestPipe.Create();
        var pipeName = testPipe.Name;
        string? request = null;
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            Assert.Equal(1, pipe.ReadByte());
            request = await ResourceProxy.ReadStringAsync(pipe, CancellationToken.None);
            pipe.WriteByte(1);
            pipe.WriteByte(1);
            await ResourceProxy.WriteStringAsync(pipe, "started", CancellationToken.None);
            await pipe.FlushAsync();
        });

        var exitCode = await ResourceProxy.RunAsync(["--server", pipeName, "--entrypoint", "/src/Api/Api.csproj"]).WaitAsync(TimeSpan.FromSeconds(30));
        await server.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ResourceProxy.ExitServerDisconnected, exitCode);
        Assert.NotNull(request);
        using var document = JsonDocument.Parse(request);
        Assert.Equal("/src/Api/Api.csproj", document.RootElement.GetProperty("EntryPoint").GetString());
    }

    [Fact]
    public async Task ConnectTimesOutWithoutAServer()
    {
        Environment.SetEnvironmentVariable(DotnetWatchHostProtocol.ConnectTimeoutVariable, "1");
        try
        {
            var exitCode = await ResourceProxy.RunAsync(["--server", $"aspire-proxy-test-{Guid.NewGuid():N}", "--entrypoint", "/src/Api/Api.csproj"])
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ResourceProxy.ExitConnectFailed, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetWatchHostProtocol.ConnectTimeoutVariable, null);
        }
    }

    [Fact]
    public async Task FailsWhenTheServerRejectsTheRequest()
    {
        using var testPipe = TestPipe.Create();
        var pipeName = testPipe.Name;
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            Assert.Equal(1, pipe.ReadByte());
            await ResourceProxy.ReadStringAsync(pipe, CancellationToken.None);
            pipe.WriteByte(0);
            await pipe.FlushAsync();
        });

        var exitCode = await ResourceProxy.RunAsync(["--server", pipeName, "--entrypoint", "/src/Api/Api.csproj"]).WaitAsync(TimeSpan.FromSeconds(30));
        await server.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ResourceProxy.ExitRejected, exitCode);
    }

    [Fact]
    public async Task LaunchGateAdmitsOneLaunchUntilTheProjectHasLaunched()
    {
        var directory = Directory.CreateTempSubdirectory("aspire-launch-gate-");
        try
        {
            var marker = Path.Combine(directory.FullName, "launched0-1-AB");
            var first = await LaunchGate.EnterAsync(marker, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(first);

            var second = LaunchGate.EnterAsync(marker, CancellationToken.None);
            var release = first.ReleaseAfterLaunchAsync(new TaskCompletionSource().Task);
            await Task.Delay(200);
            Assert.False(second.IsCompleted);
            Assert.False(release.IsCompleted);

            await File.WriteAllBytesAsync(marker, []);
            await release.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Null(await LaunchGate.EnterAsync(marker, CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LaunchGatePassesOnWhenTheLaunchConnectionEnds()
    {
        var directory = Directory.CreateTempSubdirectory("aspire-launch-gate-");
        try
        {
            var marker = Path.Combine(directory.FullName, "launched0-1-AB");
            var first = await LaunchGate.EnterAsync(marker, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(first);

            var second = LaunchGate.EnterAsync(marker, CancellationToken.None);
            var connection = new TaskCompletionSource();
            var release = first.ReleaseAfterLaunchAsync(connection.Task);
            connection.SetResult();
            await release.WaitAsync(TimeSpan.FromSeconds(30));

            using var next = await second.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(next);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LaunchGateWaitIsCancelable()
    {
        var directory = Directory.CreateTempSubdirectory("aspire-launch-gate-");
        try
        {
            var marker = Path.Combine(directory.FullName, "launched0-1-AB");
            using var first = await LaunchGate.EnterAsync(marker, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            using var cts = new CancellationTokenSource();
            var second = LaunchGate.EnterAsync(marker, cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A pipe name the server and the proxy agree on. On Unix a named pipe is a socket file, and a bare name is placed
    /// under <c>$TMPDIR</c>, which on macOS is long enough to exceed the socket path limit (104 bytes); like the watch
    /// server's own pipes, use a short absolute socket path there.
    /// </summary>
    private sealed class TestPipe : IDisposable
    {
        private TestPipe(string name) => Name = name;

        public string Name { get; }

        public static TestPipe Create() => new(OperatingSystem.IsWindows()
            ? $"aspire-proxy-test-{Guid.NewGuid():N}"
            : Path.Combine("/tmp", $"apx-{Guid.NewGuid().ToString("N")[..12]}.sock"));

        public void Dispose()
        {
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(Name);
            }
        }
    }
}
