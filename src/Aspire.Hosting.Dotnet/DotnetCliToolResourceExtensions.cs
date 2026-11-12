// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for acquiring and configuring .NET CLI tools.
/// </summary>
/// <remarks>
/// Tool packages are launched with <c>dotnet tool exec</c>. Arguments configured with
/// <c>WithArgs</c> are passed to the tool after the invocation's argument separator.
/// </remarks>
[Experimental("ASPIREDOTNETCLITOOL001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class DotnetCliToolResourceExtensions
{
    /// <summary>
    /// Adds an executable acquired from a NuGet .NET tool package.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="packageId">The NuGet tool package ID.</param>
    /// <returns>The tool resource builder.</returns>
    /// <remarks>The tool is acquired when started and requires .NET SDK 10 or later.</remarks>
    /// <example>
    /// <code>
    /// builder.AddDotnetCliTool("ef", "dotnet-ef").WithToolVersion("10.0.*").WithArgs("--help");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<DotnetCliToolResource> AddDotnetCliTool(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string packageId)
        => builder.AddDotnetCliTool(new DotnetCliToolResource(name, packageId));

    /// <summary>
    /// Adds a custom .NET CLI tool resource and configures its acquisition and execution.
    /// </summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The application builder.</param>
    /// <param name="resource">The tool resource to register.</param>
    /// <returns>The same resource builder type.</returns>
    /// <remarks>Polyglot AppHosts use the name and package ID overload instead.</remarks>
    [AspireExportIgnore(Reason = "Open generic resource overload; polyglot hosts use name and packageId.")]
    public static IResourceBuilder<T> AddDotnetCliTool<T>(this IDistributedApplicationBuilder builder, T resource)
        where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);
        builder.Services.TryAddSingleton<DotnetCliToolSdkVersionProvider>();

        return DotnetToolHelpers.AddTool(builder, resource, context =>
        {
            if (resource.ToolConfiguration is { } configuration)
            {
                DotnetToolHelpers.AddArguments(context, configuration.PackageId, configuration.Version,
                    configuration.Prerelease, configuration.Sources,
                    configuration.IgnoreExistingFeeds, configuration.IgnoreFailedSources);
            }
        }, static current => current.CreateSnapshotProperties(), ValidateSdkAsync);
    }

    /// <summary>Sets the tool's NuGet package ID.</summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="packageId">The package ID.</param>
    /// <returns>The same resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="packageId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="packageId"/> is empty or whitespace.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithToolPackage<T>(this IResourceBuilder<T> builder, string packageId) where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        builder.Resource.ToolConfiguration?.PackageId = packageId;
        return builder;
    }

    /// <summary>Sets the tool's package version or version range.</summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="version">The package version or range.</param>
    /// <returns>The same resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="version"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> is empty or whitespace.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithToolVersion<T>(this IResourceBuilder<T> builder, string version) where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        builder.Resource.ToolConfiguration?.Version = version;
        return builder;
    }

    /// <summary>Allows prerelease packages when no explicit version is configured.</summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The same resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithToolPrerelease<T>(this IResourceBuilder<T> builder) where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.ToolConfiguration?.Prerelease = true;
        return builder;
    }

    /// <summary>Adds a NuGet package source for acquisition.</summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The package source.</param>
    /// <returns>The same resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is empty or whitespace.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithToolSource<T>(this IResourceBuilder<T> builder, string source) where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        builder.Resource.ToolConfiguration?.Sources.Add(source);
        return builder;
    }

    /// <summary>Uses only explicitly configured sources, ignoring existing NuGet configuration.</summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The same resource builder.</returns>
    /// <remarks>Has no effect when no package sources are configured.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithToolIgnoreExistingFeeds<T>(this IResourceBuilder<T> builder) where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.ToolConfiguration?.IgnoreExistingFeeds = true;
        return builder;
    }

    /// <summary>Treats package source failures as warnings.</summary>
    /// <typeparam name="T">The tool resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The same resource builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithToolIgnoreFailedSources<T>(this IResourceBuilder<T> builder) where T : DotnetCliToolResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.ToolConfiguration?.IgnoreFailedSources = true;
        return builder;
    }

#pragma warning disable ASPIRECOMMAND001 // Preserve the existing tool SDK-validation policy.
    private static async Task<RequiredCommandValidationResult> ValidateSdkAsync(RequiredCommandValidationContext context, string workingDirectory)
    {
        var version = await context.Services.GetRequiredService<DotnetCliToolSdkVersionProvider>()
            .TryGetVersionAsync(workingDirectory, context.CancellationToken).ConfigureAwait(false);
        return DotnetToolHelpers.ValidateSdkVersion(version, nameof(DotnetCliToolResource));
    }
#pragma warning restore ASPIRECOMMAND001
}
