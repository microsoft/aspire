// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Telemetry;

public class AgentTelemetryDeduplicatorTests(ITestOutputHelper outputHelper)
{
    private const string SessionId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void TryMarkFirst_ReturnsFalseOnlyForRepeatedSessionAndToolUseId()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var deduplicator = new AgentTelemetryDeduplicator(Path.Combine(workspace.WorkspaceRoot.FullName, "seen"), new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(deduplicator.TryMarkFirst(SessionId, "toolu_1"));
        Assert.False(deduplicator.TryMarkFirst(SessionId, "toolu_1"));
        Assert.True(deduplicator.TryMarkFirst(SessionId, "toolu_2"));
        Assert.True(deduplicator.TryMarkFirst("22222222-2222-2222-2222-222222222222", "toolu_1"));
    }

    [Fact]
    public void TryMarkFirst_PrunesExpiredMarkers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = Path.Combine(workspace.WorkspaceRoot.FullName, "seen");
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var deduplicator = new AgentTelemetryDeduplicator(directory, timeProvider);

        Assert.True(deduplicator.TryMarkFirst(SessionId, "toolu_1"));
        Assert.Single(Directory.GetFiles(directory));

        timeProvider.Advance(AgentTelemetryDeduplicator.s_markerLifetime + TimeSpan.FromMinutes(1));

        Assert.True(deduplicator.TryMarkFirst(SessionId, "toolu_1"));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void TryMarkFirst_RecordsWhenMarkerDirectoryCannotBeCreated()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var blocker = Path.Combine(workspace.WorkspaceRoot.FullName, "blocker");
        File.WriteAllText(blocker, "");
        var deduplicator = new AgentTelemetryDeduplicator(Path.Combine(blocker, "seen"), new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(deduplicator.TryMarkFirst(SessionId, "toolu_1"));
        Assert.True(deduplicator.TryMarkFirst(SessionId, "toolu_1"));
    }
}
