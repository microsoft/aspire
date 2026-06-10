// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry.Tests;

public class StaticValueProviderTests
{
    [Fact]
    public async Task GetValueAsync_ReturnsSetValue()
    {
        var provider = new StaticValueProvider<string>();
        provider.Set("hello");

        var value = await provider.GetValueAsync();
        Assert.Equal("hello", value);
    }

    [Fact]
    public async Task GetValueAsync_ThrowsIfNotSet()
    {
        var provider = new StaticValueProvider<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetValueAsync().AsTask());
    }

    [Fact]
    public void Set_ThrowsIfAlreadySet()
    {
        var provider = new StaticValueProvider<string>();
        provider.Set("first");

        Assert.Throws<InvalidOperationException>(() => provider.Set("second"));
    }

    [Fact]
    public async Task ConstructorWithValue_SetsImmediately()
    {
        var provider = new StaticValueProvider<string>("preset");

        var value = await provider.GetValueAsync();
        Assert.Equal("preset", value);
    }

    [Fact]
    public async Task GetValueAsync_WithIntType_ReturnsStringRepresentation()
    {
        var provider = new StaticValueProvider<int>();
        provider.Set(42);

        var value = await provider.GetValueAsync();
        Assert.Equal("42", value);
    }
}
