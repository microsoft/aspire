// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Core.Tests;

public class ResourceTypeIdTests
{
    [Theory]
    [InlineData("example/Cache")]
    [InlineData("Aspire.Hosting.Redis/Aspire.Hosting.ApplicationModel.RedisServerResource")]
    public void ValidIdentifiersPreserveTheirWireSpellingAndOrdinalEquality(string value)
    {
        var type = new ResourceTypeId(value);
        Assert.Equal(value, type.Value);
        Assert.Equal(value, type.ToString());
        Assert.Equal(type, new ResourceTypeId(value));
        Assert.NotEqual(type, new ResourceTypeId(value.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Cache")]
    [InlineData("example/")]
    [InlineData("example/Cache/extra")]
    [InlineData("example/ Cache")]
    [InlineData("example/Cache\0")]
    public void InvalidIdentifiersCannotEnterTheModel(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new ResourceTypeId(value));
}
