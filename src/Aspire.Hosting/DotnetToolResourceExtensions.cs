// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Dotnet Tool resources to the application model.
/// </summary>
[Experimental("ASPIREDOTNETTOOL", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DotnetToolResourceExtensions
{
    internal const string ArgumentSeparator = "--";

    /// <summary>
    /// Adds a .NET tool resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="packageId">The package id of the tool.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addDotnetTool", Description = "Adds a .NET tool resource")]
    public static IResourceBuilder<DotnetToolResource> AddDotnetTool(this IDistributedApplicationBuilder builder, [ResourceName] string name, string packageId)
        => builder.AddDotnetTool(new DotnetToolResource(name, packageId));

    /// <summary>
    /// Adds a .NET tool resource to the distributed application model and configures it for execution via the <c>dotnet
    /// tool exec</c> command.
    /// </summary>
    /// <typeparam name="T">The type of the .NET tool resource to add. Must inherit from <see cref="DotnetToolResource"/>.</typeparam>
    /// <param name="builder">The distributed application builder to which the .NET tool resource will be added.</param>
    /// <param name="resource">The .NET tool resource instance to add and configure.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use the overload with name and packageId instead.</remarks>
    [AspireExportIgnore(Reason = "Open generic IResource constraint — not ATS-compatible.")]
    public static IResourceBuilder<T> AddDotnetTool<T>(this IDistributedApplicationBuilder builder, T resource)
        where T : DotnetToolResource
    {
        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = KnownResourceTypes.Tool,
                Properties = []
            })
            .WithIconName("Toolbox")
            .WithCommand("dotnet")
            .WithArgs(BuildToolExecArguments)
            .OnBeforeResourceStarted(BeforeResourceStarted);

        void BuildToolExecArguments(CommandLineArgsCallbackContext x)
        {
            var toolConfig = resource.ToolConfiguration;
            if (toolConfig == null)
            {
                // If the annotation has been removed, don't add any dotnet tool arguments.
                return;
            }

            x.Args.Add("tool");
            x.Args.Add("exec");
            x.Args.Add(toolConfig.PackageId);

            var sourceArg = toolConfig.IgnoreExistingFeeds ? "--source" : "--add-source";

            foreach (var source in toolConfig.Sources)
            {
                x.Args.Add(sourceArg);
                x.Args.Add(source);
            }

            if (toolConfig.IgnoreFailedSources)
            {
                x.Args.Add("--ignore-failed-sources");
            }

            if (toolConfig.Version is not null)
            {
                x.Args.Add("--version");
                x.Args.Add(toolConfig.Version);
            }
            else if (toolConfig.Prerelease)
            {
                x.Args.Add("--prerelease");
            }

            x.Args.Add("--yes");
            x.Args.Add(ArgumentSeparator);
        }

        //TODO: Move to WithConfigurationFinalizer once merged - https://github.com/microsoft/aspire/pull/13200
        async Task BeforeResourceStarted(T resource, BeforeResourceStartedEvent evt, CancellationToken ct)
        {
            var rns = evt.Services.GetRequiredService<ResourceNotificationService>();
            var toolConfig = resource.ToolConfiguration;
            if (toolConfig == null)
            {
                return;
            }

            await rns.PublishUpdateAsync(resource, x => x with
            {
                Properties = x.Properties.SetResourcePropertyRange([
                    new (KnownProperties.Tool.Package, toolConfig.PackageId),
                    new (KnownProperties.Tool.Version, toolConfig.Version),
                    new (KnownProperties.Resource.Source, resource.ToolConfiguration?.PackageId)
                    ])
            }).ConfigureAwait(false);

            var version = await DotnetSdkUtils.TryGetVersionAsync(resource.WorkingDirectory).ConfigureAwait(false);
            ValidateDotnetSdkVersion(version, resource.WorkingDirectory);
        }

    }

    /// <summary>
    /// Sets the package identifier for the tool configuration associated with the resource builder.
    /// </summary>
    /// <typeparam name="T">The Dotnet Tool resource type</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="packageId">The package identifier to assign to the tool configuration. Cannot be null.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withToolPackage", Description = "Sets the tool package ID")]
    public static IResourceBuilder<T> WithToolPackage<T>(this IResourceBuilder<T> builder, string packageId)
        where T : DotnetToolResource
    {
        builder.Resource.ToolConfiguration?.PackageId = packageId;
        return builder;
    }

    /// <summary>
    /// Sets the package version for a tool to use.
    /// </summary>
    /// <typeparam name="T">The Dotnet Tool resource type</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="version">The package version to use</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withToolVersion", Description = "Sets the tool version")]
    public static IResourceBuilder<T> WithToolVersion<T>(this IResourceBuilder<T> builder, string version)
        where T : DotnetToolResource
    {
        builder.Resource.ToolConfiguration?.Version = version;
        return builder;
    }

    /// <summary>
    /// Allows prerelease versions of the tool to be used
    /// </summary>
    /// <typeparam name="T">The type of resource being built. Must inherit from DotnetToolResource.</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withToolPrerelease", Description = "Allows prerelease tool versions")]
    public static IResourceBuilder<T> WithToolPrerelease<T>(this IResourceBuilder<T> builder)
        where T : DotnetToolResource
    {
        builder.Resource.ToolConfiguration?.Prerelease = true;
        return builder;
    }

    /// <summary>
    /// Adds a NuGet package source for tool acquisition.
    /// </summary>
    /// <typeparam name="T">The Dotnet Tool resource type</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <param name="source">The source to add.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withToolSource", Description = "Adds a NuGet source for the tool")]
    public static IResourceBuilder<T> WithToolSource<T>(this IResourceBuilder<T> builder, string source)
        where T : DotnetToolResource
    {
        builder.Resource.ToolConfiguration?.Sources.Add(source);
        return builder;
    }

    /// <summary>
    /// Configures the tool to use only the specified package sources, ignoring existing NuGet configuration.
    /// </summary>
    /// <typeparam name="T">The Dotnet Tool resource type</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withToolIgnoreExistingFeeds", Description = "Ignores existing NuGet feeds")]
    public static IResourceBuilder<T> WithToolIgnoreExistingFeeds<T>(this IResourceBuilder<T> builder)
        where T : DotnetToolResource
    {
        builder.Resource.ToolConfiguration?.IgnoreExistingFeeds = true;
        return builder;
    }

    /// <summary>
    /// Configures the resource to treat package source failures as warnings.
    /// </summary>
    /// <typeparam name="T">The Dotnet Tool resource type</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withToolIgnoreFailedSources", Description = "Ignores failed NuGet sources")]
    public static IResourceBuilder<T> WithToolIgnoreFailedSources<T>(this IResourceBuilder<T> builder)
        where T : DotnetToolResource
    {
        builder.Resource.ToolConfiguration?.IgnoreFailedSources = true;
        return builder;
    }

    internal static void ValidateDotnetSdkVersion(Version? version, string workingDirectory)
    {
        if (version is null)
        {
            // This most likely means something is majorly wrong with the dotnet sdk
            // Which will show up as an error once dcp runs `dotnet tool exec`
            return;
        }

        if (version.Major < 10)
        {
            throw new DistributedApplicationException($"DotnetToolResource requires dotnet SDK 10 or higher to run. Detected version: {version} for working directory {workingDirectory}");
        }
    }
}
