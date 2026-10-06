// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests;

public class AppHostIdTests
{
    [Fact]
    public void IdentityPreservesTheSourcePathCasing()
    {
        var path = Path.GetFullPath(Path.Combine("MyWorktree", "Shop.AppHost", "AppHost.cs"));
        var host = new AppHostInfo(path, 42, null) { ProcessStartTimeUnixMilliseconds = 1000 };

        Assert.Equal(path, host.Id.AppHostPath);
        Assert.Equal(42, host.Id.AppHostPid);
        Assert.Equal(1000, host.Id.ProcessStartTimeUnixMilliseconds);
    }

    [Fact]
    public void EqualityAndHashCollectionsUsePlatformPathSemantics()
    {
        var path = Path.GetFullPath(Path.Combine("MyWorktree", "Shop.AppHost", "AppHost.cs"));
        var first = new AppHostId(path, 42, 1000);
        var other = first with { AppHostPath = path.ToUpperInvariant() };
        var windows = OperatingSystem.IsWindows();
        var ids = new HashSet<AppHostId> { first, other };
        var values = new Dictionary<AppHostId, string> { [first] = "original" };

        Assert.Equal(windows, first == other);
        Assert.Equal(!windows, first != other);
        Assert.Equal(windows, first.Equals((object)other));
        Assert.Equal(windows ? 1 : 2, ids.Count);
        Assert.Equal(windows, values.TryGetValue(other, out var value));
        if (windows)
        {
            Assert.Equal(first.GetHashCode(), other.GetHashCode());
            Assert.Equal("original", value);
        }
        Assert.Equal(path, first.AppHostPath);
    }

    [Theory]
    [InlineData(43, 1000L)]
    [InlineData(42, 1001L)]
    [InlineData(42, null)]
    public void EqualityStillRequiresTheExactProcessLifetime(int pid, long? startedAt)
    {
        var first = new AppHostId(Path.GetFullPath("AppHost.cs"), 42, 1000);
        var other = first with { AppHostPid = pid, ProcessStartTimeUnixMilliseconds = startedAt };

        Assert.NotEqual(first, other);
        Assert.True(new HashSet<AppHostId> { first }.Add(other));
    }

    [Fact]
    public void DefaultIdentityCanBeUsedAsAHashKey()
    {
        var values = new Dictionary<AppHostId, string> { [default] = "unbound" };

        Assert.Equal("unbound", values[default]);
        Assert.NotEqual(default, new AppHostId("", 0, null));
    }
}
