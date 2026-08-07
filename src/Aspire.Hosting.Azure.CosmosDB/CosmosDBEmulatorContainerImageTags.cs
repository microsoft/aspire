// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.CosmosDB;

internal static class CosmosDBEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>cosmosdb/linux/azure-cosmos-emulator</remarks>
    public const string Image = "cosmosdb/linux/azure-cosmos-emulator";

    // This is the Linux-based (vNext) emulator, which became generally available in June 2026.
    // It replaces the original "stable" (2.14.x) image, which is no longer published: that image has a
    // 180-day evaluation period compiled into the binary, measured from when Microsoft pushes the image to
    // MCR rather than from when it is pulled. Once it lapses the container prints
    // "Error: The evaluation period has expired." followed by "PAL initialization failed. Error: 104" and
    // exits with code 1, so every consumer breaks at once. See https://github.com/microsoft/aspire/issues/18898.
    /// <remarks>vnext-latest</remarks>
    public const string Tag = "vnext-latest";
}
