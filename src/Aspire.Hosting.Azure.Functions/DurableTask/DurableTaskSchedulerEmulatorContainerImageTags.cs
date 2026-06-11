// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.DurableTask;

internal static class DurableTaskSchedulerEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>dts/dts-emulator</remarks>
    public const string Image = "dts/dts-emulator";

    /// <remarks>latest</remarks>
    public const string Tag = "latest";
}
