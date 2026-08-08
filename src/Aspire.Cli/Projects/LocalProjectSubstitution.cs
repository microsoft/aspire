// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// A package an AppHost server builds from local source instead of restoring from a feed.
/// </summary>
/// <remarks>
/// This only happens in repository development mode. It matters to callers that publish artifacts
/// keyed on a package version, because the substituted project carries whatever the checkout
/// currently contains rather than the version that was requested.
/// </remarks>
/// <param name="ProjectPath">The project built in place of the package.</param>
/// <param name="CheckoutVersionPrefix">
/// The <c>Major.Minor.Patch</c> the checkout produces, read from the checkout itself, or
/// <see langword="null"/> when it cannot be established. This is deliberately not the running CLI's
/// reported version: that value is overrideable (<c>ASPIRE_CLI_VERSION</c>, the install sidecar), so
/// checking a requested version against it alone lets a caller name local source whatever they like.
/// It is a prefix rather than a full version because the prerelease suffix is assigned at build time
/// by Arcade and is not recorded in the checkout.
/// </param>
internal sealed record LocalProjectSubstitution(string ProjectPath, string? CheckoutVersionPrefix);
