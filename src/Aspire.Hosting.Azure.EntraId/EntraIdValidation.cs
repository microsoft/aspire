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
    /// Validates the client ID that selects a user-assigned managed identity for federation.
    /// </summary>
    internal static string? ValidateManagedIdentityClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return NoValueError;
        }

        return IsGuid(clientId)
            ? null
            : "Expected the Client ID of a user-assigned managed identity as a GUID, not its Azure resource ID.";
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

    /// <summary>
    /// Validates an application ID URI, which must be an absolute URI without a query string or fragment.
    /// </summary>
    internal static string? ValidateApplicationIdUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return NoValueError;
        }

        // Microsoft Graph limits identifier URIs to 255 characters and rejects ones that contain whitespace.
        if (uri.Length <= 255 &&
            !uri.Any(char.IsWhiteSpace) &&
            !uri.Contains('?') &&
            !uri.Contains('#') &&
            Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            return null;
        }

        return "Expected an absolute URI of at most 255 characters with no whitespace, query string or fragment, such as 'api://contoso-orders' or 'https://contoso.onmicrosoft.com/orders'.";
    }

    /// <summary>
    /// Validates the name of an exposed scope, the value that appears in the token's <c>scp</c> claim.
    /// </summary>
    internal static string? ValidateScopeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return NoValueError;
        }

        // The granted scopes of a consent are stored as one space-separated string, so a scope name cannot contain a space.
        if (name.Length <= 120 && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':'))
        {
            return null;
        }

        return "Expected at most 120 ASCII letters, digits, '.', '_', '-' or ':' characters, such as 'access_as_user'.";
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
