// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure.Core;

namespace Aspire.Hosting.Azure.Tests;

public class DefaultAzurePrincipalProviderTests
{
    [Fact]
    public async Task GetPrincipalAsync_ReturnsValidPrincipal()
    {
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.NotNull(principal);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), principal.Id);
        Assert.Equal("test@example.com", principal.Name);
    }

    [Fact]
    public async Task GetPrincipalAsync_ParsesTokenCorrectly()
    {
        var expectedOid = Guid.NewGuid();
        var expectedUpn = "test@example.com";
        var token = CreateTestToken(expectedOid, upn: expectedUpn);
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal(expectedOid, principal.Id);
        Assert.Equal(expectedUpn, principal.Name);
    }

    [Fact]
    public async Task GetPrincipalAsync_ParsesTokenWithEmail()
    {
        var expectedOid = Guid.NewGuid();
        var expectedEmail = "user@company.com";
        var token = CreateTestToken(expectedOid, email: expectedEmail);
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal(expectedOid, principal.Id);
        Assert.Equal(expectedEmail, principal.Name);
    }

    [Fact]
    public async Task GetPrincipalAsync_DefaultsTypeToUserWhenIdtypMissing()
    {
        // Legacy tokens and some federated flows omit the `idtyp` claim. Defaulting to "User"
        // preserves the behavior that shipped before the auto-detect change.
        var token = CreateTestToken(Guid.NewGuid(), upn: "test@example.com");
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("User", principal.Type);
    }

    [Fact]
    public async Task GetPrincipalAsync_DetectsUserPrincipalFromIdtyp()
    {
        var token = CreateTestToken(Guid.NewGuid(), upn: "test@example.com", idtyp: "user");
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("User", principal.Type);
    }

    [Fact]
    public async Task GetPrincipalAsync_DetectsServicePrincipalFromIdtyp()
    {
        // App-only tokens (service principal / managed identity / federated workload identity)
        // carry `idtyp: "app"`. The role-assignment Bicep child resources need
        // `principalType: 'ServicePrincipal'` in this case, otherwise ARM rejects them with
        // PrincipalNotFound / UnmatchedPrincipalType. See:
        // https://github.com/microsoft/aspire/issues/13933
        var token = CreateTestToken(Guid.NewGuid(), upn: string.Empty, idtyp: "app");
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("ServicePrincipal", principal.Type);
    }

    [Fact]
    public async Task GetPrincipalAsync_TreatsIdtypComparisonCaseInsensitively()
    {
        var token = CreateTestToken(Guid.NewGuid(), upn: string.Empty, idtyp: "APP");
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("ServicePrincipal", principal.Type);
    }

    [Fact]
    public async Task GetPrincipalAsync_HandlesCancellation()
    {
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.GetPrincipalAsync(cts.Token));
    }

    [Fact]
    public async Task GetPrincipalAsync_RespectsTokenCredentialProviderDependency()
    {
        var customTokenCredential = new TestTokenCredential();
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCredential(customTokenCredential);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.NotNull(principal);
        Assert.True(customTokenCredential.GetTokenCalled);
    }

    // Produces a JWT-shaped string ("header.payload.signature") with the requested claims.
    // The signature is not validated by the provider; only the base64url-encoded payload is parsed.
    private static string CreateTestToken(Guid oid, string? upn = null, string? email = null, string? idtyp = null)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" })));

        var payload = new Dictionary<string, object?>
        {
            ["oid"] = oid.ToString(),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        if (!string.IsNullOrEmpty(upn))
        {
            payload["upn"] = upn;
        }
        if (!string.IsNullOrEmpty(email))
        {
            payload["email"] = email;
        }
        if (!string.IsNullOrEmpty(idtyp))
        {
            payload["idtyp"] = idtyp;
        }

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var signature = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-signature"));

        return $"{header}.{payloadBase64}.{signature}";
    }

    private sealed class TestTokenCredentialProviderWithCustomToken(string token) : ITokenCredentialProvider
    {
        public TokenCredential TokenCredential => new TestTokenCredentialWithCustomToken(token);
    }

    private sealed class TestTokenCredentialWithCustomToken(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TestTokenCredentialProviderWithCredential(TestTokenCredential tokenCredential) : ITokenCredentialProvider
    {
        public TokenCredential TokenCredential => tokenCredential;
    }

    private sealed class TestTokenCredential : TokenCredential
    {
        public bool GetTokenCalled { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            GetTokenCalled = true;
            return new AccessToken(CreateTestToken(Guid.NewGuid(), upn: "test@example.com"), DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken = default)
        {
            GetTokenCalled = true;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(CreateTestToken(Guid.NewGuid(), upn: "test@example.com"), DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
