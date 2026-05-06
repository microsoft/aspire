// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // DockerfileBuilder is experimental
#pragma warning disable ASPIRECSHARPAPPS001 // AddCSharpApp is experimental
#pragma warning disable ASPIREATS001 // AspireExportIgnore is experimental

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Blazor WebAssembly apps and gateway resources.
/// </summary>
public static class BlazorGatewayExtensions
{
    /// <summary>
    /// Registers the built-in Blazor Gateway as a file-based C# app.
    /// The gateway is shipped as Gateway.cs alongside this library and launched
    /// via <c>AddCSharpApp</c>. No separate project is needed.
    /// </summary>
    [AspireExportIgnore(Reason = "Blazor gateway APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> AddBlazorGateway(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        var gatewayPath = GetGatewayScriptPath();
        var gateway = builder.AddCSharpApp(name, gatewayPath)
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        if (builder.ExecutionContext.IsPublishMode)
        {
            var gatewayDir = Path.GetDirectoryName(gatewayPath)!;

            gateway.PublishAsDockerFile(container =>
            {
                container.WithDockerfileBuilder(gatewayDir, ctx =>
                {
                    ctx.Builder
                        .From("mcr.microsoft.com/dotnet/sdk:10.0", "build")
                        .WorkDir("/src")
                        .Copy("Gateway.cs", ".")
                        .Run("dotnet publish Gateway.cs -c Release -o /app/publish");

                    ctx.Builder.AddContainerFilesStages(ctx.Resource, logger: null);

                    ctx.Builder
                        .From("mcr.microsoft.com/dotnet/aspnet:10.0")
                        .WorkDir("/app")
                        .CopyFrom("build", "/app/publish", ".")
                        .AddContainerFiles(ctx.Resource, "/app", logger: null)
                        .Entrypoint(["dotnet", "Gateway.dll"]);
                });
            });
        }

        return gateway;
    }

    /// <summary>
    /// Registers a Blazor WebAssembly project as a resource using the Aspire-generated
    /// IProjectMetadata type to discover the project path. The resource name becomes the
    /// URL path prefix (e.g., "store" → served at /store/).
    /// Use WithReference() to declare service dependencies.
    /// </summary>
    [AspireExportIgnore(Reason = "Open generic type parameter TProject is not ATS-compatible.")]
    public static IResourceBuilder<BlazorWasmAppResource> AddBlazorWasmProject<TProject>(
        this IDistributedApplicationBuilder builder,
        string name)
        where TProject : IProjectMetadata, new()
    {
        var metadata = new TProject();
        var projectPath = metadata.ProjectPath;
        var resource = new BlazorWasmAppResource(name, projectPath);
        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "BlazorWasmApp",
                State = KnownResourceStates.Waiting,
                Properties = [
                    new(CustomResourceKnownProperties.Source, Path.GetFileName(projectPath))
                ]
            })
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Registers a Blazor WebAssembly project as a resource without launching it as a process.
    /// Prefer AddBlazorWasmProject&lt;TProject&gt; which uses IProjectMetadata for path discovery.
    /// </summary>
    [AspireExportIgnore(Reason = "Blazor gateway APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<BlazorWasmAppResource> AddBlazorWasmApp(
        this IDistributedApplicationBuilder builder,
        string name,
        string projectPath)
    {
        var resolvedPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, projectPath));
        var resource = new BlazorWasmAppResource(name, resolvedPath);
        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "BlazorWasmApp",
                State = KnownResourceStates.Waiting,
                Properties = [
                    new(CustomResourceKnownProperties.Source, Path.GetFileName(resolvedPath))
                ]
            })
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Attaches a Blazor WebAssembly app to the Gateway. The resource name is used as the
    /// URL path prefix (e.g., resource "store" → /store/). Service names are derived from
    /// WithReference() annotations on the WASM resource.
    /// Service references from the WASM app are automatically forwarded to the gateway
    /// so the gateway can resolve service endpoints for YARP proxying.
    /// </summary>
    /// <param name="gateway">The gateway resource builder.</param>
    /// <param name="wasmApp">The Blazor WebAssembly app to attach to the gateway.</param>
    /// <param name="apiPrefix">The URL path prefix for API proxy routes. Defaults to <c>"_api"</c>.</param>
    /// <param name="otlpPrefix">The URL path prefix for OTLP proxy routes. Defaults to <c>"_otlp"</c>.</param>
    /// <param name="proxyTelemetry"><see langword="true"/> to expose the OTLP proxy for the client app; otherwise, <see langword="false"/>.</param>
    [AspireExportIgnore(Reason = "Blazor gateway APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> WithClient(
        this IResourceBuilder<ProjectResource> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
        bool proxyTelemetry = true)
    {
        var pathPrefix = wasmApp.Resource.Name;

        // Read service references from ResourceRelationshipAnnotation (added by WithReference).
        // Filter to only resources that have endpoints (i.e., actual services like weatherapi,
        // not parameters or connection strings).
        var referencedServices = GetServiceDiscoveryReferences(wasmApp.Resource);

        var serviceNames = GetResourceNames(referencedServices);

        // Auto-forward service references to the gateway so YARP can resolve service endpoints
        // via Aspire's service discovery (services__{name}__{scheme}__{index} env vars).
        // Skip if the gateway already references this service.
        var existingGatewayRefs = GetReferencedResourceNames(gateway.Resource);

        foreach (var svcAnnotation in referencedServices)
        {
            if (!existingGatewayRefs.Contains(svcAnnotation.Resource.Name)
                && svcAnnotation.Resource is IResourceWithServiceDiscovery svcResource)
            {
                gateway.WithReference(gateway.ApplicationBuilder.CreateResourceBuilder(svcResource));
            }
        }

        return gateway.WithBlazorApp(wasmApp, pathPrefix, serviceNames, apiPrefix, otlpPrefix, proxyTelemetry);
    }

    /// <summary>
    /// Attaches a Blazor WebAssembly app to a Gateway project resource at the given path prefix.
    /// At orchestration time, each app is built, its manifests are discovered via MSBuild properties,
    /// transformed (AssetFile prefixed, runtime tree wrapped under prefix), then injected
    /// into the Gateway as environment variables.
    /// </summary>
    /// <param name="gateway">The gateway resource builder.</param>
    /// <param name="wasmApp">The Blazor WebAssembly app to serve behind the gateway.</param>
    /// <param name="pathPrefix">The URL path prefix under which the app is served.</param>
    /// <param name="serviceNames">Optional service names to expose to the client through the gateway proxy.</param>
    /// <param name="apiPrefix">The URL path prefix for API proxy routes. Defaults to <c>"_api"</c>.</param>
    /// <param name="otlpPrefix">The URL path prefix for OTLP proxy routes. Defaults to <c>"_otlp"</c>.</param>
    /// <param name="proxyTelemetry"><see langword="true"/> to expose the OTLP proxy for the client app; otherwise, <see langword="false"/>.</param>
    [AspireExportIgnore(Reason = "Blazor gateway APIs are not yet stable for ATS export.")]
    public static IResourceBuilder<ProjectResource> WithBlazorApp(
        this IResourceBuilder<ProjectResource> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string pathPrefix,
        string[]? serviceNames = null,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
        bool proxyTelemetry = true)
    {
        var registration = new GatewayAppRegistration(wasmApp, pathPrefix, serviceNames ?? [], apiPrefix, otlpPrefix, proxyTelemetry);

        // Get or create the annotation on the gateway resource
        var annotation = GetOrAddGatewayAppsAnnotation(gateway.Resource);

        if (!annotation.IsInitialized)
        {
            annotation.IsInitialized = true;
            MirrorGatewayStateToClients(gateway);

            gateway.WithEnvironment(async context =>
            {
                var registeredApps = GetRegisteredApps(gateway.Resource);
                var httpsGatewayEndpoint = GetEndpointIfDefined(gateway.Resource, "https");
                var httpGatewayEndpoint = GetEndpointIfDefined(gateway.Resource, "http");
                var gatewayEndpoint = httpsGatewayEndpoint ?? httpGatewayEndpoint
                    ?? throw new InvalidOperationException($"The gateway '{gateway.Resource.Name}' must define an HTTP or HTTPS endpoint.");

                if (context.ExecutionContext.IsPublishMode)
                {
                    ConfigurePublishEnvironment(context, registeredApps, gatewayEndpoint, httpGatewayEndpoint);
                    return;
                }

                var outputDir = Directory.CreateTempSubdirectory($"aspire-blazor-gateway-{gateway.Resource.Name}-")
                    .FullName;

                var manifests = await BuildAndDiscoverManifestsAsync(registeredApps, context.Logger, context.CancellationToken).ConfigureAwait(false);
                if (manifests == null)
                {
                    return;
                }

                if (!await PrefixAndWriteEndpointsAsync(manifests, outputDir, context).ConfigureAwait(false))
                {
                    return;
                }

                var mergedRuntimePath = Path.Combine(outputDir, "merged.staticwebassets.runtime.json");
                await EndpointsManifestTransformer.MergeRuntimeManifestsAsync(manifests, mergedRuntimePath, context.Logger, context.CancellationToken).ConfigureAwait(false);
                context.EnvironmentVariables["staticWebAssets"] = mergedRuntimePath;

                GatewayConfigurationBuilder.EmitProxyConfiguration(context.EnvironmentVariables, registeredApps, gatewayEndpoint, httpGatewayEndpoint);
            });
        }

        annotation.Apps.Add(registration);

        if (gateway.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            CreatePublishCompanion(gateway, wasmApp, pathPrefix);
        }

        return gateway;
    }

    private static string GetGatewayScriptPath()
    {
        return GetScriptPath("Gateway.cs");
    }

    private static ProjectInfo GetProjectInfo(string projectPath, string appHostDirectory)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var assemblyName = Path.GetFileNameWithoutExtension(projectPath);
        var solutionRoot = Path.GetFullPath(Path.Combine(appHostDirectory, ".."));
        var relativeProjectPath = Path.GetRelativePath(solutionRoot, projectDir)
            .Replace('\\', '/');
        return new ProjectInfo(assemblyName, solutionRoot, relativeProjectPath);
    }

    private static void MirrorGatewayStateToClients(IResourceBuilder<ProjectResource> gateway)
    {
        gateway.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(gateway.Resource, async (e, ct) =>
        {
            var notificationService = e.Services.GetRequiredService<ResourceNotificationService>();
            var registeredApps = GetRegisteredApps(gateway.Resource);
            var now = DateTime.UtcNow;
            var gatewayEndpoints = GetAllocatedEndpoints(gateway.Resource);

            foreach (var reg in registeredApps)
            {
                var urls = BuildClientUrls(gatewayEndpoints, reg.PathPrefix);

                await notificationService.PublishUpdateAsync(reg.AppBuilder.Resource, snapshot => snapshot with
                {
                    State = KnownResourceStates.Running,
                    StartTimeStamp = now,
                    Urls = urls
                }).ConfigureAwait(false);
            }
        });
    }

    private static void ConfigurePublishEnvironment(
        EnvironmentCallbackContext context,
        List<GatewayAppRegistration> apps,
        EndpointReference gatewayEndpoint,
        EndpointReference? httpGatewayEndpoint)
    {
        foreach (var reg in apps)
        {
            var envPrefix = $"ClientApps__{reg.Resource.Name}";
            context.EnvironmentVariables[$"{envPrefix}__PathPrefix"] = reg.PathPrefix;
            context.EnvironmentVariables[$"{envPrefix}__EndpointsManifest"] = $"/app/{reg.PathPrefix}.endpoints.json";
            context.EnvironmentVariables[$"{envPrefix}__ConfigEndpointPath"] = $"{reg.PathPrefix}/_blazor/_configuration";
        }

        GatewayConfigurationBuilder.EmitProxyConfiguration(context.EnvironmentVariables, apps, gatewayEndpoint, httpGatewayEndpoint);
    }

    private static async Task<List<AppManifestPaths>?> BuildAndDiscoverManifestsAsync(
        List<GatewayAppRegistration> apps, ILogger logger, CancellationToken ct)
    {
        var result = new List<AppManifestPaths>();

        foreach (var reg in apps)
        {
            var success = await BlazorWasmAppBuilder.BuildAsync(reg.Resource.ProjectPath, logger, ct).ConfigureAwait(false);
            if (!success)
            {
                BlazorGatewayLog.FailedToBuild(logger, reg.Resource.Name);
                return null;
            }

            var paths = await BlazorWasmAppBuilder.GetManifestPathsAsync(reg.Resource.ProjectPath, logger, ct).ConfigureAwait(false);
            if (paths == null)
            {
                BlazorGatewayLog.FailedToResolveManifests(logger, reg.Resource.Name);
                return null;
            }

            result.Add(new AppManifestPaths(reg, paths.Value.endpointsManifest, paths.Value.runtimeManifest));
            BlazorGatewayLog.DiscoveredManifests(logger,
                reg.Resource.Name, paths.Value.endpointsManifest, paths.Value.runtimeManifest);
        }

        return result;
    }

    private static async Task<bool> PrefixAndWriteEndpointsAsync(
        List<AppManifestPaths> manifests, string outputDir, EnvironmentCallbackContext context)
    {
        foreach (var manifest in manifests)
        {
            var reg = manifest.Registration;
            var srcEndpoints = manifest.EndpointsManifest;

            if (!File.Exists(srcEndpoints))
            {
                BlazorGatewayLog.EndpointsManifestNotFound(context.Logger, srcEndpoints);
                return false;
            }

            var modifiedEndpoints = await EndpointsManifestTransformer.PrefixEndpointsAssetFileAsync(
                srcEndpoints, reg.PathPrefix, context.CancellationToken).ConfigureAwait(false);
            var destEndpoints = Path.Combine(outputDir, $"{reg.Resource.Name}.endpoints.json");
            await File.WriteAllTextAsync(destEndpoints, modifiedEndpoints, context.CancellationToken).ConfigureAwait(false);

            BlazorGatewayLog.WrotePrefixedEndpoints(context.Logger, reg.Resource.Name, destEndpoints);

            var envPrefix = $"ClientApps__{reg.Resource.Name}";
            context.EnvironmentVariables[$"{envPrefix}__PathPrefix"] = reg.PathPrefix;
            context.EnvironmentVariables[$"{envPrefix}__EndpointsManifest"] = destEndpoints;
            context.EnvironmentVariables[$"{envPrefix}__ConfigEndpointPath"] = $"{reg.PathPrefix}/_blazor/_configuration";
        }

        return true;
    }

    private static void CreatePublishCompanion(
        IResourceBuilder<ProjectResource> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string pathPrefix)
    {
        var publishResourceName = $"{wasmApp.Resource.Name}publish";
        var project = GetProjectInfo(wasmApp.Resource.ProjectPath, gateway.ApplicationBuilder.AppHostDirectory);
        var relativeProjectPath = Path.GetRelativePath(
            project.SolutionRoot, wasmApp.Resource.ProjectPath).Replace('\\', '/');

        // Copy the PrefixEndpoints.cs script into a project-local build folder so it's
        // available inside the Docker build context without clobbering the solution root.
        var scriptSource = GetScriptPath("PrefixEndpoints.cs");
        var scriptRelativePath = Path.Combine(project.RelativeProjectPath, "obj", "Aspire.Hosting.Blazor", "PrefixEndpoints.cs")
            .Replace('\\', '/');
        var scriptDest = Path.Combine(project.SolutionRoot, scriptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(scriptDest)!);
        File.Copy(scriptSource, scriptDest, overwrite: true);

        var companion = gateway.ApplicationBuilder.AddResource(
            new BlazorWasmPublishResource(publishResourceName))
            .WithImage("placeholder")
            .WithContainerFilesSource("/app/output");

        companion.WithDockerfileFactory(project.SolutionRoot, ctx =>
        {
            return $$"""
                FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
                WORKDIR /src
                COPY . .
                RUN dotnet publish "{{relativeProjectPath}}" -c Release -o /app/publish

                # Prefix asset paths and add SPA fallback endpoint
                RUN mkdir -p /app/output/wwwroot/{{pathPrefix}} && \
                    cp -r /app/publish/wwwroot/* /app/output/wwwroot/{{pathPrefix}}/ && \
                    dotnet run "{{scriptRelativePath}}" -- \
                        /app/publish/*.staticwebassets.endpoints.json \
                        {{pathPrefix}} \
                        /app/output/{{pathPrefix}}.endpoints.json
                """;
        });

        gateway.WithAnnotation(new ContainerFilesDestinationAnnotation
        {
            Source = companion.Resource,
            DestinationPath = "."
        });
    }

    private static string GetScriptPath(string scriptName)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(BlazorGatewayExtensions).Assembly.Location)!;
        var scriptPath = Path.Combine(assemblyDir, "Scripts", scriptName);

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                $"{scriptName} not found at '{scriptPath}'. Ensure the Aspire.Hosting.Blazor package includes the file as content.");
        }

        return scriptPath;
    }

    private static List<ResourceRelationshipAnnotation> GetServiceDiscoveryReferences(IResource resource)
    {
        var references = new List<ResourceRelationshipAnnotation>();
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is ResourceRelationshipAnnotation rel
                && rel.Type == "Reference"
                && rel.Resource is IResourceWithServiceDiscovery)
            {
                references.Add(rel);
            }
        }
        return references;
    }

    private static string[] GetResourceNames(List<ResourceRelationshipAnnotation> references)
    {
        var names = new string[references.Count];
        for (var i = 0; i < references.Count; i++)
        {
            names[i] = references[i].Resource.Name;
        }
        return names;
    }

    private static HashSet<string> GetReferencedResourceNames(IResource resource)
    {
        var names = new HashSet<string>();
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is ResourceRelationshipAnnotation rel)
            {
                names.Add(rel.Resource.Name);
            }
        }
        return names;
    }

    private static EndpointReference? GetEndpointIfDefined(IResourceWithEndpoints resource, string endpointName)
    {
        var endpoint = resource.GetEndpoint(endpointName);
        return endpoint.Exists ? endpoint : null;
    }

    private static GatewayAppsAnnotation GetOrAddGatewayAppsAnnotation(IResource resource)
    {
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is GatewayAppsAnnotation existing)
            {
                return existing;
            }
        }

        var newAnnotation = new GatewayAppsAnnotation();
        resource.Annotations.Add(newAnnotation);
        return newAnnotation;
    }

    private static List<GatewayAppRegistration> GetRegisteredApps(IResource resource)
    {
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is GatewayAppsAnnotation apps)
            {
                return apps.Apps;
            }
        }

        throw new InvalidOperationException("GatewayAppsAnnotation not found on resource.");
    }

    private static List<EndpointAnnotation> GetAllocatedEndpoints(IResource resource)
    {
        var endpoints = new List<EndpointAnnotation>();
        foreach (var annotation in resource.Annotations)
        {
            if (annotation is EndpointAnnotation ep && ep.AllocatedEndpoint is not null)
            {
                endpoints.Add(ep);
            }
        }
        return endpoints;
    }

    private static ImmutableArray<UrlSnapshot> BuildClientUrls(
        List<EndpointAnnotation> endpoints, string pathPrefix)
    {
        var builder = ImmutableArray.CreateBuilder<UrlSnapshot>(endpoints.Count);
        foreach (var ep in endpoints)
        {
            builder.Add(new UrlSnapshot(
                Name: ep.Name,
                Url: $"{ep.AllocatedEndpoint!.UriString}/{pathPrefix}",
                IsInternal: false));
        }
        return builder.MoveToImmutable();
    }

    private readonly struct ProjectInfo(string assemblyName, string solutionRoot, string relativeProjectPath)
    {
        public string AssemblyName { get; } = assemblyName;
        public string SolutionRoot { get; } = solutionRoot;
        public string RelativeProjectPath { get; } = relativeProjectPath;
    }
}
