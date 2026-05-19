// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Packaging;
using ModelContextProtocol.Protocol;
using Semver;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// Represents an Aspire hosting integration package.
/// </summary>
internal sealed class Integration
{
    /// <summary>
    /// Gets or sets the friendly name of the integration (e.g., "Redis", "PostgreSQL").
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the NuGet package ID.
    /// </summary>
    [JsonPropertyName("packageId")]
    public required string PackageId { get; set; }

    /// <summary>
    /// Gets or sets the package version.
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version { get; set; }
}

/// <summary>
/// Represents the response from the list_integrations tool.
/// </summary>
internal sealed class ListIntegrationsResponse
{
    /// <summary>
    /// Gets or sets the list of available integrations.
    /// </summary>
    [JsonPropertyName("integrations")]
    public required List<Integration> Integrations { get; set; }
}

/// <summary>
/// MCP tool for listing available Aspire hosting integrations.
/// </summary>
internal sealed class ListIntegrationsTool(IPackagingService packagingService, CliExecutionContext executionContext, IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor) : CliMcpTool
{
    public override string Name => KnownMcpTools.ListIntegrations;

    public override string Description => "List available Aspire hosting integrations. These are NuGet packages that can be added to an Aspire AppHost project to integrate with various services like databases, message brokers, and cloud services. Use 'aspire add <integration-name>' to add an integration to your AppHost project. Use the 'get_integration_docs' tool to get detailed documentation for a specific integration. This tool does not require a running AppHost.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false,
              "description": "This tool takes no input parameters. It returns a list of available Aspire hosting integrations with their short name, full package ID, and version."
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Get all channels
            var packageChannels = await packagingService.GetChannelsAsync(cancellationToken);

            // Use only the default (first) channel
            var defaultChannel = packageChannels.FirstOrDefault();
            if (defaultChannel == null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = "No package channels available" }]
                };
            }

            // Determine the working directory to use
            // If there's an in-scope AppHost, use its directory; otherwise use the MCP's working directory
            var workingDirectory = GetWorkingDirectory();

            // Get integration packages from the default channel
            var integrationPackages = await defaultChannel.GetIntegrationPackagesAsync(workingDirectory, cancellationToken);

            // Group by package ID and select the latest version using semantic version comparison
            // Parse version once and include it in the result to avoid redundant parsing
            var packagesWithParsedVersions = integrationPackages
                .Select(p => new
                {
                    FriendlyName = GetFriendlyName(p.Id),
                    PackageId = p.Id,
                    Version = p.Version,
                    ParsedVersion = SemVersion.TryParse(p.Version, SemVersionStyles.Any, out var v) ? v : null
                })
                .Where(p => p.ParsedVersion is not null)
                .ToList();

            var distinctPackages = packagesWithParsedVersions
                .GroupBy(p => p.PackageId)
                .Select(g => g.OrderByDescending(p => p.ParsedVersion!, SemVersion.PrecedenceComparer).First())
                .OrderBy(p => p.FriendlyName)
                .ToList();

            var integrations = distinctPackages
                .Select(p => new Integration
                {
                    Name = p.FriendlyName,
                    PackageId = p.PackageId,
                    Version = p.Version
                })
                .ToList();

            var response = new ListIntegrationsResponse
            {
                Integrations = integrations
            };

            var jsonContent = JsonSerializer.Serialize(response, JsonSourceGenerationContext.Default.ListIntegrationsResponse);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = jsonContent }]
            };
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Failed to list integrations: {ex.Message}" }]
            };
        }
    }

    /// <summary>
    /// Gets the appropriate working directory for package resolution.
    /// Uses the AppHost directory if an in-scope AppHost exists, otherwise uses the MCP's working directory.
    /// </summary>
    private DirectoryInfo GetWorkingDirectory()
    {
        // Get in-scope connections
        var inScopeConnections = auxiliaryBackchannelMonitor.GetConnectionsForWorkingDirectory(executionContext.WorkingDirectory);

        // If there's exactly one in-scope AppHost, use its directory
        if (inScopeConnections.Count == 1)
        {
            var appHostPath = inScopeConnections[0].AppHostInfo?.AppHostPath;
            if (!string.IsNullOrEmpty(appHostPath))
            {
                var appHostDirectory = Path.GetDirectoryName(appHostPath);
                if (!string.IsNullOrEmpty(appHostDirectory) && Directory.Exists(appHostDirectory))
                {
                    return new DirectoryInfo(appHostDirectory);
                }
            }
        }

        // Default to the MCP's working directory
        return executionContext.WorkingDirectory;
    }

    private static string GetFriendlyName(string packageId)
    {
        // Handle CommunityToolkit packages
        if (packageId.StartsWith("CommunityToolkit.Aspire.Hosting.", StringComparison.Ordinal))
        {
            return packageId["CommunityToolkit.Aspire.Hosting.".Length..];
        }

        // Handle Aspire.Hosting packages
        if (packageId.StartsWith("Aspire.Hosting.", StringComparison.Ordinal))
        {
            return packageId["Aspire.Hosting.".Length..];
        }

        return packageId;
    }
}
