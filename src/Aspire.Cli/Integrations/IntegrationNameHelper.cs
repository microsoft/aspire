// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Integrations;

internal static class IntegrationNameHelper
{
    public static string GenerateFriendlyName(string packageId)
    {
        var name = packageId.Replace("Aspire.Hosting.", "", StringComparison.OrdinalIgnoreCase);
        return name.Replace('.', '-').ToLowerInvariant();
    }
}
