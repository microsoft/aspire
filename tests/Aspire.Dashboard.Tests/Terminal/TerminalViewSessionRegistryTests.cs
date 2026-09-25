// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Terminal;
using Xunit;

namespace Aspire.Dashboard.Tests.Terminal;

public sealed class TerminalViewSessionRegistryTests
{
    [Fact]
    public void ReadOnlyChangesAreLimitedToOneView()
    {
        var registry = new TerminalViewSessionRegistry();
        const string endpoint = "/api/apphost-terminal?terminalId=shared";
        using var first = registry.Create(endpoint, readOnly: false);
        using var second = registry.Create(endpoint, readOnly: false);
        Assert.True(registry.TryGet(first.Id, endpoint, out var connection));
        Assert.Same(first, connection);

        first.ReadOnly = true;
        Assert.True(connection.ReadOnly);
        Assert.False(second.ReadOnly);

        first.ReadOnly = false;
        Assert.False(connection.ReadOnly);
    }

    [Fact]
    public void DisposalUnregistersViewAndDisablesExistingConnections()
    {
        var registry = new TerminalViewSessionRegistry();
        const string endpoint = "/api/terminal?resource=redis&replica=0";
        var session = registry.Create(endpoint, readOnly: false);
        Assert.True(registry.TryGet(session.Id, endpoint, out var connection));

        session.Dispose();
        session.Dispose();
        session.ReadOnly = false;

        Assert.True(connection.ReadOnly);
        Assert.False(connection.Ended.IsCompleted);
        Assert.False(registry.TryGet(session.Id, endpoint, out _));
    }

    [Fact]
    public async Task CompletionDisablesInputButRetainsViewRegistration()
    {
        var registry = new TerminalViewSessionRegistry();
        const string endpoint = "/api/apphost-terminal?terminalId=shared";
        using var session = registry.Create(endpoint, readOnly: false);
        using var otherView = registry.Create(endpoint, readOnly: false);

        session.MarkEnded();
        session.MarkEnded();
        session.ReadOnly = false;

        await session.Ended;
        Assert.True(session.ReadOnly);
        Assert.True(registry.TryGet(session.Id, endpoint, out var retained));
        Assert.Same(session, retained);
        Assert.False(otherView.Ended.IsCompleted);
        Assert.False(otherView.ReadOnly);
    }

    [Theory]
    [InlineData("/dashboard/api/terminal?resource=a%20b&replica=0", true)]
    [InlineData("/dashboard/api/terminal?viewId=viewer&replica=0&resource=a+b", true)]
    [InlineData("/dashboard/api/terminal?resource=a%20b&replica=1", false)]
    [InlineData("/api/terminal?resource=a%20b&replica=0", false)]
    [InlineData("/dashboard/api/terminal?resource=other&replica=0", false)]
    [InlineData("/dashboard/api/terminal?resource=a%20b&replica=0&replica=1", false)]
    [InlineData("/dashboard/api/apphost-terminal?terminalId=a%20b", false)]
    public void RegistrationOnlyMatchesItsEndpoint(string requestedEndpoint, bool matches)
    {
        var registry = new TerminalViewSessionRegistry();
        using var session = registry.Create("/dashboard/api/terminal?resource=a%20b&replica=0", readOnly: true);

        Assert.Equal(matches, registry.TryGet(session.Id, requestedEndpoint, out _));
        Assert.False(registry.TryGet("unknown", requestedEndpoint, out _));
    }
}
