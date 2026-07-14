// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Secret-store types are experimental; consumed internally by the integration.

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>The documented Radius consumers of a secret store.</summary>
internal enum RadiusSecretStoreConsumerKind
{
    /// <summary><c>recipeConfig.bicep.authentication['&lt;registry&gt;'].secret</c>.</summary>
    BicepRegistryAuth,

    /// <summary><c>recipeConfig.terraform.authentication.git.pat['&lt;host&gt;'].secret</c>.</summary>
    TerraformGitPat,

    /// <summary><c>recipeConfig.envSecrets['&lt;VAR&gt;'] = { source: &lt;storeId&gt;, key: &lt;k&gt; }</c>.</summary>
    EnvSecret,

    /// <summary>Gateway <c>tls.certificateFrom</c>.</summary>
    GatewayTls,
}

/// <summary>
/// A wiring that references a <see cref="RadiusSecretStoreResource"/> from one of its
/// documented Radius consumers, emitted by the store's fully-qualified UCP ID.
/// </summary>
/// <param name="Kind">The consumer kind.</param>
/// <param name="Store">The referenced secret store.</param>
/// <param name="Selector">Registry/git host, provider, or <c>envSecrets</c> variable name — as applicable.</param>
/// <param name="Key">The secret key for an <see cref="RadiusSecretStoreConsumerKind.EnvSecret"/> consumer.</param>
internal sealed record RadiusSecretStoreConsumer(
    RadiusSecretStoreConsumerKind Kind,
    RadiusSecretStoreResource Store,
    string? Selector,
    string? Key);
