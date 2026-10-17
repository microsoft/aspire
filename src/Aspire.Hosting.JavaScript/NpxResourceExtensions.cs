// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // WithLaunchToolArgs is experimental.
#pragma warning disable ASPIRECOMMAND001 // RequiredCommandAnnotation is for evaluation purposes only.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;

namespace Aspire.Hosting;

public static partial class JavaScriptHostingExtensions
{
    /// <summary>
    /// Adds a resource that runs a command from an npm package using npx.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="packageName">The name of the npm package to execute.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> AddNpxApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string packageName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = new NpxResource(name, packageName);

        return builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Npx",
                Properties = []
            })
            .WithIconName("Toolbox")
            .WithRequiredCommand("npx", NpmHelpLink)
            .WithRequiredCommand("node", NodeHelpLink)
            .WithLaunchToolArgs(context => AddNpxLaunchArguments(context, resource));
    }

    /// <summary>
    /// Sets the npm package to execute.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <param name="packageName">The name of the npm package to execute.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxPackage(
        this IResourceBuilder<NpxResource> builder,
        string packageName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        var (name, version) = NpxResource.ParsePackageSpec(packageName);
        builder.Resource.Configuration.PackageName = name;
        builder.Resource.Configuration.Version = version;
        return builder;
    }

    /// <summary>
    /// Sets the package version or distribution tag to execute.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <param name="version">The package version or distribution tag.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxVersion(
        this IResourceBuilder<NpxResource> builder,
        string version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        builder.Resource.Configuration.Version = version;
        return builder;
    }

    /// <summary>
    /// Selects the executable to run from the package.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <param name="executable">The package executable name.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxExecutable(
        this IResourceBuilder<NpxResource> builder,
        string executable)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        builder.Resource.Configuration.Executable = executable;
        return builder;
    }

    /// <summary>
    /// Sets the npm registry used to resolve the package.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <param name="registry">The npm registry URL.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxRegistry(
        this IResourceBuilder<NpxResource> builder,
        string registry)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(registry);

        builder.Resource.Configuration.Registry = registry;
        return builder;
    }

    /// <summary>
    /// Forces npx to use only data already available in its cache.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxOffline(this IResourceBuilder<NpxResource> builder)
        => SetNpxCachePreference(builder, NpxCachePreference.Offline);

    /// <summary>
    /// Prefers cached package data while allowing npx to fetch data that is not cached.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxPreferOffline(this IResourceBuilder<NpxResource> builder)
        => SetNpxCachePreference(builder, NpxCachePreference.PreferOffline);

    /// <summary>
    /// Forces npx to check the registry for fresh package data.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxPreferOnline(this IResourceBuilder<NpxResource> builder)
        => SetNpxCachePreference(builder, NpxCachePreference.PreferOnline);

    /// <summary>
    /// Adds raw arguments to npx before the package selection.
    /// </summary>
    /// <param name="builder">The npx resource builder.</param>
    /// <param name="args">Arguments passed to npx before the package selection.</param>
    /// <returns>The resource builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<NpxResource> WithNpxArgs(
        this IResourceBuilder<NpxResource> builder,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Copy the caller's array now; the arguments are evaluated later when the process starts or the manifest is published.
        builder.Resource.Configuration.NpxArgs.AddRange(args ?? []);
        return builder;
    }

    private static IResourceBuilder<NpxResource> SetNpxCachePreference(
        IResourceBuilder<NpxResource> builder,
        NpxCachePreference preference)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.Configuration.CachePreference = preference;
        return builder;
    }

    private static Task AddNpxLaunchArguments(CommandLineArgsCallbackContext context, NpxResource resource)
    {
        var configuration = resource.Configuration;

        // npx parses its options before the package positional argument. Keep the options here so the
        // user's WithArgs values are always forwarded to the package executable after package selection.
        context.Args.Add("--yes");

        if (configuration.Registry is not null)
        {
            context.Args.Add("--registry");
            context.Args.Add(configuration.Registry);
        }

        var cacheArgument = configuration.CachePreference switch
        {
            NpxCachePreference.Offline => "--offline",
            NpxCachePreference.PreferOffline => "--prefer-offline",
            NpxCachePreference.PreferOnline => "--prefer-online",
            _ => null
        };

        if (cacheArgument is not null)
        {
            context.Args.Add(cacheArgument);
        }

        foreach (var arg in configuration.NpxArgs)
        {
            context.Args.Add(arg);
        }

        var packageSpec = configuration.Version is null
            ? configuration.PackageName
            : $"{configuration.PackageName}@{configuration.Version}";

        if (configuration.Executable is null)
        {
            context.Args.Add(packageSpec);
        }
        else
        {
            context.Args.Add("--package");
            context.Args.Add(packageSpec);
            context.Args.Add(configuration.Executable);
        }

        context.Args.Add("--");

        return Task.CompletedTask;
    }
}

#pragma warning restore ASPIREEXTENSION001
#pragma warning restore ASPIRECOMMAND001
