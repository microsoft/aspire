// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Adds a connection string to the distributed application a resource with the specified expression.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="connectionStringExpression">The connection string expression.</param>
public sealed class ConnectionStringResource(string name, ReferenceExpression connectionStringExpression) : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport
{
    /// <summary>
    /// Describes the connection string format string used for this resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => connectionStringExpression;
}
