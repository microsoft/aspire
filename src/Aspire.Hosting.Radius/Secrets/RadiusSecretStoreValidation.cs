// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Secret-store types are experimental; consumed internally by the integration.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Config-time (pre-publish/deploy) validation of Radius secret stores. Runs as a
/// fail-fast gate that is <c>RequiredBy</c> both publish and deploy, so type/mode/key/
/// encoding/duplicate-name failures surface before any Bicep is emitted or
/// <c>kubectl</c>/<c>rad</c> is contacted. Store-name grammar and empty/whitespace keys
/// are rejected earlier, at the builder call site (FR-005a). No-op when the model
/// declares no secret stores (the feature is inactive; the default path is unchanged).
/// </summary>
internal static class RadiusSecretStoreValidation
{
    /// <summary>Pipeline-step entry point. No-ops in Run mode.</summary>
    internal static Task ValidateAsync(PipelineStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ExecutionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        Validate(context.Model);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates every declared secret store over the whole application model.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A required key is missing (<c>ASPIRERADIUS040</c>), the population mode count is not
    /// exactly one (<c>ASPIRERADIUS041</c>), an inline key binds a non-secret parameter
    /// (<c>ASPIRERADIUS042</c>), a duplicate key is declared (<c>ASPIRERADIUS043</c>), an
    /// invalid encoding is set for the type (<c>ASPIRERADIUS047</c>), two stores share a
    /// Bicep identifier within the same emitted scope (<c>ASPIRERADIUS048</c>), an
    /// application-scoped existing store uses a namespace-less reference (<c>ASPIRERADIUS055</c>),
    /// or a non-sealed store sets <c>WithMaterializationTimeout</c> (<c>ASPIRERADIUS062</c>).
    /// </exception>
    internal static void Validate(DistributedApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var stores = model.Resources.OfType<RadiusSecretStoreResource>().ToList();
        if (stores.Count == 0)
        {
            return;
        }

        foreach (var store in stores)
        {
            ValidateStore(store);
        }

        ValidateNoDuplicateNames(stores);
        ValidateConsumers(model);
    }

    private static void ValidateStore(RadiusSecretStoreResource store)
    {
        var population = store.Population;

        // ASPIRERADIUS041 — exactly one population mode.
        if (population.DeclaredModeCount != 1)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Name}' must declare exactly one population mode " +
                "(WithData, WithExistingSecret, or WithSealedSecret); it declares " +
                $"{population.DeclaredModeCount}. Diagnostic: ASPIRERADIUS041.");
        }

        var declaredKeys = population.HasInlineData
            ? population.Data.Keys.ToList()
            : population.Keys;

        // ASPIRERADIUS043 — duplicate keys. Inline keys are rejected as they are added (the data
        // dictionary rejects a duplicate via RadiusSecretStoreDataBuilder.Add), so only the
        // existing/sealed key list needs a duplicate scan here.
        if (population.IsSecretReference)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in population.Keys)
            {
                if (!seen.Add(key))
                {
                    throw new InvalidOperationException(
                        $"Secret store '{store.Name}' declares the key '{key}' more than once. " +
                        "Diagnostic: ASPIRERADIUS043.");
                }
            }
        }

        // ASPIRERADIUS040 — type-aware required keys.
        foreach (var required in store.Type.RequiredKeys())
        {
            if (!declaredKeys.Contains(required, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Secret store '{store.Name}' of type '{store.Type.ToRadiusTypeString()}' is missing " +
                    $"the required key '{required}'. Diagnostic: ASPIRERADIUS040.");
            }
        }

        // ASPIRERADIUS042 / ASPIRERADIUS047 — inline bindings must be secret and use valid encoding.
        if (population.HasInlineData)
        {
            foreach (var (key, binding) in population.Data)
            {
                if (!binding.Parameter.Secret)
                {
                    throw new InvalidOperationException(
                        $"Secret store '{store.Name}' binds key '{key}' to the non-secret parameter " +
                        $"'{binding.Parameter.Name}'. Bind a parameter created with secret: true. " +
                        "Diagnostic: ASPIRERADIUS042.");
                }

                if (binding.Encoding is not null && !store.Type.IsValidEncoding(binding.Encoding))
                {
                    throw new InvalidOperationException(
                        $"Secret store '{store.Name}' sets encoding '{binding.Encoding}' on key '{key}', which is " +
                        $"invalid for a '{store.Type.ToRadiusTypeString()}' store. Diagnostic: ASPIRERADIUS047.");
                }
            }
        }

        // ASPIRERADIUS062 — WithMaterializationTimeout only affects the sealed-secret deploy path,
        // which awaits the SealedSecret controller. On any other population mode it would silently
        // no-op, so reject an explicit override rather than mislead the author.
        if (store.MaterializationTimeoutWasSet && !population.HasSealedSecret)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Name}' sets WithMaterializationTimeout but is not populated with " +
                "WithSealedSecret. The materialization timeout only applies to sealed secrets; remove the " +
                "call or use WithSealedSecret. Diagnostic: ASPIRERADIUS062.");
        }

        // ASPIRERADIUS055 — an application-scoped existing-secret store has no single owning environment,
        // so a bare '<name>' reference has no deterministic namespace to default to (it would otherwise
        // fall back to whichever environment happens to build the store). Require a fully-qualified
        // '<namespace>/<name>' reference. Sealed stores are checked after their manifest metadata is read
        // because only then can we tell whether metadata.namespace was explicit or defaulted.
        if (store.Scope == RadiusSecretStoreScope.Application &&
            population.HasExistingSecret &&
            population.ResourceReference is { } reference &&
            !reference.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Application-scoped secret store '{store.Name}' references the existing Secret '{reference}' " +
                "without a namespace. Application-scoped stores have no owning environment to default the " +
                "namespace from; use a fully-qualified '<namespace>/<name>' reference. " +
                "Diagnostic: ASPIRERADIUS055.");
        }
    }

    /// <summary>
    /// Validates every recorded secret-store consumer wiring across the model.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A consumer kind is incompatible with the store type (<c>ASPIRERADIUS051</c>), an
    /// <c>envSecrets</c> consumer references a key the store does not declare
    /// (<c>ASPIRERADIUS052</c>), or a key-specific <c>envSecrets</c> consumer references a store
    /// that declares no keys (<c>ASPIRERADIUS064</c>).
    /// </exception>
    private static void ValidateConsumers(DistributedApplicationModel model)
    {
        foreach (var resource in model.Resources)
        {
            var annotation = resource.Annotations.OfType<Annotations.RadiusSecretStoresAnnotation>().FirstOrDefault();
            if (annotation is null)
            {
                continue;
            }

            foreach (var consumer in annotation.Consumers)
            {
                ValidateConsumer(consumer);
            }
        }
    }

    private static void ValidateConsumer(RadiusSecretStoreConsumer consumer)
    {
        var store = consumer.Store;

        // ASPIRERADIUS051 — a Bicep private-registry auth consumer references a basicAuthentication
        // (username/password) store, matching the OCI registry credential shape. envSecrets can source
        // from any type, so it is unconstrained here (its per-key check is below).
        if (consumer.Kind == RadiusSecretStoreConsumerKind.BicepRegistryAuth &&
            store.Type != RadiusSecretStoreType.BasicAuthentication)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Name}' of type '{store.Type.ToRadiusTypeString()}' is referenced as a " +
                $"{DescribeKind(consumer.Kind)} consumer, which requires a '{RadiusSecretStoreType.BasicAuthentication.ToRadiusTypeString()}' store. " +
                "Diagnostic: ASPIRERADIUS051.");
        }

        // ASPIRERADIUS051 — a Terraform Git PAT consumer references a store that must expose a 'pat' key
        // (optionally with 'username'); this is the shape Radius reads for
        // recipeConfig.terraform.authentication.git.pat, and it is typically a 'generic' store — NOT a
        // basicAuthentication (username/password) store, whose 'password' key Radius never consumes here.
        // See https://docs.radapp.io/guides/recipes/terraform/howto-private-registry/. Only enforce when
        // the store declares its keys inline/explicitly; an existing/sealed store that materializes keys
        // out-of-band is left unchecked (consistent with the envSecrets keyless handling below).
        if (consumer.Kind == RadiusSecretStoreConsumerKind.TerraformGitPat)
        {
            var declaredKeys = store.Population.HasInlineData
                ? store.Population.Data.Keys.ToList()
                : store.Population.Keys;

            if (declaredKeys.Count > 0 && !declaredKeys.Contains("pat", StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Secret store '{store.Name}' is referenced as a {DescribeKind(consumer.Kind)} consumer but does not " +
                    "declare the required 'pat' key (optionally with 'username'). Use a store that exposes a 'pat' key " +
                    $"(typically a '{RadiusSecretStoreType.Generic.ToRadiusTypeString()}' store). Declared keys: " +
                    $"{string.Join(", ", declaredKeys)}. Diagnostic: ASPIRERADIUS051.");
            }
        }

        // ASPIRERADIUS052 / ASPIRERADIUS064 — a key-specific envSecrets consumer must reference a key the
        // store exposes. Emission only exposes explicitly declared keys, so:
        //   * a keyless store (no inline data and no existing/sealed key list) cannot satisfy a
        //     key-specific reference — the emitted envSecrets entry would dangle (ASPIRERADIUS064);
        //   * a store with a non-empty declared set must contain the referenced key (ASPIRERADIUS052).
        // A store with no declared keys that is referenced WITHOUT a specific key is left alone: such a
        // sealed/existing store materializes its keys out-of-band and is intentionally unchecked.
        if (consumer.Kind == RadiusSecretStoreConsumerKind.EnvSecret && consumer.Key is { } key)
        {
            var declaredKeys = store.Population.HasInlineData
                ? store.Population.Data.Keys.ToList()
                : store.Population.Keys;

            if (declaredKeys.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Secret store '{store.Name}' declares no keys, but the recipe environment secret " +
                    $"'{consumer.Selector}' references the key '{key}'. A key-specific envSecrets consumer requires " +
                    "the store to declare that key (via WithData, or WithExistingSecret/WithSealedSecret with keys). " +
                    "Diagnostic: ASPIRERADIUS064.");
            }

            if (!declaredKeys.Contains(key, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Secret store '{store.Name}' does not declare the key '{key}' referenced by the recipe " +
                    $"environment secret '{consumer.Selector}'. Declared keys: {string.Join(", ", declaredKeys)}. " +
                    "Diagnostic: ASPIRERADIUS052.");
            }
        }
    }

    private static string DescribeKind(RadiusSecretStoreConsumerKind kind) => kind switch
    {
        RadiusSecretStoreConsumerKind.BicepRegistryAuth => "Bicep registry authentication",
        RadiusSecretStoreConsumerKind.TerraformGitPat => "Terraform Git PAT authentication",
        RadiusSecretStoreConsumerKind.EnvSecret => "recipe environment secret",
        _ => kind.ToString(),
    };

    /// <summary>Validates that an application-scoped sealed store has deterministic manifest namespace metadata.</summary>
    /// <exception cref="InvalidOperationException">
    /// The store is application-scoped and its sealed manifest omitted <c>metadata.namespace</c> (<c>ASPIRERADIUS055</c>).
    /// </exception>
    internal static void ValidateSealedSecretNamespace(
        RadiusSecretStoreResource store,
        SealedSecretManifest.Metadata metadata,
        string manifestPath)
    {
        if (store.Scope == RadiusSecretStoreScope.Application &&
            store.Population.HasSealedSecret &&
            !metadata.NamespaceWasExplicit)
        {
            throw new InvalidOperationException(
                $"Application-scoped secret store '{store.Name}' references the SealedSecret manifest at " +
                $"'{manifestPath}' without metadata.namespace. Application-scoped stores have no owning " +
                "environment to default the namespace from; set metadata.namespace in the manifest. " +
                "Diagnostic: ASPIRERADIUS055.");
        }
    }

    private static void ValidateNoDuplicateNames(IReadOnlyList<RadiusSecretStoreResource> stores)
    {
        // Aspire already enforces unique resource names, so exact-name collisions cannot occur.
        // Two distinct store names can still sanitize to the same Bicep identifier (e.g. 'db-creds'
        // and 'db.creds' both become 'db_creds'), which would emit duplicate resource symbols.
        // Application-scoped stores are emitted into every environment's app.bicep, so they must
        // also be checked against every environment-scoped identifier, not just other app stores.
        var appScoped = new Dictionary<string, RadiusSecretStoreResource>(StringComparer.Ordinal);
        var envScoped = new Dictionary<(string? EnvironmentName, string Identifier), RadiusSecretStoreResource>();

        foreach (var store in stores)
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(store.Name);

            if (store.Scope == RadiusSecretStoreScope.Application)
            {
                if (appScoped.TryGetValue(identifier, out var appCollision))
                {
                    ThrowDuplicateName(appCollision, store, identifier);
                }

                var envCollision = envScoped.FirstOrDefault(kvp => string.Equals(kvp.Key.Identifier, identifier, StringComparison.Ordinal));
                if (envCollision.Value is not null)
                {
                    ThrowDuplicateName(envCollision.Value, store, identifier);
                }

                appScoped[identifier] = store;
                continue;
            }

            var envKey = (store.OwningEnvironment?.Name, identifier);
            if (envScoped.TryGetValue(envKey, out var sameEnvironmentCollision))
            {
                ThrowDuplicateName(sameEnvironmentCollision, store, identifier);
            }

            if (appScoped.TryGetValue(identifier, out var applicationCollision))
            {
                ThrowDuplicateName(applicationCollision, store, identifier);
            }

            envScoped[envKey] = store;
        }
    }

    private static void ThrowDuplicateName(
        RadiusSecretStoreResource existing,
        RadiusSecretStoreResource store,
        string identifier)
    {
        throw new InvalidOperationException(
            $"Secret stores '{existing.Name}' and '{store.Name}' map to the same Bicep identifier " +
            $"'{identifier}' within the same emitted scope. Rename one so they produce distinct identifiers. " +
            "Diagnostic: ASPIRERADIUS048.");
    }
}
