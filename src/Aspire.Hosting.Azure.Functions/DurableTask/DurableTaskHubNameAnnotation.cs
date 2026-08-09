// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Annotation that supplies the name for an existing Durable Task hub resource.
/// </summary>
/// <param name="hubName">The name of the existing Durable Task hub.</param>
internal sealed class DurableTaskHubNameAnnotation(object hubName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the existing Durable Task hub.
    /// </summary>
    public object HubName { get; } = hubName;
}
