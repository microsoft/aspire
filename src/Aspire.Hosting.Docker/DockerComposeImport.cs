// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker;

/// <summary>
/// Configures replacement of imported Compose services with existing Aspire resources.
/// </summary>
/// <remarks>
/// Replacements inherit environment and dependencies, not the image, command or mounts.
/// Define an endpoint named <c>port{target}</c> for every Compose port on the replacement.
/// </remarks>
public sealed class DockerComposeImportOptions
{
    internal Dictionary<string, IResourceWithEndpoints> Replacements { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Replaces a Compose service with a resource already added to this AppHost.
    /// </summary>
    /// <param name="serviceName">The case-sensitive Compose service name.</param>
    /// <param name="resource">The replacement resource.</param>
    /// <remarks>
    /// <code>
    /// options.ReplaceService("api", builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithHttpEndpoint(name: "port8080"));
    /// </code>
    /// </remarks>
    public void ReplaceService<T>(string serviceName, IResourceBuilder<T> resource)
        where T : IResourceWithEndpoints, IResourceWithEnvironment, IResourceWithWaitSupport
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(resource);
        Replacements.Add(serviceName, resource.Resource);
    }
}

/// <summary>
/// The native Aspire resources imported from a Compose file.
/// </summary>
/// <remarks>Docker Compose normalizes configuration; Aspire and DCP own execution and cleanup.</remarks>
public sealed class DockerComposeImport
{
    private readonly IDistributedApplicationBuilder _builder;
    private readonly IReadOnlyDictionary<string, IResourceWithEndpoints> _services;

    internal DockerComposeImport(IDistributedApplicationBuilder builder, IReadOnlyDictionary<string, IResourceWithEndpoints> services)
    {
        _builder = builder;
        _services = services;
    }

    /// <summary>
    /// Gets the imported service, including any replacement.
    /// </summary>
    /// <param name="serviceName">The case-sensitive Compose service name.</param>
    /// <returns>The native resource builder.</returns>
    /// <remarks>
    /// <code>
    /// var endpoint = compose.GetService("gateway").GetEndpoint("port8080");
    /// </code>
    /// </remarks>
    public IResourceBuilder<IResourceWithEndpoints> GetService(string serviceName) =>
        _builder.CreateResourceBuilder(_services[serviceName]);
}
