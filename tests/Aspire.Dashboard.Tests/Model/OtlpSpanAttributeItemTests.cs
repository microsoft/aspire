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

    [Theory]
    [InlineData("", "postgresql", DashboardUIHelpers.SqlFormat)]
    [InlineData("", "redis", null)]
    [InlineData("postgresql", "redis", DashboardUIHelpers.SqlFormat)]
    [InlineData("redis", "postgresql", null)]
    public void CreateItems_EmptyCurrentDatabaseSystem_UsesLegacyOrInheritedMetadata(string legacySystem, string inheritedSystem, string? expectedFormat)
    {
        var items = OtlpSpanAttributeItem.CreateItems(
        [
            KeyValuePair.Create("db.query.text", "SELECT 1"),
            KeyValuePair.Create("db.system.name", ""),
            KeyValuePair.Create("db.system", legacySystem)
        ],
        [KeyValuePair.Create("db.system.name", inheritedSystem)]);

        Assert.Equal(expectedFormat, items.Single(i => i.Name == "db.query.text").TextVisualizerFormat);
    }
}
