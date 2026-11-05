// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        Assert.True(DotnetWatchResourceProxy.TryParse(args, out var server, out var entryPoint, out var appArgs, out var error));
        Assert.Null(error);
        Assert.Equal("/tmp/aspw/res.sock", server);
        Assert.Equal("/src/Api/Api.csproj", entryPoint);
        Assert.Equal(["--urls", "http://localhost:5000", "--"], appArgs);
    }

    [Theory]
    [InlineData(new[] { "__dotnet-watch-proxy", "--server", "s" }, "--server and --entrypoint are required.")]
    [InlineData(new[] { "__dotnet-watch-proxy", "--entrypoint", "e" }, "--server and --entrypoint are required.")]
    [InlineData(new[] { "__dotnet-watch-proxy", "--server", "s", "--entrypoint", "e", "--bogus" }, "unexpected argument '--bogus'.")]
    public void RejectsInvalidArguments(string[] args, string expectedError)
    {
        Assert.False(DotnetWatchResourceProxy.TryParse(args, out _, out _, out _, out var error));
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

    private static BackchannelLogEntry CreateEntry(string category, LogLevel level, string message) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        LogLevel = level,
        Message = message,
        EventId = new EventId(),
        CategoryName = category,
    };
}
