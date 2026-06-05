// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Mcp;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class McpServerDialog
{
    private readonly string _vsCopyButtonId = $"copy-{Guid.NewGuid():N}";
    private readonly string _vsCodeCopyButtonId = $"copy-{Guid.NewGuid():N}";

    [CascadingParameter]
    public FluentDialog Dialog { get; set; } = default!;

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IOptions<DashboardOptions> DashboardOptions { get; init; }

    private MarkdownProcessor _markdownProcessor = default!;
    private string? _mcpServerInstallButtonJson;
    private string? _mcpServerConfigFileJson;
    private string? _mcpUrl;
    private bool _isHttps;
    private McpToolView _activeView;
    private List<McpConfigPropertyViewModel> _mcpConfigProperties = [];

    protected override void OnInitialized()
    {
        _markdownProcessor = new MarkdownProcessor(ControlsStringsLoc, MarkdownHelpers.SafeUrlSchemes, []);

        if (McpEnabled)
        {
            // Check if we should show CLI MCP instructions instead of dashboard MCP instructions
            if (IsCliMcpMode)
            {
                // For CLI MCP mode, we don't need the HTTP endpoint configuration
                (_mcpServerInstallButtonJson, _mcpServerConfigFileJson) = GetCliMcpServerInstallButtonJson();
                _mcpConfigProperties =
                [
                    new McpConfigPropertyViewModel { Name = "name", Value = "aspire" },
                    new McpConfigPropertyViewModel { Name = "type", Value = "stdio" },
                    new McpConfigPropertyViewModel { Name = "command", Value = "aspire" },
                    new McpConfigPropertyViewModel { Name = "args", Value = @"[""mcp"", ""start""]" }
                ];
            }
            else
            {
                if ((DashboardOptions.Value.Mcp.PublicUrl ?? DashboardOptions.Value.Mcp.EndpointUrl) is { Length: > 0 } mcpUrl)
                {
                    var uri = new Uri(baseUri: new Uri(mcpUrl), relativeUri: "/mcp");

                    _mcpUrl = uri.ToString();
                    _isHttps = uri.Scheme == "https";
                }

                // Original dashboard MCP mode
                (_mcpServerInstallButtonJson, _mcpServerConfigFileJson) = GetDashboardMcpServerInstallButtonJson();
                _mcpConfigProperties =
                [
                    new McpConfigPropertyViewModel { Name = "name", Value = "aspire-dashboard" },
                    new McpConfigPropertyViewModel { Name = "type", Value = "http" },
                    new McpConfigPropertyViewModel { Name = "url", Value = _mcpUrl! }
                ];

                if (DashboardOptions.Value.Mcp.AuthMode == McpAuthMode.ApiKey)
                {
                    _mcpConfigProperties.Add(new McpConfigPropertyViewModel { Name = $"{McpApiKeyAuthenticationHandler.ApiKeyHeaderName} (header)", Value = DashboardOptions.Value.Mcp.PrimaryApiKey! });
                }
            }
        }
        else
        {
            throw new InvalidOperationException("MCP server is not enabled or configured.");
        }
    }

    [MemberNotNullWhen(true, nameof(_mcpServerInstallButtonJson))]
    [MemberNotNullWhen(true, nameof(_mcpServerConfigFileJson))]
    private bool McpEnabled => !DashboardOptions.Value.Mcp.Disabled.GetValueOrDefault();

    private bool IsCliMcpMode => DashboardOptions.Value.Mcp.UseCliMcp == true;

    private (string InstallButtonJson, string ConfigFileJson) GetDashboardMcpServerInstallButtonJson()
    {
        Debug.Assert(_mcpUrl != null);

        Dictionary<string, string>? headers = null;
        List<McpInputModel>? inputs = null;

        if (DashboardOptions.Value.Mcp.AuthMode == McpAuthMode.ApiKey)
        {
            // Use input reference instead of hardcoded API key
            headers = new Dictionary<string, string>
            {
                [McpApiKeyAuthenticationHandler.ApiKeyHeaderName] = "${input:aspire_mcp_api_key}"
            };

            // Define the input for the API key
            // Don't localize the description here because this value flows out from the dashboard and is persisted.
            // I don't think we should use the value of the dashboard's culture at the moment the button is clicked. Leave it as a static English value.
            inputs = new List<McpInputModel>
            {
                new McpInputModel
                {
                    Id = "aspire_mcp_api_key",
                    Type = "promptString",
                    Description = "Enter Aspire MCP API key",
                    Password = true
                }
            };
        }

        var name = "aspire-dashboard";

        var installButtonJson = JsonSerializer.Serialize(
            new McpInstallButtonServerModel
            {
                Name = name,
                Inputs = inputs,
                Type = "http",
                Url = _mcpUrl,
                Headers = headers
            },
            McpInstallButtonModelContext.Default.McpInstallButtonServerModel);

        var configFileJson = JsonSerializer.Serialize(
            new McpJsonFileServerModel
            {
                Inputs = inputs,
                Servers = new()
                {
                    [name] = new()
                    {
                        Type = "http",
                        Url = _mcpUrl,
                        Headers = headers
                    }
                }
            },
            McpConfigFileModelContext.Default.McpJsonFileServerModel);

        return (installButtonJson, configFileJson);
    }

    private static (string InstallButtonJson, string ConfigFileJson) GetCliMcpServerInstallButtonJson()
    {
        var name = "aspire";

        var installButtonJson = JsonSerializer.Serialize(
            new McpInstallButtonServerModel
            {
                Name = name,
                Type = "stdio",
                Command = "aspire",
                Args = ["mcp", "start"]
            },
            McpInstallButtonModelContext.Default.McpInstallButtonServerModel);

        var configFileJson = JsonSerializer.Serialize(
            new McpJsonFileServerModel
            {
                Servers = new()
                {
                    [name] = new()
                    {
                        Type = "stdio",
                        Command = "aspire",
                        Args = ["mcp", "start"]
                    }
                }
            },
            McpConfigFileModelContext.Default.McpJsonFileServerModel);

        return (installButtonJson, configFileJson);
    }

    private Task OnTabChangeAsync(FluentTab newTab)
    {
        var id = newTab.Id?.Substring("tab-".Length);

        if (id is null
            || !Enum.TryParse(typeof(McpToolView), id, out var o)
            || o is not McpToolView viewKind)
        {
            return Task.CompletedTask;
        }

        _activeView = viewKind;
        return Task.CompletedTask;
    }

    private string GetJsonConfigurationMarkdown() =>
        $"""
        ```json
        {_mcpServerConfigFileJson}
        ```
        """;

    public enum McpToolView
    {
        VSCode,
        VisualStudio,
        Other
    }
}
