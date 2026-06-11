// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.CosmosDB;

internal static class CosmosDBEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>cosmosdb/linux/azure-cosmos-emulator</remarks>
    public const string Image = "cosmosdb/linux/azure-cosmos-emulator";

    /// <remarks>stable</remarks>
    public const string Tag = "stable";

    /// <remarks>vnext-preview</remarks>
    public const string TagVNextPreview = "vnext-preview";
}
