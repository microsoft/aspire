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
    [AspireExportIgnore(Reason = "Experimental Radius secret-store surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<RadiusSecretStoreResource> AddRadiusSecretStore(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
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
    [AspireExportIgnore(Reason = "Experimental Radius secret-store surface; the population callback is not ATS-compatible.")]
    public static IResourceBuilder<RadiusEnvironmentResource> WithSecretStore(
        this IResourceBuilder<RadiusEnvironmentResource> radius,
        [ResourceName] string name,
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
    [AspireExportIgnore(Reason = "Experimental Radius secret-store surface; the data-binding callback is not ATS-compatible.")]
    public static IResourceBuilder<RadiusSecretStoreResource> WithData(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        Action<RadiusSecretStoreDataBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configure);

        EnsureNotAlreadyPopulated(store.Resource);

        // Build into a scratch population so a throwing callback leaves the store unpopulated:
        // assignment is transactional, so a corrected retry is not wrongly blocked by the
        // ASPIRERADIUS065 "already populated" guard.
        var scratch = new RadiusSecretStorePopulation();
        configure(new RadiusSecretStoreDataBuilder(scratch));

        var population = store.Resource.Population;
        population.HasInlineData = true;
        foreach (var (key, binding) in scratch.Data)
        {
            population.Data[key] = binding;
        }

        return store;
    }

    /// <summary>
    /// Populates the store inline (Radius-created) by binding a single data <paramref name="key"/>
    /// to a secret parameter. This is a convenience over the callback overload for the common
    /// single-key case; the value is emitted as a valueless <c>@secure()</c> Bicep <c>param</c> and
    /// supplied at deploy time via <c>rad deploy --parameters</c>. Like the other population methods
    /// this may be called at most once per store (a second population call throws
    /// <c>ASPIRERADIUS065</c>); use the callback overload to bind multiple keys.
    /// </summary>
    /// <param name="store">The secret-store builder.</param>
    /// <param name="key">The data key (non-empty).</param>
    /// <param name="parameter">The secret parameter supplying the value (must be <c>secret: true</c>).</param>
    /// <param name="encoding">Optional per-key encoding (defaults to the store type's default).</param>
    /// <returns>The same store builder for chaining.</returns>
    /// <exception cref="ArgumentException">The key is empty/whitespace.</exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Experimental Radius secret-store surface; the data binding is not ATS-compatible.")]
    public static IResourceBuilder<RadiusSecretStoreResource> WithData(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        string key,
        IResourceBuilder<ParameterResource> parameter,
        RadiusSecretStoreEncoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parameter);

        return store.WithData(s => s.Add(key, parameter, encoding));
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
    [AspireExportIgnore(Reason = "Experimental Radius secret-store surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<RadiusSecretStoreResource> WithExistingSecret(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        string namespaceAndName,
        params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceAndName);

        var validatedKeys = ValidateKeys(keys);
        EnsureNotAlreadyPopulated(store.Resource);

        var population = store.Resource.Population;
        population.HasExistingSecret = true;
        population.ResourceReference = ValidateSecretReference(namespaceAndName);
        population.Keys.AddRange(validatedKeys);
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
    [AspireExportIgnore(Reason = "Experimental Radius secret-store surface; there is no polyglot ATS equivalent yet.")]
    public static IResourceBuilder<RadiusSecretStoreResource> WithSealedSecret(
        this IResourceBuilder<RadiusSecretStoreResource> store,
        string manifestPath,
        params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var validatedKeys = ValidateKeys(keys);
        EnsureNotAlreadyPopulated(store.Resource);

        var population = store.Resource.Population;
        population.HasSealedSecret = true;
        // Resolve a relative manifest path against the AppHost directory (not the process working
        // directory), matching other hosting file APIs (e.g. KeycloakResourceBuilderExtensions), so
        // the documented `./secrets/...` usage works regardless of where the AppHost is launched.
        population.SealedManifestPath = Path.GetFullPath(manifestPath, store.ApplicationBuilder.AppHostDirectory);
        population.Keys.AddRange(validatedKeys);
        return store;
    }

    /// <summary>
    /// Overrides the per-store timeout for awaiting a <b>sealed</b> <c>Secret</c> to materialize
    /// in-cluster before <c>rad deploy</c> (default 120 seconds). The timeout bounds the whole
    /// apply&#8594;sync-poll&#8594;key-verify sequence as a single shared budget, so a stalled
    /// <c>kubectl apply</c> or verify call is cancelled with <c>ASPIRERADIUS066</c> once it is
    /// exhausted.
    /// <para>
    /// This only affects the sealed-secret deploy path (<c>WithSealedSecret</c>). Calling it on a
    /// store that is not populated with <c>WithSealedSecret</c> is rejected by the validation gate
    /// with <c>ASPIRERADIUS062</c> rather than silently ignored.
    /// </para>
    /// </summary>
    /// <param name="store">The secret-store builder.</param>
    /// <param name="timeout">A positive materialization timeout.</param>
    /// <returns>The same store builder for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Sealed-secret deploy-timing knob with no polyglot ATS equivalent.")]
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
        store.Resource.MaterializationTimeoutWasSet = true;
        return store;
    }

    // Validates every key without mutating the store's population, so a later invalid key cannot
    // leave the population partially assigned (which would then trip the ASPIRERADIUS065 guard on a
    // corrected retry). Returns the validated keys for the caller to commit atomically.
    private static List<string> ValidateKeys(string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var key in keys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key, nameof(keys));
        }

        return [.. keys];
    }

    // Validates that an existing-secret reference is either a bare Kubernetes object name or exactly
    // one '<namespace>/<name>' pair, and that each segment is a valid Kubernetes name. Radius's
    // Kubernetes secret-store parser rejects anything else at deploy time, so validating at the API
    // boundary keeps the failure fast and local. Accepted:  'db-creds', 'app/db-creds'. Rejected:
    // '/secret' (empty namespace), 'namespace/' (empty name), 'a/b/c' (more than one separator), and
    // names that are not DNS-1123-conformant (e.g. 'App_Creds', 'UPPER').
    private static string ValidateSecretReference(string namespaceAndName)
    {
        var separatorCount = namespaceAndName.Count(c => c == '/');
        if (separatorCount > 1)
        {
            throw new ArgumentException(
                $"Existing-secret reference '{namespaceAndName}' is invalid. Use a bare '<name>' or a single " +
                "'<namespace>/<name>' pair. Diagnostic: ASPIRERADIUS046.",
                nameof(namespaceAndName));
        }

        string? ns = null;
        string name;
        if (separatorCount == 1)
        {
            var slash = namespaceAndName.IndexOf('/', StringComparison.Ordinal);
            ns = namespaceAndName[..slash];
            name = namespaceAndName[(slash + 1)..];
        }
        else
        {
            name = namespaceAndName;
        }

        // The Secret name is a DNS-1123 subdomain; the namespace (when present) is a DNS-1123 label.
        // https://kubernetes.io/docs/concepts/overview/working-with-objects/names/
        if (!IsDns1123Subdomain(name) || (ns is not null && !IsDns1123Label(ns)))
        {
            throw new ArgumentException(
                $"Existing-secret reference '{namespaceAndName}' is invalid. The name must be a DNS-1123 subdomain and " +
                "the optional namespace a DNS-1123 label (lowercase alphanumeric, '-', with '.' allowed in the name). " +
                "Diagnostic: ASPIRERADIUS046.",
                nameof(namespaceAndName));
        }

        return namespaceAndName;
    }

    // DNS-1123 label: 1-63 chars, lowercase alphanumeric or '-', must start and end alphanumeric.
    private static bool IsDns1123Label(string value)
    {
        if (value.Length is 0 or > 63)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isEdge = i == 0 || i == value.Length - 1;
            if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') || (!isEdge && c == '-')))
            {
                return false;
            }
        }

        return true;
    }

    // DNS-1123 subdomain: 1-253 chars, a dot-separated series of DNS-1123 labels.
    private static bool IsDns1123Subdomain(string value)
    {
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in value.Split('.'))
        {
            if (!IsDns1123Label(label))
            {
                return false;
            }
        }

        return true;
    }

    // A secret store must declare exactly one population mode. Reject a second population call
    // (repeated same-mode or cross-mode) at the call site so misuse fails immediately with a clear
    // stack trace, rather than silently appending keys across modes/manifests or reaching the gate.
    [Experimental("ASPIRERADIUS006", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    private static void EnsureNotAlreadyPopulated(RadiusSecretStoreResource store)
    {
        if (store.Population.IsPopulated)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Name}' already declares a population mode; declare exactly one of " +
                "WithData, WithExistingSecret, or WithSealedSecret, once. Diagnostic: ASPIRERADIUS065.");
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
                $"Secret-store name '{name}' is invalid. It must be 1-{RadiusSecretStoreNaming.MaxNameLength} characters of ASCII " +
                "letters, digits, and '-', must start with a letter, may not contain consecutive hyphens, may not end with a " +
                "hyphen, and may not be a reserved device name. Diagnostic: ASPIRERADIUS049.",
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
    /// <exception cref="InvalidOperationException">The key was already declared (<c>ASPIRERADIUS043</c>).</exception>
    public RadiusSecretStoreDataBuilder Add(
        string key,
        IResourceBuilder<ParameterResource> parameter,
        RadiusSecretStoreEncoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(parameter);

        // Reject a duplicate inline key rather than silently overwriting the earlier binding via the
        // dictionary indexer: a silent overwrite would hide a duplicate declaration from the
        // ASPIRERADIUS043 gate and could bind the store to the wrong parameter. This mirrors the
        // duplicate-key rejection applied to the existing/sealed key list.
        if (!_population.Data.TryAdd(key, new RadiusSecretKeyBinding(parameter.Resource, encoding?.ToRadiusEncodingString())))
        {
            throw new InvalidOperationException(
                $"Secret-store data key '{key}' is declared more than once. Diagnostic: ASPIRERADIUS043.");
        }

        return this;
    }
}
