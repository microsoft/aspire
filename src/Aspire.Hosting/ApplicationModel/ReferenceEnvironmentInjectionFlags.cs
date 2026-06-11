// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies which connection or endpoint information should be injected into environment variables when <c>WithReference()</c> is invoked.
/// </summary>
[Flags]
public enum ReferenceEnvironmentInjectionFlags
{
    /// <summary>
    /// No connection information will be injected.
    /// </summary>
    None = 0,

    /// <summary>
    /// The connection string will be injected as an environment variable.
    /// </summary>
    ConnectionString = 1 << 0,

    /// <summary>
    /// Individual connection properties will be injected as environment variables.
    /// </summary>
    ConnectionProperties = 1 << 1,

    /// <summary>
    /// Each endpoint defined on the resource will be injected using the format "services__{resourceName}__{endpointScheme}__{endpointIndex}".
    /// </summary>
    ServiceDiscovery = 1 << 2,

    /// <summary>
    /// Each endpoint defined on the resource will be injected using the format "{RESOURCENAME}_{ENDPOINTNAME}".
    /// </summary>
    Endpoints = 1 << 3,

    /// <summary>
    /// Connection string, connection properties and service endpoints will be injected as environment variables.
    /// </summary>
    All = ConnectionString | ConnectionProperties | ServiceDiscovery | Endpoints
}
