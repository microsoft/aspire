// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Azd;
using Aspire.Hosting.JavaScript;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for importing an existing Azure Developer CLI (azd) project into an
/// Aspire application model.
/// </summary>
/// <remarks>
/// These APIs let a team that already uses azd adopt Aspire without abandoning their existing assets.
/// The importer reads the project's <c>azure.yaml</c>, its selected <c>.azure</c> environment, and its
/// existing <c>infra</c> folder, and projects the azd services and resources onto equivalent Aspire
/// resources. Existing infrastructure-as-code is preserved (not regenerated), and anything that cannot
/// be represented automatically is reported through <see cref="AzdImport.Diagnostics"/>.
/// </remarks>
public static partial class AzdProjectBuilderExtensions
{
    /// <summary>
    /// Imports an existing azd project (described by an <c>azure.yaml</c> file) into the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="azureYamlPath">
    /// The path to the project's <c>azure.yaml</c> file or the directory that contains it. The path may
    /// be absolute or relative to the app host directory. When <see langword="null"/>, the app host
    /// directory is searched for <c>azure.yaml</c> (or <c>azure.yml</c>).
    /// </param>
    /// <param name="configureOptions">An optional callback used to configure how the project is imported.</param>
    /// <returns>
    /// An <see cref="AzdImport"/> describing the parsed project, the resolved environment, the Aspire
    /// resource builders that were created, and any diagnostics produced.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">Thrown when an <c>azure.yaml</c> file cannot be located.</exception>
    /// <example>
    /// Import an azd project that sits next to the app host and reference an imported resource:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var azd = builder.AddAzdProject();
    ///
    /// foreach (var diagnostic in azd.Diagnostics.Items)
    /// {
    ///     Console.WriteLine(diagnostic);
    /// }
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [Experimental("ASPIREAZUREAZD001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "azd import is a .NET app host authoring API and is not part of the polyglot ATS surface.")]
    public static AzdImport AddAzdProject(
        this IDistributedApplicationBuilder builder,
        string? azureYamlPath = null,
        Action<AzdImportOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AzdImportOptions();
        configureOptions?.Invoke(options);

        var resolvedPath = ResolveAzureYamlPath(builder, azureYamlPath);
        var projectDirectory = Path.GetDirectoryName(resolvedPath)!;

        var project = AzdProjectParser.Parse(File.ReadAllText(resolvedPath));
        var environment = AzdEnvironmentReader.Read(projectDirectory, options.EnvironmentName);

        var import = new AzdImport(builder, project, projectDirectory, environment);

        if (environment is not null)
        {
            import.Diagnostics.Information($"Loaded azd environment '{environment.Name}' from the .azure directory.");
        }

        // Reuse the subscription/location/resource group azd recorded so a migrated app provisions into
        // the same place, instead of re-prompting or creating a parallel environment.
        ApplyAzureEnvironment(builder, project, environment, options, import);

        PreserveInfrastructure(import, project);

        var mappers = new List<IAzdResourceMapper>(options.ResourceMappers);
        mappers.AddRange(BuiltInResourceMappers.Create());

        // Services and resources share a single Aspire resource-name namespace; track allocated names so
        // two azd keys that sanitize to the same name do not collide (which would throw in AppHost build).
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resources are imported before services so that service `uses` references can resolve to them.
        ImportResources(builder, import, project, environment, mappers, usedNames, options);
        ImportServices(builder, import, project, environment, projectDirectory, options, usedNames);
        WireUses(builder, import, project);
        ReportUnwiredResourceUses(import, project);

        return import;
    }

    /// <summary>
    /// Imports an existing azd project (described by an <c>azure.yaml</c> file) into the application model.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="azureYamlPath">
    /// The path to the project's <c>azure.yaml</c> file or the directory that contains it. The path may
    /// be absolute or relative to the app host directory.
    /// </param>
    /// <returns>
    /// An <see cref="AzdImport"/> describing the parsed project, the resolved environment, the Aspire
    /// resource builders that were created, and any diagnostics produced.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This overload exists so the importer can be called from polyglot (for example TypeScript) app hosts
    /// through the Aspire Type System. It is the path-only form of
    /// <see cref="AddAzdProject(IDistributedApplicationBuilder, string?, System.Action{AzdImportOptions}?)"/>;
    /// callers that need to customize the import (environment name, emulator behavior, custom mappers) use
    /// the C# overload that accepts an <see cref="AzdImportOptions"/> callback.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="azureYamlPath"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when an <c>azure.yaml</c> file cannot be located.</exception>
    /// <example>
    /// Import an azd project from a TypeScript app host:
    /// <code>
    /// const azd = await builder.addAzdProject("./azd-app");
    /// await builder.build().run();
    /// </code>
    /// </example>
    [Experimental("ASPIREAZUREAZD001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static AzdImport AddAzdProject(this IDistributedApplicationBuilder builder, string azureYamlPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(azureYamlPath);

        return AddAzdProject(builder, azureYamlPath, configureOptions: null);
    }

    private static void ImportResources(
        IDistributedApplicationBuilder builder,
        AzdImport import,
        AzdProject project,
        AzdEnvironment? environment,
        List<IAzdResourceMapper> mappers,
        HashSet<string> usedNames,
        AzdImportOptions options)
    {
        foreach (var (key, resource) in project.Resources)
        {
            var resourceName = AllocateUniqueName(BuiltInResourceMappers.Sanitize(resource.Name ?? key), usedNames, import, key);
            var context = new AzdResourceMapContext(builder, resourceName, resource, environment, import.Diagnostics);

            var mapper = mappers.FirstOrDefault(m => m.CanMap(context));
            if (mapper is null)
            {
                import.Diagnostics.Warning(
                    $"No mapper is registered for azd resource type '{resource.Type ?? "(none)"}'. The resource was skipped; add a custom IAzdResourceMapper to handle it.",
                    key);
                continue;
            }

            var mapped = mapper.Map(context);
            if (mapped is not null)
            {
                import.AddResource(key, mapped);
                ConfigureProvisioning(builder, mapped, resource, environment, project, options, import, key);
            }
        }
    }

    // Turns each imported Azure resource into something that both runs locally and deploys to the
    // customer's existing Azure environment, which is what makes the import behave natively end to end.
    private static void ConfigureProvisioning(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<IResource> mapped,
        AzdResource resource,
        AzdEnvironment? environment,
        AzdProject project,
        AzdImportOptions options,
        AzdImport import,
        string key)
    {
        // azd's `existing: true` means "reference, don't provision". Bind the imported resource to the
        // already-provisioned Azure resource so neither run nor publish recreates something the customer
        // already owns. This is the annotation form of AsExisting; we set it directly because the builder
        // is upcast to IResourceBuilder<IResource> here, so the generic AsExisting<T> overload is not callable.
        if (resource.Existing && options.BindExistingResources && mapped.Resource is IAzureResource)
        {
            var existingName = resource.Name ?? key;
            var resourceGroup = ResolveResourceGroup(environment, project);

            mapped.WithAnnotation(
                resourceGroup is null
                    ? new ExistingAzureResourceAnnotation(existingName)
                    : new ExistingAzureResourceAnnotation(existingName, resourceGroup),
                ResourceAnnotationMutationBehavior.Replace);

            var resourceGroupSuffix = resourceGroup is null ? string.Empty : $" in resource group '{resourceGroup}'";
            import.Diagnostics.Information(
                $"azd resource '{key}' is marked 'existing: true'; it was bound to the existing Azure resource '{existingName}'{resourceGroupSuffix} instead of being provisioned again.",
                key);
        }

        if (options.UseEmulatorsForLocalRun && builder.ExecutionContext.IsRunMode)
        {
            ApplyLocalRunMode(builder, mapped, import, key);
        }
    }

    // In run mode, swap resources that support a local development experience to a container or emulator
    // so `aspire run` works offline. Publish mode leaves the real Azure resource untouched; the
    // RunAs* methods also self-gate to run mode, but gating here keeps the intent explicit.
    private static void ApplyLocalRunMode(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<IResource> mapped,
        AzdImport import,
        string key)
    {
        switch (mapped.Resource)
        {
            case AzureManagedRedisResource redis:
                builder.CreateResourceBuilder(redis).RunAsContainer();
                import.Diagnostics.Information($"azd resource '{key}' runs locally as a Redis container; it provisions Azure Managed Redis on publish.", key);
                break;
            case AzurePostgresFlexibleServerResource postgres:
                builder.CreateResourceBuilder(postgres).RunAsContainer();
                import.Diagnostics.Information($"azd resource '{key}' runs locally as a PostgreSQL container; it provisions Azure Database for PostgreSQL on publish.", key);
                break;
            case AzureStorageResource storage:
                builder.CreateResourceBuilder(storage).RunAsEmulator();
                import.Diagnostics.Information($"azd resource '{key}' runs locally on the Azurite storage emulator; it provisions Azure Storage on publish.", key);
                break;
            case AzureCosmosDBResource cosmos:
                builder.CreateResourceBuilder(cosmos).RunAsEmulator();
                import.Diagnostics.Information($"azd resource '{key}' runs locally on the Cosmos DB emulator; it provisions Azure Cosmos DB on publish.", key);
                break;
            case AzureServiceBusResource serviceBus:
                builder.CreateResourceBuilder(serviceBus).RunAsEmulator();
                import.Diagnostics.Information($"azd resource '{key}' runs locally on the Service Bus emulator; it provisions Azure Service Bus on publish.", key);
                break;
            case AzureEventHubsResource eventHubs:
                builder.CreateResourceBuilder(eventHubs).RunAsEmulator();
                import.Diagnostics.Information($"azd resource '{key}' runs locally on the Event Hubs emulator; it provisions Azure Event Hubs on publish.", key);
                break;
        }
    }

    // The existing Azure resource group comes from the customer's .azure environment (a concrete value).
    // The azure.yaml resourceGroup may be an azd expandable string (for example "rg-${AZURE_ENV_NAME}"),
    // so resolve it against the environment before using it as a fallback. If it still cannot be resolved
    // to a concrete value (no environment, or an unresolved "${...}" token remains), it is meaningless to
    // Aspire as a deployment target, so it is ignored.
    private static string? ResolveResourceGroup(AzdEnvironment? environment, AzdProject project)
    {
        if (!string.IsNullOrEmpty(environment?.ResourceGroup))
        {
            return environment.ResourceGroup;
        }

        var resourceGroup = ExpandAzdExpandableString(project.ResourceGroup, environment);

        // A remaining '$' means an azd token (either "${VAR}" or the bare "$VAR" form) could not be
        // resolved against the environment. Azure resource group names never contain '$', so treat any
        // leftover token as unresolved and ignore the value rather than passing a bogus target (for example
        // "rg-$UNKNOWN") to provisioning.
        if (string.IsNullOrEmpty(resourceGroup) || resourceGroup.Contains('$', StringComparison.Ordinal))
        {
            return null;
        }

        return resourceGroup;
    }

    // Matches an azd expandable-string token: "${VAR}" or "$VAR". For "${...}" azd uses os.Expand
    // semantics and reads everything up to the closing brace; for the bare form it reads a shell-style
    // name ([A-Za-z_][A-Za-z0-9_]*).
    [GeneratedRegex(@"\$\{(?<name>[^}]*)\}|\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex AzdExpandableStringRegex();

    // azd treats several azure.yaml fields (notably resourceGroup and service env values) as
    // "expandable strings" and resolves "${VAR}" / "$VAR" against the selected environment before use,
    // e.g. `resourceGroup: rg-${AZURE_ENV_NAME}`. Mirror that resolution against the .azure environment's
    // recorded values so a migrated project targets the same resource group / values azd would have.
    //
    // azd uses os.Expand semantics, where a missing variable expands to an empty string. We deviate in
    // one deliberate, safe way: an unknown variable is left as its literal "${VAR}" token rather than
    // blanked, so we never silently empty an adopter's value when a variable is not recorded in the
    // environment. In practice the AZURE_* variables referenced from azure.yaml are always written to
    // .azure/<env>/.env by azd, so they resolve; only genuinely unknown tokens are preserved.
    // See osutil.ExpandableString.Envsubst in https://github.com/Azure/azure-dev.
    [return: NotNullIfNotNull(nameof(value))]
    private static string? ExpandAzdExpandableString(string? value, AzdEnvironment? environment)
    {
        if (string.IsNullOrEmpty(value) || environment is null || !value.Contains('$', StringComparison.Ordinal))
        {
            return value;
        }

        return AzdExpandableStringRegex().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;

            if (environment.Values.TryGetValue(name, out var resolved))
            {
                return resolved;
            }

            // azd always writes AZURE_ENV_NAME into the environment and it equals the environment's
            // directory name, so resolve it from Name even if the key is absent from .env.
            if (string.Equals(name, "AZURE_ENV_NAME", StringComparison.Ordinal))
            {
                return environment.Name;
            }

            return match.Value;
        });
    }

    // Reuse the azd environment for Azure provisioning by seeding two configuration shapes from the same
    // recorded values, so a migrated app targets the same subscription/region/resource group azd already
    // used instead of re-prompting or creating a parallel environment. Values the app host author already
    // set are preserved.
    //
    // The two shapes feed different parts of the Azure hosting stack:
    //   - Azure:*        binds into AzureProvisionerOptions and is what BaseProvisioningContextProvider
    //                    reads to pick the subscription/location/resource group during run-mode
    //                    provisioning and `aspire deploy`.
    //   - Parameters:*   backs the AzureEnvironmentResource that AddAzureProvisioning always adds (its
    //                    Location/ResourceGroupName/PrincipalId are ParameterResources named "location",
    //                    "resourceGroupName", and "principalId"; see AzureEnvironmentResourceExtensions.
    //                    AddAzureEnvironment). That resource is the canonical deployment target the
    //                    generated main.bicep is parameterized on. Without these, resolving those
    //                    parameters throws MissingParameterValueException, so a grown-up app would prompt
    //                    for (or fail on) values azd already knew at `aspire publish`/`aspire deploy` time.
    private static void ApplyAzureEnvironment(
        IDistributedApplicationBuilder builder,
        AzdProject project,
        AzdEnvironment? environment,
        AzdImportOptions options,
        AzdImport import)
    {
        if (!options.ReuseAzureEnvironment || environment is null)
        {
            return;
        }

        var resourceGroup = ResolveResourceGroup(environment, project);

        var settings = new Dictionary<string, string?>();

        AddConfigIfMissing(builder, settings, "Azure:SubscriptionId", environment.SubscriptionId);
        AddConfigIfMissing(builder, settings, "Azure:Location", environment.Location);
        AddConfigIfMissing(builder, settings, "Azure:ResourceGroup", resourceGroup);
        AddConfigIfMissing(builder, settings, "Azure:TenantId", environment.TenantId);

        // Populate the AzureEnvironmentResource parameters from the same azd values. The parameter names
        // must match those AddAzureEnvironment creates.
        AddConfigIfMissing(builder, settings, "Parameters:location", environment.Location);
        AddConfigIfMissing(builder, settings, "Parameters:resourceGroupName", resourceGroup);
        AddConfigIfMissing(builder, settings, "Parameters:principalId", environment.PrincipalId);

        if (settings.Count == 0)
        {
            return;
        }

        builder.Configuration.AddInMemoryCollection(settings);

        import.Diagnostics.Information(
            $"Reusing the azd environment '{environment.Name}' for Azure provisioning ({string.Join(", ", settings.Keys)}). Run 'AddAzdProject' with ReuseAzureEnvironment disabled to opt out.");
    }

    private static void AddConfigIfMissing(
        IDistributedApplicationBuilder builder,
        Dictionary<string, string?> settings,
        string key,
        string? value)
    {
        // Don't clobber a value the app host author already configured (config, user secrets, env vars).
        if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(builder.Configuration[key]))
        {
            settings[key] = value;
        }
    }

    private static void ImportServices(
        IDistributedApplicationBuilder builder,
        AzdImport import,
        AzdProject project,
        AzdEnvironment? environment,
        string projectDirectory,
        AzdImportOptions options,
        HashSet<string> usedNames)
    {
        // Compute environments are created on demand the first time a service needs one.
        IResourceBuilder<IComputeEnvironmentResource>? acaEnvironment = null;
        IResourceBuilder<IComputeEnvironmentResource>? appServiceEnvironment = null;

        IResourceBuilder<IComputeEnvironmentResource>? GetComputeEnvironment(AzdService service)
        {
            if (!options.CreateComputeEnvironments)
            {
                return null;
            }

            switch (service.Host?.ToLowerInvariant())
            {
                case "containerapp":
                    return acaEnvironment ??= builder.AddAzureContainerAppEnvironment(options.ContainerAppEnvironmentName);
                // azd's `function` host is Microsoft.Web/sites. Only .NET function services are imported as
                // Azure Functions projects (the Aspire Functions integration is .NET-only), and their supported
                // Aspire compute target is Azure Container Apps. Non-.NET function services fall through to the
                // container/JS paths and retain azd's original unmapped behavior, so they are not placed on a
                // compute environment here.
                case "function" when IsDotNet(service):
                    return acaEnvironment ??= builder.AddAzureContainerAppEnvironment(options.ContainerAppEnvironmentName);
                case "appservice":
                    return appServiceEnvironment ??= builder.AddAzureAppServiceEnvironment(options.AppServiceEnvironmentName);
                default:
                    return null;
            }
        }

        foreach (var (key, service) in project.Services)
        {
            var serviceName = AllocateUniqueName(BuiltInResourceMappers.Sanitize(key), usedNames, import, key);

            if (!TryCreateServiceResource(builder, import, service, serviceName, projectDirectory, key, out var resourceBuilder))
            {
                continue;
            }

            ConfigureService(builder, resourceBuilder, service, environment, GetComputeEnvironment(service), import, key);
            import.AddService(key, resourceBuilder.AsGeneric());
        }
    }

    private static bool TryCreateServiceResource(
        IDistributedApplicationBuilder builder,
        AzdImport import,
        AzdService service,
        string serviceName,
        string projectDirectory,
        string serviceKey,
        [NotNullWhen(true)] out IResourceBuilder<IComputeResource>? resourceBuilder)
    {
        resourceBuilder = null;

        var serviceSource = service.Project is null
            ? projectDirectory
            : Path.GetFullPath(Path.Combine(projectDirectory, service.Project));

        // azd's `project` usually names a directory but may point directly at a project file. The service's
        // base directory (used to resolve docker context/paths and the fallback Dockerfile) is then its parent.
        var serviceBaseDirectory = File.Exists(serviceSource)
            ? Path.GetDirectoryName(serviceSource)!
            : serviceSource;

        // 1. A prebuilt image deploys directly as a container.
        if (!string.IsNullOrEmpty(service.Image))
        {
            resourceBuilder = builder.AddContainer(serviceName, service.Image);
            return true;
        }

        // 2. An explicit docker block builds from a Dockerfile.
        if (service.Docker is not null)
        {
            var context = service.Docker.Context is null
                ? serviceBaseDirectory
                : Path.GetFullPath(Path.Combine(serviceBaseDirectory, service.Docker.Context));

            // azd resolves docker.path relative to the service directory, whereas AddDockerfile resolves
            // the Dockerfile path relative to the build context. Resolve to an absolute path so the two
            // agree even when the context is not the service directory.
            var dockerfilePath = string.IsNullOrEmpty(service.Docker.Path)
                ? null
                : Path.GetFullPath(Path.Combine(serviceBaseDirectory, service.Docker.Path));

            // Pass the docker target as the build stage so multi-stage Dockerfiles build the right stage.
            resourceBuilder = builder.AddDockerfile(serviceName, context, dockerfilePath, service.Docker.Target);
            return true;
        }

        // 3. A .NET service whose azd host is `function` becomes an Azure Functions project so the Functions
        //    host model and its generated infrastructure are preserved instead of being flattened to a generic
        //    compute app. azd runs Functions on Microsoft.Web/sites; the Aspire Functions integration deploys
        //    to Azure Container Apps (its supported compute target), which ConfigureService applies. That
        //    integration is .NET-specific, so non-.NET function apps fall through to the paths below.
        if (IsFunctionHost(service) && IsDotNet(service) && TryResolveProjectFile(serviceSource, out var functionProjectFile))
        {
            resourceBuilder = builder.AddAzureFunctionsProject(serviceName, functionProjectFile);
            import.Diagnostics.Warning(
                $"Service '{serviceKey}' uses azd host 'function'. It was imported as an Azure Functions project deployed to Azure Container Apps (the Aspire Functions compute target), whereas azd hosts Functions on Microsoft.Web/sites. Verify Container Apps hosting is acceptable.",
                serviceKey);
            return true;
        }

        // 4. A .NET project becomes an Aspire project resource.
        if (IsDotNet(service) && TryResolveProjectFile(serviceSource, out var projectFile))
        {
            resourceBuilder = builder.AddProject(serviceName, projectFile);
            return true;
        }

        // 5. A JavaScript or TypeScript service becomes a native Aspire JavaScript app. It runs locally via
        //    the project's package manager (npm) and is containerized automatically on publish, which mirrors
        //    how azd builds js/ts services for `host: containerapp`/`appservice` while giving a first-class
        //    local run experience instead of requiring a container build.
        if (IsJavaScript(service))
        {
            var runScript = DetectJavaScriptRunScript(serviceBaseDirectory);
            var jsApp = builder.AddJavaScriptApp(serviceName, serviceBaseDirectory, runScript);

            // AddJavaScriptApp always configures a "build" build-script, which suits apps that compile or
            // bundle (Vite, tsc). Many azd Node services instead execute their sources directly through a
            // runtime loader (e.g. tsx) and define no "build" script, so the auto-generated Dockerfile's
            // `npm run build` would fail the image build. Drop the default build-script unless the service's
            // package.json actually defines one.
            if (!JavaScriptScriptExists(serviceBaseDirectory, "build"))
            {
                foreach (var buildScriptAnnotation in jsApp.Resource.Annotations.OfType<JavaScriptBuildScriptAnnotation>().ToList())
                {
                    jsApp.Resource.Annotations.Remove(buildScriptAnnotation);
                }
            }

            // azd services targeted at a web compute host (containerapp/appservice) are HTTP apps, so give
            // the app an HTTP endpoint whose assigned port is published via the PORT environment variable.
            // That mirrors azd's ACA/App Service ingress (which routes to a target port) and matches the
            // convention Node web servers use to choose their listen port. A service with no host is treated
            // as a web app too, since that is the common case for an adopted azd service.
            if (service.Host is null || IsKnownComputeHost(service.Host))
            {
                jsApp.WithHttpEndpoint(env: "PORT");

                // azd gives a scaffolded `host: containerapp`/`appservice` service public (external) ingress by
                // default when azure.yaml declares no explicit ingress override: the Container App is created
                // with `ingress { external: true }` and azd writes a public `SERVICE_<NAME>_ENDPOINT_URL`. An
                // Aspire HTTP endpoint defaults to internal-only ingress on publish, so mark the endpoint
                // external to preserve the same reachability the service had under azd (otherwise a migrated
                // front end would silently become unreachable from the internet).
                jsApp.WithExternalHttpEndpoints();

                // azd builds and runs a js/ts compute service as a long-lived server container. By default an
                // Aspire JavaScript app publishes as a build-only image (intended for a static front end that
                // is consumed by another resource via PublishWithContainerFiles), which is excluded from
                // publish and deploy. Configure a standalone runtime that runs the production package script
                // with production node_modules present, so the imported service is containerized and deployed
                // the same way azd would run it. Prefer the production "start" script; otherwise reuse the
                // detected run script (azd has no separate run-script concept).
                var publishScript = JavaScriptScriptExists(serviceBaseDirectory, "start") ? "start" : runScript;
#pragma warning disable ASPIREJAVASCRIPT001 // PublishAsPackageScript is experimental and subject to change.
                jsApp.PublishAsPackageScript(publishScript);
#pragma warning restore ASPIREJAVASCRIPT001
            }

            resourceBuilder = jsApp;
            import.Diagnostics.Information(
                $"Service '{serviceKey}' (language '{service.Language}') was imported as a JavaScript app running the '{runScript}' script.",
                serviceKey);
            return true;
        }

        // 6. Fall back to a Dockerfile in the service directory for other non-.NET languages.
        var fallbackDockerfile = Path.Combine(serviceBaseDirectory, "Dockerfile");
        if (File.Exists(fallbackDockerfile))
        {
            resourceBuilder = builder.AddDockerfile(serviceName, serviceBaseDirectory);
            import.Diagnostics.Information(
                $"Service '{serviceKey}' (language '{service.Language ?? "unknown"}') was imported from its Dockerfile.",
                serviceKey);
            return true;
        }

        import.Diagnostics.Warning(
            $"Service '{serviceKey}' could not be imported automatically (host '{service.Host ?? "unknown"}', language '{service.Language ?? "unknown"}'). Map it manually using AddProject, AddContainer, or AddDockerfile.",
            serviceKey);
        return false;
    }

    private static void ConfigureService(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<IComputeResource> resourceBuilder,
        AzdService service,
        AzdEnvironment? environment,
        IResourceBuilder<IComputeEnvironmentResource>? computeEnvironment,
        AzdImport import,
        string serviceKey)
    {
        if (computeEnvironment is not null)
        {
            resourceBuilder.WithComputeEnvironment(computeEnvironment);
        }
        else if (service.Host is { } host && !IsKnownComputeHost(host))
        {
            import.Diagnostics.Warning(
                $"azd host '{host}' is not yet supported for automatic compute placement. Service '{serviceKey}' was imported without a compute environment.",
                serviceKey);
        }

        // Compute resources implement IResourceWithEnvironment; recreate a builder typed to that
        // interface so environment values can be applied regardless of the concrete resource type.
        if (service.Env.Count > 0 && resourceBuilder.Resource is IResourceWithEnvironment environmentResource)
        {
            var environmentBuilder = builder.CreateResourceBuilder(environmentResource);
            foreach (var (name, value) in service.Env)
            {
                // azd resolves "${VAR}"/"$VAR" in service env values against the environment, so do the same.
                environmentBuilder.WithEnvironment(name, ExpandAzdExpandableString(value, environment));
            }
        }
    }

    private static void WireUses(IDistributedApplicationBuilder builder, AzdImport import, AzdProject project)
    {
        foreach (var (serviceKey, service) in project.Services)
        {
            if (service.Uses.Count == 0 || !import.Services.TryGetValue(serviceKey, out var consumerResource))
            {
                continue;
            }

            // The consuming resource must accept environment/configuration injection.
            if (consumerResource.Resource is not IResourceWithEnvironment consumerWithEnv)
            {
                continue;
            }

            var consumer = builder.CreateResourceBuilder(consumerWithEnv);

            foreach (var used in service.Uses)
            {
                if (!import.TryResolveReference(used, out var target))
                {
                    import.Diagnostics.Warning(
                        $"Service '{serviceKey}' declares a dependency on '{used}', which was not imported. The reference was skipped.",
                        serviceKey);
                    continue;
                }

                switch (target.Resource)
                {
                    case IResourceWithConnectionString connectionStringResource:
                        consumer.WithReference(builder.CreateResourceBuilder(connectionStringResource));
                        break;

                    case IResourceWithServiceDiscovery serviceDiscoveryResource:
                        consumer.WithReference(builder.CreateResourceBuilder(serviceDiscoveryResource));
                        break;

                    default:
                        // The resource was imported but exposes no Aspire reference surface (for example
                        // AzureStorageResource is IResourceWithEndpoints, not IResourceWithConnectionString).
                        // This is unsafe to deploy: the consuming service will be missing the dependency at
                        // runtime, so report it as an error rather than a quiet note.
                        import.Diagnostics.Error(
                            $"Service '{serviceKey}' uses '{used}', but the imported resource exposes neither a connection string nor service discovery, so no reference could be wired. Reference the appropriate child resource (for example a storage blob service) manually.",
                            serviceKey);
                        break;
                }

                // azd injects a well-known set of environment variables into the consumer for each
                // `uses` edge; Aspire's WithReference uses different (connection-string) conventions.
                // Surface the azd names so a customer whose app code reads them knows what to preserve
                // (e.g. with WithEnvironment) rather than discovering the change at runtime.
                if (project.Resources.TryGetValue(used, out var usedResource))
                {
                    var azdEnvironmentVariables = BuiltInResourceMappers.GetAzdInjectedEnvironmentVariables(usedResource.Type);
                    if (azdEnvironmentVariables.Count > 0)
                    {
                        import.Diagnostics.Information(
                            $"In azd, service '{serviceKey}' received {string.Join(", ", azdEnvironmentVariables)} from '{used}'. Aspire wires this dependency with WithReference using its own connection conventions; if your application code reads the azd variable names, add WithEnvironment to preserve them.",
                            serviceKey);
                    }
                }
            }
        }
    }

    // azd also supports resource-level `uses` (resources[].uses) for resource-to-resource edges, which in
    // azd drive connection-setting injection and role assignments on the consuming resource. The importer
    // wires service `uses` (see WireUses) but does not reproduce resource-to-resource wiring yet. Surface it
    // as a diagnostic rather than dropping it silently, consistent with the importer's "nothing is lost
    // without a diagnostic" contract, so a migrating customer can re-establish the dependency.
    private static void ReportUnwiredResourceUses(AzdImport import, AzdProject project)
    {
        foreach (var (resourceKey, resource) in project.Resources)
        {
            if (resource.Uses.Count == 0)
            {
                continue;
            }

            import.Diagnostics.Warning(
                $"azd resource '{resourceKey}' declares uses [{string.Join(", ", resource.Uses)}], a resource-to-resource dependency that the importer does not wire automatically. In azd this injects connection settings (and role assignments) on '{resourceKey}'; re-establish it manually (for example with WithReference or an explicit role assignment).",
                resourceKey);
        }
    }

    private static void PreserveInfrastructure(AzdImport import, AzdProject project)
    {
        if (!AzdInfrastructureLocator.TryLocate(import.ProjectDirectory, project, out var infraPath, out var provider, out var infraRelativePath))
        {
            return;
        }

        import.InfraPath = infraPath;

        import.Diagnostics.Information(
            $"Existing azd infrastructure ('{provider}') at '{infraRelativePath}' is preserved. It is not regenerated by the import; continue to manage it with your existing deployment workflow.");

        // Aspire models infrastructure as Bicep. A Terraform infra folder is preserved on disk, but
        // Aspire cannot provision it, so flag that explicitly rather than implying it will be deployed.
        if (string.Equals(provider, "terraform", StringComparison.OrdinalIgnoreCase))
        {
            import.Diagnostics.Warning(
                $"The azd infrastructure at '{infraRelativePath}' uses Terraform, which Aspire does not provision. The files are preserved; continue to deploy them with azd.");
        }
    }

    private static string ResolveAzureYamlPath(IDistributedApplicationBuilder builder, string? azureYamlPath)
    {
        // Candidate filenames azd recognizes, in priority order.
        string[] candidateFileNames = ["azure.yaml", "azure.yml"];

        if (string.IsNullOrEmpty(azureYamlPath))
        {
            foreach (var candidate in candidateFileNames)
            {
                var probe = Path.Combine(builder.AppHostDirectory, candidate);
                if (File.Exists(probe))
                {
                    return probe;
                }
            }

            throw new FileNotFoundException(
                $"Could not find 'azure.yaml' in the app host directory '{builder.AppHostDirectory}'. Pass an explicit path to AddAzdProject.");
        }

        var fullPath = Path.IsPathRooted(azureYamlPath)
            ? azureYamlPath
            : Path.GetFullPath(Path.Combine(builder.AppHostDirectory, azureYamlPath));

        if (Directory.Exists(fullPath))
        {
            foreach (var candidate in candidateFileNames)
            {
                var probe = Path.Combine(fullPath, candidate);
                if (File.Exists(probe))
                {
                    return probe;
                }
            }

            throw new FileNotFoundException($"Could not find 'azure.yaml' in directory '{fullPath}'.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The azd project file '{fullPath}' does not exist.", fullPath);
        }

        return fullPath;
    }

    private static string AllocateUniqueName(string sanitized, HashSet<string> usedNames, AzdImport import, string azdKey)
    {
        if (usedNames.Add(sanitized))
        {
            return sanitized;
        }

        // Two azd keys can sanitize to the same Aspire resource name (for example "foo.bar" and
        // "foo-bar"), and services share the namespace with resources. Disambiguate deterministically so
        // neither asset is dropped and AppHost construction does not throw on a duplicate name.
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{sanitized}-{suffix}";
            if (usedNames.Add(candidate))
            {
                import.Diagnostics.Warning(
                    $"azd name '{azdKey}' maps to Aspire resource name '{sanitized}', which is already in use. It was imported as '{candidate}' to avoid a collision.",
                    azdKey);
                return candidate;
            }
        }
    }

    private static bool IsDotNet(AzdService service)
    {
        if (service.Language is { } language &&
            (language.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("fsharp", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // azd often omits `language` for .NET services and infers it from a project file reference.
        return service.Project is { } project &&
               (project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || project.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFunctionHost(AzdService service)
        => service.Host is { } host && host.Equals("function", StringComparison.OrdinalIgnoreCase);

    private static bool IsJavaScript(AzdService service)
    {
        // azd's schema uses the short forms `js`/`ts` for Node services; accept the spelled-out forms too.
        return service.Language is { } language &&
            (language.Equals("js", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("ts", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("javascript", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("typescript", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("node", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("nodejs", StringComparison.OrdinalIgnoreCase));
    }

    // Picks the npm script the JavaScript app host should run. azure.yaml does not record a run script, so
    // prefer a conventional "start" script, then "dev", and otherwise fall back to "start". The script is
    // resolved again at launch time, so a missing script surfaces as a normal run error rather than here.
    private static string DetectJavaScriptRunScript(string serviceDirectory)
    {
        if (JavaScriptScriptExists(serviceDirectory, "start"))
        {
            return "start";
        }

        if (JavaScriptScriptExists(serviceDirectory, "dev"))
        {
            return "dev";
        }

        return "start";
    }

    // Returns true when the service's package.json defines the named npm script.
    private static bool JavaScriptScriptExists(string serviceDirectory, string scriptName)
    {
        var packageJsonPath = Path.Combine(serviceDirectory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return false;
        }

        try
        {
            // package.json shape we read:
            //   { "scripts": { "start": "node server.js", "dev": "tsx watch src/server.ts", "build": "tsc" } }
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return document.RootElement.TryGetProperty("scripts", out var scripts) &&
                   scripts.ValueKind == JsonValueKind.Object &&
                   scripts.TryGetProperty(scriptName, out _);
        }
        catch (JsonException)
        {
            // A malformed package.json is the customer's to fix; treat the script as absent.
            return false;
        }
    }

    private static bool TryResolveProjectFile(string serviceDirectory, [NotNullWhen(true)] out string? projectFile)
    {
        projectFile = null;

        // The azd `project` value can point directly at a project file or at a directory containing one.
        if (File.Exists(serviceDirectory) &&
            (serviceDirectory.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || serviceDirectory.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
        {
            projectFile = serviceDirectory;
            return true;
        }

        if (!Directory.Exists(serviceDirectory))
        {
            return false;
        }

        projectFile = Directory.EnumerateFiles(serviceDirectory, "*.csproj").FirstOrDefault()
            ?? Directory.EnumerateFiles(serviceDirectory, "*.fsproj").FirstOrDefault();

        return projectFile is not null;
    }

    private static bool IsKnownComputeHost(string host)
        => host.Equals("containerapp", StringComparison.OrdinalIgnoreCase)
           || host.Equals("appservice", StringComparison.OrdinalIgnoreCase);
}
