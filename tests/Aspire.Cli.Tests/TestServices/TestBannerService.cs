// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestBannerService : IBannerService
{
    public bool WasBannerDisplayed { get; private set; }
    public int DisplayCount { get; private set; }

    public Task DisplayBannerAsync(CancellationToken cancellationToken = default)
    {
        WasBannerDisplayed = true;
        DisplayCount++;
        return Task.CompletedTask;
    }
}
