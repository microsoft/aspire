// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Validates the identifiers that describe an Entra ID app registration.
/// </summary>
/// <remarks>
/// Each method returns <see langword="null"/> for a valid value, or a sentence explaining what was expected.
/// The sentence never repeats the value, because values can come from parameters, and callers that know
/// the value is safe to show (for example, a string literal from the AppHost) add it themselves.
/// </remarks>
internal static class EntraIdValidation
{
    // The placeholder IDs that Microsoft Learn uses throughout the Entra documentation. They are shown in
    // error messages as examples of the expected format, and they identify no real tenant or application.
    internal const string ExampleTenantId = "aaaabbbb-0000-cccc-1111-dddd2222eeee";
    internal const string ExampleClientId = "00001111-aaaa-2222-bbbb-3333cccc4444";

    private const string NoValueError = "No value was provided.";

    /// <summary>
    /// Validates the ID of the app's home tenant, which must be a GUID or a domain name.
    /// </summary>
    internal static string? ValidateTenantId(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return NoValueError;
        }

        // The keywords are valid in Microsoft.Identity.Web's TenantId setting, but they choose who can sign in
        // rather than naming a tenant. WithSignInAudience emits them, so accepting one here would hide the home
        // tenant, which multi-tenant apps still need to acquire tokens as themselves.
        if (GetSignInAudienceForKeyword(tenantId) is { } audience)
        {
            return $"The keywords 'organizations', 'common' and 'consumers' choose who can sign in; they do not identify a tenant. " +
                $"Use the ID of the tenant where the app is registered, and call WithSignInAudience(EntraIdSignInAudience.{audience}) instead.";
        }

        if (IsGuid(tenantId) || IsDomainName(tenantId))
        {
            return null;
        }

        return $"Expected the directory (tenant) ID shown on the app registration's Overview page, such as '{ExampleTenantId}', " +
            "or a domain name such as 'contoso.onmicrosoft.com'.";
    }

    /// <summary>
    /// Validates the application (client) ID, which must be a GUID.
    /// </summary>
    internal static string? ValidateClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return NoValueError;
        }

        if (IsGuid(clientId))
        {
            return null;
        }

        return $"Expected the application (client) ID shown on the app registration's Overview page, such as '{ExampleClientId}'.";
    }

    /// <summary>
    /// Validates the Entra ID instance, which must be an absolute HTTPS URL.
    /// </summary>
    internal static string? ValidateInstance(string? instance)
    {
        if (string.IsNullOrEmpty(instance))
        {
            return NoValueError;
        }

        // Microsoft.Identity.Web appends the tenant and "/v2.0" to the instance to build the authority, so a
        // query string or fragment would end up in the middle of the authority URL.
        if (Uri.TryCreate(instance, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Query.Length == 0 &&
            uri.Fragment.Length == 0)
        {
            return null;
        }

        return "Expected an absolute HTTPS URL with no query string or fragment, such as 'https://login.microsoftonline.com/'.";
    }

    private static EntraIdSignInAudience? GetSignInAudienceForKeyword(string tenantId)
    {
        if (string.Equals(tenantId, "organizations", StringComparison.OrdinalIgnoreCase))
        {
            return EntraIdSignInAudience.AzureADMultipleOrgs;
        }

        if (string.Equals(tenantId, "common", StringComparison.OrdinalIgnoreCase))
        {
            return EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount;
        }

        if (string.Equals(tenantId, "consumers", StringComparison.OrdinalIgnoreCase))
        {
            return EntraIdSignInAudience.PersonalMicrosoftAccount;
        }

        return null;
    }

    // The portal shows IDs in the hyphenated "D" format, such as "00001111-aaaa-2222-bbbb-3333cccc4444".
    // The length check rejects surrounding whitespace, which Guid parsing would otherwise ignore even though
    // the untrimmed value is what reaches the application's configuration.
    private static bool IsGuid(string value) => value.Length == 36 && Guid.TryParseExact(value, "D", out _);

    // A tenant can also be named by one of its verified domains, such as "contoso.onmicrosoft.com". Requiring a
    // dot rejects single-label placeholders such as "my-tenant" that are syntactically valid host names.
    private static bool IsDomainName(string value) => Uri.CheckHostName(value) == UriHostNameType.Dns && value.Contains('.');
}
