// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECERTIFICATES001

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Python applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class PythonAppResourceBuilderExtensions
{
    private const string DefaultVirtualEnvFolder = ".venv";
    private const string DefaultPythonVersion = "3.13";

    /// <summary>
    /// Adds a Python application to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the python application.</param>
    /// <param name="scriptPath">The path to the script relative to the app directory to run.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method executes a Python script directly using <c>python script.py</c>.
    /// By default, the virtual environment is resolved using the following priority:
    /// <list type="number">
    /// <item>If <c>.venv</c> exists in the app directory, use it.</item>
    /// <item>If <c>.venv</c> exists in the AppHost directory, use it.</item>
    /// <item>Otherwise, default to <c>.venv</c> in the app directory.</item>
    /// </list>
    /// Use <see cref="WithVirtualEnvironment{T}(IResourceBuilder{T}, string, bool)"/> to specify a different virtual environment path.
    /// Use <c>WithArgs</c> to pass arguments to the script.
    /// </para>
    /// <para>
    /// Python applications automatically have debugging support enabled.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a FastAPI Python application to the application model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddPythonApp("fastapi-app", "../api", "main.py")
    ///        .WithArgs("arg1", "arg2");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [OverloadResolutionPriority(1)]
    [AspireExport("addPythonApp", Description = "Adds a Python script application resource")]
    public static IResourceBuilder<PythonAppResource> AddPythonApp(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string scriptPath)
        => AddPythonAppCore(builder, name, appDirectory, EntrypointType.Script, scriptPath, DefaultVirtualEnvFolder)
            .WithDebugging();

    /// <summary>
    /// Adds a Python module to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the python application.</param>
    /// <param name="moduleName">The name of the Python module to run (e.g., "flask", "uvicorn").</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method runs a Python module using <c>python -m &lt;module&gt;</c>.
    /// By default, the virtual environment folder is expected to be named <c>.venv</c> and located in the app directory.
    /// Use <see cref="WithVirtualEnvironment{T}(IResourceBuilder{T}, string, bool)"/> to specify a different virtual environment path.
    /// Use <c>WithArgs</c> to pass arguments to the module.
    /// </para>
    /// <para>
    /// Python modules automatically have debugging support enabled.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Flask module to the application model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddPythonModule("flask-dev", "../flaskapp", "flask")
    ///        .WithArgs("run", "--debug", "--host=0.0.0.0");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addPythonModule", Description = "Adds a Python module application resource")]
    public static IResourceBuilder<PythonAppResource> AddPythonModule(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string moduleName)
        => AddPythonAppCore(builder, name, appDirectory, EntrypointType.Module, moduleName, DefaultVirtualEnvFolder)
            .WithDebugging();

    /// <summary>
    /// Adds a Python executable to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the python application.</param>
    /// <param name="executableName">The name of the executable in the virtual environment (e.g., "pytest", "uvicorn", "flask").</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method runs an executable from the virtual environment's bin directory.
    /// By default, the virtual environment folder is expected to be named <c>.venv</c> and located in the app directory.
    /// Use <see cref="WithVirtualEnvironment{T}(IResourceBuilder{T}, string, bool)"/> to specify a different virtual environment path.
    /// Use <c>WithArgs</c> to pass arguments to the executable.
    /// </para>
    /// <para>
    /// Unlike scripts and modules, Python executables do not have debugging support enabled by default.
    /// Use <see cref="WithDebugging"/> to explicitly enable debugging support if the executable is a Python-based
    /// tool that can be debugged.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a pytest executable to the application model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddPythonExecutable("pytest", "../api", "pytest")
    ///        .WithArgs("-q")
    ///        .WithDebugging();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addPythonExecutable", Description = "Adds a Python executable application resource")]
    public static IResourceBuilder<PythonAppResource> AddPythonExecutable(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string executableName)
        => AddPythonAppCore(builder, name, appDirectory, EntrypointType.Executable, executableName, DefaultVirtualEnvFolder);

    /// <summary>
    /// Adds a python application with a virtual environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the python app files.</param>
    /// <param name="scriptPath">The path to the script relative to the app directory to run.</param>
    /// <param name="scriptArgs">The arguments for the script.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload is obsolete. Use one of the more specific methods instead:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="AddPythonApp(IDistributedApplicationBuilder, string, string, string)"/> - To run a Python script file</description></item>
    /// <item><description><see cref="AddPythonModule"/> - To run a Python module via <c>python -m</c></description></item>
    /// <item><description><see cref="AddPythonExecutable"/> - To run an executable from the virtual environment</description></item>
    /// </list>
    /// <para>
    /// Chain with <c>WithArgs</c> to pass arguments:
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddPythonScript("name", "dir", "script.py")
    ///        .WithArgs("arg1", "arg2");
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete("Use AddPythonScript, AddPythonModule, or AddPythonExecutable and chain with .WithArgs(...) instead.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<PythonAppResource> AddPythonApp(
        this IDistributedApplicationBuilder builder, string name, string appDirectory, string scriptPath, params string[] scriptArgs)
    {
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);
        ThrowIfNullOrContainsIsNullOrEmpty(scriptArgs);
        return AddPythonAppCore(builder, name, appDirectory, EntrypointType.Script, scriptPath, DefaultVirtualEnvFolder)
            .WithDebugging()
            .WithArgs(scriptArgs);
    }

    /// <summary>
    /// Adds a python application with a virtual environment to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the python app files.</param>
    /// <param name="scriptPath">The path to the script to run, relative to the app directory.</param>
    /// <param name="virtualEnvironmentPath">Path to the virtual environment.</param>
    /// <param name="scriptArgs">The arguments for the script.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload is obsolete. Use one of the more specific methods instead:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="AddPythonApp(IDistributedApplicationBuilder, string, string, string)"/> - To run a Python script file</description></item>
    /// <item><description><see cref="AddPythonModule"/> - To run a Python module via <c>python -m</c></description></item>
    /// <item><description><see cref="AddPythonExecutable"/> - To run an executable from the virtual environment</description></item>
    /// </list>
    /// <para>
    /// Chain with <see cref="WithVirtualEnvironment"/> and <c>WithArgs</c>:
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddPythonScript("name", "dir", "script.py")
    ///        .WithVirtualEnvironment("myenv")
    ///        .WithArgs("arg1", "arg2");
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete("Use AddPythonScript, AddPythonModule, or AddPythonExecutable and chain with .WithVirtualEnvironment(...).WithArgs(...) instead.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IResourceBuilder<PythonAppResource> AddPythonApp(
        this IDistributedApplicationBuilder builder, string name, string appDirectory, string scriptPath,
        string virtualEnvironmentPath, params string[] scriptArgs)
    {
        ThrowIfNullOrContainsIsNullOrEmpty(scriptArgs);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);
        return AddPythonAppCore(builder, name, appDirectory, EntrypointType.Script, scriptPath, virtualEnvironmentPath)
            .WithDebugging()
            .WithArgs(scriptArgs);
    }

    /// <summary>
    /// Adds a Uvicorn-based Python application to the distributed application builder with HTTP endpoint configuration.
    /// </summary>
    /// <param name="builder">The distributed application builder to which the Uvicorn application resource will be added.</param>
    /// <param name="name">The unique name of the Uvicorn application resource.</param>
    /// <param name="appDirectory">The directory containing the Python application files.</param>
    /// <param name="app">The ASGI app import path which informs Uvicorn which module and variable to load as your web application.
    /// For example, "main:app" means "main.py" file and variable named "app".</param>
    /// <returns>A resource builder for further configuration of the Uvicorn Python application resource.</returns>
    /// <remarks>
    /// <para>
    /// This method configures the application to use Uvicorn as the ASGI server and exposes an HTTP
    /// endpoint. When publishing, it sets the entry point to use the Uvicorn executable with appropriate arguments for
    /// host and port.
    /// </para>
    /// <para>
    /// By default, the virtual environment folder is expected to be named <c>.venv</c> and located in the app directory.
    /// Use <see cref="WithVirtualEnvironment"/> to specify a different virtual environment path.
    /// </para>
    /// <para>
    /// In non-publish mode, the <c>--reload</c> flag is automatically added to enable hot reload during development.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a FastAPI application using Uvicorn:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var api = builder.AddUvicornApp("api", "../fastapi-app", "main:app")
    ///     .WithUv()
    ///     .WithExternalHttpEndpoints();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addUvicornApp", Description = "Adds a Uvicorn-based Python application resource")]
    public static IResourceBuilder<UvicornAppResource> AddUvicornApp(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory, string app)
    {
        var resourceBuilder =
            AddPythonAppCore(
                builder,
                name,
                appDirectory,
                EntrypointType.Executable,
                "uvicorn",
                DefaultVirtualEnvFolder,
                (n, e, d) => new UvicornAppResource(n, e, d))
            .WithDebugging()
            .WithHttpEndpoint(env: "PORT")
            .WithArgs(c =>
            {
                c.Args.Add(app);

                c.Args.Add("--host");
                var endpoint = ((IResourceWithEndpoints)c.Resource).GetEndpoint("http");
                if (builder.ExecutionContext.IsPublishMode)
                {
                    c.Args.Add("0.0.0.0");
                }
                else
                {
                    c.Args.Add(endpoint.EndpointAnnotation.TargetHost);
                }

                c.Args.Add("--port");
                c.Args.Add(endpoint.Property(EndpointProperty.TargetPort));

                // Add hot reload in non-publish mode
                if (!builder.ExecutionContext.IsPublishMode)
                {
                    c.Args.Add("--reload");
                }
            })
            .WithHttpsCertificateConfiguration(ctx =>
            {
                ctx.Arguments.Add("--ssl-keyfile");
                ctx.Arguments.Add(ctx.KeyPath);
                ctx.Arguments.Add("--ssl-certfile");
                ctx.Arguments.Add(ctx.CertificatePath);
                if (ctx.Password is not null)
                {
                    ctx.Arguments.Add("--ssl-keyfile-password");
                    ctx.Arguments.Add(ctx.Password);
                }

                return Task.CompletedTask;
            });

        if (builder.ExecutionContext.IsRunMode)
        {
            // If a TLS certificate is configured, override the endpoint to use HTTPS instead of HTTP.
            // Uvicorn only supports binding to a single port.
            resourceBuilder.SubscribeHttpsEndpointsUpdate(ctx =>
            {
                resourceBuilder.WithEndpoint("http", ep => ep.UriScheme = "https");
            });
        }

        return resourceBuilder;
    }

    private static IResourceBuilder<PythonAppResource> AddPythonAppCore(
        IDistributedApplicationBuilder builder, string name, string appDirectory, EntrypointType entrypointType,
        string entrypoint, string virtualEnvironmentPath)
    {
        return AddPythonAppCore(builder, name, appDirectory, entrypointType, entrypoint,
            virtualEnvironmentPath, (n, e, d) => new PythonAppResource(n, e, d));
    }

    private static IResourceBuilder<T> AddPythonAppCore<T>(
        IDistributedApplicationBuilder builder, string name, string appDirectory, EntrypointType entrypointType,
        string entrypoint, string virtualEnvironmentPath, Func<string, string, string, T> createResource) where T : PythonAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(appDirectory);
        ArgumentException.ThrowIfNullOrEmpty(entrypoint);
        ArgumentNullException.ThrowIfNull(virtualEnvironmentPath);

        // When using the default virtual environment path, look for existing virtual environments
        // in multiple locations: app directory first, then AppHost directory as fallback
        var resolvedVenvPath = virtualEnvironmentPath;
        if (virtualEnvironmentPath == DefaultVirtualEnvFolder)
        {
            resolvedVenvPath = ResolveDefaultVirtualEnvironmentPath(builder, appDirectory, virtualEnvironmentPath);
        }

        // python will be replaced with the resolved entrypoint based on the virtualEnvironmentPath
        var resource = createResource(name, "python", Path.GetFullPath(appDirectory, builder.AppHostDirectory));

        var resourceBuilder = builder
            .AddResource(resource)
            // Order matters, we need to bootstrap the entrypoint before setting the entrypoint
            .WithAnnotation(new PythonEntrypointAnnotation
            {
                Type = entrypointType,
                Entrypoint = entrypoint
            })
            // This will resolve the correct python executable based on the virtual environment
            .WithVirtualEnvironment(resolvedVenvPath)
            // This will set up the the entrypoint based on the PythonEntrypointAnnotation
            .WithEntrypoint(entrypointType, entrypoint);

        resourceBuilder.WithIconName("CodePyRectangle");

        resourceBuilder.WithOtlpExporter();

        // Configure OpenTelemetry exporters using environment variables
        // https://opentelemetry.io/docs/specs/otel/configuration/sdk-environment-variables/#exporter-selection
        resourceBuilder.WithEnvironment(context =>
        {
            context.EnvironmentVariables["OTEL_TRACES_EXPORTER"] = "otlp";
            context.EnvironmentVariables["OTEL_LOGS_EXPORTER"] = "otlp";
            context.EnvironmentVariables["OTEL_METRICS_EXPORTER"] = "otlp";

            // Make sure to attach the logging instrumentation setting, so we can capture logs.
            // Without this you'll need to configure logging yourself. Which is kind of a pain.
            context.EnvironmentVariables["OTEL_PYTHON_LOGGING_AUTO_INSTRUMENTATION_ENABLED"] = "true";

            // Set PYTHONUTF8=1 on Windows in run mode to enable UTF-8 mode
            // See: https://docs.python.org/3/using/cmdline.html#envvar-PYTHONUTF8
            if (OperatingSystem.IsWindows() && context.ExecutionContext.IsRunMode)
            {
                context.EnvironmentVariables["PYTHONUTF8"] = "1";
            }
        });

        // Configure required environment variables for custom certificate trust when running as an executable.
        resourceBuilder
            .WithCertificateTrustScope(CertificateTrustScope.System)
            .WithCertificateTrustConfiguration(ctx =>
            {
                if (ctx.Scope == CertificateTrustScope.Append)
                {
                    var resourceLogger = ctx.ExecutionContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                    var logger = resourceLogger.GetLogger(ctx.Resource);
                    logger.LogInformation("Certificate trust scope is set to 'Append', but Python resources do not support appending to the default certificate authorities; only OTLP certificate trust will be applied.");
                }
                else
                {
                    // Override default certificates path for the requests module.
                    // See: https://docs.python-requests.org/en/latest/user/advanced/#ssl-cert-verification
                    ctx.EnvironmentVariables["REQUESTS_CA_BUNDLE"] = ctx.CertificateBundlePath;

                    // Requests also supports CURL_CA_BUNDLE as an alternative config (lower priority than REQUESTS_CA_BUNDLE).
                    // Setting it to be as complete as possible and avoid potential issues with conflicting configurations.
                    ctx.EnvironmentVariables["CURL_CA_BUNDLE"] = ctx.CertificateBundlePath;
                }

                // Override default opentelemetry-python certificate bundle path
                // See: https://opentelemetry-python.readthedocs.io/en/latest/exporter/otlp/otlp.html#module-opentelemetry.exporter.otlp
                ctx.EnvironmentVariables["OTEL_EXPORTER_OTLP_CERTIFICATE"] = ctx.CertificateBundlePath;

                return Task.CompletedTask;
            });

        resourceBuilder.PublishAsDockerFile(c =>
        {
            // Only generate a Dockerfile if one doesn't already exist in the app directory
            if (File.Exists(Path.Combine(resource.WorkingDirectory, "Dockerfile")))
            {
                return;
            }

            c.WithDockerfileBuilder(resource.WorkingDirectory,
                context =>
                {
                    if (!context.Resource.TryGetLastAnnotation<PythonEntrypointAnnotation>(out var entrypointAnnotation))
                    {
                        // No entrypoint annotation found, cannot generate Dockerfile
                        return;
                    }

                    // Try to get Python environment annotation
                    context.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var pythonEnvironmentAnnotation);

                    // Detect Python version
                    var pythonVersion = pythonEnvironmentAnnotation?.Version;
                    if (pythonVersion is null)
                    {
                        var virtualEnvironment = pythonEnvironmentAnnotation?.VirtualEnvironment;
                        pythonVersion = PythonVersionDetector.DetectVersion(appDirectory, virtualEnvironment);
                    }

                    // if we could not detect Python version, use the default
                    pythonVersion ??= DefaultPythonVersion;

                    var entrypointType = entrypointAnnotation.Type;
                    var entrypoint = entrypointAnnotation.Entrypoint;

                    // Check if using UV by looking at the package manager annotation
                    var isUsingUv = context.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var pkgMgr) &&
                                    pkgMgr.ExecutableName == "uv";

                    if (isUsingUv)
                    {
                        GenerateUvDockerfile(context, resource, pythonVersion, entrypointType, entrypoint);
                    }
                    else
                    {
                        GenerateFallbackDockerfile(context, resource, pythonVersion, entrypointType, entrypoint);
                    }
                });
        });

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

        if (builder.ExecutionContext.IsRunMode)
        {
            // Subscribe to BeforeStartEvent for this specific resource to wire up dependencies dynamically
            // This allows methods like WithPip, WithUv, and WithVirtualEnvironment to add/remove resources
            // and the dependencies will be established based on which resources actually exist
            // Only do this in run mode since the installer and venv creator only run in run mode
            var resourceToSetup = resourceBuilder.Resource;
            builder.Eventing.Subscribe<BeforeStartEvent>((evt, ct) =>
            {
                // Wire up wait dependencies for this resource based on which child resources exist
                SetupDependencies(builder, resourceToSetup);
                return Task.CompletedTask;
            });

            // Automatically add pip as the package manager if pyproject.toml or requirements.txt exists
            // Only do this in run mode since the installer resource only runs in run mode
            // Note: pip supports both pyproject.toml and requirements.txt
            var appDirectoryFullPath = Path.GetFullPath(appDirectory, builder.AppHostDirectory);

            if (File.Exists(Path.Combine(appDirectoryFullPath, "pyproject.toml")) ||
                File.Exists(Path.Combine(appDirectoryFullPath, "requirements.txt")))
            {
                resourceBuilder.WithPip();
            }
            else
            {
                // No package files found, but we should still create venv if it doesn't exist
                // and createIfNotExists is true (which is the default)
                CreateVenvCreatorIfNeeded(resourceBuilder);
            }
        }

        return resourceBuilder;
    }

    private static void GenerateUvDockerfile(DockerfileBuilderCallbackContext context, PythonAppResource resource,
        string pythonVersion, EntrypointType entrypointType, string entrypoint)
    {
        // Check if uv.lock exists in the working directory
        var uvLockPath = Path.Combine(resource.WorkingDirectory, "uv.lock");
        var hasUvLock = File.Exists(uvLockPath);

        // Get custom base images from annotation, if present
        context.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
        var buildImage = baseImageAnnotation?.BuildImage ?? $"ghcr.io/astral-sh/uv:python{pythonVersion}-bookworm-slim";
        var runtimeImage = baseImageAnnotation?.RuntimeImage ?? $"python:{pythonVersion}-slim-bookworm";

        var builderStage = context.Builder
            .From(buildImage, "builder")
            .EmptyLine()
            .Comment("Enable bytecode compilation and copy mode for the virtual environment")
            .Env("UV_COMPILE_BYTECODE", "1")
            .Env("UV_LINK_MODE", "copy")
            .EmptyLine()
            .WorkDir("/app")
            .EmptyLine();

        if (hasUvLock)
        {
            // If uv.lock exists, use locked mode for reproducible builds
            builderStage
                .Comment("Install dependencies first for better layer caching")
                .Comment("Uses BuildKit cache mounts to speed up repeated builds")
                .RunWithMounts(
                    "uv sync --locked --no-install-project --no-dev",
                    "type=cache,target=/root/.cache/uv",
                    "type=bind,source=uv.lock,target=uv.lock",
                    "type=bind,source=pyproject.toml,target=pyproject.toml")
                .EmptyLine()
                .Comment("Copy the rest of the application source and install the project")
                .Copy(".", "/app")
                .RunWithMounts(
                    "uv sync --locked --no-dev",
                    "type=cache,target=/root/.cache/uv");
        }
        else
        {
            // If uv.lock doesn't exist, copy pyproject.toml and generate lock file
            builderStage
                .Comment("Copy pyproject.toml to install dependencies")
                .Copy("pyproject.toml", "/app/")
                .EmptyLine()
                .Comment("Install dependencies and generate lock file")
                .Comment("Uses BuildKit cache mount to speed up repeated builds")
                .RunWithMounts(
                    "uv sync --no-install-project --no-dev",
                    "type=cache,target=/root/.cache/uv")
                .EmptyLine()
                .Comment("Copy the rest of the application source and install the project")
                .Copy(".", "/app")
                .RunWithMounts(
                    "uv sync --no-dev",
                    "type=cache,target=/root/.cache/uv");
        }

        var logger = context.Services.GetService<ILogger<PythonAppResource>>();
        context.Builder.AddContainerFilesStages(context.Resource, logger);

        var runtimeBuilder = context.Builder
            .From(runtimeImage, "app")
            .EmptyLine()
            .AddContainerFiles(context.Resource, "/app", logger)
            .Comment("------------------------------")
            .Comment("🚀 Runtime stage")
            .Comment("------------------------------")
            .Comment("Create non-root user for security")
            .Run("groupadd --system --gid 999 appuser && useradd --system --gid 999 --uid 999 --create-home appuser")
            .EmptyLine()
            .Comment("Copy the application and virtual environment from builder")
            .CopyFrom(builderStage.StageName!, "/app", "/app", "appuser:appuser")
            .EmptyLine()
            .Comment("Add virtual environment to PATH and set VIRTUAL_ENV")
            .Env("PATH", "/app/.venv/bin:${PATH}")
            .Env("VIRTUAL_ENV", "/app/.venv")
            .Env("PYTHONDONTWRITEBYTECODE", "1")
            .Env("PYTHONUNBUFFERED", "1")
            .EmptyLine()
            .Comment("Use the non-root user to run the application")
            .User("appuser")
            .EmptyLine()
            .Comment("Set working directory")
            .WorkDir("/app")
            .EmptyLine()
            .Comment("Run the application");

        // Set the appropriate entrypoint and command based on entrypoint type
        switch (entrypointType)
        {
            case EntrypointType.Script:
                runtimeBuilder.Entrypoint(["python", entrypoint]);
                break;
            case EntrypointType.Module:
                runtimeBuilder.Entrypoint(["python", "-m", entrypoint]);
                break;
            case EntrypointType.Executable:
                runtimeBuilder.Entrypoint([entrypoint]);
                break;
        }
    }

    private static void GenerateFallbackDockerfile(DockerfileBuilderCallbackContext context, PythonAppResource resource,
        string pythonVersion, EntrypointType entrypointType, string entrypoint)
    {
        // Use the same runtime image as UV workflow for consistency
        context.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
        var runtimeImage = baseImageAnnotation?.RuntimeImage ?? $"python:{pythonVersion}-slim-bookworm";

        // Check if requirements.txt or pyproject.toml exists
        var requirementsTxtPath = Path.Combine(resource.WorkingDirectory, "requirements.txt");
        var hasRequirementsTxt = File.Exists(requirementsTxtPath);

        var logger = context.Services.GetService<ILogger<PythonAppResource>>();
        context.Builder.AddContainerFilesStages(context.Resource, logger);

        var stage = context.Builder
            .From(runtimeImage)
            .EmptyLine()
            .AddContainerFiles(context.Resource, "/app", logger)
            .Comment("------------------------------")
            .Comment("🚀 Python Application")
            .Comment("------------------------------")
            .Comment("Create non-root user for security")
            .Run("groupadd --system --gid 999 appuser && useradd --system --gid 999 --uid 999 --create-home appuser")
            .EmptyLine()
            .Comment("Set working directory")
            .WorkDir("/app")
            .EmptyLine();

        if (hasRequirementsTxt)
        {
            // Copy requirements.txt first for better layer caching
            stage
                .Comment("Copy requirements.txt for dependency installation")
                .Copy("requirements.txt", "/app/requirements.txt")
                .EmptyLine()
                .Comment("Install dependencies using pip")
                .Run(
                """
                apt-get update \
                  && apt-get install -y --no-install-recommends build-essential \
                  && pip install --no-cache-dir -r requirements.txt \
                  && apt-get purge -y --auto-remove build-essential \
                  && rm -rf /var/lib/apt/lists/*
                """)
                .EmptyLine();
        }
        else
        {
            var pyprojectTomlPath = Path.Combine(resource.WorkingDirectory, "pyproject.toml");
            var hasPyprojectToml = File.Exists(pyprojectTomlPath);

            if (hasPyprojectToml)
            {
                // Copy pyproject.toml first for better layer caching
                stage
                    .Comment("Copy pyproject.toml for dependency installation")
                    .Copy("pyproject.toml", "/app/pyproject.toml")
                    .EmptyLine()
                    .Comment("Install dependencies using pip")
                    .Run(
                    """
                apt-get update \
                  && apt-get install -y --no-install-recommends build-essential \
                  && pip install --no-cache-dir . \
                  && apt-get purge -y --auto-remove build-essential \
                  && rm -rf /var/lib/apt/lists/*
                """)
                    .EmptyLine();
            }
        }

        // Copy the rest of the application
        stage
            .Comment("Copy application files")
            .Copy(".", "/app", "appuser:appuser")
            .EmptyLine()
            .Comment("Set environment variables")
            .Env("PYTHONDONTWRITEBYTECODE", "1")
            .Env("PYTHONUNBUFFERED", "1")
            .EmptyLine()
            .Comment("Use the non-root user to run the application")
            .User("appuser")
            .EmptyLine()
            .Comment("Run the application");

        // Set the appropriate entrypoint based on entrypoint type
        switch (entrypointType)
        {
            case EntrypointType.Script:
                stage.Entrypoint(["python", entrypoint]);
                break;
            case EntrypointType.Module:
                stage.Entrypoint(["python", "-m", entrypoint]);
                break;
            case EntrypointType.Executable:
                stage.Entrypoint([entrypoint]);
                break;
        }
    }

    private static void ThrowIfNullOrContainsIsNullOrEmpty(string[] scriptArgs)
    {
        ArgumentNullException.ThrowIfNull(scriptArgs);
        foreach (var scriptArg in scriptArgs)
        {
            if (string.IsNullOrEmpty(scriptArg))
            {
                var values = string.Join(", ", scriptArgs);
                if (scriptArg is null)
                {
                    throw new ArgumentNullException(nameof(scriptArgs), $"Array params contains null item: [{values}]");
                }
                throw new ArgumentException($"Array params contains empty item: [{values}]", nameof(scriptArgs));
            }
        }
    }

    /// <summary>
    /// Resolves the default virtual environment path by checking multiple candidate locations.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="appDirectory">The Python app directory (relative to AppHost).</param>
    /// <param name="virtualEnvironmentPath">The relative virtual environment path (e.g., ".venv").</param>
    /// <returns>The resolved virtual environment path.</returns>
    private static string ResolveDefaultVirtualEnvironmentPath(IDistributedApplicationBuilder builder, string appDirectory, string virtualEnvironmentPath)
    {
        var appDirectoryFullPath = Path.GetFullPath(appDirectory, builder.AppHostDirectory);

        // Walk up from the Python app directory looking for the virtual environment
        // Stop at the AppHost's parent directory to avoid picking up unrelated venvs
        var appHostParentDirectory = Path.GetDirectoryName(builder.AppHostDirectory);

        // Check if the app directory is under the AppHost's parent directory
        // If not, only look in the app directory itself
        if (appHostParentDirectory != null)
        {
            var relativePath = Path.GetRelativePath(appHostParentDirectory, appDirectoryFullPath);
            var isUnderAppHostParent = !relativePath.StartsWith("..", StringComparison.Ordinal) &&
                                        !Path.IsPathRooted(relativePath);

            if (!isUnderAppHostParent)
            {
                // App is not under AppHost's parent, only use the app directory
                return Path.Combine(appDirectoryFullPath, virtualEnvironmentPath);
            }
        }

        var currentDirectory = appDirectoryFullPath;

        while (currentDirectory != null)
        {
            var venvPath = Path.Combine(currentDirectory, virtualEnvironmentPath);
            if (Directory.Exists(venvPath))
            {
                return venvPath;
            }

            // Stop if we've reached the AppHost's parent directory
            // Use case-insensitive comparison on Windows, case-sensitive on Unix
            var reachedBoundary = OperatingSystem.IsWindows()
                ? string.Equals(currentDirectory, appHostParentDirectory, StringComparison.OrdinalIgnoreCase)
                : string.Equals(currentDirectory, appHostParentDirectory, StringComparison.Ordinal);

            if (reachedBoundary)
            {
                break;
            }

            // Move up to the parent directory
            var parentDirectory = Path.GetDirectoryName(currentDirectory);

            // Stop if we can't go up anymore or if we've gone beyond the AppHost's parent
            if (parentDirectory == null || parentDirectory == currentDirectory)
            {
                break;
            }

            currentDirectory = parentDirectory;
        }

        // Default: Return app directory path (for cases where the venv will be created later)
        return Path.Combine(appDirectoryFullPath, virtualEnvironmentPath);
    }

    /// <summary>
    /// Configures a custom virtual environment path for the Python application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="virtualEnvironmentPath">
    /// The path to the virtual environment. Can be absolute or relative to the app directory.
    /// When relative, it is resolved from the working directory of the Python application.
    /// Common values include ".venv", "venv", or "myenv".
    /// </param>
    /// <param name="createIfNotExists">
    /// Whether to automatically create the virtual environment if it doesn't exist. Defaults to <c>true</c>.
    /// Set to <c>false</c> to disable automatic venv creation (the venv must already exist).
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method updates the Python executable path to use the specified virtual environment.
    /// </para>
    /// <para>
    /// By default (<paramref name="createIfNotExists"/> = <c>true</c>), if the virtual environment doesn't exist,
    /// it will be automatically created before running the application (when using pip package manager, not uv).
    /// Set <paramref name="createIfNotExists"/> to <c>false</c> to disable this behavior and require the venv to already exist.
    /// </para>
    /// <para>
    /// Virtual environments allow Python applications to have isolated dependencies separate from
    /// the system Python installation. This is the recommended approach for Python applications.
    /// </para>
    /// <para>
    /// When you explicitly specify a virtual environment path using this method, the path is used verbatim.
    /// The automatic multi-location lookup (checking both app and AppHost directories) only applies when
    /// using the default ".venv" path during initial app creation via AddPythonScript, AddPythonModule, or AddPythonExecutable.
    /// </para>
    /// </remarks>
    /// <example>
    /// Configure a Python app to use a custom virtual environment:
    /// <code lang="csharp">
    /// var python = builder.AddPythonApp("api", "../python-api", "main.py")
    ///     .WithVirtualEnvironment("myenv");
    ///
    /// // Disable automatic venv creation (require venv to exist)
    /// var python2 = builder.AddPythonApp("api2", "../python-api2", "main.py")
    ///     .WithVirtualEnvironment("myenv", createIfNotExists: false);
    /// </code>
    /// </example>
    [AspireExport("withVirtualEnvironment", Description = "Configures the virtual environment for a Python application")]
    public static IResourceBuilder<T> WithVirtualEnvironment<T>(
        this IResourceBuilder<T> builder, string virtualEnvironmentPath, bool createIfNotExists = true) where T : PythonAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(virtualEnvironmentPath);

        // Use the provided path verbatim - resolve relative paths against the app working directory
        var resolvedPath = Path.IsPathRooted(virtualEnvironmentPath)
            ? virtualEnvironmentPath
            : Path.GetFullPath(virtualEnvironmentPath, builder.Resource.WorkingDirectory);

        var virtualEnvironment = new VirtualEnvironment(resolvedPath);

        // Get the entrypoint annotation to determine how to update the command
        if (!builder.Resource.TryGetLastAnnotation<PythonEntrypointAnnotation>(out var entrypointAnnotation))
        {
            throw new InvalidOperationException("Cannot update virtual environment: Python entrypoint annotation not found.");
        }

        // Update the command based on entrypoint type
        string command = entrypointAnnotation.Type switch
        {
            EntrypointType.Executable => virtualEnvironment.GetExecutable(entrypointAnnotation.Entrypoint),
            EntrypointType.Script or EntrypointType.Module => virtualEnvironment.GetExecutable("python"),
            _ => throw new InvalidOperationException($"Unsupported entrypoint type: {entrypointAnnotation.Type}")
        };

        builder.WithCommand(command);
        builder.WithPythonEnvironment(env =>
        {
            env.VirtualEnvironment = virtualEnvironment;
            env.CreateVenvIfNotExists = createIfNotExists;
        });

        // If createIfNotExists is false, remove venv creator
        if (!createIfNotExists)
        {
            RemoveVenvCreator(builder);
        }

        return builder;
    }

    /// <summary>
    /// Enables debugging support for the Python application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method adds the <see cref="PythonExecutableDebuggableAnnotation"/> to the resource, which enables
    /// debugging support. The debugging configuration is automatically set up based on the
    /// entrypoint type (Script, Module, or Executable).
    /// </para>
    /// <para>
    /// The debug configuration includes the Python interpreter path from the virtual environment,
    /// the program or module to debug, and appropriate launch settings.
    /// </para>
    /// </remarks>
    [AspireExport("withDebugging", Description = "Enables debugging support for a Python application")]
    public static IResourceBuilder<T> WithDebugging<T>(
        this IResourceBuilder<T> builder) where T : PythonAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Add the annotation that marks this resource as debuggable
        builder.WithAnnotation(new PythonExecutableDebuggableAnnotation());

        // Get the entrypoint annotation to determine how to configure debugging
        if (!builder.Resource.TryGetLastAnnotation<PythonEntrypointAnnotation>(out var entrypointAnnotation))
        {
            throw new InvalidOperationException("Cannot configure debugging: Python entrypoint annotation not found.");
        }

        var entrypointType = entrypointAnnotation.Type;
        var entrypoint = entrypointAnnotation.Entrypoint;

        string programPath;
        string module;

        if (entrypointType == EntrypointType.Script)
        {
            programPath = Path.GetFullPath(entrypoint, builder.Resource.WorkingDirectory);
            module = string.Empty;
        }
        else
        {
            programPath = builder.Resource.WorkingDirectory;
            module = entrypoint;
        }

        builder.WithDebugSupport(
            mode =>
            {
                string interpreterPath;
                if (!builder.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var annotation) || annotation.VirtualEnvironment is null)
                {
                    interpreterPath = string.Empty;
                }
                else
                {
                    var venvPath = Path.IsPathRooted(annotation.VirtualEnvironment.VirtualEnvironmentPath)
                        ? annotation.VirtualEnvironment.VirtualEnvironmentPath
                        : Path.GetFullPath(annotation.VirtualEnvironment.VirtualEnvironmentPath, builder.Resource.WorkingDirectory);

                    if (OperatingSystem.IsWindows())
                    {
                        interpreterPath = Path.Join(venvPath, "Scripts", "python.exe");
                    }
                    else
                    {
                        interpreterPath = Path.Join(venvPath, "bin", "python");
                    }
                }

                return new PythonLaunchConfiguration
                {
                    ProgramPath = programPath,
                    Module = module,
                    Mode = mode,
                    InterpreterPath = interpreterPath
                };
            },
            "python",
            static ctx =>
            {
                // Remove entrypoint-specific arguments that VS Code will handle.
                // We need to verify the annotation to ensure we remove the correct args.
                if (!ctx.Resource.TryGetLastAnnotation<PythonEntrypointAnnotation>(out var annotation))
                {
                    return;
                }

                // For Module type: remove "-m" and module name (2 args)
                if (annotation.Type == EntrypointType.Module)
                {
                    if (ctx.Args is [string arg0, string arg1, ..] &&
                        arg0 == "-m" &&
                        arg1 == annotation.Entrypoint)
                    {
                        ctx.Args.RemoveAt(0); // Remove "-m"
                        ctx.Args.RemoveAt(0); // Remove module name
                    }
                }
                // For Script type: remove script path (1 arg)
                else if (annotation.Type == EntrypointType.Script)
                {
                    if (ctx.Args is [string arg0, ..] &&
                        arg0 == annotation.Entrypoint)
                    {
                        ctx.Args.RemoveAt(0); // Remove script path
                    }
                }
            });

        return builder;
    }

    /// <summary>
    /// Configures the entrypoint for the Python application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="entrypointType">The type of entrypoint (Script, Module, or Executable).</param>
    /// <param name="entrypoint">The entrypoint value (script path, module name, or executable name).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method allows you to change the entrypoint configuration of a Python application after it has been created.
    /// The command and arguments will be updated based on the specified entrypoint type:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Script</b>: Runs as <c>python &lt;scriptPath&gt;</c></description></item>
    /// <item><description><b>Module</b>: Runs as <c>python -m &lt;moduleName&gt;</c></description></item>
    /// <item><description><b>Executable</b>: Runs the executable directly from the virtual environment</description></item>
    /// </list>
    /// <para>
    /// <b>Important:</b> This method resets all command-line arguments. If you need to add arguments after changing
    /// the entrypoint, call <c>WithArgs</c> after this method.
    /// </para>
    /// </remarks>
    /// <example>
    /// Change a Python app from running a script to running a module:
    /// <code lang="csharp">
    /// var python = builder.AddPythonScript("api", "../python-api", "main.py")
    ///     .WithEntrypoint(EntrypointType.Module, "uvicorn")
    ///     .WithArgs("main:app", "--reload");
    /// </code>
    /// </example>
    [AspireExport("withEntrypoint", Description = "Configures the entrypoint for a Python application")]
    public static IResourceBuilder<T> WithEntrypoint<T>(
        this IResourceBuilder<T> builder, EntrypointType entrypointType, string entrypoint) where T : PythonAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(entrypoint);

        // Get or create the virtual environment from the annotation
        if (!builder.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var pythonEnv) ||
            pythonEnv.VirtualEnvironment is null)
        {
            throw new InvalidOperationException("Cannot set entrypoint: Python environment annotation with virtual environment not found.");
        }

        var virtualEnvironment = pythonEnv.VirtualEnvironment;

        // Determine the new command based on entrypoint type
        var command = entrypointType switch
        {
            EntrypointType.Executable => virtualEnvironment.GetExecutable(entrypoint),
            EntrypointType.Script or EntrypointType.Module => virtualEnvironment.GetExecutable("python"),
            _ => throw new ArgumentOutOfRangeException(nameof(entrypointType), entrypointType, "Invalid entrypoint type.")
        };

        // Update the command inline
        builder.WithCommand(command);
        builder.WithAnnotation(new PythonEntrypointAnnotation
        {
            Type = entrypointType,
            Entrypoint = entrypoint
        },
        ResourceAnnotationMutationBehavior.Replace);

        builder.WithArgs(static context =>
        {
            if (!context.Resource.TryGetLastAnnotation<PythonEntrypointAnnotation>(out var existingAnnotation))
            {
                return;
            }

            // Clear existing args since we're replacing the entrypoint
            context.Args.Clear();

            var entrypointType = existingAnnotation.Type;
            var entrypoint = existingAnnotation.Entrypoint;

            // Add entrypoint-specific arguments
            switch (entrypointType)
            {
                case EntrypointType.Module:
                    context.Args.Add("-m");
                    context.Args.Add(entrypoint);
                    break;
                case EntrypointType.Script:
                    context.Args.Add(entrypoint);
                    break;
                case EntrypointType.Executable:
                    // Executable runs directly, no additional args needed for entrypoint
                    break;
            }
        });

        return builder;
    }

    /// <summary>
    /// Configures the Python resource to use pip as the package manager and optionally installs packages before the application starts.
    /// </summary>
    /// <typeparam name="T">The type of the Python application resource, must derive from <see cref="PythonAppResource"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="install">When true (default), automatically installs packages before the application starts. When false, only sets the package manager annotation without creating an installer resource.</param>
    /// <param name="installArgs">The command-line arguments passed to pip install command.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method creates a child resource that runs <c>pip install</c> in the working directory of the Python application.
    /// The Python application will wait for this resource to complete successfully before starting.
    /// </para>
    /// <para>
    /// Pip will automatically detect and use either pyproject.toml or requirements.txt based on which file exists in the application directory.
    /// If pyproject.toml exists, pip will use it. Otherwise, if requirements.txt exists, pip will use that.
    /// Calling this method will replace any previously configured package manager (such as uv).
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Python app with automatic pip package installation:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var python = builder.AddPythonScript("api", "../python-api", "main.py")
    ///     .WithPip()  // Automatically installs from pyproject.toml or requirements.txt
    ///     .WithHttpEndpoint(port: 5000);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    [AspireExport("withPip", Description = "Configures pip package installation for a Python application")]
    public static IResourceBuilder<T> WithPip<T>(this IResourceBuilder<T> builder, bool install = true, string[]? installArgs = null)
        where T : PythonAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Ensure virtual environment exists - create default .venv if not configured
        if (!builder.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var pythonEnv) ||
            pythonEnv.VirtualEnvironment is null)
        {
            // Create default virtual environment if none exists
            builder.WithVirtualEnvironment(".venv");
            pythonEnv = builder.Resource.Annotations.OfType<PythonEnvironmentAnnotation>().Last();
        }

        var virtualEnvironment = pythonEnv.VirtualEnvironment!;

        // Determine install command based on which file exists
        // Pip supports both pyproject.toml and requirements.txt
        var workingDirectory = builder.Resource.WorkingDirectory;
        string[] baseInstallArgs;

        if (File.Exists(Path.Combine(workingDirectory, "pyproject.toml")))
        {
            // Use pip install with pyproject.toml (pip will read from pyproject.toml automatically)
            baseInstallArgs = ["install", "."];
        }
        else if (File.Exists(Path.Combine(workingDirectory, "requirements.txt")))
        {
            // Use pip install with requirements.txt
            baseInstallArgs = ["install", "-r", "requirements.txt"];
        }
        else
        {
            // Default to requirements.txt even if it doesn't exist (will fail at runtime if no file is present)
            baseInstallArgs = ["install", "-r", "requirements.txt"];
        }

        builder
            .WithAnnotation(new PythonPackageManagerAnnotation(virtualEnvironment.GetExecutable("pip")), ResourceAnnotationMutationBehavior.Replace)
            .WithAnnotation(new PythonInstallCommandAnnotation([.. baseInstallArgs, .. installArgs ?? []]), ResourceAnnotationMutationBehavior.Replace);

        AddInstaller(builder, install);

        // Create venv creator if needed (will check if venv exists)
        CreateVenvCreatorIfNeeded(builder);

        return builder;
    }

    /// <summary>
    /// Adds a UV environment setup task to ensure the virtual environment exists before running the Python application.
    /// </summary>
    /// <typeparam name="T">The type of the Python application resource, must derive from <see cref="PythonAppResource"/>.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="install">When true (default), automatically runs uv sync before the application starts. When false, only sets the package manager annotation without creating an installer resource.</param>
    /// <param name="args">Optional custom arguments to pass to the uv command. If not provided, defaults to ["sync"].</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for method chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method creates a child resource that runs <c>uv sync</c> in the working directory of the Python application.
    /// The Python application will wait for this resource to complete successfully before starting.
    /// </para>
    /// <para>
    /// UV (https://github.com/astral-sh/uv) is a modern Python package manager written in Rust that can manage virtual environments
    /// and dependencies with significantly faster performance than traditional tools. The <c>uv sync</c> command ensures that the virtual
    /// environment exists, Python is installed if needed, and all dependencies specified in pyproject.toml are installed and synchronized.
    /// </para>
    /// <para>
    /// Calling this method will replace any previously configured package manager (such as pip).
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Python app with automatic UV environment setup:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var python = builder.AddPythonScript("api", "../python-api", "main.py")
    ///     .WithUv()  // Automatically runs 'uv sync' before starting the app
    ///     .WithHttpEndpoint(port: 5000);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <example>
    /// Add a Python app with custom UV arguments:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var python = builder.AddPythonScript("api", "../python-api", "main.py")
    ///     .WithUv(args: ["sync", "--python", "3.12", "--no-dev"])  // Custom uv sync args
    ///     .WithHttpEndpoint(port: 5000);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    [AspireExport("withUv", Description = "Configures uv package management for a Python application")]
    public static IResourceBuilder<T> WithUv<T>(this IResourceBuilder<T> builder, bool install = true, string[]? args = null)
        where T : PythonAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Default args: sync only (uv will auto-detect Python and dependencies from pyproject.toml)
        args ??= ["sync"];

        builder
            .WithAnnotation(new PythonPackageManagerAnnotation("uv"), ResourceAnnotationMutationBehavior.Replace)
            .WithAnnotation(new PythonInstallCommandAnnotation(args), ResourceAnnotationMutationBehavior.Replace);

        AddInstaller(builder, install);

        // UV handles venv creation, so remove any existing venv creator
        RemoveVenvCreator(builder);

        return builder;
    }

    private static bool IsPythonCommandAvailable(string command)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            // On Windows, try both .exe and .cmd extensions
            foreach (var ext in new[] { ".exe", ".cmd" })
            {
                var commandWithExt = command + ext;
                foreach (var directory in pathVariable.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(directory, commandWithExt);
                    if (File.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
        }
        else
        {
            // On Unix-like systems, no extension needed
            foreach (var directory in pathVariable.Split(Path.PathSeparator))
            {
                var fullPath = Path.Combine(directory, command);
                if (File.Exists(fullPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddInstaller<T>(IResourceBuilder<T> builder, bool install) where T : PythonAppResource
    {
        // Only install packages if in run mode
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // Check if the installer resource already exists
            var installerName = $"{builder.Resource.Name}-installer";
            builder.ApplicationBuilder.TryCreateResourceBuilder<PythonInstallerResource>(installerName, out var existingResource);

            if (existingResource is not null)
            {
                // Installer already exists, update its configuration based on install parameter
                if (!install)
                {
                    // Add WithExplicitStart to the existing installer when install is false
                    existingResource.WithExplicitStart();
                }
                // Note: Wait relationships are managed dynamically by SetupDependencies during BeforeStartEvent.
                // No need to remove wait annotations here - SetupDependencies checks for ExplicitStartupAnnotation
                // and skips creating wait relationships when install=false.
                return;
            }

            var installer = new PythonInstallerResource(installerName, builder.Resource);
            var installerBuilder = builder.ApplicationBuilder.AddResource(installer)
                .WithParentRelationship(builder.Resource)
                .ExcludeFromManifest()
                .WithCertificateTrustScope(CertificateTrustScope.None);

            if (!install)
            {
                // Add WithExplicitStart when install is false
                // Note: Wait relationships are managed by SetupDependencies, which checks for ExplicitStartupAnnotation
                installerBuilder.WithExplicitStart();
            }

            builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
            {
                // Set the installer's working directory to match the resource's working directory
                // and set the install command and args based on the resource's annotations
                if (!builder.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var packageManager) ||
                    !builder.Resource.TryGetLastAnnotation<PythonInstallCommandAnnotation>(out var installCommand))
                {
                    // No package manager configured - don't fail, just don't run the installer
                    // This allows venv to be created without requiring a package manager
                    return Task.CompletedTask;
                }

                installerBuilder
                    .WithCommand(packageManager.ExecutableName)
                    .WithWorkingDirectory(builder.Resource.WorkingDirectory)
                    .WithArgs(installCommand.Args);

                // Add required command validation based on the package manager
                if (packageManager.ExecutableName == "uv")
                {
                    installerBuilder.WithRequiredCommand("uv", "https://docs.astral.sh/uv/getting-started/installation/");
                }
                // For other package managers (pip, etc.), Python validation happens via PythonVenvCreatorResource

                return Task.CompletedTask;
            });

            builder.WithAnnotation(new PythonPackageInstallerAnnotation(installer));
        }
    }

    private static void CreateVenvCreatorIfNeeded<T>(IResourceBuilder<T> builder) where T : PythonAppResource
    {
        // Check if we should create a venv
        if (!ShouldCreateVenv(builder))
        {
            return;
        }

        if (!builder.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var pythonEnv) ||
            pythonEnv.VirtualEnvironment == null)
        {
            return;
        }

        var venvPath = Path.IsPathRooted(pythonEnv.VirtualEnvironment.VirtualEnvironmentPath)
            ? pythonEnv.VirtualEnvironment.VirtualEnvironmentPath
            : Path.GetFullPath(pythonEnv.VirtualEnvironment.VirtualEnvironmentPath, builder.Resource.WorkingDirectory);

        // Create venv creator as a child resource
        var venvCreatorName = $"{builder.Resource.Name}-venv-creator";

        // Use TryCreateResourceBuilder to check if it already exists
        if (builder.ApplicationBuilder.TryCreateResourceBuilder<PythonVenvCreatorResource>(venvCreatorName, out _))
        {
            // Venv creator already exists, no need to create again
            return;
        }

        // Create new venv creator resource
        var venvCreator = new PythonVenvCreatorResource(venvCreatorName, builder.Resource, venvPath);

        // Determine which Python command to use
        string pythonCommand;
        if (OperatingSystem.IsWindows())
        {
            // On Windows, try py launcher first, then python
            pythonCommand = IsPythonCommandAvailable("py") ? "py" : "python";
        }
        else
        {
            // On Unix-like systems, try python3 first (more explicit), then python
            pythonCommand = IsPythonCommandAvailable("python3") ? "python3" : "python";
        }

        builder.ApplicationBuilder.AddResource(venvCreator)
            .WithCommand(pythonCommand)
            .WithArgs(["-m", "venv", venvPath])
            .WithWorkingDirectory(builder.Resource.WorkingDirectory)
            .WithParentRelationship(builder.Resource)
            .ExcludeFromManifest()
            .WithRequiredCommand(pythonCommand, "https://www.python.org/downloads/");

        // Wait relationships will be set up dynamically in SetupDependencies
    }

    private static void RemoveVenvCreator<T>(IResourceBuilder<T> builder) where T : PythonAppResource
    {
        var venvCreatorName = $"{builder.Resource.Name}-venv-creator";

        // Use TryCreateResourceBuilder to check if venv creator exists
        if (builder.ApplicationBuilder.TryCreateResourceBuilder<PythonVenvCreatorResource>(venvCreatorName, out var venvCreatorBuilder))
        {
            builder.ApplicationBuilder.Resources.Remove(venvCreatorBuilder.Resource);
            // Wait relationships are managed dynamically in SetupDependencies, so no need to clean them up here
        }
    }

    private static void SetupDependencies(IDistributedApplicationBuilder builder, PythonAppResource resource)
    {
        // This method is called in BeforeStartEvent to dynamically set up dependencies
        // based on which child resources actually exist after all method calls have been made

        var venvCreatorName = $"{resource.Name}-venv-creator";
        var installerName = $"{resource.Name}-installer";

        // Try to get the venv creator and installer resources
        builder.TryCreateResourceBuilder<PythonVenvCreatorResource>(venvCreatorName, out var venvCreatorBuilder);
        builder.TryCreateResourceBuilder<PythonInstallerResource>(installerName, out var installerBuilder);

        // Get the Python app resource builder
        builder.TryCreateResourceBuilder<PythonAppResource>(resource.Name, out var appBuilder);

        if (appBuilder == null)
        {
            return; // Resource doesn't exist, nothing to set up
        }

        // Check if installer has explicit start annotation (install=false was used)
        var shouldSkipInstallerWait = installerBuilder?.Resource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _) ?? false;

        // Set up wait dependencies based on what exists:
        // 1. If both venv creator and installer exist (and installer doesn't have explicit start): installer waits for venv creator, app waits for installer
        // 2. If both exist but installer has explicit start: app waits for venv creator only
        // 3. If only installer exists (without explicit start): app waits for installer
        // 4. If only venv creator exists: app waits for venv creator (no installer needed)
        // 5. If neither exists or installer has explicit start: app runs directly (no waits needed)

        if (venvCreatorBuilder != null && installerBuilder != null)
        {
            if (!shouldSkipInstallerWait)
            {
                // Both exist and installer should run automatically: installer waits for venv, app waits for installer
                installerBuilder.WaitForCompletion(venvCreatorBuilder);
                appBuilder.WaitForCompletion(installerBuilder);
            }
            else
            {
                // Installer has explicit start, so only app waits for venv creator
                appBuilder.WaitForCompletion(venvCreatorBuilder);
            }
        }
        else if (installerBuilder != null && !shouldSkipInstallerWait)
        {
            // Only installer exists (without explicit start): app waits for installer
            appBuilder.WaitForCompletion(installerBuilder);
        }
        else if (venvCreatorBuilder != null)
        {
            // Only venv creator exists: app waits for venv creator
            appBuilder.WaitForCompletion(venvCreatorBuilder);
        }
        // If neither exists or installer has explicit start, no wait relationships needed for the app
    }

    private static bool ShouldCreateVenv<T>(IResourceBuilder<T> builder) where T : PythonAppResource
    {
        // Check if we're using uv (which handles venv creation itself)
        var isUsingUv = builder.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var pkgMgr) &&
                        pkgMgr.ExecutableName == "uv";

        if (isUsingUv)
        {
            // UV handles venv creation, we don't need to create it
            return false;
        }

        // Get the virtual environment path
        if (!builder.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var pythonEnv) ||
            pythonEnv.VirtualEnvironment == null)
        {
            return false;
        }

        // Check if automatic venv creation is disabled
        if (!pythonEnv.CreateVenvIfNotExists)
        {
            return false;
        }

        var venvPath = Path.IsPathRooted(pythonEnv.VirtualEnvironment.VirtualEnvironmentPath)
            ? pythonEnv.VirtualEnvironment.VirtualEnvironmentPath
            : Path.GetFullPath(pythonEnv.VirtualEnvironment.VirtualEnvironmentPath, builder.Resource.WorkingDirectory);

        // Check if venv directory exists (simple check, don't verify validity)
        if (Directory.Exists(venvPath))
        {
            return false;
        }

        return true;
    }

    internal static IResourceBuilder<PythonAppResource> WithPythonEnvironment(this IResourceBuilder<PythonAppResource> builder, Action<PythonEnvironmentAnnotation> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (!builder.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var existing))
        {
            existing = new PythonEnvironmentAnnotation();
            builder.WithAnnotation(existing);
        }

        configure(existing);

        return builder;
    }
}
