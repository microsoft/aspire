// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.DotnetWatch;

// SPIKE: `aspire run --watch` (docs/plans/project-v2-csharpprogram-watch.md).
public class DotnetWatchResourceProxyTests
{
    [Fact]
    public void ParsesServerEntryPointAndApplicationArguments()
    {
        string[] args = [DotnetWatchResourceProxy.CommandName, "--server", "/tmp/aspw/res.sock", "--entrypoint", "/src/Api/Api.csproj", "--", "--urls", "http://localhost:5000", "--"];

        Assert.True(DotnetWatchResourceProxy.Matches(args));
        Assert.True(DotnetWatchResourceProxy.TryParse(args, out var server, out var entryPoint, out var workingDirectory, out var appArgs, out var error));
        Assert.Null(error);
        Assert.Equal("/tmp/aspw/res.sock", server);
        Assert.Equal("/src/Api/Api.csproj", entryPoint);
        Assert.Null(workingDirectory);
        Assert.Equal(["--urls", "http://localhost:5000", "--"], appArgs);
    }

    [Theory]
    [InlineData(new[] { "__dotnet-watch-proxy", "--server", "s" }, "--server and --entrypoint are required.")]
    [InlineData(new[] { "__dotnet-watch-proxy", "--entrypoint", "e" }, "--server and --entrypoint are required.")]
    [InlineData(new[] { "__dotnet-watch-proxy", "--server", "s", "--entrypoint", "e", "--bogus" }, "unexpected argument '--bogus'.")]
    public void RejectsInvalidArguments(string[] args, string expectedError)
    {
        Assert.False(DotnetWatchResourceProxy.TryParse(args, out _, out _, out _, out _, out var error));
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

        var forwarded = DotnetWatchResourceProxy.CollectForwardedEnvironment(
            "GREETING_SOURCE;ASPNETCORE_URLS;;MISSING", name => environment.GetValueOrDefault(name));

        Assert.Equal(2, forwarded.Count);
        Assert.Equal("apphost", forwarded["GREETING_SOURCE"]);
        Assert.Equal("http://localhost:5380", forwarded["ASPNETCORE_URLS"]);
        Assert.Empty(DotnetWatchResourceProxy.CollectForwardedEnvironment(null, name => environment.GetValueOrDefault(name)));
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

        var forwarded = DotnetWatchResourceProxy.CollectForwardedEnvironment(
            "GREETING_SOURCE", name => environment.GetValueOrDefault(name), environment.Keys);

        Assert.Equal(["ASPNETCORE_Kestrel__Certificates__Default__KeyPath", "Kestrel__Certificates__Default__Path", "SSL_CERT_DIR"], forwarded.Keys.Order());
    }

    [Fact]
    public void EmptyApplicationArgumentsStayEmpty()
    {
        var json = DotnetWatchResourceProxy.CreateRequestJson("/src/Api/Api.csproj", [], new Dictionary<string, string>());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("ApplicationArguments").GetArrayLength());
    }

    [Fact]
    public void RequestJsonMatchesTheWatcherProtocol()
    {
        var json = DotnetWatchResourceProxy.CreateRequestJson(
            "/src/Api/Api.csproj", ["--flag", "value with spaces"], new Dictionary<string, string> { ["A"] = "1" });

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
    public void ParsesWorkingDirectory()
    {
        string[] args = [DotnetWatchResourceProxy.CommandName, "--server", "s", "--entrypoint", "/src/Api/Api.csproj", "--working-directory", "/data/run dir", "--"];

        Assert.True(DotnetWatchResourceProxy.TryParse(args, out _, out _, out var workingDirectory, out var appArgs, out _));
        Assert.Equal("/data/run dir", workingDirectory);
        Assert.Empty(appArgs);
    }

    [Fact]
    public void RunOptionsCarryWorkingDirectoryAndReplicaMarkerBeforeApplicationArguments()
    {
        var options = DotnetWatchResourceProxy.GetRunOptions("/data/a;b,c%d", "api-abcd1234");

        Assert.Equal(["-p:RunWorkingDirectory=/data/a%3Bb%2Cc%25d", "-e", "ASPIRE_DOTNET_WATCH_INSTANCE=api-abcd1234"], options);

        var json = DotnetWatchResourceProxy.CreateRequestJson("/src/Api/Api.csproj", options, ["--urls", "x"], new Dictionary<string, string>());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["-p:RunWorkingDirectory=/data/a%3Bb%2Cc%25d", "-e", "ASPIRE_DOTNET_WATCH_INSTANCE=api-abcd1234", "--", "--urls", "x"],
            document.RootElement.GetProperty("ApplicationArguments").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("api-{{- index .Annotations \"otel-service-instance-id\" -}}")]
    public void UnexpandedOrMissingReplicaMarkerIsOmitted(string? instance)
    {
        Assert.Empty(DotnetWatchResourceProxy.GetRunOptions(null, instance));
    }

    [Fact]
    public void WatcherOnlyVariablesAreRestoredOrUnsetForTheService()
    {
        var proxyEnvironment = new Dictionary<string, string> { ["TMPDIR"] = "/tmp/user", ["Configuration"] = "from-shell" };
        var environment = new Dictionary<string, string> { ["Configuration"] = "from-app-model" };

        DotnetWatchResourceProxy.AddRestoredEnvironment(environment, "TMPDIR;Configuration;BUILD_ONLY;", name => proxyEnvironment.GetValueOrDefault(name));

        Assert.Equal("/tmp/user", environment["TMPDIR"]);
        Assert.Equal("from-app-model", environment["Configuration"]);
        Assert.Equal("", environment["BUILD_ONLY"]);
    }

    [Fact]
    public void ServerHostParsesTheWrappedCommand()
    {
        string[] args = [DotnetWatchServerHost.CommandName, "--", "/usr/bin/dotnet", "exec", "watch.dll", "server"];

        Assert.True(DotnetWatchServerHost.Matches(args));
        Assert.True(DotnetWatchServerHost.TryParse(args, out var fileName, out var arguments, out var error));
        Assert.Null(error);
        Assert.Equal("/usr/bin/dotnet", fileName);
        Assert.Equal(["exec", "watch.dll", "server"], arguments);

        Assert.False(DotnetWatchServerHost.TryParse([DotnetWatchServerHost.CommandName, "/usr/bin/dotnet"], out _, out _, out error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(null, 1500)]
    [InlineData("250", 250)]
    [InlineData("0", 0)]
    [InlineData("-5", 1500)]
    [InlineData("soon", 1500)]
    public void ServerHostShutdownGraceDefaultsWhenInvalid(string? value, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), DotnetWatchServerHost.GetShutdownGrace(value ?? ""));
    }

    [Fact]
    public void ServerHostRegistersStopSignalsSupportedOnEveryPlatform()
    {
        // PosixSignalRegistration maps these to console control events on Windows (Ctrl+C, Ctrl+Break, close, shutdown).
        Assert.Equal([PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP], DotnetWatchServerHost.ShutdownSignals);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses /bin/sh to create a process tree.")]
    public async Task ServerHostKillsTheWholeProcessTreeAfterTheGracePeriod()
    {
        // A parent that ignores SIGTERM (like the watcher under DCP) with a child that would outlive it.
        var pidFile = Path.Combine(Path.GetTempPath(), $"aspire-watch-host-{Guid.NewGuid():N}.pid");
        var startInfo = new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"trap '' TERM; sleep 60 & echo $! > {pidFile}; wait" },
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "child did not start");
                await Task.Delay(50);
            }

            var childId = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await DotnetWatchServerHost.StopAsync(process, process.WaitForExitAsync(), TimeSpan.FromMilliseconds(200));

            Assert.True(process.HasExited);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsRunning(childId))
            {
                Assert.True(DateTime.UtcNow < deadline, $"child {childId} survived");
                await Task.Delay(50);
            }
        }
        finally
        {
            File.Delete(pidFile);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        static bool IsRunning(int id)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(id);
                return !p.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
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

        await DotnetWatchResourceProxy.WriteStringAsync(stream, value, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(value, await DotnetWatchResourceProxy.ReadStringAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadingATruncatedFrameThrows()
    {
        using var stream = new MemoryStream([0x05, (byte)'a']);
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => DotnetWatchResourceProxy.ReadStringAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void RunEnvironmentEnablesWatchAndPointsAtThisCli()
    {
        var environment = new Dictionary<string, string>();

        DotnetWatchRunEnvironment.Apply(environment);

        Assert.Equal("true", environment[DotnetWatchRunEnvironment.WatchEnabledVariable]);
        Assert.Equal("2", environment[DotnetWatchRunEnvironment.ProxyProtocolVariable]);
        var path = environment[DotnetWatchRunEnvironment.ProxyPathVariable];
        Assert.True(File.Exists(path), path);
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(environment.ContainsKey(DotnetWatchRunEnvironment.ProxyHostVariable));
        }
    }

    [Theory]
    [InlineData(LogLevel.Information, "🔥 Hot reload applied to api in 42 ms (Program.cs).", "🔥 Hot reload applied to api in 42 ms (Program.cs).")]
    [InlineData(LogLevel.Error, "❌ Build error in api: Program.cs(8,43): CS0117: [x]", "[red]❌ Build error in api: Program.cs(8,43): CS0117: [[x]][/]")]
    [InlineData(LogLevel.Warning, "📦 Api.csproj changed.", "[yellow]📦 Api.csproj changed.[/]")]
    public void WatchNoticesAreFormattedForTheTerminal(LogLevel level, string message, string expected)
    {
        var line = RunCommand.FormatWatchNotice(CreateEntry(RunCommand.WatchNoticeCategory, level, message));

        Assert.NotNull(line);
        Assert.StartsWith("[grey]", line);
        Assert.EndsWith($"[/] {expected}", line);
    }

    [Theory]
    [InlineData("Aspire.Hosting.Dotnet.Watch", LogLevel.Debug)]
    [InlineData("Aspire.Hosting.Dotnet.WatchServer", LogLevel.Information)]
    [InlineData("Aspire.Hosting.Dcp", LogLevel.Error)]
    public void OtherAppHostLogsAreNotShownAsWatchNotices(string category, LogLevel level)
    {
        Assert.Null(RunCommand.FormatWatchNotice(CreateEntry(category, level, "message")));
    }

    [Fact]
    public async Task RetriesWhenTheServerClosesTheConnectionBeforeTheApplicationStarts()
    {
        var pipeName = $"aspire-proxy-test-{Guid.NewGuid():N}";
        var requests = new List<string>();
        var server = Task.Run(async () =>
        {
            // First request: ACK then close without output, like the watcher's failed concurrent web launch.
            // Second request: ACK, one output record, then close (the server went away after the app started).
            for (var i = 0; i < 2; i++)
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                Assert.Equal(1, pipe.ReadByte());
                requests.Add(await DotnetWatchResourceProxy.ReadStringAsync(pipe, CancellationToken.None));
                pipe.WriteByte(1);
                if (i == 1)
                {
                    pipe.WriteByte(1);
                    await DotnetWatchResourceProxy.WriteStringAsync(pipe, "started", CancellationToken.None);
                }

                await pipe.FlushAsync();
            }
        });

        var exitCode = await DotnetWatchResourceProxy.RunAsync([DotnetWatchResourceProxy.CommandName, "--server", pipeName, "--entrypoint", "/src/Api/Api.csproj"]).WaitAsync(TimeSpan.FromSeconds(30));
        await server.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DotnetWatchResourceProxy.ExitServerDisconnected, exitCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
    }

    [Fact]
    public async Task DoesNotRetryWhenTheServerRejectsTheRequest()
    {
        var pipeName = $"aspire-proxy-test-{Guid.NewGuid():N}";
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            Assert.Equal(1, pipe.ReadByte());
            await DotnetWatchResourceProxy.ReadStringAsync(pipe, CancellationToken.None);
            pipe.WriteByte(0);
            await pipe.FlushAsync();
        });

        var exitCode = await DotnetWatchResourceProxy.RunAsync([DotnetWatchResourceProxy.CommandName, "--server", pipeName, "--entrypoint", "/src/Api/Api.csproj"]).WaitAsync(TimeSpan.FromSeconds(30));
        await server.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DotnetWatchResourceProxy.ExitRejected, exitCode);
    }

    private static BackchannelLogEntry CreateEntry(string category, LogLevel level, string message) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        LogLevel = level,
        Message = message,
        EventId = new EventId(),
        CategoryName = category,
    };
}
