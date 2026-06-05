// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREUSERSECRETS001

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// A builder for creating instances of <see cref="DistributedApplication"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="IDistributedApplicationBuilder"/> is the central interface for defining
/// the resources which are orchestrated by the <see cref="DistributedApplication"/> when
/// the app host is launched.
/// </para>
/// <para>
/// To create an instance of the <see cref="IDistributedApplicationBuilder"/> interface
/// developers should use the <see cref="DistributedApplication.CreateBuilder(string[])"/>
/// method. Once the builder is created extension methods which target the <see cref="IDistributedApplicationBuilder"/>
/// interface can be used to add resources to the distributed application.
/// </para>
/// <example>
/// <para>
/// This example shows a distributed application that contains a .NET project (InventoryService) that uses
/// a Redis cache and a PostgreSQL database. The builder is created using the <see cref="DistributedApplication.CreateBuilder(string[])"/>
/// method.
/// </para>
/// <para>
/// The <see href="https://learn.microsoft.com/dotnet/api/aspire.hosting.redisbuilderextensions.addredis">AddRedis</see>
/// and <see href="https://learn.microsoft.com/dotnet/api/aspire.hosting.postgresbuilderextensions.addpostgres">AddPostgres</see>
/// methods are used to add Redis and PostgreSQL container resources. The results of the methods are stored in variables for
/// later use.
/// </para>
///
/// <code lang="csharp">
/// var builder = DistributedApplication.CreateBuilder(args);
/// var cache = builder.AddRedis("cache");
/// var inventoryDatabase = builder.AddPostgres("postgres").AddDatabase("inventory");
/// builder.AddProject&lt;Projects.InventoryService&gt;("inventoryservice")
///        .WithReference(cache)
///        .WithReference(inventory);
/// builder.Build().Run();
/// </code>
/// </example>
/// </remarks>
public interface IDistributedApplicationBuilder
{
    /// <inheritdoc cref="HostApplicationBuilder.Configuration" />
    public ConfigurationManager Configuration { get; }

    /// <summary>
    /// Directory of the project where the app host is located. Defaults to the content root if there's no project.
    /// </summary>
    public string AppHostDirectory { get; }

    /// <summary>
    /// Assembly of the app host project.
    /// </summary>
    public Assembly? AppHostAssembly { get; }

    /// <inheritdoc cref="HostApplicationBuilder.Environment" />
    public IHostEnvironment Environment { get; }

    /// <inheritdoc cref="HostApplicationBuilder.Services" />
    public IServiceCollection Services { get; }

    /// <summary>
    /// Eventing infrastructure for AppHost lifecycle.
    /// </summary>
    public IDistributedApplicationEventing Eventing { get; }

    /// <summary>
    /// Execution context for this invocation of the AppHost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="ExecutionContext"/> property provides access key information about the context
    /// in which the distributed application is running. The most important properties that
    /// the <see cref="DistributedApplicationExecutionContext" /> provides is the
    /// <see cref="DistributedApplicationExecutionContext.IsPublishMode"/> and <see cref="DistributedApplicationExecutionContext.IsRunMode"/>
    /// properties. Developers building .NET Aspire based applications may whish to change the application
    /// model depending on whether they are running locally, or whether they are publishing to the cloud.
    /// </para>
    /// <example>
    /// <para>
    /// An example of using the <see cref="DistributedApplicationExecutionContext.IsRunMode"/> property on the <see cref="IDistributedApplicationBuilder"/> via
    /// the <see cref="IResourceBuilder{T}.ApplicationBuilder"/>. In this case an extension method is used to generate a stable node name for RabbitMQ for local
    /// development runs.
    /// </para>
    /// <code lang="csharp">
    /// private static IResourceBuilder&lt;RabbitMQServerResource&gt; RunWithStableNodeName(this IResourceBuilder&lt;RabbitMQServerResource&gt; builder)
    /// {
    ///     if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
    ///     {
    ///         builder.WithEnvironment(context =>
    ///         {
    ///             // Set a stable node name so queue storage is consistent between sessions
    ///             var nodeName = $"{builder.Resource.Name}@localhost";
    ///             context.EnvironmentVariables["RABBITMQ_NODENAME"] = nodeName;
    ///         });
    ///     }
    ///
    ///     return builder;
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public DistributedApplicationExecutionContext ExecutionContext { get; }

    /// <summary>
    /// Gets the collection of resources for the distributed application.
    /// </summary>
    /// <remarks>
    /// This can be mutated by adding more resources, which will update its current view.
    /// </remarks>
    public IResourceCollection Resources { get; }

    /// <summary>
    /// Gets the deployment pipeline for this distributed application.
    /// </summary>
    /// <remarks>
    /// The pipeline allows adding custom deployment steps that execute during the deploy process.
    /// Steps can declare dependencies on other steps to control execution order.
    /// </remarks>
    [Experimental("ASPIREPIPELINES001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public IDistributedApplicationPipeline Pipeline { get; }

    /// <summary>
    /// Gets the service for managing Aspire file system operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="FileSystemService"/> provides a centralized way to manage temporary files and directories
    /// used by Aspire, enabling testability and consistent temp file management.
    /// </para>
    /// <para>
    /// Resources and infrastructure code should use this service instead of static methods like
    /// <see cref="Path.GetTempPath"/> or <see cref="Directory.CreateTempSubdirectory(string?)"/> to ensure
    /// consistent directory management across the application.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREFILESYSTEM001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public IFileSystemService FileSystemService => throw new NotImplementedException();

    /// <summary>
    /// Gets the service for managing user secrets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="UserSecretsManager"/> provides a centralized way to manage user secrets
    /// used by Aspire, enabling testability and consistent secret management.
    /// </para>
    /// </remarks>
    [Experimental("ASPIREUSERSECRETS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public IUserSecretsManager UserSecretsManager => throw new NotImplementedException();

    /// <summary>
    /// Adds a resource of type <typeparamref name="T"/> to the distributed application.
    /// </summary>
    /// <typeparam name="T">The type of resource to add.</typeparam>
    /// <param name="resource">The resource to add.</param>
    /// <returns>A builder for configuring the added resource.</returns>
    /// <exception cref="DistributedApplicationException">Thrown when a resource with the same name already exists.</exception>
    /// <remarks>
    /// <para>
    /// The <see cref="AddResource{T}(T)"/> method is not typically used directly by developers building
    /// Aspire-based applications. It is typically used by developers building extensions to Aspire and is
    /// called from within an extension method to add a custom resource to the application model.
    /// </para>
    /// <example>
    /// This example shows the implementation of the <see cref="ContainerResourceBuilderExtensions.AddContainer(IDistributedApplicationBuilder, string, string)"/>
    /// method which makes use of the <see cref="AddResource{T}(T)"/> method to add a container resource to the application. In .NET Aspire
    /// the pattern for defining new resources is to include a method that extends <see cref="IDistributedApplicationBuilder"/> and and then
    /// constructs a resource derived from <see cref="IResource"/> and adds it to the application model using the <see cref="AddResource{T}(T)"/>
    /// method. Other extension methods (such as <see cref="ContainerResourceBuilderExtensions.WithImage{T}(IResourceBuilder{T}, string, string)"/>
    /// in this case) can be chained to configure the resource as desired.
    /// <code lang="csharp">
    /// public static IResourceBuilder&lt;ContainerResource&gt; AddContainer(this IDistributedApplicationBuilder builder, [ResourceName] string name, string image, string tag)
    /// {
    ///     var container = new ContainerResource(name);
    ///     return builder.AddResource(container)
    ///                   .WithImage(image, tag);
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    IResourceBuilder<T> AddResource<T>(T resource) where T : IResource;

    /// <summary>
    /// Creates a new resource builder based on an existing resource.
    /// </summary>
    /// <typeparam name="T">Type of resource.</typeparam>
    /// <param name="resource">An existing resource.</param>
    /// <returns>A resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The <see cref="CreateResourceBuilder{T}(T)"/> method is used to create an <see cref="IResourceBuilder{T}"/> for a specific
    /// resource where the original resource builder cannot be referenced. This does not create a new resource, but instead returns
    /// a resource builder for an existing resource.
    /// </para>
    /// <para>
    /// This method is typically used when building extensions to .NET Aspire where the original resource builder cannot be
    /// referenced directly. Using the <see cref="CreateResourceBuilder{T}(T)"/> method allows for easier mutation of resources
    /// within the application model.
    /// </para>
    /// <para>
    /// Calling extension methods on the <see cref="IResourceBuilder{T}"/> typically results in modifications to the <see cref="IResource.Annotations"/>
    /// collection. Not all changes to annotations will be effective depending on what stage of the lifecycle the app host is in. See <see cref="IDistributedApplicationLifecycleHook"/>
    /// for more details.
    /// </para>
    /// <example>
    /// <para>
    /// The following example shows the implementation of the <see cref="ParameterResourceBuilderExtensions.AddConnectionString(IDistributedApplicationBuilder, string, string?)"/>
    /// extension method.
    /// </para>
    /// <para>
    /// The <see cref="ParameterResourceBuilderExtensions.AddConnectionString(IDistributedApplicationBuilder, string, string?)" /> method creates a new
    /// <see cref="ParameterResource"/> in the application model. The return type of <see cref="ParameterResourceBuilderExtensions.AddConnectionString(IDistributedApplicationBuilder, string, string?)"/>
    /// is <see cref="IResourceBuilder{IResourceWithConnectionString}"/>. The <see cref="ParameterResource"/> type does not implement the <see cref="IResourceWithConnectionString"/>.
    /// </para>
    /// <para>
    /// To work around this issue the <see cref="ParameterResourceBuilderExtensions.AddConnectionString(IDistributedApplicationBuilder, string, string?)"/> method wraps the
    /// parameter resource in a "surrogate" class which proxies access to the <see cref="ParameterResource"/> fields but implements <see cref="IResourceWithConnectionString"/>. The
    /// <see cref="CreateResourceBuilder{T}(T)"/> method assists by allowing the creation of a <see cref="IResourceBuilder{T}"/> without adding
    /// another resource to the application model.
    /// </para>
    /// <code lang="csharp">
    /// public static IResourceBuilder&lt;IResourceWithConnectionString&gt; AddConnectionString(this IDistributedApplicationBuilder builder, [ResourceName] string name, string? environmentVariableName = null)
    /// {
    ///     var parameterBuilder = builder.AddParameter(name, _ =>
    ///     {
    ///         return builder.Configuration.GetConnectionString(name) ?? throw new DistributedApplicationException($"Connection string parameter resource could not be used because connection string '{name}' is missing.");
    ///     },
    ///     secret: true,
    ///     connectionString: true);
    ///
    ///     var surrogate = new ConnectionStringParameterResource(parameterBuilder.Resource, environmentVariableName);
    ///     return builder.CreateResourceBuilder(surrogate);
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    IResourceBuilder<T> CreateResourceBuilder<T>(T resource) where T : IResource;

    /// <summary>
    /// Builds and returns a new <see cref="DistributedApplication"/> instance. This can only be called once.
    /// </summary>
    /// <returns>A new <see cref="DistributedApplication"/> instance.</returns>
    /// <remarks>
    /// <para>
    /// Callers of the <see cref="Build"/> method should only call it once. are responsible for the lifecycle of the
    /// <see cref="DistributedApplication"/> instance that is returned. Note that the <see cref="DistributedApplication"/>
    /// type implements <see cref="IDisposable" /> and should be disposed of when it is no longer needed. Note that in
    /// many templates and samples Dispose is omitted for brevity because in those cases the instance is destroyed
    /// when the process exists.
    /// </para>
    /// </remarks>
    DistributedApplication Build();
}
