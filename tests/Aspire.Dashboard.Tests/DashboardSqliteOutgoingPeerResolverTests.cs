// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardSqliteOutgoingPeerResolverTests
{
    [Fact]
    public void TryResolvePeer_DashboardSqlite_ReturnsDatabaseName()
    {
        var resolver = new DashboardSqliteOutgoingPeerResolver();

        var result = resolver.TryResolvePeer(CreateAttributes(), out var name, out var resource);

        Assert.True(result);
        Assert.Equal("dashboard.db", name);
        Assert.Null(resource);
    }

    [Theory]
    [InlineData(OtlpSpan.PeerServiceAttributeKey, "postgresql")]
    [InlineData("db.system.name", "postgresql")]
    [InlineData("db.namespace", "application.db")]
    public void TryResolvePeer_NonDashboardSqlite_ReturnsFalse(string attributeName, string attributeValue)
    {
        var resolver = new DashboardSqliteOutgoingPeerResolver();
        var attributes = CreateAttributes();
        var attributeIndex = Array.FindIndex(attributes, attribute => attribute.Key == attributeName);
        attributes[attributeIndex] = KeyValuePair.Create(attributeName, attributeValue);

        var result = resolver.TryResolvePeer(attributes, out var name, out var resource);

        Assert.False(result);
        Assert.Null(name);
        Assert.Null(resource);
    }

    private static KeyValuePair<string, string>[] CreateAttributes() =>
    [
        KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "dashboard.db"),
        KeyValuePair.Create("db.system.name", "sqlite"),
        KeyValuePair.Create("db.namespace", "dashboard.db")
    ];
}