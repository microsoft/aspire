// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

#pragma warning disable ASPIRECOMPUTE002 // GetEndpointPropertyExpression/GetHostAddressExpression are experimental compute-environment APIs the publisher relies on.
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
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

    /// <summary>
    /// Default recipe template paths per resource type.
    /// </summary>
    private static readonly Dictionary<string, string> s_defaultRecipeTemplates = new(StringComparer.Ordinal)
    {
        [RadiusResourceTypes.RedisCaches] = "ghcr.io/radius-project/recipes/local-dev/rediscaches:latest",
        [RadiusResourceTypes.SqlDatabases] = "ghcr.io/radius-project/recipes/local-dev/sqldatabases:latest",
        [RadiusResourceTypes.PostgreSqlDatabases] = "ghcr.io/radius-project/recipes/local-dev/postgresqldatabases:latest",
        [RadiusResourceTypes.MongoDatabases] = "ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest",
        [RadiusResourceTypes.RabbitMQQueues] = "ghcr.io/radius-project/recipes/local-dev/rabbitmqqueues:latest",
        // The Radius.Compute/containers UDT needs a recipe registered in the env's recipe pack;
        // shipped Radius does not include one by default, so register the published container
        // recipe so native containers deploy without a manually-authored recipe.
        [RadiusResourceTypes.Containers] = "ghcr.io/radius-project/kube-recipes/containers:latest",
        // Legacy fallback types also get default recipes
        [RadiusResourceTypes.LegacyRedisCaches] = "ghcr.io/radius-project/recipes/local-dev/rediscaches:latest",
        [RadiusResourceTypes.LegacyMongoDatabases] = "ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest",
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
        var typeInstancesByResourceName = new Dictionary<string, RadiusResourceTypeConstruct>(StringComparer.Ordinal);

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

        if (hasLegacyResources)
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
            typeInstancesByResourceName[resource.Name] = typeInstance;
            instanceParents[typeInstance] = (parentEnv, parentApp);
        }

        // 5. Container workloads always route to the UDT compute container type
        // (Radius.Compute/containers) parented to the UDT application.
        var containerConnectionTargets = new Dictionary<RadiusContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>>();
        foreach (var resource in computeResources)
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(resource.Name);
            var image = GetContainerImage(resource);
            var connectionTargets = GetConnectionTargets(resource, radiusResources, typeInstancesByResourceName);
            WarnIfImageMayNotPull(resource.Name, image);

            // Resolve the resource's environment variables (config, connection strings, OTEL_*,
            // WithEnvironment, and `services__*` service discovery) and its endpoint ports the
            // same way the Kubernetes publisher does, so the deployed container behaves like the
            // local run. Secret/parameter values are routed to Bicep `param`s (never literals).
            var env = await ResolveEnvironmentAsync(resource).ConfigureAwait(false);
            var ports = ResolvePorts(resource);

            var containerConstruct = CreateContainerConstruct(
                identifier, resource.Name, image, appConstruct!, envConstruct, connectionTargets, env, ports);
            options.Containers.Add(containerConstruct);
            containerConnectionTargets[containerConstruct] = connectionTargets;
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
                    tkv => tkv.Key, tkv => tkv.Value.BicepIdentifier)));

        RunConfigureCallbacks(options);

        // 7. Rewire `.id` cross-references for targets whose BicepIdentifier
        // was changed by a callback; leave everything else (including callback
        // edits to references) alone.
        RewireIdReferences(options, appConstruct, envConstruct,
            legacyAppConstruct, legacyEnvConstruct, instanceParents,
            containerConnectionTargets,
            identifierSnapshot);

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

        if (_deployParametersByIdentifier.Count > 0)
        {
            _environment.Annotations.Add(new RadiusDeployParametersAnnotation(
                new Dictionary<string, ParameterResource>(_deployParametersByIdentifier, StringComparer.Ordinal)));
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
        Dictionary<RadiusContainerConstruct, Dictionary<string, string>> ContainerConnectionTargetIds);

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

    private static RadiusRecipePackConstruct CreateRecipePackConstruct(
        string identifier, Dictionary<string, RecipeEntry> recipeEntries)
    {
        var construct = new RadiusRecipePackConstruct(identifier);
        construct.PackName = "default";

        foreach (var (type, entry) in recipeEntries)
        {
            construct.Recipes[type] = new RecipeEntryConstruct
            {
                RecipeKind = entry.RecipeKind,
                RecipeLocation = entry.RecipeLocation,
            };
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
            foreach (var (recipeName, entry) in byName)
            {
                inner[recipeName] = new LegacyRecipeEntryConstruct
                {
                    TemplateKind = entry.RecipeKind,
                    TemplatePath = entry.RecipeLocation,
                };
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

        foreach (var endpoint in endpoints)
        {
            // Prefer the target (in-container) port; fall back to the declared host port. In
            // publish mode neither may be allocated yet, in which case there is nothing to emit.
            var portValue = endpoint.TargetPort ?? endpoint.Port;
            if (portValue is not int containerPort)
            {
                continue;
            }

            var port = new ContainerPortConstruct
            {
                ContainerPort = containerPort,
                Protocol = endpoint.Protocol == ProtocolType.Udp ? "UDP" : "TCP",
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
                await ResolveEnvPartsAsync(rawValue, resource, parts).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // Some endpoint references cannot be resolved at publish time — e.g. a non-HTTP
                // scheme (redis, tcp, ...) whose port isn't allocated yet. Those backing resources
                // are surfaced through Radius `connection`s instead of literal container env vars,
                // so skip the variable rather than failing the whole build.
                _logger.LogDebug(ex, "Skipping environment variable '{Key}' on resource '{Resource}': value could not be resolved at publish time.", key, resource.Name);
                continue;
            }

            result[key] = new ContainerEnvVarConstruct { Value = BuildEnvBicepValue(parts) };
        }

        return result;
    }

    // An ordered fragment of a container env-var value: either a literal string or a reference to
    // a Bicep parameter (used for secret/parameter values so the literal is never emitted).
    private readonly record struct EnvPart(string? Literal, ProvisioningParameter? Parameter)
    {
        public static EnvPart FromLiteral(string literal) => new(literal, null);
        public static EnvPart FromParameter(ProvisioningParameter parameter) => new(null, parameter);
    }

    /// <summary>
    /// Recursively flattens an environment-variable value into ordered <see cref="EnvPart"/>s.
    /// Endpoint references resolve to cluster-FQDN URLs, parameter resources resolve to Bicep
    /// <c>param</c> references, and composite reference expressions are spliced together so a
    /// mixed literal/secret value is preserved precisely.
    /// </summary>
    private async Task ResolveEnvPartsAsync(object? value, IResource owner, List<EnvPart> parts)
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
                parts.Add(EnvPart.FromParameter(GetOrAddEnvParameter(param)));
                return;
            case IResourceBuilder<ParameterResource> paramBuilder:
                parts.Add(EnvPart.FromParameter(GetOrAddEnvParameter(paramBuilder.Resource)));
                return;
            case EndpointReference endpointReference:
                parts.Add(EnvPart.FromLiteral(ResolveEndpointUrl(endpointReference)));
                return;
            case EndpointReferenceExpression endpointReferenceExpression:
                parts.Add(EnvPart.FromLiteral(ResolveEndpointProperty(endpointReferenceExpression)));
                return;
            case ConnectionStringReference connectionStringReference:
                await ResolveEnvPartsAsync(connectionStringReference.Resource.ConnectionStringExpression, owner, parts).ConfigureAwait(false);
                return;
            case IResourceWithConnectionString resourceWithConnectionString:
                await ResolveEnvPartsAsync(resourceWithConnectionString.ConnectionStringExpression, owner, parts).ConfigureAwait(false);
                return;
            case ReferenceExpression referenceExpression:
                await ResolveReferenceExpressionPartsAsync(referenceExpression, owner, parts).ConfigureAwait(false);
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
                    var resolved = await valueProvider.GetValueAsync(context, _cancellationToken).ConfigureAwait(false);
                    parts.Add(EnvPart.FromLiteral(resolved ?? string.Empty));
                    return;
                }

                parts.Add(EnvPart.FromLiteral(value.ToString() ?? string.Empty));
                return;
        }
    }

    /// <summary>
    /// Splices a composite <see cref="ReferenceExpression"/> into ordered parts by interleaving
    /// its literal <see cref="ReferenceExpression.Format"/> chunks with the recursively-resolved
    /// parts of each value provider (matching the <c>{0}</c>, <c>{1}</c>, ... placeholders).
    /// </summary>
    private async Task ResolveReferenceExpressionPartsAsync(ReferenceExpression expression, IResource owner, List<EnvPart> parts)
    {
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
            await ResolveEnvPartsAsync(expression.ValueProviders[i], owner, inner).ConfigureAwait(false);
            providerParts[i] = inner;
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

    private static string UnescapeBraces(string format) =>
        format.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);

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
        if (parts.All(static p => p.Parameter is null))
        {
            return string.Concat(parts.Select(static p => p.Literal));
        }

        // A single parameter with no surrounding literals maps straight to the `param` reference,
        // emitting `value: paramName` rather than an interpolated string.
        if (parts is [{ Literal: null, Parameter: { } soleParameter }])
        {
            return soleParameter;
        }

        // Mixed literal/parameter value: build an interpolated Bicep string ('...${param}...').
        // Literals are passed as interpolation arguments (not spliced into the format) so any '{'
        // or '}' they contain can't be misread as a placeholder.
        var format = new StringBuilder();
        var args = new object[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            format.Append('{').Append(i.ToString(CultureInfo.InvariantCulture)).Append('}');
            args[i] = parts[i].Parameter is { } parameter ? parameter : parts[i].Literal!;
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

    internal readonly record struct RecipeEntry(string RecipeKind, string RecipeLocation);
}
