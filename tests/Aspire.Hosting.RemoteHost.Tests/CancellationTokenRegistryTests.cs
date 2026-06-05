// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class CancellationTokenRegistryTests
{
    [Fact]
    public void Create_ReturnsUniqueTokenId()
    {
        using var registry = new CancellationTokenRegistry();

        var (id1, _) = registry.Create();
        var (id2, _) = registry.Create();

        Assert.NotEqual(id1, id2);
        Assert.StartsWith("ct_", id1);
        Assert.StartsWith("ct_", id2);
    }

    [Fact]
    public void Create_ReturnsValidCancellationToken()
    {
        using var registry = new CancellationTokenRegistry();

        var (_, token) = registry.Create();

        Assert.False(token.IsCancellationRequested);
        Assert.True(token.CanBeCanceled);
    }

    [Fact]
    public void CreateLinked_TokenIsCancelledWhenLinkedTokenCancels()
    {
        using var registry = new CancellationTokenRegistry();
        using var cts = new CancellationTokenSource();

        var (_, token) = registry.CreateLinked(cts.Token);

        Assert.False(token.IsCancellationRequested);

        cts.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_CancelsRegisteredToken()
    {
        using var registry = new CancellationTokenRegistry();
        var (tokenId, token) = registry.Create();

        Assert.False(token.IsCancellationRequested);

        var result = registry.Cancel(tokenId);

        Assert.True(result);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_ReturnsFalseForUnknownId()
    {
        using var registry = new CancellationTokenRegistry();

        var result = registry.Cancel("ct_unknown");

        Assert.False(result);
    }

    [Fact]
    public void TryGetToken_ReturnsTrueAndTokenForRegisteredId()
    {
        using var registry = new CancellationTokenRegistry();
        var (tokenId, originalToken) = registry.Create();

        var found = registry.TryGetToken(tokenId, out var retrievedToken);

        Assert.True(found);
        Assert.Equal(originalToken, retrievedToken);
    }

    [Fact]
    public void TryGetToken_ReturnsFalseForUnknownId()
    {
        using var registry = new CancellationTokenRegistry();

        var found = registry.TryGetToken("ct_unknown", out var token);

        Assert.False(found);
        Assert.Equal(default, token);
    }

    [Fact]
    public void Unregister_RemovesTokenAndReturnsTrue()
    {
        using var registry = new CancellationTokenRegistry();
        var (tokenId, _) = registry.Create();

        var result = registry.Unregister(tokenId);

        Assert.True(result);
        Assert.False(registry.TryGetToken(tokenId, out _));
    }

    [Fact]
    public void Unregister_ReturnsFalseForUnknownId()
    {
        using var registry = new CancellationTokenRegistry();

        var result = registry.Unregister("ct_unknown");

        Assert.False(result);
    }

    [Fact]
    public void Dispose_ClearsAllRegisteredTokens()
    {
        var registry = new CancellationTokenRegistry();
        var (tokenId1, _) = registry.Create();
        var (tokenId2, _) = registry.Create();

        registry.Dispose();

        // After dispose, TryGetToken should return false
        Assert.False(registry.TryGetToken(tokenId1, out _));
        Assert.False(registry.TryGetToken(tokenId2, out _));
    }

    [Fact]
    public void Create_ThrowsAfterDispose()
    {
        var registry = new CancellationTokenRegistry();
        registry.Dispose();

        Assert.Throws<ObjectDisposedException>(() => registry.Create());
    }

    [Fact]
    public void CreateLinked_ThrowsAfterDispose()
    {
        var registry = new CancellationTokenRegistry();
        registry.Dispose();

        Assert.Throws<ObjectDisposedException>(() => registry.CreateLinked(CancellationToken.None));
    }
}
