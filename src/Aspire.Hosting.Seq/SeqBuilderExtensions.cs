// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Seq server resources to the application model.
/// </summary>
public static class SeqBuilderExtensions
{
    // The path within the container in which Seq stores its data
    const string SeqContainerDataDirectory = "/data";

    /// <summary>
    /// Adds a Seq server resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="SeqContainerImageTags.Tag"/> tag of the <inheritdoc cref="SeqContainerImageTags.Image"/> container image.
    /// This overload is not available in polyglot app hosts. Use <see cref="AddSeq(IDistributedApplicationBuilder, string, IResourceBuilder{ParameterResource}, int?)"/> instead.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name to give the resource.</param>
    /// <param name="port">The host port for the Seq server.</param>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with optional adminPassword parameter instead.")]
    public static IResourceBuilder<SeqResource> AddSeq(
        this IDistributedApplicationBuilder builder,
        string name,
        int? port = null)
    {
        return AddSeq(builder, name, adminPassword: null, port: port);
    }

    /// <summary>
    /// Adds a Seq server resource to the application model with authentication enabled. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="SeqContainerImageTags.Tag"/> tag of the <inheritdoc cref="SeqContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name to give the resource.</param>
    /// <param name="adminPassword">The admin password for Seq. If not provided, authentication will be disabled.</param>
    /// <param name="port">The host port for the Seq server.</param>
    [AspireExport("addSeq", Description = "Adds a Seq server container resource")]
    public static IResourceBuilder<SeqResource> AddSeq(
        this IDistributedApplicationBuilder builder,
        string name,
        IResourceBuilder<ParameterResource>? adminPassword,
        int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var seqResource = new SeqResource(name);
        var resourceBuilder = builder.AddResource(seqResource)
            .WithHttpEndpoint(port: port, targetPort: 80, name: SeqResource.PrimaryEndpointName)
            .WithImage(SeqContainerImageTags.Image, SeqContainerImageTags.Tag)
            .WithImageRegistry(SeqContainerImageTags.Registry)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithHttpHealthCheck("/health"); // Add health check for Seq's /health endpoint

        if (adminPassword != null)
        {
            resourceBuilder.WithEnvironment("SEQ_FIRSTRUN_ADMINPASSWORD", adminPassword);
        }
        else
        {
            resourceBuilder.WithEnvironment("SEQ_FIRSTRUN_NOAUTHENTICATION", "True");
        }

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Seq container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataVolume", Description = "Adds a data volume for Seq")]
    public static IResourceBuilder<SeqResource> WithDataVolume(this IResourceBuilder<SeqResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), SeqContainerDataDirectory, isReadOnly);
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Seq container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withDataBindMount", Description = "Adds a data bind mount for Seq")]
    public static IResourceBuilder<SeqResource> WithDataBindMount(this IResourceBuilder<SeqResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        return builder.WithBindMount(source, SeqContainerDataDirectory, isReadOnly);
    }
}
