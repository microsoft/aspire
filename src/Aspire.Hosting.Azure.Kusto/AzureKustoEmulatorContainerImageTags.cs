// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Container image configuration for the Kusto emulator.
/// </summary>
internal static class AzureKustoEmulatorContainerImageTags
{
    /// <summary>
    /// The container registry hosting the Kusto emulator image.
    /// </summary>
    /// <remarks>mcr.microsoft.com</remarks>
    public static string Registry { get; } = "mcr.microsoft.com";

    /// <summary>
    /// The Kusto emulator container image name.
    /// </summary>
    /// <remarks>azuredataexplorer/kustainer-linux</remarks>
    public static string Image { get; } = "azuredataexplorer/kustainer-linux";

    /// <summary>
    /// The tag for the Kusto emulator container image.
    /// </summary>
    /// <remarks>2026.03.16.1116-2611-994a3c9-master</remarks>
    public static string Tag { get; } = "2026.03.16.1116-2611-994a3c9-master";
}
