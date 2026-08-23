// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Docker.Resources.ServiceNodes;

/// <summary>
/// Represents the build configuration for a service within a Docker Compose file.
/// This class is used to define various build parameters such as context, dockerfile,
/// arguments, target stages, cache sources, and labels.
/// </summary>
[YamlSerializable]
public sealed class Build
{
    /// <summary>
    /// Gets or sets the build context for the service in the Docker Compose file.
    /// The context specifies the directory containing the Dockerfile and other resources
    /// needed for building the image.
    /// </summary>
    [YamlMember(Alias = "context")]
    public string? Context { get; set; }

    /// <summary>
    /// Specifies the path to the Dockerfile used for building the Docker image.
    /// This property points to the Dockerfile that contains the instructions
    /// for building the service image in a Docker Compose configuration.
    /// </summary>
    [YamlMember(Alias = "dockerfile")]
    public string? Dockerfile { get; set; }

    /// <summary>
    /// Gets or sets a dictionary of build arguments for the Docker image.
    /// Build arguments provide values that can be passed to the Dockerfile during the build process.
    /// These arguments allow customization of the build process by defining key-value pairs
    /// that are accessible within the Dockerfile.
    /// </summary>
    [YamlMember(Alias = "args", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Args { get; set; } = [];

    /// <summary>
    /// Specifies the target build stage to be used from a multi-stage Dockerfile.
    /// This property allows defining a specific target stage name, enabling partial builds
    /// and optimizing the build process by selecting only the required build stage.
    /// </summary>
    [YamlMember(Alias = "target")]
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets a list of cache sources to be used during the build process.
    /// This property corresponds to the "cache_from" field in a Docker Compose
    /// build configuration and allows specifying external images or sources
    /// to use as a cache for layers during Docker image builds.
    /// </summary>
    [YamlMember(Alias = "cache_from", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> CacheFrom { get; set; } = [];

    /// <summary>
    /// Gets or sets a list of cache export destinations for the build process.
    /// This property corresponds to the "cache_to" field in a Docker Compose
    /// build configuration and allows exporting built layers as cache entries
    /// so that subsequent builds can consume them via <see cref="CacheFrom"/>.
    /// </summary>
    /// <value>
    /// A list of cache export definitions in the Compose long syntax, for example
    /// "type=registry,ref=myregistry/myimage:cache,mode=max".
    /// </value>
    /// <remarks>
    /// Exporting cache to a registry requires a BuildKit-enabled builder
    /// (such as Docker Buildx); the legacy builder ignores this field.
    /// See the <see href="https://compose-spec.readthedocs.io/en/latest/build.html#cache_to">Compose Build specification</see>.
    /// </remarks>
    /// <example>
    /// Configure registry cache round-trips for a published service:
    /// <code>
    /// builder.AddProject&lt;Projects.MyApi&gt;("api")
    ///     .PublishAsDockerComposeService((resource, service) =>
    ///     {
    ///         service.Build.CacheTo.Add("type=registry,ref=cr.example.com/api:cache");
    ///     });
    /// </code>
    /// </example>
    [YamlMember(Alias = "cache_to", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> CacheTo { get; set; } = [];

    /// <summary>
    /// Gets or sets the collection of additional labels to be applied to the build.
    /// Labels are key-value pairs that provide metadata about the build.
    /// </summary>
    [YamlMember(Alias = "labels", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public Dictionary<string, string> Labels { get; set; } = [];
}
