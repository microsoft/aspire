// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Serialization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardJsonSerializerContextTests
{
    [Fact]
    public void OverflowChangedEventArgs_UsesGeneratedMetadata()
    {
        const string json =
            """{"id":"toolbar","items":[],"overflowCount":0,"firstOverflowIndex":-1,"orderedItemIds":[]}""";

        var value = JsonSerializer.Deserialize(
            json,
            DashboardJsonSerializerContext.Default.OverflowChangedEventArgs);

        Assert.IsType<OverflowChangedEventArgs>(value);
        Assert.Equal("toolbar", value.Id);
        Assert.NotNull(value.Items);
        Assert.Empty(value.Items);
        Assert.Equal(0, value.OverflowCount);
        Assert.Equal(-1, value.FirstOverflowIndex);
        Assert.NotNull(value.OrderedItemIds);
        Assert.Empty(value.OrderedItemIds);
    }
}
