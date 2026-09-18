// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Indicates that a RabbitMQ resource exposes queue-specific arguments such as message TTL,
/// length limits, and dead-letter routing.
/// </summary>
public interface IResourceWithQueueArguments : IResource
{
    /// <summary>
    /// Gets the queue arguments for this resource.
    /// </summary>
    RabbitMQQueueArguments QueueArguments { get; }
}
