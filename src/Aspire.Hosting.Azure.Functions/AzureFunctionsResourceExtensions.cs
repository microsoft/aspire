// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001 // The stable Functions factory uses experimental integration-authoring infrastructure.
#pragma warning disable ASPIREEXTENSION001 // Functions owns its IDE launch contract.

using System.Reflection;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

public static partial class AzureFunctionsProjectResourceExtensions
{
    internal const string FunctionsExternalBuildLaunchConfigurationType = "azure-functions-with-external-build.v1";
    private const string FunctionsLaunchConfigurationType = "azure-functions";

    /// <summary>
    /// Adds an SDK-style Azure Functions project to the distributed application.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name used for service discovery.</param>
    /// <param name="projectPath">The project file or directory, relative to the AppHost directory unless absolute.</param>
    /// <returns>The Azure Functions resource builder for additional configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/>, <paramref name="name"/>, or <paramref name="projectPath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="projectPath"/> is empty.</exception>
    /// <exception cref="DistributedApplicationException">The path is a file-based C# app rather than a project.</exception>
    /// <remarks>
    /// <para>
    /// Aspire builds the project and starts Azure Functions Core Tools using the project's SDK-selected
    /// invocation. C# isolated-worker projects are the validated workload. SDK-style project path resolution
    /// is shared with <c>AddDotnetProject</c>; directories must contain exactly one supported project.
    /// File-based C# apps are not supported.
    /// </para>
    /// <para>
    /// An implicit Azure Storage resource is shared by Functions apps for host bookkeeping.
    /// Use <c>WithHostStorage</c> to select a different storage resource. Functions-specific
    /// <c>WithReference</c> calls configure both trigger bindings and Aspire client integrations.
    /// SDK container publishing is enabled, and Azure Container Apps uses the <c>functionapp</c> kind.
    /// </para>
    /// <para>
    /// IDEs supporting <c>azure-functions-with-external-build.v1</c> launch Aspire's build output.
    /// Older compatible IDEs build the project themselves and can use different build settings.
    /// Aspire warns at startup and waits for other coordinated initial builds before that fallback launch.
    /// Updating the IDE integration or running outside an IDE uses Aspire-owned initial builds.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a C# isolated-worker Functions project:
    /// <code lang="csharp">
    /// var storage = builder.AddAzureStorage("storage").RunAsEmulator();
    /// builder.AddAzureFunctions("functions", "../Functions/Functions.csproj")
    ///     .WithBuildEnvironment("BUILD_FLAVOR", "local")
    ///     .WithHostStorage(storage)
    ///     .WithReference(storage.AddQueues("queue"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<AzureFunctionsResource> AddAzureFunctions(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        var fullPath = NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, projectPath));
        if (string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(fullPath))
        {
            throw new DistributedApplicationException("Azure Functions requires an SDK-style project. File-based C# apps are not supported.");
        }

        return builder.AddDotnetProject(
            name,
            projectPath,
            static (resourceName, workingDirectory) => new AzureFunctionsResource(resourceName, workingDirectory),
            FunctionsExternalBuildLaunchConfigurationType,
            functionsBuilder =>
            {
                functionsBuilder.WithDotnetBaseDefaults(new ProjectResourceOptions());

                var storage = builder.GetOrAddDefaultHostStorage();
                functionsBuilder.Resource.HostStorage = storage;
                functionsBuilder
                    .WithIconName("Flash")
                    .WithAnnotation(new AzureFunctionsAnnotation())
                    .WithAnnotation(new AzureFunctionsHostStorageAnnotation(storage), ResourceAnnotationMutationBehavior.Replace)
                    .WithFunctionsEnvironment(configureLegacyDefaults: false)
                    .WithFunctionsHttpEndpoint();

                if (builder.ExecutionContext.IsRunMode)
                {
                    functionsBuilder.WithRequiredCommand("func", AzureFunctionsCoreToolsHelpLink);
                    ConfigureFunctionsDebugSupport(functionsBuilder);
                }
            });
    }

    /// <summary>
    /// Configures the Azure Storage resource used for Functions host bookkeeping.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <param name="storage">The storage resource to use instead of implicit host storage.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="storage"/> is null.</exception>
    [AspireExport("withAzureFunctionsHostStorage", MethodName = "withHostStorage")]
    public static IResourceBuilder<AzureFunctionsResource> WithHostStorage(
        this IResourceBuilder<AzureFunctionsResource> builder,
        IResourceBuilder<AzureStorageResource> storage)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(storage);

        return WithHostStorageCore(builder, storage.Resource);
    }

    /// <summary>
    /// Configures a connection reference, preserving Functions binding settings for supported Azure resources.
    /// </summary>
    /// <param name="destination">The Azure Functions resource builder.</param>
    /// <param name="source">The connection-string resource to reference.</param>
    /// <param name="connectionName">The connection name, or the source resource name when null.</param>
    /// <param name="optional">Whether a missing connection string is allowed for an ordinary reference. Functions binding references do not support optional connections.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="optional"/> is true for a Functions binding reference.</exception>
    /// <remarks>
    /// This overload also handles source builders stored as <see cref="IResourceWithConnectionString"/>.
    /// Resources without Functions-specific settings retain ordinary connection-string reference behavior.
    /// Polyglot AppHosts use the standard <c>withReference</c> dispatcher.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the standard withReference dispatcher, which preserves Functions binding settings.")]
    public static IResourceBuilder<AzureFunctionsResource> WithReference(
        this IResourceBuilder<AzureFunctionsResource> destination,
        IResourceBuilder<IResourceWithConnectionString> source,
        string? connectionName = null,
        bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        return TryWithReference(destination, source, connectionName, optional, name: null)
            ?? ResourceBuilderExtensions.WithReference(destination, source, connectionName, optional);
    }

    /// <summary>
    /// Adds an environment variable used only while building the Functions project.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <param name="name">The build environment variable name.</param>
    /// <param name="value">The build environment variable value.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/>, <paramref name="name"/>, or <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <remarks>
    /// This does not set the host or worker runtime environment. Do not use build environment variables for
    /// secrets; they are carried in IDE launch metadata and can appear in build diagnostics.
    /// </remarks>
    [AspireExport("withAzureFunctionsBuildEnvironment", MethodName = "withBuildEnvironment")]
    public static IResourceBuilder<AzureFunctionsResource> WithBuildEnvironment(
        this IResourceBuilder<AzureFunctionsResource> builder,
        string name,
        string value)
    {
        DotnetProjectHostingExtensions.WithBuildEnvironment(builder, name, value);
        return builder;
    }

    /// <summary>
    /// Adds a callback that configures build-only environment variables for the Functions project.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <param name="callback">The build environment callback.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="callback"/> is null.</exception>
    /// <remarks>Values are not added to the host or worker environment. They are not a secret transport.</remarks>
    [AspireExportIgnore(Reason = "Raw Action callbacks use the .NET build environment context; polyglot AppHosts use the name/value overload.")]
    public static IResourceBuilder<AzureFunctionsResource> WithBuildEnvironment(
        this IResourceBuilder<AzureFunctionsResource> builder,
        Action<EnvironmentCallbackContext> callback)
    {
        DotnetProjectHostingExtensions.WithBuildEnvironment(builder, callback);
        return builder;
    }

    /// <summary>
    /// Adds an asynchronous callback that configures build-only environment variables for the Functions project.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <param name="callback">The asynchronous build environment callback.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="callback"/> is null.</exception>
    /// <remarks>Values are not added to the host or worker environment. They are not a secret transport.</remarks>
    [AspireExportIgnore(Reason = "Raw Func callbacks use the .NET build environment context; polyglot AppHosts use the name/value overload.")]
    public static IResourceBuilder<AzureFunctionsResource> WithBuildEnvironment(
        this IResourceBuilder<AzureFunctionsResource> builder,
        Func<EnvironmentCallbackContext, Task> callback)
    {
        DotnetProjectHostingExtensions.WithBuildEnvironment(builder, callback);
        return builder;
    }

    /// <summary>
    /// Configures the number of Functions host replicas.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <param name="replicas">The number of replicas.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    [AspireExport("withAzureFunctionsReplicas", MethodName = "withReplicas")]
    public static IResourceBuilder<AzureFunctionsResource> WithReplicas(this IResourceBuilder<AzureFunctionsResource> builder, int replicas)
    {
        DotnetProjectHostingExtensions.WithReplicas(builder, replicas);
        return builder;
    }

    /// <summary>
    /// Disables automatic forwarded-header configuration when publishing the Functions project.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    [AspireExport("disableAzureFunctionsForwardedHeaders", MethodName = "disableForwardedHeaders")]
    public static IResourceBuilder<AzureFunctionsResource> DisableForwardedHeaders(this IResourceBuilder<AzureFunctionsResource> builder)
    {
        DotnetProjectHostingExtensions.DisableForwardedHeaders(builder);
        return builder;
    }

    /// <summary>
    /// Configures which endpoints contribute environment variables for the Functions project.
    /// </summary>
    /// <param name="builder">The Azure Functions resource builder.</param>
    /// <param name="filter">The filter selecting endpoints whose environment variables should be injected.</param>
    /// <returns>The same Azure Functions resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="filter"/> is null.</exception>
    /// <remarks>Filters compose: an endpoint is included only when every filter accepts it.</remarks>
    [AspireExportIgnore(Reason = "Uses Func<EndpointAnnotation, bool>; polyglot AppHosts use the endpoint-name dispatcher.")]
    public static IResourceBuilder<AzureFunctionsResource> WithEndpointsInEnvironment(
        this IResourceBuilder<AzureFunctionsResource> builder,
        Func<EndpointAnnotation, bool> filter)
    {
        DotnetProjectHostingExtensions.WithEndpointsInEnvironment(builder, filter);
        return builder;
    }

    /// <summary>
    /// Configures endpoint environment-variable injection for Functions projects in polyglot AppHosts.
    /// </summary>
    [AspireExport("withAzureFunctionsEndpointsInEnvironment", MethodName = "withEndpointsInEnvironment")]
    internal static IResourceBuilder<AzureFunctionsResource> WithFunctionsEndpointsInEnvironmentForPolyglot(
        this IResourceBuilder<AzureFunctionsResource> builder,
        string[] endpointNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpointNames);

        var names = endpointNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return builder.WithEndpointsInEnvironment(endpoint => names.Contains(endpoint.Name));
    }

    internal static IResourceBuilder<AzureFunctionsResource>? TryWithReference(
        IResourceBuilder<AzureFunctionsResource> destination,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (source.Resource is not IResourceWithConnectionString || source.Resource is not IResourceWithAzureFunctionsConfig azureFunctionsConfig)
        {
            return null;
        }

        if (optional)
        {
            throw new InvalidOperationException("Optional references are not supported for Azure Functions resources.");
        }

        if (name is not null)
        {
            throw new InvalidOperationException("Named service references are not supported for Azure Functions resources.");
        }

        return TryWithReferenceCore(destination, source, connectionName, azureFunctionsConfig);
    }

    private static void ConfigureFunctionsDebugSupport(IResourceBuilder<AzureFunctionsResource> builder)
    {
        var applicationBuilder = builder.ApplicationBuilder;
        DebugSessionInfoParser.TryGetSupportedLaunchConfigurations(
            applicationBuilder.Configuration["DEBUG_SESSION_INFO"],
            out var supportedLaunchConfigurations);

        var launchConfigurationType = supportedLaunchConfigurations switch
        {
            { } supported when supported.Contains(FunctionsExternalBuildLaunchConfigurationType) => FunctionsExternalBuildLaunchConfigurationType,
            { } supported when supported.Contains(FunctionsLaunchConfigurationType) => FunctionsLaunchConfigurationType,
            // Visual Studio does not advertise capabilities and only knows projects loaded in its solution.
            null when !string.IsNullOrEmpty(applicationBuilder.Configuration["DEBUG_SESSION_PORT"]) => KnownLaunchConfigurationTypes.Project,
            _ => FunctionsExternalBuildLaunchConfigurationType
        };

        builder.WithDebugSupport(
            mode => CreateFunctionsLaunchConfiguration(builder, mode, launchConfigurationType),
            launchConfigurationType);

        var warningLogged = 0;
        applicationBuilder.OnBeforeStart((context, _) =>
        {
            if (context.Model.Resources.Contains(builder.Resource)
                && builder.Resource.SupportsDebugging(applicationBuilder.Configuration, out var annotation)
                && annotation.LaunchConfigurationType is FunctionsLaunchConfigurationType or KnownLaunchConfigurationTypes.Project
                && Interlocked.Exchange(ref warningLogged, 1) == 0)
            {
                context.Services.GetRequiredService<ILogger<AzureFunctionsResource>>().LogWarning(
                    "Azure Functions resource '{ResourceName}' is using an IDE-built fallback because the IDE does not support '{Capability}'. " +
                    "The IDE may use different build configuration or build environment settings. Upgrade the IDE integration or run outside the IDE to use Aspire-owned builds.",
                    builder.Resource.Name,
                    FunctionsExternalBuildLaunchConfigurationType);
            }

            return Task.CompletedTask;
        });
    }

    private static object CreateFunctionsLaunchConfiguration(
        IResourceBuilder<AzureFunctionsResource> builder,
        string mode,
        string launchConfigurationType)
    {
        var resource = builder.Resource;
        // The shared .NET factory resolves the coordinated build barrier before invoking the producer.
        // Read metadata here, not during registration, so build-only environment values are authoritative.
        var metadata = resource.GetProjectMetadata();
        if (launchConfigurationType == FunctionsLaunchConfigurationType)
        {
            return new AzureFunctionsLaunchConfiguration { Mode = mode, ProjectPath = metadata.ProjectPath };
        }

        var configuration = builder.ApplicationBuilder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (launchConfigurationType == KnownLaunchConfigurationTypes.Project)
        {
            return new ProjectLaunchConfiguration
            {
                Mode = mode,
                ProjectPath = metadata.ProjectPath,
                BuildConfiguration = configuration,
                SuppressBuild = false,
                DisableLaunchProfile = resource.HasAnnotationOfType<ExcludeLaunchProfileAnnotation>(),
                LaunchProfile = resource.GetEffectiveLaunchProfile()?.Name ?? string.Empty
            };
        }

        if (!metadata.SuppressBuild || string.IsNullOrEmpty(metadata.BuildWorkingDirectory))
        {
            throw new InvalidOperationException(
                $"Azure Functions resource '{resource.Name}' requires completed external-build metadata for '{FunctionsExternalBuildLaunchConfigurationType}'.");
        }

        return new AzureFunctionsExternalBuildLaunchConfiguration
        {
            Mode = mode,
            ProjectPath = metadata.ProjectPath,
            BuildConfiguration = configuration,
            BuildEnvironment = metadata.BuildEnvironment.Count == 0 ? null : metadata.BuildEnvironment.ToDictionary(),
            BuildWorkingDirectory = metadata.BuildWorkingDirectory
        };
    }

    private sealed class AzureFunctionsExternalBuildLaunchConfiguration
    {
        [JsonPropertyName("type")]
        public string Type { get; } = FunctionsExternalBuildLaunchConfigurationType;

        [JsonPropertyName("mode")]
        public required string Mode { get; init; }

        [JsonPropertyName("project_path")]
        public required string ProjectPath { get; init; }

        [JsonPropertyName("build_configuration")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BuildConfiguration { get; init; }

        [JsonPropertyName("build_environment")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? BuildEnvironment { get; init; }

        [JsonPropertyName("build_working_directory")]
        public required string BuildWorkingDirectory { get; init; }

        [JsonPropertyName("suppress_build")]
        public bool SuppressBuild { get; } = true;
    }
}
