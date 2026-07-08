// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Cpp;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding C++ CMake applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class CMakeAppResourceBuilderExtensions
{
    private const string CMakeCommand = "cmake";
    private const string DefaultRunBuildType = "Debug";
    private const string DefaultPublishBuildType = "Release";
    private const string DefaultCMakeHelpLink = "https://cmake.org/download/";
    private const string DefaultVcpkgHelpLink = "https://vcpkg.io/";
    private const string DefaultInstallDirectoryName = "aspire-install";
    private const string DefaultBuildImage = "debian:bookworm";
    private const string DefaultRuntimeImage = "debian:bookworm-slim";
    private const string VcpkgCommand = "vcpkg";
    private const string DockerBuildDirectory = "/src/.aspire/cmake/docker-build";
    private const string DockerRuntimeOutputDirectory = "/src/.aspire/cmake/docker-bin";
    private const string DockerInstallDirectory = "/src/.aspire/cmake/docker-install";
    private const string DockerVcpkgRoot = "/opt/vcpkg";
    private const string DockerVcpkgToolchainFile = "/opt/vcpkg/scripts/buildsystems/vcpkg.cmake";

    /// <summary>
    /// Adds a C++ application built with CMake to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="sourceDirectory">The path to the directory containing the CMake project.</param>
    /// <param name="targetName">The CMake target to build before running the application.</param>
    /// <param name="buildDirectory">
    /// The CMake build directory. When <see langword="null"/>, defaults to
    /// <c>&lt;sourceDirectory&gt;/.aspire/cmake/&lt;name&gt;/build</c>.
    /// </param>
    /// <param name="executablePath">
    /// The path to the executable produced by <paramref name="targetName"/>. When <see langword="null"/>,
    /// the executable is expected under Aspire's managed runtime output directory.
    /// </param>
    /// <param name="buildType">
    /// The CMake build configuration. When <see langword="null"/>, defaults to <c>Debug</c> for local run
    /// and <c>Release</c> for generated Dockerfiles.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// The local development workflow creates two setup resources that run before the application:
    /// <c>cmake -S ... -B ...</c> and <c>cmake --build ... --target ...</c>. The application resource
    /// waits for the build resource to complete successfully before launching the executable.
    /// </para>
    /// <para>
    /// The generated CMake configure command sets <c>CMAKE_RUNTIME_OUTPUT_DIRECTORY</c> and the common
    /// per-configuration output directory variables so single-config and multi-config generators write
    /// executables to a deterministic location. Projects with custom output names or directories can use
    /// <see cref="WithExecutablePath{T}"/> and <see cref="WithConfigureArgs{T}"/> to override the default.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a C++ HTTP API that reads its port from the <c>PORT</c> environment variable:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddCMakeApp("api", "../cpp-api", targetName: "api")
    ///        .WithHttpEndpoint(env: "PORT")
    ///        .WithExternalHttpEndpoints();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<CMakeAppResource> AddCMakeApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string sourceDirectory,
        string targetName,
        string? buildDirectory = null,
        string? executablePath = null,
        string? buildType = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(targetName);

        if (buildType is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(buildType);
        }

        sourceDirectory = Path.GetFullPath(sourceDirectory, builder.AppHostDirectory);
        buildDirectory = buildDirectory is null
            ? Path.Combine(sourceDirectory, ".aspire", "cmake", name, "build")
            : Path.GetFullPath(buildDirectory, builder.AppHostDirectory);

        var runtimeOutputDirectory = Path.Combine(buildDirectory, "aspire-bin");
        executablePath = executablePath is null
            ? Path.Combine(runtimeOutputDirectory, GetExecutableFileName(targetName))
            : Path.GetFullPath(executablePath, builder.AppHostDirectory);

        var resource = new CMakeAppResource(
            name,
            sourceDirectory,
            buildDirectory,
            targetName,
            executablePath,
            runtimeOutputDirectory);

        var resourceBuilder = builder.AddResource(resource)
            .WithArgs(ctx =>
            {
                if (ctx.Resource.TryGetLastAnnotation<CMakeAppArgsAnnotation>(out var argsAnnotation))
                {
                    AddRange(ctx.Args, argsAnnotation.Args);
                }
            })
            .WithRequiredCommand(CMakeCommand, DefaultCMakeHelpLink)
            .WithOtlpExporter()
            .PublishAsDockerFile(containerBuilder =>
            {
                if (File.Exists(Path.Combine(sourceDirectory, "Dockerfile")))
                {
                    return;
                }

                containerBuilder.WithDockerfileBuilder(sourceDirectory, context =>
                {
                    var logger = context.Services.GetService<ILogger<CMakeAppResource>>();
                    context.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);

                    var buildImage = baseImageAnnotation?.BuildImage ?? DefaultBuildImage;
                    var runtimeImage = baseImageAnnotation?.RuntimeImage ?? DefaultRuntimeImage;
                    var publishBuildType = GetBuildType(resource, DefaultPublishBuildType);
                    var targetName = resource.TargetName;
                    var executableName = GetDockerExecutableFileName(targetName);
                    resource.TryGetLastAnnotation<CMakeInstallAnnotation>(out var installAnnotation);
                    var usesVcpkg = resource.TryGetLastAnnotation<CMakeVcpkgAnnotation>(out _);
                    var buildPackages = usesVcpkg
                        ? "build-essential cmake ninja-build ca-certificates git curl zip unzip tar pkg-config"
                        : "build-essential cmake ninja-build ca-certificates";

                    var buildStage = context.Builder
                        .From(buildImage, "build")
                        .WorkDir("/src")
                        .Run($"apt-get update && apt-get install -y --no-install-recommends {buildPackages} && rm -rf /var/lib/apt/lists/*");

                    if (usesVcpkg)
                    {
                        buildStage
                            .Run($"git clone --depth=1 https://github.com/microsoft/vcpkg {DockerVcpkgRoot} && {DockerVcpkgRoot}/bootstrap-vcpkg.sh -disableMetrics")
                            .Env("VCPKG_ROOT", DockerVcpkgRoot);
                    }

                    buildStage
                        .Copy(".", ".")
                        .RunWithMounts(
                            BuildDockerConfigureCommand(resource, publishBuildType),
                            $"type=cache,target={DockerBuildDirectory}")
                        .RunWithMounts(
                            BuildDockerBuildCommand(resource, publishBuildType),
                            $"type=cache,target={DockerBuildDirectory}");

                    if (installAnnotation is not null)
                    {
                        buildStage.RunWithMounts(
                            BuildDockerInstallCommand(publishBuildType),
                            $"type=cache,target={DockerBuildDirectory}");
                    }

                    context.Builder.AddContainerFilesStages(context.Resource, logger);

                    var runtimeStage = context.Builder
                        .From(runtimeImage)
                        .Run("apt-get update && apt-get install -y --no-install-recommends ca-certificates libstdc++6 && rm -rf /var/lib/apt/lists/*")
                        .Run("groupadd --system --gid 999 app && useradd --system --gid 999 --uid 999 --no-create-home app")
                        .WorkDir("/app")
                        .AddContainerFiles(context.Resource, "/app", logger);

                    if (installAnnotation is not null)
                    {
                        var executableRelativePath = GetDockerInstallExecutableRelativePath(resource, installAnnotation.ExecutableRelativePath);
                        runtimeStage.CopyFrom(buildStage.StageName!, $"{DockerInstallDirectory}/", "/app/");
                        runtimeStage.User("app").Entrypoint([$"/app/{executableRelativePath}"]);
                    }
                    else
                    {
                        runtimeStage.CopyFrom(buildStage.StageName!, $"{DockerRuntimeOutputDirectory}/{executableName}", $"/app/{executableName}");
                        runtimeStage.User("app").Entrypoint([$"/app/{executableName}"]);
                    }
                });
            });

        if (buildType is not null)
        {
            resourceBuilder.WithBuildType(buildType);
        }

        if (builder.ExecutionContext.IsRunMode)
        {
            AddCMakeSetupResources(resourceBuilder);
        }

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

        return resourceBuilder;
    }

    /// <summary>
    /// Passes extra arguments to the C++ application at runtime.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="args">The arguments to pass to the application executable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithAppArgs<T>(this IResourceBuilder<T> builder, params object[] args)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithAnnotation(new CMakeAppArgsAnnotation(args), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds extra arguments to the generated <c>cmake -S ... -B ...</c> configure command.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="args">The arguments to append to the CMake configure command.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithConfigureArgs<T>(this IResourceBuilder<T> builder, params string[] args)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithAnnotation(new CMakeConfigureArgsAnnotation(args), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds extra arguments to the generated <c>cmake --build</c> command.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="args">The arguments to append to the CMake build command.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithBuildArgs<T>(this IResourceBuilder<T> builder, params string[] args)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithAnnotation(new CMakeBuildArgsAnnotation(args), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures the CMake build type used by the local build and generated Dockerfile.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="buildType">The CMake build type, such as <c>Debug</c> or <c>Release</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithBuildType<T>(this IResourceBuilder<T> builder, string buildType)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(buildType);

        return builder.WithAnnotation(new CMakeBuildTypeAnnotation(buildType), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures the CMake application to use vcpkg for dependency resolution.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="vcpkgRoot">
    /// The path to the local vcpkg root. When <see langword="null"/> in run mode, the <c>VCPKG_ROOT</c>
    /// environment variable is used.
    /// </param>
    /// <param name="helpLink">An optional URL shown to users when the local vcpkg command is missing.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// In run mode, this method adds the local vcpkg CMake toolchain file to the configure command and
    /// declares the <c>vcpkg</c> command as a required build tool. In publish mode, generated Dockerfiles
    /// bootstrap vcpkg inside the build image and use the container-local toolchain path.
    /// </para>
    /// </remarks>
    /// <example>
    /// Use vcpkg manifest mode for a CMake application:
    /// <code lang="csharp">
    /// builder.AddCMakeApp("api", "../cpp-api", targetName: "api")
    ///        .WithVcpkg()
    ///        .WithHttpEndpoint(env: "PORT");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithVcpkg<T>(this IResourceBuilder<T> builder, string? vcpkgRoot = null, string? helpLink = DefaultVcpkgHelpLink)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (vcpkgRoot is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(vcpkgRoot);
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            vcpkgRoot ??= Environment.GetEnvironmentVariable("VCPKG_ROOT")
                ?? throw new InvalidOperationException("Set VCPKG_ROOT to the root of your vcpkg installation, or pass the vcpkg root path to WithVcpkg.");

            builder.WithRequiredBuildTool(VcpkgCommand, helpLink);
        }

        return builder.WithAnnotation(new CMakeVcpkgAnnotation(vcpkgRoot), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures the CMake application to run from the output of <c>cmake --install</c>.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="executableRelativePath">
    /// The executable path relative to the CMake install prefix. When <see langword="null"/>, defaults to
    /// <c>bin/&lt;targetName&gt;</c>.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// Use this method when the CMake project has <c>install()</c> rules that define the runtime closure.
    /// Local run mode adds a hidden <c>cmake --install</c> resource after the build resource. Generated
    /// Dockerfiles install into an intermediate prefix and copy the full installed tree into the runtime image.
    /// </para>
    /// <para>
    /// If the installed executable is not under <c>bin/&lt;targetName&gt;</c>, pass the relative path to
    /// <paramref name="executableRelativePath"/>. Use this parameter instead of <see cref="WithExecutablePath{T}"/>
    /// when install mode is enabled.
    /// </para>
    /// </remarks>
    /// <example>
    /// Run and publish a CMake application from its install output:
    /// <code lang="csharp">
    /// builder.AddCMakeApp("api", "../cpp-api", targetName: "api")
    ///        .WithCMakeInstall()
    ///        .WithHttpEndpoint(env: "PORT");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithCMakeInstall<T>(this IResourceBuilder<T> builder, string? executableRelativePath = null)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateExecutableRelativePath(executableRelativePath);

        builder.WithAnnotation(new CMakeInstallAnnotation(executableRelativePath), ResourceAnnotationMutationBehavior.Replace);
        builder.WithCommand(GetLocalInstallExecutablePath(builder.Resource, executableRelativePath));

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
            !builder.Resource.TryGetLastAnnotation<CMakeInstallResourceAnnotation>(out _))
        {
            AddCMakeInstallResource(builder);
        }

        return builder;
    }

    /// <summary>
    /// Configures the path to the executable produced by the CMake target.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="executablePath">The path to the executable to run. Relative paths are resolved from the AppHost directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithExecutablePath<T>(this IResourceBuilder<T> builder, string executablePath)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(executablePath);

        if (builder.Resource.TryGetLastAnnotation<CMakeInstallAnnotation>(out _))
        {
            throw new InvalidOperationException("WithExecutablePath cannot be used after WithCMakeInstall. Pass the installed executable path to WithCMakeInstall instead.");
        }

        executablePath = Path.GetFullPath(executablePath, builder.ApplicationBuilder.AppHostDirectory);
        return builder.WithCommand(executablePath);
    }

    /// <summary>
    /// Declares that the CMake application requires an additional build tool to be available on the local machine PATH.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="command">The command that should be available on the local machine PATH.</param>
    /// <param name="helpLink">An optional URL shown to users when the command is missing.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithRequiredBuildTool<T>(this IResourceBuilder<T> builder, string command, string? helpLink = null)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(command);

        return builder.WithRequiredCommand(command, helpLink);
    }

    /// <summary>
    /// Declares that the CMake application requires additional build tools to be available on the local machine PATH.
    /// </summary>
    /// <typeparam name="T">The type of the CMake application resource.</typeparam>
    /// <param name="builder">The resource builder for the CMake application.</param>
    /// <param name="tools">The build tools that should be available on the local machine PATH.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Uses CMakeBuildTool, which is not ATS-compatible. Polyglot app hosts can call withRequiredBuildTool repeatedly.")]
    public static IResourceBuilder<T> WithRequiredBuildTools<T>(this IResourceBuilder<T> builder, params CMakeBuildTool[] tools)
        where T : CMakeAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            builder.WithRequiredBuildTool(tool.Command, tool.HelpLink);
        }

        return builder;
    }

    private static void AddCMakeSetupResources(IResourceBuilder<CMakeAppResource> builder)
    {
        var resource = builder.Resource;
        var configureResource = new ExecutableResource($"{resource.Name}-cmake-configure", CMakeCommand, resource.SourceDirectory);

        var configure = builder.ApplicationBuilder
            .AddResource(configureResource)
            .WithArgs(ctx => AddConfigureArgs(resource, ctx.Args))
            .WithRequiredCommand(CMakeCommand, DefaultCMakeHelpLink)
            .ExcludeFromManifest();

        var buildResource = new ExecutableResource($"{resource.Name}-cmake-build", CMakeCommand, resource.SourceDirectory);

        var build = builder.ApplicationBuilder
            .AddResource(buildResource)
            .WithArgs(ctx => AddBuildArgs(resource, ctx.Args))
            .WithRequiredCommand(CMakeCommand, DefaultCMakeHelpLink)
            .ExcludeFromManifest()
            .WaitForCompletion(configure);

        builder
            .WithAnnotation(new CMakeConfigureResourceAnnotation(configure))
            .WithAnnotation(new CMakeBuildResourceAnnotation(build))
            .WaitForCompletion(build);
    }

    private static void AddCMakeInstallResource(IResourceBuilder<CMakeAppResource> builder)
    {
        var resource = builder.Resource;

        if (!resource.TryGetLastAnnotation<CMakeBuildResourceAnnotation>(out var buildAnnotation))
        {
            throw new InvalidOperationException("CMake install resources can only be added in run mode after the CMake build resource has been created.");
        }

        var installResource = new ExecutableResource($"{resource.Name}-cmake-install", CMakeCommand, resource.SourceDirectory);

        var install = builder.ApplicationBuilder
            .AddResource(installResource)
            .WithArgs(ctx => AddInstallArgs(resource, ctx.Args))
            .WithRequiredCommand(CMakeCommand, DefaultCMakeHelpLink)
            .ExcludeFromManifest()
            .WaitForCompletion(buildAnnotation.ResourceBuilder);

        builder
            .WithAnnotation(new CMakeInstallResourceAnnotation(install))
            .WaitForCompletion(install);
    }

    private static void AddConfigureArgs(CMakeAppResource resource, IList<object> args)
    {
        var buildType = GetBuildType(resource, DefaultRunBuildType);

        args.Add("-S");
        args.Add(resource.SourceDirectory);
        args.Add("-B");
        args.Add(resource.BuildDirectory);
        args.Add($"-DCMAKE_BUILD_TYPE={buildType}");
        args.Add($"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY={resource.RuntimeOutputDirectory}");

        foreach (var configurationName in GetCommonConfigurationNames(buildType))
        {
            args.Add($"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_{configurationName.ToUpperInvariant()}={resource.RuntimeOutputDirectory}");
        }

        var usesVcpkg = resource.TryGetLastAnnotation<CMakeVcpkgAnnotation>(out var vcpkgAnnotation);
        if (vcpkgAnnotation?.Root is not null)
        {
            args.Add($"-DCMAKE_TOOLCHAIN_FILE={GetVcpkgToolchainFile(vcpkgAnnotation.Root)}");
        }

        if (resource.TryGetLastAnnotation<CMakeConfigureArgsAnnotation>(out var configureArgsAnnotation))
        {
            foreach (var arg in configureArgsAnnotation.Args)
            {
                if (usesVcpkg && IsVcpkgToolchainFileArg(arg))
                {
                    continue;
                }

                args.Add(arg);
            }
        }
    }

    private static void AddBuildArgs(CMakeAppResource resource, IList<object> args)
    {
        args.Add("--build");
        args.Add(resource.BuildDirectory);
        args.Add("--config");
        args.Add(GetBuildType(resource, DefaultRunBuildType));
        args.Add("--target");
        args.Add(resource.TargetName);

        if (resource.TryGetLastAnnotation<CMakeBuildArgsAnnotation>(out var buildArgsAnnotation))
        {
            AddRange(args, buildArgsAnnotation.Args);
        }
    }

    private static void AddInstallArgs(CMakeAppResource resource, IList<object> args)
    {
        args.Add("--install");
        args.Add(resource.BuildDirectory);
        args.Add("--config");
        args.Add(GetBuildType(resource, DefaultRunBuildType));
        args.Add("--prefix");
        args.Add(GetInstallDirectory(resource));
    }

    private static void AddRange(IList<object> args, IEnumerable<object> values)
    {
        foreach (var value in values)
        {
            args.Add(value);
        }
    }

    private static string GetBuildType(IResource resource, string defaultBuildType) =>
        resource.TryGetLastAnnotation<CMakeBuildTypeAnnotation>(out var buildTypeAnnotation)
            ? buildTypeAnnotation.BuildType
            : defaultBuildType;

    private static IEnumerable<string> GetCommonConfigurationNames(string buildType)
    {
        var names = new[] { buildType, "Debug", "Release", "RelWithDebInfo", "MinSizeRel" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (seen.Add(name))
            {
                yield return name;
            }
        }
    }

    private static string GetExecutableFileName(string targetName) =>
        OperatingSystem.IsWindows() && !targetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? $"{targetName}.exe"
            : targetName;

    private static string GetDockerExecutableFileName(string targetName) =>
        targetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? targetName[..^4]
            : targetName;

    private static string BuildDockerConfigureCommand(CMakeAppResource resource, string buildType)
    {
        var args = new List<string>
        {
            "cmake",
            "-S", ".",
            "-B", DockerBuildDirectory,
            "-G", "Ninja",
            $"-DCMAKE_BUILD_TYPE={ShellQuote(buildType)}",
            $"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY={DockerRuntimeOutputDirectory}"
        };

        foreach (var configurationName in GetCommonConfigurationNames(buildType))
        {
            args.Add($"-DCMAKE_RUNTIME_OUTPUT_DIRECTORY_{configurationName.ToUpperInvariant()}={DockerRuntimeOutputDirectory}");
        }

        var usesVcpkg = resource.TryGetLastAnnotation<CMakeVcpkgAnnotation>(out _);
        if (usesVcpkg)
        {
            args.Add($"-DCMAKE_TOOLCHAIN_FILE={DockerVcpkgToolchainFile}");
        }

        if (resource.TryGetLastAnnotation<CMakeConfigureArgsAnnotation>(out var configureArgsAnnotation))
        {
            foreach (var arg in configureArgsAnnotation.Args)
            {
                if (usesVcpkg && IsVcpkgToolchainFileArg(arg))
                {
                    continue;
                }

                args.Add(ShellQuote(arg));
            }
        }

        return string.Join(" ", args);
    }

    private static string BuildDockerBuildCommand(CMakeAppResource resource, string buildType)
    {
        var args = new List<string>
        {
            "cmake",
            "--build",
            DockerBuildDirectory,
            "--config",
            ShellQuote(buildType),
            "--target",
            ShellQuote(resource.TargetName)
        };

        if (resource.TryGetLastAnnotation<CMakeBuildArgsAnnotation>(out var buildArgsAnnotation))
        {
            foreach (var arg in buildArgsAnnotation.Args)
            {
                args.Add(ShellQuote(arg));
            }
        }

        return string.Join(" ", args);
    }

    private static string BuildDockerInstallCommand(string buildType)
    {
        var args = new List<string>
        {
            "cmake",
            "--install",
            DockerBuildDirectory,
            "--config",
            ShellQuote(buildType),
            "--prefix",
            DockerInstallDirectory
        };

        return string.Join(" ", args);
    }

    private static string GetVcpkgToolchainFile(string vcpkgRoot) =>
        Path.Combine(vcpkgRoot, "scripts", "buildsystems", "vcpkg.cmake");

    private static string GetInstallDirectory(CMakeAppResource resource) =>
        Path.Combine(resource.BuildDirectory, DefaultInstallDirectoryName);

    private static string GetLocalInstallExecutablePath(CMakeAppResource resource, string? executableRelativePath)
    {
        var relativePath = executableRelativePath ?? Path.Combine("bin", GetExecutableFileName(resource.TargetName));
        relativePath = NormalizeLocalRelativePath(relativePath);

        return Path.Combine(GetInstallDirectory(resource), relativePath);
    }

    private static string GetDockerInstallExecutableRelativePath(CMakeAppResource resource, string? executableRelativePath) =>
        NormalizeDockerRelativePath(executableRelativePath ?? $"bin/{GetDockerExecutableFileName(resource.TargetName)}");

    private static string NormalizeLocalRelativePath(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string NormalizeDockerRelativePath(string path) =>
        path.Replace('\\', '/');

    private static void ValidateExecutableRelativePath(string? executableRelativePath)
    {
        if (executableRelativePath is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrEmpty(executableRelativePath);

        if (Path.IsPathRooted(executableRelativePath) ||
            executableRelativePath.StartsWith('/') ||
            executableRelativePath.StartsWith('\\') ||
            executableRelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal) ||
            (executableRelativePath.Length >= 2 && char.IsAsciiLetter(executableRelativePath[0]) && executableRelativePath[1] == ':'))
        {
            throw new ArgumentException("The executable path must be relative to the CMake install prefix.", nameof(executableRelativePath));
        }
    }

    private static bool IsVcpkgToolchainFileArg(string arg)
    {
        const string toolchainPrefix = "-DCMAKE_TOOLCHAIN_FILE=";

        if (!arg.StartsWith(toolchainPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var toolchainPath = arg[toolchainPrefix.Length..].Replace('\\', '/');
        return toolchainPath.EndsWith("/scripts/buildsystems/vcpkg.cmake", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wraps <paramref name="value"/> in POSIX single quotes for Dockerfile shell-form commands.
    /// Embedded single quotes are escaped with the standard POSIX <c>'\''</c> sequence.
    /// </summary>
    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
