// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Shared;

namespace Aspire.Tray.Spike.Tests;

public class CliProtocolTests
{
    [Fact]
    public void EmptySnapshotIsExplicitlyLive()
    {
        var snapshot = Parse("""{"version":1,"type":"snapshot","appHosts":[]}""");
        Assert.Equal(DiscoveryState.Live, snapshot.Discovery);
        Assert.Empty(snapshot.AppHosts);
    }

    [Fact]
    public void CompleteSnapshotReplacesPriorInstancesAndDashboardData()
    {
        var first = Parse(Snapshot(Host(42), Host(43)));
        var second = Parse(Snapshot(Host(43) with { DashboardUrl = "https://localhost:5678/" }));
        Assert.Equal([42, 43], first.AppHosts.Select(host => host.AppHostPid));
        Assert.Equal(43, Assert.Single(second.AppHosts).AppHostPid);
        Assert.Equal("https://localhost:5678/", second.AppHosts[0].DashboardUri?.AbsoluteUri);
    }

    [Fact]
    public void LifetimeIsPartOfIdentityButDashboardUrlIsNot()
    {
        var original = Parse(Snapshot(Host(42))).AppHosts[0];
        var updated = original with { DashboardUrl = "https://localhost:5678/" };
        var replacement = original with { ProcessStartTimeUnixMilliseconds = 2000 };
        Assert.Equal(original.Id, updated.Id);
        Assert.NotEqual(original.Id, replacement.Id);
    }

    [Fact]
    public void PathIdentityUsesPlatformCaseSemantics()
    {
        var host = Parse(Snapshot(Host(42))).AppHosts[0];
        var otherCase = host with { AppHostPath = host.AppHostPath.ToUpperInvariant() };
        Assert.Equal(OperatingSystem.IsWindows(), host.Id == otherCase.Id);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("""{"type":"snapshot","appHosts":[]}""")]
    [InlineData("""{"version":1,"appHosts":[]}""")]
    public void MalformedOrMissingRequiredFieldsAreRejected(string json)
    {
        Assert.Throws<JsonException>(() => CliProtocol.ReadWatchMessage(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"version":2,"type":"snapshot","appHosts":[]}""")]
    [InlineData("""{"version":1,"type":"snapshot"}""")]
    [InlineData("""{"version":1,"type":"running","appHosts":[]}""")]
    [InlineData("""{"version":1,"type":"heartbeat","appHosts":[]}""")]
    [InlineData("""{"version":1,"type":"error"}""")]
    public void UnsupportedOrConflictingFramesAreRejected(string json)
    {
        Assert.Throws<CliProtocolException>(() => CliProtocol.ReadWatchMessage(json));
    }

    [Fact]
    public void UnknownAdditiveFieldsDoNotBreakTheProtocol()
    {
        var snapshot = Parse("""{"version":1,"type":"snapshot","appHosts":[],"futureField":true}""");
        Assert.Empty(snapshot.AppHosts);
        Assert.Equal(DiscoveryState.Live, snapshot.Discovery);
    }

    [Fact]
    public void FailureIsNotAnEmptySnapshot()
    {
        var message = CliProtocol.ReadWatchMessage("""{"version":1,"type":"error","errorCode":"discovery_failed"}""");
        Assert.Equal("error", message.Type);
        Assert.Null(message.AppHosts);
        Assert.Throws<CliProtocolException>(() => CliProtocol.ReadSnapshot(message));
    }

    [Fact]
    public void InvalidIdentityAndDuplicateProcessesAreRejected()
    {
        var invalid = new[]
        {
            Host(0), Host(-1), Host(42) with { AppHostPath = "relative" },
            Host(42) with { ProcessStartTimeUnixMilliseconds = 0 },
            Host(42) with { AppHostPath = Path.GetFullPath("apphost.cs") + "\0" }
        };
        foreach (var host in invalid)
        {
            Assert.Throws<CliProtocolException>(() => Parse(Snapshot(host)));
        }
        Assert.Throws<CliProtocolException>(() => Parse(Snapshot(Host(42), Host(42))));
        Assert.Throws<CliProtocolException>(() => Parse(Snapshot(Host(42), Host(42) with { AppHostPath = Path.GetFullPath("other.cs") })));
    }

    [Fact]
    public void UnavailableLifetimeIsDisplayableWithoutInventingAnIdentity()
    {
        var host = Assert.Single(Parse(Snapshot(Host(42) with { ProcessStartTimeUnixMilliseconds = null })).AppHosts);
        Assert.Null(host.Id.ProcessStartTimeUnixMilliseconds);
    }

    [Fact]
    public void HostLimitIsInclusiveAndNeverTruncatesAList()
    {
        var hosts = Enumerable.Range(1, TrayCliProtocol.MaximumAppHosts).Select(Host).ToArray();
        Assert.Equal(TrayCliProtocol.MaximumAppHosts, Parse(Snapshot(hosts)).AppHosts.Count);
        Assert.Throws<CliProtocolException>(() => Parse(Snapshot([.. hosts, Host(hosts.Length + 1)])));
    }

    [Theory]
    [InlineData("""{"version":2,"outcome":"stopped","exitCode":0}""")]
    [InlineData("""{"version":1,"outcome":"stopped","exitCode":7}""")]
    [InlineData("""{"version":1,"outcome":"not_found","exitCode":0}""")]
    [InlineData("""{"version":1,"outcome":"future_outcome","exitCode":7}""")]
    public void StopResultMustHaveAKnownVersionOutcomeAndConsistentSuccess(string json)
    {
        Assert.Throws<CliProtocolException>(() => CliProtocol.ReadStopMessage(json));
    }

    [Fact]
    public async Task NdjsonHandlesCrLfEmptyLinesAndUnterminatedLastLine()
    {
        Assert.Equal(["{}", """{"type":"heartbeat"}""", "{}"], await LinesAsync("{}\r\n\n{\"type\":\"heartbeat\"}\n{}"));
    }

    [Fact]
    public async Task MessageLimitIsInclusive()
    {
        var value = new string('x', TrayCliProtocol.MaximumMessageLength);
        Assert.Equal([value], await LinesAsync(value + "\n"));
        Assert.Equal([value], await LinesAsync(value + "\r\n"));
        await Assert.ThrowsAsync<CliProtocolException>(() => LinesAsync(value + "x\n"));
        await Assert.ThrowsAsync<CliProtocolException>(() => LinesAsync(value + "\rx\n"));
    }

    internal static TrayAppHost Host(int pid) => new()
    {
        AppHostPath = Path.GetFullPath("sample/apphost.cs"),
        AppHostPid = pid,
        ProcessStartTimeUnixMilliseconds = 1000,
        DashboardUrl = "https://localhost:1234/"
    };

    internal static string Snapshot(params TrayAppHost[] hosts)
        => JsonSerializer.Serialize(new TrayWatchMessage
        {
            Version = TrayCliProtocol.Version,
            Type = "snapshot",
            AppHosts = hosts
        }, TrayCliJsonContext.Default.TrayWatchMessage);

    private static AppHostSnapshot Parse(string json) => CliProtocol.ReadSnapshot(CliProtocol.ReadWatchMessage(json));

    private static async Task<List<string>> LinesAsync(string text)
    {
        using var reader = new StringReader(text);
        var lines = new List<string>();
        await foreach (var line in CliProtocol.ReadLinesAsync(reader, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lines.Add(line);
        }
        return lines;
    }
}
