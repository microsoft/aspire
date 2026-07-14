// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Secret-store types are experimental; the private helpers below consume them.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.Secrets;

namespace Aspire.Hosting;

/// <summary>
/// Fluent entry points for consuming a declared Radius secret store from its documented Radius
/// consumers (environment <c>recipeConfig</c> authentication / <c>envSecrets</c>, and gateway
/// TLS). Each reference is emitted by the store's fully-qualified UCP secret-store ID
/// (<c>/planes/radius/local/resourceGroups/&lt;group&gt;/providers/Applications.Core/secretStores/&lt;name&gt;</c>).
/// All surface is experimental and gated by <c>ASPIRERADIUS006</c>.
/// </summary>
public static class RadiusSecretStoreConsumerExtensions
{
    /// <summary>Uses a <c>basicAuthentication</c> store as private Bicep-registry auth in <c>recipeConfig</c>.</summary>
    /// <param name="radius">The Radius environment builder.</param>
    /// <param name="registryHost">The container-registry host the credentials authenticate against (e.g. <c>myregistry.azurecr.io</c>).</param>
    /// <param name="store">The <c>basicAuthentication</c> secret store supplying the registry credentials.</param>
    /// <returns>The same environment builder for chaining.</returns>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Experimental Radius secret-store consumer surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithBicepRegistryAuthentication(
        this IResourceBuilder<RadiusEnvironmentResource> radius,
        string registryHost,
        IResourceBuilder<RadiusSecretStoreResource> store)
        => AddConsumer(radius, RadiusSecretStoreConsumerKind.BicepRegistryAuth, registryHost, store, key: null);

    /// <summary>Uses a <c>basicAuthentication</c> store as a Terraform Git PAT in <c>recipeConfig</c>.</summary>
    /// <param name="radius">The Radius environment builder.</param>
    /// <param name="gitHost">The Git host the PAT authenticates against (e.g. <c>github.com</c>).</param>
    /// <param name="store">The <c>basicAuthentication</c> secret store supplying the Git PAT.</param>
    /// <returns>The same environment builder for chaining.</returns>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Experimental Radius secret-store consumer surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithTerraformGitAuthentication(
        this IResourceBuilder<RadiusEnvironmentResource> radius,
        string gitHost,
        IResourceBuilder<RadiusSecretStoreResource> store)
        => AddConsumer(radius, RadiusSecretStoreConsumerKind.TerraformGitPat, gitHost, store, key: null);

    /// <summary>Adds a <c>recipeConfig.envSecrets['&lt;VAR&gt;'] = { source: &lt;storeId&gt;, key: &lt;k&gt; }</c> entry.</summary>
    /// <param name="radius">The Radius environment builder.</param>
    /// <param name="variableName">The recipe environment-variable name the secret is exposed as.</param>
    /// <param name="store">The secret store supplying the value.</param>
    /// <param name="key">The key within <paramref name="store"/> to read. Must be a key the store declares.</param>
    /// <returns>The same environment builder for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty or whitespace.</exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Experimental Radius secret-store consumer surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithRecipeEnvironmentSecret(
        this IResourceBuilder<RadiusEnvironmentResource> radius,
        string variableName,
        IResourceBuilder<RadiusSecretStoreResource> store,
        string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return AddConsumer(radius, RadiusSecretStoreConsumerKind.EnvSecret, variableName, store, key);
    }

    /// <summary>
    /// Records a <c>certificate</c> store as gateway TLS (<c>tls.certificateFrom</c>).
    /// <para>
    /// This consumer is <b>not yet supported</b>: the integration does not model Radius gateways yet,
    /// so no <c>tls.certificateFrom</c> is emitted. The reference is recorded and rejected at the
    /// publish/deploy validation gate with <c>ASPIRERADIUS060</c> so callers get an explicit failure
    /// rather than a silent no-op. The receiver is left open (<typeparamref name="T"/>) because there
    /// is no gateway resource type to constrain to yet; the wiring is recorded on the store's owning
    /// environment so it can be emitted once gateways are modeled. See the README "Known limitations".
    /// </para>
    /// </summary>
    /// <typeparam name="T">The gateway resource type. Unconstrained because gateways are not modeled yet.</typeparam>
    /// <param name="gateway">The resource acting as the gateway.</param>
    /// <param name="store">The <c>certificate</c> secret store supplying the TLS certificate.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="store"/> is application-scoped, so it has no owning environment to record the
    /// reference against (<c>ASPIRERADIUS054</c>). Declare the certificate store on an environment
    /// (<c>WithSecretStore</c>) to use it for gateway TLS.
    /// </exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Experimental Radius secret-store consumer surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<T> WithTlsCertificate<T>(
        this IResourceBuilder<T> gateway,
        IResourceBuilder<RadiusSecretStoreResource> store)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);

        // Gateway TLS references are recorded on the store's owning environment (that is where
        // recipeConfig-adjacent wiring lives). An application-scoped store has no single owning
        // environment, so there is nowhere deterministic to record the reference — fail loudly
        // instead of silently dropping it (which previously happened).
        if (store.Resource.OwningEnvironment is not { } environment)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Resource.Name}' is application-scoped and cannot be used for gateway TLS, " +
                "which requires an environment-scoped certificate store. Declare it with WithSecretStore on an " +
                "environment. Diagnostic: ASPIRERADIUS054.");
        }

        RadiusSecretStoresAnnotation.GetOrAdd(environment).Consumers.Add(
            new RadiusSecretStoreConsumer(
                RadiusSecretStoreConsumerKind.GatewayTls, store.Resource, gateway.Resource.Name, Key: null));

        return gateway;
    }

    private static IResourceBuilder<RadiusEnvironmentResource> AddConsumer(
        IResourceBuilder<RadiusEnvironmentResource> radius,
        RadiusSecretStoreConsumerKind kind,
        string selector,
        IResourceBuilder<RadiusSecretStoreResource> store,
        string? key)
    {
        ArgumentNullException.ThrowIfNull(radius);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(store);

        RadiusSecretStoresAnnotation.GetOrAdd(radius.Resource).Consumers.Add(
            new RadiusSecretStoreConsumer(kind, store.Resource, selector, key));

        return radius;
    }
}
