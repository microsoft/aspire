// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.EntraId.Tests;

public class EntraIdValidationTests
{
    private const string TenantIdError =
        "Expected the directory (tenant) ID shown on the app registration's Overview page, such as 'aaaabbbb-0000-cccc-1111-dddd2222eeee', " +
        "or a domain name such as 'contoso.onmicrosoft.com'.";
    private const string ClientIdError =
        "Expected the application (client) ID shown on the app registration's Overview page, such as '00001111-aaaa-2222-bbbb-3333cccc4444'.";
    private const string InstanceError =
        "Expected an absolute HTTPS URL with no query string or fragment, such as 'https://login.microsoftonline.com/'.";

    [Theory]
    [InlineData("aaaabbbb-0000-cccc-1111-dddd2222eeee")]
    [InlineData("AAAABBBB-0000-CCCC-1111-DDDD2222EEEE")]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("contoso.com")]
    public void ValidateTenantId_AcceptsGuidOrDomainName(string tenantId)
    {
        Assert.Null(EntraIdValidation.ValidateTenantId(tenantId));
    }

    [Theory]
    // Single labels are valid host names, but no tenant domain looks like this.
    [InlineData("my-tenant")]
    [InlineData("contoso")]
    // Guid parsing accepts these forms too, but the value reaches the app's configuration as written.
    [InlineData(" aaaabbbb-0000-cccc-1111-dddd2222eeee")]
    [InlineData("{aaaabbbb-0000-cccc-1111-dddd2222eeee}")]
    [InlineData("aaaabbbb0000cccc1111dddd2222eeee")]
    [InlineData("https://login.microsoftonline.com/contoso.onmicrosoft.com")]
    [InlineData("10.0.0.1")]
    public void ValidateTenantId_RejectsOtherValues(string tenantId)
    {
        Assert.Equal(TenantIdError, EntraIdValidation.ValidateTenantId(tenantId));
    }

    [Theory]
    [InlineData("00001111-aaaa-2222-bbbb-3333cccc4444")]
    [InlineData("00001111-AAAA-2222-BBBB-3333CCCC4444")]
    public void ValidateClientId_AcceptsGuid(string clientId)
    {
        Assert.Null(EntraIdValidation.ValidateClientId(clientId));
    }

    [Theory]
    [InlineData("my-client")]
    [InlineData("00001111-aaaa-2222-bbbb-3333cccc4444 ")]
    [InlineData("{00001111-aaaa-2222-bbbb-3333cccc4444}")]
    [InlineData("00001111aaaa2222bbbb3333cccc4444")]
    // The Application ID URI is shown next to the client ID on the Overview page, so it is easy to copy by mistake.
    [InlineData("api://00001111-aaaa-2222-bbbb-3333cccc4444")]
    public void ValidateClientId_RejectsOtherValues(string clientId)
    {
        Assert.Equal(ClientIdError, EntraIdValidation.ValidateClientId(clientId));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/")]
    [InlineData("https://login.microsoftonline.com")]
    [InlineData("https://contoso.ciamlogin.com/")]
    // Emulators and proxies used in development run on other hosts.
    [InlineData("https://localhost:8443/")]
    public void ValidateInstance_AcceptsHttpsUrl(string instance)
    {
        Assert.Null(EntraIdValidation.ValidateInstance(instance));
    }

    [Theory]
    [InlineData("login.microsoftonline.com")]
    [InlineData("http://login.microsoftonline.com/")]
    [InlineData("ftp://login.microsoftonline.com/")]
    [InlineData("https://login.microsoftonline.com/?slice=testslice")]
    [InlineData("https://login.microsoftonline.com/#tenant")]
    public void ValidateInstance_RejectsOtherValues(string instance)
    {
        Assert.Equal(InstanceError, EntraIdValidation.ValidateInstance(instance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_RejectsMissingValue(string? value)
    {
        Assert.Equal("No value was provided.", EntraIdValidation.ValidateTenantId(value));
        Assert.Equal("No value was provided.", EntraIdValidation.ValidateClientId(value));
        Assert.Equal("No value was provided.", EntraIdValidation.ValidateInstance(value));
    }
}
