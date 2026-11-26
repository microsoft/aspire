// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;

namespace Aspire.Hosting.GitHub.Tests;

public class GitHubAppValidationTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("123456")]
    [InlineData("18446744073709551615")]
    public void ValidateAppId_NumericId_IsValid(string appId)
    {
        Assert.Null(GitHubAppValidation.ValidateAppId(appId));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 123")]
    [InlineData("123 ")]
    [InlineData("1.5")]
    [InlineData("Iv23liAbCdEf01234567")]
    [InlineData("18446744073709551616")]
    public void ValidateAppId_NonNumericOrOutOfRange_IsInvalid(string appId)
    {
        Assert.Equal(
            "Expected the numeric App ID shown on the app's settings page, such as '123456'. The client ID, which starts with 'Iv', is a different value.",
            GitHubAppValidation.ValidateAppId(appId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_MissingValue_IsInvalid(string? value)
    {
        Assert.Equal("No value was provided.", GitHubAppValidation.ValidateAppId(value));
        Assert.Equal("No value was provided.", GitHubAppValidation.ValidateClientId(value));
        Assert.Equal("No value was provided.", GitHubAppValidation.ValidatePrivateKey(value));
        Assert.Equal("No value was provided.", GitHubAppValidation.ValidateSecret(value));
    }

    [Theory]
    [InlineData("Iv23liAbCdEf01234567")]
    [InlineData("Iv1.0123456789abcdef")]
    public void ValidateClientId_ClientId_IsValid(string clientId)
    {
        Assert.Null(GitHubAppValidation.ValidateClientId(clientId));
    }

    [Fact]
    public void ValidateClientId_NumericValue_PointsAtAppId()
    {
        Assert.Equal(
            "Expected the Client ID shown on the app's settings page, such as 'Iv23liAbCdEf01234567'. A numeric value is the App ID.",
            GitHubAppValidation.ValidateClientId("123456"));
    }

    [Theory]
    [InlineData("Iv23li AbCdEf")]
    [InlineData("Iv23liAbCdEf\n")]
    public void ValidateClientId_Whitespace_IsInvalid(string clientId)
    {
        Assert.Equal(
            "Expected the Client ID shown on the app's settings page, such as 'Iv23liAbCdEf01234567', with no whitespace.",
            GitHubAppValidation.ValidateClientId(clientId));
    }

    [Fact]
    public void ValidatePrivateKey_Pkcs1Pem_IsValid()
    {
        using var rsa = RSA.Create(2048);

        Assert.Null(GitHubAppValidation.ValidatePrivateKey(rsa.ExportRSAPrivateKeyPem()));
    }

    [Fact]
    public void ValidatePrivateKey_Pkcs8Pem_IsValid()
    {
        using var rsa = RSA.Create(2048);

        Assert.Null(GitHubAppValidation.ValidatePrivateKey(rsa.ExportPkcs8PrivateKeyPem()));
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nbm90IGEga2V5\n-----END RSA PRIVATE KEY-----")]
    public void ValidatePrivateKey_NotPem_IsInvalid(string privateKey)
    {
        Assert.Equal(
            "Expected the contents of the .pem file generated on the app's settings page, which starts with '-----BEGIN RSA PRIVATE KEY-----'.",
            GitHubAppValidation.ValidatePrivateKey(privateKey));
    }

    [Fact]
    public void ValidatePrivateKey_EscapedLineBreaks_ExplainsHowToStoreTheKey()
    {
        using var rsa = RSA.Create(2048);
        var escaped = rsa.ExportRSAPrivateKeyPem().Replace("\n", "\\n", StringComparison.Ordinal);

        Assert.Equal(
            "Expected an RSA private key in PEM format. The value contains '\\n' character sequences instead of line breaks; store the key with real line breaks, " +
            "for example with 'aspire secret set <parameter> \"$(cat key.pem)\"'.",
            GitHubAppValidation.ValidatePrivateKey(escaped));
    }

    [Fact]
    public void ValidatePrivateKey_PublicKey_IsInvalid()
    {
        using var rsa = RSA.Create(2048);

        Assert.Equal(
            "Expected the contents of the .pem file generated on the app's settings page, which starts with '-----BEGIN RSA PRIVATE KEY-----'.",
            GitHubAppValidation.ValidatePrivateKey(rsa.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("my-app", null)]
    [InlineData("app2", "contoso")]
    [InlineData("a", "c-o-n-t-o-s-o")]
    public void ValidateSlugAndOrganization_Valid(string slug, string? organization)
    {
        Assert.Null(GitHubAppValidation.ValidateSlug(slug));
        if (organization is not null)
        {
            Assert.Null(GitHubAppValidation.ValidateOrganization(organization));
        }
    }

    [Theory]
    [InlineData("My-App")]
    [InlineData("my app")]
    [InlineData("my_app")]
    [InlineData("my/app")]
    public void ValidateSlug_Invalid(string slug)
    {
        Assert.NotNull(GitHubAppValidation.ValidateSlug(slug));
    }

    [Theory]
    [InlineData("-contoso")]
    [InlineData("contoso-")]
    [InlineData("con--toso")]
    [InlineData("con toso")]
    [InlineData("contoso/apps")]
    [InlineData("a234567890123456789012345678901234567890")]
    public void ValidateOrganization_Invalid(string organization)
    {
        Assert.NotNull(GitHubAppValidation.ValidateOrganization(organization));
    }
}
