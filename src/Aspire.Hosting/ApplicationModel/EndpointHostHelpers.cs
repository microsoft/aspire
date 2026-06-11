// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides helper methods for validating localhost addresses.
/// </summary>
public static class EndpointHostHelpers
{
    /// <summary>
    /// Determines whether the specified host is "localhost".
    /// </summary>
    /// <param name="host">The host to check.</param>
    /// <returns>
    /// <c>true</c> if the host is "localhost" (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocalhost([NotNullWhen(true)] string? host)
    {
        return host is not null && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the specified URI uses a host that is "localhost".
    /// </summary>
    /// <param name="uri">The URI to check.</param>
    /// <returns>
    /// <c>true</c> if the host is "localhost" (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocalhost([NotNullWhen(true)] Uri? uri)
    {
        return uri?.Host is not null && IsLocalhost(uri.Host);
    }

    /// <summary>
    /// Determines whether the specified host ends with ".localhost".
    /// </summary>
    /// <param name="host">The host to check.</param>
    /// <returns>
    /// <c>true</c> if the host ends with ".localhost" (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocalhostTld([NotNullWhen(true)] string? host)
    {
        return host is not null && host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the specified host ends with ".dev.localhost".
    /// </summary>
    /// <param name="host">The host to check.</param>
    /// <returns>
    /// <c>true</c> if the host ends with ".dev.localhost" (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsDevLocalhostTld([NotNullWhen(true)] string? host)
    {
        return host is not null && host.EndsWith(".dev.localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the specified URI uses a host that is "localhost".
    /// </summary>
    /// <param name="uri">The URI to check.</param>
    /// <returns>
    /// <c>true</c> if the host ends with ".localhost" (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocalhostTld([NotNullWhen(true)] Uri? uri)
    {
        return uri?.Host is not null && IsLocalhostTld(uri.Host);
    }

    /// <summary>
    /// Determines whether the specified URI uses a host that ends with ".dev.localhost".
    /// </summary>
    /// <param name="uri">The URI to check.</param>
    /// <returns>
    /// <c>true</c> if the host ends with ".dev.localhost" (case-insensitive); otherwise, <c>false</c>.
    /// </returns>
    public static bool IsDevLocalhostTld([NotNullWhen(true)] Uri? uri)
    {
        return uri?.Host is not null && IsDevLocalhostTld(uri.Host);
    }

    /// <summary>
    /// Determines whether the specified host is "localhost" or uses the ".localhost" top-level domain.
    /// </summary>
    /// <param name="host">The host to check.</param>
    /// <returns>
    /// <c>true</c> if the host is "localhost" (case-insensitive) or ends with ".localhost" (case-insensitive);
    /// otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocalhostOrLocalhostTld([NotNullWhen(true)] string? host)
    {
        return IsLocalhost(host) || IsLocalhostTld(host);
    }

    /// <summary>
    /// Determines whether the specified URI uses a host that is "localhost" or ends with ".localhost".
    /// </summary>
    /// <param name="uri"></param>
    /// <returns>
    /// <c>true</c> if the host is "localhost" (case-insensitive) or ends with ".localhost" (case-insensitive);
    /// otherwise, <c>false</c>.
    /// </returns>
    public static bool IsLocalhostOrLocalhostTld([NotNullWhen(true)] Uri? uri)
    {
        return uri?.Host is not null && IsLocalhostOrLocalhostTld(uri.Host);
    }

    /// <summary>
    /// Gets the URL of the endpoint, adjusting for localhost TLD if configured.
    /// </summary>
    /// <remarks>
    /// When the endpoint's <see cref="EndpointAnnotation.TargetHost"/> is a localhost TLD
    /// (e.g., aspire-dashboard.dev.localhost), the allocated endpoint address will be "localhost"
    /// since that's what the service actually binds to. This method returns the URL with the
    /// configured TLD hostname instead, which is what users expect to see and use in browsers.
    /// </remarks>
    /// <param name="endpoint">The endpoint reference.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The URL with the appropriate hostname.</returns>
    internal static async ValueTask<string?> GetUrlWithTargetHostAsync(EndpointReference endpoint, CancellationToken cancellationToken = default)
    {
        var allocatedUrl = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(allocatedUrl))
        {
            return allocatedUrl;
        }

        // If the configured TargetHost is a localhost TLD (e.g., aspire-dashboard.dev.localhost),
        // we need to use that instead of the allocated address (localhost) since the TLD hostname
        // is what the user expects to see and use in the browser.
        var targetHost = endpoint.EndpointAnnotation.TargetHost;
        if (IsLocalhostTld(targetHost) && Uri.TryCreate(allocatedUrl, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{targetHost}:{uri.Port}";
        }

        return allocatedUrl;
    }
}
