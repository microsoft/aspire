// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

#pragma warning disable ASPIRECOMPUTE002 // GetEndpointPropertyExpression/GetHostAddressExpression are experimental compute-environment APIs the publisher relies on.
#pragma warning disable ASPIRERADIUS006 // Secret-store model types (RadiusSecretStoreResource, etc.) are experimental; consumed internally by the publisher.
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Radius.Secrets;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Builds an Azure.Provisioning Infrastructure AST from a <see cref="DistributedApplicationModel"/>
/// for a specific Radius environment. Generates typed <c>ProvisionableResource</c> constructs
/// (environments, applications, recipe packs, resource type instances, containers) that are
/// compiled to Bicep via <c>Infrastructure.Build().Compile()</c>.
/// </summary>
internal sealed class RadiusInfrastructureBuilder
{
    private readonly RadiusEnvironmentResource _environment;
    private readonly DistributedApplicationModel _model;
    private readonly ResourceTypeMapper _typeMapper;
    private readonly ILogger _logger;

    /// <summary>
    /// Publish-mode execution context used to resolve container environment variables and
    /// service-discovery values. Set at the start of <see cref="BuildAsync"/>.
    /// </summary>
    private DistributedApplicationExecutionContext _executionContext = null!;
    private CancellationToken _cancellationToken;

    // Bicep parameters allocated for secret/parameter values referenced by container env vars.
    // Keyed by the Aspire parameter name so repeated references reuse a single param declaration.
    // These are emitted as top-level Bicep `param`s (secure when the source parameter is secret)
    // instead of inlining values, so no literal secret is written to the published artifact.
    private readonly Dictionary<string, ProvisioningParameter> _envParametersByName = new(StringComparer.Ordinal);

    // Maps the emitted Bicep parameter identifier to its originating Aspire ParameterResource, so
    // the deploy step can resolve each value at deploy time and pass it via `rad deploy --parameters`.
    private readonly Dictionary<string, ParameterResource> _deployParametersByIdentifier = new(StringComparer.Ordinal);

    // Bicep `param`s allocated for recipe-parameter and inline-secret values that bind an Aspire
    // ParameterResource. Keyed by the Aspire parameter name so repeated references reuse a single
    // declaration; secure when the source parameter is secret so no value is written to the artifact.
    private readonly Dictionary<string, ProvisioningParameter> _recipeParameters = new(StringComparer.Ordinal);

    // Maps the emitted recipe/inline-secret Bicep parameter identifier to its originating Aspire
    // ParameterResource, unioned into RadiusDeployParametersAnnotation so the deploy step resolves a
    // value for every valueless `param` at deploy time.
    private readonly Dictionary<string, ParameterResource> _recipeParameterBindings = new(StringComparer.Ordinal);

    // Guards against two distinct Aspire parameter names sanitizing to the same Bicep identifier,
    // which would emit duplicate `param` declarations (ASPIRERADIUS028). Keyed by Bicep identifier.
    private readonly Dictionary<string, string> _recipeParameterIdentifiers = new(StringComparer.Ordinal);

    // Recipe parameters are user-supplied object graphs, so bound traversal to avoid
    // unbounded recursion from accidental cycles or pathological nesting.
    private const int MaxRecipeParameterNestingDepth = 32;

    // Radius resource-type instances emitted by this environment, keyed by Aspire resource name.
    // Backing-resource values (host/port/credentials) are projected off these constructs instead of
    // being derived from Aspire endpoints. See https://github.com/microsoft/aspire/issues/18935.
    private readonly Dictionary<string, RadiusResourceTypeConstruct> _typeInstancesByResourceName = new(StringComparer.Ordinal);

    // The emitted Radius type string per Aspire resource name. The projection shape depends on the
    // *emitted* type, because the legacy Applications.* and the new Radius.* UDT schemas for the
    // same Aspire resource expose different properties and different secret mechanisms.
    private readonly Dictionary<string, string> _radiusTypeByResourceName = new(StringComparer.Ordinal);

    // Aspire ParameterResources that must be replaced by a recipe-generated secret. A legacy
    // backing resource's password is created by its Radius recipe, so the parameter Aspire
    // generates for local run mode is not the deployed password; substituting the parameter with
    // `<resource>.listSecrets().password` keeps every composed value (connection string, URI,
    // splatted *_PASSWORD) correct without duplicating any connection-string format here.
    private readonly Dictionary<ParameterResource, ProjectedValue> _recipeSecretSubstitutions = [];
    private readonly Dictionary<ParameterResource, (IResource Owner, bool IsProjectionSubstitution)> _recipeCredentialOwners = [];

    // Tracks (resource, parameter) pairs that have already produced an unrelated-use warning, so a
    // parameter referenced by the same resource in multiple env vars only warns once.
    private readonly HashSet<(IResource Resource, ParameterResource Parameter)> _warnedUnrelatedSubstitutions = [];
    private readonly List<ProjectedEnvValue> _projectedEnvValues = [];
    private readonly List<ProjectedTypeProperty> _projectedTypeProperties = [];

    /// <summary>
    /// Default recipe template paths per resource type.
    /// </summary>
    private static readonly Dictionary<string, string> s_defaultRecipeTemplates = new(StringComparer.Ordinal)
    {
        [RadiusResourceTypes.RedisCaches] = "ghcr.io/radius-project/recipes/local-dev/rediscaches:latest",
        // The Radius.* UDT recipes are published under kube-recipes/, not the legacy
        // recipes/local-dev/ prefix that serves the Applications.* portable types. The UDT recipe is
        // the one that reads username/password/database from context.resource.properties, so pairing
        // this type with a local-dev recipe both fails to pull (there is no
        // recipes/local-dev/postgresqldatabases artifact) and would ignore the credentials Aspire
        // sets. See https://github.com/radius-project/resource-types-contrib/blob/main/Data/postgreSqlDatabases/recipes/kubernetes/bicep/kubernetes-postgresql.bicep.
        [RadiusResourceTypes.PostgreSqlDatabases] = "ghcr.io/radius-project/kube-recipes/postgresqldatabases:latest",
        [RadiusResourceTypes.MongoDatabases] = "ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest",
        [RadiusResourceTypes.RabbitMQQueues] = "ghcr.io/radius-project/recipes/local-dev/rabbitmqqueues:latest",
        // The Radius.Compute/containers UDT needs a recipe registered in the env's recipe pack;
        // shipped Radius does not include one by default, so register the published container
        // recipe so native containers deploy without a manually-authored recipe.
        [RadiusResourceTypes.Containers] = "ghcr.io/radius-project/kube-recipes/containers:latest",
        // Legacy fallback types also get default recipes
        [RadiusResourceTypes.LegacyRedisCaches] = "ghcr.io/radius-project/recipes/local-dev/rediscaches:latest",
        [RadiusResourceTypes.LegacyMongoDatabases] = "ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest",
        // Paired with LegacySqlDatabases, which is what SqlServerServerResource emits. The
        // Radius.Data/sqlServerDatabases UDT has no published kube-recipes artifact yet, so there is
        // no default recipe to register for it.
        [RadiusResourceTypes.LegacySqlDatabases] = "ghcr.io/radius-project/recipes/local-dev/sqldatabases:latest",
        [RadiusResourceTypes.LegacyRabbitMQQueues] = "ghcr.io/radius-project/recipes/local-dev/rabbitmqqueues:latest",
        [RadiusResourceTypes.LegacyDaprStateStores] = "ghcr.io/radius-project/recipes/local-dev/daprstatestores:latest",
        [RadiusResourceTypes.LegacyDaprPubSubBrokers] = "ghcr.io/radius-project/recipes/local-dev/daprpubsubbrokers:latest",
    };

    internal RadiusInfrastructureBuilder(
        RadiusEnvironmentResource environment,
        DistributedApplicationModel model,
        ResourceTypeMapper typeMapper,
        ILogger logger)
    {
        _environment = environment;
        _model = model;
        _typeMapper = typeMapper;
        _logger = logger;
    }

    /// <summary>
    /// Builds the Bicep AST and populates a <see cref="RadiusInfrastructureOptions"/> with
    /// typed constructs. Runs <c>ConfigureRadiusInfrastructure</c> callbacks last (last-write-wins).
    /// </summary>
    /// <param name="executionContext">
    /// Publish-mode execution context used to resolve container environment variables and
    /// service-discovery values from the application model.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the build.</param>
    internal async Task<RadiusInfrastructureOptions> BuildAsync(
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        _executionContext = executionContext;
        _cancellationToken = cancellationToken;

        var options = new RadiusInfrastructureOptions();
        var envIdentifier = BicepPostProcessor.SanitizeIdentifier(_environment.Name);

        // Classify resources for this environment. ResolveResourceType is computed once per
        // resource here and reused below — calling it repeatedly would re-emit the
        // ResourceTypeMapper Info/Warning logs (legacy fallback / unmapped type) for every
        // resource, producing duplicate noise on every publish.
        var (radiusResources, computeResources, resolvedTypes) = ClassifyResources();

        // 1. UDT recipe pack (created first so environment can reference its ID)
        var recipePackIdentifier = "recipepack";
        var udtRecipeEntries = new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
        var legacyRecipeEntries = new Dictionary<string, Dictionary<string, RecipeEntry>>(StringComparer.Ordinal);

        // Radius binds one recipe per resource type per environment. Each type gets its default
        // in-cluster recipe: UDT (Radius.*) types via the shared recipe pack, legacy
        // Applications.* types via inline named recipes on the legacy environment. Per-instance
        // and custom recipe overrides are not part of this PR — they arrive with the follow-up
        // that reintroduces the recipe customization API.
        foreach (var resource in radiusResources)
        {
            var (resourceType, _) = resolvedTypes[resource];

            if (IsLegacyResourceType(resourceType))
            {
                AddLegacyRecipeEntry(legacyRecipeEntries, resourceType);
            }
            else
            {
                AddRecipeEntry(udtRecipeEntries, resourceType);
            }
        }

        // Partition flags.
        var hasUdtResources = radiusResources.Any(r =>
            !IsLegacyResourceType(resolvedTypes[r].ResourceType));
        var hasLegacyResources = radiusResources.Any(r =>
            IsLegacyResourceType(resolvedTypes[r].ResourceType));
        var hasComputeResources = computeResources.Any();

        // Radius secret stores routed to this environment. Applications.Core/secretStores is a
        // legacy Applications.Core resource, so its presence forces the legacy environment/
        // application chain (which it references for scope). No-op when no store is declared,
        // keeping the default path byte-for-byte unchanged.
        var secretStoresForScope = GetSecretStoresForScope().ToList();
        var hasSecretStores = secretStoresForScope.Count > 0;

        // Secret-store consumers (recipeConfig auth / envSecrets) also require the legacy
        // Applications.Core/environments chain, since recipeConfig lives on that resource.
        var secretStoresAnnotation = _environment.Annotations
            .OfType<Annotations.RadiusSecretStoresAnnotation>()
            .FirstOrDefault();
        var hasSecretStoreConsumers = secretStoresAnnotation is { Consumers.Count: > 0 };

        // Compute workloads always route to the UDT compute container type
        // (Radius.Compute/containers), which forces the UDT environment/application chain.
        var computeForcesUdtChain = hasComputeResources;

        // 2. UDT environment + application — emitted only when we have UDT
        // radius resources or any UDT-bound compute workload. Pure-legacy
        // publishes (Redis-only) skip the UDT chain entirely so older Radius
        // installs aren't forced to understand `Radius.Core/*`.
        RadiusRecipePackConstruct? recipePackConstruct = null;
        RadiusEnvironmentConstruct? envConstruct = null;
        RadiusApplicationConstruct? appConstruct = null;
        var appIdentifier = "app";

        if (hasUdtResources || computeForcesUdtChain)
        {
            // UDT containers route to Radius.Compute/containers, which the control plane
            // provisions through a recipe. Register the default container recipe in the
            // pack so native containers deploy on shipped Radius without a hand-authored
            // recipe — mirroring how backing resources get their default recipes.
            if (computeForcesUdtChain)
            {
                AddRecipeEntry(udtRecipeEntries, RadiusResourceTypes.Containers);
            }

            recipePackConstruct = CreateRecipePackConstruct(recipePackIdentifier, udtRecipeEntries);
            options.RecipePacks.Add(recipePackConstruct);

            envConstruct = CreateEnvironmentConstruct(envIdentifier, recipePackConstruct);
            options.Environments.Add(envConstruct);

            appConstruct = CreateApplicationConstruct(appIdentifier, envConstruct);
            options.Applications.Add(appConstruct);
        }

        // 3. Legacy parents are emitted lazily — only if any legacy backing
        // resource, secret store, or secret-store consumer is present. Legacy
        // env/app share the *resource name* with the UDT pair so Radius still
        // sees them as the same logical app/environment; only the Bicep
        // identifiers differ.
        LegacyApplicationEnvironmentConstruct? legacyEnvConstruct = null;
        LegacyApplicationConstruct? legacyAppConstruct = null;

        if (hasLegacyResources || hasSecretStores || hasSecretStoreConsumers)
        {
            // If the UDT chain is also emitted we suffix legacy identifiers with
            // `_legacy`; otherwise (pure-legacy publish) legacy can claim the
            // unsuffixed identifiers.
            var legacyEnvIdentifier = (hasUdtResources || computeForcesUdtChain)
                ? envIdentifier + "_legacy" : envIdentifier;
            var legacyAppIdentifier = (hasUdtResources || computeForcesUdtChain)
                ? appIdentifier + "_legacy" : appIdentifier;

            legacyEnvConstruct = CreateLegacyEnvironmentConstruct(
                legacyEnvIdentifier, legacyRecipeEntries);
            options.LegacyEnvironments.Add(legacyEnvConstruct);

            legacyAppConstruct = CreateLegacyApplicationConstruct(
                legacyAppIdentifier, appIdentifier, BuildIdExpression(legacyEnvConstruct));

            options.LegacyApplications.Add(legacyAppConstruct);
        }

        // Secret stores (Applications.Core/secretStores) — emitted after the legacy chain they
        // reference for scope. No-op when no store is declared.
        var secretStoreConstructs = EmitSecretStores(options, secretStoresForScope, legacyEnvConstruct, legacyAppConstruct);

        // 4. Resource type instances — parent wiring depends on legacy vs UDT.
        // Track each builder-created instance's parent pair so RewireIdReferences
        // can re-resolve `.id` after callbacks without clobbering resources that
        // a callback added itself.
        var instanceParents = new Dictionary<RadiusResourceTypeConstruct, (ProvisionableResource? Env, ProvisionableResource App)>();

        foreach (var resource in radiusResources)
        {
            var (resourceType, apiVersion) = resolvedTypes[resource];
            var identifier = BicepPostProcessor.SanitizeIdentifier(resource.Name);

            var isLegacy = IsLegacyResourceType(resourceType);

            ProvisionableResource? parentEnv = isLegacy ? legacyEnvConstruct : envConstruct;
            ProvisionableResource parentApp = isLegacy ? legacyAppConstruct! : appConstruct!;

            var typeInstance = CreateResourceTypeConstruct(
                identifier, resource.Name, resourceType, apiVersion,
                parentApp, parentEnv);
            options.ResourceTypeInstances.Add(typeInstance);
            _typeInstancesByResourceName[resource.Name] = typeInstance;
            _radiusTypeByResourceName[resource.Name] = resourceType;
            instanceParents[typeInstance] = (parentEnv, parentApp);
        }

        // 4b. Wire backing-resource credentials before any container env var is resolved, so the
        // substitutions below are in place by the time connection strings are composed.
        await ApplyBackingResourceCredentialsAsync(radiusResources).ConfigureAwait(false);

        // 5. Container workloads always route to the UDT compute container type
        // (Radius.Compute/containers) parented to the UDT application.
        var containerConnectionTargets = new Dictionary<RadiusContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>>();

        // Records the literal container ports (endpoint name -> port + protocol) that service
        // discovery was derived from, keyed by the immutable container map key (the resource name),
        // so a ConfigureRadiusInfrastructure callback that later changes/removes a port — or replaces
        // or drops the whole container — can be rejected after callbacks run. Keying by the stable
        // map key (not the construct instance) means a callback that swaps in a new construct for the
        // same workload is still validated. See ValidatePostCallbackContainerInvariants.
        var containerPortSnapshots = new Dictionary<string, Dictionary<string, (int Port, string Protocol)>>(StringComparer.Ordinal);
        foreach (var resource in computeResources)
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(resource.Name);
            var image = GetContainerImage(resource);
            var connectionTargets = GetConnectionTargets(resource, radiusResources, _typeInstancesByResourceName);
            WarnIfImageMayNotPull(resource.Name, image);

            // Resolve the resource's environment variables (config, connection strings, OTEL_*,
            // WithEnvironment, and `services__*` service discovery) and its endpoint ports the
            // same way the Kubernetes publisher does, so the deployed container behaves like the
            // local run. Secret/parameter values are routed to Bicep `param`s (never literals).
            var projectedStart = _projectedEnvValues.Count;
            var env = await ResolveEnvironmentAsync(resource).ConfigureAwait(false);
            var ports = ResolvePorts(resource);

            var containerConstruct = CreateContainerConstruct(
                identifier, resource.Name, image, appConstruct!, envConstruct, connectionTargets, env, ports);

            // Associate the values projected above with the construct that now owns them, so a
            // callback that replaces or drops the workload can be told apart from one that renames
            // a backing resource. See RebuildProjectedEnvValues.
            for (var i = projectedStart; i < _projectedEnvValues.Count; i++)
            {
                _projectedEnvValues[i].Container = containerConstruct;
            }
            options.Containers.Add(containerConstruct);
            containerConnectionTargets[containerConstruct] = connectionTargets;
            containerPortSnapshots[resource.Name] = ports.ToDictionary(
                kv => kv.Key,
                kv => (
                    ((IBicepValue)kv.Value.ContainerPort).LiteralValue is int literalPort ? literalPort : -1,
                    ((IBicepValue)kv.Value.Protocol).LiteralValue is string literalProtocol ? literalProtocol : string.Empty),
                StringComparer.Ordinal);
        }

        // Emit the Bicep parameters allocated for secret/parameter-backed container env vars as
        // top-level `param`s, before ConfigureRadiusInfrastructure runs so callbacks can see them.
        options.Parameters.AddRange(_envParametersByName.Values);

        // 6. Snapshot every identifier that rewiring depends on, then run
        // ConfigureRadiusInfrastructure callbacks (last-write-wins). We only
        // re-resolve a `.id` reference below if its target was *renamed* by a
        // callback; references the callback set explicitly are preserved.
        var identifierSnapshot = new IdentifierSnapshot(
            envConstruct?.BicepIdentifier,
            appConstruct?.BicepIdentifier,
            legacyEnvConstruct?.BicepIdentifier,
            legacyAppConstruct?.BicepIdentifier,
            options.RecipePacks.ToDictionary(p => p, p => p.BicepIdentifier),
            instanceParents.ToDictionary(
                kv => kv.Key,
                kv => (EnvId: kv.Value.Env?.BicepIdentifier,
                       AppId: kv.Value.App.BicepIdentifier)),
            containerConnectionTargets.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(
                    tkv => tkv.Key, tkv => tkv.Value.BicepIdentifier)),
            secretStoreConstructs.Values.ToDictionary(c => c, c => c.BicepIdentifier));

        RunConfigureCallbacks(options);

        // Validate the post-callback container set. A ConfigureRadiusInfrastructure callback can
        // rename containers, mutate/remove ports, add ports to a previously portless container, or
        // replace/drop a workload entirely. Service discovery (`services__*` URLs and the recipe
        // Service name/port) was derived from the pre-callback model, so all of these can silently
        // break cross-container calls or emit an invalid manifest. Validate the final state and fail
        // fast on any detectable divergence.
        ValidatePostCallbackContainerInvariants(options, containerPortSnapshots);

        // Container env values that read a backing resource's recipe outputs capture that
        // resource's Bicep identifier, so a callback rename breaks them the same way it breaks a
        // `.id` reference. Repair them before the `.id` rewiring below.
        RebuildProjectedEnvValues(options);

        // 7. Rewire `.id` cross-references for targets whose BicepIdentifier
        // was changed by a callback; leave everything else (including callback
        // edits to references) alone.
        RewireIdReferences(options, appConstruct, envConstruct,
            legacyAppConstruct, legacyEnvConstruct, instanceParents,
            containerConnectionTargets,
            identifierSnapshot);

        // Secret stores participate in the same escape-hatch surface, so their consumer references
        // (recipeConfig `<store>.id`) and parent scope IDs must be rewired too when a callback
        // renames a store construct or the legacy application/environment it is scoped to.
        RewireSecretStoreReferences(secretStoresForScope, secretStoreConstructs,
            legacyAppConstruct, legacyEnvConstruct, identifierSnapshot);

        // Surface recipe-parameter scopes that target a resource type with no emitted recipe
        // entry, and register any ParameterResource-backed recipe/inline-secret Bicep params.
        WarnUnmatchedResourceTypeScopes(udtRecipeEntries.Keys.Concat(legacyRecipeEntries.Keys));
        foreach (var (name, parameter) in _recipeParameters)
        {
            options.RecipeParameters[name] = parameter;
        }

        // Surface the param-identifier -> ParameterResource bindings so the deploy step can
        // resolve a value for every valueless `param` at deploy time (rad deploy --parameters).
        foreach (var (identifier, parameter) in _recipeParameterBindings)
        {
            options.RecipeParameterBindings[identifier] = parameter;
        }

        RecordDeployParameters();

        return options;
    }

    // Records the emitted Bicep parameter identifier → ParameterResource mapping on the
    // environment resource so the deploy step can resolve each value at deploy time and pass it
    // via `rad deploy --parameters`. Replaces any prior annotation so a re-publish (e.g. repeated
    // BuildAsync calls) stays idempotent rather than accumulating stale mappings.
    private void RecordDeployParameters()
    {
        foreach (var existing in _environment.Annotations.OfType<RadiusDeployParametersAnnotation>().ToList())
        {
            _environment.Annotations.Remove(existing);
        }

        // Persist the union of PR1 container-env parameters and PR2 recipe/inline-secret
        // parameter bindings. A parameter referenced by both a container env var and a recipe/
        // secret value must resolve to exactly one deploy binding, so merge rather than replace.
        var deployParameters = new Dictionary<string, ParameterResource>(_deployParametersByIdentifier, StringComparer.Ordinal);
        foreach (var (identifier, parameter) in _recipeParameterBindings)
        {
            deployParameters[identifier] = parameter;
        }

        if (deployParameters.Count > 0)
        {
            _environment.Annotations.Add(new RadiusDeployParametersAnnotation(deployParameters));
        }
    }

    /// <summary>
    /// Pre-callback snapshot of every construct identifier the builder wired
    /// references against. After callbacks run, <see cref="RewireIdReferences"/>
    /// compares each target's current identifier against the snapshot and only
    /// rewires the ones that changed — preserving any direct reference edits a
    /// callback performed.
    /// </summary>
    private sealed record IdentifierSnapshot(
        string? EnvId,
        string? AppId,
        string? LegacyEnvId,
        string? LegacyAppId,
        Dictionary<RadiusRecipePackConstruct, string> RecipePackIds,
        Dictionary<RadiusResourceTypeConstruct, (string? EnvId, string AppId)> InstanceParentIds,
        Dictionary<RadiusContainerConstruct, Dictionary<string, string>> ContainerConnectionTargetIds,
        Dictionary<RadiusSecretStoreConstruct, string> SecretStoreIds);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="resourceType"/> is a legacy
    /// <c>Applications.*</c> type that should be parented to
    /// <c>Applications.Core/environments</c> rather than <c>Radius.Core/environments</c>.
    /// </summary>
    private static bool IsLegacyResourceType(string resourceType) =>
        resourceType.StartsWith("Applications.", StringComparison.Ordinal);

    /// <summary>
    /// After callbacks run, re-resolve each builder-created <c>.id</c>
    /// cross-reference only when its target's <c>BicepIdentifier</c> was
    /// actually changed by a callback. References the callback edited directly
    /// (without renaming the target) are preserved — honouring the public
    /// "last-write-wins" contract on <c>ConfigureRadiusInfrastructure</c>.
    /// </summary>
    private static void RewireIdReferences(
        RadiusInfrastructureOptions options,
        RadiusApplicationConstruct? appConstruct,
        RadiusEnvironmentConstruct? envConstruct,
        LegacyApplicationConstruct? legacyAppConstruct,
        LegacyApplicationEnvironmentConstruct? legacyEnvConstruct,
        Dictionary<RadiusResourceTypeConstruct, (ProvisionableResource? Env, ProvisionableResource App)> instanceParents,
        Dictionary<RadiusContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>> containerConnectionTargets,
        IdentifierSnapshot snapshot)
    {
        // UDT env → recipe packs. Rebuild only if any builder-created pack was
        // renamed. (New packs added by a callback and removed packs are left to
        // the callback to wire up — this method only fixes broken refs.)
        if (envConstruct is not null)
        {
            var anyPackRenamed = false;
            foreach (var (pack, snapId) in snapshot.RecipePackIds)
            {
                if (!string.Equals(pack.BicepIdentifier, snapId, StringComparison.Ordinal))
                {
                    anyPackRenamed = true;
                    break;
                }
            }

            if (anyPackRenamed)
            {
                envConstruct.RecipePacks.Clear();
                foreach (var pack in options.RecipePacks)
                {
                    envConstruct.RecipePacks.Add(BuildIdExpression(pack));
                }
            }
        }

        // UDT app → UDT env.
        if (appConstruct is not null && envConstruct is not null &&
            IdentifierChanged(envConstruct, snapshot.EnvId))
        {
            appConstruct.EnvironmentId = BuildIdExpression(envConstruct);
        }

        // Legacy app → legacy env.
        if (legacyAppConstruct is not null && legacyEnvConstruct is not null &&
            IdentifierChanged(legacyEnvConstruct, snapshot.LegacyEnvId))
        {
            legacyAppConstruct.EnvironmentId = BuildIdExpression(legacyEnvConstruct);
        }

        // Resource type instances: rewire each parent ref only if *that*
        // parent's identifier was renamed.
        foreach (var instance in options.ResourceTypeInstances)
        {
            if (!instanceParents.TryGetValue(instance, out var parents))
            {
                continue;
            }

            if (!snapshot.InstanceParentIds.TryGetValue(instance, out var snapIds))
            {
                continue;
            }

            if (!string.Equals(parents.App.BicepIdentifier, snapIds.AppId, StringComparison.Ordinal))
            {
                instance.ApplicationId = BuildIdExpression(parents.App);
            }

            if (parents.Env is not null &&
                !string.Equals(parents.Env.BicepIdentifier, snapIds.EnvId, StringComparison.Ordinal))
            {
                instance.EnvironmentId = BuildIdExpression(parents.Env);
            }
        }

        // Containers — rewire ApplicationId only if the UDT app was renamed;
        // rewire each connection source only if its target was renamed.
        foreach (var container in options.Containers)
        {
            if (!containerConnectionTargets.TryGetValue(container, out var targets))
            {
                // Callback-added container; leave its refs alone.
                continue;
            }

            if (appConstruct is not null && IdentifierChanged(appConstruct, snapshot.AppId))
            {
                container.ApplicationId = BuildIdExpression(appConstruct);
            }

            if (envConstruct is not null && IdentifierChanged(envConstruct, snapshot.EnvId))
            {
                container.EnvironmentId = BuildIdExpression(envConstruct);
            }

            if (targets.Count == 0 ||
                !snapshot.ContainerConnectionTargetIds.TryGetValue(container, out var targetSnapIds))
            {
                continue;
            }

            foreach (var (connectionName, targetConstruct) in targets)
            {
                if (!targetSnapIds.TryGetValue(connectionName, out var snapTargetId))
                {
                    continue;
                }

                if (string.Equals(targetConstruct.BicepIdentifier, snapTargetId, StringComparison.Ordinal))
                {
                    continue;
                }

                // Target was renamed — replace the stale connection entry.
                container.Connections[connectionName] = new ConnectionConstruct
                {
                    Source = BuildIdExpression(targetConstruct),
                };
            }
        }
    }

    private static bool IdentifierChanged(ProvisionableResource resource, string? snapshotId)
        => !string.Equals(resource.BicepIdentifier, snapshotId, StringComparison.Ordinal);

    /// <summary>
    /// After callbacks run, rewire secret-store cross-references whose target was renamed:
    /// <list type="bullet">
    /// <item>a store's <c>ApplicationId</c>/<c>EnvironmentId</c> parent scope, if the legacy
    /// application/environment it points at was renamed; and</item>
    /// <item>the environment's <c>recipeConfig</c>, which references consumed stores by
    /// <c>&lt;identifier&gt;.id</c>, if any store construct was renamed.</item>
    /// </list>
    /// Mirrors <see cref="RewireIdReferences"/> for the secret-store surface exposed via
    /// <see cref="RadiusInfrastructureOptions.SecretStores"/>.
    /// </summary>
    private void RewireSecretStoreReferences(
        IReadOnlyList<RadiusSecretStoreResource> stores,
        IReadOnlyDictionary<string, RadiusSecretStoreConstruct> storeConstructs,
        LegacyApplicationConstruct? legacyAppConstruct,
        LegacyApplicationEnvironmentConstruct? legacyEnvConstruct,
        IdentifierSnapshot snapshot)
    {
        if (storeConstructs.Count == 0)
        {
            return;
        }

        // Parent scope IDs: an application-scoped store references the legacy application, an
        // environment-scoped store the legacy environment. If a callback renamed that parent
        // construct, the store's ApplicationId/EnvironmentId still points at the old symbol.
        var legacyAppRenamed = legacyAppConstruct is not null && IdentifierChanged(legacyAppConstruct, snapshot.LegacyAppId);
        var legacyEnvRenamed = legacyEnvConstruct is not null && IdentifierChanged(legacyEnvConstruct, snapshot.LegacyEnvId);

        if (legacyAppRenamed || legacyEnvRenamed)
        {
            foreach (var store in stores)
            {
                if (!storeConstructs.TryGetValue(store.Name, out var construct))
                {
                    continue;
                }

                // Mirror the scope selection used when the store was emitted (see EmitSecretStores).
                if (store.Scope == RadiusSecretStoreScope.Application && legacyAppConstruct is not null)
                {
                    if (legacyAppRenamed)
                    {
                        construct.ApplicationId = BuildIdExpression(legacyAppConstruct);
                    }
                }
                else if (legacyEnvConstruct is not null && legacyEnvRenamed)
                {
                    construct.EnvironmentId = BuildIdExpression(legacyEnvConstruct);
                }
            }
        }

        // recipeConfig references each consumed store by `<identifier>.id`. It is a single serialized
        // object (not individually addressable per store), so — unlike the per-reference constructs
        // above — the consistent way to honor a store rename is to rebuild the whole recipeConfig from
        // the current constructs. Only do so when a store was actually renamed, preserving direct
        // callback edits in every other case.
        var anyStoreRenamed = false;
        foreach (var (construct, snapId) in snapshot.SecretStoreIds)
        {
            if (!string.Equals(construct.BicepIdentifier, snapId, StringComparison.Ordinal))
            {
                anyStoreRenamed = true;
                break;
            }
        }

        if (anyStoreRenamed && legacyEnvConstruct is not null)
        {
            ApplySecretStoreConsumers(legacyEnvConstruct, storeConstructs);
        }
    }

    private (string ResourceType, string ApiVersion) ResolveResourceType(IResource resource)
    {
        return _typeMapper.MapResource(resource);
    }

    private (List<IResource> radiusResources, List<IResource> computeResources, Dictionary<IResource, (string ResourceType, string ApiVersion)> resolvedTypes) ClassifyResources()
    {
        var radiusTypes = new List<IResource>();
        var compute = new List<IResource>();
        var resolved = new Dictionary<IResource, (string ResourceType, string ApiVersion)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in _model.Resources)
        {
            // Skip the Radius environment itself
            if (resource is RadiusEnvironmentResource)
            {
                continue;
            }

            // Check deployment target: only include resources targeted to this environment
            // or resources with no explicit target (default to this environment)
            if (!IsTargetedToThisEnvironment(resource))
            {
                continue;
            }

            // Resolve child resources to parent
            var resolvedResource = ResolveToParent(resource);
            if (resolvedResource != resource)
            {
                // Child resources (e.g., SqlServerDatabaseResource) are represented
                // via their parent; skip the child itself
                continue;
            }

            // Avoid duplicates
            if (!seen.Add(resource.Name))
            {
                continue;
            }

            // Use ResourceTypeMapper to determine classification:
            // - Explicit container/project resources with Containers mapping → compute workloads
            // - Resources with a specific resource type mapping → resource type instances
            // - Unmapped resources (ParameterResource, etc.) → skip
            var resolvedType = ResolveResourceType(resource);
            resolved[resource] = resolvedType;
            var resourceType = resolvedType.ResourceType;

            if (resource is ProjectResource ||
                (resource is ContainerResource && resourceType == RadiusResourceTypes.Containers))
            {
                compute.Add(resource);
            }
            else if (resourceType != RadiusResourceTypes.Containers)
            {
                radiusTypes.Add(resource);
            }
            // else: unmapped resource (e.g., ParameterResource) — skip
        }

        return (radiusTypes, compute, resolved);
    }

    private bool IsTargetedToThisEnvironment(IResource resource)
    {
        // The PrepareDeploymentTargets pipeline step (RadiusInfrastructure.PrepareDeploymentTargetsAsync)
        // attaches a DeploymentTargetAnnotation to every compute resource that belongs to this
        // environment, with ComputeEnvironment set to OwningComputeEnvironment ?? this. With multiple
        // compute environments in the model, untargeted resources are rejected upstream by
        // ValidateComputeEnvironments before this code runs.
        //
        // Use the framework's canonical lookup (Aspire.Hosting.ApplicationModel.ResourceExtensions
        // .GetDeploymentTargetAnnotation) so behaviour stays in sync with manifest/publish paths
        // and so the lookup honours ComputeEnvironmentAnnotation overrides set via WithComputeEnvironment.
        var targetComputeEnvironment = _environment.OwningComputeEnvironment ?? _environment;
        return resource.GetDeploymentTargetAnnotation(targetComputeEnvironment) is not null;
    }

    /// <summary>
    /// Resolves a child resource (e.g., SqlServerDatabaseResource) to its parent.
    /// Returns the resource itself if it has no parent.
    /// </summary>
    private static IResource ResolveToParent(IResource resource)
    {
        if (resource is IResourceWithParent childResource)
        {
            return childResource.Parent;
        }

        return resource;
    }

    /// <summary>
    /// Builds a <c>.id</c> expression for a resource, e.g., <c>envIdentifier.id</c>.
    /// </summary>
    private static BicepExpression BuildIdExpression(Azure.Provisioning.Primitives.ProvisionableResource resource)
    {
        return new MemberExpression(new IdentifierExpression(resource.BicepIdentifier), "id");
    }

    private RadiusEnvironmentConstruct CreateEnvironmentConstruct(
        string identifier, RadiusRecipePackConstruct recipePackConstruct)
    {
        var construct = new RadiusEnvironmentConstruct(identifier);
        construct.EnvironmentName = _environment.Name;
        construct.KubernetesNamespace = _environment.Namespace;
        construct.RecipePacks.Add(BuildIdExpression(recipePackConstruct));
        ApplyCloudProviders(construct);
        return construct;
    }

    private void ApplyCloudProviders(RadiusEnvironmentConstruct construct)
    {
        var annotation = _environment.Annotations
            .OfType<Annotations.RadiusCloudProvidersAnnotation>()
            .FirstOrDefault();
        if (annotation is null)
        {
            return;
        }

        if (annotation.Azure is { } azure)
        {
            construct.AzureSubscriptionId = azure.SubscriptionId;
            construct.AzureResourceGroupName = azure.ResourceGroup;
        }

        if (annotation.Aws is { } aws)
        {
            construct.AwsAccountId = aws.AccountId;
            construct.AwsRegion = aws.Region;
        }
    }

    // The legacy Applications.Core/environments schema carries cloud providers under the
    // same properties.providers.{azure,aws}.scope paths as the UDT environment. Apply them
    // here too so a pure-legacy publish (e.g. a managed Redis with no UDT compute) still
    // emits the provider configuration that the publish-time ASPIRERADIUS020 check requires.
    private void ApplyCloudProviders(LegacyApplicationEnvironmentConstruct construct)
    {
        var annotation = _environment.Annotations
            .OfType<Annotations.RadiusCloudProvidersAnnotation>()
            .FirstOrDefault();
        if (annotation is null)
        {
            return;
        }

        if (annotation.Azure is { } azure)
        {
            construct.AzureScope = BuildAzureScope(azure);
        }

        if (annotation.Aws is { } aws)
        {
            construct.AwsScope = BuildAwsScope(aws);
        }
    }

    private static string BuildAzureScope(CloudProviders.AzureRadiusProviderConfig azure)
        => $"/subscriptions/{azure.SubscriptionId}/resourceGroups/{azure.ResourceGroup}";

    private static string BuildAwsScope(CloudProviders.AwsRadiusProviderConfig aws)
        => $"/planes/aws/aws/accounts/{aws.AccountId}/regions/{aws.Region}";

    private static RadiusApplicationConstruct CreateApplicationConstruct(
        string identifier, RadiusEnvironmentConstruct? envConstruct)
    {
        var construct = new RadiusApplicationConstruct(identifier);
        construct.ApplicationName = identifier;
        construct.EnvironmentId = BuildIdExpression(envConstruct!);
        return construct;
    }

    private static RadiusResourceTypeConstruct CreateResourceTypeConstruct(
        string identifier, string resourceName, string resourceType, string apiVersion,
        ProvisionableResource appConstruct, ProvisionableResource? envConstruct)
    {
        var construct = new RadiusResourceTypeConstruct(identifier, resourceType, apiVersion);
        construct.ResourceName = resourceName;
        construct.ApplicationId = BuildIdExpression(appConstruct);
        construct.EnvironmentId = BuildIdExpression(envConstruct!);

        // Every instance binds its resource type's single default recipe (UDT types via the
        // shared recipe pack, legacy types via the "default" entry on the legacy environment),
        // so no per-instance recipe name is emitted here. Per-instance / named recipe overrides
        // are deferred to the follow-up that reintroduces the recipe customization API.
        return construct;
    }

    private void AddRecipeEntry(
        Dictionary<string, RecipeEntry> entries,
        string resourceType)
    {
        if (s_defaultRecipeTemplates.TryGetValue(resourceType, out var defaultTemplate))
        {
            // Don't overwrite a custom entry a ConfigureRadiusInfrastructure callback may add.
            entries.TryAdd(resourceType, new RecipeEntry("bicep", defaultTemplate));
        }
        else
        {
            _logger.LogWarning(
                "No default recipe template found for resource type '{ResourceType}'. " +
                "Register a recipe for this type via ConfigureRadiusInfrastructure().",
                resourceType);
        }
    }

    private RadiusRecipePackConstruct CreateRecipePackConstruct(
        string identifier, Dictionary<string, RecipeEntry> recipeEntries)
    {
        var construct = new RadiusRecipePackConstruct(identifier);
        construct.PackName = "default";

        foreach (var (type, entry) in recipeEntries)
        {
            var recipeEntry = new RecipeEntryConstruct
            {
                RecipeKind = entry.RecipeKind,
                RecipeLocation = entry.RecipeLocation,
            };

            // Apply environment-level WithRecipeParameters for this resource type (environment-wide
            // merged with any resource-type-scoped overrides). No-op when none are declared.
            var parameters = GetEffectiveRecipeParameters(type);
            if (parameters is not null)
            {
                ApplyRecipeParameters(recipeEntry.Parameters, parameters);
            }

            construct.Recipes[type] = recipeEntry;
        }

        return construct;
    }

    private void AddLegacyRecipeEntry(
        Dictionary<string, Dictionary<string, RecipeEntry>> entries,
        string resourceType)
    {
        // Legacy Applications.* types register their recipe under the "default" name on the
        // legacy environment. The outer map is keyed by recipe name so a future PR can register
        // multiple named recipes per type; this PR only emits the single default recipe.
        const string recipeName = "default";

        if (!entries.TryGetValue(resourceType, out var byName))
        {
            byName = new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
            entries[resourceType] = byName;
        }

        if (s_defaultRecipeTemplates.TryGetValue(resourceType, out var defaultTemplate))
        {
            byName.TryAdd(recipeName, new RecipeEntry("bicep", defaultTemplate));
        }
        else
        {
            _logger.LogWarning(
                "No default recipe template found for legacy resource type '{ResourceType}'. " +
                "Register a recipe for this type via ConfigureRadiusInfrastructure().",
                resourceType);
        }
    }

    private LegacyApplicationEnvironmentConstruct CreateLegacyEnvironmentConstruct(
        string identifier,
        Dictionary<string, Dictionary<string, RecipeEntry>> legacyRecipeEntries)
    {
        var construct = new LegacyApplicationEnvironmentConstruct(identifier);
        // Resource name intentionally matches the UDT environment so Radius
        // treats both parents as the same logical environment scope.
        construct.EnvironmentName = _environment.Name;
        construct.ComputeKind = "kubernetes";
        construct.ComputeNamespace = _environment.Namespace;
        ApplyCloudProviders(construct);

        foreach (var (resourceType, byName) in legacyRecipeEntries)
        {
            var inner = new BicepDictionary<LegacyRecipeEntryConstruct>();
            var parameters = GetEffectiveRecipeParameters(resourceType);
            foreach (var (recipeName, entry) in byName)
            {
                var legacyEntry = new LegacyRecipeEntryConstruct
                {
                    TemplateKind = entry.RecipeKind,
                    TemplatePath = entry.RecipeLocation,
                };

                // Apply environment-level WithRecipeParameters for this legacy resource type.
                // No-op when none are declared.
                if (parameters is not null)
                {
                    ApplyRecipeParameters(legacyEntry.Parameters, parameters);
                }

                inner[recipeName] = legacyEntry;
            }
            construct.Recipes[resourceType] = inner;
        }

        return construct;
    }

    private static LegacyApplicationConstruct CreateLegacyApplicationConstruct(
        string identifier, string applicationName,
        BicepValue<string> environmentId)
    {
        var construct = new LegacyApplicationConstruct(identifier);
        // Share the UDT application's `name:` — rubber-duck feedback: only the
        // Bicep identifier is suffixed with `_legacy`.
        construct.ApplicationName = applicationName;
        construct.EnvironmentId = environmentId;
        return construct;
    }

    private static string GetContainerImage(IResource resource)
    {
        var imageAnnotation = resource.Annotations.OfType<ContainerImageAnnotation>().FirstOrDefault();

        if (imageAnnotation is not null)
        {
            var image = imageAnnotation.Image;
            if (!string.IsNullOrEmpty(imageAnnotation.Tag))
            {
                image = $"{image}:{imageAnnotation.Tag}";
            }

            if (!string.IsNullOrEmpty(imageAnnotation.Registry))
            {
                image = $"{imageAnnotation.Registry}/{image}";
            }

            return image;
        }

        // ProjectResource has no ContainerImageAnnotation by default — the integration does
        // not (yet) build and push project images. Failing fast at publish time with a clear
        // remediation prevents the silent `aspire publish && aspire deploy` → in-cluster
        // ImagePullBackOff failure mode, which is opaque to the user (Radius/Kubernetes
        // surface it, not Aspire). Mirrors the CLI behaviour guideline that errors should
        // name the specific action the user must take.
        if (resource is ProjectResource)
        {
            throw new InvalidOperationException(
                $"Project resource '{resource.Name}' cannot be published to Radius because no container image " +
                "has been associated with it. The Aspire.Hosting.Radius integration does not yet build or push " +
                "project images. As a workaround, build and push an image to a registry the target cluster can " +
                "pull from, then attach it via WithContainerImage(\"<registry>/<image>:<tag>\") on the project " +
                "resource. Tracking issue: https://github.com/microsoft/aspire/issues/16844.");
        }

        // Non-project, non-container resources reach this path only in misconfiguration
        // (the resource type mapping would normally skip them). Fall back to a placeholder
        // image with a logged warning via WarnIfImageMayNotPull so the publish still
        // produces inspectable output.
        return $"{resource.Name}:latest";
    }

    /// <summary>
    /// Wires up how each backing resource's credentials reach the consumer, so every value composed
    /// from them (the connection string, the URI, the splatted <c>*_PASSWORD</c> variable) is
    /// consistent with what the recipe actually provisions.
    /// </summary>
    /// <remarks>
    /// Two mechanisms, chosen by the emitted Radius type:
    /// <list type="bullet">
    /// <item><b>Legacy <c>Applications.*</c> types</b> generate their own credentials inside the
    /// recipe and expose them through <c>listSecrets()</c>. Aspire's own generated password is
    /// therefore meaningless at deploy time, so the parameter is substituted for the secret
    /// accessor wherever it appears.</item>
    /// <item><b><c>Radius.*</c> UDTs</b> have no <c>listSecrets()</c>, and their
    /// <c>username</c>/<c>password</c> are <em>required schema properties</em> on the resource
    /// itself that are redacted on read. Aspire writes its own parameters into those properties, so
    /// the deployed credentials are the ones Aspire already composed into the connection string —
    /// the two agree by construction. This also fills in required inputs that were previously never
    /// supplied at all, which the type's schema rejects outright.</item>
    /// </list>
    /// Because both mechanisms operate on the *values* Aspire's own connection-string expressions
    /// are built from, no connection-string format is duplicated here.
    /// </remarks>
    private async Task ApplyBackingResourceCredentialsAsync(List<IResource> radiusResources)
    {
        var referencedResourceNames = GetReferencedResourceNames();

        foreach (var resource in radiusResources)
        {
            if (!_radiusTypeByResourceName.TryGetValue(resource.Name, out var radiusType) ||
                !ResourceTypeMapper.IsBackingResource(resource))
            {
                continue;
            }

            // A backing resource this environment emits but the schema table does not describe
            // cannot have its credentials wired at all, so every consumer would silently receive the
            // password Aspire generated for local run mode instead of the one the recipe creates.
            // That is exactly https://github.com/microsoft/aspire/issues/18935, so fail here rather
            // than waiting for a consumer to reference it (ApplyBackingResourceCredentials runs for
            // every emitted resource; TryProjectBackingEndpoint only runs for referenced ones).
            if (RadiusBackingConnections.GetSchema(radiusType) is not { } schema)
            {
                throw new RadiusBackingResourceEndpointException(
                    resource,
                    $"Resource '{resource.Name}' is emitted as Radius type '{radiusType}', for which Aspire has no " +
                    $"connection schema, so its recipe-generated credentials cannot be projected to consumers. Map the " +
                    $"resource to a type Aspire describes, or set the connection values explicitly with WithEnvironment. " +
                    $"Diagnostic: ASPIRERADIUS071.");
            }

            if (resource is not IResourceWithConnectionString withConnectionString ||
                !_typeInstancesByResourceName.TryGetValue(resource.Name, out var construct))
            {
                continue;
            }

            switch (schema.Credentials)
            {
                case RadiusBackingConnections.RadiusCredentialMode.ListSecrets(var passwordSecret):
                    ApplyListSecretsCredentials(resource, withConnectionString, construct, schema, passwordSecret);
                    break;

                case RadiusBackingConnections.RadiusCredentialMode.RecipeInputProperties:
                    await ApplyRecipeInputPropertyCredentialsAsync(
                        resource, withConnectionString, construct, referencedResourceNames).ConfigureAwait(false);
                    break;

                case RadiusBackingConnections.RadiusCredentialMode.NotProjected:
                    // Nothing to wire: the type carries no address or credential Aspire composes.
                    // TryProjectBackingEndpoint still fails loudly if a consumer asks for one.
                    break;
            }
        }
    }

    /// <summary>
    /// Wires a type whose recipe generates its own credentials and exposes them through
    /// <c>listSecrets()</c>: Aspire's parameters are substituted for the recipe's own values
    /// wherever they appear, so every composed value carries what is actually deployed.
    /// </summary>
    private void ApplyListSecretsCredentials(
        IResource resource,
        IResourceWithConnectionString withConnectionString,
        RadiusResourceTypeConstruct construct,
        RadiusBackingConnections.RadiusConnectionSchema schema,
        string passwordSecret)
    {
        var passwordParameter = TryGetCredentialParameter(withConnectionString, "password");

        if (passwordParameter is not null)
        {
            WarnIfUserSuppliedCredentialIsReplaced(resource, passwordParameter, "password");
            RegisterRecipeCredential(passwordParameter, resource, isProjectionSubstitution: true);
            _recipeSecretSubstitutions[passwordParameter] =
                new ProjectedValue(construct, passwordSecret, IsSecret: true, IsNumeric: false);
        }

        // The user name is a plain top-level property, not a listSecrets() key: the legacy
        // Applications.Datastores/mongoDatabases and Applications.Messaging/rabbitMQQueues types
        // return only connectionString and password from listSecrets(), and expose the user the
        // recipe created at properties.username.
        //
        // Known gap: this only works when the AppHost supplied a user-name *parameter*. The default
        // user names ("admin" for MongoDB, "guest" for RabbitMQ) are appended through
        // ReferenceExpressionBuilder.AppendFormatted(string?, string?), which formats immediately
        // and writes the result into the format string, so they arrive here as opaque literal text
        // with no value provider to substitute. Those connection strings keep the default user name.
        if (schema.UserNameProperty is { } userNameProperty &&
            TryGetCredentialParameter(withConnectionString, "username") is { } userNameParameter)
        {
            // Substitutions are keyed by parameter identity — that is all a value provider exposes
            // when an env var is resolved — so one parameter cannot stand for two different
            // recipe-generated values. Assigning both would silently keep only the later one and
            // hand consumers `properties.username` where they asked for the password.
            if (passwordParameter is not null && ReferenceEquals(passwordParameter, userNameParameter))
            {
                throw new InvalidOperationException(
                    $"Parameter '{userNameParameter.Name}' is used as both the user name and the password of " +
                    $"'{resource.Name}'. Its Radius recipe generates a separate value for each, and a single parameter " +
                    $"cannot be substituted for both, so consumers would receive the same value for both. Give the user " +
                    $"name and the password their own parameters. Diagnostic: ASPIRERADIUS070.");
            }

            WarnIfUserSuppliedCredentialIsReplaced(resource, userNameParameter, "user name");
            RegisterRecipeCredential(userNameParameter, resource, isProjectionSubstitution: true);
            _recipeSecretSubstitutions[userNameParameter] =
                new ProjectedValue(construct, userNameProperty, IsSecret: false, IsNumeric: false);
        }
    }

    /// <summary>
    /// Wires a type whose <c>username</c>/<c>password</c> are required schema properties on the
    /// resource: Aspire writes its own parameters there, so the deployed credentials are the ones
    /// already composed into the connection string.
    /// </summary>
    /// <remarks>
    /// The values go under <c>properties</c> directly, not under <c>properties.recipe.parameters</c>.
    /// The resource-type manifests declare <c>username</c>/<c>password</c> as <c>required</c> schema
    /// properties and the recipes read them as <c>context.resource.properties.&lt;name&gt;</c>, so a
    /// resource that only carried them as recipe parameters is rejected by schema validation before
    /// any recipe runs.
    /// See <see href="https://github.com/radius-project/resource-types-contrib/blob/main/Data/postgreSqlDatabases/postgreSqlDatabases.yaml"/>.
    /// </remarks>
    private async Task ApplyRecipeInputPropertyCredentialsAsync(
        IResource resource,
        IResourceWithConnectionString withConnectionString,
        RadiusResourceTypeConstruct construct,
        HashSet<string> referencedResourceNames)
    {
        var recipePassword = TryGetCredentialParameter(withConnectionString, "password");

        if (recipePassword is not null)
        {
            RegisterRecipeCredential(recipePassword, resource, isProjectionSubstitution: false);
            construct.SetSchemaProperty("password", GetOrAddEnvParameter(recipePassword));
        }

        // The user name has to be registered too, even though it is written straight onto the
        // resource rather than substituted. Sharing it with a resource that *does* use the
        // listSecrets() substitution is unsafe in exactly the same way as sharing the password: this
        // resource would keep the parameter's own value while the substitution rewrote every
        // consumer reference to the other resource's recipe secret. Registering it lets
        // RegisterRecipeCredential see the collision instead of letting it through.
        if (TryGetCredentialParameter(withConnectionString, "username") is { } recipeUserName)
        {
            // Both roles are written straight onto the resource here (neither is a listSecrets()
            // substitution), so RegisterRecipeCredential's same-owner check alone would not catch
            // one parameter used for both: it only rejects sharing across *different* owners. A
            // single value published for both properties is never correct, exactly as for the
            // listSecrets() types above, so reject it the same way.
            if (recipePassword is not null && ReferenceEquals(recipePassword, recipeUserName))
            {
                throw new InvalidOperationException(
                    $"Parameter '{recipeUserName.Name}' is used as both the user name and the password of " +
                    $"'{resource.Name}'. Give the user name and the password their own parameters. " +
                    $"Diagnostic: ASPIRERADIUS070.");
            }

            RegisterRecipeCredential(recipeUserName, resource, isProjectionSubstitution: false);
        }

        await SetTypePropertyAsync(construct, "username", withConnectionString, "username").ConfigureAwait(false);

        // The recipe provisions exactly one database, so it has to be told which one Aspire's
        // consumers will connect to — otherwise the connection string names a database the recipe
        // never created. Aspire models databases as child resources, and the database *name* can
        // differ from the child resource name, so read it from the child's own connection properties
        // rather than assuming they match.
        var databaseChildren = FindDatabaseChildren(resource);
        if (databaseChildren.Count == 0)
        {
            // A server with no AddDatabase(...) child is a valid and common model, so this is a
            // warning rather than a failure. But omitting the property is not safe: the server-level
            // connection string carries no database name (PostgresServerResource appends `/{db}`
            // only when a database child exists), and libpq/Npgsql then default `dbname` to the
            // *user name*, while the recipe would default its own database to `postgres_db`. For a
            // user such as `appuser` the consumer would open a database the recipe never created.
            // Emitting the user name as the database keeps the two ends in agreement — the schema
            // marks `database` optional precisely so it can be supplied here.
            await SetTypePropertyAsync(construct, "database", withConnectionString, "username").ConfigureAwait(false);

            _logger.LogWarning(
                "Radius resource '{ResourceName}' declares no database, so its recipe is asked to create a database " +
                "named after the user: the server-level connection string carries no database name and clients default " +
                "it to the user name. Add a database with AddDatabase(...) if consumers expect a specific database name.",
                resource.Name);
            return;
        }

        // Only a database a consumer actually references can produce a wrong connection string, so
        // scope the failure to those. An unreferenced extra AddDatabase(...) is inert and must not
        // break a model that published before.
        var referenced = databaseChildren.Where(d => referencedResourceNames.Contains(d.Name)).ToList();

        if (referenced.Count > 1)
        {
            // Emitting one of them would leave every consumer of the others pointed at a database
            // the recipe never created — a connection failure at run time with nothing in the
            // generated Bicep to explain it.
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has {referenced.Count} referenced databases " +
                $"('{string.Join("', '", referenced.Select(d => d.Name))}'), but its Radius recipe provisions a single " +
                $"database. Reference at most one database per resource, or split them across separate resources. " +
                $"Diagnostic: ASPIRERADIUS072.");
        }

        if (referenced.Count == 0 && databaseChildren.Count > 1)
        {
            // Annotations are the only reference signal available this early, and a WithEnvironment
            // callback that composes a database's connection string inline records none. So "no
            // referenced database" does not mean "no consumer": picking the first child here would
            // create `first` while a consumer connects to `second`. Fail instead of guessing.
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' declares {databaseChildren.Count} databases " +
                $"('{string.Join("', '", databaseChildren.Select(d => d.Name))}') and its Radius recipe provisions a " +
                $"single database, but none of them is referenced through WithReference, so Aspire cannot tell which one " +
                $"to create. Reference the database consumers connect to with WithReference, or declare one database per " +
                $"resource. Diagnostic: ASPIRERADIUS072.");
        }

        var databaseChild = referenced.Count == 1 ? referenced[0] : databaseChildren[0];

        if (databaseChildren.Count > 1)
        {
            _logger.LogWarning(
                "Radius resource '{ResourceName}' declares {Count} databases but its recipe provisions one; " +
                "'{Selected}' was passed as the 'database' property. The others are not created.",
                resource.Name,
                databaseChildren.Count,
                databaseChild.Name);
        }

        await SetTypePropertyAsync(construct, "database", databaseChild, "databasename").ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a connection property of <paramref name="source"/> and assigns it to a property on
    /// the Radius resource, recording the result so a <c>ConfigureRadiusInfrastructure</c> callback
    /// that later renames or removes a construct the value reads from can be repaired or rejected.
    /// </summary>
    private async Task SetTypePropertyAsync(
        RadiusResourceTypeConstruct construct,
        string propertyName,
        IResourceWithConnectionString source,
        string connectionPropertyKey)
    {
        if (await TryResolveConnectionPropertyAsync(source, connectionPropertyKey).ConfigureAwait(false) is not { } resolved)
        {
            return;
        }

        construct.SetSchemaProperty(propertyName, resolved.Value);

        if (resolved.Parts.Any(static p => p.Projection is not null))
        {
            _projectedTypeProperties.Add(new ProjectedTypeProperty(
                construct,
                propertyName,
                resolved.Parts,
                RenderBicepValue(resolved.Value),
                resolved.Parts.Where(static p => p.Projection is not null)
                    .Select(static p => p.Projection!.Target)
                    .Distinct()
                    .Select(static t => (t, t.BicepIdentifier))
                    .ToList()));
        }
    }

    /// <summary>
    /// The names of every resource referenced by another resource in the model, taken from the
    /// <see cref="ResourceRelationshipAnnotation"/>s that <c>WithReference</c> and the
    /// <c>WithEnvironment</c> overloads record.
    /// </summary>
    /// <remarks>
    /// Reference information is needed at step 4b, before container environment values are resolved,
    /// so it cannot be derived from the resolved values themselves. Annotations are the only signal
    /// available this early, and a reference created through a <c>WithEnvironment</c> callback that
    /// builds its value inline records none. Because of that blind spot, an empty result is treated
    /// as "unknown" rather than "unused": <see cref="ApplyRecipeInputPropertyCredentialsAsync"/>
    /// fails when several databases exist and none is annotated, instead of picking one that a
    /// callback-based consumer may not be using.
    /// </remarks>
    private HashSet<string> GetReferencedResourceNames()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in _model.Resources)
        {
            foreach (var relationship in resource.Annotations.OfType<ResourceRelationshipAnnotation>())
            {
                if (string.Equals(relationship.Type, KnownRelationshipTypes.Reference, StringComparison.OrdinalIgnoreCase))
                {
                    referenced.Add(relationship.Resource.Name);
                }
            }
        }

        return referenced;
    }

    /// <summary>
    /// Warns when a credential the AppHost supplied explicitly is discarded in favour of the value
    /// the Radius recipe generates.
    /// </summary>
    /// <remarks>
    /// A parameter Aspire generated for local run mode is meaningless at deploy time, so replacing
    /// it is invisible and correct. A parameter the AppHost author supplied is a deliberate choice,
    /// and silently ignoring it would leave them debugging a credential mismatch against a value
    /// that never reached the cluster.
    /// <para>
    /// <c>Default is GenerateParameterDefault</c> only distinguishes the two in publish mode:
    /// <c>ParameterResourceBuilderExtensions.CreateGeneratedParameter</c> rewrites <c>Default</c> to
    /// an internal user-secrets wrapper in run mode. This code only ever runs while publishing.
    /// </para>
    /// </remarks>
    private void WarnIfUserSuppliedCredentialIsReplaced(IResource resource, ParameterResource parameter, string credentialKind)
    {
        if (parameter.Default is GenerateParameterDefault)
        {
            return;
        }

        _logger.LogWarning(
            "The {CredentialKind} parameter '{ParameterName}' supplied for '{ResourceName}' is not used when deploying " +
            "to Radius. The recipe that provisions that resource generates its own credentials, and consumers are given " +
            "those instead. Remove the parameter, or provision the resource yourself if the value must be fixed.",
            credentialKind,
            parameter.Name,
            resource.Name);
    }

    /// <summary>
    /// Warns when a parameter that was substituted for a recipe-generated value is also referenced
    /// by a resource that has nothing to do with the backing resource that owns it.
    /// </summary>
    /// <remarks>
    /// The substitution rewrites the parameter <em>everywhere it appears</em>, so an unrelated
    /// <c>WithEnvironment("ADMIN_PASSWORD", sharedParameter)</c> silently receives another
    /// resource's recipe secret rather than the parameter's own value. This is checked here, where
    /// the substitution is actually applied during environment resolution, rather than by scanning
    /// <see cref="ResourceRelationshipAnnotation"/>s beforehand: a relationship-based pre-scan can't
    /// see a parameter that only shows up inside an <c>EnvironmentCallbackAnnotation</c> lambda
    /// (e.g. <c>.WithEnvironment(ctx => ctx.EnvironmentVariables["ADMIN_PASSWORD"] = shared)</c>),
    /// which records no relationship at all. Sharing between two backing resources is rejected
    /// outright by <see cref="RegisterRecipeCredential"/>; this covers the looser case, where the
    /// intent is genuinely ambiguous, so it warns instead of failing.
    /// </remarks>
    private void WarnIfUnrelatedUseOfSubstitutedParameter(ParameterResource parameter, IResource resource)
    {
        if (!_recipeCredentialOwners.TryGetValue(parameter, out var owner) ||
            ReferenceEquals(owner.Owner, resource) ||
            !_warnedUnrelatedSubstitutions.Add((resource, parameter)))
        {
            return;
        }

        _logger.LogWarning(
            "Resource '{ResourceName}' references parameter '{ParameterName}', which is also the credential of " +
            "'{OwnerName}'. That resource is provisioned by a Radius recipe which generates its own credential, " +
            "so the referencing resource receives the recipe's value rather than the parameter's. Use a separate " +
            "parameter if that is not intended. Diagnostic: ASPIRERADIUS070.",
            resource.Name,
            parameter.Name,
            owner.Owner.Name);
    }

    /// <summary>
    /// Returns the resource whose own connection value <paramref name="rawValue"/> is, or
    /// <paramref name="resource"/> when the value is the consuming resource's own.
    /// </summary>
    /// <remarks>
    /// <c>WithReference(cache)</c> splats the referenced resource's connection properties straight
    /// into the consumer's environment (<c>CACHE_PASSWORD</c>, <c>CACHE_URI</c>, ...) as plain
    /// <see cref="ReferenceExpression"/>s, with nothing in the value identifying where they came
    /// from — unlike the connection string itself, which arrives wrapped in a
    /// <see cref="ConnectionStringReference"/>. Those values legitimately contain the backing
    /// resource's credential parameter and must not be reported as unrelated uses, so they are
    /// matched back to their owner here.
    /// <para>
    /// The match is structural rather than by reference: <c>GetConnectionProperties()</c> builds a
    /// fresh <see cref="ReferenceExpression"/> — and fresh endpoint providers inside it — on every
    /// call, so nothing about the instance is stable. The manifest expression is, and two values
    /// that render to the same manifest expression are the same value by construction. Matching on
    /// the value rather than on the environment-variable name keeps this correct for an aliased
    /// reference name and for a resource that suppresses part of the injection via
    /// <c>ReferenceEnvironmentInjectionFlags</c>.
    /// </para>
    /// </remarks>
    private IResource ResolveEnvValueProvenance(object? rawValue, IResource resource)
    {
        if (_recipeSecretSubstitutions.Count == 0 || rawValue is not ReferenceExpression expression)
        {
            return resource;
        }

        var valueExpression = expression.ValueExpression;

        foreach (var (credentialOwner, _) in _recipeCredentialOwners.Values)
        {
            if (ReferenceEquals(credentialOwner, resource) ||
                credentialOwner is not IResourceWithConnectionString withConnectionString)
            {
                continue;
            }

            foreach (var (_, connectionProperty) in withConnectionString.GetConnectionProperties())
            {
                if (string.Equals(connectionProperty.ValueExpression, valueExpression, StringComparison.Ordinal))
                {
                    return credentialOwner;
                }
            }
        }

        return resource;
    }

    /// <summary>
    /// Finds the database resources parented to <paramref name="resource"/>. These are skipped by
    /// <see cref="ClassifyResources"/> (they are represented by their parent), but the parent's
    /// recipe still needs to know which database to create.
    /// </summary>
    private List<IResourceWithConnectionString> FindDatabaseChildren(IResource resource) =>
        _model.Resources
            .OfType<IResourceWithConnectionString>()
            .Where(r => r is IResourceWithParent child && ReferenceEquals(child.Parent, resource))
            .ToList();

    /// <summary>
    /// Resolves a named connection property to a Bicep value suitable for a Radius resource property,
    /// or <see langword="null"/> when the resource does not expose that property.
    /// </summary>
    /// <remarks>
    /// Goes through the same resolution the container env vars use, so a property that is a plain
    /// literal, a parameter, or a composition of both all produce the value the consumer will see.
    /// <para>
    /// Recipe <em>inputs</em> are resolved with parameter substitution disabled. A substitution
    /// rewrites an Aspire parameter to the value a recipe generates, which is the right answer for a
    /// value flowing <em>out</em> to a consumer but circular for a value flowing <em>in</em>: it
    /// would feed a resource's own output back in as its input. Because substitutions are registered
    /// as the resource loop progresses, leaving it enabled would also make the result depend on
    /// model order — a parameter shared with an earlier resource would resolve differently than one
    /// shared with a later resource.
    /// </para>
    /// </remarks>
    private async Task<(BicepValue<object> Value, List<EnvPart> Parts)?> TryResolveConnectionPropertyAsync(
        IResourceWithConnectionString resource,
        string key)
    {
        if (FindConnectionProperty(resource, key) is not { } expression)
        {
            return null;
        }

        var parts = new List<EnvPart>();
        await ResolveEnvPartsAsync(expression, resource, parts, resource, allowRecipeSubstitutions: false).ConfigureAwait(false);

        return parts.Count == 0 ? null : (new BicepValue<object>(BuildEnvBicepValue(parts).Compile()), parts);
    }

    /// <summary>
    /// Extracts the <see cref="ParameterResource"/> backing a named connection property (e.g.
    /// <c>password</c>) when that property is nothing but the parameter, so the publisher can reach
    /// a backing resource's credential without referencing the optional hosting package that
    /// defines the resource type.
    /// </summary>
    private static ParameterResource? TryGetCredentialParameter(IResourceWithConnectionString resource, string key)
    {
        if (FindConnectionProperty(resource, key) is not { } expression)
        {
            return null;
        }

        // Only a property that is *exactly* one parameter can be substituted; anything composed
        // with literals is a formatted value whose parameter cannot be swapped wholesale.
        //
        // The parameter is often wrapped in one or more pass-through ReferenceExpressions rather
        // than sitting directly in ValueProviders. PostgresServerResource, for example, exposes
        // `new("Username", ReferenceExpression.Create($"{UserNameReference}"))` where
        // `UserNameReference` is itself `ReferenceExpression.Create($"{UserNameParameter}")`, so the
        // parameter is two levels down. Unwrap those wrappers, but only while the expression adds
        // nothing of its own — a format other than "{0}" means literal text is being composed in.
        while (expression.Format == "{0}" && expression.ValueProviders is [ReferenceExpression nested])
        {
            expression = nested;
        }

        return expression.ValueProviders is [ParameterResource parameter] && expression.Format == "{0}"
            ? parameter
            : null;
    }

    private static ReferenceExpression? FindConnectionProperty(IResourceWithConnectionString resource, string key)
    {
        foreach (var property in resource.GetConnectionProperties())
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Projects an endpoint property of a backing resource onto the Radius recipe's own outputs.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> when the endpoint's resource is not a backing resource,
    /// in which case the caller falls back to normal container service discovery. Throws when the
    /// resource *is* a backing resource but cannot be addressed — emitting a wrong value here is
    /// what https://github.com/microsoft/aspire/issues/18935 is about, so this never degrades
    /// silently.
    /// </remarks>
    private bool TryProjectBackingEndpoint(
        EndpointReference endpointReference,
        EndpointProperty property,
        List<EnvPart> parts)
    {
        var resource = ResolveToParent(endpointReference.Resource);

        if (!ResourceTypeMapper.IsBackingResource(resource))
        {
            return false;
        }

        if (!_typeInstancesByResourceName.TryGetValue(resource.Name, out var construct) ||
            !_radiusTypeByResourceName.TryGetValue(resource.Name, out var radiusType))
        {
            // The resource is a backing resource but this environment did not emit it — it belongs
            // to a different Radius environment. There is no construct to project from, and the
            // recipe outputs of another environment's deployment are not reachable from this Bicep.
            throw new RadiusBackingResourceEndpointException(
                resource,
                $"Resource '{resource.Name}' is deployed by a Radius recipe in a different environment than '{_environment.Name}', " +
                $"so its address cannot be resolved here. Deploy the consumer and '{resource.Name}' to the same Radius environment. " +
                $"Diagnostic: ASPIRERADIUS069.");
        }

        if (RadiusBackingConnections.GetSchema(radiusType) is not { } schema)
        {
            throw new RadiusBackingResourceEndpointException(
                resource,
                $"Resource '{resource.Name}' maps to Radius type '{radiusType}', which does not expose an address Aspire can " +
                $"project. Remove the reference, or map the resource to a Radius type that publishes host/port outputs. " +
                $"Diagnostic: ASPIRERADIUS071.");
        }

        var scheme = endpointReference.EndpointAnnotation.UriScheme;

        switch (property)
        {
            case EndpointProperty.Host or EndpointProperty.IPV4Host:
                parts.Add(EnvPart.FromProjection(Host(construct, schema, resource, radiusType)));
                return true;
            case EndpointProperty.Port or EndpointProperty.TargetPort:
                parts.Add(EnvPart.FromProjection(Port(construct, schema, resource, radiusType)));
                return true;
            case EndpointProperty.HostAndPort:
                parts.Add(EnvPart.FromProjection(Host(construct, schema, resource, radiusType)));
                parts.Add(EnvPart.FromLiteral(":"));
                parts.Add(EnvPart.FromProjection(Port(construct, schema, resource, radiusType)));
                return true;
            case EndpointProperty.Url:
                parts.Add(EnvPart.FromLiteral($"{scheme}://"));
                parts.Add(EnvPart.FromProjection(Host(construct, schema, resource, radiusType)));
                parts.Add(EnvPart.FromLiteral(":"));
                parts.Add(EnvPart.FromProjection(Port(construct, schema, resource, radiusType)));
                return true;
            case EndpointProperty.Scheme:
                parts.Add(EnvPart.FromLiteral(scheme));
                return true;
            case EndpointProperty.TlsEnabled:
                parts.Add(EnvPart.FromLiteral(endpointReference.EndpointAnnotation.TlsEnabled ? bool.TrueString : bool.FalseString));
                return true;
            default:
                throw new RadiusBackingResourceEndpointException(
                    resource,
                    $"The endpoint property '{property}' is not supported for Radius backing resource '{resource.Name}'. " +
                    $"Diagnostic: ASPIRERADIUS071.");
        }

        static ProjectedValue Host(
            RadiusResourceTypeConstruct construct,
            RadiusBackingConnections.RadiusConnectionSchema schema,
            IResource resource,
            string radiusType) =>
            schema.HostProperty is { } hostProperty
                ? new ProjectedValue(construct, hostProperty, IsSecret: false, IsNumeric: false)
                : throw new RadiusBackingResourceEndpointException(
                    resource,
                    $"Radius type '{radiusType}' used for resource '{resource.Name}' does not publish a host output, " +
                    $"so consumers cannot be given its address. Diagnostic: ASPIRERADIUS071.");

        static ProjectedValue Port(
            RadiusResourceTypeConstruct construct,
            RadiusBackingConnections.RadiusConnectionSchema schema,
            IResource resource,
            string radiusType) =>
            schema.PortProperty is { } portProperty
                // Radius types the port output as an int, so it needs an explicit string()
                // conversion when it lands in an env var on its own.
                ? new ProjectedValue(construct, portProperty, IsSecret: false, IsNumeric: true)
                : throw new RadiusBackingResourceEndpointException(
                    resource,
                    $"Radius type '{radiusType}' used for resource '{resource.Name}' does not publish a port output, " +
                    $"so consumers cannot be given its address. Diagnostic: ASPIRERADIUS071.");
    }

    private static Dictionary<string, RadiusResourceTypeConstruct> GetConnectionTargets(
        IResource resource,
        List<IResource> radiusResources,
        Dictionary<string, RadiusResourceTypeConstruct> typeInstancesByResourceName)
    {
        var connections = new Dictionary<string, RadiusResourceTypeConstruct>(StringComparer.Ordinal);

        // Find all ResourceRelationshipAnnotation with type "Reference"
        var references = resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .Where(r => r.Type == "Reference");

        foreach (var reference in references)
        {
            var referencedResource = reference.Resource;

            // Resolve child resources (e.g., SqlServerDatabaseResource) to parent
            if (referencedResource is IResourceWithParent childResource)
            {
                referencedResource = childResource.Parent;
            }

            // Only create connections for Radius resource type instances (non-compute)
            if (radiusResources.Any(p => p.Name == referencedResource.Name)
                && typeInstancesByResourceName.TryGetValue(referencedResource.Name, out var targetConstruct))
            {
                connections[referencedResource.Name] = targetConstruct;
            }
        }

        return connections;
    }

    private static RadiusContainerConstruct CreateContainerConstruct(
        string identifier, string resourceName, string image,
        RadiusApplicationConstruct appConstruct,
        RadiusEnvironmentConstruct? envConstruct,
        Dictionary<string, RadiusResourceTypeConstruct> connectionTargets,
        IReadOnlyDictionary<string, ContainerEnvVarConstruct> env,
        IReadOnlyDictionary<string, ContainerPortConstruct> ports)
    {
        var construct = new RadiusContainerConstruct(identifier, resourceName);
        construct.ContainerName = resourceName;
        construct.Image = image;
        construct.ApplicationId = BuildIdExpression(appConstruct);
        construct.EnvironmentId = BuildIdExpression(envConstruct!);

        if (connectionTargets.Count > 0)
        {
            foreach (var (name, targetConstruct) in connectionTargets)
            {
                var connectionConstruct = new ConnectionConstruct();
                connectionConstruct.Source = BuildIdExpression(targetConstruct);
                construct.Connections[name] = connectionConstruct;
            }
        }

        foreach (var (name, envVar) in env)
        {
            construct.Env[name] = envVar;
        }

        foreach (var (name, port) in ports)
        {
            construct.Ports[name] = port;
        }

        return construct;
    }

    /// <summary>
    /// Maps a compute resource's <see cref="EndpointAnnotation"/>s to Radius container ports,
    /// keyed by endpoint name. Uses the target (container) port when specified, otherwise the
    /// allocated/declared port. Endpoints with no resolvable port are skipped.
    /// </summary>
    private static Dictionary<string, ContainerPortConstruct> ResolvePorts(IResource resource)
    {
        var ports = new Dictionary<string, ContainerPortConstruct>(StringComparer.Ordinal);
        if (!resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            return ports;
        }

        var seenPorts = new HashSet<(int ContainerPort, string Protocol)>();
        foreach (var endpoint in endpoints)
        {
            // Use the shared service-port resolver so the container port emitted here matches the
            // Service port the recipe exposes and the port the environment puts in service-discovery
            // URLs (RadiusServiceDiscovery). A null result means this endpoint contributes no port
            // (e.g. the synthetic default HTTPS endpoint), so the recipe creates no Service for it.
            if (RadiusServiceDiscovery.ResolveServicePort(resource, endpoint.Name) is not int containerPort)
            {
                continue;
            }

            var protocol = endpoint.Protocol == ProtocolType.Udp ? "UDP" : "TCP";

            // Deduplicate by (container port, protocol), matching the Kubernetes publisher's ToService
            // dedup. Multiple endpoints can resolve to the same container port (e.g. an explicit
            // portless HTTP and HTTPS endpoint on a project both default to 8080), and the recipe would
            // otherwise emit two Kubernetes Service ports with the same (port, protocol), which the
            // provider rejects. The first endpoint wins; the others still resolve to the same port in
            // their service-discovery URLs, so nothing is lost. See: https://github.com/microsoft/aspire/issues/14029
            if (!seenPorts.Add((containerPort, protocol)))
            {
                continue;
            }

            var port = new ContainerPortConstruct
            {
                ContainerPort = containerPort,
                Protocol = protocol,
            };
            ports[endpoint.Name] = port;
        }

        return ports;
    }

    /// <summary>
    /// Resolves a compute resource's environment variables into Radius container <c>env</c>
    /// entries. Mirrors the Kubernetes publisher: HTTPS service-discovery variables are dropped
    /// (no in-cluster TLS), endpoint references become cluster-FQDN URLs via the environment's
    /// <see cref="RadiusEnvironmentResource.GetHostAddressExpression"/>, and secret/parameter
    /// values are routed to Bicep <c>param</c>s so no literal secret is written to the artifact.
    /// </summary>
    private async Task<Dictionary<string, ContainerEnvVarConstruct>> ResolveEnvironmentAsync(IResource resource)
    {
        var result = new Dictionary<string, ContainerEnvVarConstruct>(StringComparer.Ordinal);
        if (resource is not IResourceWithEnvironment)
        {
            return result;
        }

        var context = new EnvironmentCallbackContext(_executionContext, resource, cancellationToken: _cancellationToken)
        {
            Logger = _logger,
        };

        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var callbacks))
        {
            foreach (var callback in callbacks)
            {
                await callback.Callback(context).ConfigureAwait(false);
            }
        }

        // Drop HTTPS service-discovery variables: containers in the cluster don't terminate TLS
        // (ingress/service mesh does), so an https `services__*` URL would be unreachable. This
        // matches RemoveHttpsServiceDiscoveryVariables in the Kubernetes/Docker Compose publishers.
        var httpsServiceKeys = context.EnvironmentVariables
            .Where(kvp => kvp.Value is EndpointReference epRef
                && epRef.Scheme == "https"
                && kvp.Key.StartsWith("services__", StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in httpsServiceKeys)
        {
            context.EnvironmentVariables.Remove(key);
        }

        foreach (var (key, rawValue) in context.EnvironmentVariables)
        {
            var parts = new List<EnvPart>();

            try
            {
                await ResolveEnvPartsAsync(rawValue, resource, parts, ResolveEnvValueProvenance(rawValue, resource)).ConfigureAwait(false);
            }
            catch (RadiusUnresolvableValueException ex)
            {
                // Only the two conditions the publisher explicitly recognises as unavailable at
                // publish time reach here — see RadiusUnresolvableValueException. This used to be
                // `catch (InvalidOperationException)`, which also swallowed genuine publish errors:
                // whether a bug surfaced depended on the exception's type rather than on the
                // publisher having decided the value was legitimately unavailable.
                //
                // Logged at Warning, not Debug: dropping a variable the container asked for is
                // observable in the deployed app, and the previous Debug level meant it never
                // appeared in a normal publish.
                _logger.LogWarning(
                    "Environment variable '{Key}' on resource '{Resource}' was omitted from the Radius output: {Reason}",
                    key, resource.Name, ex.Message);
                continue;
            }

            var envVar = new ContainerEnvVarConstruct { Value = BuildEnvBicepValue(parts) };
            result[key] = envVar;

            // Track values that name a backing resource's Bicep identifier so they can be repaired
            // (or rejected) if a ConfigureRadiusInfrastructure callback later renames or removes it.
            if (parts.Any(static p => p.Projection is not null))
            {
                _projectedEnvValues.Add(new ProjectedEnvValue(
                    envVar,
                    parts,
                    resource.Name,
                    key,
                    RenderBicepValue(envVar.Value),
                    parts.Where(p => p.Projection is not null)
                        .Select(p => p.Projection!.Target)
                        .Distinct()
                        .Select(t => (t, t.BicepIdentifier))
                        .ToList()));
            }
        }

        return result;
    }

    /// <summary>
    /// A container environment value that reads from a backing resource's Radius construct, kept so
    /// it can be re-emitted after <c>ConfigureRadiusInfrastructure</c> callbacks run.
    /// </summary>
    private sealed record ProjectedEnvValue(
        ContainerEnvVarConstruct EnvVar,
        List<EnvPart> Parts,
        string ResourceName,
        string Key,
        string? OriginalValue,
        List<(RadiusResourceTypeConstruct Target, string OriginalIdentifier)> TargetIdentifiers)
    {
        /// <summary>The container the value belongs to, attached once the construct exists.</summary>
        public RadiusContainerConstruct? Container { get; set; }
    }

    /// <summary>
    /// A Radius resource property whose value reads from a backing resource's Radius construct.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="ProjectedEnvValue"/> because the two live in different
    /// places: an env value hangs off a container construct and is a <c>BicepValue&lt;string&gt;</c>,
    /// while such a property hangs off a <see cref="RadiusResourceTypeConstruct"/> and is an
    /// already-compiled <c>BicepValue&lt;object&gt;</c>. Without this record a
    /// <c>ConfigureRadiusInfrastructure</c> callback that renames a construct would repair the
    /// container's env vars and silently leave those properties pointing at the old symbol.
    /// </remarks>
    private sealed record ProjectedTypeProperty(
        RadiusResourceTypeConstruct Owner,
        string Key,
        List<EnvPart> Parts,
        string? OriginalValue,
        List<(RadiusResourceTypeConstruct Target, string OriginalIdentifier)> TargetIdentifiers);

    /// <summary>
    /// Renders a Bicep value to a comparable string, so a value a callback overwrote can be told
    /// apart from the one the publisher generated. <c>ContainerEnvVarConstruct.Value</c> assigns
    /// into the existing <see cref="BicepValue{T}"/> rather than replacing it, so reference
    /// equality cannot detect an override.
    /// </summary>
    private static string? RenderBicepValue<T>(BicepValue<T> value) =>
        value is IBicepValue bicepValue
            ? bicepValue.Expression?.ToString() ?? bicepValue.LiteralValue?.ToString()
            : null;

    // An ordered fragment of a container env-var value: a literal string, a reference to a Bicep
    // parameter (used for secret/parameter values so the literal is never emitted), or a value
    // projected out of a backing resource's Radius construct (e.g. `cache.properties.host` or
    // `cache.listSecrets().password`).
    private readonly record struct EnvPart(
        string? Literal,
        ProvisioningParameter? Parameter,
        ProjectedValue? Projection,
        string? StringFormat = null)
    {
        public static EnvPart FromLiteral(string literal) => new(literal, null, null);
        public static EnvPart FromParameter(ProvisioningParameter parameter) => new(null, parameter, null);
        public static EnvPart FromProjection(ProjectedValue projection) => new(null, null, projection);

        /// <summary>
        /// Applies the format declared on the placeholder this part came from. A literal is escaped
        /// here and now, because its value is already known; a parameter or projection is only known
        /// at deploy time, so the escaping has to be emitted as a Bicep call instead.
        /// </summary>
        public EnvPart WithStringFormat(string stringFormat) => Literal is { } literal
            // Mirrors Aspire.Hosting's internal FormattingHelpers.FormatValue, which this assembly
            // cannot reference. Keep the two in sync if another format is ever added.
            ? this with
            {
                Literal = string.Equals(stringFormat, "uri", StringComparison.OrdinalIgnoreCase)
                    ? Uri.EscapeDataString(literal)
                    : throw new NotSupportedException(
                        $"The string format '{stringFormat}' is not supported by the Radius publisher. " +
                        $"Diagnostic: ASPIRERADIUS073."),
            }
            : this with { StringFormat = stringFormat };

        /// <summary>
        /// Wraps <paramref name="expression"/> in the Bicep equivalent of this part's format.
        /// </summary>
        public BicepExpression ApplyStringFormat(BicepExpression expression) => StringFormat?.ToLowerInvariant() switch
        {
            null => expression,
            // Aspire escapes "uri"-formatted values with Uri.EscapeDataString. Bicep's
            // uriComponent() is the closest available equivalent and is what ARM documents for
            // percent-encoding a value for use inside a URI.
            // https://learn.microsoft.com/azure/azure-resource-manager/bicep/bicep-functions-string#uricomponent
            "uri" => RadiusBackingConnections.UriComponent(expression),
            var unsupported => throw new NotSupportedException(
                $"The string format '{unsupported}' has no Bicep equivalent, so a value using it cannot be emitted " +
                $"for Radius. Diagnostic: ASPIRERADIUS073."),
        };
    }

    /// <summary>
    /// A value read off a backing resource's Radius construct.
    /// </summary>
    /// <remarks>
    /// The Bicep identifier is resolved lazily, at emit time, rather than captured as a finished
    /// expression. A <c>ConfigureRadiusInfrastructure</c> callback may rename the construct after
    /// the environment is resolved, and an eagerly-built expression would then reference a symbol
    /// that no longer exists. See <c>RebuildProjectedEnvValues</c>.
    /// </remarks>
    /// <param name="Target">The construct the value is read from.</param>
    /// <param name="Accessor">The property name, or the <c>listSecrets()</c> key when <paramref name="IsSecret"/>.</param>
    /// <param name="IsSecret">Whether the value comes from <c>listSecrets()</c> rather than <c>properties</c>.</param>
    /// <param name="IsNumeric">Whether the Radius schema types this value as a number, which needs
    /// an explicit <c>string(...)</c> conversion when it is not inside an interpolation.</param>
    private sealed record ProjectedValue(
        RadiusResourceTypeConstruct Target,
        string Accessor,
        bool IsSecret,
        bool IsNumeric)
    {
        public BicepExpression Build() => IsSecret
            ? RadiusBackingConnections.Secret(Target.BicepIdentifier, Accessor)
            : RadiusBackingConnections.Property(Target.BicepIdentifier, Accessor);
    }

    /// <summary>
    /// Recursively flattens an environment-variable value into ordered <see cref="EnvPart"/>s.
    /// Endpoint references resolve to cluster-FQDN URLs, parameter resources resolve to Bicep
    /// <c>param</c> references, and composite reference expressions are spliced together so a
    /// mixed literal/secret value is preserved precisely.
    /// </summary>
    /// <remarks>
    /// <paramref name="referencedResource"/> is the resource whose own value is currently being
    /// expanded, which is <paramref name="owner"/> until the recursion descends into a referenced
    /// resource's connection string. It only exists to tell a credential parameter reached through
    /// its owner's connection string (expected) from one the owner named directly (ambiguous) — see
    /// <see cref="WarnIfUnrelatedUseOfSubstitutedParameter"/>.
    /// </remarks>
    private async Task ResolveEnvPartsAsync(object? value, IResource owner, List<EnvPart> parts, IResource referencedResource, bool allowRecipeSubstitutions = true)
    {
        switch (value)
        {
            case null:
                return;
            case string s:
                parts.Add(EnvPart.FromLiteral(s));
                return;
            case bool b:
                parts.Add(EnvPart.FromLiteral(b ? "true" : "false"));
                return;
            case ParameterResource param:
                parts.Add(ResolveParameterPart(param, referencedResource, allowRecipeSubstitutions));
                return;
            case IResourceBuilder<ParameterResource> paramBuilder:
                parts.Add(ResolveParameterPart(paramBuilder.Resource, referencedResource, allowRecipeSubstitutions));
                return;
            case EndpointReference endpointReference:
                ThrowIfEndpointMissing(endpointReference, owner);
                if (!TryProjectBackingEndpoint(endpointReference, EndpointProperty.Url, parts))
                {
                    parts.Add(EnvPart.FromLiteral(ResolveEndpointUrl(endpointReference)));
                }
                return;
            case EndpointReferenceExpression endpointReferenceExpression:
                ThrowIfEndpointMissing(endpointReferenceExpression.Endpoint, owner);
                if (!TryProjectBackingEndpoint(
                        endpointReferenceExpression.Endpoint,
                        endpointReferenceExpression.Property,
                        parts))
                {
                    parts.Add(EnvPart.FromLiteral(ResolveEndpointProperty(endpointReferenceExpression)));
                }
                return;
            case ConnectionStringReference connectionStringReference:
                // The credential parameters inside a backing resource's own connection string are
                // exactly the ones the substitution is meant to rewrite, so the referenced resource
                // becomes the context here — otherwise every consumer of `.WithReference(cache)`
                // would be reported as an unrelated use of the cache's own password.
                await ResolveEnvPartsAsync(connectionStringReference.Resource.ConnectionStringExpression, owner, parts, connectionStringReference.Resource, allowRecipeSubstitutions).ConfigureAwait(false);
                return;
            case IResourceWithConnectionString resourceWithConnectionString:
                await ResolveEnvPartsAsync(resourceWithConnectionString.ConnectionStringExpression, owner, parts, resourceWithConnectionString, allowRecipeSubstitutions).ConfigureAwait(false);
                return;
            case ReferenceExpression referenceExpression:
                await ResolveReferenceExpressionPartsAsync(referenceExpression, owner, parts, referencedResource, allowRecipeSubstitutions).ConfigureAwait(false);
                return;
            case IFormattable formattable:
                parts.Add(EnvPart.FromLiteral(formattable.ToString(null, CultureInfo.InvariantCulture)));
                return;
            default:
                // Fall back to publish-mode resolution (e.g. manifest expression providers) and
                // capture whatever literal string the framework produces.
                if (value is IValueProvider valueProvider)
                {
                    var context = new ValueProviderContext { ExecutionContext = _executionContext, Caller = owner };

                    string? resolved;
                    try
                    {
                        resolved = await valueProvider.GetValueAsync(context, _cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex) when (value is IManifestExpressionProvider manifestExpressionProvider)
                    {
                        // Only a provider that positively declares deployment-substituted semantics
                        // may be skipped. Reaching this default branch means the value is not a
                        // parameter, endpoint, connection string, or reference expression (all
                        // handled above), so an IManifestExpressionProvider here is a placeholder
                        // another deployment fills in — an Azure Bicep output is the canonical case,
                        // and it throws "...has no value..." until the deployment that produces it
                        // has run. Radius does not run those deployments, so the value genuinely
                        // cannot be known while publishing.
                        //
                        // The marker gates the skip; the failure alone never does. An arbitrary
                        // IValueProvider may use InvalidOperationException for a genuine invalid
                        // state, and silently dropping the variable with a warning would hide a real
                        // publish bug behind an exception type.
                        //
                        // The marker alone is not sufficient either: some manifest-expression
                        // providers do resolve at publish time (Aspire.Hosting.Blazor's
                        // GatewayOriginReference wraps an endpoint, for example), so pre-emptively
                        // skipping every one of them would drop values that are available.
                        throw new RadiusUnresolvableValueException(
                            owner,
                            $"'{manifestExpressionProvider.ValueExpression}' is only known after that resource's own " +
                            $"deployment, which Radius does not perform ({ex.Message})",
                            ex);
                    }

                    parts.Add(EnvPart.FromLiteral(resolved ?? string.Empty));
                    return;
                }

                parts.Add(EnvPart.FromLiteral(value.ToString() ?? string.Empty));
                return;
        }
    }

    /// <summary>
    /// Rejects a reference to an endpoint the target resource does not declare, before any code
    /// touches <see cref="EndpointReference.EndpointAnnotation"/> (which raises a bare
    /// <see cref="InvalidOperationException"/> that would be indistinguishable from a real error).
    /// </summary>
    private static void ThrowIfEndpointMissing(EndpointReference endpointReference, IResource owner)
    {
        if (endpointReference.Exists)
        {
            return;
        }

        throw new RadiusUnresolvableValueException(
            owner,
            $"the endpoint '{endpointReference.EndpointName}' is not defined on resource " +
            $"'{endpointReference.Resource.Name}'");
    }

    /// <summary>
    /// Splices a composite <see cref="ReferenceExpression"/> into ordered parts by interleaving
    /// its literal <see cref="ReferenceExpression.Format"/> chunks with the recursively-resolved
    /// parts of each value provider (matching the <c>{0}</c>, <c>{1}</c>, ... placeholders).
    /// </summary>
    private async Task ResolveReferenceExpressionPartsAsync(ReferenceExpression expression, IResource owner, List<EnvPart> parts, IResource referencedResource, bool allowRecipeSubstitutions = true)
    {
        // A conditional expression carries no format at all and exposes the *union* of both
        // branches' providers, so the splice below would resolve both branches — potentially
        // failing the publish on the inactive one — and then append nothing, leaving the variable
        // empty. Select the branch first, matching ReferenceExpression.GetValueAsync and
        // ExpressionResolver.EvalExpressionAsync.
        if (expression.IsConditional)
        {
            var conditionContext = new ValueProviderContext { ExecutionContext = _executionContext, Caller = owner };
            var conditionValue = await expression.Condition!.GetValueAsync(conditionContext, _cancellationToken).ConfigureAwait(false);

            var branch = string.Equals(conditionValue, expression.MatchValue, StringComparison.OrdinalIgnoreCase)
                ? expression.WhenTrue!
                : expression.WhenFalse!;

            await ResolveReferenceExpressionPartsAsync(branch, owner, parts, referencedResource, allowRecipeSubstitutions).ConfigureAwait(false);
            return;
        }

        // No providers: the format string is already the literal value (after un-escaping braces).
        if (expression.ValueProviders.Count == 0)
        {
            parts.Add(EnvPart.FromLiteral(UnescapeBraces(expression.Format)));
            return;
        }

        // Pre-resolve each provider's parts so the placeholder splice is a simple lookup.
        var providerParts = new List<EnvPart>[expression.ValueProviders.Count];
        for (var i = 0; i < expression.ValueProviders.Count; i++)
        {
            var inner = new List<EnvPart>();
            await ResolveEnvPartsAsync(expression.ValueProviders[i], owner, inner, referencedResource, allowRecipeSubstitutions).ConfigureAwait(false);

            // Apply the placeholder's string format (today only "uri") to every part the provider
            // produced. Without this the emitted value contains the raw credential: Aspire's own
            // resolution escapes it via Uri.EscapeDataString, but the publisher writes a Bicep
            // expression rather than a resolved string, so the escaping has to be carried into the
            // generated Bicep instead. StringFormats can be shorter than ValueProviders (the
            // conditional-expression constructor leaves it empty), so index defensively.
            var stringFormat = i < expression.StringFormats.Count ? expression.StringFormats[i] : null;
            providerParts[i] = stringFormat is null
                ? inner
                : inner.Select(part => part.WithStringFormat(stringFormat)).ToList();
        }

        // Walk the format string, emitting literal text and substituting `{i}` placeholders.
        // Braces are escaped as `{{`/`}}` in composite expression formats.
        var format = expression.Format;
        var literal = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{')
                {
                    literal.Append('{');
                    i++;
                    continue;
                }

                var close = format.IndexOf('}', i + 1);
                var indexText = format.Substring(i + 1, close - i - 1);
                var index = int.Parse(indexText, CultureInfo.InvariantCulture);

                if (literal.Length > 0)
                {
                    parts.Add(EnvPart.FromLiteral(literal.ToString()));
                    literal.Clear();
                }

                parts.AddRange(providerParts[index]);
                i = close;
                continue;
            }

            if (c == '}' && i + 1 < format.Length && format[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }

            literal.Append(c);
        }

        if (literal.Length > 0)
        {
            parts.Add(EnvPart.FromLiteral(literal.ToString()));
        }
    }

    /// <summary>
    /// Records which backing resource a credential parameter belongs to, and rejects sharing that
    /// cannot resolve to a correct value.
    /// </summary>
    /// <remarks>
    /// Sharing one <see cref="ParameterResource"/> across resources is only safe when every owner
    /// takes the credential as a <em>schema property</em> on its own resource: the same parameter is
    /// then passed into
    /// each recipe, and every consumer reads back that same value. It is not safe once any owner
    /// uses the <c>listSecrets()</c> substitution, because that rewrites the parameter to one
    /// specific resource's recipe-generated secret everywhere it appears — the other resources'
    /// consumers would silently be handed the wrong credential.
    /// </remarks>
    private void RegisterRecipeCredential(ParameterResource parameter, IResource owner, bool isProjectionSubstitution)
    {
        if (_recipeCredentialOwners.TryGetValue(parameter, out var existing) &&
            !ReferenceEquals(existing.Owner, owner) &&
            (existing.IsProjectionSubstitution || isProjectionSubstitution))
        {
            throw new InvalidOperationException(
                $"Parameter '{parameter.Name}' is used as the credential of both '{existing.Owner.Name}' and '{owner.Name}', " +
                $"and at least one of them is provisioned by a Radius recipe that generates its own credential. The shared " +
                $"parameter would be rewritten to one resource's secret for both. Give each resource its own parameter. " +
                $"Diagnostic: ASPIRERADIUS070.");
        }

        _recipeCredentialOwners[parameter] = (owner, isProjectionSubstitution);
    }

    /// <summary>
    /// Re-emits every projected environment value whose target construct was renamed by a
    /// <c>ConfigureRadiusInfrastructure</c> callback, and fails when the target was removed.
    /// </summary>
    /// <remarks>
    /// Projected values reference a backing resource by Bicep identifier, exactly like the
    /// <c>.id</c> cross-references <see cref="RewireIdReferences"/> repairs, so they break the same
    /// way. Values are only rewritten when the identifier actually changed, preserving the
    /// last-write-wins contract for a callback that set an environment value itself.
    /// </remarks>
    private void RebuildProjectedEnvValues(RadiusInfrastructureOptions options)
    {
        var liveInstances = new HashSet<RadiusResourceTypeConstruct>(options.ResourceTypeInstances);
        var liveContainers = new HashSet<RadiusContainerConstruct>(options.Containers);

        foreach (var projected in _projectedEnvValues)
        {
            // A callback that dropped or replaced the workload, removed the variable, or set the
            // variable itself owns the result — last-write-wins. Only values still exactly as the
            // publisher generated them are ours to repair or reject.
            if (projected.Container is null ||
                !liveContainers.Contains(projected.Container) ||
                !projected.Container.Env.TryGetValue(projected.Key, out var currentEnvVar) ||
                // BicepDictionary wraps each entry, so unwrap before comparing construct identity.
                !ReferenceEquals(currentEnvVar?.Value, projected.EnvVar) ||
                !string.Equals(RenderBicepValue(projected.EnvVar.Value), projected.OriginalValue, StringComparison.Ordinal))
            {
                continue;
            }

            var changed = false;

            foreach (var (target, originalIdentifier) in projected.TargetIdentifiers)
            {
                if (!liveInstances.Contains(target))
                {
                    throw new InvalidOperationException(
                        $"Environment variable '{projected.Key}' on container '{projected.ResourceName}' reads connection " +
                        $"information from Radius resource '{target.BicepIdentifier}', but a ConfigureRadiusInfrastructure " +
                        $"callback removed or replaced that resource. Keep the resource, or set '{projected.Key}' explicitly " +
                        $"in the callback. Diagnostic: ASPIRERADIUS074.");
                }

                if (!string.Equals(target.BicepIdentifier, originalIdentifier, StringComparison.Ordinal))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                projected.EnvVar.Value = BuildEnvBicepValue(projected.Parts);
            }
        }

        RebuildProjectedTypeProperties(liveInstances);
    }

    /// <summary>
    /// The <see cref="RebuildProjectedEnvValues"/> counterpart for projected resource properties.
    /// </summary>
    private void RebuildProjectedTypeProperties(HashSet<RadiusResourceTypeConstruct> liveInstances)
    {
        foreach (var projected in _projectedTypeProperties)
        {
            // The construct that owns the parameter is gone, or the callback set the parameter
            // itself — last-write-wins, exactly as for container env values.
            if (!liveInstances.Contains(projected.Owner) ||
                projected.Owner.GetSchemaProperty(projected.Key) is not { } current ||
                !string.Equals(RenderBicepValue(current), projected.OriginalValue, StringComparison.Ordinal))
            {
                continue;
            }

            var changed = false;

            foreach (var (target, originalIdentifier) in projected.TargetIdentifiers)
            {
                if (!liveInstances.Contains(target))
                {
                    throw new InvalidOperationException(
                        $"Recipe parameter '{projected.Key}' on Radius resource '{projected.Owner.BicepIdentifier}' reads " +
                        $"connection information from Radius resource '{target.BicepIdentifier}', but a " +
                        $"ConfigureRadiusInfrastructure callback removed or replaced that resource. Keep the resource, or " +
                        $"set '{projected.Key}' explicitly in the callback. Diagnostic: ASPIRERADIUS074.");
                }

                if (!string.Equals(target.BicepIdentifier, originalIdentifier, StringComparison.Ordinal))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                projected.Owner.SetSchemaProperty(
                    projected.Key,
                    new BicepValue<object>(BuildEnvBicepValue(projected.Parts).Compile()));
            }
        }
    }

    private static string UnescapeBraces(string format) =>
        format.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);

    // A backing resource's password is generated by its Radius recipe, not by Aspire, so the
    // Aspire parameter is replaced by the recipe's own secret accessor wherever it is referenced.
    // Everything composed from it (connection string, URI, splatted *_PASSWORD) then carries the
    // deployed value. Parameters that are not a recipe credential keep the normal `param` routing.
    private EnvPart ResolveParameterPart(ParameterResource parameter, IResource owner, bool allowRecipeSubstitutions)
    {
        if (allowRecipeSubstitutions && _recipeSecretSubstitutions.TryGetValue(parameter, out var secretProjection))
        {
            WarnIfUnrelatedUseOfSubstitutedParameter(parameter, owner);
            return EnvPart.FromProjection(secretProjection);
        }

        return EnvPart.FromParameter(GetOrAddEnvParameter(parameter));
    }

    // Allocates (or reuses) the Bicep parameter that carries this Aspire parameter's value. The
    // parameter is declared `@secure()` when the source is a secret so its value is neither printed
    // in deploy logs nor written to the artifact. The identifier→resource mapping is recorded for
    // the deploy step, which supplies the actual value via `rad deploy --parameters`.
    private ProvisioningParameter GetOrAddEnvParameter(ParameterResource parameter)
    {
        if (_envParametersByName.TryGetValue(parameter.Name, out var existing))
        {
            return existing;
        }

        var identifier = Infrastructure.NormalizeBicepIdentifier(parameter.Name);

        // A recipe parameter / inline secret may already have allocated a secure `param` for this
        // same Aspire parameter — recipe-pack and secret-store emission both run before container
        // env-var resolution. Reuse that declaration (it is emitted via options.RecipeParameters)
        // so the shared value produces a single Bicep `param` and one deploy binding rather than a
        // duplicate declaration. Keyed on the exact Aspire parameter name (unique in the app model)
        // so two *distinct* parameters whose names normalize to the same identifier are NOT merged
        // here — they fall through and surface as a genuine identifier collision (ASPIRERADIUS056).
        // Not cached in _envParametersByName so it is not emitted twice.
        if (_recipeParameters.TryGetValue(parameter.Name, out var recipeParameter))
        {
            return recipeParameter;
        }

        var provisioningParameter = new ProvisioningParameter(identifier, typeof(string))
        {
            IsSecure = parameter.Secret,
        };

        _envParametersByName[parameter.Name] = provisioningParameter;
        _deployParametersByIdentifier[identifier] = parameter;
        return provisioningParameter;
    }

    private static BicepValue<string> BuildEnvBicepValue(List<EnvPart> parts)
    {
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        // All-literal value: concatenate directly (also covers the common single-literal case).
        if (parts.All(static p => p.Parameter is null && p.Projection is null))
        {
            return string.Concat(parts.Select(static p => p.Literal));
        }

        // A single parameter with no surrounding literals maps straight to the `param` reference,
        // emitting `value: paramName` rather than an interpolated string. A formatted parameter has
        // to go through the expression path instead, so the escaping call is emitted around it.
        if (parts is [{ Literal: null, StringFormat: null, Parameter: { } soleParameter }])
        {
            return soleParameter;
        }

        // Likewise a lone Bicep expression is emitted bare (`value: cache.properties.host`) rather
        // than wrapped in a single-placeholder interpolation.
        if (parts is [{ Literal: null, Parameter: null, Projection: { } soleProjection } soleProjectionPart])
        {
            var expression = soleProjection.Build();
            return new BicepValue<string>(
                soleProjectionPart.ApplyStringFormat(
                    soleProjection.IsNumeric ? RadiusBackingConnections.ToStringExpression(expression) : expression));
        }

        // Mixed literal/parameter value: build an interpolated Bicep string ('...${param}...').
        // Literals are passed as interpolation arguments (not spliced into the format) so any '{'
        // or '}' they contain can't be misread as a placeholder.
        var format = new StringBuilder();
        var args = new object[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            format.Append('{').Append(i.ToString(CultureInfo.InvariantCulture)).Append('}');
            args[i] = parts[i] switch
            {
                // A formatted parameter cannot be passed as the ProvisioningParameter itself: the
                // escaping call has to wrap the identifier, so hand the interpolation an expression.
                { Parameter: { } parameter, StringFormat: not null } part =>
                    part.ApplyStringFormat(new IdentifierExpression(parameter.BicepIdentifier)),
                { Parameter: { } parameter } => parameter,
                // A formatted numeric projection (e.g. a `:uri`-formatted port) has to be converted
                // to a string before the format is applied: `uriComponent()` requires a string
                // argument, and Bicep type-checks that eagerly rather than coercing it implicitly
                // the way string interpolation does for an unformatted numeric projection.
                { Projection: { IsNumeric: true } projection, StringFormat: not null } part =>
                    part.ApplyStringFormat(RadiusBackingConnections.ToStringExpression(projection.Build())),
                { Projection: { } projection } part => part.ApplyStringFormat(projection.Build()),
                var part => part.Literal!,
            };
        }

        return BicepFunction.Interpolate(FormattableStringFactory.Create(format.ToString(), args));
    }

    /// <summary>
    /// Resolves an <see cref="EndpointReference"/> to a cluster-FQDN URL (<c>scheme://host:port</c>)
    /// using the environment's <see cref="RadiusEnvironmentResource.GetHostAddressExpression"/> so
    /// the namespace-qualified service name is used.
    /// </summary>
    private string ResolveEndpointUrl(EndpointReference endpointReference) =>
        ResolveHostExpression(((IComputeEnvironmentResource)_environment).GetEndpointPropertyExpression(endpointReference.Property(EndpointProperty.Url)));

    private string ResolveEndpointProperty(EndpointReferenceExpression endpointReferenceExpression) =>
        ResolveHostExpression(((IComputeEnvironmentResource)_environment).GetEndpointPropertyExpression(endpointReferenceExpression));

    /// <summary>
    /// Resolves a <see cref="ReferenceExpression"/> produced by the environment's endpoint
    /// helpers to a literal string. The host address is a literal cluster FQDN, so the whole
    /// expression resolves synchronously without needing the run-mode value pipeline.
    /// </summary>
    private static string ResolveHostExpression(ReferenceExpression expression) =>
        expression.GetValueAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult() ?? string.Empty;

    /// <summary>
    /// Warns when a container image may not pull correctly without <c>imagePullPolicy</c>.
    /// The container v2 schema removes <c>imagePullPolicy</c>, so users of kind clusters or
    /// local images need to ensure images are pre-loaded and use explicit tags.
    /// </summary>
    private void WarnIfImageMayNotPull(string resourceName, string image)
    {
        if (image.EndsWith(":latest", StringComparison.Ordinal) || !image.Contains(':'))
        {
            _logger.LogWarning(
                "Resource '{ResourceName}' uses image '{Image}' which may default to 'Always' pull policy " +
                "in Kubernetes. The Radius container v2 schema no longer supports imagePullPolicy. " +
                "For kind clusters, pre-load images with 'kind load docker-image' and use explicit tags.",
                resourceName, image);
        }

        if (!image.Contains('/'))
        {
            _logger.LogWarning(
                "Resource '{ResourceName}' uses image '{Image}' without a registry prefix. " +
                "Ensure the image is available in the target cluster (e.g., pre-loaded via 'kind load docker-image').",
                resourceName, image);
        }
    }

    private void RunConfigureCallbacks(RadiusInfrastructureOptions options)
    {
        var callbacks = _environment.Annotations
            .OfType<RadiusInfrastructureConfigureAnnotation>()
            .ToArray();

        foreach (var callback in callbacks)
        {
            callback.Configure(options);
        }
    }

    // A Kubernetes Service name must be a valid RFC 1123 DNS label of at most 63 characters:
    // https://kubernetes.io/docs/concepts/overview/working-with-objects/names/#dns-label-names
    // The Radius recipe names the Service `{resource}-{resource}` (RadiusServiceDiscovery), so a
    // resource name longer than 31 characters overflows the limit even though Aspire itself allows
    // names up to ModelName.DefaultMaxLength (64).
    private const int MaxKubernetesServiceNameLength = 63;

    // Validates the final (post-callback) container set. Aspire emits service discovery
    // (`services__*` URLs and the recipe Service name/port) from the pre-callback model, so a
    // ConfigureRadiusInfrastructure callback that renames a container, changes/removes a port,
    // adds a port to a previously portless container, or replaces/drops a workload can silently
    // break cross-container calls or emit an invalid manifest. Fail fast on any detectable
    // divergence. Only literal values are validated; a non-literal (Bicep-expression) name or port
    // cannot be reconciled with the fixed literal service-discovery value, so it is rejected too.
    private static void ValidatePostCallbackContainerInvariants(
        RadiusInfrastructureOptions options,
        IReadOnlyDictionary<string, Dictionary<string, (int Port, string Protocol)>> portSnapshots)
    {
        // Index the final containers by their immutable map key (the resource name, fixed at
        // construction). Keying by the map key rather than the construct instance means a callback
        // that swapped in a new construct for the same workload is still matched to its baseline.
        var containersByMapKey = new Dictionary<string, RadiusContainerConstruct>(StringComparer.Ordinal);
        foreach (var container in options.Containers)
        {
            containersByMapKey[container.ContainerMapKey] = container;
        }

        foreach (var (mapKey, snapshot) in portSnapshots)
        {
            // A portless container has no Service and no `services__*` value can address it, so
            // removing it in a callback is harmless — skip the preservation check for empty
            // snapshots so the invariant does not needlessly reject valid customization callbacks.
            if (snapshot.Count == 0)
            {
                continue;
            }

            // The workload service discovery was emitted for must still be present under the same
            // map key. A callback that removed it — or replaced it with a differently keyed
            // container — leaves consumers pointing at a Service that is no longer produced.
            if (!containersByMapKey.TryGetValue(mapKey, out var container))
            {
                throw new InvalidOperationException(
                    $"A ConfigureRadiusInfrastructure callback removed or replaced container '{mapKey}'. Aspire " +
                    $"service discovery already emitted 'services__*' variables that address it, so dropping the " +
                    $"workload would break cross-container calls. Keep the container to keep service discovery consistent.");
            }

            // Only containers that had service ports pre-callback have a Service (`{name}-{name}`)
            // that `services__*` addresses, so the name/map-key equality is only required for them.
            // A portless baseline container or one added entirely by the callback has no service-
            // discovery contract — Radius permits its top-level name to differ from the map key — so
            // gating this check on a non-empty snapshot keeps the customization escape hatch open.
            ValidateContainerNameMatchesMapKey(container);

            foreach (var (portName, expected) in snapshot)
            {
                if (!container.Ports.TryGetValue(portName, out var portValue) || portValue.Value is not { } port)
                {
                    throw new InvalidOperationException(
                        $"A ConfigureRadiusInfrastructure callback removed port '{portName}' from container " +
                        $"'{mapKey}'. Aspire service discovery already emitted this port ({expected.Port}) into " +
                        $"consumer 'services__*' variables, so removing it would break cross-container calls. " +
                        $"Remove the port change to keep service discovery consistent.");
                }

                // Reject a non-literal port/protocol: service discovery is a fixed literal, so a
                // callback that swaps in a Bicep expression could evaluate to a different value at
                // deploy time, reintroducing exactly the mismatch this guard prevents. An
                // expression-backed BicepValue<int> reports a default LiteralValue of 0 (not null),
                // so a non-null Expression is the reliable "non-literal" signal, not the LiteralValue.
                var portValueBicep = (IBicepValue)port.ContainerPort;
                if (portValueBicep.Expression is not null || portValueBicep.LiteralValue is not int literalPort)
                {
                    throw new InvalidOperationException(
                        $"A ConfigureRadiusInfrastructure callback replaced port '{portName}' on container " +
                        $"'{mapKey}' with a non-literal Bicep expression. Aspire service discovery already emitted " +
                        $"the literal port {expected.Port} into consumer 'services__*' variables and cannot follow a " +
                        $"computed port, so a computed containerPort is not supported. Remove the port change.");
                }

                var protocolValueBicep = (IBicepValue)port.Protocol;
                if (protocolValueBicep.Expression is not null || protocolValueBicep.LiteralValue is not string literalProtocol)
                {
                    throw new InvalidOperationException(
                        $"A ConfigureRadiusInfrastructure callback replaced the protocol of port '{portName}' on " +
                        $"container '{mapKey}' with a non-literal Bicep expression. Aspire service discovery assumes " +
                        $"the literal protocol '{expected.Protocol}', so a computed protocol is not supported. Remove " +
                        $"the protocol change.");
                }

                if (literalPort != expected.Port || !string.Equals(literalProtocol, expected.Protocol, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"A ConfigureRadiusInfrastructure callback changed port '{portName}' on container " +
                        $"'{mapKey}' from {expected.Port}/{expected.Protocol} to {literalPort}/{literalProtocol}. " +
                        $"Aspire service discovery already emitted {expected.Port}/{expected.Protocol} into consumer " +
                        $"'services__*' variables, so this would break cross-container calls. Remove the port change.");
                }
            }
        }

        // Validate the FINAL container set. A callback can add the first port to a previously
        // portless container, add a new container, or add a second endpoint. Once a container
        // declares ports the recipe creates a Service, so re-check the two things the recipe cares
        // about on the post-callback state: the Service name fits the Kubernetes limit, and the
        // container's ports are unique by (containerPort, protocol).
        foreach (var container in options.Containers)
        {
            if (container.Ports.Count == 0)
            {
                continue;
            }

            ValidateServiceNameWithinKubernetesLimit(container);

            // The pre-callback `seenPorts` dedup in ResolvePorts only covers the baseline ports. A
            // callback can add a second endpoint (e.g. `http2`) that resolves to the same
            // (containerPort, protocol) as a preserved one, which would make the recipe emit
            // duplicate Kubernetes Service ports — exactly what the baseline dedup prevents. Re-run
            // the dedup on the FINAL literal ports so a callback can't reintroduce the collision.
            var seenPorts = new HashSet<(int ContainerPort, string Protocol)>();
            foreach (var (portName, portValue) in container.Ports)
            {
                if (portValue.Value is not { } port)
                {
                    continue;
                }

                // Only literal ports can collide deterministically; non-literal (expression-backed)
                // ports on callback-added containers are the customization's own responsibility and
                // can't be compared here.
                if (((IBicepValue)port.ContainerPort).LiteralValue is not int literalPort ||
                    ((IBicepValue)port.Protocol).LiteralValue is not string literalProtocol)
                {
                    continue;
                }

                if (!seenPorts.Add((literalPort, literalProtocol)))
                {
                    throw new InvalidOperationException(
                        $"A ConfigureRadiusInfrastructure callback left container '{container.ContainerMapKey}' with " +
                        $"more than one port on {literalPort}/{literalProtocol} (for example port '{portName}'). The " +
                        $"Radius container recipe creates one Kubernetes Service port per declared port, so duplicate " +
                        $"(containerPort, protocol) pairs would emit conflicting Service ports. Remove the duplicate port.");
                }
            }
        }
    }

    // Ensures a container's top-level `name:` still equals its `properties.containers` map key. The
    // default name is a literal (the resource name); a callback that changes it to a mismatched
    // literal, or replaces it with a non-literal Bicep expression we cannot compare, throws.
    //
    // NOTE: this is an *Aspire* service-discovery limitation, not a Radius v2 schema requirement.
    // Radius itself permits a container resource whose map keys (e.g. `frontend`, `sidecar`) differ
    // from the top-level name; Aspire derives `services__*` values from the original resource name,
    // so a rename would make the emitted address diverge from the deployed Service.
    private static void ValidateContainerNameMatchesMapKey(RadiusContainerConstruct container)
    {
        var name = (IBicepValue)container.ContainerName;

        // An expression-backed BicepValue reports a default LiteralValue (null for string), but to
        // stay consistent with the port guard we treat any non-null Expression as the non-literal
        // signal.
        if (name.Expression is not null || name.LiteralValue is not string literalName)
        {
            throw new InvalidOperationException(
                $"A ConfigureRadiusInfrastructure callback replaced container '{container.ContainerMapKey}' name " +
                $"with a non-literal Bicep expression. The Aspire Radius publisher derives service discovery from " +
                $"the original container name, so it must stay the literal resource name '{container.ContainerMapKey}' " +
                $"(this is an Aspire limitation, not a Radius schema requirement). Remove the rename to keep the " +
                $"emitted 'services__*' values addressing the deployed Service.");
        }

        if (!string.Equals(literalName, container.ContainerMapKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A ConfigureRadiusInfrastructure callback renamed container '{container.ContainerMapKey}' to " +
                $"'{literalName}'. The Aspire Radius publisher derives service discovery from the original container " +
                $"name, so renaming it makes the emitted 'services__*' values point at a Service that is no longer " +
                $"produced (this is an Aspire limitation, not a Radius schema requirement). Remove the rename to keep " +
                $"cross-container calls working.");
        }
    }

    private static void ValidateServiceNameWithinKubernetesLimit(RadiusContainerConstruct container)
    {
        // The recipe names the Service `${normalizedName}-${containerName}` = `{top-level name}-
        // {map key}`. For a baseline container the name-equality guard forces name == map key, so
        // this is `{name}-{name}`; for a callback-added/portless container the name may legitimately
        // differ, so compute the actual Service name from the literal top-level name when available.
        var mapKey = container.ContainerMapKey;
        var topLevelName = ((IBicepValue)container.ContainerName).LiteralValue is string literalName ? literalName : mapKey;
        var serviceName = RadiusServiceDiscovery.GetServiceName(topLevelName, mapKey);
        if (serviceName.Length > MaxKubernetesServiceNameLength)
        {
            throw new InvalidOperationException(
                $"The Radius container recipe creates a Kubernetes Service named '{serviceName}' for resource " +
                $"'{mapKey}', but that is {serviceName.Length} characters — longer than the " +
                $"{MaxKubernetesServiceNameLength}-character limit for a Kubernetes Service name (an RFC 1123 DNS " +
                $"label). Shorten the resource name to at most {(MaxKubernetesServiceNameLength - 1) / 2} characters " +
                $"so the doubled '{{name}}-{{name}}' Service name stays within the limit.");
        }
    }

    internal readonly record struct RecipeEntry(string RecipeKind, string RecipeLocation);

    // ---------------------------------------------------------------------------------------------
    // Recipe parameters (WithRecipeParameters) — environment-wide + resource-type-scoped values
    // flowed onto the shared recipe pack. ParameterResource-backed values are emitted as valueless
    // (secure when the source is secret) Bicep `param`s so no literal secret lands in the artifact.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Computes the effective recipe parameter set for a resource type by merging the
    /// environment-wide parameters with any parameters scoped to that resource type.
    /// Resource-type-scoped values win on key collision. Returns <see langword="null"/> when no
    /// parameters apply.
    /// </summary>
    private IReadOnlyDictionary<string, object>? GetEffectiveRecipeParameters(string resourceType)
    {
        var annotation = _environment.Annotations
            .OfType<Annotations.RadiusRecipeParametersAnnotation>()
            .FirstOrDefault();
        if (annotation is null)
        {
            return null;
        }

        var effective = new Dictionary<string, object>(annotation.EnvironmentWide, StringComparer.Ordinal);

        if (annotation.ByResourceType.TryGetValue(resourceType, out var scoped))
        {
            foreach (var (key, value) in scoped)
            {
                if (effective.ContainsKey(key))
                {
                    _logger.LogDebug(
                        "Recipe parameter '{Key}' scoped to resource type '{ResourceType}' overrides the environment-wide value.",
                        key, resourceType);
                }

                effective[key] = value;
            }
        }

        return effective.Count == 0 ? null : effective;
    }

    /// <summary>
    /// Serializes each effective recipe parameter into <paramref name="target"/>, preserving Bicep
    /// type fidelity and emitting parameter references for bound <see cref="ParameterResource"/>
    /// values and provider references.
    /// </summary>
    private void ApplyRecipeParameters(BicepDictionary<object> target, IReadOnlyDictionary<string, object> parameters)
    {
        foreach (var (key, value) in parameters)
        {
            target[key] = ConvertRecipeParameterValue(value);
        }
    }

    /// <summary>
    /// Converts a single recipe parameter value to a Bicep value. Handles
    /// <see cref="ParameterResource"/> bindings (emitted as a Bicep <c>param</c> reference, never a
    /// resolved secret), provider-scope references, and literal/array/object values.
    /// </summary>
    private BicepValue<object> ConvertRecipeParameterValue(object? value) =>
        ConvertRecipeParameterValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);

    private BicepValue<object> ConvertRecipeParameterValue(object? value, HashSet<object> visited, int depth)
    {
        if (depth > MaxRecipeParameterNestingDepth)
        {
            throw new NotSupportedException(
                $"Recipe parameter values cannot be nested deeper than {MaxRecipeParameterNestingDepth} levels.");
        }

        switch (value)
        {
            case null:
                return new BicepValue<object>(new NullLiteralExpression());
            case BicepValue<object> bicepValue:
                return bicepValue;
            case IBicepValue alreadyBicep:
                return new BicepValue<object>(alreadyBicep);
            case BicepExpression expression:
                return new BicepValue<object>(expression);
            case IResourceBuilder<ParameterResource> parameterBuilder:
                return ParameterReference(GetOrAddRecipeParameter(parameterBuilder.Resource));
            case ParameterResource parameterResource:
                return ParameterReference(GetOrAddRecipeParameter(parameterResource));
            case RadiusProviderReference providerReference:
                return ToRecipeBicepValue(ResolveProviderReference(providerReference));
            case System.Collections.IDictionary dictionary:
                return ConvertRecipeParameterObject(dictionary, visited, depth);
            case string or int or long or bool or double or float or decimal:
                return ToRecipeBicepValue(value);
            case System.Collections.IEnumerable sequence:
                return ConvertRecipeParameterArray(sequence, visited, depth);
            default:
                return ToRecipeBicepValue(value);
        }
    }

    private BicepValue<object> ConvertRecipeParameterObject(
        System.Collections.IDictionary dictionary,
        HashSet<object> visited,
        int depth)
    {
        if (!visited.Add(dictionary))
        {
            throw new NotSupportedException("Recipe parameter values cannot contain cycles.");
        }

        try
        {
            var result = new BicepDictionary<object>();
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new NotSupportedException(
                        $"Recipe parameter object keys must be strings, but found '{entry.Key?.GetType().Name ?? "null"}'.");
                }

                result[key] = ConvertRecipeParameterValue(entry.Value, visited, depth + 1);
            }

            return new BicepValue<object>(result);
        }
        finally
        {
            visited.Remove(dictionary);
        }
    }

    private BicepValue<object> ConvertRecipeParameterArray(
        System.Collections.IEnumerable sequence,
        HashSet<object> visited,
        int depth)
    {
        if (!visited.Add(sequence))
        {
            throw new NotSupportedException("Recipe parameter values cannot contain cycles.");
        }

        try
        {
            var result = new BicepList<object>();
            foreach (var element in sequence)
            {
                result.Add(ConvertRecipeParameterValue(element, visited, depth + 1));
            }

            return new BicepValue<object>(result);
        }
        finally
        {
            visited.Remove(sequence);
        }
    }

    private static BicepValue<object> ToRecipeBicepValue(object value)
    {
        return BicepPostProcessor.ToBicepValue(value) switch
        {
            BicepValue<object> bicepValue => bicepValue,
            var nestedValue => new BicepValue<object>(nestedValue)
        };
    }

    /// <summary>
    /// Wraps a Bicep <c>param</c> declaration as a value usable inside a recipe <c>parameters</c>
    /// object (a reference to the parameter identifier).
    /// </summary>
    private static BicepValue<object> ParameterReference(ProvisioningParameter parameter)
    {
        BicepValue<object> reference = parameter;
        return reference;
    }

    /// <summary>
    /// Returns (creating once) the Bicep <c>param</c> declaration for an Aspire
    /// <see cref="ParameterResource"/>. Secret parameters are declared secure so no value is
    /// written to the published artifact.
    /// </summary>
    private ProvisioningParameter GetOrAddRecipeParameter(ParameterResource parameter)
    {
        if (!_recipeParameters.TryGetValue(parameter.Name, out var provisioningParameter))
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(parameter.Name);

            // Two distinct parameter names can sanitize to the same Bicep identifier (e.g.
            // "my-key" and "my.key" both become "my_key"). Emitting two `param my_key`
            // declarations produces invalid Bicep, so fail with an actionable diagnostic
            // (ASPIRERADIUS028) instead.
            if (_recipeParameterIdentifiers.TryGetValue(identifier, out var existingName))
            {
                throw new InvalidOperationException(
                    $"Recipe parameters bound to Aspire parameters '{existingName}' and '{parameter.Name}' both " +
                    $"map to the Bicep identifier '{identifier}'. Rename one of the parameters so they produce " +
                    "distinct Bicep identifiers. Diagnostic: ASPIRERADIUS028.");
            }

            provisioningParameter = new ProvisioningParameter(identifier, typeof(string))
            {
                IsSecure = parameter.Secret,
            };
            _recipeParameters[parameter.Name] = provisioningParameter;
            _recipeParameterIdentifiers[identifier] = parameter.Name;
            // Remember the originating ParameterResource keyed by the Bicep identifier so the
            // deploy step can pass `--parameters <identifier>=<value>` for this valueless param.
            _recipeParameterBindings[identifier] = parameter;
        }

        return provisioningParameter;
    }

    /// <summary>
    /// Resolves a <see cref="RadiusProviderReference"/> to the corresponding scope value from the
    /// cloud provider configured on this environment. Throws when the referenced provider is not
    /// configured.
    /// </summary>
    private string ResolveProviderReference(RadiusProviderReference reference)
    {
        var providers = _environment.Annotations
            .OfType<Annotations.RadiusCloudProvidersAnnotation>()
            .FirstOrDefault();

        return reference.Field switch
        {
            RadiusProviderScopeField.Region =>
                providers?.Aws?.Region ?? throw MissingProviderReference("AWS", "WithAwsProvider"),
            RadiusProviderScopeField.AccountId =>
                providers?.Aws?.AccountId ?? throw MissingProviderReference("AWS", "WithAwsProvider"),
            RadiusProviderScopeField.SubscriptionId =>
                providers?.Azure?.SubscriptionId ?? throw MissingProviderReference("Azure", "WithAzureProvider"),
            RadiusProviderScopeField.ResourceGroup =>
                providers?.Azure?.ResourceGroup ?? throw MissingProviderReference("Azure", "WithAzureProvider"),
            _ => throw new NotSupportedException($"Unknown provider scope field '{reference.Field}'."),
        };
    }

    private InvalidOperationException MissingProviderReference(string cloud, string configureMethod) =>
        new($"A recipe parameter on Radius environment '{_environment.Name}' references {cloud} provider " +
            $"configuration, but no {cloud} provider is configured. Call {configureMethod}(...) on the environment.");

    /// <summary>
    /// Emits a non-fatal warning for each resource-type-scoped parameter set whose resource type
    /// has no recipe entry in the emitted recipe pack.
    /// </summary>
    private void WarnUnmatchedResourceTypeScopes(IEnumerable<string> emittedResourceTypes)
    {
        var annotation = _environment.Annotations
            .OfType<Annotations.RadiusRecipeParametersAnnotation>()
            .FirstOrDefault();
        if (annotation is null)
        {
            return;
        }

        var emitted = new HashSet<string>(emittedResourceTypes, StringComparer.Ordinal);
        foreach (var resourceType in annotation.ByResourceType.Keys)
        {
            if (!emitted.Contains(resourceType))
            {
                _logger.LogWarning(
                    "Recipe parameters were scoped to resource type '{ResourceType}' on Radius environment " +
                    "'{Environment}', but no recipe entry of that type exists in the emitted recipe pack; " +
                    "those parameters were ignored.",
                    resourceType, _environment.Name);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Secret stores (AddRadiusSecretStore / WithSecretStore) — emitted as Applications.Core/
    // secretStores scoped to the legacy environment/application, plus recipeConfig consumers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the Radius secret stores routed to this environment: environment-scoped stores owned
    /// by this environment, plus all application-scoped stores.
    /// </summary>
    private IEnumerable<RadiusSecretStoreResource> GetSecretStoresForScope()
    {
        return _model.Resources.OfType<RadiusSecretStoreResource>().Where(s =>
            (s.Scope == RadiusSecretStoreScope.Environment && ReferenceEquals(s.OwningEnvironment, _environment))
            || s.Scope == RadiusSecretStoreScope.Application);
    }

    /// <summary>
    /// Emits one <see cref="RadiusSecretStoreConstruct"/> per declared store, scoped to the legacy
    /// Applications.Core environment/application (secret stores are Applications.Core resources) and
    /// populated per mode (inline / existing / sealed).
    /// </summary>
    private Dictionary<string, RadiusSecretStoreConstruct> EmitSecretStores(
        RadiusInfrastructureOptions options,
        IReadOnlyList<RadiusSecretStoreResource> stores,
        LegacyApplicationEnvironmentConstruct? legacyEnvConstruct,
        LegacyApplicationConstruct? legacyAppConstruct)
    {
        var storeConstructs = new Dictionary<string, RadiusSecretStoreConstruct>(StringComparer.Ordinal);

        foreach (var store in stores)
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(store.Name);
            var construct = new RadiusSecretStoreConstruct(identifier)
            {
                StoreName = store.Name,
                StoreType = store.Type.ToRadiusTypeString(),
            };

            // Scope is implied by the declaring API form: application-scoped stores reference the
            // application; environment-scoped stores reference the environment.
            if (store.Scope == RadiusSecretStoreScope.Application && legacyAppConstruct is not null)
            {
                construct.ApplicationId = BuildIdExpression(legacyAppConstruct);
            }
            else if (legacyEnvConstruct is not null)
            {
                construct.EnvironmentId = BuildIdExpression(legacyEnvConstruct);
            }

            PopulateInlineSecretStoreData(store, construct);
            PopulateSecretReferenceData(store, construct, options);

            storeConstructs[store.Name] = construct;
            options.SecretStores.Add(construct);
        }

        ApplySecretStoreConsumers(legacyEnvConstruct, storeConstructs);

        return storeConstructs;
    }

    /// <summary>
    /// Emits the environment's <c>recipeConfig</c> from the recorded secret-store consumers
    /// (private Bicep-registry auth, Terraform Git PAT auth, and <c>envSecrets</c>), referencing
    /// each store by its <c>.id</c>.
    /// </summary>
    private void ApplySecretStoreConsumers(
        LegacyApplicationEnvironmentConstruct? legacyEnvConstruct,
        IReadOnlyDictionary<string, RadiusSecretStoreConstruct> storeConstructs)
    {
        var annotation = _environment.Annotations
            .OfType<Annotations.RadiusSecretStoresAnnotation>()
            .FirstOrDefault();
        if (legacyEnvConstruct is null || annotation is null || annotation.Consumers.Count == 0)
        {
            return;
        }

        var bicepAuth = new Dictionary<string, object>(StringComparer.Ordinal);
        var gitPat = new Dictionary<string, object>(StringComparer.Ordinal);
        var envSecrets = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var consumer in annotation.Consumers)
        {
            var secretRef = ResolveSecretStoreReference(consumer.Store, storeConstructs);
            switch (consumer.Kind)
            {
                case RadiusSecretStoreConsumerKind.BicepRegistryAuth:
                    bicepAuth[consumer.Selector!] = new Dictionary<string, object> { ["secret"] = secretRef };
                    break;
                case RadiusSecretStoreConsumerKind.TerraformGitPat:
                    gitPat[consumer.Selector!] = new Dictionary<string, object> { ["secret"] = secretRef };
                    break;
                case RadiusSecretStoreConsumerKind.EnvSecret:
                    envSecrets[consumer.Selector!] = new Dictionary<string, object>
                    {
                        ["source"] = secretRef,
                        ["key"] = consumer.Key!,
                    };
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown secret-store consumer kind '{consumer.Kind}' for store '{consumer.Store.Name}'.");
            }
        }

        var recipeConfig = new Dictionary<string, object>(StringComparer.Ordinal);
        if (bicepAuth.Count > 0)
        {
            recipeConfig["bicep"] = new Dictionary<string, object> { ["authentication"] = bicepAuth };
        }

        if (gitPat.Count > 0)
        {
            recipeConfig["terraform"] = new Dictionary<string, object>
            {
                ["authentication"] = new Dictionary<string, object>
                {
                    ["git"] = new Dictionary<string, object> { ["pat"] = gitPat },
                },
            };
        }

        if (envSecrets.Count > 0)
        {
            recipeConfig["envSecrets"] = envSecrets;
        }

        if (recipeConfig.Count > 0)
        {
            legacyEnvConstruct.RecipeConfig = BicepPostProcessor.ToBicepObject(recipeConfig);
        }
    }

    /// <summary>
    /// Resolves the value emitted for a secret-store reference in <c>recipeConfig</c>: the store's
    /// <c>.id</c> expression.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The store is not emitted for this environment (<c>ASPIRERADIUS050</c>).
    /// </exception>
    private object ResolveSecretStoreReference(
        RadiusSecretStoreResource store,
        IReadOnlyDictionary<string, RadiusSecretStoreConstruct> storeConstructs)
    {
        if (storeConstructs.TryGetValue(store.Name, out var construct))
        {
            return BuildIdExpression(construct);
        }

        // Never fall back to the bare store name: that emits a plain string where a secret-store
        // `.id` is expected, producing a reference Radius rejects only at deploy (or, worse, that
        // silently resolves to nothing). Fail fast with an actionable diagnostic naming the
        // consuming environment and the unresolved store.
        throw new InvalidOperationException(
            $"Environment '{_environment.Name}' references secret store '{store.Name}', but that store is not " +
            "emitted for this environment. Ensure the store is declared on this environment. " +
            "Diagnostic: ASPIRERADIUS050.");
    }

    /// <summary>
    /// Populates a secret-store construct's <c>data</c> for the inline (Radius-created) mode: each
    /// key's value is a reference to a valueless <c>@secure()</c> Bicep <c>param</c> (reusing
    /// <see cref="GetOrAddRecipeParameter"/>), with <c>encoding</c> emitted when the author set it
    /// explicitly or the type default is not <c>raw</c>.
    /// </summary>
    private void PopulateInlineSecretStoreData(RadiusSecretStoreResource store, RadiusSecretStoreConstruct construct)
    {
        if (!store.Population.HasInlineData)
        {
            return;
        }

        foreach (var (key, binding) in store.Population.Data)
        {
            var parameter = GetOrAddRecipeParameter(binding.Parameter);
            var entry = new RadiusSecretStoreDataEntryConstruct
            {
                Value = new IdentifierExpression(parameter.BicepIdentifier),
            };

            var encoding = binding.Encoding ?? store.Type.DefaultEncoding();
            if (binding.Encoding is not null || !string.Equals(encoding, "raw", StringComparison.Ordinal))
            {
                entry.Encoding = encoding;
            }

            construct.Data[key] = entry;
        }
    }

    /// <summary>
    /// Populates a secret-store construct for the existing-secret / sealed-secret modes: emits
    /// <c>properties.resource: '&lt;namespace&gt;/&lt;name&gt;'</c> and each declared key as an
    /// empty object (<c>{}</c>). A bare <c>&lt;name&gt;</c> defaults its namespace to the owning
    /// environment's <see cref="RadiusEnvironmentResource.Namespace"/>.
    /// </summary>
    private void PopulateSecretReferenceData(
        RadiusSecretStoreResource store,
        RadiusSecretStoreConstruct construct,
        RadiusInfrastructureOptions options)
    {
        if (!store.Population.IsSecretReference)
        {
            return;
        }

        construct.ResourceReference = ResolveSecretResourceReference(store, options);

        foreach (var key in store.Population.Keys)
        {
            // An entry with no assigned properties emits as an empty object, naming a key to
            // expose from the referenced Secret without passing any value through Aspire.
            construct.Data[key] = new RadiusSecretStoreDataEntryConstruct();
        }
    }

    /// <summary>
    /// Resolves a secret store's <c>resource</c> reference: a fully-qualified
    /// <c>&lt;namespace&gt;/&lt;name&gt;</c> is emitted verbatim; a bare <c>&lt;name&gt;</c> is
    /// prefixed with the owning environment's namespace.
    /// </summary>
    private string ResolveSecretResourceReference(RadiusSecretStoreResource store, RadiusInfrastructureOptions options)
    {
        var population = store.Population;
        var defaultNamespace = store.OwningEnvironment?.Namespace ?? _environment.Namespace;

        // For a sealed store the underlying Secret's namespace/name come from the SealedSecret
        // manifest metadata (also the deploy-time materialization poll target); a missing or
        // unreadable manifest fails publish with ASPIRERADIUS044.
        if (population.HasSealedSecret)
        {
            var manifestPath = store.Population.SealedManifestPath!;
            if (!options.SealedSecretManifests.TryGetValue(store.Name, out var manifest))
            {
                manifest = SealedSecretManifest.ReadValidated(store.Name, manifestPath, defaultNamespace);
                options.SealedSecretManifests[store.Name] = manifest;
            }

            var metadata = manifest.Metadata;
            RadiusSecretStoreValidation.ValidateSealedSecretNamespace(store, metadata, manifest.SourcePath);
            return $"{metadata.Namespace}/{metadata.Name}";
        }

        var reference = population.ResourceReference!;
        if (reference.Contains('/', StringComparison.Ordinal))
        {
            return reference;
        }

        return $"{defaultNamespace}/{reference}";
    }
}
