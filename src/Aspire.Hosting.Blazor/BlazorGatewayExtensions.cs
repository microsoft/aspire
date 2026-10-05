// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // DockerfileBuilder is experimental
#pragma warning disable ASPIRECSHARPAPPS001 // AddCSharpApp is experimental
#pragma warning disable ASPIREDOTNETPROJECT001 // AddDotnetProject is experimental
#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental
#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchArgsOverrideAnnotation is experimental

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Blazor WebAssembly apps and gateway resources.
/// </summary>
[Experimental("ASPIREBLAZOR001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class BlazorGatewayExtensions
{
    private static readonly string s_blazorGatewayCliVersion = GetAssemblyMetadataValue("BlazorGatewayCliVersion");
    private static readonly string s_blazorSdkImageTag = GetAssemblyMetadataValue("BlazorSdkImageTag");
    private static readonly string s_blazorGatewayAspNetImageTag = GetAssemblyMetadataValue("BlazorGatewayAspNetImageTag");
    private const string DotNetSdkImageRepo = "mcr.microsoft.com/dotnet/sdk";
    private const string DotNetAspNetImageRepo = "mcr.microsoft.com/dotnet/aspnet";
    private const string BlazorGatewayCliPackageId = "Microsoft.AspNetCore.Components.Gateway.Cli";

    /// <summary>
    /// Registers the built-in Blazor Gateway.
    /// During development the gateway runs from the official .NET tool. Publishing continues
    /// to use the file-based app shipped with this package.
    /// </summary>
    /// <remarks>
    /// Development requires a .NET SDK compatible with the configured gateway tool package.
    /// When publishing attached Blazor WebAssembly apps, each client must target a single framework
    /// supported by this package. The client's selected SDK must be available locally and is
    /// installed in the build image. The AppHost and client projects must also
    /// be contained by a common ancestor directory with a <c>.sln</c> or <c>.slnx</c> file; that
    /// directory is used as the Docker build context.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<ProjectResource> AddBlazorGateway(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        var gatewayPath = GetScriptPath("Gateway.cs");
        var gateway = builder.AddCSharpApp(name, gatewayPath)
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        ConfigureGatewayToolForRunMode(gateway, builder.AppHostDirectory, builder.Environment.EnvironmentName);
        if (builder.ExecutionContext.IsPublishMode)
        {
            var gatewayDir = Path.GetDirectoryName(gatewayPath)!;

            gateway.PublishAsDockerFile(container =>
            {
                container.WithDockerfileBuilder(gatewayDir, ctx =>
                {
                    var logger = ctx.Services.GetService<ILogger<BlazorWasmAppResource>>();

                    ctx.Builder
                        .From($"{DotNetSdkImageRepo}:{s_blazorSdkImageTag}", "build")
                        .WorkDir("/src")
                        .Copy("Gateway.cs", ".")
                        .Run("dotnet publish Gateway.cs -c Release -o /app/publish");

                    ctx.Builder.AddContainerFilesStages(ctx.Resource, logger);

                    ctx.Builder
                        .From($"{DotNetAspNetImageRepo}:{s_blazorGatewayAspNetImageTag}")
                        .WorkDir("/app")
                        .CopyFrom("build", "/app/publish", ".")
                        .AddContainerFiles(ctx.Resource, "/app", logger)
                        .Entrypoint(["dotnet", "Gateway.dll"]);
                });
            });
        }

        return gateway;
    }

    /// <summary>
    /// Registers the built-in Blazor Gateway backed by an experimental <see cref="DotnetProjectResource"/>
    /// from <c>Aspire.Hosting.Dotnet</c> (the run/watch-capable .NET resource), rather than the
    /// <see cref="ProjectResource"/> used by <see cref="AddBlazorGateway"/>.
    /// </summary>
    /// <remarks>
    /// During development, the gateway runs from the official .NET tool. Publishing uses the
    /// file-based <c>Gateway.cs</c> app shipped with this package. The development and publish
    /// prerequisites are the same as for <see cref="AddBlazorGateway"/>.
    /// </remarks>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the gateway resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the gateway resource.</returns>
    /// <ats-summary>Adds the built-in Blazor gateway as a .NET program resource.</ats-summary>
    /// <ats-param name="builder">The distributed application builder.</ats-param>
    /// <ats-param name="name">The name of the gateway resource.</ats-param>
    /// <ats-returns>The gateway resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<DotnetProjectResource> AddDotnetProjectBlazorGateway(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        var gatewayPath = GetScriptPath("Gateway.cs");
        var gateway = builder.AddDotnetProject(name, gatewayPath)
            .WithHttpEndpoint()
            .WithHttpsEndpoint();

        ConfigureGatewayToolForRunMode(gateway, builder.AppHostDirectory, builder.Environment.EnvironmentName);

        return gateway;
    }

    private static void ConfigureGatewayToolForRunMode<TGateway>(
        IResourceBuilder<TGateway> gateway,
        string workingDirectory,
        string environmentName)
        where TGateway : class, IResourceWithArgs
    {
        if (!gateway.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return;
        }

        // Keep the project-shaped resource and its dashboard/endpoint behavior, but force process
        // execution so IDEs do not launch the file-based app represented by the project metadata.
        gateway
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Project",
                Properties = [
                    new(CustomResourceKnownProperties.Source, string.Empty)
                ]
            })
            .WithAnnotation(new ForceProcessExecutionAnnotation())
            .WithLaunchToolArgs(context =>
            {
                context.Args.Add("tool");
                context.Args.Add("exec");
                context.Args.Add(BlazorGatewayCliPackageId);
                context.Args.Add("--version");
                context.Args.Add(s_blazorGatewayCliVersion);
                context.Args.Add("--yes");
                context.Args.Add("--");
            }, showInCommandLine: false)
            .WithArgs(
                "--environment", environmentName,
                "--Logging:LogLevel:Microsoft=Warning",
                "--Logging:LogLevel:Microsoft.Hosting.Lifetime=Information",
                "--Logging:LogLevel:System.Net.Http.HttpClient.OtlpExporter=Warning")
            .WithRequiredCommand("dotnet");

        if (gateway.Resource is ProjectResource)
        {
            gateway
                .WithAnnotation(new ExecutableAnnotation
                {
                    Command = "dotnet",
                    WorkingDirectory = workingDirectory
                })
                .WithAnnotation(new ProjectLaunchArgsOverrideAnnotation(["run"]));
        }
        else if (gateway.Resource is ExecutableResource executableResource)
        {
            var executableAnnotation = executableResource.Annotations.OfType<ExecutableAnnotation>().Last();
            executableAnnotation.Command = "dotnet";
            executableAnnotation.WorkingDirectory = workingDirectory;
            executableAnnotation.WorkingDirectoryExplicitlySet = true;
        }
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
        [ResourceName] string name)
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
    [AspireExport("addBlazorWasmProject")]
    public static IResourceBuilder<BlazorWasmAppResource> AddBlazorWasmApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
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
    /// <remarks>
    /// For development and publish prerequisites, see <see cref="AddBlazorGateway"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<ProjectResource> WithBlazorClientApp(
        this IResourceBuilder<ProjectResource> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
        bool proxyTelemetry = true)
        => gateway.WithBlazorClientAppCore(wasmApp, apiPrefix, otlpPrefix, proxyTelemetry);

    /// <summary>
    /// Attaches a Blazor WebAssembly app to a <see cref="DotnetProjectResource"/>-backed Gateway created via
    /// <see cref="AddDotnetProjectBlazorGateway"/>. Behaves identically to
    /// <see cref="WithBlazorClientApp(IResourceBuilder{ProjectResource}, IResourceBuilder{BlazorWasmAppResource}, string, string, bool)"/>.
    /// </summary>
    /// <param name="gateway">The gateway resource builder.</param>
    /// <param name="wasmApp">The Blazor WebAssembly app to attach to the gateway.</param>
    /// <param name="apiPrefix">The URL path prefix for API proxy routes. Defaults to <c>"_api"</c>.</param>
    /// <param name="otlpPrefix">The URL path prefix for OTLP proxy routes. Defaults to <c>"_otlp"</c>.</param>
    /// <param name="proxyTelemetry"><see langword="true"/> to expose the OTLP proxy for the client app; otherwise, <see langword="false"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for the gateway resource.</returns>
    /// <ats-summary>
    /// Attaches a Blazor WebAssembly app to the gateway. The app's resource name becomes its URL path prefix,
    /// and its service references are forwarded to the gateway for proxying.
    /// </ats-summary>
    /// <ats-param name="gateway">The gateway resource builder.</ats-param>
    /// <ats-param name="wasmApp">The Blazor WebAssembly app to attach to the gateway.</ats-param>
    /// <ats-param name="apiPrefix">The URL path prefix for API proxy routes. The default is <c>"_api"</c>.</ats-param>
    /// <ats-param name="otlpPrefix">The URL path prefix for telemetry proxy routes. The default is <c>"_otlp"</c>.</ats-param>
    /// <ats-param name="proxyTelemetry"><see langword="true"/> to expose the telemetry proxy for the client app; otherwise, <see langword="false"/>.</ats-param>
    /// <ats-returns>The gateway resource builder.</ats-returns>
    /// <remarks>
    /// For development and publish prerequisites, see <see cref="AddBlazorGateway"/>.
    /// </remarks>
    [AspireExport("withDotnetProjectBlazorClientApp", MethodName = "withBlazorClientApp")]
    public static IResourceBuilder<DotnetProjectResource> WithBlazorClientApp(
        this IResourceBuilder<DotnetProjectResource> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
        bool proxyTelemetry = true)
        => gateway.WithBlazorClientAppCore(wasmApp, apiPrefix, otlpPrefix, proxyTelemetry);

    private static IResourceBuilder<TGateway> WithBlazorClientAppCore<TGateway>(
        this IResourceBuilder<TGateway> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string apiPrefix,
        string otlpPrefix,
        bool proxyTelemetry)
        where TGateway : class, IResourceWithServiceDiscovery, IResourceWithEnvironment
    {
        var pathPrefix = wasmApp.Resource.Name;

        // Read endpoint references from EndpointReferenceAnnotation (added by WithReference).
        // Filter to only resources that support service discovery (i.e., actual services like weatherapi,
        // not parameters or connection strings).
        var referencedServices = GetServiceDiscoveryReferences(wasmApp.Resource);

        // Auto-forward service references to the gateway so YARP can resolve service endpoints
        // via Aspire's service discovery (services__{name}__{scheme}__{index} env vars).
        // Skip if the gateway already references this service. Preserve specific endpoint names
        // from the original annotation so only the intended endpoints are forwarded.
        var existingGatewayRefs = GetReferencedResourceNames(gateway.Resource);

        foreach (var endpointRef in referencedServices)
        {
            if (!existingGatewayRefs.Contains(endpointRef.Resource.Name))
            {
                ForwardEndpointReference(gateway, endpointRef);
            }
        }

        // Make the WASM app a child of the gateway so the orchestrator mirrors lifecycle
        // state (Running, Stopped, etc.) from the gateway to this resource automatically.
        wasmApp.Resource.Parent = gateway.Resource;

        // Build GatewayAppService instances from the endpoint reference annotations.
        var services = BuildGatewayAppServices(referencedServices);

        gateway.WithBlazorApp(wasmApp, pathPrefix, services, apiPrefix, otlpPrefix, proxyTelemetry);

        // Register browser debugging support: create a hidden child debugger resource
        // parented to the gateway, and a "Debug in Browser" command on the WASM app resource.
        if (!gateway.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            BrowserDebuggerHelper.AddBrowserDebuggerResource(
                gateway.ApplicationBuilder,
                gateway.Resource,
                wasmApp,
                wasmApp.Resource.ProjectPath,
                relativePath: pathPrefix,
                browser: wasmApp.Resource.DebuggerBrowser);
        }

        return gateway;
    }

    /// <summary>
    /// Configures the browser launched when starting a debug session for the Blazor WebAssembly app.
    /// The value is read when the debugger is registered on the gateway, so call this before
    /// <see cref="WithBlazorClientApp(IResourceBuilder{ProjectResource}, IResourceBuilder{BlazorWasmAppResource}, string, string, bool)"/>
    /// or <see cref="WithBlazorClientApp(IResourceBuilder{DotnetProjectResource}, IResourceBuilder{BlazorWasmAppResource}, string, string, bool)"/>
    /// attaches the app.
    /// </summary>
    /// <param name="wasmApp">The Blazor WebAssembly app resource builder.</param>
    /// <param name="browser">The browser to use for debugging. Defaults to <c>"msedge"</c>. Supported values include <c>"msedge"</c> and <c>"chrome"</c>.</param>
    [AspireExport]
    public static IResourceBuilder<BlazorWasmAppResource> WithBlazorDebuggerBrowser(
        this IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string browser = "msedge")
    {
        wasmApp.Resource.DebuggerBrowser = browser;
        return wasmApp;
    }

    /// <summary>
    /// Attaches a Blazor WebAssembly app to a Gateway project resource at the given path prefix.
    /// At orchestration time, each app is built, its manifests are discovered via MSBuild properties,
    /// transformed (AssetFile prefixed, runtime tree wrapped under prefix), then injected
    /// into the Gateway as environment variables.
    /// </summary>
    [AspireExportIgnore(Reason = "Internal open-generic implementation helper; polyglot AppHosts use the exported WithBlazorClientApp methods.")]
    internal static IResourceBuilder<TGateway> WithBlazorApp<TGateway>(
        this IResourceBuilder<TGateway> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string pathPrefix,
        GatewayAppService[] services,
        string apiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
        string otlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
        bool proxyTelemetry = true)
        where TGateway : class, IResourceWithServiceDiscovery, IResourceWithEnvironment
    {
        var registration = new GatewayAppRegistration(wasmApp, pathPrefix, services, apiPrefix, otlpPrefix, proxyTelemetry);

        // Get or create the annotation on the gateway resource
        var annotation = GetOrAddGatewayAppsAnnotation(gateway.Resource);

        var gatewayOutputRoot = Path.Combine(
            GetBlazorStorePath(gateway.ApplicationBuilder), "gateways", gateway.Resource.Name);

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

                // Resolve the HTTP OTLP endpoint for WASM client proxying.
                // WASM clients use HTTP/protobuf (not gRPC), so we need the HTTP endpoint.
                // First try to resolve from the dashboard resource model (handles randomized ports
                // and isolated mode). Fall back to configuration for cases where the dashboard
                // resource isn't in the model (e.g. external dashboard).
                var httpOtlpEndpointUrl = ResolveHttpOtlpEndpointUrl(context, gateway.ApplicationBuilder.Configuration);

                if (httpOtlpEndpointUrl is null && registeredApps.Any(a => a.ProxyBlazorTelemetry))
                {
                    context.Logger.LogWarning(
                        "OTLP telemetry proxying was requested but no dashboard HTTP endpoint could be resolved. " +
                        "WASM client telemetry will not be forwarded.");
                }

                if (context.ExecutionContext.IsPublishMode)
                {
                    ConfigurePublishEnvironment(context, registeredApps, gatewayEndpoint, httpGatewayEndpoint);
                    return;
                }

                // Clean up stale output from previous runs (but preserve the scripts subdir).
                var outputDir = Path.Combine(gatewayOutputRoot, "output");
                if (Directory.Exists(outputDir))
                {
                    Directory.Delete(outputDir, recursive: true);
                }

                Directory.CreateDirectory(outputDir);

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

                GatewayConfigurationBuilder.EmitProxyConfiguration(context.EnvironmentVariables, registeredApps, gatewayEndpoint, httpGatewayEndpoint, httpOtlpEndpointUrl);
            });
        }

        annotation.Apps.Add(registration);

        if (gateway.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            CreatePublishCompanion(gateway, wasmApp, pathPrefix);
        }

        return gateway;
    }

    private static ProjectInfo GetProjectInfo(string projectPath, string appHostDirectory)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var solutionRoot = GetSolutionRoot(appHostDirectory, projectDir);
        var relativeProjectPath = Path.GetRelativePath(solutionRoot, projectDir)
            .Replace('\\', '/');
        return new ProjectInfo(solutionRoot, relativeProjectPath);
    }

    internal static string GetSolutionRoot(string appHostDirectory, string projectDirectory)
    {
        for (var directory = new DirectoryInfo(appHostDirectory); directory is not null; directory = directory.Parent)
        {
            if (ContainsPath(directory.FullName, projectDirectory)
                && (directory.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any()
                    || directory.EnumerateFiles("*.slnx", SearchOption.TopDirectoryOnly).Any()))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Publishing the Blazor WebAssembly project '{projectDirectory}' requires a .sln or .slnx file " +
            $"in an ancestor of the AppHost directory '{appHostDirectory}' that also contains the client project. " +
            "This boundary is used as the Docker build context.");

        static bool ContainsPath(string parentPath, string childPath)
        {
            var relativePath = Path.GetRelativePath(parentPath, childPath);
            return !Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }

    private static void MirrorGatewayStateToClients<TGateway>(IResourceBuilder<TGateway> gateway)
        where TGateway : class, IResource
    {
        // Subscribe to the gateway's InitializeResourceEvent to start a background watcher
        // that mirrors state changes from the gateway to all registered WASM app resources.
        // This mirrors the pattern used by ApplicationOrchestrator.SetChildResourceAsync for
        // container children, but uses ResourceNotificationService.WatchAsync since the
        // orchestrator does not propagate state for the gateway resource's parents.
        gateway.ApplicationBuilder.Eventing.Subscribe<InitializeResourceEvent>(gateway.Resource, (e, ct) =>
        {
            var notificationService = e.Notifications;
            _ = Task.Run(() => WatchGatewayStateAsync(gateway.Resource, notificationService, ct), ct);
            return Task.CompletedTask;
        });
    }

    private static async Task WatchGatewayStateAsync<TGateway>(
        TGateway gateway,
        ResourceNotificationService notificationService,
        CancellationToken cancellationToken)
        where TGateway : class, IResource
    {
        await foreach (var resourceEvent in notificationService.WatchAsync(cancellationToken).ConfigureAwait(false))
        {
            if (resourceEvent.Resource != gateway)
            {
                continue;
            }

            var registeredApps = GetRegisteredApps(gateway);
            var gatewayState = resourceEvent.Snapshot.State;

            var isRunning = gatewayState?.Text == KnownResourceStates.Running;

            foreach (var reg in registeredApps)
            {
                var clientUrls = isRunning
                    ? BuildClientUrls(GetAllocatedEndpoints(gateway), reg.PathPrefix)
                    : [];

                await notificationService.PublishUpdateAsync(reg.AppBuilder.Resource, snapshot => snapshot with
                {
                    State = gatewayState,
                    StartTimeStamp = resourceEvent.Snapshot.StartTimeStamp,
                    StopTimeStamp = resourceEvent.Snapshot.StopTimeStamp,
                    Urls = clientUrls
                }).ConfigureAwait(false);
            }
        }
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

    private static void CreatePublishCompanion<TGateway>(
        IResourceBuilder<TGateway> gateway,
        IResourceBuilder<BlazorWasmAppResource> wasmApp,
        string pathPrefix)
        where TGateway : class, IResource
    {
        var publishResourceName = $"{wasmApp.Resource.Name}publish";
        var project = GetProjectInfo(wasmApp.Resource.ProjectPath, gateway.ApplicationBuilder.AppHostDirectory);
        var relativeProjectPath = Path.GetRelativePath(
            project.SolutionRoot, wasmApp.Resource.ProjectPath).Replace('\\', '/');

        // Copy PrefixEndpoints.cs into .aspire/scripts/ within the solution root so it's
        // included in the Docker build context.
        var scriptSource = GetScriptPath("PrefixEndpoints.cs");
        var scriptDest = Path.Combine(project.SolutionRoot, ".aspire", "scripts", "PrefixEndpoints.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptDest)!);
        File.Copy(scriptSource, scriptDest, overwrite: true);
        var scriptRelativePath = Path.GetRelativePath(project.SolutionRoot, scriptDest)
            .Replace('\\', '/');

        var companion = gateway.ApplicationBuilder.AddResource(
            new BlazorWasmPublishResource(publishResourceName))
            .WithImage("placeholder")
            .WithContainerFilesSource("/app/output");

        companion.WithDockerfileFactory(project.SolutionRoot, async context =>
        {
            ILogger logger = context.Services.GetService<ILogger<BlazorWasmAppResource>>() is { } typedLogger
                ? typedLogger
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            var properties = await BlazorWasmAppBuilder.GetPublishPropertiesAsync(
                wasmApp.Resource.ProjectPath,
                logger,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unable to determine publish properties for '{wasmApp.Resource.ProjectPath}'.");
            return BuildBlazorWasmPublishDockerfile(relativeProjectPath, scriptRelativePath, pathPrefix, properties.NETCoreSdkVersion, properties.TargetFramework);
        });

        gateway.WithAnnotation(new ContainerFilesDestinationAnnotation
        {
            Source = companion.Resource,
            DestinationPath = "."
        });
    }

    internal static string BuildBlazorWasmPublishDockerfile(
        string relativeProjectPath,
        string scriptRelativePath,
        string pathPrefix,
        string clientSdkVersion,
        string targetFramework)
    {
        var numericVersion = clientSdkVersion.Split('-', 2)[0];
        if (!Version.TryParse(numericVersion, out var version)
            || version.Major is < 8 or > 11
            || clientSdkVersion.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new NotSupportedException($"Publishing a Blazor WebAssembly project with SDK '{clientSdkVersion}' is not supported. Use a .NET SDK from 8 through 11.");
        }

        ValidateBlazorWasmPublishTargetFramework(targetFramework);
        var projectDirectory = Path.GetDirectoryName(relativeProjectPath)?.Replace('\\', '/');
        var projectFileName = Path.GetFileName(relativeProjectPath);
        var containerProjectDirectory = string.IsNullOrEmpty(projectDirectory)
            ? "/src"
            : $"/src/{projectDirectory}";

        return $$"""
            FROM {{DotNetSdkImageRepo}}:{{s_blazorSdkImageTag}} AS build
            RUN curl --fail --show-error --location https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \
                bash /tmp/dotnet-install.sh --version {{clientSdkVersion}} --install-dir /opt/client-dotnet --no-path && \
                rm /tmp/dotnet-install.sh
            WORKDIR /src
            COPY . .
            WORKDIR {{containerProjectDirectory}}
            RUN /opt/client-dotnet/dotnet publish "{{projectFileName}}" -f {{targetFramework}} -c Release -o /app/publish

            # Prefix asset paths and add SPA fallback endpoint
            WORKDIR /tmp
            RUN mkdir -p /app/output/wwwroot/{{pathPrefix}} && \
                cp -r /app/publish/wwwroot/* /app/output/wwwroot/{{pathPrefix}}/ && \
                cp "/src/{{scriptRelativePath}}" /tmp/PrefixEndpoints.cs && \
                dotnet run /tmp/PrefixEndpoints.cs -- \
                    /app/publish/*.staticwebassets.endpoints.json \
                    {{pathPrefix}} \
                    /app/output/{{pathPrefix}}.endpoints.json
            """;
    }

    internal static void ValidateBlazorWasmPublishTargetFramework(string targetFramework)
    {
        const int MaximumSupportedMajorVersion = 11;

        if (targetFramework.Contains(';', StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Publishing a multi-targeted Blazor WebAssembly project ('{targetFramework}') is not supported. Select one target framework.");
        }

        var versionSeparator = targetFramework.IndexOf('.');
        var majorText = versionSeparator > 3
            ? targetFramework.AsSpan(3, versionSeparator - 3)
            : default;
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            || majorText.IsEmpty
            || !int.TryParse(majorText, out var majorVersion)
            || !Version.TryParse(targetFramework[3..], out _)
            || majorVersion > MaximumSupportedMajorVersion)
        {
            throw new NotSupportedException(
                $"Publishing a Blazor WebAssembly project targeting '{targetFramework}' is not supported by the .NET {MaximumSupportedMajorVersion} SDK image.");
        }
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

    private const string AspireStorePathKey = "Aspire:Store:Path";

    /// <summary>
    /// Gets the Blazor-specific store path under the Aspire store directory.
    /// </summary>
    private static string GetBlazorStorePath(IDistributedApplicationBuilder builder)
    {
        var storePath = builder.Configuration[AspireStorePathKey]
            ?? builder.AppHostDirectory;

        return Path.Combine(storePath, ".aspire", "blazor");
    }

    private static List<EndpointReferenceAnnotation> GetServiceDiscoveryReferences(IResource resource)
    {
        // EndpointReferenceAnnotation is added by WithReference and tracks which endpoint
        // resources are referenced and which specific endpoint names were requested.
        return resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Where(a => a.Resource is IResourceWithServiceDiscovery)
            .ToList();
    }

    /// <summary>
    /// Builds <see cref="GatewayAppService"/> instances from endpoint reference annotations.
    /// Each service carries its resource name and any specific endpoint names referenced.
    /// </summary>
    private static GatewayAppService[] BuildGatewayAppServices(List<EndpointReferenceAnnotation> references)
    {
        var services = new GatewayAppService[references.Count];

        for (var i = 0; i < references.Count; i++)
        {
            var annotation = references[i];
            var service = new GatewayAppService(annotation.Resource.Name);

            if (!annotation.UseAllEndpoints)
            {
                foreach (var endpointName in annotation.EndpointNames)
                {
                    service.EndpointNames.Add(endpointName);
                }
            }

            services[i] = service;
        }

        return services;
    }

    private static HashSet<string> GetReferencedResourceNames(IResource resource)
    {
        return resource.Annotations
            .OfType<EndpointReferenceAnnotation>()
            .Select(a => a.Resource.Name)
            .ToHashSet(StringComparers.ResourceName);
    }

    /// <summary>
    /// Forwards an endpoint reference to the gateway. When specific named endpoints are
    /// referenced, each one is forwarded individually (YARP uses the named endpoint format
    /// <c>https+http://_endpointName.serviceName</c>). When all endpoints are referenced,
    /// all endpoints are forwarded so YARP can resolve by scheme.
    /// </summary>
    private static void ForwardEndpointReference<TGateway>(
        IResourceBuilder<TGateway> gateway,
        EndpointReferenceAnnotation endpointRef)
        where TGateway : class, IResourceWithEnvironment
    {
        var svcResource = (IResourceWithServiceDiscovery)endpointRef.Resource;

        if (!endpointRef.UseAllEndpoints)
        {
            // Forward each specific named endpoint. YARP will resolve via
            // https+http://_endpointName.serviceName using these entries.
            foreach (var endpointName in endpointRef.EndpointNames)
            {
                gateway.WithReference(svcResource.GetEndpoint(endpointName));
            }
        }
        else
        {
            // Forward all endpoints so scheme-based resolution works.
            var svcBuilder = gateway.ApplicationBuilder.CreateResourceBuilder(svcResource);
            gateway.WithReference(svcBuilder);
        }
    }

    private static EndpointReference? GetEndpointIfDefined(IResourceWithEndpoints resource, string endpointName)
    {
        var endpoint = resource.GetEndpoint(endpointName);
        return endpoint.Exists ? endpoint : null;
    }

    private static GatewayAppsAnnotation GetOrAddGatewayAppsAnnotation(IResource resource)
    {
        if (resource.TryGetLastAnnotation<GatewayAppsAnnotation>(out var existing))
        {
            return existing;
        }

        var newAnnotation = new GatewayAppsAnnotation();
        resource.Annotations.Add(newAnnotation);
        return newAnnotation;
    }

    private static List<GatewayAppRegistration> GetRegisteredApps(IResource resource)
    {
        if (resource.TryGetLastAnnotation<GatewayAppsAnnotation>(out var apps))
        {
            return apps.Apps;
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

    /// <summary>
    /// Resolves the HTTP OTLP endpoint for proxying browser telemetry to the dashboard.
    /// Tries the dashboard resource model first (handles randomized ports), then falls back
    /// to well-known configuration keys for cases where the dashboard isn't in the model
    /// (e.g. external or standalone dashboard).
    /// </summary>
    internal static object? ResolveHttpOtlpEndpointUrl(EnvironmentCallbackContext context, IConfiguration configuration)
    {
        DistributedApplicationModel? model;
        try
        {
            model = context.ExecutionContext.Services.GetService<DistributedApplicationModel>();
        }
        catch (InvalidOperationException)
        {
            // ServiceProvider may not be available if the container hasn't been built yet.
            model = null;
        }

        if (model is not null
            && model.Resources.TryGetByName("aspire-dashboard", out var resource)
            && resource is IResourceWithEndpoints dashboardResource)
        {
            var httpEndpoint = dashboardResource.GetEndpoint("otlp-http");
            if (httpEndpoint.Exists)
            {
                return httpEndpoint;
            }
        }

        // Fall back to configuration for external dashboard scenarios.
        return (object?)configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"]
            ?? configuration["DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"];
    }

    private readonly struct ProjectInfo(string solutionRoot, string relativeProjectPath)
    {
        public string SolutionRoot { get; } = solutionRoot;
        public string RelativeProjectPath { get; } = relativeProjectPath;
    }

    private static string GetAssemblyMetadataValue(string key)
    {
        return typeof(BlazorGatewayExtensions).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value
            ?? throw new InvalidOperationException($"Assembly metadata '{key}' is required.");
    }

}
