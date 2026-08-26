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

    [Theory]
    [InlineData("""{"upn":"test@example.com","idtyp":"user"}""")]
    [InlineData("""{"oid":"not-a-guid","idtyp":"user"}""")]
    [InlineData("""{"oid":{"value":"11111111-2222-3333-4444-555555555555"},"idtyp":"user"}""")]
    public async Task GetPrincipalAsync_ThrowsClearErrorWhenOidClaimIsUnusable(string payloadJson)
    {
        // The principal id becomes the `principalId` of an ARM role assignment, so a token without
        // a usable `oid` must fail loudly here rather than surfacing later as an opaque
        // "Unrecognized Guid format" FormatException from the middle of provisioning.
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPrincipalAsync());
        Assert.Contains("'oid'", ex.Message);
    }

    [Theory]
    // A delegation-style `act` claim (RFC 8693 §4.1) carrying a competing identity, positioned
    // after the real claims so a last-write-wins scan would end up preferring it.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"app","act":{"oid":"99999999-9999-9999-9999-999999999999","idtyp":"user"}}
        """, "ServicePrincipal")]
    // Same shape but nested inside an array element rather than a bare object.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"app","things":[{"oid":"99999999-9999-9999-9999-999999999999","idtyp":"user"}]}
        """, "ServicePrincipal")]
    // Entra's groups-overage payload nests an object under `_claim_sources`; this variant hides a
    // conflicting `idtyp` there while the token itself carries none, so no ordering is involved.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","_claim_sources":{"src1":{"idtyp":"app"}}}
        """, "User")]
    public async Task GetPrincipalAsync_IgnoresClaimsNestedInsideOtherClaims(string payloadJson, string expectedPrincipalType)
    {
        // Claims are top-level by definition, but a claim's *value* may be an object or array.
        // Those nested properties must never be mistaken for real claims: the parsed principal is
        // written straight into the principalId/principalType of an ARM role assignment, so
        // picking up a nested identity would grant access to the wrong principal.
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), principal.Id);
        Assert.Equal(expectedPrincipalType, principal.Type);
    }

    [Fact]
    public async Task GetPrincipalAsync_IgnoresNameClaimsNestedInsideOtherClaims()
    {
        var payloadJson = """
            {"oid":"11111111-2222-3333-4444-555555555555","upn":"real@example.com","act":{"upn":"nested@example.com","email":"nested@example.com"}}
            """;
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("real@example.com", principal.Name);
    }

    [Fact]
    public async Task GetPrincipalAsync_PrefersUpnOverEmailWhenBothPresent()
    {
        // `email` is only a fallback for accounts that carry no `upn`, so a token with both must
        // resolve to the `upn` regardless of the order the two claims appear in the payload.
        var payloadJson = """
            {"oid":"11111111-2222-3333-4444-555555555555","email":"fallback@example.com","upn":"primary@example.com"}
            """;
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("primary@example.com", principal.Name);
    }

    [Fact]
    public async Task GetPrincipalAsync_UsesAppDisplayNameForAppOnlyTokens()
    {
        // App-only tokens carry neither `upn` nor `email`, so before this fallback existed
        // `principalName` was written as an empty string. `app_displayname` is the display name of
        // the app registration, which is the name Entra reports for its service principal and the
        // value Azure Database for PostgreSQL requires on
        // Microsoft.DBforPostgreSQL/flexibleServers/administrators.
        // See https://github.com/microsoft/aspire/issues/19487.
        var payloadJson = """
            {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"app","app_displayname":"aspire-deployment-tests"}
            """;
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("ServicePrincipal", principal.Type);
        Assert.Equal("aspire-deployment-tests", principal.Name);
    }

    [Theory]
    // A user-delegated token from `az login`: `app_displayname` names the *client application*, not
    // the signed-in identity, so the `upn` must win.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"user","upn":"real@example.com","app_displayname":"Microsoft Azure CLI"}
        """, "real@example.com")]
    // Same client-application claim, but with no `upn`/`email` to fall back to. Using
    // "Microsoft Azure CLI" as the principal name here would be badly wrong, so the object id is
    // used instead.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"user","app_displayname":"Microsoft Azure CLI"}
        """, "11111111-2222-3333-4444-555555555555")]
    public async Task GetPrincipalAsync_IgnoresAppDisplayNameForUserTokens(string payloadJson, string expectedName)
    {
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal(expectedName, principal.Name);
    }

    [Theory]
    // App-only token without the optional `app_displayname` claim. Entra documents that claim as one
    // callers must not depend on, so this is the shape the fallback has to cover.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"app"}
        """)]
    // A token carrying no name claim of any kind.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555"}
        """)]
    // Structurally unexpected claim values are treated as "not supplied" rather than being coerced,
    // so they must land on the same fallback instead of producing an empty name.
    [InlineData("""
        {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"app","upn":"","email":"","app_displayname":42}
        """)]
    public async Task GetPrincipalAsync_FallsBackToObjectIdWhenNoNameClaimIsUsable(string payloadJson)
    {
        // An empty `principalName` is not benign: Azure SQL's principal reconciliation script runs
        // `CREATE USER QUOTENAME(@name)`, and QUOTENAME('') produces the invalid identifier `[]`.
        // The object id is the only claim guaranteed to be present, so it is what makes the result
        // non-empty in every case.
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("11111111-2222-3333-4444-555555555555", principal.Name);
    }

    [Fact]
    public async Task GetPrincipalAsync_IgnoresAppDisplayNameNestedInsideOtherClaims()
    {
        // Same structural guarantee the other nested-claim tests assert, extended to the name
        // fallback: a display name buried inside another claim's value is not a claim, so the
        // object id fallback must still apply.
        var payloadJson = """
            {"oid":"11111111-2222-3333-4444-555555555555","idtyp":"app","act":{"app_displayname":"nested-app","upn":"nested@example.com"}}
            """;
        var tokenCredentialProvider = new TestTokenCredentialProviderWithCustomToken(CreateTokenFromPayload(payloadJson));
        var provider = new DefaultAzurePrincipalProvider(tokenCredentialProvider);

        var principal = await provider.GetPrincipalAsync();

        Assert.Equal("11111111-2222-3333-4444-555555555555", principal.Name);
    }

    // Produces a JWT-shaped string ("header.payload.signature") with the requested claims.
    // All three segments use base64url encoding (RFC 7515 §3) so the helper matches the wire
    // format real Azure AD tokens use, even though the provider currently only decodes the
    // payload segment.
    private static string CreateTestToken(Guid oid, string? upn = null, string? email = null, string? idtyp = null)
    {
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

        return CreateTokenFromPayload(JsonSerializer.Serialize(payload));
    }

    // Wraps a verbatim payload in JWT framing. Tests that care about claim ordering or nesting
    // supply the JSON directly so the exact wire shape under test is visible in the test itself
    // rather than depending on how a serializer happens to order a dictionary.
    private static string CreateTokenFromPayload(string payloadJson)
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signature = Base64UrlEncode(Encoding.UTF8.GetBytes("test-signature"));

        return $"{header}.{payload}.{signature}";
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
