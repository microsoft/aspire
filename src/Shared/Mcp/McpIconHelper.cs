// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using ModelContextProtocol.Protocol;

namespace Aspire.Shared.Mcp;

/// <summary>
/// Helper class for loading MCP server icons from embedded resources.
/// </summary>
internal static class McpIconHelper
{
    private static readonly string[] s_iconSizes = ["16", "32", "48", "64", "256"];

    /// <summary>
    /// Gets the Aspire MCP server icons from embedded resources.
    /// </summary>
    /// <param name="assembly">The assembly containing the embedded icon resources.</param>
    /// <param name="resourceNamespace">The namespace prefix for the embedded resources (e.g., "Aspire.Dashboard.Mcp.Resources" or "Aspire.Cli.Mcp.Resources").</param>
    /// <returns>A list of Icon objects with PNG data embedded as base64 data URIs.</returns>
    public static List<Icon> GetAspireIcons(Assembly assembly, string resourceNamespace)
    {
        // SVG isn't a required icon format for MCP. Use PNGs to ensure the icon is visible in all tools that support icons.
        var icons = s_iconSizes.Select(s =>
        {
            var resourceName = $"{resourceNamespace}.aspire-{s}.png";
            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                throw new InvalidOperationException($"Could not find embedded resource '{resourceName}' in assembly '{assembly.FullName}'.");
            }

            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            var data = memoryStream.ToArray();

            // The Sizes property expects an array of size strings. Each icon has a single size.
            return new Icon { Source = $"data:image/png;base64,{Convert.ToBase64String(data)}", MimeType = "image/png", Sizes = new[] { s } };
        }).ToList();

        return icons;
    }
}
