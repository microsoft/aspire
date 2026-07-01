// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.ServiceBus;

/// <summary>
/// Represents an annotation holding the resolved SQL Server connection inputs for the Azure Service Bus emulator.
/// </summary>
internal sealed class SqlServerConnectionAnnotation : IResourceAnnotation
{
    public SqlServerConnectionAnnotation(EndpointReference endpoint, ParameterResource password)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public EndpointReference Endpoint { get; }

    public ParameterResource Password { get; }
}
