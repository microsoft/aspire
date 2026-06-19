// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal interface IAzureFunctionsResource : IResource
{
    AzureStorageResource? HostStorage { get; set; }
}

/// <summary>
/// Specifies the authoring language used by an Azure Functions app.
/// </summary>
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
[AspireExport(ExposeProperties = true)]
public class AzureFunctionsAppResource(string name, string command, string appDirectory, AzureFunctionsLanguage language)
    : ExecutableResource(name, command, appDirectory), IResourceWithServiceDiscovery, IAzureFunctionsResource, IResourceWithCustomWithReference<AzureFunctionsAppResource>
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
