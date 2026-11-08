// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Dotnet.Tests.Watch;

/// <summary>
/// Contract tests against the REAL <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server, driven through the
/// real watch host proxy. They pin what the coordinator and the proxy rely on: the resource pipe
/// launch spec (args after <c>--</c>, forwarded environment, endpoints), the status events the notices and resource
/// states are derived from, and what the control pipe's <c>rebuild</c> does.
/// </summary>
/// <remarks>
/// Outerloop: each test builds a web app with the watcher (tens of seconds), using the watch tool Aspire.Hosting.Dotnet
/// lays out (or <c>ASPIRE_DOTNET_WATCH_TOOL_PATH</c>) and a .NET 10 SDK installed next to the <c>dotnet</c> on PATH. Run locally with
/// <c>dotnet test tests/Aspire.Hosting.Dotnet.Tests -- --filter-class *DotnetWatchContractTests</c>.
/// </remarks>
[OuterloopTest("Builds and hot reloads a real app with the dotnet-watch server and a .NET 10 SDK.")]
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
        var (_, index) = await harness.WaitForStatusAsync(0, "build_complete", s => !s.GetProperty("success").GetBoolean());

        // The protocol doesn't say when the watcher waits for changes again (it reloads the projects first), so save
        // the fix until it builds.
        (_, index) = await SaveUntilStatusAsync(harness, index + 1, greetingPath, greeting("\"contract v1\"", ""), "build_complete", s => s.GetProperty("success").GetBoolean());

        var port = GetFreePort();
        harness.LaunchThroughProxy(proxy, [], new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" }, ["ASPNETCORE_URLS"]);
        (_, index) = await harness.WaitForStatusAsync(index + 1, "process_started");
        await GetUntilAsync(port, "/", "contract v1");

        // Hot reload.
        await EditAsync(greetingPath, greeting("\"contract v2\"", ""));
        (_, index) = await harness.WaitForStatusAsync(index + 1, "hot_reload_applied");
        await GetUntilAsync(port, "/", "contract v2");

        // Compile error during a session: hot_reload_applied is reported although nothing applied (the protocol has no
        // compile-error event), and the app keeps running the last good code.
        await EditAsync(greetingPath, greeting("\"contract v3\" +", ""));
        (_, index) = await harness.WaitForStatusAsync(index + 1, "hot_reload_applied");
        Assert.Equal("contract v2", await GetAsync(port, "/"));

        await EditAsync(greetingPath, greeting("\"contract v3\"", ""));
        (_, index) = await harness.WaitForStatusAsync(index + 1, "hot_reload_applied");
        await GetUntilAsync(port, "/", "contract v3");

        // Rude edit (a new field in a struct): the watcher rebuilds and restarts the app.
        await EditAsync(greetingPath, greeting("\"contract v4\"", " public int B;"));
        (_, index) = await harness.WaitForStatusAsync(index + 1, "restarting");
        (_, index) = await harness.WaitForStatusAsync(index + 1, "process_started");
        await GetUntilAsync(port, "/", "contract v4");

        // Crash: a non-zero exit is reported with its exit code; the next change relaunches the app.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(port, "/crash"));
        (var exited, index) = await harness.WaitForStatusAsync(index + 1, "process_exited");
        Assert.Equal(42, exited.Status.GetProperty("exitCode").GetInt32());

        await EditAsync(greetingPath, greeting("\"contract v5\"", " public int B;"));
        (_, index) = await harness.WaitForStatusAsync(index + 1, "process_started");
        await GetUntilAsync(port, "/", "contract v5");

        var notices = Interpret(harness, "contract");
        foreach (var notice in notices)
        {
            output.WriteLine($"{notice.Kind}: {notice.Message}");
        }

        AssertLifecycleNotices(notices);
    }

    /// <summary>The notices <c>aspire run --watch</c> derives from the status events of the edit lifecycle.</summary>
    internal static IReadOnlyList<WatchNotice> Interpret(WatchServerHarness harness, string name)
    {
        var interpreter = new DotnetWatchEventInterpreter([(harness.ProjectPath, name)], "dotnet-watch");
        var notices = new List<WatchNotice>();
        foreach (var status in harness.Observations.Where(o => o.Kind == WatchObservationKind.Status).Select(o => o.Status))
        {
            notices.AddRange(interpreter.OnStatus(
                status.GetProperty("type").GetString()!,
                status.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Array ? [.. projects.EnumerateArray().Select(p => p.GetString()!)] : [],
                status.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False ? success.GetBoolean() : null,
                status.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : null,
                status.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null));
        }

        return notices;
    }

    internal static void AssertLifecycleNotices(IReadOnlyList<WatchNotice> notices)
    {
        var expected = new (WatchNoticeKind Kind, string Fragment)[]
        {
            (WatchNoticeKind.BuildFailed, "❌ Initial build failed: "),
            (WatchNoticeKind.Watching, "✅ Build succeeded. 👀 Watching contract"),
            (WatchNoticeKind.HotReloadApplied, "⌚ The watcher processed the change for contract."),
            (WatchNoticeKind.HotReloadApplied, "⌚ The watcher processed the change for contract."),
            (WatchNoticeKind.HotReloadApplied, "⌚ The watcher processed the change for contract."),
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
    }

    [Fact]
    public async Task RebuildRestartsRunningReplicasWithoutBuilding()
    {
        var proxy = RequireProxy();
        await using var harness = await WatchServerHarness.StartAsync(output);
        await harness.WaitForStatusAsync(0, "build_complete", s => s.GetProperty("success").GetBoolean());

        // Launched one at a time: the watcher races concurrent first launches of a web project.
        var port1 = GetFreePort();
        var port2 = GetFreePort();
        harness.LaunchThroughProxy(proxy, [], new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port1}" }, ["ASPNETCORE_URLS"]);
        await GetUntilAsync(port1, "/", "contract v1");
        harness.LaunchThroughProxy(proxy, [], new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port2}" }, ["ASPNETCORE_URLS"]);
        await GetUntilAsync(port2, "/", "contract v1");
        var pid1 = await GetAsync(port1, "/pid");
        var pid2 = await GetAsync(port2, "/pid");

        // `rebuild` restarts every running process of the project (dotnet run --no-build): no build events.
        var mark = harness.Observations.Count;
        Assert.True(await harness.RequestRebuildAsync(harness.ProjectPath));
        var (_, index) = await harness.WaitForStatusAsync(mark, "restarting");
        (_, index) = await harness.WaitForStatusAsync(index + 1, "process_started");
        await harness.WaitForStatusAsync(index + 1, "process_started");
        await WaitForPidChangeAsync(port1, pid1);
        await WaitForPidChangeAsync(port2, pid2);
        Assert.DoesNotContain(harness.Observations.Skip(mark), o => o.StatusType is "building" or "build_complete");

        // A crashed process isn't relaunched by `rebuild`; only the running one restarts.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(port1, "/crash"));
        (_, index) = await harness.WaitForStatusAsync(mark, "process_exited");
        pid2 = await GetAsync(port2, "/pid");
        mark = harness.Observations.Count;
        Assert.True(await harness.RequestRebuildAsync(harness.ProjectPath));
        (_, index) = await harness.WaitForStatusAsync(mark, "process_started");
        await WaitForPidChangeAsync(port2, pid2);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(harness.Observations.Skip(mark), o => o.StatusType == "process_started");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(port1, "/"));
    }

    [Fact]
    public async Task PackageReferenceChangeIsNotDeployedByTheWatcher()
    {
        // Pins why the coordinator restarts the watcher when a project file changes: the watcher re-evaluates and
        // restores the project but doesn't build it, so code using a new package hot reloads and then fails to load it.
        var proxy = RequireProxy();
        await using var harness = await WatchServerHarness.StartAsync(output);
        var (_, index) = await harness.WaitForStatusAsync(0, "build_complete", s => s.GetProperty("success").GetBoolean());
        var port = GetFreePort();
        harness.LaunchThroughProxy(proxy, [], new Dictionary<string, string> { ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}" }, ["ASPNETCORE_URLS"]);
        await GetUntilAsync(port, "/", "contract v1");
        var pid = await GetAsync(port, "/pid");

        var mark = harness.Observations.Count;
        await EditAsync(harness.ProjectPath, File.ReadAllText(harness.ProjectPath).Replace("</Project>", "  <ItemGroup><PackageReference Include=\"Humanizer.Core\" Version=\"3.0.10\" /></ItemGroup>\n</Project>"));
        (_, index) = await harness.WaitForStatusAsync(mark, "hot_reload_applied");
        await EditAsync(Path.Combine(harness.AppDirectory, "Greeting.cs"), "using Humanizer; internal static class Greeting { public static string Message() => \"hello_world\".Humanize(); }");
        (_, index) = await harness.WaitForStatusAsync(index + 1, "hot_reload_applied");

        // The edit is hot reloaded into the running process, which can't load the package.
        var stopwatch = Stopwatch.StartNew();
        HttpRequestException? failure = null;
        while (failure is null && stopwatch.Elapsed < s_httpTimeout)
        {
            try
            {
                await GetAsync(port, "/");
                await Task.Delay(250);
            }
            catch (HttpRequestException ex)
            {
                failure = ex;
            }
        }

        Assert.Equal(HttpStatusCode.InternalServerError, failure?.StatusCode);
        Assert.Equal(pid, await GetAsync(port, "/pid"));
        Assert.DoesNotContain(harness.Observations.Skip(mark), o => o.StatusType is "building" or "restarting" or "process_started");
    }

    private static async Task<(WatchObservation Observation, int Index)> SaveUntilStatusAsync(
        WatchServerHarness harness, int fromIndex, string path, string content, string type, Func<JsonElement, bool> predicate)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            await EditAsync(path, content);
            try
            {
                return await harness.WaitForAsync(fromIndex, o => o.StatusType == type && predicate(o.Status), $"status '{type}'", TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException) when (stopwatch.Elapsed < TimeSpan.FromMinutes(2))
            {
            }
        }
    }

    private static async Task WaitForPidChangeAsync(int port, string previous)
    {
        var stopwatch = Stopwatch.StartNew();
        string? last = null;
        while (stopwatch.Elapsed < s_httpTimeout)
        {
            last = await SafeGetAsync(port, "/pid");
            if (int.TryParse(last, out _) && last != previous)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"The process on port {port} still has PID {previous} (last: {last}).");
    }

    private static async Task<string> SafeGetAsync(int port, string path)
    {
        try
        {
            return await GetAsync(port, path);
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}: {ex.Message}>";
        }
    }

    private static string RequireProxy()
    {
        var proxy = WatchServerHarness.FindWatchHost();
        Assert.SkipWhen(proxy is null, "The watch host isn't laid out; build src/Aspire.Hosting.Dotnet.");
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
