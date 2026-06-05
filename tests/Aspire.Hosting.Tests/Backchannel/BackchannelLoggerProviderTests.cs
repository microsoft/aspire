// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Backchannel;

public class BackchannelLoggerProviderTests
{
    [Fact]
    public void Subscribe_ReturnsBufferedEntries()
    {
        using var provider = new BackchannelLoggerProvider();

        var logger = provider.CreateLogger("TestCategory");
        logger.LogInformation("Message 1");
        logger.LogWarning("Message 2");
        logger.LogError("Message 3");

        var (snapshot, subscriberId, _) = provider.Subscribe();
        provider.Unsubscribe(subscriberId);

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("Message 1", snapshot[0].Message);
        Assert.Equal("Message 2", snapshot[1].Message);
        Assert.Equal("Message 3", snapshot[2].Message);
        Assert.Equal("TestCategory", snapshot[0].CategoryName);
        Assert.Equal(LogLevel.Information, snapshot[0].LogLevel);
        Assert.Equal(LogLevel.Warning, snapshot[1].LogLevel);
        Assert.Equal(LogLevel.Error, snapshot[2].LogLevel);
    }

    [Fact]
    public void ReplayBuffer_EvictsOldestWhenFull()
    {
        using var provider = new BackchannelLoggerProvider();

        var logger = provider.CreateLogger("TestCategory");

        // Write 1001 entries — the first should be evicted
        for (var i = 0; i < 1001; i++)
        {
            logger.LogInformation("Message {Index}", i);
        }

        var (snapshot, subscriberId, _) = provider.Subscribe();
        provider.Unsubscribe(subscriberId);

        Assert.Equal(1000, snapshot.Count);
        // First entry should be "Message 1" (index 0 was evicted)
        Assert.Equal("Message 1", snapshot[0].Message);
        Assert.Equal("Message 1000", snapshot[999].Message);
    }

    [Fact]
    public void Subscribe_ReturnsIndependentSnapshot()
    {
        using var provider = new BackchannelLoggerProvider();

        var logger = provider.CreateLogger("TestCategory");
        logger.LogInformation("Before snapshot");

        var (snapshot1, sub1, _) = provider.Subscribe();
        provider.Unsubscribe(sub1);

        logger.LogInformation("After snapshot");

        var (snapshot2, sub2, _) = provider.Subscribe();
        provider.Unsubscribe(sub2);

        // First snapshot should not be affected by subsequent writes
        Assert.Single(snapshot1);
        Assert.Equal(2, snapshot2.Count);
    }

    [Fact]
    public async Task ConcurrentSubscribers_ReceiveSameEntries()
    {
        using var provider = new BackchannelLoggerProvider();

        var logger = provider.CreateLogger("TestCategory");
        logger.LogInformation("Historical");

        // Two subscribers connect concurrently
        var (snapshot1, sub1, channel1) = provider.Subscribe();
        var (snapshot2, sub2, channel2) = provider.Subscribe();

        // Both see the historical entry
        Assert.Single(snapshot1);
        Assert.Single(snapshot2);

        // New entries arrive after both subscribe
        logger.LogInformation("Live 1");
        logger.LogInformation("Live 2");

        // Both subscribers receive both live entries
        Assert.True(channel1.Reader.TryRead(out var entry1a));
        Assert.Equal("Live 1", entry1a!.Message);
        Assert.True(channel1.Reader.TryRead(out var entry1b));
        Assert.Equal("Live 2", entry1b!.Message);

        Assert.True(channel2.Reader.TryRead(out var entry2a));
        Assert.Equal("Live 1", entry2a!.Message);
        Assert.True(channel2.Reader.TryRead(out var entry2b));
        Assert.Equal("Live 2", entry2b!.Message);

        provider.Unsubscribe(sub1);
        provider.Unsubscribe(sub2);

        // Channels are completed after unsubscribe
        await channel1.Reader.Completion;
        await channel2.Reader.Completion;
    }
}
