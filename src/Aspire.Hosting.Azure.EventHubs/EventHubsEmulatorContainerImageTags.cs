// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.EventHubs;

internal static class EventHubsEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>azure-messaging/eventhubs-emulator</remarks>
    public const string Image = "azure-messaging/eventhubs-emulator";

    /// <remarks>2.2.0</remarks>
    public const string Tag = "2.2.0";
}
