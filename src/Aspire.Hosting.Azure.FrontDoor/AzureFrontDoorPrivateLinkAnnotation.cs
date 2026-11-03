// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Records that an Azure Front Door reaches a compute environment through Private Link. It is added to the Front Door,
/// once per connected environment.
/// </summary>
internal sealed class AzureFrontDoorPrivateLinkAnnotation : IResourceAnnotation
{
    // Front Door limits the request message to 140 characters.
    // https://learn.microsoft.com/azure/templates/microsoft.cdn/profiles/origingroups/origins#sharedprivatelinkresourceproperties
    private const int MaxRequestMessageLength = 140;

    public AzureFrontDoorPrivateLinkAnnotation(
        AzureFrontDoorResource frontDoor,
        IAzureCdnPrivateLinkTarget environment)
    {
        Environment = environment;

        // The environment owner sees this message on the pending connection request, so it names both ends.
        var message = $"Azure Front Door {frontDoor.Name} requests access to {environment.Name}.";
        RequestMessage = message.Length <= MaxRequestMessageLength ? message : message[..MaxRequestMessageLength];
    }

    /// <summary>
    /// Gets the compute environment the Front Door connects to.
    /// </summary>
    public IAzureCdnPrivateLinkTarget Environment { get; }

    /// <summary>
    /// Gets the message that the connection request shows to the environment owner.
    /// </summary>
    public string RequestMessage { get; }
}
