// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.ServiceBus;

/// <summary>
/// Represents an annotation holding the resolved SQL Server connection inputs for the Azure Service Bus emulator.
/// </summary>
internal sealed class SqlServerConnectionAnnotation : IResourceAnnotation
{
    private readonly Func<ParameterResource> _passwordResolver;

    // The password is resolved lazily because SqlServerServerResource.PasswordParameter can be replaced
    // after this annotation is created (e.g. by a later WithPassword call on the SQL Server resource).
    public SqlServerConnectionAnnotation(EndpointReference endpoint, Func<ParameterResource> passwordResolver)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _passwordResolver = passwordResolver ?? throw new ArgumentNullException(nameof(passwordResolver));
    }

    public EndpointReference Endpoint { get; }

    public ParameterResource Password => _passwordResolver();
}
