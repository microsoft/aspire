// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Npm;

/// <summary>
/// Resolves the registry that npm would use to install a given package, following
/// npm's own configuration precedence.
/// </summary>
internal interface INpmRegistryResolver
{
    /// <summary>
    /// Gets the registry base address <c>npm install &lt;packageName&gt;</c> would resolve against.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@microsoft/aspire-cli").</param>
    NpmRegistryResolution Resolve(string packageName);
}

/// <summary>
/// The registry selected for a package, along with the configuration layer it came from.
/// </summary>
/// <param name="RegistryUri">
/// The registry base address. Always carries a trailing slash so it can be used directly as a
/// <see cref="Uri"/> base.
/// </param>
/// <param name="Source">
/// A human-readable description of the layer the value came from, for diagnostics
/// (e.g., "the npm_config_registry environment variable" or a <c>.npmrc</c> path).
/// </param>
internal sealed record NpmRegistryResolution(Uri RegistryUri, string Source)
{
    /// <summary>
    /// Gets the registry address with any embedded credentials removed, for logs and error
    /// messages.
    /// </summary>
    /// <remarks>
    /// npm accepts <c>https://user:token@host/</c> in a <c>.npmrc</c> <c>registry</c> value, so the
    /// resolved address is not automatically safe to print. Every message that names the registry
    /// must use this instead of <see cref="RegistryUri"/>.
    /// </remarks>
    public string DisplayUri { get; } = Redact(RegistryUri);

    private static string Redact(Uri registryUri)
    {
        if (string.IsNullOrEmpty(registryUri.UserInfo))
        {
            return registryUri.AbsoluteUri;
        }

        return new UriBuilder(registryUri)
        {
            UserName = string.Empty,
            Password = string.Empty
        }.Uri.AbsoluteUri;
    }
}
