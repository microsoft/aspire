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

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("header.payload")]
    public async Task GetPrincipalAsync_ThrowsClearErrorForMalformedToken(string token)
    {
        // Real Azure AD always returns a 3-segment JWT, but a misbehaving custom credential
        // could hand back something else. The provider must surface that as a descriptive
        // InvalidOperationException instead of leaking an IndexOutOfRangeException from the
        // payload split.
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(token);
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPrincipalAsync());
        Assert.Contains("not a valid JWT", ex.Message);
    }

    // Produces a JWT-shaped string ("header.payload.signature") with the requested claims.
    // All three segments use base64url encoding (RFC 7515 §3) so the helper matches the wire
    // format real Azure AD tokens use, even though the provider currently only decodes the
    // payload segment.
    private static string CreateTestToken(Guid oid, string? upn = null, string? email = null, string? idtyp = null)
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));

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

        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var signature = Base64UrlEncode(Encoding.UTF8.GetBytes("test-signature"));

        return $"{header}.{payloadBase64}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
