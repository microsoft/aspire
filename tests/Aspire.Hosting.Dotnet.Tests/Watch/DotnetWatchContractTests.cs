// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Dotnet.Tests.Watch;

/// <summary>
/// SPIKE: contract tests against the REAL <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server, driven through the
/// real <c>aspire __dotnet-watch-proxy</c>. They pin what the coordinator and the CLI proxy rely on: the resource pipe
/// launch spec (args after <c>--</c>, forwarded environment, endpoints) and the status events and console lines the
/// notices are derived from.
/// </summary>
/// <remarks>
/// Outerloop: each test builds a web app with the watcher (tens of seconds) and acquires the watch tool from a NuGet
/// feed unless <c>ASPIRE_DOTNET_WATCH_TOOL_PATH</c> is set. Run locally with
/// <c>dotnet test tests/Aspire.Hosting.Dotnet.Tests -- --filter-class *DotnetWatchContractTests</c>; set
/// <c>ASPIRE_WATCH_CONTRACT_RECORD_DIR=tests/Aspire.Hosting.Dotnet.Tests/Watch/Recordings</c> to refresh the
/// recordings replayed by <see cref="DotnetWatchRecordedOutputTests"/> in regular CI.
/// </remarks>
[OuterloopTest("Builds and hot reloads a real app with the dotnet-watch server; acquires the watch tool from a NuGet feed.")]
[RequiresTools(["dotnet"])]
public class DotnetWatchContractTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_httpTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task LaunchSpecReachesTheApp()
    {
        var proxy = RequireProxy();
        await using var harness = await WatchServerHarness.StartAsync(output);
        await harness.WaitForStatusAsync(0, "build_complete", s => s.GetProperty("success").GetBoolean());

        var port = GetFreePort();
        var app = harness.LaunchThroughProxy(
            proxy,
            appArgs: ["alpha", "beta gamma", "--configuration", "Bogus"],
            environment: new Dictionary<string, string>
            {
                ["CONTRACT_VALUE"] = "from the app model",
                ["CONTRACT_UNLISTED"] = "set on the proxy only",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            },
            forwardedNames: ["CONTRACT_VALUE", "ASPNETCORE_URLS"]);

        var (started, _) = await harness.WaitForStatusAsync(0, "process_started");
        Assert.Equal([harness.ProjectPath], started.Status.GetProperty("projects").EnumerateArray().Select(e => e.GetString()));

        // Args after `--` reach the app verbatim, including ones `dotnet run` would otherwise parse as its own options.
        await harness.WaitForProxyOutputAsync("contract-args: alpha|beta gamma|--configuration|Bogus");

        // Only the variables the app model named are forwarded; the rest of the proxy's environment is not.
        await harness.WaitForProxyOutputAsync("contract-env: CONTRACT_VALUE=from the app model");
        await harness.WaitForProxyOutputAsync("contract-env: CONTRACT_UNLISTED=(unset)");

        // The endpoint allocated by the app model (ASPNETCORE_URLS) is where the app listens.
        Assert.Equal("contract v1", await GetUntilAsync(port, "/", "contract v1"));
        Assert.False(app.HasExited);

        harness.SaveRecording("launch-spec");
    }

    [Fact]
    public async Task StatusEventsAndNoticesFollowTheEditLifecycle()
    {
        var proxy = RequireProxy();
        var greeting = (string body, string extraField) => $$"""
            internal static class Greeting
            {
                public static string Message() => {{body}};
            }

            internal struct Counter
            {
                public int A;{{extraField}}
            }
            """;

        // Initial build failure: the server reports a failed build and waits for a fix.
        await using var harness = await WatchServerHarness.StartAsync(output, dir => File.WriteAllText(Path.Combine(dir, "Greeting.cs"), greeting("\"contract v1\" +", "")));
        var greetingPath = Path.Combine(harness.AppDirectory, "Greeting.cs");
        // Read directly from the server, the compiler output precedes the status event (through DCP it arrives later).
        await harness.WaitForAsync(0, o => o.Kind == WatchObservationKind.Output && o.Text.Contains("error CS", StringComparison.Ordinal), "compiler error line");
        var (_, index) = await harness.WaitForStatusAsync(0, "build_complete", s => !s.GetProperty("success").GetBoolean());

        await EditAsync(greetingPath, greeting("\"contract v1\"", ""));
        (_, index) = await harness.WaitForStatusAsync(index, "build_complete", s => s.GetProperty("success").GetBoolean());

        var port = GetFreePort();
        harness.LaunchThroughProxy(proxy, [], new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" }, ["ASPNETCORE_URLS"]);
        (_, index) = await harness.WaitForStatusAsync(index, "process_started");
        await GetUntilAsync(port, "/", "contract v1");

        // Hot reload.
        await EditAsync(greetingPath, greeting("\"contract v2\"", ""));
        (_, index) = await harness.WaitForStatusAsync(index, "hot_reload_applied");
        await GetUntilAsync(port, "/", "contract v2");

        // Compile error during a session: the app keeps running the last good code.
        await EditAsync(greetingPath, greeting("\"contract v3\" +", ""));
        (_, index) = await harness.WaitForAsync(index, o => o.Kind == WatchObservationKind.Output && o.Text.Contains("error CS", StringComparison.Ordinal), "compiler error line");
        Assert.Equal("contract v2", await GetAsync(port, "/"));

        await EditAsync(greetingPath, greeting("\"contract v3\"", ""));
        (_, index) = await harness.WaitForAsync(index, o => o.Kind == WatchObservationKind.Output && DotnetWatchOutputPatterns.ChangesApplied().IsMatch(o.Text), "changes applied line");
        await GetUntilAsync(port, "/", "contract v3");

        // Another compile error, reverted to the running code: there's nothing to apply.
        await EditAsync(greetingPath, greeting("\"contract v4\" +", ""));
        (_, index) = await harness.WaitForAsync(index, o => o.Kind == WatchObservationKind.Output && o.Text.Contains("error CS", StringComparison.Ordinal), "compiler error line");
        await EditAsync(greetingPath, greeting("\"contract v3\"", ""));
        (_, index) = await harness.WaitForAsync(index, o => o.Kind == WatchObservationKind.Output && o.Text.Contains(DotnetWatchOutputPatterns.NoManagedCodeChanges, StringComparison.Ordinal), "no managed code changes line");

        // Rude edit (a new field in a struct): the watcher rebuilds and restarts the app.
        await EditAsync(greetingPath, greeting("\"contract v4\"", " public int B;"));
        (_, index) = await harness.WaitForStatusAsync(index, "restarting");
        (_, index) = await harness.WaitForStatusAsync(index, "process_started");
        await GetUntilAsync(port, "/", "contract v4");

        // Crash: a non-zero exit is reported with its exit code; the next change relaunches the app.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(port, "/crash"));
        (var exited, index) = await harness.WaitForStatusAsync(index, "process_exited");
        Assert.Equal(42, exited.Status.GetProperty("exitCode").GetInt32());

        await EditAsync(greetingPath, greeting("\"contract v5\"", " public int B;"));
        (_, index) = await harness.WaitForStatusAsync(index, "process_started");
        await GetUntilAsync(port, "/", "contract v5");

        harness.SaveRecording("edit-lifecycle");

        var notices = WatchRecording.Interpret(harness.Observations, harness.ProjectPath, "contract", harness.Directory);
        foreach (var notice in notices)
        {
            output.WriteLine($"{notice.Kind}: {notice.Message}");
        }

        AssertLifecycleNotices(notices);
    }

    /// <summary>The notices <c>aspire run --watch</c> derives from the edit lifecycle, in order.</summary>
    internal static void AssertLifecycleNotices(IReadOnlyList<WatchNotice> notices)
    {
        var expected = new (WatchNoticeKind Kind, string Fragment)[]
        {
            (WatchNoticeKind.BuildFailed, "Initial build failed: Greeting.cs("),
            (WatchNoticeKind.Watching, "✅ Build succeeded. 👀 Watching contract"),
            (WatchNoticeKind.HotReloadApplied, "🔥 Hot reload applied to contract in "),
            (WatchNoticeKind.CompileError, "❌ Build error in contract: Greeting.cs("),
            (WatchNoticeKind.HotReloadApplied, "✅ Fixed. 🔥 Hot reload applied to contract"),
            (WatchNoticeKind.CompileError, "❌ Build error in contract: Greeting.cs("),
            (WatchNoticeKind.HotReloadApplied, "✅ Fixed. contract already runs this code"),
            (WatchNoticeKind.Rebuilding, "⌚ Rebuilding contract"),
            (WatchNoticeKind.Restarting, "🔄 Restarting contract"),
            (WatchNoticeKind.Crashed, "💥 contract exited with code 42."),
            (WatchNoticeKind.Relaunched, "▶️ contract is running again."),
        };

        var position = 0;
        foreach (var (kind, fragment) in expected)
        {
            var match = notices.Skip(position).Select((n, i) => (n, i)).FirstOrDefault(x => x.n.Kind == kind && x.n.Message.Contains(fragment, StringComparison.Ordinal));
            Assert.True(match.n is not null, $"Missing {kind} notice containing '{fragment}' after position {position}. Notices:{Environment.NewLine}{string.Join(Environment.NewLine, notices.Select(n => $"{n.Kind}: {n.Message}"))}");
            position += match.i + 1;
        }

        // The initial-build and compile-error notices carry the compiler's first error, not the fallback text.
        Assert.All(notices.Where(n => n.Kind is WatchNoticeKind.BuildFailed or WatchNoticeKind.CompileError), n => Assert.Contains(": CS", n.Message));
    }

    private static string RequireProxy()
    {
        var proxy = WatchServerHarness.FindProxy();
        Assert.SkipWhen(proxy is null, $"The Aspire CLI (aspire.dll) isn't built; build src/Aspire.Cli or set {DotnetWatchCoordinator.ProxyPathConfigurationKey}.");
        return proxy!;
    }

    private static async Task EditAsync(string path, string content)
    {
        // Let the watcher settle so consecutive edits aren't coalesced or missed.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(path, content);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> GetAsync(int port, string path)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetStringAsync($"http://127.0.0.1:{port}{path}");
    }

    private static async Task<string> GetUntilAsync(int port, string path, string expected)
    {
        var stopwatch = Stopwatch.StartNew();
        string? last = null;
        while (stopwatch.Elapsed < s_httpTimeout)
        {
            try
            {
                last = await GetAsync(port, path);
                if (last == expected)
                {
                    return last;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex.Message;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"GET {path} on port {port} never returned '{expected}' (last: {last}).");
    }
}
