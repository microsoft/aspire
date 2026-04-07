#pragma warning disable ASPIRECERTIFICATES001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Keycloak;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Keycloak resources to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class KeycloakResourceBuilderExtensions
{
    private const string AdminEnvVarName = "KC_BOOTSTRAP_ADMIN_USERNAME";
    private const string AdminPasswordEnvVarName = "KC_BOOTSTRAP_ADMIN_PASSWORD";
    private const string HealthCheckEnvVarName = "KC_HEALTH_ENABLED"; // As per https://www.keycloak.org/observability/health
    private const string EnabledFeaturesEnvVarName = "KC_FEATURES";
    private const string DisabledFeaturesEnvVarName = "KC_FEATURES_DISABLED";

    private const int DefaultContainerPort = 8080;
    private const int DefaultHttpsPort = 8443;
    private const int ManagementInterfaceContainerPort = 9000; // As per https://www.keycloak.org/server/management-interface

    private const string ManagementEndpointName = "management";
    private const string KeycloakImportDirectory = "/opt/keycloak/data/import";

    /// <summary>
    /// Adds a Keycloak container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. </param>
    /// <param name="port">The host port that the underlying container is bound to when running locally.</param>
    /// <param name="adminUsername">The parameter used as the admin for the Keycloak resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="adminPassword">The parameter used as the admin password for the Keycloak resource. If <see langword="null"/> a default password will be used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The container exposes port 8080 by default.
    /// This version of the package defaults to the <inheritdoc cref="KeycloakContainerImageTags.Tag"/> tag of the <inheritdoc cref="KeycloakContainerImageTags.Registry"/>/<inheritdoc cref="KeycloakContainerImageTags.Image"/> container image.
    /// <example>
    /// Use in application host
    /// <code lang="csharp">
    /// var keycloak = builder.AddKeycloak("keycloak");
    ///
    /// var myService = builder.AddProject&lt;Projects.MyService&lt;()
    ///                        .WithReference(keycloak);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds a Keycloak container resource")]
    public static IResourceBuilder<KeycloakResource> AddKeycloak(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? adminUsername = null,
        IResourceBuilder<ParameterResource>? adminPassword = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = adminPassword?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password");

        var resource = new KeycloakResource(name, adminUsername?.Resource, passwordParameter);

        var keycloak = builder
            .AddResource(resource)
            .WithImage(KeycloakContainerImageTags.Image)
            .WithImageRegistry(KeycloakContainerImageTags.Registry)
            .WithImageTag(KeycloakContainerImageTags.Tag)
            .WithHttpEndpoint(port: port, targetPort: DefaultContainerPort)
            .WithHttpEndpoint(targetPort: ManagementInterfaceContainerPort, name: ManagementEndpointName)
            .WithEndpoint(ManagementEndpointName, e => e.ExcludeReferenceEndpoint = true)
            .WithHttpHealthCheck(endpointName: ManagementEndpointName, path: "/health/ready")
            .WithOtlpExporter()
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables[AdminEnvVarName] = resource.AdminReference;
                context.EnvironmentVariables[AdminPasswordEnvVarName] = resource.AdminPasswordParameter;
                context.EnvironmentVariables[HealthCheckEnvVarName] = "true";
                if (resource.EnabledFeatures.Any())
                {
                    context.EnvironmentVariables[EnabledFeaturesEnvVarName] = string.Join(',', resource.EnabledFeatures);
                }
                if (resource.DisabledFeatures.Any())
                {
                    context.EnvironmentVariables[DisabledFeaturesEnvVarName] = string.Join(',', resource.DisabledFeatures);
                }
            })
            .WithUrlForEndpoint(ManagementEndpointName, u => u.DisplayLocation = UrlDisplayLocation.DetailsOnly)
            .WithHttpsCertificateConfiguration(ctx =>
            {
                if (ctx.Password is null)
                {
                    ctx.EnvironmentVariables["KC_HTTPS_CERTIFICATE_FILE"] = ctx.CertificatePath;
                    ctx.EnvironmentVariables["KC_HTTPS_CERTIFICATE_KEY_FILE"] = ctx.KeyPath;
                }
                else
                {
                    ctx.EnvironmentVariables["KC_HTTPS_KEY_STORE_FILE"] = ctx.PfxPath;
                    ctx.EnvironmentVariables["KC_HTTPS_KEY_STORE_TYPE"] = "pkcs12";
                    ctx.EnvironmentVariables["KC_HTTPS_KEY_STORE_PASSWORD"] = ctx.Password;
                }

                return Task.CompletedTask;
            });

        if (builder.ExecutionContext.IsRunMode)
        {
            keycloak.SubscribeHttpsEndpointsUpdate(ctx =>
            {
                // If a TLS certificate is configured, ensure the keycloak resource has an HTTPS endpoint and
                // configure the environment variables to use it.
                keycloak
                    .WithHttpsEndpoint(targetPort: DefaultHttpsPort, env: "KC_HTTPS_PORT")
                    .WithEndpoint(ManagementEndpointName, ep => ep.UriScheme = "https");
            });
        }

        if (builder.ExecutionContext.IsRunMode)
        {
            keycloak.WithArgs("start-dev");
        }
        else
        {
            keycloak.WithArgs("start");
        }

        keycloak.WithArgs("--import-realm");

        return keycloak;
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Keycloak container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The volume is mounted at /opt/keycloak/data in the container.
    /// <example>
    /// Use a data volume
    /// <code lang="csharp">
    /// var keycloak = builder.AddKeycloak("keycloak")
    ///                       .WithDataVolume();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds a data volume for Keycloak")]
    public static IResourceBuilder<KeycloakResource> WithDataVolume(this IResourceBuilder<KeycloakResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/opt/keycloak/data", false);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Keycloak container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The source directory is mounted at /opt/keycloak/data in the container.
    /// <example>
    /// Use a bind mount
    /// <code lang="csharp">
    /// var keycloak = builder.AddKeycloak("keycloak")
    ///                       .WithDataBindMount("mydata");
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Adds a data bind mount for Keycloak")]
    public static IResourceBuilder<KeycloakResource> WithDataBindMount(this IResourceBuilder<KeycloakResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/opt/keycloak/data", false);
    }

    /// <summary>
    /// Adds a realm import to a Keycloak container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="import">The directory containing the realm import files or a single import file.</param>
    /// <param name="isReadOnly">A flag that indicates if the realm import directory is read-only.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The realm import files are copied to /opt/keycloak/data/import in the container.
    /// <example>
    /// Import the realms from a directory
    /// <code lang="csharp">
    /// var keycloak = builder.AddKeycloak("keycloak")
    ///                       .WithRealmImport("../realms");
    /// </code>
    /// </example>
    /// </remarks>
    [Obsolete("Use WithRealmImport(string import) instead.")]
    public static IResourceBuilder<KeycloakResource> WithRealmImport(
        this IResourceBuilder<KeycloakResource> builder,
        string import,
        bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(import);

        var importFullPath = Path.GetFullPath(import, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithBindMount(importFullPath, KeycloakImportDirectory, isReadOnly);
    }

    /// <summary>
    /// Adds a realm import to a Keycloak container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="import">The directory containing the realm import files or a single import file.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The realm import files are copied to /opt/keycloak/data/import in the container.
    /// <example>
    /// Import the realms from a directory
    /// <code lang="csharp">
    /// var keycloak = builder.AddKeycloak("keycloak")
    ///                       .WithRealmImport("../realms");
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Parameter name 'import' is a reserved keyword in TypeScript. Use the internal ATS-compatible overload instead.")]
    public static IResourceBuilder<KeycloakResource> WithRealmImport(
        this IResourceBuilder<KeycloakResource> builder,
        string import)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(import);

        var importFullPath = Path.GetFullPath(import, builder.ApplicationBuilder.AppHostDirectory);

        return builder.WithContainerFiles(
            KeycloakImportDirectory,
            importFullPath,
            defaultOwner: KeycloakContainerImageTags.ContainerUser);
    }

    /// <summary>
    /// Additional feature names to enable for the keycloak resource
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="features">Names of features to enable for the keycloak resource</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Enables Keycloak features")]
    public static IResourceBuilder<KeycloakResource> WithEnabledFeatures(
        this IResourceBuilder<KeycloakResource> builder,
        params string[] features)
    {
        foreach (var feature in features)
        {
            // Add the feature to the enabled features set (and ensure it isn't in the disabled features set)
            builder.Resource.EnabledFeatures.Add(feature);
            builder.Resource.DisabledFeatures.Remove(feature);
        }

        return builder;
    }

    /// <summary>
    /// Additional feature names to disable for the keycloak resource
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="features">Names of features to disable for the keycloak resource</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Disables Keycloak features")]
    public static IResourceBuilder<KeycloakResource> WithDisabledFeatures(
        this IResourceBuilder<KeycloakResource> builder,
        params string[] features)
    {
        foreach (var feature in features)
        {
            // Add the feature to the disabled features set (and ensure it isn't in the enabled features set)
            builder.Resource.DisabledFeatures.Add(feature);
            builder.Resource.EnabledFeatures.Remove(feature);
        }

        return builder;
    }

    /// <summary>
    /// Injects the appropriate environment variables to allow the resource to enable sending telemetry to the dashboard.
    /// <list type="number">
    ///   <item>It ensures the "opentelemetry" Keycloak feature is enabled</item>
    ///   <item>It sets the OTLP endpoint to the value of the <c>ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL</c> environment variable.</item>
    ///   <item>It sets the service name and instance id to the resource name and UID. Values are injected by the orchestrator.</item>
    ///   <item>It sets a small batch schedule delay in development. This reduces the delay that OTLP exporter waits to sends telemetry and makes the dashboard telemetry pages responsive.</item>
    /// </list>
    /// </summary>
    /// <param name="builder">The keycloak resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{KeycloakResource}"/>.</returns>
    [AspireExport(Description = "Configures the OTLP exporter for Keycloak")]
    public static IResourceBuilder<KeycloakResource> WithOtlpExporter(this IResourceBuilder<KeycloakResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Opentelemetry support requires the opentelemetry feature to be enabled.
        builder.WithEnabledFeatures("opentelemetry");
        OtlpConfigurationExtensions.WithOtlpExporter(builder);

        return builder;
    }

    /// <summary>
    /// Injects the appropriate environment variables to allow the resource to enable sending telemetry to the dashboard.
    /// <list type="number">
    ///   <item>It ensures the "opentelemetry" Keycloak feature is enabled</item>
    ///   <item>It sets the OTLP endpoint to the value of the <c>ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL</c> environment variable.</item>
    ///   <item>It sets the service name and instance id to the resource name and UID. Values are injected by the orchestrator.</item>
    ///   <item>It sets a small batch schedule delay in development. This reduces the delay that OTLP exporter waits to sends telemetry and makes the dashboard telemetry pages responsive.</item>
    /// </list>
    /// </summary>
    /// <param name="builder">The keycloak resource builder.</param>
    /// <param name="protocol">The protocol to use for the OTLP exporter. If not set, it will try gRPC then Http.</param>
    /// <returns>The <see cref="IResourceBuilder{KeycloakResource}"/>.</returns>
    [AspireExport("withOtlpExporterWithProtocol", Description = "Configures the OTLP exporter for Keycloak with a specific protocol")]
    public static IResourceBuilder<KeycloakResource> WithOtlpExporter(this IResourceBuilder<KeycloakResource> builder, OtlpProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Opentelemetry support requires the opentelemetry feature to be enabled.
        builder.WithEnabledFeatures("opentelemetry");
        OtlpConfigurationExtensions.WithOtlpExporter(builder, protocol);

        return builder;
    }

    /// <summary>
    /// Adds a realm import to a Keycloak container resource.
    /// </summary>
    [AspireExport("withRealmImport", Description = "Imports a Keycloak realm configuration")]
    internal static IResourceBuilder<KeycloakResource> WithRealmImportInternal(
        this IResourceBuilder<KeycloakResource> builder,
        string importPath)
    {
        return builder.WithRealmImport(importPath);
    }
}
