// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace Aspire.Hosting.Azure;

internal interface IAzureFunctionsResource : IResource
{
    AzureStorageResource? HostStorage { get; set; }
}

/// <summary>
/// Represents an SDK-style Azure Functions project built and launched using the .NET project infrastructure.
/// </summary>
/// <param name="name">The resource name used within the distributed application.</param>
/// <param name="workingDirectory">The default working directory containing the Functions project.</param>
/// <remarks>
/// Use <see cref="AzureFunctionsProjectResourceExtensions.AddAzureFunctions"/> to register the project,
/// configure host storage and endpoints, and enable coordinated builds and SDK container publishing.
/// Local execution starts Azure Functions Core Tools, which hosts the isolated .NET worker.
/// Constructing this resource directly does not register it or configure those behaviors.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class AzureFunctionsResource(string name, string workingDirectory)
    : DotnetProjectResource(name, workingDirectory), IAzureFunctionsResource, IResourceWithCustomWithReference<AzureFunctionsResource>
{
    internal AzureStorageResource? HostStorage { get; set; }

    AzureStorageResource? IAzureFunctionsResource.HostStorage
    {
        get => HostStorage;
        set => HostStorage = value;
    }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<AzureFunctionsResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (builder is not IResourceBuilder<AzureFunctionsResource> functionsBuilder)
        {
            return null;
        }

        return (IResourceBuilder<TDestination>?)global::Aspire.Hosting.AzureFunctionsProjectResourceExtensions.TryWithReference(functionsBuilder, source, connectionName, optional, name);
    }
}

/// <summary>
/// Specifies the authoring language used by an Azure Functions app.
/// </summary>
[Experimental("ASPIREAZUREFUNCTIONS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum AzureFunctionsLanguage
{
    /// <summary>
    /// A TypeScript Azure Functions app that runs on the Node language worker.
    /// </summary>
    TypeScript,

    /// <summary>
    /// A JavaScript Azure Functions app that runs on the Node language worker.
    /// </summary>
    JavaScript
}

/// <summary>
/// Represents an Azure Functions project resource within the Aspire hosting environment.
/// </summary>
/// <remarks>
/// This class is used to define and manage the configuration of an Azure Functions project,
/// including its associated host storage. We create a strongly-typed resource for the Azure Functions
/// to support Functions-specific customizations, like the mapping of connection strings and configurations
/// for host storage.
/// </remarks>
/// <param name="name">The resource name used within the distributed application.</param>
[Obsolete("Use AzureFunctionsResource and AddAzureFunctions instead. This legacy resource retains ProjectResource compatibility.")]
public class AzureFunctionsProjectResource(string name) : ProjectResource(name), IAzureFunctionsResource, IResourceWithCustomWithReference<AzureFunctionsProjectResource>
{
    internal AzureStorageResource? HostStorage { get; set; }

    AzureStorageResource? IAzureFunctionsResource.HostStorage
    {
        get => HostStorage;
        set => HostStorage = value;
    }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<AzureFunctionsProjectResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (builder is not IResourceBuilder<AzureFunctionsProjectResource> functionsBuilder)
        {
            return null;
        }

        return (IResourceBuilder<TDestination>?)global::Aspire.Hosting.AzureFunctionsProjectResourceExtensions.TryWithReference(functionsBuilder, source, connectionName, optional, name);
    }
}

/// <summary>
/// Represents an Azure Functions app resource that is launched from a source directory.
/// </summary>
/// <remarks>
/// This resource is intended for Azure Functions apps that are not represented by a .NET project file,
/// such as TypeScript or JavaScript Functions apps that run on the Node language worker.
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="command">The command used to start the local Azure Functions app.</param>
/// <param name="appDirectory">The directory that contains the Azure Functions app.</param>
/// <param name="language">The authoring language used by the Azure Functions app.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="appDirectory"/> is null.</exception>
/// <exception cref="ArgumentException">Thrown when <paramref name="appDirectory"/> is empty.</exception>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREAZUREFUNCTIONS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class AzureFunctionsAppResource(string name, string command, string appDirectory, AzureFunctionsLanguage language)
    : ExecutableResource(name, command, ValidateAppDirectory(appDirectory)), IResourceWithServiceDiscovery, IAzureFunctionsResource, IResourceWithCustomWithReference<AzureFunctionsAppResource>
{
    /// <summary>
    /// Gets the directory that contains the Azure Functions app.
    /// </summary>
    public string AppDirectory => WorkingDirectory;

    /// <summary>
    /// Gets the authoring language used by the Azure Functions app.
    /// </summary>
    public AzureFunctionsLanguage Language { get; } = IsSupportedLanguage(language) ? language : throw new ArgumentOutOfRangeException(nameof(language));

    /// <summary>
    /// Gets the Azure Functions worker runtime for the app.
    /// </summary>
    public string WorkerRuntime => GetWorkerRuntime(Language);

    internal AzureStorageResource? HostStorage { get; set; }

    AzureStorageResource? IAzureFunctionsResource.HostStorage
    {
        get => HostStorage;
        set => HostStorage = value;
    }

    internal static string GetWorkerRuntime(AzureFunctionsLanguage language) => language switch
    {
        AzureFunctionsLanguage.TypeScript or AzureFunctionsLanguage.JavaScript => "node",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    private static bool IsSupportedLanguage(AzureFunctionsLanguage language) => language is AzureFunctionsLanguage.TypeScript or AzureFunctionsLanguage.JavaScript;

    private static string ValidateAppDirectory(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);

        return appDirectory;
    }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<AzureFunctionsAppResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (builder is not IResourceBuilder<AzureFunctionsAppResource> functionsBuilder)
        {
            return null;
        }

        return (IResourceBuilder<TDestination>?)global::Aspire.Hosting.AzureFunctionsProjectResourceExtensions.TryWithReference(functionsBuilder, source, connectionName, optional, name);
    }
}
