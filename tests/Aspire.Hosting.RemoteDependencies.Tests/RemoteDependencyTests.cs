// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using RemoteDependencies;
using Xunit;

namespace Aspire.Hosting.RemoteDependencies.Tests;

public class RemoteDependencyTests
{
    [Theory]
    [InlineData("/tmp/path with spaces/known_hosts", "UserKnownHostsFile=\"/tmp/path with spaces/known_hosts\"")]
    [InlineData("/tmp/percent%file", "UserKnownHostsFile=\"/tmp/percent%%file\"")]
    [InlineData("/tmp/a\"b\\c", "UserKnownHostsFile=\"/tmp/a\\\"b\\\\c\"")]
    public void EncodesKnownHostsForOpenSshParser(string path, string expected)
    {
        Assert.Equal(expected, SshTarget.KnownHostsOption(path));
    }

    [Theory]
    [InlineData("-oProxyCommand=bad", "developer")]
    [InlineData("host name", "developer")]
    [InlineData("host", "-bad")]
    [InlineData("host", "user name")]
    public void RejectsSshOptionLikeNames(string host, string user)
    {
        Assert.Throws<ArgumentException>(() => new SshTarget(host, 22, user, "unused", "unused"));
    }

    [Fact]
    public async Task EndpointIsLocalAndOwnershipIsPerRun()
    {
        var directory = Directory.CreateTempSubdirectory();
        var file = Path.Combine(directory.FullName, "placeholder");
        try
        {
            await File.WriteAllTextAsync(file, "Only file existence is needed for model tests.");
            var machine = new RemoteMachineResource("remote", new SshTarget("example.test", 22, "developer", file, file));
            var first = new RemoteRedisResource("cache", machine, 16379, "redis:7-alpine", 26379);
            var second = new RemoteRedisResource("cache", machine, 16379, "redis:7-alpine", null);
            Assert.NotEqual(first.Owner, second.Owner);
            Assert.Equal(32, first.Owner.Length);
            Assert.Equal("aspire-remote-" + first.Owner, first.ContainerName);
            Assert.False(first.Endpoint.IsProxied);
            Assert.Equal(16379, first.Endpoint.Port);
            Assert.Equal(16379, first.Endpoint.TargetPort);
            Assert.Equal("127.0.0.1:16379", await ((IResourceWithConnectionString)first).GetConnectionStringAsync());
            Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteRedisResource("bad", machine, 0, "redis", null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteRedisResource("bad", machine, 1234, "redis", 65536));
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = false });
            var remote = builder.AddRemoteMachine("machine", machine.Target);
            var cache = builder.AddRemoteRedis("cache", remote, 16379, "redis:7-alpine", 26379);
            Assert.Equal(WaitBehavior.StopOnResourceUnavailable,
                Assert.Single(cache.Resource.Annotations.OfType<WaitAnnotation>()).WaitBehavior);
        }
        finally
        {
            File.Delete(file);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData("127.0.0.1:32768", 32768)]
    [InlineData("127.0.0.1:1", 1)]
    [InlineData("127.0.0.1:65535", 65535)]
    public void ParsesOnlyRemoteLoopback(string mapping, int port)
    {
        Assert.Equal(port, RemoteSessions.ParseRemotePort(mapping));
    }

    [Theory]
    [InlineData("0.0.0.0:32768")]
    [InlineData("[::]:32768")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:42\n127.0.0.1:43")]
    [InlineData("127.0.0.1:42; touch /tmp/injection")]
    public void RejectsUnexpectedMappings(string mapping)
    {
        Assert.Throws<InvalidOperationException>(() => RemoteSessions.ParseRemotePort(mapping));
    }

    [Theory]
    [InlineData("redis:7-alpine", "'redis:7-alpine'")]
    [InlineData("x'; touch /tmp/injection; '", "'x'\"'\"'; touch /tmp/injection; '\"'\"''")]
    public void QuotesRemoteShellArguments(string value, string expected)
    {
        Assert.Equal(expected, SshTarget.Quote(value));
    }

    [Fact]
    public async Task ProbeRequiresRedisProtocolNotJustAnOpenPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var probe = RemoteSessions.PingAsync(port, timeout.Token);
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var request = new byte[14];
        await client.GetStream().ReadExactlyAsync(request, timeout.Token);
        Assert.Equal("*1\r\n$4\r\nPING\r\n", System.Text.Encoding.ASCII.GetString(request));
        await client.GetStream().WriteAsync("+PONG\r\n"u8.ToArray(), timeout.Token);
        await probe;
    }

    [Fact]
    public async Task ProbeRejectsWrongService()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var probe = RemoteSessions.PingAsync(((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await client.GetStream().WriteAsync("+NOPE\r\n"u8.ToArray(), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => probe);
    }

    [Fact]
    public async Task ProbeHonorsCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var probe = RemoteSessions.PingAsync(((IPEndPoint)listener.LocalEndpoint).Port, cancellation.Token);
        using var client = await listener.AcceptTcpClientAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }
}
