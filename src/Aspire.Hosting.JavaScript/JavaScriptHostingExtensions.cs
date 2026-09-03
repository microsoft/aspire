// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDENO001 // AddDenoApp and its implementation use the experimental Deno resource

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECOMMAND001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding JavaScript applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static partial class JavaScriptHostingExtensions
{
    private const string BrowserCapability = "browser";
    private const string DefaultNodeVersion = "22";
    // Default to the public npm registry so generated Dockerfiles work for customers out of the box.
    // Operators who want an internal mirror can override it at build time via `--build-arg NPM_REGISTRY=...`.
    // See https://github.com/microsoft/aspire/issues/19370.
    private const string DefaultNpmRegistry = "https://registry.npmjs.org/";
    private const string DefaultPnpmVersion = "10.30.1";
    private const string DefaultJavaScriptRunScriptName = "dev";
    private const string DefaultYarpImage = Yarp.YarpContainerImageTags.Registry + "/" + Yarp.YarpContainerImageTags.Image + ":" + Yarp.YarpContainerImageTags.Tag;

    // Help links surfaced when a required command is missing, mapped to a command by ResolveHelpLink.
    private const string NodeHelpLink = "https://nodejs.org/en/download/";
    private const string NpmHelpLink = "https://nodejs.org/en/download";
    private const string BunHelpLink = "https://bun.sh/docs/installation";
    private const string DenoHelpLink = "https://docs.deno.com/runtime/getting_started/installation/";
    private const string YarnHelpLink = "https://yarnpkg.com/getting-started/install";
    private const string PnpmHelpLink = "https://pnpm.io/installation";
    private const string DenoDefaultUser = "deno";
    private const string DenoDefaultUserAndGroup = "deno:deno";

    // Deno's dependency store. Pinned to a known path so multi-stage builds can copy it from the
    // build stage into the runtime stage. See https://docs.deno.com/runtime/reference/docker/.
    private const string DenoCacheDirectory = "/deno-dir";

    // npm/yarn/pnpm are Node CLIs: whether they install packages or launch the app's run script, they spawn
    // node, so node must be on PATH too. bun is a full Node replacement and needs no node.
    private static readonly string[] s_nodeBasedPackageManagers = ["npm", "yarn", "pnpm"];

    // This is the order of config files that Vite will look for by default
    // See https://github.com/vitejs/vite/blob/main/packages/vite/src/node/constants.ts#L97
    private static readonly string[] s_defaultConfigFiles = ["vite.config.js", "vite.config.mjs", "vite.config.ts", "vite.config.cjs", "vite.config.mts", "vite.config.cts"];

    // The token to replace with the relative path to the user's Vite config file
    private const string AspireViteConfigPathToken = "%%ASPIRE_VITE_CONFIG_PATH%%";

    // The token to replace with the absolute path to the original Vite config file
    private const string AspireViteAbsoluteConfigToken = "%%ASPIRE_VITE_ABSOLUTE_CONFIG_PATH%%";

    // A template Vite config that loads an existing config provides a default https configuration if one isn't present
    // Uses environment variables to configure a TLS certificate in PFX format and its password if specified
    // The value of %%ASPIRE_VITE_CONFIG_PATH%% is replaced with the relative path to the user's actual Vite config file at runtime
    // Vite only supports module style config files, so we don't have to handle commonjs style imports or exports here
    private const string AspireViteConfig = """
    import { defineConfig } from 'vite'
    import config from '%%ASPIRE_VITE_CONFIG_PATH%%'

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
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
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

                    // Get custom base image from annotation, if present. A caller can configure only a runtime
                    // image, which leaves BuildImage null, so fall back to the package manager's own image
                    // before the Node.js default - bun and deno are absent from the Node.js images.
                    dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
                    resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager);

                    var baseBuildImage = baseImageAnnotation?.BuildImage
                        ?? packageManager?.DefaultBuildImage
                        ?? defaultBaseImage.Value;
                    var builderStage = dockerfileContext.Builder
                        .From(baseBuildImage, "build")
                        .EmptyLine()
                        .WorkDir("/app");

                    if (packageManager is not null)
                    {
                        // Initialize the Docker build stage with package manager-specific setup commands.
                        // This allows package managers to add prerequisite commands (e.g., enabling pnpm via corepack)
                        // before package installation and build steps.
                        packageManager.InitializeDockerBuildStage?.Invoke(builderStage);

                        var copiedAllSource = false;
                        if (resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                        {
                            copiedAllSource = builderStage.CopyPackageFilesForInstall(packageManager);
                            builderStage.AddInstallCommand(packageManager, installCommand);
                        }

                        if (!copiedAllSource)
                        {
                            // Copy application source code after dependencies are installed
                            builderStage.Copy(".", ".");
                        }

                        if (resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            builderStage.EmptyLine()
                                .Run(BuildPackageScriptCommand(packageManager, buildCommand));
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

        resourceBuilder.WithVSCodeDebugging(scriptPath, "node");

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
            .WithRequiredCommandsFromPackageManager("node")
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

    // Registers a hook that materializes the resource's required commands just before start. The annotations are
    // added on BeforeStartEvent in every execution context, but they only have an effect in run mode, where
    // RequiredCommandValidationEventingSubscriber validates them against the local PATH on
    // BeforeResourceStartedEvent (which fires after BeforeStartEvent). Resolving them here - rather than eagerly
    // as each With* method runs - lets the package-manager selection settle first, so a later selection fully
    // replaces an earlier one without having to remove stale RequiredCommandAnnotations.
    // See https://github.com/microsoft/aspire/issues/18625.
    //
    // runtimeCommand is the executable the app was created to run with (node for
    // AddNodeApp/AddViteApp/AddJavaScriptApp, bun for AddBunApp); it launches the app whenever the app is not
    // routed through a package-manager run script.
    private static IResourceBuilder<TResource> WithRequiredCommandsFromPackageManager<TResource>(
        this IResourceBuilder<TResource> builder,
        string runtimeCommand) where TResource : JavaScriptAppResource
    {
        var resource = builder.Resource;
        builder.ApplicationBuilder.OnBeforeStart((_, _) =>
        {
            foreach (var (command, helpLink) in ResolveRequiredCommands(resource, runtimeCommand))
            {
                // Idempotent: skip commands already present so an unexpected second publish of BeforeStartEvent
                // cannot add duplicate RequiredCommandAnnotations for the same command.
                if (!resource.Annotations.OfType<RequiredCommandAnnotation>().Any(a => string.Equals(a.Command, command, StringComparison.Ordinal)))
                {
                    resource.Annotations.Add(new RequiredCommandAnnotation(command) { HelpLink = helpLink });
                }
            }

            return Task.CompletedTask;
        });

        return builder;
    }

    // Resolves the executables that must be on PATH for the app to install and run, from how the app is actually
    // launched. Two independent axes:
    //   - Runtime: apps that launch via a named package-manager run script (npm run dev / bun run dev) - which is
    //     every AddViteApp/AddJavaScriptApp, plus AddNodeApp/AddBunApp when WithRunScript is used - are launched by
    //     the package manager, so the package manager is the runtime. Apps that invoke a script file directly
    //     (AddNodeApp "server.js" / AddBunApp "server.ts" with no run script) are launched by their fixed runtime
    //     (node/bun) regardless of any package manager.
    //   - Install: a selected package manager also runs at install time, so it must be on PATH even when a
    //     different runtime launches the app - e.g. AddNodeApp(...).WithBun() runs `node server.js` but installs
    //     with `bun`, so both node and bun are required.
    // npm/yarn/pnpm additionally require node (they are Node CLIs); bun does not. This projection is what fixes
    // https://github.com/microsoft/aspire/issues/18625 (AddViteApp(...).WithBun() requires only bun) without
    // dropping the runtime for direct-script apps.
    private static IEnumerable<(string Command, string? HelpLink)> ResolveRequiredCommands(IResource resource, string runtimeCommand)
    {
        resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager);

        // A package manager only replaces the runtime when the app launches through a run script; otherwise the
        // runtime executes the script file directly.
        var launchesViaRunScript = resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out _);
        var runCommand = launchesViaRunScript && packageManager is not null
            ? packageManager.ExecutableName
            : runtimeCommand;

        var commands = new HashSet<string>(StringComparer.Ordinal) { runCommand };

        if (packageManager is not null)
        {
            commands.Add(packageManager.ExecutableName);
        }

        if (commands.Overlaps(s_nodeBasedPackageManagers))
        {
            commands.Add("node");
        }

        return commands.Select(static command => (command, ResolveHelpLink(command)));
    }

    // Maps a required executable to the install/help link surfaced when the command is missing on PATH.
    private static string? ResolveHelpLink(string command) => command switch
    {
        "node" => NodeHelpLink,
        "npm" => NpmHelpLink,
        "bun" => BunHelpLink,
        "deno" => DenoHelpLink,
        "yarn" => YarnHelpLink,
        "pnpm" => PnpmHelpLink,
        // Unknown/custom package manager: no specific install help link.
        _ => null,
    };

    // The default Docker image used for AddBunApp build and runtime stages.
    // Pinned to the major version tag to keep generated Dockerfiles deterministic
    // while still picking up patch updates. The image provides a non-root `bun` user.
    private const string DefaultBunImage = "oven/bun:1";

    // Default .dockerignore content emitted alongside the generated Bun Dockerfile using
    // BuildKit's per-Dockerfile ignore convention. The runtime stage uses `COPY . .` from the
    // build context so an ignore file is required to keep local node_modules, .git, dotenv
    // files, etc. out of the published image. Mirrors the recommendation at
    // https://bun.com/guides/ecosystem/docker.
    private const string DefaultBunBuildContextIgnoreContent = """
        # Generated by Aspire. Author <contextRoot>/.dockerignore to override.
        node_modules
        .git
        .gitignore
        .DS_Store
        npm-debug.log*
        yarn-debug.log*
        yarn-error.log*
        .pnpm-debug.log*
        .env
        .env.*
        .aspire
        aspire-output
        Dockerfile
        Dockerfile.*
        *.Dockerfile.dockerignore
        .dockerignore
        *.tsbuildinfo

        """;

    /// <summary>
    /// Adds a Bun application to the application model. Bun should be available on the PATH.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the Bun application.</param>
    /// <param name="scriptPath">The path to the script (for example, <c>server.ts</c>) relative to <paramref name="appDirectory"/> to run.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// This method executes the script directly using <c>bun &lt;script&gt;</c>. Bun natively runs JavaScript and TypeScript
    /// files so no transpile step is required.
    ///
    /// If the application directory contains a <c>package.json</c> file, Bun will be added as the default package manager.
    /// When publishing to a container, the default base image is <c>oven/bun:1</c> for both the build and runtime stages.
    /// </remarks>
    /// <example>
    /// Add a Bun app to the application model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddBunApp("api", "../api", "server.ts");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<BunAppResource> AddBunApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        appDirectory = Path.GetFullPath(appDirectory, builder.AppHostDirectory);
        var resource = new BunAppResource(name, "bun", appDirectory);

        var resourceBuilder = builder.AddResource(resource)
            .WithBunDefaults()
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
                    // Get custom base image from annotation, if present
                    dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);

                    // Provide a default .dockerignore that publishers emit alongside the generated
                    // Dockerfile using BuildKit's per-Dockerfile ignore convention
                    // (<dockerfile-name>.dockerignore). The runtime stage below copies source
                    // directly from the build context (`COPY . .`), so without an ignore file the
                    // user's local node_modules, .git, etc. would leak into the build context and
                    // into the image. Matches the recommendation at
                    // https://bun.com/guides/ecosystem/docker. The annotation lookup is guarded
                    // because WithDockerfileBuilder always adds a DockerfileBuildAnnotation, but
                    // we want to remain robust if a future refactor changes that.
                    if (dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
                    {
                        dockerfileBuildAnnotation.BuildContextIgnoreContent ??= DefaultBunBuildContextIgnoreContent;
                    }

                    // Bun ships its own runtime, so both stages default to the same Bun image rather than
                    // using a node-based image as in AddNodeApp.
                    var baseBuildImage = baseImageAnnotation?.BuildImage ?? DefaultBunImage;
                    var builderStage = dockerfileContext.Builder
                        .From(baseBuildImage, "build")
                        .EmptyLine()
                        .WorkDir("/app");

                    if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                    {
                        // Initialize the Docker build stage with package manager-specific setup commands.
                        packageManager.InitializeDockerBuildStage?.Invoke(builderStage);

                        var copiedAllSource = false;
                        if (resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                        {
                            copiedAllSource = builderStage.CopyPackageFilesForInstall(packageManager);
                            builderStage.AddInstallCommand(packageManager, installCommand);
                        }

                        if (!copiedAllSource)
                        {
                            builderStage.Copy(".", ".");
                        }

                        if (resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            builderStage.EmptyLine()
                                .Run(BuildPackageScriptCommand(packageManager, buildCommand));
                        }
                    }
                    else
                    {
                        // No package manager, just copy everything
                        builderStage.Copy(".", ".");
                    }

                    var logger = dockerfileContext.Services.GetService<ILogger<JavaScriptAppResource>>();
                    dockerfileContext.Builder.AddContainerFilesStages(dockerfileContext.Resource, logger);

                    // When the package manager exposes production-only install args (e.g. bun's
                    // `--production`), emit a dedicated `prod-deps` stage that installs only the
                    // runtime dependencies. The runtime stage then overlays this stage's
                    // `node_modules` on top of the build output so the final image does not ship
                    // devDependencies. This mirrors the multi-stage pattern recommended at
                    // https://bun.com/guides/ecosystem/docker.
                    JavaScriptPackageManagerAnnotation? prodPackageManager = null;
                    JavaScriptInstallCommandAnnotation? prodInstallCommand = null;
                    var emitProdDepsStage =
                        resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out prodPackageManager) &&
                        resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out prodInstallCommand) &&
                        !string.IsNullOrEmpty(prodInstallCommand.ProductionInstallArgs);

                    if (emitProdDepsStage)
                    {
                        var pm = prodPackageManager!;
                        var install = prodInstallCommand!;
                        var prodDepsStage = dockerfileContext.Builder
                            .From(baseBuildImage, "prod-deps")
                            .EmptyLine()
                            .WorkDir("/app");

                        pm.InitializeDockerBuildStage?.Invoke(prodDepsStage);

                        if (pm.PackageFilesPatterns.Count > 0)
                        {
                            foreach (var packageFilePattern in pm.PackageFilesPatterns)
                            {
                                prodDepsStage.Copy(packageFilePattern.Source, packageFilePattern.Destination);
                            }
                        }
                        else
                        {
                            prodDepsStage.Copy("package.json", "./");
                        }

                        var prodInstallCmd = BuildProductionInstallCommand(pm, install);
                        if (!string.IsNullOrEmpty(pm.CacheMount))
                        {
                            prodDepsStage.Run($"--mount=type=cache,target={pm.CacheMount} {prodInstallCmd}");
                        }
                        else
                        {
                            prodDepsStage.Run(prodInstallCmd);
                        }
                    }

                    var baseRuntimeImage = baseImageAnnotation?.RuntimeImage ?? DefaultBunImage;
                    var runtimeBuilder = dockerfileContext.Builder
                        .From(baseRuntimeImage, "runtime")
                            .EmptyLine()
                            .WorkDir("/app");

                    if (emitProdDepsStage)
                    {
                        // Mirror the multi-stage pattern recommended at https://bun.com/guides/ecosystem/docker:
                        // pull node_modules from the production-only install stage and the rest of the app
                        // source from the build context. The build stage exists for validation/caching but
                        // its filesystem is intentionally not copied here, because Docker's COPY --from=
                        // merges directories and would let devDependencies survive the overlay.
                        //
                        // A matching .dockerignore is emitted next to the published Dockerfile via the
                        // DockerfileBuildAnnotation.BuildContextIgnoreContent property (BuildKit's
                        // <dockerfile-name>.dockerignore convention) so local build artifacts
                        // (node_modules, .git, .aspire, etc.) do not leak into the image via COPY . . below.
                        runtimeBuilder
                            .CopyFrom("prod-deps", "/app/node_modules", "./node_modules")
                            .Copy(".", ".");
                    }
                    else
                    {
                        runtimeBuilder.CopyFrom("build", "/app", "/app");
                    }

                    runtimeBuilder
                        .AddContainerFiles(dockerfileContext.Resource, "/app", logger)
                        .EmptyLine()
                        .Env("NODE_ENV", "production")
                        .EmptyLine()
                        // The official oven/bun images provide a non-root `bun` user (UID 1000).
                        // See https://hub.docker.com/r/oven/bun
                        .User("bun")
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
            // Automatically add bun as the package manager if a package.json file exists
            resourceBuilder.WithBun();
        }

        resourceBuilder.WithVSCodeDebugging(scriptPath, "bun");

        if (builder.ExecutionContext.IsRunMode)
        {
            builder.OnBeforeStart((_, _) =>
            {
                // Set the command to the package manager executable if a WithRunScript was configured.
                // For the default Bun package manager this is a no-op (executable is "bun"), but it correctly
                // handles cases where a user opts into a different package manager (e.g., WithYarn).
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

    private static IResourceBuilder<TResource> WithBunDefaults<TResource>(this IResourceBuilder<TResource> builder) where TResource : JavaScriptAppResource =>
        builder.WithOtlpExporter()
            .WithRequiredCommandsFromPackageManager("bun")
            // Bun honors NODE_ENV for module resolution and runtime mode the same way Node does.
            // See https://bun.com/docs/runtime/env
            .WithEnvironment("NODE_ENV", builder.ApplicationBuilder.Environment.IsDevelopment() ? "development" : "production")
            .WithCertificateTrustConfiguration((ctx) =>
            {
                // Configure Bun's Node-compatible custom-CA hook for append-scope trust.
                // See https://bun.com/blog/bun-v1.3-nodejs-compatibility#node-extra-ca-certs.
                //
                // Important: Bun 1.3.10 and 1.3.14 still fail to trust Aspire's injected
                // self-signed localhost certificate for outgoing HTTPS requests with
                // UNABLE_TO_VERIFY_LEAF_SIGNATURE, even when NODE_EXTRA_CA_CERTS is set.
                // curl --cacert and Node.js with NODE_EXTRA_CA_CERTS accept the same cert.
                // Track the Bun dependency in https://github.com/microsoft/aspire/issues/17455.
                if (ctx.Scope == CertificateTrustScope.Append)
                {
                    ctx.EnvironmentVariables["NODE_EXTRA_CA_CERTS"] = ctx.CertificateBundlePath;
                }
                else
                {
                    // Bun reads NODE_OPTIONS for a subset of Node flags including --use-openssl-ca,
                    // which switches TLS verification to the OS trust store (matching the Override
                    // and System scopes here). See https://bun.com/docs/cli/run#node-options.
                    // This does not work around the Aspire dev-certificate issue above unless that
                    // certificate is trusted by the selected OS/OpenSSL store.
                    if (ctx.EnvironmentVariables.TryGetValue("NODE_OPTIONS", out var existingOptionsObj))
                    {
                        ctx.EnvironmentVariables["NODE_OPTIONS"] = existingOptionsObj switch
                        {
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

    // The default Docker image used for AddDenoApp build and runtime stages.
    // Pin to a concrete tag because denoland/deno does not publish floating major tags.
    // The official image provides a non-root `deno` user.
    private const string DefaultDenoImage = "denoland/deno:2.9.0";

    // Default .dockerignore content emitted alongside the generated Deno Dockerfile using
    // BuildKit's per-Dockerfile ignore convention. The runtime stage copies source from the
    // build stage, but an ignore file keeps local .git, dotenv files, and Aspire artifacts out
    // of the build context. Deno can still materialize node_modules for npm compatibility
    // (`--node-modules-dir=auto` or `manual`), so keep local dependency folders out of
    // the build context just like the Bun/Node variants.
    private const string DefaultDenoBuildContextIgnoreContent = """
        # Generated by Aspire. Author <contextRoot>/.dockerignore to override.
        node_modules
        .git
        .gitignore
        .DS_Store
        .env
        .env.*
        .aspire
        aspire-output
        Dockerfile
        Dockerfile.*
        *.Dockerfile.dockerignore
        .dockerignore
        *.tsbuildinfo

        """;

    /// <summary>
    /// Adds a Deno application to the application model. Deno should be available on the PATH.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the Deno application.</param>
    /// <param name="scriptPath">The path to the script (for example, <c>main.ts</c>) relative to <paramref name="appDirectory"/> to run.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// In run mode this method executes the script directly using <c>deno run -A &lt;script&gt;</c>. Generated
    /// containers use the more restrictive <c>deno run --allow-net --allow-env &lt;script&gt;</c> default. Deno
    /// natively runs JavaScript and TypeScript files so no transpile step is required. Deno's built-in OpenTelemetry
    /// integration is enabled via the <c>OTEL_DENO</c> environment variable, so traces, metrics, and logs flow to the
    /// Aspire dashboard with no application-level SDK wiring.
    ///
    /// The full Deno flag surface (granular permissions, <c>--config</c>/<c>--import-map</c>/<c>--lock</c>, unstable
    /// features, <c>--watch</c>, inspector flags, script args, and the <c>run</c>/<c>task</c>/<c>serve</c> sub-command
    /// modes) can be configured with the fluent <c>WithDeno*</c> methods (for example <see cref="WithDenoAllow"/>,
    /// <see cref="WithDenoConfig"/>, <see cref="WithDenoUnstable"/>, <see cref="WithDenoServe"/>). Configuring any of
    /// these fully replaces the default arg vector, so a Deno workload never has to fall back to <c>AddExecutable</c>.
    ///
    /// If the application directory contains a <c>package.json</c>, <c>deno.json</c>, or <c>deno.jsonc</c> file, Deno will
    /// be added as the default package manager. When publishing to a container, the default base image is
    /// <c>denoland/deno:2.9.0</c> for both the build and runtime stages.
    /// </remarks>
    /// <example>
    /// Add a Deno app to the application model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDenoApp("api", "../api", "main.ts");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<DenoAppResource> AddDenoApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        appDirectory = Path.GetFullPath(appDirectory, builder.AppHostDirectory);
        ValidateDenoScriptPath(scriptPath);
        var resource = new DenoAppResource(name, "deno", appDirectory);

        var resourceBuilder = builder.AddResource(resource)
            .WithDenoDefaults()
            .WithArgs(c =>
            {
                // An explicit Deno command-line annotation (configured via the WithDeno* fluent flag methods)
                // composes with WithRunScript. If no fluent mode method selected run/task/serve explicitly,
                // a run script still launches through `deno task <name>` and the Deno flags that are valid for
                // task launches are preserved.
                if (c.Resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var denoCommandLine))
                {
                    var serveEndpointArguments = denoCommandLine.Mode == DenoCommandMode.Serve
                        ? GetDenoServeEndpointArguments(c.Resource, c.ExecutionContext.IsPublishMode)
                        : null;
                    c.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runScript);
                    c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager);
                    foreach (var arg in BuildDenoArgs(
                        denoCommandLine,
                        scriptPath,
                        serveEndpointArguments,
                        runScript: runScript,
                        packageManager: packageManager))
                    {
                        c.Args.Add(arg);
                    }
                }
                // If the JavaScriptRunScriptAnnotation is present, use that to run the app via `deno task <name>`.
                else if (c.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runCommand) &&
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
                    // Direct execution differs from Node/Bun: Deno requires the `run` subcommand and, unlike
                    // Node/Bun, runs under a deny-by-default permission model. Aspire injects configuration via
                    // environment variables (PORT, OTLP endpoints, cert paths) and the app reads them with
                    // Deno.env / opens sockets with Deno.serve, both of which throw NotCapable without an explicit
                    // grant. `-A` (allow-all) is used to keep the developer experience on par with Node/Bun, whose
                    // runtimes are permissive by default. Users who want least-privilege can opt out with
                    // WithDenoAllowAll(false) and add explicit permission flags via the WithDeno* methods.
                    c.Args.Add("run");
                    c.Args.Add("-A");
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
                    // Get custom base image from annotation, if present
                    dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);

                    // Provide a default .dockerignore emitted alongside the generated Dockerfile using
                    // BuildKit's per-Dockerfile ignore convention (<dockerfile-name>.dockerignore). The
                    // runtime stage copies source from the build stage, so an ignore file keeps the user's
                    // local .git, dotenv files, and Aspire output out of the image.
                    if (dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
                    {
                        dockerfileBuildAnnotation.BuildContextIgnoreContent ??= DefaultDenoBuildContextIgnoreContent;
                    }

                    ThrowIfUnsupportedDenoDockerfileOptions(dockerfileContext.Resource);

                    // Deno ships its own runtime, so both stages default to the same Deno image. Unlike the Bun
                    // variant there is no separate production-dependency install stage: Deno caches remote and
                    // npm dependencies under DENO_DIR. Direct run/serve entrypoints pre-populate that cache in
                    // the build stage and use --cached-only at runtime. Task entrypoints are opaque shell
                    // commands in deno.json, so Aspire cannot safely infer their module graph.
                    var baseBuildImage = baseImageAnnotation?.BuildImage ?? DefaultDenoImage;
                    var buildStage = dockerfileContext.Builder
                        .From(baseBuildImage, "build");

                    // Package-script builds install from the manifest layer before copying the remaining source.
                    // Direct run/serve builds copy the full module graph first, then cache it with the same
                    // resolution and lock flags used by the runtime entrypoint.
                    dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode);
                    if (publishMode?.Mode == JavaScriptPublishMode.PackageScript)
                    {
                        if (!dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) ||
                            !dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                        {
                            throw new InvalidOperationException("PublishAsPackageScript requires a Deno package manager. Add a deno.json file or call WithDeno().");
                        }

                        buildStage.EmptyLine();
                        packageManager.InitializeDockerBuildStage?.Invoke(buildStage);
                        buildStage
                            .EmptyLine()
                            .WorkDir("/app");

                        var copiedAllSource = buildStage.CopyPackageFilesForInstall(packageManager);
                        buildStage.AddInstallCommand(packageManager, installCommand);

                        if (!copiedAllSource)
                        {
                            buildStage.Copy(".", ".");
                        }
                    }
                    else
                    {
                        var denoCacheCommand = BuildDenoCacheCommand(dockerfileContext.Resource, scriptPath, resource.WorkingDirectory);
                        buildStage
                            .EmptyLine()
                            // Pin DENO_DIR to a deterministic path so the runtime stage can copy the cache
                            // regardless of the base image's own default. The official denoland/deno image
                            // already uses /deno-dir, but a custom build image (WithDockerfileBaseImage) may not.
                            .Env("DENO_DIR", "/deno-dir")
                            .EmptyLine()
                            .WorkDir("/app")
                            .Copy(".", ".")
                            .EmptyLine()
                            .Run(denoCacheCommand);
                    }

                    if (dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                    {
                        if (!dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                        {
                            throw new InvalidOperationException("WithBuildScript requires a Deno package manager. Add a deno.json file or call WithDeno().");
                        }

                        buildStage
                            .EmptyLine()
                            .Run(BuildPackageScriptCommand(packageManager, buildCommand));
                    }

                    var logger = dockerfileContext.Services.GetService<ILogger<JavaScriptAppResource>>();
                    dockerfileContext.Builder.AddContainerFilesStages(dockerfileContext.Resource, logger);

                    var hasCustomRuntimeImage = baseImageAnnotation?.RuntimeImage is not null;
                    var baseRuntimeImage = baseImageAnnotation?.RuntimeImage ?? DefaultDenoImage;
                    var runtimeStage = dockerfileContext.Builder
                        .From(baseRuntimeImage, "runtime")
                            .EmptyLine()
                            // Match the build stage's DENO_DIR so the copied cache is discovered at runtime.
                            .Env("DENO_DIR", "/deno-dir")
                            .EmptyLine()
                            .WorkDir("/app");

                    if (hasCustomRuntimeImage)
                    {
                        runtimeStage
                            .CopyFrom("build", "/app", "/app")
                            // Ship the pre-populated dependency cache so direct run/serve container starts
                            // resolve everything locally instead of re-fetching from the network.
                            .CopyFrom("build", "/deno-dir", "/deno-dir");
                    }
                    else
                    {
                        runtimeStage
                            .CopyFrom("build", "/app", "/app", DenoDefaultUserAndGroup)
                            // Ship the pre-populated dependency cache so direct run/serve container starts
                            // resolve everything locally instead of re-fetching from the network.
                            .CopyFrom("build", "/deno-dir", "/deno-dir", DenoDefaultUserAndGroup);
                    }

                    runtimeStage
                            .AddContainerFiles(dockerfileContext.Resource, "/app", logger)
                            .EmptyLine()
                            // Deno honors NODE_ENV in its Node-compatibility mode (npm: specifiers, package.json
                            // "exports" conditions) exactly as Node/Bun do. Match the Bun publish block.
                            .Env("NODE_ENV", "production")
                            .EmptyLine();

                    if (!hasCustomRuntimeImage)
                    {
                        // The default denoland/deno images provide a non-root `deno` user. Respect custom runtime
                        // images' configured defaults because not every supported Deno variant defines that user
                        // (for example, denoland/deno:2.1-distroless).
                        // See https://github.com/denoland/deno_docker
                        runtimeStage
                            .User(DenoDefaultUser)
                            .EmptyLine();
                    }

                    runtimeStage.Entrypoint(BuildDenoEntrypoint(dockerfileContext.Resource, resource.Command, scriptPath));
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

        // Automatically add Deno as the package manager if a Deno or npm project manifest exists. Deno projects are
        // commonly configured through deno.json/deno.jsonc (tasks, imports), and Deno also honors package.json in its
        // Node compatibility mode.
        if (File.Exists(Path.Combine(appDirectory, "package.json")) ||
            File.Exists(Path.Combine(appDirectory, "deno.json")) ||
            File.Exists(Path.Combine(appDirectory, "deno.jsonc")))
        {
            resourceBuilder.WithDeno();
        }

        resourceBuilder.WithVSCodeDebugging(scriptPath, "deno");

        if (builder.ExecutionContext.IsRunMode)
        {
            builder.OnBeforeStart((_, _) =>
            {
                ThrowIfDenoOptionsConflictWithPackageManager(resourceBuilder.Resource);

                // Set the command to the package manager executable if a WithRunScript was configured.
                // For the default Deno package manager this is a no-op (executable is "deno"), but it keeps the
                // command consistent if a user opts into a different package manager.
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

    private static IResourceBuilder<TResource> WithDenoDefaults<TResource>(this IResourceBuilder<TResource> builder) where TResource : JavaScriptAppResource
    {
        // Deno has first-class, built-in OpenTelemetry support. Setting OTEL_DENO=true enables automatic export
        // of traces, metrics, and logs with no application-level SDK required. Enable it only when an OTLP HTTP
        // endpoint is injected; dashboard-free AppHosts remain valid and should not require an observability
        // backend merely because they host a Deno workload.
        //
        // Deno's native exporter sends OTLP as Protobuf over HTTP, so request that dashboard endpoint instead
        // of Aspire's default gRPC preference.
        //
        // No `--unstable-otel` flag is emitted: native OTel is STABLE on the pinned Deno 2.9.0 image.
        // Verified empirically on Deno 2.9.0 (2026-07) — `OTEL_DENO=true`
        // alone activates and exports traces/metrics/logs; `--unstable-otel` is no longer listed by
        // `deno run --help=unstable` and is only a backward-compat no-op. OTEL_DENO accepts only the literal
        // "true"/"false" (not "1"), which is what we emit.
        // See https://docs.deno.com/runtime/fundamentals/open_telemetry/
        builder.WithDenoOtlpExporter()
            .WithRequiredCommandsFromPackageManager("deno")
            // Deno honors NODE_ENV in its Node-compatibility mode (npm: specifier resolution, package.json
            // "exports" conditions) the same way Node/Bun do. Mirror the Bun defaults so npm-compat behaves.
            // See https://docs.deno.com/runtime/reference/env_variables/
            .WithEnvironment("NODE_ENV", builder.ApplicationBuilder.Environment.IsDevelopment() ? "development" : "production")
            .WithCertificateTrustConfiguration((ctx) =>
            {
                if (ctx.Scope is CertificateTrustScope.Append or CertificateTrustScope.Override or CertificateTrustScope.System)
                {
                    // DENO_CERT loads the configured PEM certificate file into Deno's trust store. The optional
                    // DENO_TLS_CA_STORE value below chooses whether that bundle is combined with Deno's Mozilla
                    // store, replaces it, or is combined with the operating system store.
                    // See https://docs.deno.com/runtime/reference/env_variables/#special-environment-variables
                    ctx.EnvironmentVariables["DENO_CERT"] = ctx.CertificateBundlePath;

                    // Deno's built-in OTLP exporter is implemented in Rust and uses the OpenTelemetry certificate
                    // variable rather than DENO_CERT for HTTPS exporter trust.
                    // See https://opentelemetry.io/docs/specs/otel/protocol/exporter/
                    ctx.EnvironmentVariables["OTEL_EXPORTER_OTLP_CERTIFICATE"] = ctx.CertificateBundlePath;

                    if (ctx.Scope == CertificateTrustScope.Override)
                    {
                        ctx.EnvironmentVariables["DENO_TLS_CA_STORE"] = "";
                    }
                    else if (ctx.Scope == CertificateTrustScope.System)
                    {
                        ctx.EnvironmentVariables["DENO_TLS_CA_STORE"] = "system";
                    }
                }

                return Task.CompletedTask;
            });

        return builder;
    }

    private static IResourceBuilder<TResource> WithDenoOtlpExporter<TResource>(this IResourceBuilder<TResource> builder)
        where TResource : IResourceWithEnvironment
    {
        builder.WithOtlpExporterIfEndpointAvailable(OtlpProtocol.HttpProtobuf);

        var exporter = builder.Resource.Annotations.OfType<OtlpExporterAnnotation>().Last();
        builder.Resource.Annotations.Remove(exporter);
        builder.Resource.Annotations.Add(new DenoOtlpExporterAnnotation
        {
            RequiredProtocol = exporter.RequiredProtocol,
        });

        return builder;
    }

    private static void ValidateDenoScriptPath(string scriptPath)
    {
        if (!TryNormalizeDenoContainerRelativePath(scriptPath, out _))
        {
            throw new ArgumentException("The script path must resolve inside the Deno application directory.", nameof(scriptPath));
        }
    }

    private static bool IsWindowsDriveQualifiedPath(string path) =>
        path.Length >= 2 &&
        char.IsAsciiLetter(path[0]) &&
        path[1] == ':';

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
    [AspireExport]
    public static IResourceBuilder<JavaScriptAppResource> AddJavaScriptApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string runScriptName = DefaultJavaScriptRunScriptName)
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
    /// <exception cref="InvalidOperationException">
    /// Thrown during generated Dockerfile creation when this method is used with a Deno app added by
    /// <c>AddDenoApp</c>. Use <c>AddJavaScriptApp(...).WithDeno()</c> or provide a custom Dockerfile.
    /// </exception>
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
    /// <exception cref="InvalidOperationException">
    /// Thrown during generated Dockerfile creation when this method is used with a Deno app added by
    /// <c>AddDenoApp</c>. Use <c>AddJavaScriptApp(...).WithDeno()</c> or provide a custom Dockerfile.
    /// </exception>
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

#pragma warning disable ASPIREEXPORT009 // Polyglot entry point — collision is intentional
    /// <summary>
    /// Publishes the JavaScript application as a standalone static website using YARP.
    /// </summary>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("publishAsStaticWebsite")]
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
               .WithOtlpExporterIfMissing();

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
    /// <exception cref="InvalidOperationException">
    /// Thrown during generated Dockerfile creation when this method is used with a Deno app added by
    /// <c>AddDenoApp</c>. Use <c>AddJavaScriptApp(...).WithDeno()</c> or provide a custom Dockerfile.
    /// </exception>
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
    [AspireExport]
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
               .WithOtlpExporterIfMissing()
               .WithEnvironment("HOST", "0.0.0.0")
               .WithEnvironment("HOSTNAME", "0.0.0.0");

        if (builder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            dockerfileBuildAnnotation.HasEntrypoint = true;
        }

        return builder;
    }

    /// <summary>
    /// Configures the JavaScript application to publish as a server that runs a package manager script at runtime.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript resource type.</typeparam>
    /// <param name="builder">The JavaScript resource builder.</param>
    /// <param name="scriptName">
    /// The name of the script to run in the published container. For Node.js and Bun applications this is a
    /// <c>package.json</c> script; for Deno applications it is a task defined in <c>deno.json</c>.
    /// For example, <c>start</c> invokes the configured package manager's run command for the <c>start</c> script,
    /// such as <c>npm run start</c>, <c>pnpm run start</c>, <c>yarn run start</c>, <c>bun run start</c>, or
    /// <c>deno task start</c>.
    /// </param>
    /// <param name="runScriptArguments">
    /// Optional arguments appended after the script name at runtime,
    /// such as <c>-- --port "$PORT"</c>.
    /// </param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Use this method for frameworks where the production server depends on packages resolved at runtime, either
    /// from <c>node_modules</c> or from the Deno dependency cache. The resulting container includes the full
    /// application with its production dependencies already installed.
    /// </para>
    /// <para>
    /// This method is appropriate for frameworks like Nuxt (where <c>useAsyncData</c>/<c>useFetch</c> requires the
    /// full Nitro environment), Remix (where <c>react-router-serve</c> is an npm dependency), and Astro SSR
    /// (where the built entry point imports unbundled <c>@astrojs/*</c> packages).
    /// </para>
    /// <para>
    /// For Deno applications the generated container runs <c>deno task &lt;scriptName&gt;</c> and copies the populated
    /// <c>DENO_DIR</c> cache from the build stage, so whatever the build resolved is already present at runtime.
    /// Unlike the Node.js and Bun package managers there is no separate production install step, because the build
    /// stage runs <c>deno install</c> and <c>DENO_DIR</c> is carried forward as-is rather than being pruned.
    /// </para>
    /// <para>
    /// <c>deno install</c> only resolves the dependencies declared in <c>deno.json</c> or <c>package.json</c>. An
    /// import written as a bare specifier in source, such as <c>import { assert } from "jsr:@std/assert"</c>, is not
    /// declared anywhere the installer can see, so it is fetched on first use and the container needs network access
    /// at startup. The same applies to anything reachable only from inside the build script's own task command, which
    /// Aspire cannot inspect. Add those imports to the <c>imports</c> map in <c>deno.json</c> if the container has to
    /// start without network access.
    /// </para>
    /// <para>
    /// For frameworks that produce a self-contained server artifact that does not require <c>node_modules</c>,
    /// use <see cref="PublishAsNodeServer{TResource}"/> instead for a smaller runtime image.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<TResource> PublishAsPackageScript<TResource>(this IResourceBuilder<TResource> builder, string scriptName = "start", string? runScriptArguments = null)
        where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(scriptName);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var annotation = new JavaScriptPublishModeAnnotation(JavaScriptPublishMode.PackageScript)
        {
            ScriptName = scriptName,
            RunScriptArguments = runScriptArguments
        };

        builder.WithAnnotation(annotation)
               .ClearContainerFilesSources()
               .WithOtlpExporterIfMissing()
               .WithEnvironment("HOST", "0.0.0.0")
               .WithEnvironment("HOSTNAME", "0.0.0.0");

        if (builder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation))
        {
            dockerfileBuildAnnotation.HasEntrypoint = true;
        }

        return builder;
    }

    private static bool CopyPackageFilesForInstall(this DockerfileStage builderStage, JavaScriptPackageManagerAnnotation packageManager)
    {
        // deno.json can reference sibling import maps, workspace members, and other files that `deno install`
        // resolves immediately. Copy the complete build context before install because the manifest files alone
        // are not a self-contained dependency description.
        if (packageManager.ExecutableName == "deno")
        {
            builderStage.Copy(".", ".");
            return true;
        }

        if (packageManager.PackageFilesPatterns.Count > 0)
        {
            foreach (var packageFilePattern in packageManager.PackageFilesPatterns)
            {
                builderStage.Copy(packageFilePattern.Source, packageFilePattern.Destination);
            }

            return false;
        }

        builderStage.Copy(".", ".");
        return true;
    }

    private static IResourceBuilder<TResource> WithOtlpExporterIfMissing<TResource>(this IResourceBuilder<TResource> builder)
        where TResource : JavaScriptAppResource
    {
        if (!builder.Resource.Annotations.OfType<OtlpExporterAnnotation>().Any())
        {
            builder.WithOtlpExporter();
        }

        return builder;
    }

    private static void AddInstallCommand(this DockerfileStage builderStage, JavaScriptPackageManagerAnnotation packageManager, JavaScriptInstallCommandAnnotation installCommand)
    {
        // Use BuildKit cache mount for package manager cache if available
        var installCmd = JoinDockerShellCommand([packageManager.ExecutableName, .. installCommand.Args]);
        if (!string.IsNullOrEmpty(packageManager.CacheMount))
        {
            builderStage.Run($"--mount=type=cache,target={packageManager.CacheMount} {installCmd}");
        }
        else
        {
            builderStage.Run(installCmd);
        }
    }

    /// <summary>
    /// Builds the <c>RUN</c> command that executes a package script during the Docker build, for example
    /// <c>npm run build</c> or <c>deno task build</c>.
    /// </summary>
    /// <remarks>
    /// The script name and its arguments are caller-supplied and are each a single logical token, so they are
    /// shell-quoted. A script named <c>build prod</c> would otherwise emit <c>RUN npm run build prod</c>, which
    /// runs the <c>build</c> script with an extra argument instead of the script the caller named.
    /// </remarks>
    private static string BuildPackageScriptCommand(JavaScriptPackageManagerAnnotation packageManager, JavaScriptBuildScriptAnnotation buildCommand)
    {
        var commandArgs = new List<string>() { packageManager.ExecutableName };
        if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
        {
            commandArgs.Add(packageManager.ScriptCommand);
        }
        commandArgs.Add(buildCommand.ScriptName);
        commandArgs.AddRange(buildCommand.Args);

        return JoinDockerShellCommand(commandArgs);
    }

    /// <summary>
    /// Builds the production dependency install command, appending the package manager's production-only flag.
    /// </summary>
    /// <remarks>
    /// <see cref="JavaScriptInstallCommandAnnotation.ProductionInstallArgs"/> is deliberately not quoted. Unlike the
    /// entries in <see cref="JavaScriptInstallCommandAnnotation.Args"/>, which are individual tokens, it is documented
    /// as a pre-formatted flag fragment (for example <c>--omit=dev</c>), so quoting it would break a caller who
    /// supplies more than one flag.
    /// </remarks>
    private static string BuildProductionInstallCommand(JavaScriptPackageManagerAnnotation packageManager, JavaScriptInstallCommandAnnotation installCommand)
        => $"{JoinDockerShellCommand([packageManager.ExecutableName, .. installCommand.Args])} {installCommand.ProductionInstallArgs}";

    /// <summary>
    /// Joins arguments into a single command string for Dockerfile <c>RUN</c>, quoting each argument so that
    /// values containing spaces or shell metacharacters survive as one token.
    /// </summary>
    private static string JoinDockerShellCommand(IEnumerable<string> args)
        => string.Join(' ', args.Select(QuoteDockerShellArgument));

    /// <summary>
    /// Quotes a single argument for a Dockerfile <c>RUN</c> instruction, which is executed through <c>/bin/sh -c</c>.
    /// </summary>
    /// <remarks>
    /// Uses a fail-closed allowlist: anything outside the set of characters that are unambiguously inert to the shell
    /// is quoted. A denylist would silently pass through any metacharacter nobody enumerated.
    /// </remarks>
    private static string QuoteDockerShellArgument(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        if (value.All(IsUnquotedDockerShellArgumentCharacter))
        {
            return value;
        }

        // Single-quote the argument and use the standard POSIX shell escape sequence for embedded quotes:
        //   import map's.json -> 'import map'"'"'s.json'
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static bool IsUnquotedDockerShellArgumentCharacter(char c) =>
        c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-'
            or '_'
            or '.'
            or '/'
            or ':'
            or '=';

    private static string GetPackageScriptRuntimeImage(
        string appDirectory,
        IServiceProvider services,
        DockerfileBaseImageAnnotation? baseImageAnnotation,
        JavaScriptPackageManagerAnnotation packageManager,
        string buildImage)
    {
        if (!string.IsNullOrEmpty(baseImageAnnotation?.RuntimeImage))
        {
            return baseImageAnnotation.RuntimeImage;
        }

        return packageManager.ResolvePackageScriptRuntimeImage?.Invoke(buildImage)
            ?? GetDefaultBaseImage(appDirectory, "alpine", services);
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
                        // Get custom base image from annotation, if present. A caller can configure only a runtime
                        // image, which leaves BuildImage null, so fall back to the package manager's own image
                        // before the Node.js default - bun and deno are absent from the Node.js images.
                        dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
                        var baseImage = baseImageAnnotation?.BuildImage
                            ?? packageManager.DefaultBuildImage
                            ?? GetDefaultBaseImage(appDirectory, "slim", dockerfileContext.Services);

                        var dockerBuilder = publishMode is not null
                            ? dockerfileContext.Builder.From(baseImage, "build").WorkDir("/app")
                            : dockerfileContext.Builder.From(baseImage).WorkDir("/app");

                        // Initialize the Docker build stage with package manager-specific setup commands
                        // for the default JavaScript app builder (used by Vite and other build-less apps).
                        packageManager.InitializeDockerBuildStage?.Invoke(dockerBuilder);

                        var copiedAllSource = dockerBuilder.CopyPackageFilesForInstall(packageManager);

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
                            dockerBuilder.Run(BuildPackageScriptCommand(packageManager, buildCommand));
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
                            case JavaScriptPublishMode.PackageScript:
                            {
                                var runtimeImage = GetPackageScriptRuntimeImage(appDirectory, dockerfileContext.Services, baseImageAnnotation, packageManager, baseImage);
                                var runCommand = string.IsNullOrWhiteSpace(publishMode.RunScriptArguments)
                                    ? $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.ScriptName}"
                                    : $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.ScriptName} {publishMode.RunScriptArguments}";

                                if (packageManager.ExecutableName == "deno")
                                {
                                    var usesDefaultDenoRuntimeImage = string.Equals(runtimeImage, DefaultDenoImage, StringComparison.Ordinal);
                                    var denoRuntimeStage = dockerfileContext.Builder
                                        .From(runtimeImage, "runtime")
                                        .WorkDir("/app");

                                    if (usesDefaultDenoRuntimeImage)
                                    {
                                        denoRuntimeStage.CopyFrom("build", "/app", "/app", DenoDefaultUserAndGroup);
                                    }
                                    else
                                    {
                                        denoRuntimeStage.CopyFrom("build", "/app", "/app");
                                    }

                                    // Carry the populated dependency store across stages so the container does not
                                    // re-download dependencies on first run.
                                    denoRuntimeStage.Env("DENO_DIR", DenoCacheDirectory);
                                    if (usesDefaultDenoRuntimeImage)
                                    {
                                        denoRuntimeStage.CopyFrom("build", DenoCacheDirectory, DenoCacheDirectory, DenoDefaultUserAndGroup);
                                    }
                                    else
                                    {
                                        denoRuntimeStage.CopyFrom("build", DenoCacheDirectory, DenoCacheDirectory);
                                    }

                                    packageManager.InitializeDockerRuntimeStage?.Invoke(denoRuntimeStage);

                                    denoRuntimeStage
                                        .Env("NODE_ENV", "production");

                                    if (usesDefaultDenoRuntimeImage)
                                    {
                                        denoRuntimeStage.User(DenoDefaultUser);
                                    }

                                    // Exec form (no `sh -c`) so the container also works with shell-less Deno
                                    // runtime images such as denoland/deno:*-distroless.
                                    denoRuntimeStage.Entrypoint(BuildDenoPackageScriptEntrypoint(
                                        packageManager.ExecutableName,
                                        packageManager.ScriptCommand ?? "run",
                                        publishMode.ScriptName!,
                                        publishMode.RunScriptArguments));
                                    break;
                                }

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
                                    throw new InvalidOperationException($"Package manager '{packageManager.ExecutableName}' does not have ProductionInstallArgs configured, which is required for PublishAsPackageScript.");
                                }

                                var prodInstallCmd = BuildProductionInstallCommand(packageManager, installAnnotation);
                                if (!string.IsNullOrEmpty(packageManager.CacheMount))
                                {
                                    prodDepsStage.Run($"--mount=type=cache,target={packageManager.CacheMount} {prodInstallCmd}");
                                }
                                else
                                {
                                    prodDepsStage.Run(prodInstallCmd);
                                }

                                // Runtime stage: copy build output then overlay prod deps
                                var runtimeStage = dockerfileContext.Builder
                                    .From(runtimeImage, "runtime")
                                    .WorkDir("/app")
                                    .CopyFrom("build", "/app", "/app")
                                    .CopyFrom("prod-deps", "/app/node_modules", "./node_modules");

                                packageManager.InitializeDockerRuntimeStage?.Invoke(runtimeStage);

                                runtimeStage
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

        if (builder.ExecutionContext.IsPublishMode &&
            builder.TryCreateResourceBuilder<ContainerResource>(resource.Name, out var containerBuilder))
        {
            var validationStepName = $"validate-javascript-dockerfile-run-script-{resource.Name}";

            Task WriteValidatedContainerAsync(ManifestPublishingContext context)
            {
                ValidateExistingDockerfileRunScript(resource, containerBuilder.Resource);
                return context.WriteContainerAsync(containerBuilder.Resource);
            }

            resourceBuilder.WithManifestPublishingCallback(WriteValidatedContainerAsync);
            containerBuilder.WithManifestPublishingCallback(WriteValidatedContainerAsync);
            containerBuilder.WithAnnotation(new PipelineStepAnnotation(_ => new PipelineStep
            {
                Name = validationStepName,
                Description = $"Validates that JavaScript app '{resource.Name}' does not publish an ignored run script with an existing Dockerfile.",
                RequiredBySteps = [WellKnownPipelineSteps.Build, WellKnownPipelineSteps.Publish],
                Resource = containerBuilder.Resource,
                Action = _ =>
                {
                    ValidateExistingDockerfileRunScript(resource, containerBuilder.Resource);
                    return Task.CompletedTask;
                }
            }));
        }

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

    private static void ValidateExistingDockerfileRunScript(JavaScriptAppResource resource, ContainerResource containerResource)
    {
        if (containerResource.Entrypoint is not null ||
            !containerResource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation) ||
            dockerfileBuildAnnotation.DockerfileFactory is not null ||
            !containerResource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runScript))
        {
            return;
        }

        // The user's effective run-script intent is captured by the last annotation: AddJavaScriptApp
        // always adds one with the supplied runScriptName, and any subsequent WithRunScript call
        // appends another. Comparing the last annotation against the default avoids false positives
        // when the user re-states the default explicitly (e.g. .WithRunScript("dev")).
        var hasExplicitRunScript =
            !string.Equals(runScript.ScriptName, DefaultJavaScriptRunScriptName, StringComparison.Ordinal) ||
            runScript.Args is { Length: > 0 };

        if (!hasExplicitRunScript)
        {
            return;
        }

        // Include the args in the message when they are the trigger, so the user can see why
        // a default-named script (e.g. "dev") still produced a conflict.
        var argsClause = runScript.Args is { Length: > 0 }
            ? $" with args [{string.Join(", ", runScript.Args)}]"
            : string.Empty;

        // Existing Dockerfiles are user-authored, so Aspire cannot safely assume that replacing
        // their entrypoint with a package-manager script will work for the image shape.
        // If the user provides an explicit container entrypoint above, honor it; otherwise fail
        // instead of silently publishing an image that ignores the requested run script.
        throw new DistributedApplicationException(
            $"JavaScript app resource '{resource.Name}' is configured to run script '{runScript.ScriptName}'{argsClause}, but publish is using the existing Dockerfile '{dockerfileBuildAnnotation.DockerfilePath}'. " +
            "An existing Dockerfile entrypoint cannot be changed automatically from runScriptName or WithRunScript. " +
            "Remove or rename the Dockerfile so Aspire can generate one, or call PublishAsDockerFile(...) and set the container entrypoint explicitly.");
    }

    /// <summary>
    /// Adds a Vite app to the distributed application builder.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the Vite app.</param>
    /// <param name="appDirectory">The path to the directory containing the Vite app.</param>
    /// <param name="runScriptName">The name of the script that runs the Vite app. Defaults to "dev".</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
    public static IResourceBuilder<ViteAppResource> AddViteApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string runScriptName = "dev")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);

        appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, appDirectory));
        var appHostId = builder.Configuration["AppHost:Sha256"]![..10].ToLowerInvariant();
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
                        string s when !string.IsNullOrEmpty(s) && !s.StartsWith("--", StringComparison.Ordinal) => s,
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

                        // Find the nearest node_modules directory by walking up from the app directory.
                        // This handles package managers that hoist dependencies (e.g. yarn workspaces)
                        // where node_modules lives at the repo root rather than in the app directory.
                        // Writing inside node_modules ensures Node.js module resolution can find
                        // bare imports like 'vite' in the generated wrapper config.
                        var nodeModulesDir = FindNearestNodeModules(appDirectory);
                        if (nodeModulesDir is null)
                        {
                            var resourceLoggerService = ctx.ExecutionContext.Services.GetRequiredService<ResourceLoggerService>();
                            var resourceLogger = resourceLoggerService.GetLogger(resource);
                            resourceLogger.LogWarning("Could not find a node_modules directory in or above '{AppDirectory}' for resource '{ResourceName}'. Automatic HTTPS configuration won't be available. Ensure packages are installed before starting the app.", appDirectory, resource.Name);
                            ctx.Arguments.Add("--config");
                            ctx.Arguments.Add(configTarget);
                            return;
                        }

                        // Use the same per-AppHost discriminator as persistent resource names so concurrent
                        // AppHosts sharing a hoisted node_modules directory cannot overwrite each other's wrappers.
                        var aspireConfigDir = Path.Join(nodeModulesDir, ".aspire", appHostId, resource.Name);
                        Directory.CreateDirectory(aspireConfigDir);

                        // Compute the relative path from the wrapper location to the original config
                        var relativeConfigPath = Path.GetRelativePath(aspireConfigDir, absoluteConfigPath).Replace("\\", "/");

                        // Generate an Aspire specific Vite config file that wraps the user's original config with HTTPS support
                        var aspireConfig = AspireViteConfig
                            .Replace(AspireViteConfigPathToken, relativeConfigPath, StringComparison.Ordinal)
                            .Replace(AspireViteAbsoluteConfigToken, absoluteConfigPath.Replace("\\", "\\\\"), StringComparison.Ordinal);
                        var aspireConfigPath = Path.Join(aspireConfigDir, $"aspire.{Path.GetFileName(configTarget)}");
                        File.WriteAllText(aspireConfigPath, aspireConfig);

                        // Override the path to the Vite config file to use the Aspire generated one
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
                        var resourceLoggerService = ctx.ExecutionContext.Services.GetRequiredService<ResourceLoggerService>();
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
            // adding a new one. The user explicitly opted into HTTPS via WithHttpsDeveloperCertificate(), so the scheme
            // change is unconditional here.
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
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
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
    [AspireExport]
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
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
            });

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
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Bun forwards script arguments without requiring the <c>--</c> command separator, so this method configures the resource to omit it.
    /// When publishing and a bun lockfile (<c>bun.lock</c> or <c>bun.lockb</c>) is present, <c>--frozen-lockfile</c> is used by default.
    /// Publishing to a container requires Bun to be present in the build image. This method configures a Bun build image when one is not already specified.
    /// <see cref="PublishAsPackageScript{TResource}"/> also uses the Bun image for the runtime stage unless a custom runtime image is configured.
    /// To use a specific Bun version, configure a custom build image (for example, <c>oven/bun:&lt;tag&gt;</c>) using <see cref="ContainerResourceBuilderExtensions.WithDockerfileBaseImage{T}(IResourceBuilder{T}, string?, string?)"/>.
    /// </remarks>
    /// <ats-remarks />
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
    [AspireExport]
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
                ResolvePackageScriptRuntimeImage = buildImage => buildImage,
                DefaultBuildImage = DefaultBunImage,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs])
            {
                ProductionInstallArgs = "--production"
            });

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

    /// <summary>
    /// Configures the JavaScript resource to use Deno as the package manager.
    /// </summary>
    /// <typeparam name="TResource">The type of the JavaScript application resource being configured.</typeparam>
    /// <param name="resource">The JavaScript application resource builder.</param>
    /// <param name="install">
    /// When <see langword="true"/>, creates an installer resource that runs <c>deno install</c> before the application
    /// starts. Defaults to <see langword="false"/>: unlike npm/Bun, Deno does not require a separate install step —
    /// <c>deno run</c> fetches and caches dependencies under <c>DENO_DIR</c> on first use — so no installer is wired by
    /// default. Set to <see langword="true"/> to pre-cache dependencies or to materialize a <c>node_modules</c> folder
    /// for Node compatibility.
    /// </param>
    /// <param name="installArgs">Additional command-line arguments passed to <c>deno install</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Package scripts are run through Deno's task runner (<c>deno task &lt;name&gt;</c>) rather than <c>run</c>.
    /// Publishing to a container requires Deno to be present in the build image. This method configures a Deno build
    /// image (<c>denoland/deno:2.9.0</c>) when one is not already specified.
    /// </remarks>
    /// <ats-remarks />
    /// <example>
    /// Run a Deno app using a <c>deno.json</c> task:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDenoApp("api", "../api", "main.ts")
    ///        .WithDeno()
    ///        .WithRunScript("dev");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    [Experimental("ASPIREDENO001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<TResource> WithDeno<TResource>(this IResourceBuilder<TResource> resource, bool install = false, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        var workingDirectory = resource.Resource.WorkingDirectory;

        installArgs ??= [];

        // Copy the manifest and lockfile first for better layer caching in publish-mode Dockerfiles.
        var packageFilesSourcePattern = "";
        foreach (var manifest in new[] { "deno.json", "deno.jsonc", "deno.lock", "package.json" })
        {
            if (File.Exists(Path.Combine(workingDirectory, manifest)))
            {
                packageFilesSourcePattern += packageFilesSourcePattern.Length == 0 ? manifest : $" {manifest}";
            }
        }

        var packageManager = new JavaScriptPackageManagerAnnotation("deno", runScriptCommand: "task")
        {
            // Deno's task runner forwards script arguments without requiring the `--` separator.
            CommandSeparator = null,
            ResolvePackageScriptRuntimeImage = buildImage => buildImage,
            DefaultBuildImage = DefaultDenoImage,
            // Deliberately no BuildKit cache mount. For npm/bun/pnpm the mount only holds a download cache
            // while the resolved dependencies still land in /app/node_modules, so discarding the mount at the
            // end of the build is harmless. For Deno, DENO_DIR *is* the dependency store, so mounting it would
            // leave the runtime image with no dependencies and force a re-download on first run. Instead the
            // cache is written into the build stage layer and copied into the runtime stage, which is what
            // Deno's own Docker guidance recommends. See https://docs.deno.com/runtime/reference/docker/.
            InitializeDockerBuildStage = stage => stage.Env("DENO_DIR", DenoCacheDirectory),
        };

        if (packageFilesSourcePattern.Length > 0)
        {
            packageManager.PackageFilesPatterns.Add(new CopyFilePattern(packageFilesSourcePattern, "./"));
        }

        resource
            .WithAnnotation(packageManager)
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs]));

        if (!resource.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out _))
        {
            // Deno is not available in the default Node.js base images used for publish-mode Dockerfile generation.
            // We override the build image so that install/build steps can execute with deno.
            resource.WithAnnotation(new DockerfileBaseImageAnnotation
            {
                // Use a constant major version tag to keep builds deterministic.
                BuildImage = DefaultDenoImage,
            });
        }

        // Deno does not need an install step by default: `deno run` fetches and caches dependencies under DENO_DIR
        // on first use. Only wire an installer resource when the caller explicitly opts in (e.g. to pre-cache deps
        // or materialize node_modules for Node compatibility).
        if (install)
        {
            AddInstaller(resource, install);
        }
        else
        {
            DisableExistingInstaller(resource);
        }

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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
            });

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
    /// <exception cref="InvalidOperationException">Thrown when <c>package.json</c> declares an invalid pnpm package manager version or integrity.</exception>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<TResource> WithPnpm<TResource>(this IResourceBuilder<TResource> resource, bool install = true, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        var workingDirectory = resource.Resource.WorkingDirectory;
        var hasPnpmLock = File.Exists(Path.Combine(workingDirectory, "pnpm-lock.yaml"));
        var hasPnpmWorkspace = File.Exists(Path.Combine(workingDirectory, "pnpm-workspace.yaml"));
        var pnpmPackageManager = GetPnpmPackageManager(workingDirectory);
        var initializeDockerStage = new Action<DockerfileStage>(stage =>
        {
            stage.Arg("NPM_REGISTRY", DefaultNpmRegistry);
            if (pnpmPackageManager.Integrity is { } integrity)
            {
                stage.Run($"archive=\"$(npm pack --json pnpm@{pnpmPackageManager.Version} --registry \"$NPM_REGISTRY\" | node -e 'const result = JSON.parse(require(\"fs\").readFileSync(0, \"utf8\")); process.stdout.write(result[0].filename)')\" && node -e 'const [algorithm, expected, file] = process.argv.slice(1); const actual = require(\"crypto\").createHash(algorithm).update(require(\"fs\").readFileSync(file)).digest(\"hex\"); if (actual !== expected) {{ console.error(\"Integrity check failed for \" + file); process.exit(1); }}' \"{integrity.Algorithm}\" \"{integrity.Hash}\" \"$archive\" && npm install --global --registry \"$NPM_REGISTRY\" \"./$archive\" && rm \"$archive\"");
            }
            else
            {
                stage.Run($"npm install --global --registry \"$NPM_REGISTRY\" pnpm@{pnpmPackageManager.Version}");
            }
        });

        installArgs ??= GetDefaultPnpmInstallArgs(resource, hasPnpmLock);

        var packageFilesSourcePattern = "package.json";
        if (hasPnpmLock)
        {
            packageFilesSourcePattern += " pnpm-lock.yaml";
        }

        if (hasPnpmWorkspace)
        {
            packageFilesSourcePattern += " pnpm-workspace.yaml";
        }

        resource
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("pnpm", runScriptCommand: "run", cacheMount: "/pnpm/store")
            {
                PackageFilesPatterns = { new CopyFilePattern(packageFilesSourcePattern, "./") },
                // pnpm does not strip the -- separator and passes it to the script, causing Vite to ignore subsequent arguments.
                CommandSeparator = null,
                // pnpm is not included in the Node.js Docker image by default.
                InitializeDockerBuildStage = initializeDockerStage,
                InitializeDockerRuntimeStage = initializeDockerStage,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs])
            {
                ProductionInstallArgs = "--prod"
            });

        AddInstaller(resource, install);
        return resource;
    }

    private static string[] GetDefaultPnpmInstallArgs(IResourceBuilder<JavaScriptAppResource> resource, bool hasPnpmLock) =>
        resource.ApplicationBuilder.ExecutionContext.IsPublishMode && hasPnpmLock
            ? ["--frozen-lockfile"]
            : [];

    private static (string Version, (string Algorithm, string Hash)? Integrity) GetPnpmPackageManager(string workingDirectory)
    {
        var packageJsonPath = Path.Combine(workingDirectory, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return (DefaultPnpmVersion, null);
        }

        try
        {
            using var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (packageJson.RootElement.TryGetProperty("packageManager", out var packageManagerElement) &&
                packageManagerElement.ValueKind == JsonValueKind.String &&
                packageManagerElement.GetString() is { } packageManager &&
                packageManager.StartsWith("pnpm@", StringComparison.Ordinal))
            {
                var version = packageManager.AsSpan("pnpm@".Length);
                ReadOnlySpan<char> integrity = default;
                var hashSeparator = version.IndexOf('+');
                var hasIntegrity = hashSeparator >= 0;
                if (hasIntegrity)
                {
                    integrity = version[(hashSeparator + 1)..];
                    version = version[..hashSeparator];
                }

                if (PnpmVersionRegex().IsMatch(version))
                {
                    var integritySeparator = integrity.IndexOf('.');
                    if (integritySeparator > 0 &&
                        integrity[(integritySeparator + 1)..] is { IsEmpty: false } hash &&
                        hash.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 &&
                        integrity[..integritySeparator] is "sha224" or "sha256" or "sha384" or "sha512")
                    {
                        return (version.ToString(), (integrity[..integritySeparator].ToString(), hash.ToString().ToLowerInvariant()));
                    }

                    if (!hasIntegrity)
                    {
                        return (version.ToString(), null);
                    }
                }

                // A declared pnpm specification controls the binary installed in the published image.
                // Fail closed instead of silently discarding the requested version and integrity.
                throw new InvalidOperationException(
                    $"The packageManager value '{packageManager}' in '{packageJsonPath}' is invalid. Expected 'pnpm@<version>' or 'pnpm@<version>+<sha224|sha256|sha384|sha512>.<hex hash>'.");
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return (DefaultPnpmVersion, null);
    }

    // Corepack requires packageManager values to use an exact semantic version. node-semver
    // also accepts the ecosystem's conventional leading "v"; integrity metadata is parsed
    // separately after the version's '+' delimiter.
    // See https://github.com/nodejs/corepack/blob/436b358a19f6d2592cff740078db1b06953c3578/sources/specUtils.ts
    [GeneratedRegex("""^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$""", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PnpmVersionRegex();

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
    [AspireExport]
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
    [AspireExport]
    public static IResourceBuilder<TResource> WithRunScript<TResource>(this IResourceBuilder<TResource> resource, string scriptName, string[]? args = null) where TResource : JavaScriptAppResource
    {
        return resource.WithAnnotation(new JavaScriptRunScriptAnnotation(scriptName, args));
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder, string scriptPath, string launchConfigType)
        where T : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        var resource = builder.Resource;
        var workingDirectory = Path.GetFullPath(resource.WorkingDirectory);

        return builder.WithDebugSupport(
            context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // Compute at run time so the launch config reflects the final annotation state
                var hasRunScript = resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out _);
                var hasPackageManager = resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pmAnnotation);
                var denoCommandLine = launchConfigType == "deno" &&
                    resource.TryGetLastAnnotation<DenoCommandLineAnnotation>(out var deno)
                    ? deno
                    : null;
                var isDenoTask = denoCommandLine?.Mode == DenoCommandMode.Task;
                var isExplicitDenoDirectLaunch = denoCommandLine is
                {
                    ModeSet: true,
                    Mode: DenoCommandMode.Run or DenoCommandMode.Serve
                };
                // WithRunScript annotations remain after an explicit Deno mode changes the emitted command.
                // Match BuildDenoArgs precedence so launch metadata describes the final command.
                var isPackageManagerScript = isDenoTask ||
                    (hasRunScript && hasPackageManager && !isExplicitDenoDirectLaunch);
                var effectiveLaunchConfigType = launchConfigType == "deno" && hasRunScript && hasPackageManager
                    ? GetJavaScriptPackageManagerLaunchConfigurationType(pmAnnotation!.ExecutableName)
                    : launchConfigType;

                return Task.FromResult(new JavaScriptLaunchConfiguration(effectiveLaunchConfigType)
                {
                    ScriptPath = Path.GetFullPath(scriptPath, workingDirectory),
                    Mode = context.Mode,
                    RuntimeExecutable = hasRunScript && hasPackageManager ? pmAnnotation!.ExecutableName : launchConfigType,
                    LaunchMethod = isPackageManagerScript ? JavaScriptLaunchConfiguration.LaunchMethodPackageManager : JavaScriptLaunchConfiguration.LaunchMethodDirect,
                    WorkingDirectory = workingDirectory
                });
            },
            launchConfigType);
    }

    private static string GetJavaScriptPackageManagerLaunchConfigurationType(string packageManagerExecutable) => packageManagerExecutable switch
    {
        "bun" => "bun",
        "deno" => "deno",
        _ => "node",
    };

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = builder.Resource;
        var workingDirectory = Path.GetFullPath(resource.WorkingDirectory);

        if (resource is BunAppResource)
        {
            throw new InvalidOperationException(
                $"Bun apps cannot be debugged through the Node dev-server debug path. '{resource.Name}' is a {nameof(BunAppResource)}; use {nameof(AddBunApp)}, which wires its own Bun debug support.");
        }

        if (resource is DenoAppResource)
        {
            throw new InvalidOperationException(
                $"Deno apps cannot be debugged through the Node dev-server debug path. '{resource.Name}' is a {nameof(DenoAppResource)}; use {nameof(AddDenoApp)}, which wires its own Deno debug support.");
        }

        return builder.WithDebugSupport(
            mode =>
            {
                // Fall back to "npm" (the default for these frameworks) if no package manager annotation is present.
                var packageManager = "npm";
                if (resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pmAnnotation))
                {
                    packageManager = pmAnnotation.ExecutableName;
                }

                return new JavaScriptLaunchConfiguration("node")
                {
                    ScriptPath = string.Empty,
                    Mode = mode,
                    RuntimeExecutable = packageManager,
                    LaunchMethod = JavaScriptLaunchConfiguration.LaunchMethodPackageManager,
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
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
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
            if (configuration["DEBUG_SESSION_INFO"] is { } debugSessionInfoJson
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
                // Installer already exists, update its configuration based on install parameter. Package manager
                // methods are composable (for example `.WithDeno(install: false).WithDeno(install: true)`), so the
                // final call has to be able to re-enable a previously disabled installer, not just disable it.
                if (install)
                {
                    EnableInstaller(resource, existingResource);
                }
                else
                {
                    DisableInstaller(resource, existingResource);
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

    private static void DisableExistingInstaller<TResource>(IResourceBuilder<TResource> resource) where TResource : JavaScriptAppResource
    {
        if (!resource.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return;
        }

        var installerName = $"{resource.Resource.Name}-installer";
        resource.ApplicationBuilder.TryCreateResourceBuilder<JavaScriptInstallerResource>(installerName, out var existingResource);
        if (existingResource is not null)
        {
            DisableInstaller(resource, existingResource);
        }
    }

    private static void DisableInstaller<TResource>(IResourceBuilder<TResource> resource, IResourceBuilder<JavaScriptInstallerResource> installer) where TResource : JavaScriptAppResource
    {
        resource.Resource.Annotations.OfType<WaitAnnotation>()
            .Where(w => w.Resource == installer.Resource)
            .ToList()
            .ForEach(w => resource.Resource.Annotations.Remove(w));

        installer.WithExplicitStart();
    }

    private static void EnableInstaller<TResource>(IResourceBuilder<TResource> resource, IResourceBuilder<JavaScriptInstallerResource> installer) where TResource : JavaScriptAppResource
    {
        // Undo WithExplicitStart so the installer starts automatically again.
        installer.Resource.Annotations.OfType<ExplicitStartupAnnotation>()
            .ToList()
            .ForEach(a => installer.Resource.Annotations.Remove(a));

        // WaitForCompletion adds a new WaitAnnotation each time, so only restore the relationship when the
        // previous disable removed it.
        if (!resource.Resource.Annotations.OfType<WaitAnnotation>().Any(w => w.Resource == installer.Resource))
        {
            resource.WaitForCompletion(installer);
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

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> to find the nearest <c>node_modules</c> directory.
    /// </summary>
    private static string? FindNearestNodeModules(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Join(current, "node_modules");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == current)
            {
                break;
            }
            current = parent;
        }

        return null;
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
