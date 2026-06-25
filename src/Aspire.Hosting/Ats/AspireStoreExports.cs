// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for Aspire store access.
/// </summary>
internal static class AspireStoreExports
{
    /// <summary>
    /// Gets the Aspire store from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    /// <returns>The Aspire store.</returns>
    [AspireExport]
    public static IAspireStore GetAspireStore(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<IAspireStore>();
    }
}
