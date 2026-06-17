// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Azure Service Bus roles.
/// </summary>
internal enum AzureServiceBusRole
{
    AzureServiceBusDataOwner,
    AzureServiceBusDataReceiver,
    AzureServiceBusDataSender,
}
