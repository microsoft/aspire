// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;

namespace Aspire.Hosting.GitHub;

/// <summary>
/// Validates the values that describe a GitHub App.
/// </summary>
/// <remarks>
/// Each method returns <see langword="null"/> for a valid value, or a sentence explaining what was expected. The sentence
/// never repeats the value, because values come from parameters that may be secret.
/// </remarks>
internal static class GitHubAppValidation
{
    private const string NoValueError = "No value was provided.";

    /// <summary>
    /// Validates the app ID, which GitHub assigns as a positive integer.
    /// </summary>
    internal static string? ValidateAppId(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return NoValueError;
        }

        // GitHub signs the app's JSON Web Tokens with the app ID as the "iss" claim, and rejects tokens whose issuer
        // isn't the numeric ID. Parsing as an invariant, unsigned integer also rejects surrounding whitespace and signs.
        if (appId.All(char.IsAsciiDigit) && ulong.TryParse(appId, out var id) && id > 0)
        {
            return null;
        }

        return "Expected the numeric App ID shown on the app's settings page, such as '123456'. The client ID, which starts with 'Iv', is a different value.";
    }

    /// <summary>
    /// Validates the client ID, such as <c>Iv23liAbCdEf01234567</c>.
    /// </summary>
    internal static string? ValidateClientId(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return NoValueError;
        }

        // The format of client IDs has changed over time ("Iv1.0123456789abcdef", then "Iv23li..."), so only reject values
        // that can't be an identifier, such as a pasted value with whitespace or a numeric app ID in the wrong parameter.
        if (clientId.All(char.IsAsciiDigit))
        {
            return "Expected the Client ID shown on the app's settings page, such as 'Iv23liAbCdEf01234567'. A numeric value is the App ID.";
        }

        if (clientId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return null;
        }

        return "Expected the Client ID shown on the app's settings page, such as 'Iv23liAbCdEf01234567', with no whitespace.";
    }

    /// <summary>
    /// Validates the app's private key, which must be an RSA key in PEM format.
    /// </summary>
    internal static string? ValidatePrivateKey(string? privateKey)
    {
        if (string.IsNullOrEmpty(privateKey))
        {
            return NoValueError;
        }

        // GitHub generates PKCS#1 keys ("-----BEGIN RSA PRIVATE KEY-----"), and tools that convert them produce PKCS#8
        // ("-----BEGIN PRIVATE KEY-----"). ImportFromPem accepts both, and finds the key among other text in the value.
        // It also accepts public keys, which can't sign, so check that the private parameters were imported too.
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            _ = rsa.ExportParameters(includePrivateParameters: true);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
        }

        // A multi-line value is often stored on one line with escaped line breaks, such as
        //   -----BEGIN RSA PRIVATE KEY-----\nMIIEow...\n-----END RSA PRIVATE KEY-----
        // which isn't PEM, and which a consumer such as Octokit can't read either.
        if (privateKey.Contains("\\n", StringComparison.Ordinal))
        {
            return "Expected an RSA private key in PEM format. The value contains '\\n' character sequences instead of line breaks; store the key with real line breaks, " +
                "for example with 'aspire secret set <parameter> \"$(cat key.pem)\"'.";
        }

        return "Expected the contents of the .pem file generated on the app's settings page, which starts with '-----BEGIN RSA PRIVATE KEY-----'.";
    }

    /// <summary>
    /// Validates a client secret or webhook secret, which must not be empty.
    /// </summary>
    internal static string? ValidateSecret(string? secret) => string.IsNullOrEmpty(secret) ? NoValueError : null;

    /// <summary>
    /// Validates an app's slug, which GitHub derives from its name using lowercase letters, digits and hyphens.
    /// </summary>
    internal static string? ValidateSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return NoValueError;
        }

        if (slug.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return null;
        }

        return "Expected the app's slug, the last segment of its public page 'https://github.com/apps/{slug}', using lowercase letters, digits and hyphens, such as 'my-app'.";
    }

    /// <summary>
    /// Validates an organization login, which GitHub limits to 39 letters, digits and single hyphens that don't begin or end it.
    /// </summary>
    internal static string? ValidateOrganization(string? organization)
    {
        if (string.IsNullOrEmpty(organization))
        {
            return NoValueError;
        }

        if (organization.Length <= 39 &&
            organization[0] != '-' &&
            organization[^1] != '-' &&
            !organization.Contains("--", StringComparison.Ordinal) &&
            organization.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return null;
        }

        return "Expected an organization login of at most 39 letters, digits and single hyphens that don't begin or end it, such as 'contoso'.";
    }

    /// <summary>
    /// Validates the name of a permission in a GitHub App manifest, such as <c>issues</c> or <c>pull_requests</c>.
    /// </summary>
    internal static string? ValidatePermissionName(string? permission)
    {
        if (string.IsNullOrEmpty(permission))
        {
            return NoValueError;
        }

        if (char.IsAsciiLetterLower(permission[0]) &&
            permission.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return null;
        }

        return "Expected GitHub's name for the permission, using lowercase letters, digits and underscores, such as 'issues' or 'pull_requests'.";
    }
}
