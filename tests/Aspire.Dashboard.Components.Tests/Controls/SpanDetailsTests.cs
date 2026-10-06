// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class SpanDetailsTests : DashboardTestContext
{
    [Theory]
    [InlineData("redis", null)]
    [InlineData("postgresql", DashboardUIHelpers.SqlFormat)]
    public void GetAttributeItems_InheritsDatabaseSystem(string system, string? expectedFormat)
    {
        var items = SpanDetails.GetAttributeItems(
            [KeyValuePair.Create("db.query.text", "SELECT 1")],
            [KeyValuePair.Create("db.system.name", system)]);

        Assert.Equal(expectedFormat, Assert.Single(items).TextVisualizerFormat);
    }

    [Fact]
    public void GetAttributeItems_PrefersOwnDatabaseSystem()
    {
        var items = SpanDetails.GetAttributeItems(
            [KeyValuePair.Create("db.statement", "SELECT 1"), KeyValuePair.Create("db.system", "redis")],
            [KeyValuePair.Create("db.system.name", "postgresql")]);

        Assert.Null(items.Single(i => i.Name == "db.statement").TextVisualizerFormat);
    }
}
