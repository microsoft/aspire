// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Configuration options and references that need to be exposed to the <see cref="DistributedApplicationExecutionContext"/>.
/// </summary>
/// <ats-summary>Configuration options and references that need to be exposed to the <ats-see cref="!:type:DistributedApplicationExecutionContext" />.</ats-summary>
[AspireExport]
public class DistributedApplicationExecutionContextOptions
{
    /// <summary>
    /// Constructs a <see cref="DistributedApplicationExecutionContextOptions" />.
    /// </summary>
    /// <param name="operation">Indicates whether the AppHost is running in Publish mode or Run mode.</param>
    public DistributedApplicationExecutionContextOptions(DistributedApplicationOperation operation)
    {
        this.Operation = operation;
    }

    /// <summary>
    /// Constructs a <see cref="DistributedApplicationExecutionContextOptions" />.
    /// </summary>
    /// <param name="operation">Indicates whether the AppHost is running in Publish mode or Run mode.</param>
    /// <param name="publisherName">The publisher name if in Publish mode.</param>
    public DistributedApplicationExecutionContextOptions(DistributedApplicationOperation operation, string publisherName)
    {
        this.Operation = operation;
        this.PublisherName = publisherName;
    }

    /// <summary>
    /// The <see cref="IServiceProvider"/> for the AppHost.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; set; }

    /// <summary>
    /// The operation currently being performed by the AppHost.
    /// </summary>
    public DistributedApplicationOperation Operation { get; }

    /// <summary>
    /// The name of the publisher if running in publish mode.
    /// </summary>
    public string? PublisherName { get; }
}
