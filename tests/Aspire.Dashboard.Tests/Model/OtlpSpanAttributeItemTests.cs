// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class OtlpSpanAttributeItemTests
{
    [Theory]
    [InlineData("redis", null)]
    [InlineData("postgresql", DashboardUIHelpers.SqlFormat)]
    public void CreateItems_InheritsDatabaseSystem(string system, string? expectedFormat)
    {
        var items = OtlpSpanAttributeItem.CreateItems(
            [KeyValuePair.Create("db.query.text", "SELECT 1")],
            [KeyValuePair.Create("db.system.name", system)]);

        Assert.Equal(expectedFormat, Assert.Single(items).TextVisualizerFormat);
    }

    [Fact]
    public void CreateItems_PrefersOwnDatabaseSystem()
    {
        var items = OtlpSpanAttributeItem.CreateItems(
            [KeyValuePair.Create("db.statement", "SELECT 1"), KeyValuePair.Create("db.system", "redis")],
            [KeyValuePair.Create("db.system.name", "postgresql")]);

        Assert.Null(items.Single(i => i.Name == "db.statement").TextVisualizerFormat);
    }
}
