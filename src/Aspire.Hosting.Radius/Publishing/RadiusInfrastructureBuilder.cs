// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS002 // RadiusRecipe.Name signals upstream deprecation for callers; internal publisher code still reads it.

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
    /// Default recipe template paths per resource type.
    /// </summary>
    private static readonly Dictionary<string, string> s_defaultRecipeTemplates = new(StringComparer.Ordinal)
    {
        [RadiusResourceTypes.RedisCaches] = "ghcr.io/radius-project/recipes/local-dev/rediscaches:latest",
        [RadiusResourceTypes.SqlDatabases] = "ghcr.io/radius-project/recipes/local-dev/sqldatabases:latest",
        [RadiusResourceTypes.PostgreSqlDatabases] = "ghcr.io/radius-project/recipes/local-dev/postgresqldatabases:latest",
        [RadiusResourceTypes.MongoDatabases] = "ghcr.io/radius-project/recipes/local-dev/mongodatabases:latest",
        [RadiusResourceTypes.RabbitMQQueues] = "ghcr.io/radius-project/recipes/local-dev/rabbitmqqueues:latest",
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
    internal RadiusInfrastructureOptions Build()
    {
        var options = new RadiusInfrastructureOptions();
        var envIdentifier = BicepPostProcessor.SanitizeIdentifier(_environment.Name);

        // Classify resources for this environment
        var (radiusResources, computeResources) = ClassifyResources();

        // 1. UDT recipe pack (created first so environment can reference its ID)
        var recipePackIdentifier = "recipepack";
        var udtRecipeEntries = new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
        var legacyRecipeEntries = new Dictionary<string, Dictionary<string, RecipeEntry>>(StringComparer.Ordinal);
        var typeInstancesByResourceName = new Dictionary<string, RadiusResourceTypeConstruct>(StringComparer.Ordinal);

        // Collect recipe entries: UDT types go into the recipe pack, legacy
        // Applications.* types are stashed for inline emission on the legacy env.
        foreach (var resource in radiusResources)
        {
            var (resourceType, _) = ResolveResourceType(resource);
            var customization = GetCustomization(resource);

            if (IsLegacyResourceType(resourceType))
            {
                AddLegacyRecipeEntry(legacyRecipeEntries, resourceType, customization);
            }
            else
            {
                AddRecipeEntry(udtRecipeEntries, resourceType, customization);
            }
        }

        // Partition flags.
        var hasUdtResources = radiusResources.Any(r =>
            !IsLegacyResourceType(ResolveResourceType(r).ResourceType));
        var hasLegacyResources = radiusResources.Any(r =>
            IsLegacyResourceType(ResolveResourceType(r).ResourceType));
        var hasComputeResources = computeResources.Any();
        var useLegacyContainers = _environment.UseLegacyContainers;

        // Compute workloads can route to either the UDT compute container type
        // or the legacy Applications.Core/containers type. When containers go
        // legacy they don't force the UDT chain (only legacy parents).
        var computeForcesUdtChain = hasComputeResources && !useLegacyContainers;
        var computeForcesLegacyChain = hasComputeResources && useLegacyContainers;

        // 2. UDT environment + application — emitted only when we have UDT
        // radius resources or any UDT-bound compute workload. Pure-legacy
        // publishes (Redis-only, or compute-with-WithLegacyContainers) skip
        // the UDT chain entirely so older Radius installs aren't forced to
        // understand `Radius.Core/*`.
        RadiusRecipePackConstruct? recipePackConstruct = null;
        RadiusEnvironmentConstruct? envConstruct = null;
        RadiusApplicationConstruct? appConstruct = null;
        var appIdentifier = "app";

        if (hasUdtResources || computeForcesUdtChain)
        {
            recipePackConstruct = CreateRecipePackConstruct(recipePackIdentifier, udtRecipeEntries);
            options.RecipePacks.Add(recipePackConstruct);

            envConstruct = CreateEnvironmentConstruct(envIdentifier, recipePackConstruct);
            options.Environments.Add(envConstruct);

            appConstruct = CreateApplicationConstruct(appIdentifier, envConstruct);
            options.Applications.Add(appConstruct);
        }

        // 3. Legacy parents are emitted lazily — only if any legacy resource is
        // present, or the env opts into legacy containers. Legacy env/app share
        // the *resource name* with the UDT pair so Radius still sees them as
        // the same logical app/environment; only the Bicep identifiers differ.
        LegacyApplicationEnvironmentConstruct? legacyEnvConstruct = null;
        LegacyApplicationConstruct? legacyAppConstruct = null;

        if (hasLegacyResources || computeForcesLegacyChain)
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
                legacyAppIdentifier, appIdentifier, legacyEnvConstruct);
            options.LegacyApplications.Add(legacyAppConstruct);
        }

        // 4. Resource type instances — parent wiring depends on legacy vs UDT.
        // Track each builder-created instance's parent pair so RewireIdReferences
        // can re-resolve `.id` after callbacks without clobbering resources that
        // a callback added itself.
        var instanceParents = new Dictionary<RadiusResourceTypeConstruct, (ProvisionableResource Env, ProvisionableResource App)>();

        foreach (var resource in radiusResources)
        {
            var (resourceType, apiVersion) = ResolveResourceType(resource);
            var identifier = BicepPostProcessor.SanitizeIdentifier(resource.Name);

            var customization = GetCustomization(resource);
            var isLegacy = IsLegacyResourceType(resourceType);

            ProvisionableResource parentEnv = isLegacy ? legacyEnvConstruct! : envConstruct!;
            ProvisionableResource parentApp = isLegacy ? legacyAppConstruct! : appConstruct!;

            var typeInstance = CreateResourceTypeConstruct(
                identifier, resource.Name, resourceType, apiVersion,
                parentApp, parentEnv, customization);
            options.ResourceTypeInstances.Add(typeInstance);
            typeInstancesByResourceName[resource.Name] = typeInstance;
            instanceParents[typeInstance] = (parentEnv, parentApp);
        }

        // 5. Container workloads. By default, route to the UDT compute container
        // type parented to the UDT application. When `WithLegacyContainers()` is
        // set, route to legacy `Applications.Core/containers` parented to the
        // legacy application instead — useful when the target Radius install
        // has no recipe registered for `Radius.Compute/containers`.
        var containerConnectionTargets = new Dictionary<RadiusContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>>();
        var legacyContainerConnectionTargets = new Dictionary<LegacyContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>>();
        foreach (var resource in computeResources)
        {
            var identifier = BicepPostProcessor.SanitizeIdentifier(resource.Name);
            var image = GetContainerImage(resource);
            var connectionTargets = GetConnectionTargets(resource, radiusResources, typeInstancesByResourceName);

            WarnIfImageMayNotPull(resource.Name, image);

            if (useLegacyContainers)
            {
                var legacyContainerConstruct = CreateLegacyContainerConstruct(
                    identifier, resource.Name, image, legacyAppConstruct!, connectionTargets);
                options.LegacyContainers.Add(legacyContainerConstruct);
                legacyContainerConnectionTargets[legacyContainerConstruct] = connectionTargets;
            }
            else
            {
                var containerConstruct = CreateContainerConstruct(
                    identifier, resource.Name, image, appConstruct!, connectionTargets);
                options.Containers.Add(containerConstruct);
                containerConnectionTargets[containerConstruct] = connectionTargets;
            }
        }

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
                kv => (EnvId: kv.Value.Env.BicepIdentifier,
                       AppId: kv.Value.App.BicepIdentifier)),
            containerConnectionTargets.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(
                    tkv => tkv.Key, tkv => tkv.Value.BicepIdentifier)),
            legacyContainerConnectionTargets.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(
                    tkv => tkv.Key, tkv => tkv.Value.BicepIdentifier)));

        RunConfigureCallbacks(options);

        // 7. Rewire `.id` cross-references for targets whose BicepIdentifier
        // was changed by a callback; leave everything else (including callback
        // edits to references) alone.
        RewireIdReferences(options, appConstruct, envConstruct,
            legacyAppConstruct, legacyEnvConstruct, instanceParents,
            containerConnectionTargets, legacyContainerConnectionTargets,
            identifierSnapshot);

        return options;
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
        Dictionary<RadiusResourceTypeConstruct, (string EnvId, string AppId)> InstanceParentIds,
        Dictionary<RadiusContainerConstruct, Dictionary<string, string>> ContainerConnectionTargetIds,
        Dictionary<LegacyContainerConstruct, Dictionary<string, string>> LegacyContainerConnectionTargetIds);

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
        Dictionary<RadiusResourceTypeConstruct, (ProvisionableResource Env, ProvisionableResource App)> instanceParents,
        Dictionary<RadiusContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>> containerConnectionTargets,
        Dictionary<LegacyContainerConstruct, Dictionary<string, RadiusResourceTypeConstruct>> legacyContainerConnectionTargets,
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

            if (!string.Equals(parents.Env.BicepIdentifier, snapIds.EnvId, StringComparison.Ordinal))
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

        // Legacy containers — rewire ApplicationId only if the legacy app was
        // renamed; rewire each connection source only if its target was renamed.
        foreach (var container in options.LegacyContainers)
        {
            if (!legacyContainerConnectionTargets.TryGetValue(container, out var targets))
            {
                continue;
            }

            if (legacyAppConstruct is not null && IdentifierChanged(legacyAppConstruct, snapshot.LegacyAppId))
            {
                container.ApplicationId = BuildIdExpression(legacyAppConstruct);
            }

            if (targets.Count == 0 ||
                !snapshot.LegacyContainerConnectionTargetIds.TryGetValue(container, out var targetSnapIds))
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
    /// Resolves the Radius resource type and API version for a resource, checking for
    /// custom type overrides from <see cref="RadiusResourceCustomization"/> before falling
    /// back to the <see cref="ResourceTypeMapper"/>.
    /// </summary>
    private (string ResourceType, string ApiVersion) ResolveResourceType(IResource resource)
    {
        var customization = GetCustomization(resource);
        if (customization?.TypeOverride is { } typeOverride)
        {
            var apiVersion = typeOverride.ApiVersion ?? RadiusResourceTypes.RadiusApiVersion;
            _logger.LogInformation(
                "Resource '{ResourceName}' using custom type override '{RadiusType}' (API {ApiVersion}).",
                resource.Name, typeOverride.Type, apiVersion);
            return (typeOverride.Type, apiVersion);
        }

        return _typeMapper.MapResource(resource);
    }

    private (List<IResource> radiusResources, List<IResource> computeResources) ClassifyResources()
    {
        var radiusTypes = new List<IResource>();
        var compute = new List<IResource>();
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
            var (resourceType, _) = ResolveResourceType(resource);

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

        return (radiusTypes, compute);
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

    private static RadiusResourceCustomization? GetCustomization(IResource resource)
    {
        var annotation = resource.Annotations.OfType<RadiusResourceCustomizationAnnotation>().LastOrDefault();
        return annotation?.Customization;
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
        return construct;
    }

    private static RadiusApplicationConstruct CreateApplicationConstruct(
        string identifier, RadiusEnvironmentConstruct envConstruct)
    {
        var construct = new RadiusApplicationConstruct(identifier);
        construct.ApplicationName = identifier;
        construct.EnvironmentId = BuildIdExpression(envConstruct);
        return construct;
    }

    private static RadiusResourceTypeConstruct CreateResourceTypeConstruct(
        string identifier, string resourceName, string resourceType, string apiVersion,
        ProvisionableResource appConstruct, ProvisionableResource envConstruct,
        RadiusResourceCustomization? customization)
    {
        var construct = new RadiusResourceTypeConstruct(identifier, resourceType, apiVersion);
        construct.ResourceName = resourceName;
        construct.ApplicationId = BuildIdExpression(appConstruct);
        construct.EnvironmentId = BuildIdExpression(envConstruct);

        if (customization is not null)
        {
            // Custom recipe
            if (customization.Recipe is not null)
            {
                // Empty Name means the caller relied on the default; map to "default"
                // so the emitted recipe pack entry has a sensible name.
                construct.RecipeName = string.IsNullOrEmpty(customization.Recipe.Name)
                    ? "default"
                    : customization.Recipe.Name;

                if (customization.Recipe.Parameters.Count > 0)
                {
                    foreach (var (key, value) in customization.Recipe.Parameters)
                    {
                        construct.RecipeParameters[key] = BicepPostProcessor.ToBicepLiteral(value);
                    }
                }
            }
        }

        return construct;
    }

    private void AddRecipeEntry(
        Dictionary<string, RecipeEntry> entries,
        string resourceType,
        RadiusResourceCustomization? customization)
    {
        // Custom recipe with template path
        if (customization?.Recipe?.RecipeLocation is not null)
        {
            entries[resourceType] = new RecipeEntry("bicep", customization.Recipe.RecipeLocation);
            return;
        }

        // Default recipe template
        if (s_defaultRecipeTemplates.TryGetValue(resourceType, out var defaultTemplate))
        {
            // Don't overwrite a custom entry
            entries.TryAdd(resourceType, new RecipeEntry("bicep", defaultTemplate));
        }
        else
        {
            _logger.LogWarning(
                "No default recipe template found for resource type '{ResourceType}'. " +
                "Consider providing a custom recipe via PublishAsRadiusResource().",
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
            var recipeEntry = new RecipeEntryConstruct();
            recipeEntry.RecipeKind = entry.RecipeKind;
            recipeEntry.RecipeLocation = entry.RecipeLocation;
            construct.Recipes[type] = recipeEntry;
        }

        return construct;
    }

    private void AddLegacyRecipeEntry(
        Dictionary<string, Dictionary<string, RecipeEntry>> entries,
        string resourceType,
        RadiusResourceCustomization? customization)
    {
        var recipeName = customization?.Recipe?.Name ?? "default";

        if (!entries.TryGetValue(resourceType, out var byName))
        {
            byName = new Dictionary<string, RecipeEntry>(StringComparer.Ordinal);
            entries[resourceType] = byName;
        }

        if (customization?.Recipe?.RecipeLocation is not null)
        {
            // Custom recipe: last write wins *for the same (type, recipeName)*.
            byName[recipeName] = new RecipeEntry("bicep", customization.Recipe.RecipeLocation);
            return;
        }

        if (s_defaultRecipeTemplates.TryGetValue(resourceType, out var defaultTemplate))
        {
            byName.TryAdd(recipeName, new RecipeEntry("bicep", defaultTemplate));
        }
        else
        {
            _logger.LogWarning(
                "No default recipe template found for legacy resource type '{ResourceType}'. " +
                "Consider providing a custom recipe via PublishAsRadiusResource().",
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
        LegacyApplicationEnvironmentConstruct legacyEnvConstruct)
    {
        var construct = new LegacyApplicationConstruct(identifier);
        // Share the UDT application's `name:` — rubber-duck feedback: only the
        // Bicep identifier is suffixed with `_legacy`.
        construct.ApplicationName = applicationName;
        construct.EnvironmentId = BuildIdExpression(legacyEnvConstruct);
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
        Dictionary<string, RadiusResourceTypeConstruct> connectionTargets)
    {
        var construct = new RadiusContainerConstruct(identifier);
        construct.ContainerName = resourceName;
        construct.Image = image;
        construct.ApplicationId = BuildIdExpression(appConstruct);

        if (connectionTargets.Count > 0)
        {
            foreach (var (name, targetConstruct) in connectionTargets)
            {
                var connectionConstruct = new ConnectionConstruct();
                connectionConstruct.Source = BuildIdExpression(targetConstruct);
                construct.Connections[name] = connectionConstruct;
            }
        }

        return construct;
    }

    private static LegacyContainerConstruct CreateLegacyContainerConstruct(
        string identifier, string resourceName, string image,
        LegacyApplicationConstruct legacyAppConstruct,
        Dictionary<string, RadiusResourceTypeConstruct> connectionTargets)
    {
        var construct = new LegacyContainerConstruct(identifier);
        construct.ContainerName = resourceName;
        construct.Image = image;
        construct.ApplicationId = BuildIdExpression(legacyAppConstruct);

        if (connectionTargets.Count > 0)
        {
            foreach (var (name, targetConstruct) in connectionTargets)
            {
                var connectionConstruct = new ConnectionConstruct();
                connectionConstruct.Source = BuildIdExpression(targetConstruct);
                construct.Connections[name] = connectionConstruct;
            }
        }

        return construct;
    }

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
