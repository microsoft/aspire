// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for <see cref="IDistributedApplicationBuilder"/> to add and configure project resources.
/// </summary>
public static class ProjectResourceBuilderExtensions
{
    private const string AspNetCoreForwardedHeadersEnabledVariableName = "ASPNETCORE_FORWARDEDHEADERS_ENABLED";

    /// <summary>
    /// Adds a .NET project to the application model.
    /// </summary>
    /// <typeparam name="TProject">A type that represents the project reference.</typeparam>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddProject{TProject}(IDistributedApplicationBuilder, string)"/> method takes
    /// a <typeparamref name="TProject"/> type parameter. The <typeparamref name="TProject"/> type parameter is constrained
    /// to types that implement the <see cref="IProjectMetadata"/> interface.
    /// </para>
    /// <para>
    /// Classes that implement the <see cref="IProjectMetadata"/> interface are generated when a .NET project is added as a reference
    /// to the app host project. The generated class contains a property that returns the path to the referenced project file. Using this path
    /// .NET Aspire parses the <c>launchSettings.json</c> file to determine which launch profile to use when running the project, and
    /// what endpoint configuration to automatically generate.
    /// </para>
    /// <para>
    /// The name of the automatically generated project metadata type is a normalized version of the project name. Periods, dashes, and
    /// spaces in project names are converted to underscores. This normalization may lead to naming conflicts. If a conflict occurs the <c>&lt;ProjectReference /&gt;</c>
    /// that references the project can have a <c>AspireProjectMetadataTypeName="..."</c> attribute added to override the name.
    /// </para>
    /// <para name="kestrel">
    /// Note that endpoints coming from the Kestrel configuration are automatically added to the project. The Kestrel Url and Protocols are used
    /// to build the equivalent <see cref="EndpointAnnotation"/>.
    /// </para>
    /// <example>
    /// Example of adding a project to the application model.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;("inventoryservice");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddProject<TProject>(this IDistributedApplicationBuilder builder, [ResourceName] string name) where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.AddProject<TProject>(name, _ => { });
    }

    /// <summary>
    /// Adds a .NET project to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="projectPath">The path to the project file.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddProject(IDistributedApplicationBuilder, string, string)"/> method adds a project to the application
    /// model using a path to the project file. This allows for projects to be referenced that may not be part of the same solution. If the project
    /// path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <inheritdoc cref="AddProject(IDistributedApplicationBuilder, string)" path="/remarks/para[@name='kestrel']" />
    /// <example>
    /// Add a project to the app model via a project path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject("inventoryservice", @"..\InventoryService\InventoryService.csproj");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(projectPath);

        return builder.AddProject(name, projectPath, _ => { });
    }

    /// <summary>
    /// Adds a .NET project to the application model. By default, this will exist in a Projects namespace. e.g. Projects.MyProject.
    /// If the project is not in a Projects namespace, make sure a project reference is added from the AppHost project to the target project.
    /// </summary>
    /// <typeparam name="TProject">A type that represents the project reference.</typeparam>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="launchProfileName">The launch profile to use. If <c>null</c> then no launch profile will be used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddProject{TProject}(IDistributedApplicationBuilder, string)"/> method takes
    /// a <typeparamref name="TProject"/> type parameter. The <typeparamref name="TProject"/> type parameter is constrained
    /// to types that implement the <see cref="IProjectMetadata"/> interface.
    /// </para>
    /// <para>
    /// Classes that implement the <see cref="IProjectMetadata"/> interface are generated when a .NET project is added as a reference
    /// to the app host project. The generated class contains a property that returns the path to the referenced project file. Using this path
    /// .NET Aspire parses the <c>launchSettings.json</c> file to determine which launch profile to use when running the project, and
    /// what endpoint configuration to automatically generate.
    /// </para>
    /// <para>
    /// The name of the automatically generated project metadata type is a normalized version of the project name. Periods, dashes, and
    /// spaces in project names are converted to underscores. This normalization may lead to naming conflicts. If a conflict occurs the <c>&lt;ProjectReference /&gt;</c>
    /// that references the project can have a <c>AspireProjectMetadataTypeName="..."</c> attribute added to override the name.
    /// </para>
    /// <inheritdoc cref="AddProject(IDistributedApplicationBuilder, string)" path="/remarks/para[@name='kestrel']" />
    /// <example>
    /// Example of adding a project to the application model.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;("inventoryservice", launchProfileName: "otherLaunchProfile");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddProject<TProject>(this IDistributedApplicationBuilder builder, [ResourceName] string name, string? launchProfileName) where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return builder.AddProject<TProject>(name, options =>
        {
            options.ExcludeLaunchProfile = launchProfileName is null;
            options.LaunchProfileName = launchProfileName;
        });
    }

    /// <summary>
    /// Adds a .NET project to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="projectPath">The path to the project file.</param>
    /// <param name="launchProfileName">The launch profile to use. If <c>null</c> then no launch profile will be used.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddProject(IDistributedApplicationBuilder, string, string)"/> method adds a project to the application
    /// model using a path to the project file. This allows for projects to be referenced that may not be part of the same solution. If the project
    /// path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <inheritdoc cref="AddProject(IDistributedApplicationBuilder, string)" path="/remarks/para[@name='kestrel']" />
    /// <example>
    /// Add a project to the app model via a project path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject("inventoryservice", @"..\InventoryService\InventoryService.csproj", launchProfileName: "otherLaunchProfile");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string projectPath, string? launchProfileName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(projectPath);

        return builder.AddProject(name, projectPath, options =>
        {
            options.ExcludeLaunchProfile = launchProfileName is null;
            options.LaunchProfileName = launchProfileName;
        });
    }

    /// <summary>
    /// Adds a .NET project to the application model.
    /// </summary>
    /// <typeparam name="TProject">A type that represents the project reference.</typeparam>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="configure">A callback to configure the project resource options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddProject{TProject}(IDistributedApplicationBuilder, string)"/> method takes
    /// a <typeparamref name="TProject"/> type parameter. The <typeparamref name="TProject"/> type parameter is constrained
    /// to types that implement the <see cref="IProjectMetadata"/> interface.
    /// </para>
    /// <para>
    /// Classes that implement the <see cref="IProjectMetadata"/> interface are generated when a .NET project is added as a reference
    /// to the app host project. The generated class contains a property that returns the path to the referenced project file. Using this path
    /// .NET Aspire parses the <c>launchSettings.json</c> file to determine which launch profile to use when running the project, and
    /// what endpoint configuration to automatically generate.
    /// </para>
    /// <para>
    /// The name of the automatically generated project metadata type is a normalized version of the project name. Periods, dashes, and
    /// spaces in project names are converted to underscores. This normalization may lead to naming conflicts. If a conflict occurs the <c>&lt;ProjectReference /&gt;</c>
    /// that references the project can have a <c>AspireProjectMetadataTypeName="..."</c> attribute added to override the name.
    /// </para>
    /// <example>
    /// Example of adding a project to the application model.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;("inventoryservice", options => { options.LaunchProfileName = "otherLaunchProfile"; });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddProject<TProject>(this IDistributedApplicationBuilder builder, [ResourceName] string name, Action<ProjectResourceOptions> configure) where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProjectResourceOptions();
        configure(options);

        var project = new ProjectResource(name);
        return builder.AddResource(project)
                      .WithAnnotation(new TProject())
                      .WithProjectDefaults(options);
    }

    /// <summary>
    /// Adds a .NET project to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="projectPath">The path to the project file or directory.</param>
    /// <param name="configure">A callback to configure the project resource options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddProject(IDistributedApplicationBuilder, string, string)"/> method adds a project to the application
    /// model using a path to the project file or directory. This allows for projects to be referenced that may not be part of the same solution. If the project
    /// path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <example>
    /// Add a project to the app model via a project path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject("inventoryservice", @"..\InventoryService\InventoryService.csproj", options => { options.LaunchProfileName = "otherLaunchProfile"; });
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string projectPath, Action<ProjectResourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProjectResourceOptions();
        configure(options);

        var project = new ProjectResource(name);

        projectPath = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, projectPath));

        return builder.AddResource(project)
                      .WithAnnotation(new ProjectMetadata(projectPath))
                      .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = projectPath, Mode = mode }, "ms-dotnettools.csharp")
                      .WithProjectDefaults(options);
    }

    /// <summary>
    /// Adds a C# project or file-based app to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="path">The path to the file-based app file, project file, or project directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddCSharpApp(IDistributedApplicationBuilder, string, string)"/> method adds a C# project or file-based app to the application
    /// model using a path to the file-based app .cs file, project file (.csproj), or project directory.
    /// If the path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <example>
    /// Add a file-based app to the app model via a file path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddCSharpApp("inventoryservice", @"..\InventoryService.cs");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECSHARPAPPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<ProjectResource> AddCSharpApp(this IDistributedApplicationBuilder builder, string name, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);

        return builder.AddCSharpApp(name, path, _ => { });
    }

    /// <summary>
    /// Adds a C# project or file-based app to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="path">The path to the file-based app file, project file, or project directory.</param>
    /// <param name="configure">An optional action to configure the C# app resource options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddCSharpApp(IDistributedApplicationBuilder, string, string)"/> method adds a C# project or file-based app to the application
    /// model using a path to the file-based app .cs file, project file (.csproj), or project directory.
    /// If the path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <example>
    /// Add a file-based app to the app model via a file path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddCSharpApp("inventoryservice", @"..\InventoryService.cs", o => o.LaunchProfileName = "https");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIRECSHARPAPPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<CSharpAppResource> AddCSharpApp(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path, Action<ProjectResourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProjectResourceOptions();
        configure(options);

        var app = new CSharpAppResource(name);

        path = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, path));
        var projectMetadata = new ProjectMetadata(path);

        var resource = builder.AddResource(app)
                              .WithAnnotation(projectMetadata)
                              .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = projectMetadata.ProjectPath, Mode = mode }, "ms-dotnettools.csharp")
                              .WithProjectDefaults(options);

        resource.OnBeforeResourceStarted(async (r, e, ct) =>
        {
            var projectPath = projectMetadata.ProjectPath;

            // Validate project path
            if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !projectPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Project path did not resolve to a .csproj or .cs file
                var message = Directory.Exists(projectPath)
                    ? $"Path to C# project could not be determined. The directory '{projectPath}' must contain a single .csproj file."
                    : $"The C# app path '{projectPath}' is invalid. The path must be to a .cs file, .csproj file, or directory containing a single .csproj file.";
                throw new DistributedApplicationException(message);
            }

            // Validate .NET version
            if (((IProjectMetadata)projectMetadata).IsFileBasedApp
                && await DotnetSdkUtils.TryGetVersionAsync(Path.GetDirectoryName(projectPath)).ConfigureAwait(false) is { } version
                && version.Major < 10)
            {
                // File-based apps are only supported on .NET 10 or later
                var versionValue = version is not null
                    ? $"is {version}"
                    : "could not be determined";
                throw new DistributedApplicationException($"File-based apps are only supported on .NET 10 or later. The version active in '{Path.GetDirectoryName(projectPath)}' {versionValue}.");
            }
        });

        return resource;
    }

    private static IResourceBuilder<TProjectResource> WithProjectDefaults<TProjectResource>(this IResourceBuilder<TProjectResource> builder, ProjectResourceOptions options)
        where TProjectResource : ProjectResource
    {
        // .NET SDK has experimental support for retries. Enable with env var.
        // https://github.com/open-telemetry/opentelemetry-dotnet/pull/5495
        // Remove once retry feature in opentelemetry-dotnet is enabled by default.
        builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY", "in_memory");

        // OTEL settings that are used to improve local development experience.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode && builder.ApplicationBuilder.Environment.IsDevelopment())
        {
            // Disable URL query redaction, e.g. ?myvalue=Redacted
            builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION", "true");
            builder.WithEnvironment("OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION", "true");
        }

        builder.WithOtlpExporter();
        builder.ConfigureConsoleLogs();

        if (OperatingSystem.IsWindows())
        {
            // On Windows, the custom certificate trust feature is not supported for .NET projects, so disable it by default.
            builder.WithCertificateTrustScope(CertificateTrustScope.None);
        }

        builder.WithCertificateTrustConfiguration(ctx =>
        {
            if (ctx.Scope != CertificateTrustScope.None && OperatingSystem.IsWindows())
            {
                // Log if the user attempts to enable certificate trust customization on Windows for .NET projects.
                var resourceLogger = ctx.ExecutionContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                var logger = resourceLogger.GetLogger(builder.Resource);
                logger.LogWarning("Certificate trust scope is set to '{Scope}', but the feature is not supported for .NET projects on Windows. No certificate trust customization will be applied. Set the certificate trust scope to 'None' to disable this warning.", Enum.GetName(ctx.Scope));
                return Task.CompletedTask;
            }

            return Task.CompletedTask;
        });

        var projectResource = builder.Resource;

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithEnvironment(context =>
            {
                // If we have any endpoints & the forwarded headers wasn't disabled then add it
                if (projectResource.GetEndpoints().Any() && !projectResource.Annotations.OfType<DisableForwardedHeadersAnnotation>().Any())
                {
                    context.EnvironmentVariables[AspNetCoreForwardedHeadersEnabledVariableName] = "true";
                }
            });
        }

        if (options.ExcludeLaunchProfile)
        {
            builder.WithAnnotation(new ExcludeLaunchProfileAnnotation());
        }
        else if (!string.IsNullOrEmpty(options.LaunchProfileName))
        {
            builder.WithAnnotation(new LaunchProfileAnnotation(options.LaunchProfileName));
        }
        else
        {
            var appHostDefaultLaunchProfileName = builder.ApplicationBuilder.Configuration["AppHost:DefaultLaunchProfileName"]
                ?? builder.ApplicationBuilder.Configuration["DOTNET_LAUNCH_PROFILE"];
            if (!string.IsNullOrEmpty(appHostDefaultLaunchProfileName))
            {
                builder.WithAnnotation(new DefaultLaunchProfileAnnotation(appHostDefaultLaunchProfileName));
            }
        }

        var effectiveLaunchProfile = options.ExcludeLaunchProfile ? null : projectResource.GetEffectiveLaunchProfile(throwIfNotFound: true);
        var launchProfile = effectiveLaunchProfile?.LaunchProfile;

        // Get all the endpoints from the Kestrel configuration
        var config = GetConfiguration(projectResource);
        var kestrelEndpoints = options.ExcludeKestrelEndpoints ? [] : config.GetSection("Kestrel:Endpoints").GetChildren();

        // Get all the Kestrel configuration endpoint bindings, grouped by scheme
        var kestrelEndpointsByScheme = kestrelEndpoints
            .Where(endpoint => endpoint["Url"] is string)
            .Select(endpoint => new
            {
                EndpointName = endpoint.Key,
                BindingAddress = BindingAddress.Parse(endpoint["Url"]!),
                Protocols = endpoint["Protocols"]
            })
            .GroupBy(entry => entry.BindingAddress.Scheme);

        // Helper to change the transport to http2 if needed
        var isHttp2ConfiguredInKestrelEndpointDefaults = config["Kestrel:EndpointDefaults:Protocols"] == nameof(HttpProtocols.Http2);
        var adjustTransport = (EndpointAnnotation e, string? bindingLevelProtocols = null) =>
        {
            if (bindingLevelProtocols != null)
            {
                // If the Kestrel endpoint has an explicit protocol, use that and ignore any EndpointDefaults
                e.Transport = bindingLevelProtocols == nameof(HttpProtocols.Http2) ? "http2" : e.Transport;
            }
            else if (isHttp2ConfiguredInKestrelEndpointDefaults)
            {
                // Fall back to honoring Http2 specified at EndpointDefaults level
                e.Transport = "http2";
            }
        };

        foreach (var schemeGroup in kestrelEndpointsByScheme)
        {
            // If there is only one endpoint for a given scheme, we use the scheme as the endpoint name
            // Otherwise, we use the actual endpoint names from the config
            var schemeAsEndpointName = schemeGroup.Count() <= 1 ? schemeGroup.Key : null;

            foreach (var endpoint in schemeGroup)
            {
                builder.WithEndpoint(schemeAsEndpointName ?? endpoint.EndpointName, e =>
                {
                    if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
                    {
                        // In Publish mode, we could not set the Port because it needs to be the standard
                        // port in scenarios like ACA. So we set the target instead, since we can control that
                        e.TargetPort = endpoint.BindingAddress.Port;
                    }
                    else
                    {
                        // Locally, there is no issue with setting the Port. And in fact, we could not set the
                        // target port because that would break replica sets
                        e.Port = endpoint.BindingAddress.Port;
                    }
                    e.UriScheme = endpoint.BindingAddress.Scheme;

                    e.TargetHost = ParseKestrelHost(endpoint.BindingAddress.Host);

                    adjustTransport(e, endpoint.Protocols);
                    // Keep track of the host separately since EndpointAnnotation doesn't have a host property
                    builder.Resource.KestrelEndpointAnnotationHosts[e] = e.TargetHost;
                },
                createIfNotExists: true);
            }
        }

        // Use environment variables to override endpoints if there is a Kestrel config
        builder.SetKestrelUrlOverrideEnvVariables();

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // We don't need to set ASPNETCORE_URLS if we have Kestrel endpoints configured
            // as Kestrel will get everything it needs from the config.
            if (!kestrelEndpointsByScheme.Any())
            {
                builder.SetAspNetCoreUrls();
            }

            // Process the launch profile and turn it into environment variables and endpoints.
            if (launchProfile is null)
            {
                return builder;
            }

            // If we had found any Kestrel endpoints, we ignore the launch profile endpoints,
            // to match the Kestrel runtime behavior.
            if (!kestrelEndpointsByScheme.Any())
            {
                var urlsFromApplicationUrl = launchProfile.ApplicationUrl?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [];
                Dictionary<string, int> endpointCountByScheme = [];
                foreach (var url in urlsFromApplicationUrl)
                {
                    var bindingAddress = BindingAddress.Parse(url);

                    // Keep track of how many endpoints we have for each scheme
                    endpointCountByScheme.TryGetValue(bindingAddress.Scheme, out var count);
                    endpointCountByScheme[bindingAddress.Scheme] = count + 1;

                    // If we have multiple for the same scheme, we differentiate them by appending a number.
                    // We only do this starting with the second endpoint, so that the first stays just http/https.
                    // This allows us to keep the same behavior as "dotnet run".
                    // Also, note that we only do this in Run mode, as in Publish mode those extra endpoints
                    // with generic names would not be easily usable.
                    var endpointName = bindingAddress.Scheme;
                    if (endpointCountByScheme[bindingAddress.Scheme] > 1)
                    {
                        endpointName += endpointCountByScheme[bindingAddress.Scheme];
                    }

                    builder.WithEndpoint(endpointName, e =>
                    {
                        e.Port = bindingAddress.Port;
                        e.TargetHost = ParseKestrelHost(bindingAddress.Host);
                        e.UriScheme = bindingAddress.Scheme;
                        e.FromLaunchProfile = true;
                        adjustTransport(e);
                    },
                    createIfNotExists: true);
                }

                // Update URLs for endpoints from the launch profile if a launchUrl is set
                if (Uri.TryCreate(launchProfile.LaunchUrl, UriKind.RelativeOrAbsolute, out var launchUri))
                {
                    builder.WithUrls(context =>
                    {
                        if (context.Resource.TryGetEndpoints(out var endpoints))
                        {
                            foreach (var endpoint in endpoints)
                            {
                                if (endpoint.FromLaunchProfile)
                                {
                                    var url = context.Urls.FirstOrDefault(u => string.Equals(u.Endpoint?.EndpointName, endpoint.Name, StringComparisons.EndpointAnnotationName));
                                    if (url is not null)
                                    {
                                        if (launchUri.IsAbsoluteUri)
                                        {
                                            // Launch URL is absolute, replace the url entirely
                                            url.Url = launchProfile.LaunchUrl;
                                        }
                                        else
                                        {
                                            // Launch URL is relative so update the URL to use the launchUrl as path/query
                                            var baseUri = new Uri(url.Url);
                                            url.Url = (new Uri(baseUri, launchUri)).ToString();
                                        }
                                    }
                                }
                            }
                        }
                    });
                }
            }

            builder.WithEnvironment(context =>
            {
                // Populate DOTNET_LAUNCH_PROFILE environment variable for consistency with "dotnet run" and "dotnet watch".
                if (effectiveLaunchProfile is not null)
                {
                    context.EnvironmentVariables.TryAdd("DOTNET_LAUNCH_PROFILE", effectiveLaunchProfile.Name);
                }

                foreach (var envVar in launchProfile.EnvironmentVariables)
                {
                    var value = Environment.ExpandEnvironmentVariables(envVar.Value);
                    context.EnvironmentVariables.TryAdd(envVar.Key, value);
                }
            });

            // NOTE: the launch profile command line arguments will be processed by ApplicationExecutor.PrepareProjectExecutables() (either by the IDE or manually passed to run)
        }
        else
        {
            // Set HTTP_PORTS/HTTPS_PORTS in publish mode, to override the default port set in the base image. Note that:
            // - We don't set them if we have Kestrel endpoints configured, as Kestrel will get everything from its config.
            // - We only do that for endpoint set explicitly (.WithHttpEndpoint), not for the ones coming from launch profile.
            //   This is because launch profile endpoints are not meant to be used in production.
            if (!kestrelEndpointsByScheme.Any())
            {
                builder.SetBothPortsEnvVariables();
            }

            // If we aren't a web project (looking at both launch profile and Kestrel config) we don't automatically add bindings.
            if (launchProfile?.ApplicationUrl == null && !kestrelEndpointsByScheme.Any())
            {
                return builder;
            }

            EndpointAnnotation GetOrCreateEndpointForScheme(string scheme)
            {
                EndpointAnnotation? GetEndpoint(string scheme) =>
                    projectResource.Annotations.OfType<EndpointAnnotation>().FirstOrDefault(sb => sb.UriScheme == scheme || string.Equals(sb.Name, scheme, StringComparisons.EndpointAnnotationName));

                var endpoint = GetEndpoint(scheme);

                // If there is no endpoint named after the scheme, create one
                if (endpoint is null)
                {
                    builder.WithEndpoint(scheme, e =>
                    {
                        e.UriScheme = scheme;
                        adjustTransport(e);

                        // Keep track of the default https endpoint so we can exclude it from HTTPS_PORTS & Kestrel env vars
                        if (scheme == "https")
                        {
                            builder.Resource.DefaultHttpsEndpoint = e;
                        }
                    },
                    createIfNotExists: true);

                    endpoint = GetEndpoint(scheme)!;
                }

                return endpoint;
            }

            var httpEndpoint = GetOrCreateEndpointForScheme("http");
            var httpsEndpoint = GetOrCreateEndpointForScheme("https");

            // We make sure that the http and https endpoints have the same target port
            var defaultEndpointTargetPort = httpEndpoint.TargetPort ?? httpsEndpoint.TargetPort;
            httpEndpoint.TargetPort = httpsEndpoint.TargetPort = defaultEndpointTargetPort;
        }

        return builder;
    }

    /// <summary>
    /// Configures how many replicas of the project should be created for the project.
    /// </summary>
    /// <param name="builder">The project resource builder.</param>
    /// <param name="replicas">The number of replicas.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When this method is applied to a project resource it will configure the app host to start multiple instances
    /// of the application based on the specified number of replicas. By default the app host automatically starts a
    /// reverse proxy for each process. When <see cref="WithReplicas(IResourceBuilder{ProjectResource}, int)"/> is
    /// used the reverse proxy will load balance traffic between the replicas.
    /// </para>
    /// <para>
    /// This capability can be useful when debugging scale out scenarios to ensure state is appropriately managed
    /// within a cluster of instances.
    /// </para>
    /// <example>
    /// Start multiple instances of the same service.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;("inventoryservice")
    ///        .WithReplicas(3);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> WithReplicas(this IResourceBuilder<ProjectResource> builder, int replicas)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ReplicaAnnotation(replicas));
        return builder;
    }

    /// <summary>
    /// Configures the project to disable forwarded headers when being published.
    /// </summary>
    /// <param name="builder">The project resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// By default .NET Aspire assumes that .NET applications which expose endpoints should be configured to
    /// use forwarded headers. This is because most typical cloud native deployment scenarios involve a reverse
    /// proxy which translates an external endpoint hostname to an internal address.
    /// </para>
    /// <para>
    /// To enable forwarded headers the <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> variable is injected
    /// into the project and set to true. If the <see cref="DisableForwardedHeaders(IResourceBuilder{ProjectResource})"/>
    /// extension is used this environment variable will not be set.
    /// </para>
    /// <example>
    /// Disable forwarded headers for a project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddProject&lt;Projects.InventoryService&gt;("inventoryservice")
    ///        .DisableForwardedHeaders();
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<ProjectResource> DisableForwardedHeaders(this IResourceBuilder<ProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation<DisableForwardedHeadersAnnotation>(ResourceAnnotationMutationBehavior.Replace);
        return builder;
    }

    /// <summary>
    /// Set a filter that determines if environment variables are injected for a given endpoint.
    /// By default, all endpoints are included (if this method is not called).
    /// </summary>
    /// <param name="builder">The project resource builder.</param>
    /// <param name="filter">The filter callback that returns true if and only if the endpoint should be included.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<ProjectResource> WithEndpointsInEnvironment(
        this IResourceBuilder<ProjectResource> builder, Func<EndpointAnnotation, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(filter);

        builder.Resource.Annotations.Add(new EndpointEnvironmentInjectionFilterAnnotation(filter));

        return builder;
    }

    /// <summary>
    /// Adds support for containerizing this <see cref="ProjectResource"/> during deployment.
    /// The resulting container image is built, and when the optional <paramref name="configure"/> action is provided,
    /// it is used to configure the container resource.
    /// </summary>
    /// <remarks>
    /// When the executable resource is converted to a container resource, the arguments to the executable
    /// are not used. This is because arguments to the project often contain physical paths that are not valid
    /// in the container. The container can be set up with the correct arguments using the <paramref name="configure"/> action.
    /// </remarks>
    /// <typeparam name="T">Type of executable resource</typeparam>
    /// <param name="builder">Resource builder</param>
    /// <param name="configure">Optional action to configure the container resource</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<T> PublishAsDockerFile<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<ContainerResource>>? configure = null)
        where T : ProjectResource
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Check if this resource has already been converted to a container resource.
        // This makes the method idempotent - multiple calls won't cause errors.
        if (builder.ApplicationBuilder.TryCreateResourceBuilder<ProjectContainerResource>(builder.Resource.Name, out var existingBuilder))
        {
            // Resource has already been converted, just invoke the configure callback if provided
            configure?.Invoke(existingBuilder);
            return builder;
        }

        // The implementation here is less than ideal, but we don't have a clean way of building resource types
        // that change their behavior based on the context. In this case, we want to change the behavior of the
        // resource from a ProjectResource to a ContainerResource. We do this by removing the ProjectResource
        // from the application model and adding a new ContainerResource in its place in publish mode.

        // There are still dangling references to the original ProjectResource in the application model, but
        // in publish mode, it won't be used. This is a limitation of the current design.
        builder.ApplicationBuilder.Resources.Remove(builder.Resource);

        var container = new ProjectContainerResource(builder.Resource);
        var cb = builder.ApplicationBuilder.AddResource(container);
        // WithImage makes this a container resource (adding the annotation)
        cb.WithImage(builder.Resource.Name);

        var projectFilePath = builder.Resource.GetProjectMetadata().ProjectPath;
        var projectDirectoryPath = Path.GetDirectoryName(projectFilePath) ?? throw new InvalidOperationException($"Unable to get directory name for {projectFilePath}");

        cb.WithDockerfile(contextPath: projectDirectoryPath);
        // Arguments to the executable often contain physical paths that are not valid in the container
        // Clear them out so that the container can be set up with the correct arguments
        cb.WithArgs(c => c.Args.Clear());

        // Configure default http/https endpoints for the container
        // Note that we set the target port to 8080 (if not already set), which is the port used in the ASP.NET base images
        cb.WithEndpoint("http", e => e.TargetPort ??= 8080, createIfNotExists: false);
        cb.WithEndpoint("https", e => e.TargetPort ??= 8080, createIfNotExists: false);

        configure?.Invoke(cb);

        // Even through we're adding a ContainerResource
        // update the manifest publishing callback on the original ProjectResource
        // so that the container resource is written to the manifest
        return builder.WithManifestPublishingCallback(context =>
            context.WriteContainerAsync(container));
    }

    private static IConfiguration GetConfiguration(ProjectResource projectResource)
    {
        var projectMetadata = projectResource.GetProjectMetadata();

        // For testing
        if (projectMetadata.Configuration is { } configuration)
        {
            return configuration;
        }

        var projectDirectoryPath = Path.GetDirectoryName(projectMetadata.ProjectPath)!;
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var appSettingsPath = Path.Combine(projectDirectoryPath, "appsettings.json");
        var appSettingsEnvironmentPath = Path.Combine(projectDirectoryPath, $"appsettings.{env}.json");
        // .NET 10 introduced support for application-specific settings files: https://github.com/dotnet/runtime/pull/116987
        var appFileName = Path.GetFileName(projectDirectoryPath);
        var appNameSettingsPath = Path.Combine(projectDirectoryPath, $"{appFileName}.settings.json");
        var appNameSettingsEnvironmentPath = Path.Combine(projectDirectoryPath, $"{appFileName}.settings.{env}.json");

        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddJsonFile(appSettingsPath, optional: true);
        configBuilder.AddJsonFile(appSettingsEnvironmentPath, optional: true);
        configBuilder.AddJsonFile(appNameSettingsPath, optional: true);
        configBuilder.AddJsonFile(appNameSettingsEnvironmentPath, optional: true);
        return configBuilder.Build();
    }

    private static void SetAspNetCoreUrls(this IResourceBuilder<ProjectResource> builder)
    {
        builder.WithEnvironment(context =>
        {
            if (context.EnvironmentVariables.ContainsKey("ASPNETCORE_URLS"))
            {
                // If the user has already set ASPNETCORE_URLS, we don't want to override it.
                return;
            }

            var aspnetCoreUrls = new ReferenceExpressionBuilder();

            var processedHttpsPort = false;
            var first = true;

            // Turn http and https endpoints into a single ASPNETCORE_URLS environment variable.
            foreach (var e in builder.Resource.GetEndpoints().Where(builder.Resource.ShouldInjectEndpointEnvironment))
            {
                if (!first)
                {
                    aspnetCoreUrls.AppendLiteral(";");
                }

                if (!processedHttpsPort && e.EndpointAnnotation.UriScheme == "https")
                {
                    // Add the environment variable for the HTTPS port if we have an HTTPS service. This will make sure the
                    // HTTPS redirection middleware avoids redirecting to the internal port.
                    context.EnvironmentVariables["ASPNETCORE_HTTPS_PORT"] = e.Property(EndpointProperty.Port);

                    processedHttpsPort = true;
                }

                aspnetCoreUrls.Append($"{e.Property(EndpointProperty.Scheme)}://{e.EndpointAnnotation.TargetHost}:{e.Property(EndpointProperty.TargetPort)}");
                first = false;
            }

            if (!aspnetCoreUrls.IsEmpty)
            {
                // Combine into a single expression
                context.EnvironmentVariables["ASPNETCORE_URLS"] = aspnetCoreUrls.Build();
            }
        });
    }

    private static void SetBothPortsEnvVariables(this IResourceBuilder<ProjectResource> builder)
    {
        builder.WithEnvironment(context =>
        {
            builder.SetOnePortsEnvVariable(context, "HTTP_PORTS", "http");
            builder.SetOnePortsEnvVariable(context, "HTTPS_PORTS", "https");
        });
    }

    private static void SetOnePortsEnvVariable(this IResourceBuilder<ProjectResource> builder, EnvironmentCallbackContext context, string portEnvVariable, string scheme)
    {
        if (context.EnvironmentVariables.ContainsKey(portEnvVariable))
        {
            // If the user has already set that variable, we don't want to override it.
            return;
        }

        var ports = new ReferenceExpressionBuilder();
        var firstPort = true;

        // Turn endpoint ports into a single environment variable
        foreach (var e in builder.Resource.GetEndpoints().Where(builder.Resource.ShouldInjectEndpointEnvironment))
        {
            // Skip the default https endpoint because the container likely won't be set up to listen on https (e.g. ACA case)
            if (e.EndpointAnnotation.UriScheme == scheme && e.EndpointAnnotation != builder.Resource.DefaultHttpsEndpoint)
            {
                Debug.Assert(!e.EndpointAnnotation.FromLaunchProfile, "Endpoints from launch profile should never make it here");

                if (!firstPort)
                {
                    ports.AppendLiteral(";");
                }

                ports.Append($"{e.Property(EndpointProperty.TargetPort)}");
                firstPort = false;
            }
        }

        if (!firstPort)
        {
            context.EnvironmentVariables[portEnvVariable] = ports.Build();
        }
    }

    private static void SetKestrelUrlOverrideEnvVariables(this IResourceBuilder<ProjectResource> builder)
    {
        builder.WithEnvironment(context =>
        {
            // If there are any Kestrel endpoints, we need to override all endpoints, even if they
            // don't come from Kestrel. This is because having Kestrel endpoints overrides everything
            if (builder.Resource.HasKestrelEndpoints)
            {
                foreach (var e in builder.Resource.GetEndpoints().Where(builder.Resource.ShouldInjectEndpointEnvironment))
                {
                    // Skip the default https endpoint because the container likely won't be set up to listen on https (e.g. ACA case)
                    if (e.EndpointAnnotation == builder.Resource.DefaultHttpsEndpoint)
                    {
                        continue;
                    }

                    // In Run mode, we keep the original Kestrel config host.
                    // In Publish mode, we always use *, so it can work in a container (where localhost wouldn't work).
                    var host = builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
                        builder.Resource.KestrelEndpointAnnotationHosts.TryGetValue(e.EndpointAnnotation, out var kestrelHost) ? kestrelHost : "*";

                    var url = ReferenceExpression.Create($"{e.EndpointAnnotation.UriScheme}://{host}:{e.Property(EndpointProperty.TargetPort)}");

                    // We use special config system environment variables to perform the override.
                    context.EnvironmentVariables[$"Kestrel__Endpoints__{e.EndpointAnnotation.Name}__Url"] = url;
                }
            }
        });
    }

    private static string ParseKestrelHost(string host)
    {
        if (EndpointHostHelpers.IsLocalhost(host))
        {
            // Localhost is used as-is rather than being resolved to a specific loopback IP address.
            return "localhost";
        }
        return host;
    }

    // Allows us to mirror annotations from ProjectContainerResource to ContainerResource
    private sealed class ProjectContainerResource(ProjectResource pr) : ContainerResource(pr.Name)
    {
        public override ResourceAnnotationCollection Annotations => pr.Annotations;
    }
}
