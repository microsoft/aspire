// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREEXTENSION001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding JavaScript applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class JavaScriptHostingExtensions
{
    private const string BrowserCapability = "browser";
    private const string DefaultNodeVersion = "22";
    private const string DefaultYarpImage = Yarp.YarpContainerImageTags.Registry + "/" + Yarp.YarpContainerImageTags.Image + ":" + Yarp.YarpContainerImageTags.Tag;

    // This is the order of config files that Vite will look for by default
    // See https://github.com/vitejs/vite/blob/main/packages/vite/src/node/constants.ts#L97
    private static readonly string[] s_defaultConfigFiles = ["vite.config.js", "vite.config.mjs", "vite.config.ts", "vite.config.cjs", "vite.config.mts", "vite.config.cts"];

    // The token to replace with the relative path to the user's Vite config file
    private const string AspireViteRelativeConfigToken = "%%ASPIRE_VITE_RELATIVE_CONFIG_PATH%%";

    // The token to replace with the absolute path to the original Vite config file
    private const string AspireViteAbsoluteConfigToken = "%%ASPIRE_VITE_ABSOLUTE_CONFIG_PATH%%";

    // A template Vite config that loads an existing config provides a default https configuration if one isn't present
    // Uses environment variables to configure a TLS certificate in PFX format and its password if specified
    // The value of %%ASPIRE_VITE_RELATIVE_CONFIG_PATH%% is replaced with the path to the user's actual Vite config file at runtime
    // Vite only supports module style config files, so we don't have to handle commonjs style imports or exports here
    private const string AspireViteConfig = """
    import { defineConfig } from 'vite'
    import config from '%%ASPIRE_VITE_RELATIVE_CONFIG_PATH%%'

    console.log('Applying Aspire specific Vite configuration for HTTPS support.')
    console.log('Found original Vite configuration at "%%ASPIRE_VITE_ABSOLUTE_CONFIG_PATH%%"')

    const aspireHttpsConfig = process.env['TLS_CONFIG_PFX'] ? {
        pfx: process.env['TLS_CONFIG_PFX'],
        passphrase: process.env['TLS_CONFIG_PASSWORD'],
    } : undefined

    const wrapConfig = (innerConfig) => ({
        ...innerConfig,
        server: {
            ...innerConfig.server,
            https: innerConfig.server?.https ?? aspireHttpsConfig,
        }
    })

    let finalConfig = config
    try {
        if (typeof config === 'function') {
            finalConfig = defineConfig((cfg) => {
                let innerConfig = config(cfg)

                return wrapConfig(innerConfig)
            });
        } else if (typeof config === 'object' && config !== null) {
            let innerConfig = config
            finalConfig = defineConfig(wrapConfig(innerConfig))
        } else {
            console.warn('Unexpected Vite config format. Falling back to original configuration without Aspire HTTPS modifications.')
            finalConfig = config
        }
    } catch {
        console.warn('Error applying Aspire Vite configuration. Falling back to original configuration without Aspire HTTPS modifications.')
        finalConfig = config
    }

    export default finalConfig
    """;

    /// <summary>
    /// Adds a node application to the application model. Node should be available on the PATH.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the node application.</param>
    /// <param name="scriptPath">The path to the script relative to the app directory to run.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method executes a Node script directly using <c>node script.js</c>. If you want to use a package manager
    /// you can add one and configure the install and run scripts using the provided extension methods.
    ///
    /// If the application directory contains a <c>package.json</c> file, npm will be added as the default package manager.
    /// </remarks>
    /// <example>
    /// Add a Node app to the application model using yarn and 'yarn run dev' for running during development:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddNodeApp("frontend", "../frontend", "app.js")
    ///        .WithYarn()
    ///        .WithRunScript("dev");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a Node.js application resource")]
    public static IResourceBuilder<NodeAppResource> AddNodeApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        appDirectory = Path.GetFullPath(appDirectory, builder.AppHostDirectory);
        var resource = new NodeAppResource(name, "node", appDirectory);

        var resourceBuilder = builder.AddResource(resource)
            .WithNodeDefaults()
            .WithArgs(c =>
            {
                // If the JavaScriptRunScriptAnnotation is present, use that to run the app
                if (c.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runCommand) &&
                    c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                {
                    if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
                    {
                        c.Args.Add(packageManager.ScriptCommand);
                    }

                    c.Args.Add(runCommand.ScriptName);

                    foreach (var arg in runCommand.Args)
                    {
                        c.Args.Add(arg);
                    }
                }
                else
                {
                    c.Args.Add(scriptPath);
                }
            })
            .WithIconName("CodeJsRectangle")
            .PublishAsDockerFile(c =>
            {
                // Only generate a Dockerfile if one doesn't already exist in the app directory
                if (File.Exists(Path.Combine(resource.WorkingDirectory, "Dockerfile")))
                {
                    return;
                }

                c.WithDockerfileBuilder(resource.WorkingDirectory, dockerfileContext =>
                {
                    var defaultBaseImage = new Lazy<string>(() => GetDefaultBaseImage(appDirectory, "alpine", dockerfileContext.Services));

                    // Get custom base image from annotation, if present
                    dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);

                    var baseBuildImage = baseImageAnnotation?.BuildImage ?? defaultBaseImage.Value;
                    var builderStage = dockerfileContext.Builder
                        .From(baseBuildImage, "build")
                        .EmptyLine()
                        .WorkDir("/app");

                    if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                    {
                        // Initialize the Docker build stage with package manager-specific setup commands.
                        // This allows package managers to add prerequisite commands (e.g., enabling pnpm via corepack)
                        // before package installation and build steps.
                        packageManager.InitializeDockerBuildStage?.Invoke(builderStage);

                        var copiedAllSource = false;
                        if (resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                        {
                            // Copy package files first for better layer caching
                            if (packageManager.PackageFilesPatterns.Count > 0)
                            {
                                foreach (var packageFilePattern in packageManager.PackageFilesPatterns)
                                {
                                    builderStage.Copy(packageFilePattern.Source, packageFilePattern.Destination);
                                }
                            }
                            else
                            {
                                builderStage.Copy(".", ".");
                                copiedAllSource = true;
                            }

                            builderStage.AddInstallCommand(packageManager, installCommand);
                        }

                        if (!copiedAllSource)
                        {
                            // Copy application source code after dependencies are installed
                            builderStage.Copy(".", ".");
                        }

                        if (resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            var commandArgs = new List<string>() { packageManager.ExecutableName };
                            if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
                            {
                                commandArgs.Add(packageManager.ScriptCommand);
                            }
                            commandArgs.Add(buildCommand.ScriptName);
                            commandArgs.AddRange(buildCommand.Args);

                            builderStage.EmptyLine()
                                .Run(string.Join(' ', commandArgs));
                        }
                    }
                    else
                    {
                        // No package manager, just copy everything
                        builderStage.Copy(".", ".");
                    }

                    var logger = dockerfileContext.Services.GetService<ILogger<JavaScriptAppResource>>();
                    dockerfileContext.Builder.AddContainerFilesStages(dockerfileContext.Resource, logger);

                    var baseRuntimeImage = baseImageAnnotation?.RuntimeImage ?? defaultBaseImage.Value;
                    var runtimeBuilder = dockerfileContext.Builder
                        .From(baseRuntimeImage, "runtime")
                            .EmptyLine()
                            .WorkDir("/app")
                            .CopyFrom("build", "/app", "/app")
                            .AddContainerFiles(dockerfileContext.Resource, "/app", logger)
                            .EmptyLine()
                            .Env("NODE_ENV", "production")
                            .EmptyLine()
                            .User("node")
                            .EmptyLine()
                            .Entrypoint([resource.Command, scriptPath]);
                });
            });

        // Configure pipeline to ensure container file sources are built first
        resourceBuilder.WithPipelineConfiguration(context =>
        {
            if (resourceBuilder.Resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out var containerFilesAnnotations))
            {
                var buildSteps = context.GetSteps(resourceBuilder.Resource, WellKnownPipelineTags.BuildCompute);

                foreach (var containerFile in containerFilesAnnotations)
                {
                    buildSteps.DependsOn(context.GetSteps(containerFile.Source, WellKnownPipelineTags.BuildCompute));
                }
            }
        });

        if (File.Exists(Path.Combine(appDirectory, "package.json")))
        {
            // Automatically add npm as the package manager if a package.json file exists
            resourceBuilder.WithNpm();
        }

        resourceBuilder.WithVSCodeDebugging(scriptPath);

        if (builder.ExecutionContext.IsRunMode)
        {
            builder.OnBeforeStart((_, _) =>
            {
                // set the command to the package manager executable if the JavaScriptRunScriptAnnotation is present
                if (resourceBuilder.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out _) &&
                    resourceBuilder.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                {
                    resourceBuilder.WithCommand(packageManager.ExecutableName);
                }

                return Task.CompletedTask;
            });
        }

        return resourceBuilder;
    }

    private static IResourceBuilder<TResource> WithNodeDefaults<TResource>(this IResourceBuilder<TResource> builder) where TResource : JavaScriptAppResource =>
        builder.WithOtlpExporter()
            .WithRequiredCommand("node", "https://nodejs.org/en/download/")
            .WithEnvironment("NODE_ENV", builder.ApplicationBuilder.Environment.IsDevelopment() ? "development" : "production")
            .WithCertificateTrustConfiguration((ctx) =>
            {
                if (ctx.Scope == CertificateTrustScope.Append)
                {
                    ctx.EnvironmentVariables["NODE_EXTRA_CA_CERTS"] = ctx.CertificateBundlePath;
                }
                else
                {
                    if (ctx.EnvironmentVariables.TryGetValue("NODE_OPTIONS", out var existingOptionsObj))
                    {
                        ctx.EnvironmentVariables["NODE_OPTIONS"] = existingOptionsObj switch
                        {
                            // Attempt to append to existing NODE_OPTIONS if possible, otherwise overwrite
                            string s when !string.IsNullOrEmpty(s) => $"{s} --use-openssl-ca",
                            ReferenceExpression re => ReferenceExpression.Create($"{re} --use-openssl-ca"),
                            _ => "--use-openssl-ca",
                        };
                    }
                    else
                    {
                        ctx.EnvironmentVariables["NODE_OPTIONS"] = "--use-openssl-ca";
                    }
                }

                return Task.CompletedTask;
            });

    /// <summary>
    /// Adds a JavaScript application resource to the distributed application using the specified app directory and
    /// run script.
    /// </summary>
    /// <param name="builder">The distributed application builder to which the JavaScript application resource will be added.</param>
    /// <param name="name">The unique name of the JavaScript application resource. Cannot be null or empty.</param>
    /// <param name="appDirectory">The path to the directory containing the JavaScript application.</param>
    /// <param name="runScriptName">The name of the npm script to run when starting the application. Defaults to "dev". Cannot be null or empty.</param>
    /// <returns>A resource builder for the newly added JavaScript application resource.</returns>
    /// <remarks>
    /// If a Dockerfile does not exist in the application's directory, one will be generated
    /// automatically when publishing. The method configures the resource with Node.js defaults and sets up npm
    /// integration.
    /// </remarks>
    [AspireExport(Description = "Adds a JavaScript application resource")]
    public static IResourceBuilder<JavaScriptAppResource> AddJavaScriptApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string runScriptName = "dev")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);
        ArgumentException.ThrowIfNullOrEmpty(runScriptName);

        appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, appDirectory));
        var resource = new JavaScriptAppResource(name, "npm", appDirectory);

        return builder.CreateDefaultJavaScriptAppBuilder(resource, appDirectory, runScriptName);
    }

    /// <summary>
    /// Configures the JavaScript application to publish as a standalone static website served by YARP.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript resource type.</typeparam>
    /// <param name="builder">The JavaScript resource builder.</param>
    /// <param name="configure">Optional callback to configure <see cref="PublishAsStaticWebsiteOptions"/>.</param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The published container uses a YARP reverse proxy image for static file serving.
    /// To add an API reverse-proxy, use the overload that accepts an <c>apiPath</c> and <c>apiTarget</c>.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Use the polyglot-compatible overload instead.")]
    public static IResourceBuilder<TResource> PublishAsStaticWebsite<TResource>(
        this IResourceBuilder<TResource> builder,
        Action<PublishAsStaticWebsiteOptions>? configure = null)
        where TResource : JavaScriptAppResource
    {
        var options = new PublishAsStaticWebsiteOptions();
        configure?.Invoke(options);
        return PublishAsStaticWebsiteCore(builder, null, null, options);
    }

    /// <summary>
    /// Configures the JavaScript application to publish as a standalone static website served by YARP,
    /// with an API reverse-proxy to the specified resource.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript resource type.</typeparam>
    /// <param name="builder">The JavaScript resource builder.</param>
    /// <param name="apiPath">
    /// A path prefix to reverse-proxy to a backend API. For example, <c>/api</c> proxies all requests
    /// matching <c>/api/{"{**catch-all}"}</c> to the backend resource.
    /// </param>
    /// <param name="apiTarget">
    /// The backend resource to proxy API requests to. YARP uses service discovery to resolve the
    /// appropriate endpoint, preferring HTTPS when available.
    /// </param>
    /// <param name="configure">Optional callback to configure <see cref="PublishAsStaticWebsiteOptions"/>.</param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The published container uses a YARP reverse proxy image for static file serving and API
    /// reverse-proxy. YARP natively supports HTTPS backends and service discovery, so API proxy requests
    /// work correctly across all deployment targets (Docker Compose, Azure App Service, etc.).
    /// </para>
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Use the polyglot-compatible overload instead.")]
    public static IResourceBuilder<TResource> PublishAsStaticWebsite<TResource>(
        this IResourceBuilder<TResource> builder,
        string apiPath,
        IResourceBuilder<IResourceWithServiceDiscovery> apiTarget,
        Action<PublishAsStaticWebsiteOptions>? configure = null)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(apiTarget);
        var options = new PublishAsStaticWebsiteOptions();
        configure?.Invoke(options);
        return PublishAsStaticWebsiteCore(builder, apiPath, apiTarget, options);
    }

    /// <summary>
    /// Polyglot-compatible overload. All parameters are optional so the TS codegen wraps them
    /// in a single options object rather than positional args.
    /// </summary>
#pragma warning disable ASPIREEXPORT009 // Polyglot entry point — collision is intentional
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("publishAsStaticWebsite", Description = "Publishes the JavaScript application as a standalone static website using YARP.")]
    internal static IResourceBuilder<TResource> PublishAsStaticWebsitePolyglot<TResource>(
#pragma warning restore ASPIREEXPORT009
        this IResourceBuilder<TResource> builder,
        string? apiPath = null,
        IResourceBuilder<IResourceWithServiceDiscovery>? apiTarget = null,
        string outputPath = "dist",
        bool stripPrefix = false,
        string? targetEndpointName = null)
        where TResource : JavaScriptAppResource
    {
        var options = new PublishAsStaticWebsiteOptions
        {
            OutputPath = outputPath,
            StripPrefix = stripPrefix,
            TargetEndpointName = targetEndpointName
        };
        return PublishAsStaticWebsiteCore(builder, apiPath, apiTarget, options);
    }

    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    private static IResourceBuilder<TResource> PublishAsStaticWebsiteCore<TResource>(
        IResourceBuilder<TResource> builder,
        string? apiPath,
        IResourceBuilder<IResourceWithServiceDiscovery>? apiTarget,
        PublishAsStaticWebsiteOptions options)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(options.OutputPath);

        if (apiPath is not null && apiTarget is null)
        {
            throw new ArgumentException("apiTarget is required when apiPath is specified.", nameof(apiTarget));
        }

        if (apiTarget is not null && apiPath is null)
        {
            throw new ArgumentException("apiPath is required when apiTarget is specified.", nameof(apiPath));
        }

        if (apiPath is not null && apiTarget is not null)
        {
            if (!apiPath.StartsWith('/'))
            {
                throw new ArgumentException("The apiPath must start with '/'.", nameof(apiPath));
            }

            apiPath = apiPath.TrimEnd('/');

            if (apiPath.Length == 0)
            {
                throw new ArgumentException("The apiPath must not be '/' — it would match all requests and make the static site unreachable.", nameof(apiPath));
            }

            ValidateApiPath(apiPath);
            builder.WithReference(apiTarget);
        }

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // YARP listens on port 5000 by default in the base image, so configure an endpoint for that port
        // and set ASPNETCORE_URLS to ensure Kestrel listens on the correct port as well for static file serving and API reverse-proxy to work correctly.
        builder.WithEndpoint("http", e => e.TargetPort = 5000, createIfNotExists: true);

        var annotation = new JavaScriptPublishModeAnnotation(JavaScriptPublishMode.StaticWebsite)
        {
            OutputPath = options.OutputPath,
        };

        builder.WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables["YARP_ENABLE_STATIC_FILES"] = "true";

            if (apiPath is not null && apiTarget is not null)
            {
                // Resolve the destination address — use a specific endpoint if configured, otherwise service discovery
                var destinationAddress = options.TargetEndpointName is not null
                    ? apiTarget.Resource.GetEndpoint(options.TargetEndpointName)
                    : (object)BuildServiceDiscoveryUrl(apiTarget.Resource);

                ctx.EnvironmentVariables["REVERSEPROXY__ROUTES__api__CLUSTERID"] = "api";
                ctx.EnvironmentVariables["REVERSEPROXY__ROUTES__api__MATCH__PATH"] = $"{apiPath}/{{**catch-all}}";
                ctx.EnvironmentVariables["REVERSEPROXY__CLUSTERS__api__DESTINATIONS__destination1__ADDRESS"] = destinationAddress;

                if (options.StripPrefix)
                {
                    ctx.EnvironmentVariables["REVERSEPROXY__ROUTES__api__TRANSFORMS__0__PATHREMOVEPREFIX"] = apiPath;
                }
            }
        });

        builder.WithAnnotation(annotation)
               .ClearContainerFilesSources()
               .WithContainerFilesSource(GetContainerFilesSourcePath(options.OutputPath))
               .WithOtlpExporter();

        if (builder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            dockerfileBuildAnnotation.HasEntrypoint = true;
        }

        return builder;
    }

    /// <summary>
    /// Configures the JavaScript application to publish as a standalone Node.js server that runs a built artifact directly.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript resource type.</typeparam>
    /// <param name="builder">The JavaScript resource builder.</param>
    /// <param name="entryPoint">
    /// The relative path to the Node.js entry point to execute in the published container after the build completes,
    /// such as <c>.output/server/index.mjs</c> or <c>build/index.js</c>.
    /// </param>
    /// <param name="outputPath">
    /// The relative path containing the built runtime files to copy into the published container. Defaults to the application root.
    /// </param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Use this method for frameworks that produce a Node.js server artifact during the build and recommend
    /// running that artifact directly in production rather than invoking a package manager script at runtime.
    /// The application source is still built using the configured package manager and build script; this method
    /// only changes the publish-time runtime container shape.
    /// </para>
    /// <para>
    /// The container files source path is automatically set to <paramref name="outputPath"/> so that only
    /// the built output directory is copied into the runtime container, not the full application source.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Publishes the JavaScript application as a standalone Node.js server that runs a built artifact directly.")]
    public static IResourceBuilder<TResource> PublishAsNodeServer<TResource>(this IResourceBuilder<TResource> builder, string entryPoint, string outputPath = ".")
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(entryPoint);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var annotation = new JavaScriptPublishModeAnnotation(JavaScriptPublishMode.NodeServer)
        {
            EntryPoint = entryPoint,
            OutputPath = outputPath
        };

        builder.WithAnnotation(annotation)
               .ClearContainerFilesSources()
               .WithContainerFilesSource(GetContainerFilesSourcePath(outputPath))
               .WithOtlpExporter()
               .WithEnvironment("HOST", "0.0.0.0")
               .WithEnvironment("HOSTNAME", "0.0.0.0");

        if (builder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            dockerfileBuildAnnotation.HasEntrypoint = true;
        }

        return builder;
    }

    /// <summary>
    /// Configures the JavaScript application to publish as a Node.js server that uses a package manager script at runtime.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript resource type.</typeparam>
    /// <param name="builder">The JavaScript resource builder.</param>
    /// <param name="startScriptName">
    /// The name of the package manager script to run in the published container.
    /// For example, <c>start</c> runs <c>npm run start</c>.
    /// </param>
    /// <param name="runScriptArguments">
    /// Optional arguments appended after the script name at runtime,
    /// such as <c>-- --port "$PORT"</c>.
    /// </param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Use this method for frameworks where the production server depends on packages in <c>node_modules</c> at runtime.
    /// The resulting container includes the full application with production dependencies installed.
    /// </para>
    /// <para>
    /// This method is appropriate for frameworks like Nuxt (where <c>useAsyncData</c>/<c>useFetch</c> requires the
    /// full Nitro environment), Remix (where <c>react-router-serve</c> is an npm dependency), and Astro SSR
    /// (where the built entry point imports unbundled <c>@astrojs/*</c> packages).
    /// </para>
    /// <para>
    /// For frameworks that produce a self-contained server artifact that does not require <c>node_modules</c>,
    /// use <see cref="PublishAsNodeServer{TResource}"/> instead for a smaller runtime image.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Publishes the JavaScript application as a Node.js server that uses a package manager script at runtime.")]
    public static IResourceBuilder<TResource> PublishAsNpmScript<TResource>(this IResourceBuilder<TResource> builder, string startScriptName = "start", string? runScriptArguments = null)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(startScriptName);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var annotation = new JavaScriptPublishModeAnnotation(JavaScriptPublishMode.NpmScript)
        {
            StartScriptName = startScriptName,
            RunScriptArguments = runScriptArguments
        };

        builder.WithAnnotation(annotation)
               .ClearContainerFilesSources()
               .WithOtlpExporter()
               .WithEnvironment("HOST", "0.0.0.0")
               .WithEnvironment("HOSTNAME", "0.0.0.0");

        if (builder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            dockerfileBuildAnnotation.HasEntrypoint = true;
        }

        return builder;
    }

    private static void AddInstallCommand(this DockerfileStage builderStage, JavaScriptPackageManagerAnnotation packageManager, JavaScriptInstallCommandAnnotation installCommand)
    {
        // Use BuildKit cache mount for package manager cache if available
        var installCmd = $"{packageManager.ExecutableName} {string.Join(' ', installCommand.Args)}";
        if (!string.IsNullOrEmpty(packageManager.CacheMount))
        {
            builderStage.Run($"--mount=type=cache,target={packageManager.CacheMount} {installCmd}");
        }
        else
        {
            builderStage.Run(installCmd);
        }
    }

    private static IResourceBuilder<TResource> CreateDefaultJavaScriptAppBuilder<TResource>(
        this IDistributedApplicationBuilder builder,
        TResource resource,
        string appDirectory,
        string runScriptName,
        Action<CommandLineArgsCallbackContext>? argsCallback = null) where TResource : JavaScriptAppResource
    {
        var resourceBuilder = builder.AddResource(resource)
            .WithNodeDefaults()
            .WithArgs(c =>
            {
                if (c.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runCommand))
                {
                    if (c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
                        !string.IsNullOrEmpty(packageManager.ScriptCommand))
                    {
                        c.Args.Add(packageManager.ScriptCommand);
                    }

                    c.Args.Add(runCommand.ScriptName);

                    foreach (var arg in runCommand.Args)
                    {
                        c.Args.Add(arg);
                    }
                }

                argsCallback?.Invoke(c);
            })
            .WithIconName("CodeJsRectangle")
            .WithNpm()
            .PublishAsDockerFile(c =>
            {
                // Only generate a Dockerfile if one doesn't already exist in the app directory
                if (File.Exists(Path.Combine(appDirectory, "Dockerfile")))
                {
                    return;
                }

                c.WithDockerfileBuilder(appDirectory, dockerfileContext =>
                {
                    dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode);

                    if (c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                    {
                        // Get custom base image from annotation, if present
                        dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
                        var baseImage = baseImageAnnotation?.BuildImage ?? GetDefaultBaseImage(appDirectory, "slim", dockerfileContext.Services);

                        var dockerBuilder = publishMode is not null
                            ? dockerfileContext.Builder.From(baseImage, "build").WorkDir("/app")
                            : dockerfileContext.Builder.From(baseImage).WorkDir("/app");

                        // Initialize the Docker build stage with package manager-specific setup commands
                        // for the default JavaScript app builder (used by Vite and other build-less apps).
                        packageManager.InitializeDockerBuildStage?.Invoke(dockerBuilder);

                        var copiedAllSource = false;

                        // Copy package files first for better layer caching
                        if (packageManager.PackageFilesPatterns.Count > 0)
                        {
                            foreach (var packageFilePattern in packageManager.PackageFilesPatterns)
                            {
                                dockerBuilder.Copy(packageFilePattern.Source, packageFilePattern.Destination);
                            }
                        }
                        else
                        {
                            dockerBuilder.Copy(".", ".");
                            copiedAllSource = true;
                        }

                        if (c.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                        {
                            dockerBuilder.AddInstallCommand(packageManager, installCommand);
                        }

                        if (!copiedAllSource)
                        {
                            // Copy application source code after dependencies are installed
                            dockerBuilder.Copy(".", ".");
                        }

                        if (c.Resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            var commandArgs = new List<string>() { packageManager.ExecutableName };
                            if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
                            {
                                commandArgs.Add(packageManager.ScriptCommand);
                            }
                            commandArgs.Add(buildCommand.ScriptName);
                            commandArgs.AddRange(buildCommand.Args);

                            dockerBuilder.Run(string.Join(' ', commandArgs));
                        }

                        switch (publishMode?.Mode)
                        {
                            case JavaScriptPublishMode.StaticWebsite:
                            {
                                var runtimeImage = baseImageAnnotation?.RuntimeImage ?? DefaultYarpImage;
                                var distPath = GetContainerFilesSourcePath(publishMode.OutputPath);
                                dockerfileContext.Builder
                                    .From(runtimeImage, "runtime")
                                    .WorkDir("/app")
                                    .CopyFrom("build", distPath, "/app/wwwroot")
                                    .Entrypoint(["dotnet", "/app/yarp.dll"]);
                                break;
                            }
                            case JavaScriptPublishMode.NodeServer:
                            {
                                var runtimeImage = baseImageAnnotation?.RuntimeImage ?? GetDefaultBaseImage(appDirectory, "alpine", dockerfileContext.Services);
                                var outputPath = GetContainerFilesSourcePath(publishMode.OutputPath);

                                dockerfileContext.Builder
                                    .From(runtimeImage, "runtime")
                                    .WorkDir("/app")
                                    .CopyFrom("build", outputPath, outputPath)
                                    .Env("NODE_ENV", "production")
                                    .User("node")
                                    .Entrypoint(["node", NormalizeRelativePath(publishMode.EntryPoint!)]);
                                break;
                            }
                            case JavaScriptPublishMode.NpmScript:
                            {
                                var runtimeImage = baseImageAnnotation?.RuntimeImage ?? GetDefaultBaseImage(appDirectory, "alpine", dockerfileContext.Services);

                                // Production dependencies stage for optimized image
                                var prodDepsStage = dockerfileContext.Builder
                                    .From(baseImage, "prod-deps")
                                    .WorkDir("/app");

                                packageManager.InitializeDockerBuildStage?.Invoke(prodDepsStage);

                                if (packageManager.PackageFilesPatterns.Count > 0)
                                {
                                    foreach (var packageFilePattern in packageManager.PackageFilesPatterns)
                                    {
                                        prodDepsStage.Copy(packageFilePattern.Source, packageFilePattern.Destination);
                                    }
                                }
                                else
                                {
                                    prodDepsStage.Copy("package*.json", "./");
                                }

                                // Install production-only dependencies using the same base install
                                // command as the build stage (e.g. 'ci' for npm, 'install --frozen-lockfile'
                                // for pnpm) plus the production-only flag (e.g. '--omit=dev').
                                var installAnnotation = c.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCmd) ? installCmd : null;
                                if (string.IsNullOrEmpty(installAnnotation?.ProductionInstallArgs))
                                {
                                    throw new InvalidOperationException($"Package manager '{packageManager.ExecutableName}' does not have ProductionInstallArgs configured, which is required for PublishAsNpmScript.");
                                }

                                var prodInstallCmd = $"{packageManager.ExecutableName} {string.Join(' ', installAnnotation.Args)} {installAnnotation.ProductionInstallArgs}";
                                if (!string.IsNullOrEmpty(packageManager.CacheMount))
                                {
                                    prodDepsStage.Run($"--mount=type=cache,target={packageManager.CacheMount} {prodInstallCmd}");
                                }
                                else
                                {
                                    prodDepsStage.Run(prodInstallCmd);
                                }

                                // Runtime stage: copy build output then overlay prod deps
                                var runCommand = string.IsNullOrWhiteSpace(publishMode.RunScriptArguments)
                                    ? $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.StartScriptName}"
                                    : $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.StartScriptName} {publishMode.RunScriptArguments}";

                                dockerfileContext.Builder
                                    .From(runtimeImage, "runtime")
                                    .WorkDir("/app")
                                    .CopyFrom("build", "/app", "/app")
                                    .CopyFrom("prod-deps", "/app/node_modules", "./node_modules")
                                    .Env("NODE_ENV", "production")
                                    .Entrypoint(["sh", "-c", $"exec {runCommand}"]);
                                break;
                            }
                            case JavaScriptPublishMode.NextStandalone:
                            {
                                var runtimeImage = baseImageAnnotation?.RuntimeImage ?? GetDefaultBaseImage(appDirectory, "alpine", dockerfileContext.Services);

                                // Match the ownership pattern from the official Next.js sample:
                                // https://github.com/vercel/next.js/blob/canary/examples/with-docker/Dockerfile
                                dockerfileContext.Builder
                                    .From(runtimeImage, "runtime")
                                    .WorkDir("/app")
                                    .Env("NODE_ENV", "production")
                                    .CopyFrom("build", "/app/public", "./public", "node:node")
                                    .Run("mkdir .next")
                                    .Run("chown node:node .next")
                                    .CopyFrom("build", "/app/.next/standalone", "./", "node:node")
                                    .CopyFrom("build", "/app/.next/static", "./.next/static", "node:node")
                                    .User("node")
                                    .Entrypoint(["node", "server.js"]);
                                break;
                            }
                        }
                    }
                });

                // JavaScript apps default to build-only publishing unless a standalone runtime is enabled.
                if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerFileAnnotation))
                {
                    dockerFileAnnotation.HasEntrypoint =
                        resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out _);
                }
                else
                {
                    throw new InvalidOperationException("DockerfileBuildAnnotation should exist after calling PublishAsDockerFile.");
                }
            })
            .WithAnnotation(new ContainerFilesSourceAnnotation() { SourcePath = "/app/dist" })
            .WithBuildScript("build")
            .WithRunScript(runScriptName);

        resourceBuilder.WithVSCodeDebugging();

        // ensure the package manager command is set before starting the resource
        if (builder.ExecutionContext.IsRunMode)
        {
            builder.OnBeforeStart((_, _) =>
            {
                if (resourceBuilder.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                {
                    resourceBuilder.WithCommand(packageManager.ExecutableName);
                }

                return Task.CompletedTask;
            });
        }

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Vite app to the distributed application builder.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the Vite app.</param>
    /// <param name="appDirectory">The path to the directory containing the Vite app.</param>
    /// <param name="runScriptName">The name of the script that runs the Vite app. Defaults to "dev".</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <example>
    /// The following example creates a Vite app using npm as the package manager.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddViteApp("frontend", "./frontend");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds a Vite application resource")]
    public static IResourceBuilder<ViteAppResource> AddViteApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string runScriptName = "dev")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);

        appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, appDirectory));
        var resource = new ViteAppResource(name, "npm", appDirectory);

        var resourceBuilder = builder.CreateDefaultJavaScriptAppBuilder(
            resource,
            appDirectory,
            runScriptName,
            argsCallback: c =>
            {
                // pnpm does not strip the -- separator and passes it to the script, causing Vite to ignore subsequent arguments.
                // npm and yarn both strip the -- separator before passing arguments to the script.
                // Only add the separator for when necessary.
                if (c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
                    packageManager.CommandSeparator is string separator)
                {
                    c.Args.Add(separator);
                }

                var targetEndpoint = resource.GetEndpoint("https");
                if (!targetEndpoint.Exists)
                {
                    targetEndpoint = resource.GetEndpoint("http");
                }

                c.Args.Add("--port");
                c.Args.Add(targetEndpoint.Property(EndpointProperty.TargetPort));

                if (!string.IsNullOrEmpty(resource.ViteConfigPath))
                {
                    c.Args.Add("--config");
                    c.Args.Add(resource.ViteConfigPath);
                }
            })
            .WithHttpEndpoint(env: "PORT")
            // Making TLS opt-in for Vite for now
            .WithoutHttpsCertificate()
            .WithHttpsCertificateConfiguration(async ctx =>
            {
                string? configTarget = resource.ViteConfigPath;

                // First we need to determine if there's an existing --config argument specified
                var cfgIndex = ctx.Arguments.IndexOf("--config");
                if (cfgIndex >= 0 && cfgIndex + 1 < ctx.Arguments.Count)
                {
                    configTarget = ctx.Arguments[cfgIndex + 1] switch
                    {
                        string s when !string.IsNullOrEmpty(s) && !s.StartsWith("--") => s,
                        ReferenceExpression re => await re.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false),
                        _ => null,
                    };

                    if (string.IsNullOrEmpty(configTarget))
                    {
                        // Couldn't determine the config target, so don't modify anything
                        return;
                    }

                    // Remove the original --config argument and its value
                    ctx.Arguments.RemoveAt(cfgIndex);
                    ctx.Arguments.RemoveAt(cfgIndex);
                }
                else if (cfgIndex >= 0)
                {
                    // --config argument is present but is missing a value
                    return;
                }

                if (string.IsNullOrEmpty(configTarget))
                {
                    // The user didn't specify a specific vite config file, so we need to look for one of the default config files
                    foreach (var configFile in s_defaultConfigFiles)
                    {
                        var candidatePath = Path.GetFullPath(Path.Join(appDirectory, configFile));
                        if (File.Exists(candidatePath))
                        {
                            configTarget = candidatePath;
                            break;
                        }
                    }
                }

                if (configTarget is not null)
                {
                    try
                    {
                        // Determine the absolute path to the original config file
                        var absoluteConfigPath = Path.GetFullPath(configTarget, appDirectory);
                        // Determine the relative path from the Aspire vite config to the original config file
                        var relativeConfigPath = Path.GetRelativePath(Path.Join(appDirectory, "node_modules", ".bin"), absoluteConfigPath);

                        // If we are expecting to run the vite app with HTTPS termination, generate an Aspire specific Vite config file that can mutate the user's original config
                        var aspireConfig = AspireViteConfig
                            .Replace(AspireViteRelativeConfigToken, relativeConfigPath.Replace("\\", "/"), StringComparison.Ordinal)
                            .Replace(AspireViteAbsoluteConfigToken, absoluteConfigPath.Replace("\\", "\\\\"), StringComparison.Ordinal);
                        var aspireConfigPath = Path.Join(appDirectory, "node_modules", ".bin", $"aspire.{Path.GetFileName(configTarget)}");
                        File.WriteAllText(aspireConfigPath, aspireConfig);

                        // Override the path to the Vite config file to use the Aspire generated one. If we made it here, we
                        // know there isn't an existing --config argument present.
                        ctx.Arguments.Add("--config");
                        ctx.Arguments.Add(aspireConfigPath);

                        ctx.EnvironmentVariables["TLS_CONFIG_PFX"] = ctx.PfxPath;
                        if (ctx.Password is not null)
                        {
                            ctx.EnvironmentVariables["TLS_CONFIG_PASSWORD"] = ctx.Password;
                        }
                    }
                    catch (Exception ex)
                    {
                        var resourceLoggerService = ctx.ExecutionContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                        var resourceLogger = resourceLoggerService.GetLogger(resource);

                        resourceLogger.LogWarning(ex, "Failed to generate Aspire Vite HTTPS config wrapper for resource '{ResourceName}'. Falling back to existing Vite config without Aspire modifications. Automatic HTTPS configuration won't be available", resource.Name);

                        if (!string.IsNullOrEmpty(configTarget))
                        {
                            // Fallback to using the existing config target
                            ctx.Arguments.Add("--config");
                            ctx.Arguments.Add(configTarget);
                        }
                    }
                }
            });

        if (builder.ExecutionContext.IsRunMode)
        {
            // Vite only supports a single endpoint, so we have to modify the existing endpoint to use HTTPS instead of
            // adding a new one.
            resourceBuilder.SubscribeHttpsEndpointsUpdate(ctx =>
            {
                resourceBuilder.WithEndpoint("http", ep => ep.UriScheme = "https");
            });
        }

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Next.js app to the distributed application builder.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the Next.js app.</param>
    /// <param name="appDirectory">The path to the directory containing the Next.js app.</param>
    /// <param name="runScriptName">The name of the script that runs the Next.js dev server. Defaults to "dev".</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method configures the Next.js application for both local development and publishing.
    /// In run mode, it starts the Next.js dev server with the correct port binding.
    /// In publish mode, it generates a multi-stage Dockerfile using Next.js standalone output mode,
    /// which copies <c>public/</c>, <c>.next/standalone/</c>, and <c>.next/static/</c> into a
    /// Node.js runtime container.
    /// </para>
    /// <para>
    /// The Next.js application must have <c>output: "standalone"</c> configured in <c>next.config.ts</c>
    /// and a <c>public/</c> directory (even if empty) for the published container to build correctly.
    /// </para>
    /// <example>
    /// The following example creates a Next.js app.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddNextJsApp("frontend", "./frontend");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Adds a Next.js application resource")]
    public static IResourceBuilder<NextJsAppResource> AddNextJsApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string runScriptName = "dev")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);

        appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, appDirectory));

        var resource = new NextJsAppResource(name, "npm", appDirectory);

        var resourceBuilder = builder.CreateDefaultJavaScriptAppBuilder(
            resource,
            appDirectory,
            runScriptName,
            argsCallback: c =>
            {
                if (c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) &&
                    packageManager.CommandSeparator is string separator)
                {
                    c.Args.Add(separator);
                }

                var targetEndpoint = resource.GetEndpoint("https");
                if (!targetEndpoint.Exists)
                {
                    targetEndpoint = resource.GetEndpoint("http");
                }

                c.Args.Add("-p");
                c.Args.Add(targetEndpoint.Property(EndpointProperty.TargetPort));
            })
            .WithHttpEndpoint(env: "PORT")
            .WithOtlpExporter();

        if (builder.ExecutionContext.IsPublishMode)
        {
            resourceBuilder
                .WithAnnotation(new JavaScriptPublishModeAnnotation(JavaScriptPublishMode.NextStandalone))
                .ClearContainerFilesSources()
                .WithEnvironment("HOSTNAME", "0.0.0.0");

            if (resourceBuilder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
            {
                dockerfileBuildAnnotation.HasEntrypoint = true;
            }
        }

        // Add a publish prereq step that validates the Next.js config has standalone output enabled.
        // This runs at deploy time (not resource creation time) so it doesn't block `aspire start`.
        // Can be disabled with .DisableBuildValidation().
        resourceBuilder.WithAnnotation(new PipelineStepAnnotation(factoryCtx =>
        [
            new PipelineStep
            {
                Name = $"nextjs-standalone-check-{name}",
                Description = $"Validates that the Next.js app '{name}' has output: \"standalone\" configured.",
                DependsOnSteps = [WellKnownPipelineSteps.BuildPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Build],
                Resource = resourceBuilder.Resource,
                Action = _ =>
                {
                    if (!resourceBuilder.Resource.TryGetLastAnnotation<SuppressPublishValidationAnnotation>(out var suppress))
                    {
                        ValidateNextJsStandaloneOutput(appDirectory);
                    }

                    return Task.CompletedTask;
                }
            }
        ]));

        return resourceBuilder;
    }

    /// <summary>
    /// Disables deploy-time build validation checks for the Next.js application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// By default, <see cref="AddNextJsApp"/> adds publish prerequisite steps that verify
    /// the Next.js configuration (e.g. that <c>output: "standalone"</c> is set). Use this method
    /// to suppress those checks when the configuration is set dynamically or via an external
    /// mechanism that cannot be detected by static file inspection.
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Disables deploy-time build validation checks for the Next.js application.")]
    public static IResourceBuilder<NextJsAppResource> DisableBuildValidation(this IResourceBuilder<NextJsAppResource> builder)
    {
        return builder.WithAnnotation<SuppressPublishValidationAnnotation>(new());
    }

    /// <summary>
    /// Configures the Vite app to use the specified Vite configuration file instead of the default resolution behavior.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configPath">The path to the Vite configuration file. Relative to the Vite service project root.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Use this method to specify a specific Vite configuration file if you need to override the default Vite configuration resolution behavior.
    /// </remarks>
    /// <example>
    /// Use a custom Vite configuration file:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var viteApp = builder.AddViteApp("frontend", "./frontend")
    ///     .WithViteConfig("./vite.production.config.js");
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures a custom Vite configuration file")]
    public static IResourceBuilder<ViteAppResource> WithViteConfig(this IResourceBuilder<ViteAppResource> builder, string configPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        builder.Resource.ViteConfigPath = configPath;

        return builder;
    }

    /// <summary>
    /// Configures the Node.js resource to use npm as the package manager and optionally installs packages before the application starts.
    /// </summary>
    /// <param name="resource">The NodeAppResource.</param>
    /// <param name="install">When true (default), automatically installs packages before the application starts. When false, only sets the package manager annotation without creating an installer resource.</param>
    /// <param name="installCommand">The install command itself passed to npm to install dependencies.</param>
    /// <param name="installArgs">The command-line arguments passed to npm to install dependencies.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures npm as the package manager")]
    public static IResourceBuilder<TResource> WithNpm<TResource>(this IResourceBuilder<TResource> resource, bool install = true, string? installCommand = null, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        installCommand ??= GetDefaultNpmInstallCommand(resource);

        resource
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("npm", runScriptCommand: "run", cacheMount: "/root/.npm")
            {
                PackageFilesPatterns = { new CopyFilePattern("package*.json", "./") },
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation([installCommand, .. installArgs ?? []])
            {
                ProductionInstallArgs = "--omit=dev"
            })
            .WithRequiredCommand("npm", "https://docs.npmjs.com/downloading-and-installing-node-js-and-npm");

        AddInstaller(resource, install);
        return resource;
    }

    /// <summary>
    /// Configures the JavaScript resource to use Bun as the package manager and optionally installs packages before the application starts.
    /// </summary>
    /// <param name="resource">The JavaScript application resource builder.</param>
    /// <param name="install">When true (default), automatically installs packages before the application starts. When false, only sets the package manager annotation without creating an installer resource.</param>
    /// <param name="installArgs">Additional command-line arguments passed to "bun install". When null, defaults are applied based on publish mode and lockfile presence.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Bun forwards script arguments without requiring the <c>--</c> command separator, so this method configures the resource to omit it.
    /// When publishing and a bun lockfile (<c>bun.lock</c> or <c>bun.lockb</c>) is present, <c>--frozen-lockfile</c> is used by default.
    /// Publishing to a container requires Bun to be present in the build image. This method configures a Bun build image when one is not already specified.
    /// To use a specific Bun version, configure a custom build image (for example, <c>oven/bun:&lt;tag&gt;</c>) using <see cref="ContainerResourceBuilderExtensions.WithDockerfileBaseImage{T}(IResourceBuilder{T}, string?, string?)"/>.
    /// </remarks>
    /// <example>
    /// Run a Vite app using Bun as the package manager:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddViteApp("frontend", "./frontend")
    ///        .WithBun()
    ///        .WithDockerfileBaseImage(buildImage: "oven/bun:latest"); // To use a specific Bun image
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures Bun as the package manager")]
    public static IResourceBuilder<TResource> WithBun<TResource>(this IResourceBuilder<TResource> resource, bool install = true, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        var workingDirectory = resource.Resource.WorkingDirectory;
        var hasBunLock = File.Exists(Path.Combine(workingDirectory, "bun.lock")) ||
            File.Exists(Path.Combine(workingDirectory, "bun.lockb"));

        installArgs ??= GetDefaultBunInstallArgs(resource, hasBunLock);

        var packageFilesSourcePattern = "package.json";
        if (File.Exists(Path.Combine(workingDirectory, "bun.lock")))
        {
            packageFilesSourcePattern += " bun.lock";
        }
        if (File.Exists(Path.Combine(workingDirectory, "bun.lockb")))
        {
            packageFilesSourcePattern += " bun.lockb";
        }

        resource
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("bun", runScriptCommand: "run", cacheMount: "/root/.bun/install/cache")
            {
                PackageFilesPatterns = { new CopyFilePattern(packageFilesSourcePattern, "./") },
                // bun supports passing script flags without the `--` separator.
                CommandSeparator = null,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs])
            {
                ProductionInstallArgs = "--production"
            })
            .WithRequiredCommand("bun", "https://bun.sh/docs/installation");

        if (!resource.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out _))
        {
            // bun is not available in the default Node.js base images used for publish-mode Dockerfile generation.
            // We override the build image so that the install and build steps can execute with bun.
            resource.WithAnnotation(new DockerfileBaseImageAnnotation
            {
                // Use a constant major version tag to keep builds deterministic.
                BuildImage = "oven/bun:1",
            });
        }

        AddInstaller(resource, install);
        return resource;
    }

    private static string[] GetDefaultBunInstallArgs(IResourceBuilder<JavaScriptAppResource> resource, bool hasBunLock) =>
        resource.ApplicationBuilder.ExecutionContext.IsPublishMode && hasBunLock
            ? ["--frozen-lockfile"]
            : [];

    private static string GetDefaultNpmInstallCommand(IResourceBuilder<JavaScriptAppResource> resource) =>
        resource.ApplicationBuilder.ExecutionContext.IsPublishMode &&
            File.Exists(Path.Combine(resource.Resource.WorkingDirectory, "package-lock.json"))
            ? "ci"
            : "install";

    /// <summary>
    /// Configures the Node.js resource to use yarn as the package manager and optionally installs packages before the application starts.
    /// </summary>
    /// <param name="resource">The NodeAppResource.</param>
    /// <param name="install">When true (default), automatically installs packages before the application starts. When false, only sets the package manager annotation without creating an installer resource.</param>
    /// <param name="installArgs">The command-line arguments passed to "yarn install".</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures yarn as the package manager")]
    public static IResourceBuilder<TResource> WithYarn<TResource>(this IResourceBuilder<TResource> resource, bool install = true, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        var workingDirectory = resource.Resource.WorkingDirectory;
        var hasYarnLock = File.Exists(Path.Combine(workingDirectory, "yarn.lock"));
        var hasYarnrc = File.Exists(Path.Combine(workingDirectory, ".yarnrc.yml"));
        var hasYarnBerryDir = Directory.Exists(Path.Combine(workingDirectory, ".yarn"));
        var hasYarnBerry = hasYarnrc || hasYarnBerryDir;

        installArgs ??= GetDefaultYarnInstallArgs(resource, hasYarnLock, hasYarnBerry);

        var cacheMount = hasYarnBerry ? ".yarn/cache" : "/root/.cache/yarn";
        var packageManager = new JavaScriptPackageManagerAnnotation("yarn", runScriptCommand: "run", cacheMount)
        {
            // Yarn doesn't require "--" separator
            // Yarn v1 strips the separator automatically but produces the warning suggesting to remove it.
            // Later Yarn versions don't strip the separator and pass it to the script as-is, causing Vite to ignore subsequent arguments.
            CommandSeparator = null,
        };
        var packageFilesSourcePattern = "package.json";
        if (hasYarnLock)
        {
            packageFilesSourcePattern += " yarn.lock";
        }
        if (hasYarnrc)
        {
            packageFilesSourcePattern += " .yarnrc.yml";
        }
        packageManager.PackageFilesPatterns.Add(new CopyFilePattern(packageFilesSourcePattern, "./"));

        if (hasYarnBerryDir)
        {
            packageManager.PackageFilesPatterns.Add(new CopyFilePattern(".yarn", "./.yarn"));
        }

        resource
            .WithAnnotation(packageManager)
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs])
            {
                ProductionInstallArgs = "--production"
            })
            .WithRequiredCommand("yarn", "https://yarnpkg.com/getting-started/install");

        AddInstaller(resource, install);
        return resource;
    }

    private static string[] GetDefaultYarnInstallArgs(
        IResourceBuilder<JavaScriptAppResource> resource,
        bool hasYarnLock,
        bool hasYarnBerry)
    {
        if (!resource.ApplicationBuilder.ExecutionContext.IsPublishMode ||
            !hasYarnLock)
        {
            // Not publish mode or no yarn.lock, use default install args
            return [];
        }

        if (hasYarnBerry)
        {
            // Yarn 2+ detected, --frozen-lockfile is deprecated in v2+, use --immutable instead
            return ["--immutable"];
        }

        // Fallback: default to Yarn v1.x behavior
        return ["--frozen-lockfile"];
    }

    /// <summary>
    /// Configures the Node.js resource to use pnpm as the package manager and optionally installs packages before the application starts.
    /// </summary>
    /// <param name="resource">The NodeAppResource.</param>
    /// <param name="install">When true (default), automatically installs packages before the application starts. When false, only sets the package manager annotation without creating an installer resource.</param>
    /// <param name="installArgs">The command-line arguments passed to "pnpm install".</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures pnpm as the package manager")]
    public static IResourceBuilder<TResource> WithPnpm<TResource>(this IResourceBuilder<TResource> resource, bool install = true, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        var workingDirectory = resource.Resource.WorkingDirectory;
        var hasPnpmLock = File.Exists(Path.Combine(workingDirectory, "pnpm-lock.yaml"));

        installArgs ??= GetDefaultPnpmInstallArgs(resource, hasPnpmLock);

        var packageFilesSourcePattern = "package.json";
        if (hasPnpmLock)
        {
            packageFilesSourcePattern += " pnpm-lock.yaml";
        }

        resource
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("pnpm", runScriptCommand: "run", cacheMount: "/pnpm/store")
            {
                PackageFilesPatterns = { new CopyFilePattern(packageFilesSourcePattern, "./") },
                // pnpm does not strip the -- separator and passes it to the script, causing Vite to ignore subsequent arguments.
                CommandSeparator = null,
                // pnpm is not included in the Node.js Docker image by default, so we need to enable it via corepack
                InitializeDockerBuildStage = stage => stage.Run("corepack enable pnpm"),
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs])
            {
                ProductionInstallArgs = "--prod"
            })
            .WithRequiredCommand("pnpm", "https://pnpm.io/installation");

        AddInstaller(resource, install);
        return resource;
    }

    private static string[] GetDefaultPnpmInstallArgs(IResourceBuilder<JavaScriptAppResource> resource, bool hasPnpmLock) =>
        resource.ApplicationBuilder.ExecutionContext.IsPublishMode && hasPnpmLock
            ? ["--frozen-lockfile"]
            : [];

    /// <summary>
    /// Adds a build script annotation to the resource builder using the specified command-line arguments.
    /// </summary>
    /// <typeparam name="TResource">The type of JavaScript application resource being configured.</typeparam>
    /// <param name="resource">The resource builder to which the build script annotation will be added.</param>
    /// <param name="scriptName">The name of the script to be executed when the resource is built.</param>
    /// <param name="args">An array of command-line arguments to use for the build script.</param>
    /// <returns>The same resource builder instance with the build script annotation applied.</returns>
    /// <remarks>
    /// Use this method to specify custom build scripts for JavaScript application resources during
    /// deployment.
    /// </remarks>
    [AspireExport(Description = "Specifies an npm script to run before starting the application")]
    public static IResourceBuilder<TResource> WithBuildScript<TResource>(this IResourceBuilder<TResource> resource, string scriptName, string[]? args = null) where TResource : JavaScriptAppResource
    {
        return resource.WithAnnotation(new JavaScriptBuildScriptAnnotation(scriptName, args));
    }

    /// <summary>
    /// Adds a run script annotation to the specified JavaScript application resource builder, specifying the script to
    /// execute and its arguments during run mode.
    /// </summary>
    /// <typeparam name="TResource">The type of the JavaScript application resource being configured. Must inherit from JavaScriptAppResource.</typeparam>
    /// <param name="resource">The resource builder to which the run script annotation will be added.</param>
    /// <param name="scriptName">The name of the script to be executed when the resource is run.</param>
    /// <param name="args">An array of arguments to pass to the script.</param>
    /// <returns>The same resource builder instance with the run script annotation applied, enabling further configuration.</returns>
    /// <remarks>
    /// Use this method to specify a custom script and its arguments that should be executed when the resource is executed
    /// in RunMode.
    /// </remarks>
    [AspireExport(Description = "Specifies an npm script to run during development")]
    public static IResourceBuilder<TResource> WithRunScript<TResource>(this IResourceBuilder<TResource> resource, string scriptName, string[]? args = null) where TResource : JavaScriptAppResource
    {
        return resource.WithAnnotation(new JavaScriptRunScriptAnnotation(scriptName, args));
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder, string scriptPath)
        where T : NodeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        var resource = builder.Resource;
        var workingDirectory = Path.GetFullPath(resource.WorkingDirectory);

        return builder.WithDebugSupport(
            mode =>
            {
                // Compute at run time so the launch config reflects the final annotation state
                var hasRunScript = resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out _);
                var hasPackageManager = resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pmAnnotation);
                var runtimeExecutable = hasRunScript && hasPackageManager ? pmAnnotation!.ExecutableName : "node";

                return new NodeLaunchConfiguration
                {
                    ScriptPath = Path.GetFullPath(scriptPath, workingDirectory),
                    Mode = mode,
                    RuntimeExecutable = runtimeExecutable,
                    WorkingDirectory = workingDirectory
                };
            },
            "node");
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = builder.Resource;
        var workingDirectory = Path.GetFullPath(resource.WorkingDirectory);

        return builder.WithDebugSupport(
            mode =>
            {
                // Compute at run time so the launch config reflects the final annotation state
                var packageManager = "npm";
                if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pmAnnotation))
                {
                    packageManager = pmAnnotation.ExecutableName;
                }

                return new NodeLaunchConfiguration
                {
                    ScriptPath = string.Empty,
                    Mode = mode,
                    RuntimeExecutable = packageManager,
                    WorkingDirectory = workingDirectory
                };
            },
            "node");
    }

    /// <summary>
    /// Configures a browser debugger for the JavaScript application resource, enabling browser-based debugging
    /// through a child resource that launches when the parent application is ready.
    /// </summary>
    /// <typeparam name="T">The type of the JavaScript application resource.</typeparam>
    /// <param name="builder">The resource builder for the JavaScript application.</param>
    /// <param name="browser">The browser to use for debugging. Defaults to <c>"msedge"</c>. Supported values include <c>"msedge"</c> and <c>"chrome"</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <remarks>
    /// This method creates a child <see cref="BrowserDebuggerResource"/> that waits for the parent JavaScript
    /// application to start, then launches a browser debug session targeting the parent's HTTP or HTTPS endpoint.
    /// The parent resource must have at least one HTTP or HTTPS endpoint configured.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the parent resource does not have an HTTP or HTTPS endpoint, or when the IDE extension
    /// does not support browser debugging.
    /// </exception>
    /// <example>
    /// Add browser debugging to a JavaScript application:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// builder.AddViteApp("frontend", "./frontend")
    ///     .WithBrowserDebugger();
    /// </code>
    /// </example>
    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport(Description = "Configures a browser debugger for the JavaScript application")]
    public static IResourceBuilder<T> WithBrowserDebugger<T>(
        this IResourceBuilder<T> builder,
        string browser = "msedge")
        where T : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Validate that the extension supports browser debugging if we're running in an extension context
        ValidateBrowserCapability(builder);

        var parentResource = builder.Resource;
        var debuggerResourceName = $"{parentResource.Name}-browser";

        var debuggerResource = new BrowserDebuggerResource(debuggerResourceName, browser, parentResource.WorkingDirectory);

        builder.ApplicationBuilder.AddResource(debuggerResource)
            .WithParentRelationship(parentResource)
            .WaitFor(builder)
            .ExcludeFromManifest()
            .WithDebugSupport(
                mode =>
                {
                    // Resolve endpoint at run time so dynamically added endpoints are reflected
                    EndpointAnnotation? endpointAnnotation = null;
                    if (parentResource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
                    {
                        endpointAnnotation = endpoints.FirstOrDefault(e => e.UriScheme == "https")
                            ?? endpoints.FirstOrDefault(e => e.UriScheme == "http");
                    }

                    if (endpointAnnotation is null)
                    {
                        throw new InvalidOperationException(
                            $"Resource '{parentResource.Name}' does not have an HTTP or HTTPS endpoint. Browser debugging requires an endpoint to navigate to.");
                    }

                    var endpointReference = parentResource.GetEndpoint(endpointAnnotation.Name);

                    return new BrowserLaunchConfiguration
                    {
                        Mode = mode,
                        Url = endpointReference.Url,
                        WebRoot = parentResource.WorkingDirectory,
                        Browser = browser
                    };
                },
                BrowserCapability);

        return builder;
    }

    private static void ValidateBrowserCapability<T>(IResourceBuilder<T> builder) where T : IResource
    {
        var configuration = builder.ApplicationBuilder.Configuration;

        try
        {
            if (configuration[KnownConfigNames.DebugSessionInfo] is { } debugSessionInfoJson
                && JsonSerializer.Deserialize<DebugSessionCapabilities>(debugSessionInfoJson) is { } info
                && info.SupportedLaunchConfigurations is not null
                && !info.SupportedLaunchConfigurations.Contains(BrowserCapability))
            {
                throw new InvalidOperationException(
                    "This version of the Aspire extension does not support browser debugging. Please update the Aspire extension to use browser debugging support with WithBrowserDebugger().");
            }
        }
        catch (JsonException)
        {
            // If we can't parse the debug session info, skip validation
        }
    }

    private sealed class DebugSessionCapabilities
    {
        [JsonPropertyName("supported_launch_configurations")]
        public string[]? SupportedLaunchConfigurations { get; set; }
    }

    private static void AddInstaller<TResource>(IResourceBuilder<TResource> resource, bool install) where TResource : JavaScriptAppResource
    {
        // Only install packages if in run mode
        if (resource.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // Check if the installer resource already exists
            var installerName = $"{resource.Resource.Name}-installer";
            resource.ApplicationBuilder.TryCreateResourceBuilder<JavaScriptInstallerResource>(installerName, out var existingResource);

            if (existingResource is not null)
            {
                // Installer already exists, update its configuration based on install parameter
                if (!install)
                {
                    // Remove wait annotation if install is false
                    resource.Resource.Annotations.OfType<WaitAnnotation>()
                        .Where(w => w.Resource == existingResource.Resource)
                        .ToList()
                        .ForEach(w => resource.Resource.Annotations.Remove(w));

                    // Add WithExplicitStart to the existing installer
                    existingResource.WithExplicitStart();
                }
                return;
            }

            var installer = new JavaScriptInstallerResource(installerName, resource.Resource.WorkingDirectory);
            installer.Annotations.Add(NameValidationPolicyAnnotation.None);
            var installerBuilder = resource.ApplicationBuilder.AddResource(installer)
                .WithParentRelationship(resource.Resource)
                .ExcludeFromManifest()
                .WithCertificateTrustScope(CertificateTrustScope.None);

            resource.ApplicationBuilder.OnBeforeStart((_, _) =>
            {
                // set the installer's working directory to match the resource's working directory
                // and set the install command and args based on the resource's annotations
                if (!resource.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) ||
                    !resource.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                {
                    throw new InvalidOperationException("JavaScriptPackageManagerAnnotation and JavaScriptInstallCommandAnnotation are required when installing packages.");
                }

                installerBuilder
                    .WithCommand(packageManager.ExecutableName)
                    .WithWorkingDirectory(resource.Resource.WorkingDirectory)
                    .WithArgs(installCommand.Args);

                return Task.CompletedTask;
            });

            if (install)
            {
                // Make the parent resource wait for the installer to complete
                resource.WaitForCompletion(installerBuilder);
            }
            else
            {
                // Add WithExplicitStart when install is false
                // Note: No need to remove wait annotations here since WaitForCompletion was never called
                installerBuilder.WithExplicitStart();
            }

            resource.WithAnnotation(new JavaScriptPackageInstallerAnnotation(installer));
        }
    }

    private static string GetDefaultBaseImage(string appDirectory, string defaultSuffix, IServiceProvider serviceProvider)
    {
        var logger = serviceProvider.GetService<ILogger<JavaScriptAppResource>>() ?? NullLogger<JavaScriptAppResource>.Instance;
        var nodeVersion = ResolveNodeVersion(appDirectory, logger);
        return $"node:{nodeVersion}-{defaultSuffix}";
    }

    private static string GetContainerFilesSourcePath(string outputPath)
    {
        var normalizedPath = NormalizeRelativePath(outputPath);
        return string.IsNullOrEmpty(normalizedPath) || normalizedPath == "."
            ? "/app"
            : $"/app/{normalizedPath}";
    }

    private static readonly string[] s_nextConfigFileNames = ["next.config.ts", "next.config.js", "next.config.mjs"];

    /// <summary>
    /// Builds a service discovery URL for the given resource, preferring HTTPS when available.
    /// Mirrors the logic in <c>YarpCluster.BuildEndpointUri</c>.
    /// </summary>
    private static string BuildServiceDiscoveryUrl(IResourceWithServiceDiscovery resource)
    {
        var endpoints = resource.GetEndpoints();
        var hasHttpsEndpoint = endpoints.Any(e => e.Exists && e.IsHttps);
        var hasHttpEndpoint = endpoints.Any(e => e.Exists && e.IsHttp);

        var scheme = (hasHttpsEndpoint, hasHttpEndpoint) switch
        {
            (true, true) => "https+http",
            (true, false) => "https",
            (false, true) => "http",
            _ => throw new ArgumentException("Cannot find a http or https endpoint for this resource.", nameof(resource))
        };

        return $"{scheme}://{resource.Name}";
    }

    /// <summary>
    /// Validates that the Next.js config file contains <c>output: "standalone"</c>.
    /// </summary>
    internal static void ValidateNextJsStandaloneOutput(string appDirectory)
    {
        foreach (var configFileName in s_nextConfigFileNames)
        {
            var configPath = Path.Combine(appDirectory, configFileName);
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(configPath);

                // Check for quoted "standalone" (double or single quotes) to reduce false positives
                if (!content.Contains("\"standalone\"") && !content.Contains("'standalone'"))
                {
                    throw new InvalidOperationException(
                        $"The Next.js config file '{configFileName}' does not contain 'output: \"standalone\"'. " +
                        "AddNextJsApp requires Next.js standalone output mode to generate a working Dockerfile. " +
                        "Add 'output: \"standalone\"' to the nextConfig object in your Next.js config file.");
                }
            }
            catch (IOException)
            {
                // If we can't read the config, skip the check — the Docker build will surface the error.
            }

            return;
        }

        throw new InvalidOperationException(
            "No Next.js configuration file found. AddNextJsApp expects one of: " +
            string.Join(", ", s_nextConfigFileNames));
    }

    private static void ValidateApiPath(string apiPath)
    {
        foreach (var c in apiPath)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '/' and not '-' and not '_')
            {
                throw new ArgumentException($"The apiPath must contain only URL-safe path characters (alphanumeric, '/', '-', '_'). Invalid character: '{c}'", nameof(apiPath));
            }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalizedPath = path.Replace('\\', '/');

        if (normalizedPath.StartsWith("./", StringComparison.Ordinal))
        {
            normalizedPath = normalizedPath[2..];
        }

        if (normalizedPath.StartsWith('/'))
        {
            throw new ArgumentException("The path must be a relative path.", nameof(path));
        }

        // Reject path traversal segments. These are virtual Docker container paths (not host
        // filesystem paths), so Path.GetFullPath cannot be used — it produces platform-specific
        // results (e.g. D:\app\dist on Windows). Segment-based validation works correctly
        // cross-platform for container paths.
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                throw new ArgumentException("The path must not contain \"..\" segments.", nameof(path));
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Resolves the Node.js version to use for a project by checking common configuration files.
    /// </summary>
    /// <param name="workingDirectory">The working directory of the Node.js project.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    /// <returns>The resolved Node.js major version number as a string.</returns>
    private static string ResolveNodeVersion(string workingDirectory, ILogger logger)
    {
        // Follow the same shape as Cloud Native Buildpacks-style tooling for Node selection:
        // pinned toolchain files (.nvmrc, .node-version, .tool-versions) are treated as
        // authoritative runtime intent, while package.json engines.node is compatibility
        // metadata rather than a deployment image pin. If there is no explicit toolchain pin,
        // generated Dockerfiles fall back to Aspire's preferred default Node major.
        if (TryDetectPinnedNodeVersion(workingDirectory, logger, out var pinnedNodeVersion))
        {
            return pinnedNodeVersion;
        }

        logger.LogDebug("No Node.js version detected, using default version {DefaultVersion}", DefaultNodeVersion);
        return DefaultNodeVersion;
    }

    private static bool TryDetectPinnedNodeVersion(string workingDirectory, ILogger logger, out string nodeVersion)
    {
        nodeVersion = string.Empty;

        // Check .nvmrc file
        var nvmrcPath = Path.Combine(workingDirectory, ".nvmrc");
        if (File.Exists(nvmrcPath))
        {
            var versionString = File.ReadAllText(nvmrcPath).Trim();
            if (TryParseNodeVersion(versionString, out var version))
            {
                logger.LogDebug("Detected Node.js version {Version} from .nvmrc file", version);
                nodeVersion = version;
                return true;
            }
        }

        // Check .node-version file
        var nodeVersionPath = Path.Combine(workingDirectory, ".node-version");
        if (File.Exists(nodeVersionPath))
        {
            var versionString = File.ReadAllText(nodeVersionPath).Trim();
            if (TryParseNodeVersion(versionString, out var version))
            {
                logger.LogDebug("Detected Node.js version {Version} from .node-version file", version);
                nodeVersion = version;
                return true;
            }
        }

        // Check .tool-versions file (asdf)
        var toolVersionsPath = Path.Combine(workingDirectory, ".tool-versions");
        if (File.Exists(toolVersionsPath))
        {
            var lines = File.ReadAllLines(toolVersionsPath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                var parts = trimmedLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 &&
                    (string.Equals(parts[0], "nodejs", StringComparison.Ordinal) ||
                     string.Equals(parts[0], "node", StringComparison.Ordinal)))
                {
                    if (TryParseNodeVersion(parts[1], out var version))
                    {
                        logger.LogDebug("Detected Node.js version {Version} from .tool-versions file", version);
                        nodeVersion = version;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse a Node.js version string and extract the major version number.
    /// </summary>
    /// <param name="versionString">The version string to parse (e.g., "22", "v22.1.0", ">=20.12", "^18.0.0").</param>
    /// <param name="majorVersion">The extracted major version number as a string.</param>
    /// <returns>True if the version was successfully parsed, false otherwise.</returns>
    private static bool TryParseNodeVersion(string versionString, out string majorVersion)
    {
        majorVersion = string.Empty;

        if (string.IsNullOrWhiteSpace(versionString))
        {
            return false;
        }

        // Remove common prefixes and operators (handle multi-character operators first)
        var cleaned = versionString.Trim();
        string[] operators = [">=", "<=", "==", ">", "<", "=", "~", "^", "v", "V"];
        foreach (var op in operators)
        {
            if (cleaned.StartsWith(op, StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(op.Length).TrimStart();
                break;
            }
        }
        var cleanedVersion = cleaned.Split('.', '-', ' ')[0]; // Take only the major version part

        // Try to parse as integer
        if (int.TryParse(cleanedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var majorVersionNumber) && majorVersionNumber > 0)
        {
            majorVersion = majorVersionNumber.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }
}
