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
    /// name within the same scope (<c>ASPIRERADIUS048</c>), or a non-sealed store sets
    /// <c>WithMaterializationTimeout</c> (<c>ASPIRERADIUS062</c>).
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

        // ASPIRERADIUS043 — duplicate keys (only possible for the existing/sealed key list;
        // inline keys are a dictionary and cannot collide).
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
        // '<namespace>/<name>' reference. Sealed stores are exempt: their namespace comes from the
        // manifest metadata, not the reference.
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
    /// (<c>ASPIRERADIUS052</c>), or a gateway TLS certificate is referenced
    /// (<c>ASPIRERADIUS060</c>, not yet supported).
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

        // ASPIRERADIUS060 — gateway TLS (tls.certificateFrom) is not yet supported: the integration
        // does not model Radius gateways yet, so there is no gateway resource to attach a certificate
        // reference to. Reject at the gate so callers get an explicit failure rather than a silent
        // no-op at emission.
        if (consumer.Kind == RadiusSecretStoreConsumerKind.GatewayTls)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Name}' is referenced as a gateway TLS certificate for '{consumer.Selector}', " +
                "which is not yet supported (Radius gateways are not modeled yet). Remove the WithTlsCertificate call. " +
                "Diagnostic: ASPIRERADIUS060.");
        }

        // ASPIRERADIUS051 — the consumer kind must be compatible with the store type. Bicep-registry and
        // Terraform-git-PAT auth reference a basicAuthentication (username/password) store; envSecrets
        // can source from any type, so it is unconstrained.
        var requiredType = consumer.Kind switch
        {
            RadiusSecretStoreConsumerKind.BicepRegistryAuth => (RadiusSecretStoreType?)RadiusSecretStoreType.BasicAuthentication,
            RadiusSecretStoreConsumerKind.TerraformGitPat => RadiusSecretStoreType.BasicAuthentication,
            _ => null,
        };

        if (requiredType is { } expected && store.Type != expected)
        {
            throw new InvalidOperationException(
                $"Secret store '{store.Name}' of type '{store.Type.ToRadiusTypeString()}' is referenced as a " +
                $"{DescribeKind(consumer.Kind)} consumer, which requires a '{expected.ToRadiusTypeString()}' store. " +
                "Diagnostic: ASPIRERADIUS051.");
        }

        // ASPIRERADIUS052 — an envSecrets consumer must reference a key the store declares. Only enforce
        // when the store declares an explicit key set (inline data, or an existing/sealed key list); a
        // sealed/existing store with no declared keys materializes them out-of-band, so we cannot check.
        if (consumer.Kind == RadiusSecretStoreConsumerKind.EnvSecret && consumer.Key is { } key)
        {
            var declaredKeys = store.Population.HasInlineData
                ? store.Population.Data.Keys.ToList()
                : store.Population.Keys;

            if (declaredKeys.Count > 0 && !declaredKeys.Contains(key, StringComparer.Ordinal))
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
        RadiusSecretStoreConsumerKind.GatewayTls => "gateway TLS",
        RadiusSecretStoreConsumerKind.EnvSecret => "recipe environment secret",
        _ => kind.ToString(),
    };

    private static void ValidateNoDuplicateNames(IReadOnlyList<RadiusSecretStoreResource> stores)
    {
        // Aspire already enforces unique resource names, so exact-name collisions cannot occur.
        // Two distinct store names can still sanitize to the same Bicep identifier (e.g. 'db-creds'
        // and 'db.creds' both become 'db_creds'), which would emit duplicate resource symbols.
        // Detect that within a scope (mirrors ASPIRERADIUS028 for recipe parameters).
        var seen = new Dictionary<string, RadiusSecretStoreResource>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(store.Name);
            var scopeKey = store.Scope == RadiusSecretStoreScope.Environment
                ? $"env:{store.OwningEnvironment?.Name}:{identifier}"
                : $"app:{identifier}";

            if (seen.TryGetValue(scopeKey, out var existing))
            {
                throw new InvalidOperationException(
                    $"Secret stores '{existing.Name}' and '{store.Name}' map to the same Bicep identifier " +
                    $"'{identifier}' within the same scope. Rename one so they produce distinct identifiers. " +
                    "Diagnostic: ASPIRERADIUS048.");
            }

            seen[scopeKey] = store;
        }
    }
}
