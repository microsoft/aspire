// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.CosmosDB;

namespace Aspire.Hosting.Azure;

internal static class AzureCosmosDBEmulatorConnectionString
{
    public static ReferenceExpression Create(EndpointReference endpoint) =>
        ReferenceExpression.Create($"AccountKey={CosmosConstants.EmulatorAccountKey};AccountEndpoint={endpoint.Property(EndpointProperty.Url)}");
}
