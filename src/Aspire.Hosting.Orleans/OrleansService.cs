// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Describes an Orleans service.
/// </summary>
[AspireExport]
public sealed class OrleansService
{
    /// <summary>Initializes a new <see cref="OrleansService"/> instance.</summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The service name.</param>
    public OrleansService(IDistributedApplicationBuilder builder, string name)
    {
        Name = name;
        Builder = builder;
        ServiceId = ParameterResourceBuilderExtensions.CreateGeneratedParameter(builder, $"{name}-service-id", secret: false, new GenerateParameterDefault
        {
            Upper = false,
            Special = false,
            MinLength = 25
        });
        ClusterId = ParameterResourceBuilderExtensions.CreateGeneratedParameter(builder, $"{name}-cluster-id", secret: false, new GenerateParameterDefault
        {
            Upper = false,
            Special = false,
            MinLength = 25
        });
    }

    /// <summary>
    /// Gets the name of the service.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the distributed application builder.
    /// </summary>
    public IDistributedApplicationBuilder Builder { get; }

    /// <summary>
    /// Gets or sets the service identifier.
    /// </summary>
    internal object ServiceId { get; set; }

    /// <summary>
    /// Gets or sets the cluster identifier.
    /// </summary>
    internal object ClusterId { get; set; }

    /// <summary>
    /// Gets or sets the clustering provider.
    /// </summary>
    public IProviderConfiguration? Clustering { get; set; }

    /// <summary>
    /// Gets or sets the reminder service provider.
    /// </summary>
    public IProviderConfiguration? Reminders { get; set; }

    /// <summary>
    /// Gets the grain storage providers.
    /// </summary>
    public Dictionary<string, IProviderConfiguration> GrainStorage { get; } = [];

    /// <summary>
    /// Gets the grain directory providers.
    /// </summary>
    public Dictionary<string, IProviderConfiguration> GrainDirectory { get; } = [];

    /// <summary>
    /// Gets the broadcast channel providers.
    /// </summary>
    public Dictionary<string, IProviderConfiguration> BroadcastChannel { get; } = [];

    /// <summary>
    /// Gets the stream providers.
    /// </summary>
    public Dictionary<string, IProviderConfiguration> Streaming { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether to enable tracing of grain calls.
    /// </summary>
    /// <remarks>
    /// Distributed tracing is enabled by default.
    /// </remarks>
    public bool? EnableDistributedTracing { get; set; }
}
