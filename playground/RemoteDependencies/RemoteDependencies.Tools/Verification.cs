// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDependencies.Tools;

internal sealed class Verification(string repo, Fixture fixture)
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private sealed record Response(int requests, string cache, string application);

    internal async Task RunAsync(bool injectShutdownFailure)
    {
        var state = await fixture.LoadAsync();
        await Fixture.VerifyOwnerAsync(state);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(fixture.StateDirectory, "env.json")))
            ?? throw new InvalidDataException("Fixture environment is empty.");
        Task<string> Remote(params string[] args) => Commands.RunAsync("docker", ["exec", state.Container, "docker", .. args]);
        Task<string> Owned() => Remote("ps", "-aq", "--no-trunc", "--filter", "label=aspire.remote.owner");
        async Task Clean() => Commands.Require(await Owned() == "", "Owned remote container leaked.");
        await Clean();
        var initialVolumes = (await Remote("volume", "ls", "-q")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var sentinel = await Remote("run", "-d", "--name", "aspire-remote-unrelated-" + Guid.NewGuid().ToString("N"), "redis:7-alpine");
        var results = new List<object>();
        Session? session = null;
        string? leaked = null;
        try
        {
            session = new Session(repo, fixture, environment, "happy");
            var first = await HealthyAsync(session);
            using (var client = new TcpClient())
            {
                var reachable = false;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await client.ConnectAsync(IPAddress.Loopback, 26379, timeout.Token);
                    reachable = true;
                }
                catch (SocketException)
                {
                    // Connection refused proves remote loopback is not the workstation loopback.
                }
                Commands.Require(!reachable, "Remote loopback leaked into local namespace.");
            }
            var second = (await RequestAsync())!.Value.Body;
            Commands.Require(second.requests == first.requests + 1, "Redis counter did not increment.");
            var cache = await Owned();
            Commands.Require(await Remote("exec", cache, "redis-cli", "-p", "26379", "get", "remote-development:requests")
                == second.requests.ToString(CultureInfo.InvariantCulture), "Direct Redis value differs from HTTP response.");
            Commands.Require(session.Content("Ready to accept connections"), "Redis logs not observed.");
            Commands.Require(await Remote("inspect", "--format", "{{.HostConfig.NetworkMode}}", cache) == "host", "Unexpected remote network.");
            await session.StopAsync();
            if (injectShutdownFailure)
            {
                throw new InvalidOperationException("Injected AppHost shutdown reporting failure; reconciliation must still run.");
            }
            Commands.Require(session.State("Finished") && session.InactiveEndpoint, "Missing finished state/inactive endpoint.");
            session = null;
            await Clean();
            Commands.Require(await Remote("inspect", "--format", "{{.State.Running}}", sentinel) == "true", "Unrelated sentinel stopped.");
            results.Add(new { scenario = "local HTTP -> SSH -> real Redis; logs/status/cleanup/ownership", passed = true, responses = new[] { first, second } });

            session = new Session(repo, fixture, environment, "disconnect");
            await HealthyAsync(session);
            var rows = (await Commands.RunAsync("ps", "-eo", "pid=,ppid=,args=")).Split('\n');
            var pids = rows.Select(row => row.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length == 3 && parts[1] == session.ProcessId.ToString(CultureInfo.InvariantCulture) &&
                    parts[2].StartsWith("ssh ", StringComparison.Ordinal) && parts[2].Contains("ASPIRE_SSH_FORWARD_READY", StringComparison.Ordinal))
                .Select(parts => parts[0]).ToArray();
            Commands.Require(pids.Length == 1, "Expected exactly one tunnel client owned by this AppHost.");
            await Commands.RunAsync("kill", "-TERM", pids[0]);
            await Commands.WaitAsync(() => Task.FromResult(session.State("Unavailable") && session.InactiveEndpoint), "disconnect diagnosed");
            Commands.Require((await WaitForRequestAsync()).Status == 503, "Disconnected API did not return HTTP 503.");
            await Commands.WaitAsync(() => Task.FromResult(session.Content("Remote cleanup verified")), "cleanup after SSH disconnect");
            await session.StopAsync();
            session = null;
            await Clean();
            results.Add(new { scenario = "SSH tunnel disconnect -> Unavailable + HTTP 503 + scoped cleanup", passed = true });

            session = new Session(repo, fixture, environment, "restart");
            await HealthyAsync(session);
            await session.StopAsync();
            session = null;
            await Clean();
            results.Add(new { scenario = "explicit AppHost restart restores remote development", passed = true });

            using (var collision = Commands.Listen())
            {
                session = new Session(repo, fixture, environment, "local-port-collision",
                    localPort: ((IPEndPoint)collision.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
                await Commands.WaitAsync(() => Task.FromResult(session.State("FailedToStart")), "local forward bind failure");
                Commands.Require(session.Content("cannot listen to port") || session.Content("Address already in use"), "Missing bind error.");
                await session.StopAsync();
                session = null;
            }
            await Clean();
            results.Add(new { scenario = "occupied local tunnel port -> FailedToStart, no false readiness", passed = true });

            session = new Session(repo, fixture, environment, "bad-image", image: "redis:aspire-poc-nonexistent-tag");
            await Commands.WaitAsync(() => Task.FromResult(session.State("FailedToStart")), "remote image startup failure");
            await session.StopAsync();
            session = null;
            await Clean();
            results.Add(new { scenario = "remote image startup failure -> FailedToStart + cleanup", passed = true });

            session = new Session(repo, fixture, environment, "remote-outage");
            await HealthyAsync(session);
            leaked = await Owned();
            await Commands.RunAsync("docker", "stop", "-t", "2", state.Container);
            await Commands.WaitAsync(() => Task.FromResult(session.State("Unavailable") && session.InactiveEndpoint), "remote machine outage");
            Commands.Require((await WaitForRequestAsync()).Status == 503, "Outage API did not return HTTP 503.");
            await Commands.WaitAsync(() => Task.FromResult(session.Content("Remote cleanup unconfirmed")), "explicit cleanup uncertainty", seconds: 25);
            await session.StopAsync();
            session = null;
            results.Add(new { scenario = "remote machine outage -> Unavailable + 503 + explicit cleanup command", passed = true });
        }
        finally
        {
            try
            {
                if (session is not null)
                {
                    await session.StopAsync();
                }
            }
            finally
            {
                await Commands.RunAsync("docker", "start", state.Container);
                await Fixture.WaitForEngineAsync(state.Container, allowCancellation: false);
                if (!string.IsNullOrEmpty(leaked))
                {
                    var owner = await Remote("inspect", "--format", "{{index .Config.Labels \"aspire.remote.owner\"}}", leaked);
                    Commands.Require(Guid.TryParseExact(owner, "N", out _) &&
                        await Remote("inspect", "--format", "{{.Name}}", leaked) == "/aspire-remote-" + owner, "Orphan owner mismatch.");
                    await Remote("rm", "-fv", leaked);
                }
                await Remote("rm", "-fv", sentinel);
                await Clean();
                var volumes = (await Remote("volume", "ls", "-q")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                Commands.Require(volumes.SetEquals(initialVolumes), "Anonymous Redis volume leaked.");
                Console.WriteLine("Fixture restored; exact owned/sentinel IDs reconciled; volume inventory unchanged.");
            }
        }
        var evidence = JsonSerializer.Serialize(new
        {
            topology = "local AppHost/API -> loopback SSH -> isolated SSH fixture -> host-network nested Redis",
            wan_tested = false,
            anonymous_volume_leaks = 0,
            verifier = "RemoteDependencies.Tools (.NET)",
            scenarios = results
        }, Program.JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(fixture.StateDirectory, "evidence.json"), evidence + "\n");
        Console.WriteLine(evidence);
    }

    private static async Task<Response> HealthyAsync(Session session)
    {
        await Commands.WaitAsync(() => Task.FromResult(session.State("Running")), $"Redis Running; see {session.LogPath}");
        var response = await WaitForRequestAsync();
        Commands.Require(response.Status == 200 && response.Body.application == "local" &&
            response.Body.cache == "127.0.0.1:16379", "Local application/reference mismatch.");
        Commands.Require(session.Content("Redis PONG verified"), "Missing protocol readiness.");
        return response.Body;
    }

    private static async Task<(int Status, Response Body)?> RequestAsync()
    {
        try
        {
            using var response = await s_http.GetAsync("http://127.0.0.1:18080/");
            // Failure payloads have a different shape; only the actual status matters there.
            var body = response.IsSuccessStatusCode
                ? JsonSerializer.Deserialize<Response>(await response.Content.ReadAsStringAsync())
                    ?? throw new InvalidDataException("Empty HTTP response.")
                : new Response(0, "", "");
            return ((int)response.StatusCode, body);
        }
        catch (HttpRequestException)
        {
            return null; // Expected only while the local API is starting.
        }
        catch (TaskCanceledException)
        {
            return null; // The bounded polling loop reports a persistent timeout explicitly.
        }
    }

    private static async Task<(int Status, Response Body)> WaitForRequestAsync()
    {
        (int Status, Response Body)? result = null;
        await Commands.WaitAsync(async () => (result = await RequestAsync()).HasValue, "local HTTP API response");
        return result!.Value;
    }
}
