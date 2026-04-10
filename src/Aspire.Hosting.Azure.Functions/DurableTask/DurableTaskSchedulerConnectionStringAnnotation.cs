// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Annotation that supplies the connection string for an existing Durable Task scheduler resource.
/// </summary>
/// <param name="connectionString">The connection string of the existing Durable Task scheduler.</param>
internal sealed class DurableTaskSchedulerConnectionStringAnnotation(object connectionString) : IResourceAnnotation
{
    /// <summary>
    /// Gets the connection string of the existing Durable Task scheduler.
    /// </summary>
    public object ConnectionString { get; } = connectionString;
}
