// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.WebPubSub;

internal static class WebPubSubEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>webpubsub/emulator</remarks>
    public const string Image = "webpubsub/emulator";

    /// <remarks>1.0.0-beta.2</remarks>
    public const string Tag = "1.0.0-beta.2";
}
