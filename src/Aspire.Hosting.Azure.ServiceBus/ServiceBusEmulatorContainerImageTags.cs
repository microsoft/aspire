// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.ServiceBus;

internal static class ServiceBusEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>azure-messaging/servicebus-emulator</remarks>
    public const string Image = "azure-messaging/servicebus-emulator";

    /// <remarks>2.0.0</remarks>
    public const string Tag = "2.0.0";
}
