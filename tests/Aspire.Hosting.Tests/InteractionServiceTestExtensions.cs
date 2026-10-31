// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

internal static class InteractionServiceTestExtensions
{
    // InteractionService starts unavailable until the app host marks its consumers as ready.
    // Tests that create the service directly act as the consumer, so mark it ready up front.
    public static InteractionService WithConsumersReady(this InteractionService interactionService)
    {
        interactionService.MarkConsumersReady();
        return interactionService;
    }
}
