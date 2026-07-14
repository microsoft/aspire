// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius;
using Aspire.Hosting.Radius.Secrets;

namespace Aspire.Hosting;

/// <summary>
/// Builder entry points for declaring and populating Radius secret stores
/// (<c>Applications.Core/secretStores</c>). All surface is experimental and gated by
/// <c>ASPIRERADIUS006</c>.
/// </summary>
public static class RadiusSecretStoreExtensions
{
    /// <summary>
    /// Declares an <b>application-scoped</b> Radius secret store
    /// (<c>properties.application</c>). Choose the population mode with exactly one of
    /// <c>WithData</c> / <c>WithExistingSecret</c> / <c>WithSealedSecret</c>.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The secret-store name (a valid resource-name segment).</param>
    /// <param name="type">The Radius secret-store type.</param>
    /// <returns>A builder for the new <see cref="RadiusSecretStoreResource"/>.</returns>
    /// <exception cref="ArgumentException">The name is empty/whitespace or not a valid resource-name segment.</exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusSecretStoreResource> AddRadiusSecretStore(
        this IDistributedApplicationBuilder builder,
        string name,
        RadiusSecretStoreType type)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateStoreName(name);

        var resource = new RadiusSecretStoreResource(name, type, RadiusSecretStoreScope.Application);

        // Publish/deploy-only, mirroring AddRadiusEnvironment: in Run mode return an
        // unregistered builder so the store does not surface in the dashboard and no
        // artifact is emitted (FR-016).
        return builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);
    }

    /// <summary>
    /// Declares an <b>environment-scoped</b> Radius secret store
    /// (<c>properties.environment</c>) on <paramref name="radius"/> and configures it.
    /// </summary>
    /// <param name="radius">The Radius environment resource builder.</param>
    /// <param name="name">The secret-store name (a valid resource-name segment).</param>
    /// <param name="type">The Radius secret-store type.</param>
    /// <param name="configure">Callback selecting the population mode (and optional timeout).</param>
    /// <returns>The same environment builder for chaining.</returns>
    /// <exception cref="ArgumentException">The name is empty/whitespace or not a valid resource-name segment.</exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusEnvironmentResource> WithSecretStore(
        this IResourceBuilder<RadiusEnvironmentResource> radius,
        string name,
        RadiusSecretStoreType type,
        Action<IResourceBuilder<RadiusSecretStoreResource>> configure)
    {
        ArgumentNullException.ThrowIfNull(radius);
        ArgumentNullException.ThrowIfNull(configure);
        ValidateStoreName(name);

        var resource = new RadiusSecretStoreResource(name, type, RadiusSecretStoreScope.Environment)
        {
            OwningEnvironment = radius.Resource,
        };

        var storeBuilder = radius.ApplicationBuilder.ExecutionContext.IsRunMode
            ? radius.ApplicationBuilder.CreateResourceBuilder(resource)
            : radius.ApplicationBuilder.AddResource(resource);

        configure(storeBuilder);
        return radius;
    }

    /// <summary>
    /// Populates the store inline (Radius-created) from Aspire secret parameters. Each
    /// key's value is emitted as a valueless <c>@secure()</c> Bicep <c>param</c> and
    /// supplied at deploy time via <c>rad deploy --parameters</c>.
    /// </summary>
    /// <param name="store">The secret-store builder.</param>
    /// <param name="configure">Callback binding data keys to secret parameters.</param>
    /// <returns>The same store builder for chaining.</returns>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusSecretStoreResource> WithData(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        Action<RadiusSecretStoreDataBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configure);

        var population = store.Resource.Population;
        population.HasInlineData = true;
        configure(new RadiusSecretStoreDataBuilder(population));
        return store;
    }

    /// <summary>
    /// References an existing cluster <c>Secret</c> by <c>&lt;namespace&gt;/&lt;name&gt;</c>
    /// (or a bare <c>&lt;name&gt;</c>, whose namespace defaults to the owning environment's).
    /// No secret value flows through Aspire.
    /// </summary>
    /// <param name="store">The secret-store builder.</param>
    /// <param name="namespaceAndName">The <c>&lt;namespace&gt;/&lt;name&gt;</c> or bare <c>&lt;name&gt;</c> reference.</param>
    /// <param name="keys">The keys to expose from the referenced <c>Secret</c>.</param>
    /// <returns>The same store builder for chaining.</returns>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusSecretStoreResource> WithExistingSecret(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        string namespaceAndName,
        params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceAndName);

        var population = store.Resource.Population;
        population.HasExistingSecret = true;
        population.ResourceReference = namespaceAndName;
        AddKeys(population, keys);
        return store;
    }

    /// <summary>
    /// References the decrypted <c>Secret</c> of a committed, encrypted Bitnami
    /// <c>SealedSecret</c> manifest. At publish the manifest is copied alongside the
    /// owning group's <c>app.bicep</c>; at deploy it is applied and awaited before
    /// <c>rad deploy</c>. The integration never seals plaintext.
    /// </summary>
    /// <param name="store">The secret-store builder.</param>
    /// <param name="manifestPath">Path to the committed encrypted <c>SealedSecret</c> manifest.</param>
    /// <param name="keys">The keys to expose from the decrypted <c>Secret</c>.</param>
    /// <returns>The same store builder for chaining.</returns>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusSecretStoreResource> WithSealedSecret(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        string manifestPath,
        params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var population = store.Resource.Population;
        population.HasSealedSecret = true;
        population.SealedManifestPath = manifestPath;
        AddKeys(population, keys);
        return store;
    }

    /// <summary>
    /// Overrides the per-store timeout for awaiting a sealed/referenced <c>Secret</c> to
    /// materialize in-cluster before <c>rad deploy</c> (default 120 seconds).
    /// </summary>
    /// <param name="store">The secret-store builder.</param>
    /// <param name="timeout">A positive materialization timeout.</param>
    /// <returns>The same store builder for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore]
    public static IResourceBuilder<RadiusSecretStoreResource> WithMaterializationTimeout(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The materialization timeout must be positive.");
        }

        store.Resource.MaterializationTimeout = timeout;
        return store;
    }

    private static void AddKeys(RadiusSecretStorePopulation population, string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(keys));
            population.Keys.Add(key);
        }
    }

    // The store name is used verbatim as a Bicep symbol/resource name, a UCP-ID segment,
    // and a Radius-created Secret name, so it must be a valid single resource-name segment.
    private static void ValidateStoreName([NotNull] string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!RadiusSecretStoreNaming.IsValidName(name))
        {
            throw new ArgumentException(
                $"Secret-store name '{name}' is invalid. It must be 1-90 characters of ASCII letters, digits, " +
                "'-', '_', or '.', may not start or end with '.', may not contain '..', and may not be a reserved " +
                "device name. Diagnostic: ASPIRERADIUS049.",
                nameof(name));
        }
    }
}

/// <summary>
/// Builds the inline <c>data</c> map for a secret store declared with <c>WithData(...)</c>.
/// </summary>
[Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusSecretStoreDataBuilder
{
    private readonly RadiusSecretStorePopulation _population;

    internal RadiusSecretStoreDataBuilder(RadiusSecretStorePopulation population) => _population = population;

    /// <summary>
    /// Binds a data key to a secret parameter. The parameter MUST be secret
    /// (<c>secret: true</c>); a non-secret parameter is rejected at the validation gate
    /// (<c>ASPIRERADIUS042</c>).
    /// </summary>
    /// <param name="key">The data key (non-empty).</param>
    /// <param name="parameter">The secret parameter supplying the value.</param>
    /// <param name="encoding">Optional per-key encoding (defaults to the store type's default).</param>
    /// <returns>The same data builder for chaining.</returns>
    /// <exception cref="ArgumentException">The key is empty/whitespace.</exception>
    public RadiusSecretStoreDataBuilder Add(
        string key,
        IResourceBuilder<ParameterResource> parameter,
        RadiusSecretStoreEncoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(parameter);

        _population.Data[key] = new RadiusSecretKeyBinding(parameter.Resource, encoding?.ToRadiusEncodingString());
        return this;
    }
}
