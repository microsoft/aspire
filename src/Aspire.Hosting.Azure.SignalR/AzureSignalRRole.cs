// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Azure SignalR roles.
/// </summary>
internal enum AzureSignalRRole
{
    /// <summary>
    /// Allows managing Azure SignalR resources.
    /// </summary>
    SignalRContributor,

    /// <summary>
    /// Allows acting as an app server for SignalR.
    /// </summary>
    SignalRAppServer,

    /// <summary>
    /// Allows using REST API for SignalR in serverless mode.
    /// </summary>
    SignalRRestApiOwner,
}
