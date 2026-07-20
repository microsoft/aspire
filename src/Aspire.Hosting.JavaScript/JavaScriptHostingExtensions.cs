// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECOMMAND001

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.JavaScript.Internal;
using Aspire.Hosting.JavaScript.Internal.Workspace;
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
public static class JavaScriptHostingExtensions
{
    private const string BrowserCapability = "browser";
    private const string DefaultJavaScriptRunScriptName = "dev";
    // Target for `pnpm deploy` in PackageScript publish mode. Outside /app (the workspace root in the
    // build stage) so the runtime stage can COPY just this self-contained directory.
    private const string PackageScriptDeployPath = "/deploy";
    private const string DefaultYarpImage = Yarp.YarpContainerImageTags.Registry + "/" + Yarp.YarpContainerImageTags.Image + ":" + Yarp.YarpContainerImageTags.Tag;

    // Help links surfaced when a required command is missing, mapped to a command by ResolveHelpLink.
    private const string NodeHelpLink = "https://nodejs.org/en/download/";
    private const string NpmHelpLink = "https://docs.npmjs.com/downloading-and-installing-node-js-and-npm";
    private const string BunHelpLink = "https://bun.sh/docs/installation";
    private const string YarnHelpLink = "https://yarnpkg.com/getting-started/install";
    private const string PnpmHelpLink = "https://pnpm.io/installation";

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

        var resourceBuilder = builder.CreateNodeAppBuilder(resource, scriptPath);

        if (File.Exists(Path.Combine(appDirectory, "package.json")))
        {
            // Automatically add npm as the package manager if a package.json file exists
            resourceBuilder.WithNpm();
        }

        return resourceBuilder;
    }

    private static IResourceBuilder<NodeAppResource> CreateNodeAppBuilder(
        this IDistributedApplicationBuilder builder,
        NodeAppResource resource,
        string scriptPath)
    {
        var resourceBuilder = builder.AddResource(resource)
            .WithNodeDefaults()
            .WithArgs(c =>
            {
                // If the JavaScriptRunScriptAnnotation is present, use that to run the app
                if (c.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runCommand) &&
                    c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                {
                    if (c.Resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx))
                    {
                        // The workspace resource owns the entire argv (executable + workspace selector +
                        // run + script + args), because the selector placement differs per package manager
                        // (npm's --workspace= is a suffix; yarn/pnpm/bun filter before run) and pnpm uses a
                        // topological build selector. So we emit it verbatim rather than reassembling here.
                        foreach (var arg in wsCtx.Workspace.GetRunScriptCommand(wsCtx.WorkspaceProjectName, runCommand.ScriptName, runCommand.Args))
                        {
                            c.Args.Add(arg);
                        }
                    }
                    else
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
                }
                else
                {
                    c.Args.Add(scriptPath);
                }
            })
            .WithIconName("CodeJsRectangle")
            .PublishAsDockerFile(c =>
            {
                // Use workspace root as Docker context if this is a workspace app
                var nodeDockerContextDir = resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsNodeCtx)
                    ? wsNodeCtx.Workspace.WorkingDirectory
                    : resource.WorkingDirectory;

                // Only generate a Dockerfile if one doesn't already exist in the context directory
                if (File.Exists(Path.Combine(nodeDockerContextDir, "Dockerfile")))
                {
                    return;
                }

                c.WithDockerfileBuilder(nodeDockerContextDir, dockerfileContext =>
                {
                    // Fail early (and aggregate all problems) before generating an invalid Dockerfile.
                    ValidateWorkspaceConfiguration(resource, dockerfileContext.Services, isPublishMode: true);

                    // For a workspace member, prefer a root-level Node pin (.nvmrc / .tool-versions); the
                    // workspace root is the same annotation used to pick the Docker context above.
                    var nodeWorkspaceRoot = wsNodeCtx?.Workspace.WorkingDirectory;
                    var defaultBaseImage = new Lazy<string>(() => GetDefaultBaseImage(nodeDockerContextDir, "alpine", dockerfileContext.Services, nodeWorkspaceRoot));

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

                        // Run post-copy finalization (e.g. regenerate Yarn PnP resolution files)
                        packageManager.FinalizeDockerBuildStage?.Invoke(builderStage);

                        if (resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            List<string> commandArgs;
                            if (resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsBuildCtx))
                            {
                                if (wsBuildCtx.Workspace.GetBuildDependenciesCommand(wsBuildCtx.WorkspaceProjectName, buildCommand.ScriptName) is { } depBuildArgs)
                                {
                                    builderStage.EmptyLine()
                                        .Run(string.Join(' ', depBuildArgs));
                                }

                                commandArgs = [.. wsBuildCtx.Workspace.GetRunScriptCommand(wsBuildCtx.WorkspaceProjectName, buildCommand.ScriptName, buildCommand.Args)];
                            }
                            else
                            {
                                commandArgs = [packageManager.ExecutableName];

                                if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
                                {
                                    commandArgs.Add(packageManager.ScriptCommand);
                                }
                                commandArgs.Add(buildCommand.ScriptName);
                                commandArgs.AddRange(buildCommand.Args);
                            }

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
                    dockerfileContext.Builder
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
                            .Entrypoint([resource.Command, GetPublishedEntryPoint(resource, scriptPath)]);
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

        var resourceBuilder = builder.CreateBunAppBuilder(resource, scriptPath);

        if (File.Exists(Path.Combine(appDirectory, "package.json")))
        {
            // Automatically add bun as the package manager if a package.json file exists
            resourceBuilder.WithBun();
        }

        return resourceBuilder;
    }

    private static IResourceBuilder<BunAppResource> CreateBunAppBuilder(
        this IDistributedApplicationBuilder builder,
        BunAppResource resource,
        string scriptPath)
    {
        var resourceBuilder = builder.AddResource(resource)
            .WithBunDefaults()
            .WithArgs(c =>
            {
                // If the JavaScriptRunScriptAnnotation is present, use that to run the app
                if (c.Resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runCommand) &&
                    c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                {
                    if (c.Resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx))
                    {
                        // The workspace resource owns the entire argv (see GetRunScriptCommand); emit verbatim.
                        foreach (var arg in wsCtx.Workspace.GetRunScriptCommand(wsCtx.WorkspaceProjectName, runCommand.ScriptName, runCommand.Args))
                        {
                            c.Args.Add(arg);
                        }
                    }
                    else
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
                }
                else
                {
                    c.Args.Add(scriptPath);
                }
            })
            .WithIconName("CodeJsRectangle")
            .PublishAsDockerFile(c =>
            {
                // Use workspace root as Docker context if this is a workspace app
                var bunDockerContextDir = resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsBunCtx)
                    ? wsBunCtx.Workspace.WorkingDirectory
                    : resource.WorkingDirectory;

                // Only generate a Dockerfile if one doesn't already exist in the context directory
                if (File.Exists(Path.Combine(bunDockerContextDir, "Dockerfile")))
                {
                    return;
                }

                c.WithDockerfileBuilder(bunDockerContextDir, dockerfileContext =>
                {
                    // Fail early (and aggregate all problems) before generating an invalid Dockerfile.
                    ValidateWorkspaceConfiguration(resource, dockerfileContext.Services, isPublishMode: true);

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
                            builderStage.Copy(".", ".");
                        }

                        // Run post-copy finalization (e.g. regenerate Yarn PnP resolution files when a
                        // Bun app is a member of a Yarn workspace). No-op for the default Bun package manager.
                        packageManager.FinalizeDockerBuildStage?.Invoke(builderStage);

                        if (resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            List<string> commandArgs;
                            if (resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsBuildCtx))
                            {
                                if (wsBuildCtx.Workspace.GetBuildDependenciesCommand(wsBuildCtx.WorkspaceProjectName, buildCommand.ScriptName) is { } depBuildArgs)
                                {
                                    builderStage.EmptyLine()
                                        .Run(string.Join(' ', depBuildArgs));
                                }

                                commandArgs = [.. wsBuildCtx.Workspace.GetRunScriptCommand(wsBuildCtx.WorkspaceProjectName, buildCommand.ScriptName, buildCommand.Args)];
                            }
                            else
                            {
                                commandArgs = [packageManager.ExecutableName];
                                if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
                                {
                                    commandArgs.Add(packageManager.ScriptCommand);
                                }
                                commandArgs.Add(buildCommand.ScriptName);
                                commandArgs.AddRange(buildCommand.Args);
                            }

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

                        var prodInstallCmd = $"{pm.ExecutableName} {string.Join(' ', install.Args)} {install.ProductionInstallArgs}";
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
                        // Resolve the entry point inside the (possibly workspace-scoped) package directory so a
                        // workspace member runs "<packagePath>/<scriptPath>"; a non-workspace app is unchanged.
                        .Entrypoint([resource.Command, GetPublishedEntryPoint(resource, scriptPath)]);
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

        // SourcePath must equal the build-output path the runtime stage copies FROM, because a
        // consuming resource (ContainerFilesDestination) copies the built assets from exactly this
        // path. For a workspace member the output lives under the member's package directory, so
        // use the workspace-aware GetBuildOutputPath (which falls through to the non-workspace
        // "/app/<output>" shape when the resource is not a workspace member). The default-JS-app
        // Dockerfile callback re-resolves this lazily so it stays correct even when PublishAs* ran
        // before the workspace membership was configured.
        builder.WithAnnotation(annotation)
               .ClearContainerFilesSources()
               .WithContainerFilesSource(GetBuildOutputPath(builder.Resource, options.OutputPath))
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

        // SourcePath must equal the build-output path the runtime stage copies FROM (see
        // PublishAsStaticWebsiteCore). GetBuildOutputPath scopes it under the member package
        // directory for workspace members and falls through to "/app/<output>" otherwise.
        builder.WithAnnotation(annotation)
               .ClearContainerFilesSources()
               .WithContainerFilesSource(GetBuildOutputPath(builder.Resource, outputPath))
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
    /// Configures the JavaScript application to publish as a Node.js server that uses a <c>package.json</c> script at runtime.
    /// </summary>
    /// <typeparam name="TResource">The JavaScript resource type.</typeparam>
    /// <param name="builder">The JavaScript resource builder.</param>
    /// <param name="scriptName">
    /// The name of the <c>package.json</c> script to run in the published container.
    /// For example, <c>start</c> invokes the configured package manager's run command for the <c>start</c> script,
    /// such as <c>npm run start</c>, <c>pnpm run start</c>, <c>yarn run start</c>, or <c>bun run start</c>.
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

    private static string GetPackageScriptRuntimeImage(
        string appDirectory,
        IServiceProvider services,
        DockerfileBaseImageAnnotation? baseImageAnnotation,
        JavaScriptPackageManagerAnnotation packageManager,
        string buildImage,
        string? workspaceRoot = null)
    {
        if (!string.IsNullOrEmpty(baseImageAnnotation?.RuntimeImage))
        {
            return baseImageAnnotation.RuntimeImage;
        }

        return packageManager.ResolvePackageScriptRuntimeImage?.Invoke(buildImage)
            ?? GetDefaultBaseImage(appDirectory, "alpine", services, workspaceRoot);
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
                    if (c.Resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx))
                    {
                        // The workspace resource owns the entire argv (see GetRunScriptCommand); emit verbatim.
                        foreach (var arg in wsCtx.Workspace.GetRunScriptCommand(wsCtx.WorkspaceProjectName, runCommand.ScriptName, runCommand.Args))
                        {
                            c.Args.Add(arg);
                        }
                    }
                    else
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
                }

                argsCallback?.Invoke(c);
            })
            .WithIconName("CodeJsRectangle")
            .PublishAsDockerFile(c =>
            {
                // Use workspace root as Docker context if this is a workspace app
                var dockerContextDir = resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsDockerCtx)
                    ? wsDockerCtx.Workspace.WorkingDirectory
                    : appDirectory;

                // Only generate a Dockerfile if one doesn't already exist in the context directory
                if (File.Exists(Path.Combine(dockerContextDir, "Dockerfile")))
                {
                    return;
                }

                c.WithDockerfileBuilder(dockerContextDir, dockerfileContext =>
                {
                    // Fail early (and aggregate all problems) before generating an invalid Dockerfile.
                    ValidateWorkspaceConfiguration(resource, dockerfileContext.Services, isPublishMode: true);

                    dockerfileContext.Resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode);

                    // Re-resolve the container-files source path here, at generation time, so it
                    // equals the build-output path the runtime stage copies FROM below (via
                    // GetBuildOutputPath). Resolving lazily makes it order-independent: the path is
                    // member-scoped for a workspace member regardless of whether PublishAs* ran
                    // before or after the workspace membership (JavaScriptWorkspaceAppPathAnnotation)
                    // was configured.
                    ResolveWorkspaceAwareContainerFilesSource(dockerfileContext.Resource, publishMode?.OutputPath ?? "dist");

                    // For a workspace member, prefer a root-level Node pin (.nvmrc / .tool-versions); the
                    // workspace root is the same annotation used to pick the Docker context above.
                    var workspaceRoot = wsDockerCtx?.Workspace.WorkingDirectory;

                    if (c.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
                    {
                        // Get custom base image from annotation, if present
                        dockerfileContext.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
                        var baseImage = baseImageAnnotation?.BuildImage ?? GetDefaultBaseImage(appDirectory, "slim", dockerfileContext.Services, workspaceRoot);

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

                        // Run post-copy finalization (e.g. regenerate Yarn PnP resolution files)
                        packageManager.FinalizeDockerBuildStage?.Invoke(dockerBuilder);

                        if (c.Resource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildCommand))
                        {
                            List<string> commandArgs;
                            if (c.Resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsBuildCtx))
                            {
                                if (wsBuildCtx.Workspace.GetBuildDependenciesCommand(wsBuildCtx.WorkspaceProjectName, buildCommand.ScriptName) is { } depBuildArgs)
                                {
                                    dockerBuilder.Run(string.Join(' ', depBuildArgs));
                                }

                                commandArgs = [.. wsBuildCtx.Workspace.GetRunScriptCommand(wsBuildCtx.WorkspaceProjectName, buildCommand.ScriptName, buildCommand.Args)];
                            }
                            else
                            {
                                commandArgs = [packageManager.ExecutableName];

                                if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
                                {
                                    commandArgs.Add(packageManager.ScriptCommand);
                                }
                                commandArgs.Add(buildCommand.ScriptName);
                                commandArgs.AddRange(buildCommand.Args);
                            }

                            dockerBuilder.Run(string.Join(' ', commandArgs));
                        }

                        switch (publishMode?.Mode)
                        {
                            case JavaScriptPublishMode.StaticWebsite:
                                {
                                    var runtimeImage = baseImageAnnotation?.RuntimeImage ?? DefaultYarpImage;
                                    var distPath = GetBuildOutputPath(c.Resource, publishMode.OutputPath);
                                    dockerfileContext.Builder
                                        .From(runtimeImage, "runtime")
                                        .WorkDir("/app")
                                        .CopyFrom("build", distPath, "/app/wwwroot")
                                        .Entrypoint(["dotnet", "/app/yarp.dll"]);
                                    break;
                                }
                            case JavaScriptPublishMode.NodeServer:
                                {
                                    var runtimeImage = baseImageAnnotation?.RuntimeImage ?? GetDefaultBaseImage(appDirectory, "alpine", dockerfileContext.Services, workspaceRoot);
                                    var outputPath = GetBuildOutputPath(c.Resource, publishMode.OutputPath);

                                    dockerfileContext.Builder
                                        .From(runtimeImage, "runtime")
                                        .WorkDir("/app")
                                        .CopyFrom("build", outputPath, outputPath)
                                        .Env("NODE_ENV", "production")
                                        .User("node")
                                        .Entrypoint(["node", GetPublishedEntryPoint(c.Resource, publishMode.EntryPoint!)]);
                                    break;
                                }
                            case JavaScriptPublishMode.PackageScript:
                                {
                                    var runtimeImage = GetPackageScriptRuntimeImage(appDirectory, dockerfileContext.Services, baseImageAnnotation, packageManager, baseImage, workspaceRoot);

                                    // A workspace member on a package manager with a self-contained deploy (pnpm)
                                    // produces a pruned single-package directory instead of overlaying the whole
                                    // workspace plus a prod-deps node_modules. The member and its workspace deps were
                                    // already built above; deploy copies files (it does not build). The runtime then
                                    // copies just that directory, so the entrypoint runs unfiltered from the package root.
                                    if (c.Resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsDeployCtx) &&
                                        wsDeployCtx.Workspace.GetDeployCommand(wsDeployCtx.WorkspaceProjectName, PackageScriptDeployPath) is { } deployArgs)
                                    {
                                        dockerBuilder.Run(string.Join(' ', deployArgs));

                                        var deployRunCommand = string.IsNullOrWhiteSpace(publishMode.RunScriptArguments)
                                            ? $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.ScriptName}"
                                            : $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.ScriptName} {publishMode.RunScriptArguments}";

                                        var deployRuntimeStage = dockerfileContext.Builder
                                            .From(runtimeImage, "runtime")
                                            .WorkDir("/app")
                                            .CopyFrom("build", PackageScriptDeployPath, "/app");

                                        packageManager.InitializeDockerRuntimeStage?.Invoke(deployRuntimeStage);

                                        deployRuntimeStage
                                            .Env("NODE_ENV", "production")
                                            .Entrypoint(["sh", "-c", $"exec {deployRunCommand}"]);
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

                                    var prodInstallCmd = $"{packageManager.ExecutableName} {string.Join(' ', installAnnotation.Args)} {installAnnotation.ProductionInstallArgs}";
                                    if (!string.IsNullOrEmpty(packageManager.CacheMount))
                                    {
                                        prodDepsStage.Run($"--mount=type=cache,target={packageManager.CacheMount} {prodInstallCmd}");
                                    }
                                    else
                                    {
                                        prodDepsStage.Run(prodInstallCmd);
                                    }

                                    // Runtime stage: copy build output then overlay prod deps. For a workspace
                                    // member the runtime copies the whole workspace and WORKDIRs at the root, so
                                    // the entrypoint must be workspace-scoped (e.g. "pnpm --filter <member> run start")
                                    // to find the member's script instead of looking for it in the root package.json.
                                    // ScriptName is non-null in PackageScript mode (PublishAsPackageScript validates it).
                                    var baseRunCommand = c.Resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsRunCtx)
                                        ? string.Join(' ', wsRunCtx.Workspace.GetRunScriptCommand(wsRunCtx.WorkspaceProjectName, publishMode.ScriptName!, []))
                                        : $"{packageManager.ExecutableName} {packageManager.ScriptCommand ?? "run"} {publishMode.ScriptName}";

                                    var runCommand = string.IsNullOrWhiteSpace(publishMode.RunScriptArguments)
                                        ? baseRunCommand
                                        : $"{baseRunCommand} {publishMode.RunScriptArguments}";

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
                                    var runtimeImage = baseImageAnnotation?.RuntimeImage ?? GetDefaultBaseImage(appDirectory, "alpine", dockerfileContext.Services, workspaceRoot);

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
            // Default build-only container-files source. The Dockerfile callback re-resolves this
            // lazily (see ResolveWorkspaceAwareContainerFilesSource) so it stays member-scoped for
            // workspace members; GetBuildOutputPath falls through to "/app/dist" for non-members.
            .WithAnnotation(new ContainerFilesSourceAnnotation() { SourcePath = GetBuildOutputPath(resource, "dist") })
            .WithBuildScript("build")
            .WithRunScript(runScriptName);

        // Only default to npm if the app is not part of a workspace (workspace apps inherit their PM)
        if (!resource.Annotations.OfType<JavaScriptWorkspaceContextAnnotation>().Any())
        {
            resourceBuilder.WithNpm();
        }

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

        return builder.CreateViteAppBuilder(resource, appHostId, appDirectory, runScriptName);
    }

    private static IResourceBuilder<ViteAppResource> CreateViteAppBuilder(
        this IDistributedApplicationBuilder builder,
        ViteAppResource resource,
        string appHostId,
        string appDirectory,
        string runScriptName)
    {
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

        return builder.CreateNextJsAppBuilder(resource, appDirectory, runScriptName);
    }

    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    private static IResourceBuilder<NextJsAppResource> CreateNextJsAppBuilder(
            this IDistributedApplicationBuilder builder,
            NextJsAppResource resource,
            string appDirectory,
            string runScriptName)
    {
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
                Name = $"nextjs-standalone-check-{resource.Name}",
                Description = $"Validates that the Next.js app '{resource.Name}' has output: \"standalone\" configured.",
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

    private static string[] GetDefaultYarnInstallArgs<TResource>(
        IResourceBuilder<TResource> resource,
        bool hasYarnLock,
        bool hasYarnBerry)
        where TResource : IResource
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<TResource> WithPnpm<TResource>(this IResourceBuilder<TResource> resource, bool install = true, string[]? installArgs = null) where TResource : JavaScriptAppResource
    {
        ArgumentNullException.ThrowIfNull(resource);

        var workingDirectory = resource.Resource.WorkingDirectory;
        var hasPnpmLock = File.Exists(Path.Combine(workingDirectory, "pnpm-lock.yaml"));
        var hasPnpmWorkspace = File.Exists(Path.Combine(workingDirectory, "pnpm-workspace.yaml"));

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
                // pnpm is not included in the Node.js Docker image by default, so we need to enable it via corepack
                InitializeDockerBuildStage = stage => stage.Run("corepack enable pnpm"),
                InitializeDockerRuntimeStage = stage =>
                {
                    // Corepack's shim is not enough by itself: without invoking pnpm during the image build,
                    // the first container start can try to download pnpm before running the app.
                    stage.Run("corepack enable pnpm && pnpm --version");
                },
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs])
            {
                ProductionInstallArgs = "--prod"
            });

        AddInstaller(resource, install);
        return resource;
    }

    private static string[] GetDefaultPnpmInstallArgs<TResource>(IResourceBuilder<TResource> resource, bool hasPnpmLock)
        where TResource : IResource =>
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
            mode =>
            {
                // Compute at run time so the launch config reflects the final annotation state
                var hasRunScript = resource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out _);
                var hasPackageManager = resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pmAnnotation);
                var isPackageManagerScript = hasRunScript && hasPackageManager;

                return new JavaScriptLaunchConfiguration(launchConfigType)
                {
                    ScriptPath = Path.GetFullPath(scriptPath, workingDirectory),
                    Mode = mode,
                    RuntimeExecutable = isPackageManagerScript ? pmAnnotation!.ExecutableName : launchConfigType,
                    LaunchMethod = isPackageManagerScript ? JavaScriptLaunchConfiguration.LaunchMethodPackageManager : JavaScriptLaunchConfiguration.LaunchMethodDirect,
                    WorkingDirectory = workingDirectory
                };
            },
            launchConfigType);
    }

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
        // Workspace apps use the workspace-level installer, not a per-app one
        if (resource.Resource.Annotations.OfType<JavaScriptWorkspaceContextAnnotation>().Any())
        {
            return;
        }

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

    /// <summary>
    /// Adds a Yarn workspace to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory of the workspace.</param>
    /// <returns>A resource builder for the Yarn workspace.</returns>
    [AspireExport]
    public static IResourceBuilder<YarnWorkspaceResource> AddYarnWorkspace(this IDistributedApplicationBuilder builder, [ResourceName] string name, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        var resource = new YarnWorkspaceResource(name, workingDirectory);

        return builder.AddResource(resource)
            .WithYarnWorkspaceDefaults();
    }

    /// <summary>
    /// Adds a pnpm workspace to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory of the workspace.</param>
    /// <returns>A resource builder for the pnpm workspace.</returns>
    [AspireExport]
    public static IResourceBuilder<PnpmWorkspaceResource> AddPnpmWorkspace(this IDistributedApplicationBuilder builder, [ResourceName] string name, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        var resource = new PnpmWorkspaceResource(name, workingDirectory);

        return builder.AddResource(resource)
            .WithPnpmWorkspaceDefaults();
    }

    /// <summary>
    /// Adds an npm workspace to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory of the workspace.</param>
    /// <returns>A resource builder for the npm workspace.</returns>
    [AspireExport]
    public static IResourceBuilder<NpmWorkspaceResource> AddNpmWorkspace(this IDistributedApplicationBuilder builder, [ResourceName] string name, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        var resource = new NpmWorkspaceResource(name, workingDirectory);

        return builder.AddResource(resource)
            .WithNpmWorkspaceDefaults();
    }

    /// <summary>
    /// Adds a Bun workspace to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory of the workspace.</param>
    /// <returns>A resource builder for the Bun workspace.</returns>
    [AspireExport]
    public static IResourceBuilder<BunWorkspaceResource> AddBunWorkspace(this IDistributedApplicationBuilder builder, [ResourceName] string name, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        var resource = new BunWorkspaceResource(name, workingDirectory);

        return builder.AddResource(resource)
            .WithBunWorkspaceDefaults();
    }

    private static IResourceBuilder<NpmWorkspaceResource> WithNpmWorkspaceDefaults(this IResourceBuilder<NpmWorkspaceResource> builder)
    {
        var workingDirectory = builder.Resource.WorkingDirectory;

        // npm uses `npm ci` for reproducible installs when a package-lock.json is present in publish mode;
        // otherwise it falls back to `npm install` (matching the per-app WithNpm default).
        var installCommand = builder.ApplicationBuilder.ExecutionContext.IsPublishMode &&
            File.Exists(Path.Combine(workingDirectory, "package-lock.json"))
                ? "ci"
                : "install";

        var packageManager = new JavaScriptPackageManagerAnnotation("npm", runScriptCommand: "run", cacheMount: "/root/.npm")
        {
            PackageFilesPatterns = { new CopyFilePattern("package*.json", "./") },
        };

        // Add package.json files from workspace packages so the lockfile matches during install
        ApplyWorkspaceManifestLayer(builder.Resource, packageManager);

        builder
            .WithAnnotation(packageManager)
            // ProductionInstallArgs mirrors the per-app WithNpm default so a workspace member can be
            // published with PublishAsPackageScript (the prod-deps stage requires it). See WithNpm.
            .WithAnnotation(new JavaScriptInstallCommandAnnotation([installCommand]) { ProductionInstallArgs = "--omit=dev" })
            .WithRequiredCommand("npm", "https://docs.npmjs.com/downloading-and-installing-node-js-and-npm");

        AddWorkspaceInstaller(builder);
        return builder;
    }

    private static IResourceBuilder<BunWorkspaceResource> WithBunWorkspaceDefaults(this IResourceBuilder<BunWorkspaceResource> builder)
    {
        var workingDirectory = builder.Resource.WorkingDirectory;
        var hasBunLock = File.Exists(Path.Combine(workingDirectory, "bun.lock")) ||
            File.Exists(Path.Combine(workingDirectory, "bun.lockb"));

        // Use --frozen-lockfile in publish mode when a lockfile is present so the install is reproducible.
        string[] installArgs = builder.ApplicationBuilder.ExecutionContext.IsPublishMode && hasBunLock
            ? ["--frozen-lockfile"]
            : [];

        var packageFilesSourcePattern = "package.json";
        if (File.Exists(Path.Combine(workingDirectory, "bun.lock")))
        {
            packageFilesSourcePattern += " bun.lock";
        }
        if (File.Exists(Path.Combine(workingDirectory, "bun.lockb")))
        {
            packageFilesSourcePattern += " bun.lockb";
        }

        var packageManager = new JavaScriptPackageManagerAnnotation("bun", runScriptCommand: "run", cacheMount: "/root/.bun/install/cache")
        {
            PackageFilesPatterns = { new CopyFilePattern(packageFilesSourcePattern, "./") },
            // bun supports passing script flags without the `--` separator.
            CommandSeparator = null,
            ResolvePackageScriptRuntimeImage = buildImage => buildImage,
        };

        // Add package.json files from workspace packages so the lockfile matches during install
        ApplyWorkspaceManifestLayer(builder.Resource, packageManager);

        builder
            .WithAnnotation(packageManager)
            // ProductionInstallArgs mirrors the per-app WithBun default so a workspace member can be
            // published with PublishAsPackageScript (the prod-deps stage requires it). See WithBun.
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs]) { ProductionInstallArgs = "--production" })
            .WithRequiredCommand("bun", "https://bun.sh/docs/installation");

        if (!builder.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out _))
        {
            // bun is not available in the default Node.js base images used for publish-mode Dockerfile generation.
            // We override the build image so that the install and build steps can execute with bun.
            builder.WithAnnotation(new DockerfileBaseImageAnnotation
            {
                // Use a constant major version tag to keep builds deterministic.
                BuildImage = "oven/bun:1",
            });
        }

        AddWorkspaceInstaller(builder);
        return builder;
    }

    private static IResourceBuilder<YarnWorkspaceResource> WithYarnWorkspaceDefaults(this IResourceBuilder<YarnWorkspaceResource> builder)
    {
        var workingDirectory = builder.Resource.WorkingDirectory;
        var hasYarnLock = File.Exists(Path.Combine(workingDirectory, "yarn.lock"));
        var hasYarnrc = File.Exists(Path.Combine(workingDirectory, ".yarnrc.yml"));
        var hasYarnBerryDir = Directory.Exists(Path.Combine(workingDirectory, ".yarn"));
        var hasYarnBerry = hasYarnrc || hasYarnBerryDir;

        var installArgs = GetDefaultYarnInstallArgs(builder, hasYarnLock, hasYarnBerry);

        var hasPnP = File.Exists(Path.Combine(workingDirectory, ".pnp.cjs"));
        var cacheMount = hasYarnBerry ? ".yarn/cache" : "/root/.cache/yarn";
        var packageManager = new JavaScriptPackageManagerAnnotation("yarn", runScriptCommand: "run", cacheMount)
        {
            CommandSeparator = null,
            InitializeDockerBuildStage = stage => stage.Run("corepack enable yarn"),
            // Yarn PnP generates .pnp.cjs with absolute paths during install. The subsequent
            // COPY . . overwrites it with the host version (which has wrong paths for Docker).
            // Re-running install after source copy regenerates .pnp.cjs with correct container paths.
            FinalizeDockerBuildStage = hasPnP ? stage => stage.Run("yarn install") : null,
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

        // Add package.json files from workspace packages so the lockfile matches during install
        ApplyWorkspaceManifestLayer(builder.Resource, packageManager);

        builder
            .WithAnnotation(packageManager)
            // ProductionInstallArgs mirrors the per-app WithYarn default so a workspace member can be
            // published with PublishAsPackageScript (the prod-deps stage requires it). See WithYarn.
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs]) { ProductionInstallArgs = "--production" })
            .WithRequiredCommand("yarn", "https://yarnpkg.com/getting-started/install");

        AddWorkspaceInstaller(builder);
        return builder;
    }

    private static IResourceBuilder<PnpmWorkspaceResource> WithPnpmWorkspaceDefaults(this IResourceBuilder<PnpmWorkspaceResource> builder)
    {
        var workingDirectory = builder.Resource.WorkingDirectory;
        var hasPnpmLock = File.Exists(Path.Combine(workingDirectory, "pnpm-lock.yaml"));
        var hasPnpmWorkspaceYaml = File.Exists(Path.Combine(workingDirectory, "pnpm-workspace.yaml"));

        var installArgs = GetDefaultPnpmInstallArgs(builder, hasPnpmLock);

        var packageFilesSourcePattern = "package.json";
        if (hasPnpmLock)
        {
            packageFilesSourcePattern += " pnpm-lock.yaml";
        }
        if (hasPnpmWorkspaceYaml)
        {
            packageFilesSourcePattern += " pnpm-workspace.yaml";
        }

        var packageManager = new JavaScriptPackageManagerAnnotation("pnpm", runScriptCommand: "run", cacheMount: "/pnpm/store")
        {
            PackageFilesPatterns = { new CopyFilePattern(packageFilesSourcePattern, "./") },
            CommandSeparator = null,
            InitializeDockerBuildStage = stage => stage.Run("corepack enable pnpm"),
            // PublishAsPackageScript runtime images (node:*-alpine) have no pnpm, but the entrypoint runs
            // `pnpm run <script>`. Enable pnpm during the image build so the first container start does not
            // try to download it. Mirrors the per-app WithPnpm default.
            InitializeDockerRuntimeStage = stage => stage.Run("corepack enable pnpm && pnpm --version"),
        };

        // Add package.json files from workspace packages so the lockfile matches during install
        ApplyWorkspaceManifestLayer(builder.Resource, packageManager);

        builder
            .WithAnnotation(packageManager)
            // ProductionInstallArgs mirrors the per-app WithPnpm default so a workspace member can be
            // published with PublishAsPackageScript (the prod-deps stage requires it). See WithPnpm.
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(["install", .. installArgs]) { ProductionInstallArgs = "--prod" })
            .WithRequiredCommand("pnpm", "https://pnpm.io/installation");

        AddWorkspaceInstaller(builder);
        return builder;
    }

    private static void AddWorkspaceInstaller<TResource>(IResourceBuilder<TResource> workspace) where TResource : JavaScriptWorkspaceResource
    {
        if (workspace.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var installerName = $"{workspace.Resource.Name}-installer";
            var installer = new JavaScriptInstallerResource(installerName, workspace.Resource.WorkingDirectory);
            var installerBuilder = workspace.ApplicationBuilder.AddResource(installer)
                .WithParentRelationship(workspace.Resource)
                .ExcludeFromManifest()
                .WithCertificateTrustScope(CertificateTrustScope.None);

            workspace.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((evt, _) =>
            {
                // Validate the workspace configuration once at install start. Multiple member apps share
                // this single installer, so the validator's per-workspace idempotency guard keeps it from
                // re-running. Run mode never reaches the publish Dockerfile callbacks, so this is the only
                // place the aggregated diagnostics surface for `aspire run`.
                var memberApps = evt.Model.Resources
                    .OfType<JavaScriptAppResource>()
                    .Where(r => r.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var ctx) && ReferenceEquals(ctx.Workspace, workspace.Resource))
                    .ToArray();
                WorkspaceConfigurationValidator.Validate(workspace.Resource, memberApps, isPublishMode: false);

                if (!workspace.Resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager) ||
                    !workspace.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand))
                {
                    throw new InvalidOperationException("JavaScriptPackageManagerAnnotation and JavaScriptInstallCommandAnnotation are required when installing packages.");
                }

                installerBuilder
                    .WithCommand(packageManager.ExecutableName)
                    .WithWorkingDirectory(workspace.Resource.WorkingDirectory)
                    .WithArgs(installCommand.Args);

                return Task.CompletedTask;
            });

            workspace.WithAnnotation(new JavaScriptPackageInstallerAnnotation(installer));
        }
    }

    /// <summary>
    /// Adds a JavaScript application to a workspace. Reuses the same logic as
    /// <see cref="AddJavaScriptApp(IDistributedApplicationBuilder, string, string, string)"/>
    /// but with workspace-aware installer, run commands, and Docker build context.
    /// </summary>
    /// <typeparam name="TWorkspace">The workspace resource type.</typeparam>
    /// <param name="builder">The workspace resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workspaceProjectName">The name of the project in the workspace.</param>
    /// <param name="packagePath">The path to the package directory relative to the workspace root (e.g. "packages/web"). Build output and entry-point paths resolve inside this member directory. When <see langword="null"/>, it is derived from the workspace member whose package.json name matches <paramref name="workspaceProjectName"/>.</param>
    /// <param name="runScriptName">The name of the script to run. Defaults to "dev".</param>
    /// <returns>A resource builder for the JavaScript application.</returns>
    [AspireExport("addJavaScriptAppToWorkspace")]
    public static IResourceBuilder<JavaScriptAppResource> AddJavaScriptApp<TWorkspace>(
        this IResourceBuilder<TWorkspace> builder,
        [ResourceName] string name,
        string workspaceProjectName,
        string? packagePath = null,
        string runScriptName = "dev")
        where TWorkspace : JavaScriptWorkspaceResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workspaceProjectName);
        ArgumentException.ThrowIfNullOrEmpty(runScriptName);

        var workspace = builder.Resource;
        var resolvedPackagePath = ResolveWorkspacePackagePath(workspace, workspaceProjectName, packagePath);
        var appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(workspace.WorkingDirectory, resolvedPackagePath));
        var resource = new JavaScriptAppResource(name, workspace.Name, workspace.WorkingDirectory);

        ConfigureWorkspaceContext(resource, workspace, workspaceProjectName);

        // The JavaScriptWorkspaceAppPathAnnotation must be added here, eagerly, because the
        // PublishAsDockerFile callback that reads it (via GetBuildOutputPath) runs later and
        // deferred; without the annotation in place the member's build-output/entry-point paths
        // would resolve at the workspace root instead of inside this package directory.
        ConfigureWorkspaceAppPath(resource, appDirectory, resolvedPackagePath);

        var resourceBuilder = builder.ApplicationBuilder
            .CreateDefaultJavaScriptAppBuilder(resource, appDirectory, runScriptName)
            .WithParentRelationship(workspace);

        WireUpWorkspaceInstaller(builder, resourceBuilder);

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Vite application to a workspace. Reuses the same logic as
    /// <see cref="AddViteApp(IDistributedApplicationBuilder, string, string, string)"/>
    /// but with workspace-aware installer, run commands, and Docker build context.
    /// </summary>
    /// <typeparam name="TWorkspace">The workspace resource type.</typeparam>
    /// <param name="builder">The workspace resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workspaceProjectName">The name of the project in the workspace.</param>
    /// <param name="packagePath">The path to the package directory relative to the workspace root (e.g. "packages/web"). Used for Vite config resolution. When <see langword="null"/>, it is derived from the workspace member whose package.json name matches <paramref name="workspaceProjectName"/>.</param>
    /// <param name="runScriptName">The name of the script to run. Defaults to "dev".</param>
    /// <returns>A resource builder for the Vite application.</returns>
    [AspireExport("addViteAppToWorkspace")]
    public static IResourceBuilder<ViteAppResource> AddViteApp<TWorkspace>(
        this IResourceBuilder<TWorkspace> builder,
        [ResourceName] string name,
        string workspaceProjectName,
        string? packagePath = null,
        string runScriptName = "dev")
        where TWorkspace : JavaScriptWorkspaceResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workspaceProjectName);

        var workspace = builder.Resource;
        var resolvedPackagePath = ResolveWorkspacePackagePath(workspace, workspaceProjectName, packagePath);
        var appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(workspace.WorkingDirectory, resolvedPackagePath));
        var resource = new ViteAppResource(name, workspace.Name, workspace.WorkingDirectory);

        ConfigureWorkspaceContext(resource, workspace, workspaceProjectName);
        ConfigureWorkspaceAppPath(resource, appDirectory, resolvedPackagePath);

        var appHostId = builder.ApplicationBuilder.Configuration["AppHost:Sha256"]![..10].ToLowerInvariant();

        var resourceBuilder = builder.ApplicationBuilder
            .CreateViteAppBuilder(resource, appHostId, appDirectory, runScriptName)
            .WithParentRelationship(workspace);

        WireUpWorkspaceInstaller(builder, resourceBuilder);

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Next.js application to a workspace. Reuses the same logic as
    /// <see cref="AddNextJsApp(IDistributedApplicationBuilder, string, string, string)"/>
    /// but with workspace-aware installer, run commands, and Docker build context.
    /// </summary>
    /// <typeparam name="TWorkspace">The workspace resource type.</typeparam>
    /// <param name="builder">The workspace resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workspaceProjectName">The name of the project in the workspace.</param>
    /// <param name="packagePath">The path to the package directory relative to the workspace root (e.g. "packages/web"). Used for Next.js config resolution. When <see langword="null"/>, it is derived from the workspace member whose package.json name matches <paramref name="workspaceProjectName"/>.</param>
    /// <param name="runScriptName">The name of the script to run. Defaults to "dev".</param>
    /// <returns>A resource builder for the Next.js application.</returns>
    [AspireExport("addNextJsAppToWorkspace")]
    [Experimental("ASPIREJAVASCRIPT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<NextJsAppResource> AddNextJsApp<TWorkspace>(
        this IResourceBuilder<TWorkspace> builder,
        [ResourceName] string name,
        string workspaceProjectName,
        string? packagePath = null,
        string runScriptName = "dev")
        where TWorkspace : JavaScriptWorkspaceResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workspaceProjectName);

        var workspace = builder.Resource;
        var resolvedPackagePath = ResolveWorkspacePackagePath(workspace, workspaceProjectName, packagePath);
        var appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(workspace.WorkingDirectory, resolvedPackagePath));
        var resource = new NextJsAppResource(name, workspace.Name, workspace.WorkingDirectory);

        ConfigureWorkspaceContext(resource, workspace, workspaceProjectName);
        ConfigureWorkspaceAppPath(resource, appDirectory, resolvedPackagePath);

        var resourceBuilder = builder.ApplicationBuilder
            .CreateNextJsAppBuilder(resource, appDirectory, runScriptName)
            .WithParentRelationship(workspace);

        WireUpWorkspaceInstaller(builder, resourceBuilder);

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Node.js application to a workspace. In development mode, the app runs via the workspace
    /// package manager. In publish mode, the build uses the workspace package manager but the runtime
    /// entry point is <c>node scriptPath</c>.
    /// </summary>
    /// <typeparam name="TWorkspace">The workspace resource type.</typeparam>
    /// <param name="builder">The workspace resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workspaceProjectName">The name of the project in the workspace.</param>
    /// <param name="scriptPath">The path to the script relative to the app directory to run in production.</param>
    /// <param name="packagePath">The path to the app package relative to the workspace root. When <see langword="null"/>, it is derived from the workspace member whose package.json name matches <paramref name="workspaceProjectName"/>.</param>
    /// <param name="runScriptName">The name of the script to run in development. Defaults to "dev".</param>
    /// <returns>A resource builder for the Node.js application.</returns>
    [AspireExport("addNodeAppToWorkspace")]
    public static IResourceBuilder<NodeAppResource> AddNodeApp<TWorkspace>(
        this IResourceBuilder<TWorkspace> builder,
        [ResourceName] string name,
        string workspaceProjectName,
        string scriptPath,
        string? packagePath = null,
        string runScriptName = "dev")
        where TWorkspace : JavaScriptWorkspaceResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workspaceProjectName);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        var workspace = builder.Resource;
        var resolvedPackagePath = ResolveWorkspacePackagePath(workspace, workspaceProjectName, packagePath);
        var appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(workspace.WorkingDirectory, resolvedPackagePath));
        var resource = new NodeAppResource(name, "node", workspace.WorkingDirectory);

        // Add workspace context annotation BEFORE any setup so AddInstaller skips per-app installer
        ConfigureWorkspaceContext(resource, workspace, workspaceProjectName);
        ConfigureWorkspaceAppPath(resource, appDirectory, resolvedPackagePath);

        var resourceBuilder = builder.ApplicationBuilder
            .CreateNodeAppBuilder(resource, scriptPath)
            .WithParentRelationship(workspace)
            .WithRunScript(runScriptName);

        WireUpWorkspaceInstaller(builder, resourceBuilder);

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Bun application to a workspace. In development mode, the app runs via the workspace
    /// package manager. In publish mode, the build uses the workspace package manager but the runtime
    /// entry point is <c>bun scriptPath</c> resolved inside the member's package directory.
    /// </summary>
    /// <typeparam name="TWorkspace">The workspace resource type.</typeparam>
    /// <param name="builder">The workspace resource builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workspaceProjectName">The name of the project in the workspace.</param>
    /// <param name="scriptPath">The path to the script (for example <c>server.ts</c>) relative to the app directory to run in production.</param>
    /// <param name="packagePath">The path to the app package relative to the workspace root. When <see langword="null"/>, it is derived from the workspace member whose package.json name matches <paramref name="workspaceProjectName"/>.</param>
    /// <param name="runScriptName">The name of the script to run in development. Defaults to "dev".</param>
    /// <returns>A resource builder for the Bun application.</returns>
    [AspireExport("addBunAppToWorkspace")]
    public static IResourceBuilder<BunAppResource> AddBunApp<TWorkspace>(
        this IResourceBuilder<TWorkspace> builder,
        [ResourceName] string name,
        string workspaceProjectName,
        string scriptPath,
        string? packagePath = null,
        string runScriptName = "dev")
        where TWorkspace : JavaScriptWorkspaceResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workspaceProjectName);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);

        var workspace = builder.Resource;
        var resolvedPackagePath = ResolveWorkspacePackagePath(workspace, workspaceProjectName, packagePath);
        var appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(workspace.WorkingDirectory, resolvedPackagePath));
        var resource = new BunAppResource(name, "bun", workspace.WorkingDirectory);

        // Add workspace context annotation BEFORE any setup so AddInstaller skips per-app installer
        ConfigureWorkspaceContext(resource, workspace, workspaceProjectName);
        ConfigureWorkspaceAppPath(resource, appDirectory, resolvedPackagePath);

        var resourceBuilder = builder.ApplicationBuilder
            .CreateBunAppBuilder(resource, scriptPath)
            .WithParentRelationship(workspace)
            .WithRunScript(runScriptName);

        WireUpWorkspaceInstaller(builder, resourceBuilder);

        return resourceBuilder;
    }

    /// <summary>
    /// Resolves the member package path. When the caller supplied an explicit <paramref name="packagePath"/> it
    /// is used verbatim; otherwise the path is derived from workspace discovery by matching the member whose
    /// package.json <c>name</c> equals <paramref name="workspaceProjectName"/>. Discovery is cached on the
    /// workspace resource (see <see cref="JavaScriptWorkspaceResource.GetWorkspaceInfo"/>), so this does not
    /// re-walk the filesystem when the manifest layer already ran during <c>Add*Workspace</c>.
    /// </summary>
    private static string ResolveWorkspacePackagePath(JavaScriptWorkspaceResource workspace, string workspaceProjectName, string? packagePath)
    {
        if (!string.IsNullOrEmpty(packagePath))
        {
            return packagePath;
        }

        // The package-manager annotation is added by the Add*Workspace defaults before any member app is added,
        // so it is always present here. It selects where the member declaration is read from (see GetWorkspaceInfo).
        if (!workspace.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager))
        {
            throw new InvalidOperationException(
                $"Cannot derive the package path for workspace member '{workspaceProjectName}' because workspace '{workspace.Name}' has no package manager configured. Specify the package path explicitly.");
        }

        var workspaceInfo = workspace.GetWorkspaceInfo(packageManager.ExecutableName);
        var member = workspaceInfo.Members.FirstOrDefault(m => string.Equals(m.PackageName, workspaceProjectName, StringComparison.Ordinal));
        if (member is null)
        {
            var discovered = workspaceInfo.Members.Count == 0
                ? "(none discovered)"
                : string.Join(", ", workspaceInfo.Members.Select(m => m.PackageName));
            throw new InvalidOperationException(
                $"Could not find a workspace member named '{workspaceProjectName}' in workspace '{workspace.Name}'. " +
                $"Specify the package path explicitly, or use one of the discovered members: {discovered}.");
        }

        return member.RelativeDir;
    }

    private static void ConfigureWorkspaceContext<TResource>(TResource resource, JavaScriptWorkspaceResource workspace, string workspaceProjectName)
        where TResource : JavaScriptAppResource
    {
        resource.Annotations.Add(new JavaScriptWorkspaceContextAnnotation(workspace, workspaceProjectName));

        // Copy package manager annotation from workspace to app resource
        if (workspace.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pm))
        {
            resource.Annotations.Add(pm);
        }

        // Copy install command annotation from workspace to app resource (used in Docker build)
        if (workspace.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCmd))
        {
            resource.Annotations.Add(installCmd);
        }

        // Copy the workspace's build base image to the member app, but only when the member does not
        // already have one. The publish Dockerfile is generated from the MEMBER resource's
        // DockerfileBaseImageAnnotation, so without this a bun workspace member would build on the
        // default node:* image (which has no bun binary) and the install/build steps would fail.
        // This must run before the deferred WithDockerfileBuilder callback reads the annotation.
        if (!resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out _) &&
            workspace.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImage))
        {
            resource.Annotations.Add(baseImage);
        }
    }

    private static void ConfigureWorkspaceAppPath<TResource>(TResource resource, string appDirectory, string packagePath)
        where TResource : JavaScriptAppResource
    {
        resource.Annotations.Add(new JavaScriptWorkspaceAppPathAnnotation(appDirectory, NormalizeRelativePath(packagePath)));
    }

    private static void WireUpWorkspaceInstaller<TWorkspace, TResource>(
        IResourceBuilder<TWorkspace> workspaceBuilder,
        IResourceBuilder<TResource> appBuilder)
        where TWorkspace : JavaScriptWorkspaceResource
        where TResource : JavaScriptAppResource
    {
        var workspace = workspaceBuilder.Resource;
        if (workspace.TryGetLastAnnotation<JavaScriptPackageInstallerAnnotation>(out var installerAnnotation))
        {
            appBuilder.WaitForCompletion(workspaceBuilder.ApplicationBuilder.CreateResourceBuilder(installerAnnotation.Resource));
        }
    }

    /// <summary>
    /// Discovers workspace manifest files (lockfiles, root configs) plus every member's package.json
    /// and adds them as copy patterns to the Dockerfile manifest layer so that the build environment
    /// matches the workspace configuration during install.
    /// </summary>
    private static void ApplyWorkspaceManifestLayer(JavaScriptWorkspaceResource workspace, JavaScriptPackageManagerAnnotation packageManager)
    {
        // Discover once and cache on the resource so the validator (run at publish/install start)
        // reads the same member set instead of walking the filesystem a second time.
        var workspaceInfo = workspace.GetWorkspaceInfo(packageManager.ExecutableName);

        // The package-manager defaults already seed a copy pattern for the root manifests
        // (e.g. npm's "package*.json", pnpm's "package.json pnpm-lock.yaml pnpm-workspace.yaml").
        // Re-adding those exact files would emit redundant COPY lines, so skip any root file/dir
        // already covered by an existing pattern. The member package.json files added last are never
        // covered by the defaults — they are the whole reason this manifest layer exists.
        var existingSourceTokens = packageManager.PackageFilesPatterns
            .SelectMany(p => p.Source.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        foreach (var file in workspaceInfo.RootFiles)
        {
            if (!IsCoveredBySourceToken(existingSourceTokens, file))
            {
                packageManager.PackageFilesPatterns.Add(new CopyFilePattern(file, $"./{file}"));
            }
        }
        foreach (var dir in workspaceInfo.RootDirs)
        {
            if (!IsCoveredBySourceToken(existingSourceTokens, dir))
            {
                packageManager.PackageFilesPatterns.Add(new CopyFilePattern(dir, $"./{dir}/"));
            }
        }

        foreach (var dir in workspaceInfo.WorkspaceDirs)
        {
            packageManager.PackageFilesPatterns.Add(new CopyFilePattern($"{dir}/package.json", $"./{dir}/package.json"));
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidate"/> (a root-level file or directory
    /// name) is already copied by one of the existing pattern source tokens. Tokens are either literal
    /// names or the single-<c>*</c> glob the npm default uses (<c>package*.json</c>).
    /// </summary>
    /// <remarks>
    /// Matching mirrors Docker COPY / Go <c>filepath.Match</c> semantics where <c>*</c> does NOT cross a
    /// <c>/</c>: that is critical so a root glob like <c>package*.json</c> is never treated as covering a
    /// member manifest such as <c>packages/api/package.json</c> (which would silently drop it from the
    /// cache-stable layer). Any shape we don't model returns <see langword="false"/>, erring toward an
    /// explicit (harmless) COPY rather than dropping a file.
    /// </remarks>
    private static bool IsCoveredBySourceToken(IReadOnlyList<string> sourceTokens, string candidate)
    {
        foreach (var token in sourceTokens)
        {
            if (string.Equals(token, candidate, StringComparison.Ordinal))
            {
                return true;
            }

            var starIndex = token.IndexOf('*', StringComparison.Ordinal);
            if (starIndex < 0 || token.IndexOf('*', starIndex + 1) >= 0)
            {
                // No star, or more than one star: not a shape we evaluate.
                continue;
            }

            var prefix = token[..starIndex];
            var suffix = token[(starIndex + 1)..];
            if (candidate.Length < prefix.Length + suffix.Length ||
                !candidate.StartsWith(prefix, StringComparison.Ordinal) ||
                !candidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // The span the '*' stands in for must not cross a directory separator.
            var middle = candidate[prefix.Length..(candidate.Length - suffix.Length)];
            if (!middle.Contains('/'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the aggregated workspace configuration validator for <paramref name="resource"/> when it is a
    /// workspace member. Resolves the workspace from the context annotation and the sibling member apps
    /// from the application model, then defers to <see cref="WorkspaceConfigurationValidator"/> (which is
    /// idempotent per workspace). No-op for non-workspace resources.
    /// </summary>
    private static void ValidateWorkspaceConfiguration(JavaScriptAppResource resource, IServiceProvider services, bool isPublishMode)
    {
        if (!resource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var context))
        {
            return;
        }

        var workspace = context.Workspace;
        var appModel = services.GetService<DistributedApplicationModel>();

        // All member apps point at the same workspace via the context annotation. We validate them
        // together so the user sees every problem (typos, missing scripts) in one aggregated message.
        var memberApps = appModel is null
            ? [resource]
            : appModel.Resources
                .OfType<JavaScriptAppResource>()
                .Where(r => r.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var ctx) && ReferenceEquals(ctx.Workspace, workspace))
                .ToArray();

        WorkspaceConfigurationValidator.Validate(workspace, memberApps, isPublishMode);
    }

    private static string GetDefaultBaseImage(string appDirectory, string defaultSuffix, IServiceProvider serviceProvider, string? workspaceRoot = null)
    {
        var logger = serviceProvider.GetService<ILogger<JavaScriptAppResource>>() ?? NullLogger<JavaScriptAppResource>.Instance;

        // For workspace members the Node version is commonly pinned at the monorepo root rather than per-app, so the resolver inspects the workspace root first and
        // lets an explicit per-app pin still win. The resolver guards root == appDirectory internally.
        var nodeVersion = NodeVersionResolver.ResolveNodeVersion(appDirectory, logger, workspaceRoot);
        return $"node:{nodeVersion}-{defaultSuffix}";
    }

    private static string GetContainerFilesSourcePath(string outputPath)
    {
        var normalizedPath = NormalizeRelativePath(outputPath);
        return string.IsNullOrEmpty(normalizedPath) || normalizedPath == "."
            ? "/app"
            : $"/app/{normalizedPath}";
    }

    private static string GetBuildOutputPath(IResource resource, string outputPath)
    {
        var normalizedOutputPath = NormalizeRelativePath(outputPath);

        if (!resource.TryGetLastAnnotation<JavaScriptWorkspaceAppPathAnnotation>(out var workspaceAppPath) ||
            string.IsNullOrEmpty(workspaceAppPath.PackagePath) ||
            workspaceAppPath.PackagePath == ".")
        {
            return GetContainerFilesSourcePath(normalizedOutputPath);
        }

        var packageRelativeOutputPath = string.IsNullOrEmpty(normalizedOutputPath) || normalizedOutputPath == "."
            ? workspaceAppPath.PackagePath
            : $"{workspaceAppPath.PackagePath}/{normalizedOutputPath}";

        return GetContainerFilesSourcePath(packageRelativeOutputPath);
    }

    // Re-points the resource's ContainerFilesSourceAnnotation at the build-output path resolved
    // from the (possibly workspace-scoped) package directory. Called from the Dockerfile callback
    // so the SourcePath a consuming resource copies FROM always equals the runtime stage's CopyFrom
    // source — and stays correct regardless of the order PublishAs* and workspace config ran in.
    private static void ResolveWorkspaceAwareContainerFilesSource(IResource resource, string outputPath)
    {
        // A resource without container files (e.g. PublishAsPackageScript clears them) should not
        // gain one here; only re-resolve when a source annotation is already present.
        if (!resource.TryGetLastAnnotation<ContainerFilesSourceAnnotation>(out _))
        {
            return;
        }

        var resolvedSourcePath = GetBuildOutputPath(resource, outputPath);

        foreach (var existing in resource.Annotations.OfType<ContainerFilesSourceAnnotation>().ToList())
        {
            resource.Annotations.Remove(existing);
        }

        resource.Annotations.Add(new ContainerFilesSourceAnnotation { SourcePath = resolvedSourcePath });
    }

    private static string GetPublishedEntryPoint(IResource resource, string entryPoint)
    {
        var normalizedEntryPoint = NormalizeRelativePath(entryPoint);

        if (!resource.TryGetLastAnnotation<JavaScriptWorkspaceAppPathAnnotation>(out var workspaceAppPath) ||
            string.IsNullOrEmpty(workspaceAppPath.PackagePath) ||
            workspaceAppPath.PackagePath == ".")
        {
            return normalizedEntryPoint;
        }

        return NormalizeRelativePath($"{workspaceAppPath.PackagePath}/{normalizedEntryPoint}");
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
}
