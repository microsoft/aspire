// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;

namespace RemoteDependencies.Tools;

internal sealed class Fixture(string root)
{
    internal string Root => root;
    internal string StateDirectory => Path.Combine(root, ".fixture");
    private string StatePath => Path.Combine(StateDirectory, "state.json");

    internal sealed record State(string Container, string Owner, string Image);

    internal async Task<State> LoadAsync() =>
        JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(StatePath))
        ?? throw new InvalidDataException("Fixture state is empty.");

    internal static async Task VerifyOwnerAsync(State state)
    {
        var owner = await Commands.RunAsync("docker", "inspect", "--format",
            "{{index .Config.Labels \"aspire.remote.fixture\"}}", state.Container);
        Commands.Require(owner == state.Owner, "Refusing to operate on a fixture not owned by this state file.");
    }

    internal async Task UpAsync(bool nonRootPublicKey)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The fixture requires Linux file permissions.");
        }
        await Commands.RunAsync("docker", "info");
        if (File.Exists(StatePath))
        {
            await VerifyOwnerAsync(await LoadAsync());
            Console.WriteLine("Existing fixture retained; use .fixture/env.sh or run the down command first.");
            return;
        }
        Directory.CreateDirectory(StateDirectory);
        File.SetUnixFileMode(StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var key = Path.Combine(StateDirectory, "id_ed25519");
        if (!File.Exists(key))
        {
            await Commands.RunAsync("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", key);
        }
        if (nonRootPublicKey)
        {
            await Commands.RunAsync("chown", "1000:1000", key + ".pub");
        }
        var owner = Guid.NewGuid().ToString("N");
        var image = $"aspire-remote-fixture:{owner}";
        await Commands.RunAsync("docker", "build", "-q", "-t", image, Path.Combine(root, "fixture"));
        int port;
        using (var reservation = Commands.Listen())
        {
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        // Keep the loopback-published SSH port stable across the outage/restart scenario.
        var container = await Commands.RunAsync("docker", "run", "-d", "--privileged",
            "--label", $"aspire.remote.fixture={owner}", "--name", $"aspire-ssh-fixture-{owner}",
            "-p", $"127.0.0.1:{port}:22",
            "--mount", $"type=bind,src={key}.pub,dst=/run/fixture-authorized-key,readonly", image);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new State(container, owner, image), Program.JsonOptions));
        await WaitForEngineAsync(container);
        var mapping = await Commands.RunAsync("docker", "port", container, "22/tcp");
        Commands.Require(mapping == $"127.0.0.1:{port}", $"Unexpected SSH binding: {mapping}");
        // Pin trust through the local Docker control channel, not unauthenticated ssh-keyscan.
        var publicKey = await Commands.RunAsync("docker", "exec", container, "cat", "/etc/ssh/ssh_host_ed25519_key.pub");
        var knownHosts = Path.Combine(StateDirectory, "known hosts");
        await File.WriteAllTextAsync(knownHosts, $"[127.0.0.1]:{port} {publicKey}\n");
        var environment = new Dictionary<string, string>
        {
            ["REMOTE_HOST"] = "127.0.0.1",
            ["REMOTE_SSH_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["REMOTE_USER"] = "root",
            ["REMOTE_IDENTITY"] = key,
            ["REMOTE_KNOWN_HOSTS"] = knownHosts,
            ["REMOTE_HOST_NETWORK_PORT"] = "26379"
        };
        await File.WriteAllTextAsync(Path.Combine(StateDirectory, "env.json"), JsonSerializer.Serialize(environment, Program.JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(StateDirectory, "env.sh"),
            string.Concat(environment.Select(pair => $"export {pair.Key}='{pair.Value.Replace("'", "'\"'\"'")}'\n")));
        await Commands.RunAsync("docker", "exec", container, "docker", "pull", "redis:7-alpine");
        Console.WriteLine($"Fixture ready: SSH {mapping}; source {Path.Combine(StateDirectory, "env.sh")}");
    }

    internal static Task WaitForEngineAsync(string container, bool allowCancellation = true) => Commands.WaitAsync(async () =>
        (await Commands.CaptureAsync("docker", "exec", container, "docker", "info")).Code == 0,
        "fixture engine ready; inspect docker logs, then run down if startup failed", seconds: 90,
        allowCancellation: allowCancellation);

    internal async Task DownAsync()
    {
        var state = await LoadAsync();
        await VerifyOwnerAsync(state);
        await Commands.RunAsync("docker", "rm", "-fv", state.Container);
        await Commands.RunAsync("docker", "image", "rm", state.Image);
        foreach (var name in new[] { "state.json", "env.json", "env.sh", "id_ed25519", "id_ed25519.pub", "known hosts" })
        {
            File.Delete(Path.Combine(StateDirectory, name));
        }
        Console.WriteLine("Owned fixture, nested containers and ephemeral SSH keys removed.");
    }
}
