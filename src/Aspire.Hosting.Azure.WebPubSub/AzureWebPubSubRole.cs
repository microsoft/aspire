// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Azure Web PubSub roles.
/// </summary>
internal enum AzureWebPubSubRole
{
    WebPubSubContributor,
    WebPubSubServiceOwner,
    WebPubSubServiceReader,
}
