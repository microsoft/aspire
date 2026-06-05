// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

/// <summary>
/// Provides functionality to display the Aspire CLI animated banner.
/// </summary>
internal interface IBannerService
{
    /// <summary>
    /// Displays the animated Aspire CLI banner.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the animation.</param>
    /// <returns>A task that completes when the banner animation is finished.</returns>
    Task DisplayBannerAsync(CancellationToken cancellationToken = default);
}
