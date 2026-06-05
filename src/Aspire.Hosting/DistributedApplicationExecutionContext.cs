// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Exposes the global contextual information for this invocation of the AppHost.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class DistributedApplicationExecutionContext
{
    /// <summary>
    /// Constructs a <see cref="DistributedApplicationExecutionContext" /> without a callback to retrieve the <see cref="IServiceProvider" />.
    /// </summary>
    /// <param name="operation">The operation being performed in this invocation of the AppHost.</param>
    /// <remarks>
    /// This constructor is used for internal testing purposes.
    /// </remarks>
    public DistributedApplicationExecutionContext(DistributedApplicationOperation operation) : this(operation, "manifest")
    {
    }

    /// <summary>
    /// Constructs a <see cref="DistributedApplicationExecutionContext" /> without a callback to retrieve the <see cref="IServiceProvider" />.
    /// </summary>
    /// <param name="operation">The operation being performed in this invocation of the AppHost.</param>
    /// <param name="publisherName">The name of the publisher being used for the publish operation. This corresponds to publishers added via the <see cref="PublisherDistributedApplicationBuilderExtensions.AddPublisher{TPublisher, TPublisherOptions}(IDistributedApplicationBuilder, string, Action{TPublisherOptions})"/> extension method.</param>
    /// <remarks>
    /// This constructor is used for internal testing purposes.
    /// </remarks>
    public DistributedApplicationExecutionContext(DistributedApplicationOperation operation, string publisherName)
    {
        Operation = operation;
        PublisherName = publisherName;
    }

    /// <summary>
    /// The name of the publisher that is being used if <see cref="Operation"/> is set to <see cref="DistributedApplicationOperation.Publish"/>. 
    /// </summary>
    public string PublisherName { get; set; }

    private readonly DistributedApplicationExecutionContextOptions? _options;

    /// <summary>
    /// Constructs a <see cref="DistributedApplicationExecutionContext" /> with a callback to retrieve the <see cref="IServiceProvider" />.
    /// </summary>
    /// <param name="options">Options for <see cref="DistributedApplicationExecutionContext"/>.</param>
    public DistributedApplicationExecutionContext(DistributedApplicationExecutionContextOptions options) : this(options.Operation, options.PublisherName ?? "manifest")
    {
        _options = options;
    }

    /// <summary>
    /// The operation currently being performed by the AppHost.
    /// </summary>
    public DistributedApplicationOperation Operation { get; }

    /// <summary>
    /// The <see cref="IServiceProvider"/> for the AppHost.
    /// </summary>
    /// <exception cref="InvalidOperationException" accessor="get">Thrown when the <see cref="IServiceProvider"/> is not available.</exception>
    public IServiceProvider ServiceProvider
    {
        get
        {
            if (_options is not { } options)
            {
                throw new InvalidOperationException("IServiceProvider is not available because execution context was not constructed with DistributedApplicationExecutionContextOptions.");
            }

            if (options.ServiceProvider is not { } serviceProvider)
            {
                throw new InvalidOperationException("IServiceProvider is not available because the container has not yet been built.");
            }

            return serviceProvider;
        }
    }

    /// <summary>
    /// Returns true if the current operation is publishing.
    /// </summary>
    public bool IsPublishMode => Operation == DistributedApplicationOperation.Publish;

    /// <summary>
    /// Returns true if the current operation is running.
    /// </summary>
    public bool IsRunMode => Operation == DistributedApplicationOperation.Run;
}
