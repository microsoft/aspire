// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Storage;

internal static class StorageEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>azure-storage/azurite</remarks>
    public const string Image = "azure-storage/azurite";

    /// <remarks>3.37.0</remarks>
    public const string Tag = "3.37.0";
}
