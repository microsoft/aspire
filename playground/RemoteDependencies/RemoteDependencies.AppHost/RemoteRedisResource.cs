// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteDependencies;

public sealed class RemoteMachineResource(string name, SshTarget target) : Resource(name)
{
    public SshTarget Target { get; } = target;
}

public sealed class RemoteRedisResource : Resource, IResourceWithEndpoints, IResourceWithConnectionString, IResourceWithWaitSupport
{
    public RemoteRedisResource(string name, RemoteMachineResource machine, int localPort, string image, int? hostNetworkPort) : base(name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(localPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        if (hostNetworkPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(hostNetworkPort));
        }
        Machine = machine;
        Image = image;
        HostNetworkPort = hostNetworkPort;
        // This endpoint describes the local tunnel, not the container's port on the remote network.
        Endpoint = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "tcp", name: "tcp",
            port: localPort, targetPort: localPort, isProxied: false)
        {
            TargetHost = "127.0.0.1"
        };
        Endpoint.AllocatedEndpoint = new AllocatedEndpoint(Endpoint, "127.0.0.1", localPort,
            EndpointBindingMode.SingleAddress, targetPortExpression: null, networkId: null);
        Annotations.Add(Endpoint);
    }

    public RemoteMachineResource Machine { get; }
    public string Image { get; }
    public int? HostNetworkPort { get; }
    public EndpointAnnotation Endpoint { get; }
    public string Owner { get; } = Guid.NewGuid().ToString("N");
    public string ContainerName => $"aspire-remote-{Owner}";
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{this.GetEndpoint("tcp").Property(EndpointProperty.Host)}:{this.GetEndpoint("tcp").Property(EndpointProperty.Port)}");

    public string CleanupCommand =>
        $"set -eu; ids=\"$(docker container ls -a --no-trunc --filter {SshTarget.Quote($"name=^/{ContainerName}$")} --format '{{{{.ID}}}}')\"; " +
        "if [ -n \"$ids\" ]; then " +
        $"test \"$(docker inspect --format '{{{{ index .Config.Labels \"aspire.remote.owner\" }}}}' \"$ids\")\" = {SshTarget.Quote(Owner)} " +
        "&& docker rm -fv \"$ids\"; fi";
}

public static class RemoteDependencyExtensions
{
    [AspireExportIgnore(Reason = "Experimental Linux/OpenSSH playground, not a supported hosting API.")]
    public static IResourceBuilder<RemoteMachineResource> AddRemoteMachine(
        this IDistributedApplicationBuilder builder, string name, SshTarget target)
    {
        if (builder.ExecutionContext.IsPublishMode)
        {
            throw new NotSupportedException("Remote dependencies are for local development, not deployment.");
        }
        return builder.AddResource(new RemoteMachineResource(name, target))
            .ExcludeFromManifest()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "SSH machine",
                State = KnownResourceStates.NotStarted,
                Properties = [new("SSH", $"{target.User}@{target.Host}:{target.Port}")]
            })
            .OnInitializeResource(async (resource, e, token) =>
            {
                try
                {
                    await e.Notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Starting });
                    var version = await target.RunAsync("docker version --format '{{.Server.Version}}'", token);
                    e.Logger.LogInformation("SSH authenticated; remote Docker {Version}", version);
                    await e.Notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Running });
                }
                catch (Exception ex)
                {
                    e.Logger.LogError(ex, "SSH machine unavailable");
                    await e.Notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.FailedToStart });
                }
            });
    }

    [AspireExportIgnore(Reason = "Experimental Redis-only remote dependency, not RunOnHost for arbitrary containers.")]
    public static IResourceBuilder<RemoteRedisResource> AddRemoteRedis(this IDistributedApplicationBuilder builder,
        string name, IResourceBuilder<RemoteMachineResource> machine, int localPort, string image, int? hostNetworkPort)
    {
        builder.Services.TryAddSingleton<RemoteSessions>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RemoteSessions>());
        return builder.AddResource(new RemoteRedisResource(name, machine.Resource, localPort, image, hostNetworkPort))
            .WithParentRelationship(machine)
            .WaitFor(machine, WaitBehavior.StopOnResourceUnavailable)
            .ExcludeFromManifest()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Remote Redis",
                State = KnownResourceStates.NotStarted,
                Properties = [new("Image", image), new("Placement", "Remote dependency; local application")]
            })
            .OnInitializeResource((resource, e, _) =>
            {
                e.Services.GetRequiredService<RemoteSessions>().Start(resource, e);
                return Task.CompletedTask;
            });
    }
}

// Own the asynchronous sessions explicitly so shutdown awaits remote cleanup, not just cancellation.
internal sealed class RemoteSessions : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _sessions = [];
    private readonly object _gate = new();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Start(RemoteRedisResource resource, InitializeResourceEvent e)
    {
        lock (_gate)
        {
            _sessions.Add(RunAsync(resource, e, _stopping.Token));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        Task[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }
        await Task.WhenAll(sessions).WaitAsync(cancellationToken);
    }

    public void Dispose() => _stopping.Dispose();

    private static async Task RunAsync(RemoteRedisResource resource, InitializeResourceEvent e, CancellationToken token)
    {
        var target = resource.Machine.Target;
        var started = false;
        var createAttempted = false;
        var createUncertain = false;
        var cleanupConfirmed = true;
        System.Diagnostics.Process? tunnel = null;
        Task[] streams = [];
        using var streamCancellation = new CancellationTokenSource();
        try
        {
            await e.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, e.Services), token);
            await e.Notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Starting });
            e.Logger.LogInformation("Creating owned remote container {Container}; owner {Owner}", resource.ContainerName, resource.Owner);
            // Mark before sending: an interrupted SSH response is not proof that creation failed remotely.
            createAttempted = true;
            var networking = resource.HostNetworkPort is null ? "--publish 127.0.0.1::6379" : "--network host";
            var redisArguments = resource.HostNetworkPort is { } hostPort
                ? $"--bind 127.0.0.1 --port {hostPort}" : "";
            try
            {
                await target.RunAsync($"docker create --name {SshTarget.Quote(resource.ContainerName)} " +
                    $"--label aspire.remote.owner={resource.Owner} {networking} " +
                    $"-- {SshTarget.Quote(resource.Image)} redis-server {redisArguments} --save '' --appendonly no", token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SshCommandException { ExitCode: 255 })
            {
                createUncertain = true;
                throw;
            }
            await target.RunAsync($"docker start {SshTarget.Quote(resource.ContainerName)}", token);
            var remotePort = resource.HostNetworkPort ?? ParseRemotePort(
                await target.RunAsync($"docker port {SshTarget.Quote(resource.ContainerName)} 6379/tcp", token));
            tunnel = target.Start($"printf 'ASPIRE_SSH_FORWARD_READY\\n'; exec docker logs --follow {SshTarget.Quote(resource.ContainerName)}",
                $"127.0.0.1:{resource.Endpoint.Port}:127.0.0.1:{remotePort}");
            // OpenSSH runs the remote command only after ExitOnForwardFailure has accepted the local bind.
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(10));
                var marker = await tunnel.StandardOutput.ReadLineAsync(ready.Token);
                if (marker != "ASPIRE_SSH_FORWARD_READY")
                {
                    throw new IOException("SSH did not confirm tunnel setup: " + await tunnel.StandardError.ReadToEndAsync(ready.Token));
                }
            }
            streams = [
                SshTarget.StreamAsync(tunnel.StandardOutput, e.Logger, streamCancellation.Token),
                SshTarget.StreamAsync(tunnel.StandardError, e.Logger, streamCancellation.Token)
            ];
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    ThrowIfExited(tunnel);
                    try
                    {
                        await PingAsync(resource.Endpoint.Port!.Value, startup.Token);
                        break;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException)
                    {
                        await Task.Delay(200, startup.Token);
                    }
                }
            }
            ThrowIfExited(tunnel);
            if (await target.RunAsync($"docker inspect --format '{{{{.State.Running}}}}' {SshTarget.Quote(resource.ContainerName)}", token) != "true")
            {
                throw new IOException("The owned remote container exited during startup.");
            }
            await e.Eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(resource, e.Services), token);
            await e.Eventing.PublishAsync(new ConnectionStringAvailableEvent(resource, e.Services), token);
            started = true;
            e.Logger.LogInformation("Remote 127.0.0.1:{RemotePort} -> local 127.0.0.1:{LocalPort}; Redis PONG verified", remotePort, resource.Endpoint.Port);
            await e.Notifications.PublishUpdateAsync(resource, s => s with
            {
                State = KnownResourceStates.Running,
                StartTimeStamp = DateTime.UtcNow,
                Urls = [new UrlSnapshot("tcp", $"tcp://127.0.0.1:{resource.Endpoint.Port}", false)],
                Properties = [.. s.Properties, new("Container", resource.ContainerName), new("Owner", resource.Owner),
                    new("Remote endpoint", $"127.0.0.1:{remotePort}"), new("Local endpoint", $"127.0.0.1:{resource.Endpoint.Port}")]
            });
            while (true)
            {
                await Task.Delay(1000, token);
                ThrowIfExited(tunnel);
                if (streams.FirstOrDefault(t => t.IsFaulted) is { } failedStream)
                {
                    await failedStream;
                }
                await PingAsync(resource.Endpoint.Port!.Value, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            e.Logger.LogInformation("Remote dependency stopping");
        }
        catch (Exception ex)
        {
            e.Logger.LogError(ex, "Remote dependency {Phase}; no automatic reconnect. Restart AppHost after restoring SSH.", started ? "unavailable" : "failed to start");
            await e.Notifications.PublishUpdateAsync(resource, s => s with
            {
                State = started ? new ResourceStateSnapshot("Unavailable", KnownResourceStateStyles.Error) : KnownResourceStates.FailedToStart,
                Urls = [.. s.Urls.Select(u => u with { IsInactive = true })]
            });
        }
        finally
        {
            if (tunnel is not null)
            {
                SshTarget.Stop(tunnel);
                await tunnel.WaitForExitAsync(CancellationToken.None);
                await streamCancellation.CancelAsync();
                await Task.WhenAll(streams).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                tunnel.Dispose();
            }
            if (createAttempted)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await target.RunAsync(resource.CleanupCommand, cleanup.Token);
                    if (createUncertain)
                    {
                        throw new IOException("Remote creation was interrupted and may still complete after this cleanup snapshot; reconcile again once the remote command has settled.");
                    }
                    e.Logger.LogInformation("Remote cleanup verified for owner {Owner}", resource.Owner);
                }
                catch (Exception ex)
                {
                    cleanupConfirmed = false;
                    e.Logger.LogError(ex, "Remote cleanup unconfirmed. Once SSH returns, run on the remote machine: {Cleanup}", resource.CleanupCommand);
                    await e.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        Properties = [.. s.Properties, new("Cleanup required", resource.CleanupCommand)]
                    });
                }
            }
            if (token.IsCancellationRequested)
            {
                await e.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = cleanupConfirmed ? KnownResourceStates.Finished : new ResourceStateSnapshot("Cleanup required", KnownResourceStateStyles.Error),
                    StopTimeStamp = DateTime.UtcNow,
                    Urls = [.. s.Urls.Select(u => u with { IsInactive = true })]
                });
            }
        }
    }

    public static int ParseRemotePort(string mapping)
    {
        // docker port emits "127.0.0.1:32768". Reject non-loopback or multiple bindings.
        const string prefix = "127.0.0.1:";
        if (!mapping.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(mapping.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Expected one remote IPv4 loopback mapping, got: {mapping}");
        }
        return port;
    }

    private static void ThrowIfExited(System.Diagnostics.Process process)
    {
        if (process.HasExited)
        {
            throw new IOException($"SSH tunnel/log stream exited ({process.ExitCode}).");
        }
    }

    internal static async Task PingAsync(int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync("*1\r\n$4\r\nPING\r\n"u8.ToArray(), timeout.Token);
        var response = new byte[7];
        await stream.ReadExactlyAsync(response, timeout.Token);
        if (!response.AsSpan().SequenceEqual("+PONG\r\n"u8))
        {
            throw new IOException("Redis did not return PONG through the SSH tunnel.");
        }
    }
}
