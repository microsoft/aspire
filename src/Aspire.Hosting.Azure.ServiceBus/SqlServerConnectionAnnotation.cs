// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.ServiceBus;

/// <summary>
/// Represents an annotation holding the SQL Server resource the Azure Service Bus emulator stores its state in.
/// </summary>
internal sealed class SqlServerConnectionAnnotation : IResourceAnnotation
{
    public SqlServerConnectionAnnotation(SqlServerServerResource sqlServer, IReadOnlyList<IResourceAnnotation> waitAnnotations)
    {
        SqlServer = sqlServer ?? throw new ArgumentNullException(nameof(sqlServer));
        WaitAnnotations = waitAnnotations ?? throw new ArgumentNullException(nameof(waitAnnotations));
    }

    public SqlServerServerResource SqlServer { get; }

    // Track only waits added by WithSqlServer so replacing the connection does not remove waits
    // the user independently added for the previous SQL Server resource.
    public IReadOnlyList<IResourceAnnotation> WaitAnnotations { get; }

    public EndpointReference Endpoint => SqlServer.PrimaryEndpoint;

    // The password is read lazily because SqlServerServerResource.PasswordParameter can be replaced after
    // this annotation is created (e.g. by a later WithPassword call on the SQL Server resource).
    public ParameterResource Password => SqlServer.PasswordParameter;
}
