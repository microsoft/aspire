// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Mcp;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class McpConfigurationTests
{
    [Fact]
    public void McpConfig_WithApiKey_GeneratesProperInputConfiguration()
    {
        // Arrange
        var inputs = new List<McpInputModel>
        {
            new McpInputModel
            {
                Id = "x_mcp_api_key",
                Type = "promptString",
                Description = "Enter x-mcp-api-key",
                Password = true
            }
        };

        var configModel = new McpJsonFileServerModel
        {
            Inputs = inputs,
            Servers = new()
            {
                ["aspire-dashboard"] = new()
                {
                    Type = "http",
                    Url = "http://localhost:23052/mcp",
                    Headers = new Dictionary<string, string>
                    {
                        [McpApiKeyAuthenticationHandler.McpApiKeyHeaderName] = "${input:x_mcp_api_key}"
                    }
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(configModel, McpConfigFileModelContext.Default.McpJsonFileServerModel);
        var parsed = JsonNode.Parse(json);

        // Assert - verify the structure matches the expected format from the issue
        Assert.NotNull(parsed);
        
        // Check inputs array exists
        var inputsArray = parsed!["inputs"]?.AsArray();
        Assert.NotNull(inputsArray);
        Assert.Single(inputsArray!);

        // Verify input configuration
        var input = inputsArray![0];
        Assert.Equal("x_mcp_api_key", input!["id"]?.GetValue<string>());
        Assert.Equal("promptString", input["type"]?.GetValue<string>());
        Assert.Equal("Enter x-mcp-api-key", input["description"]?.GetValue<string>());
        Assert.True(input["password"]?.GetValue<bool>());

        // Check servers configuration
        var servers = parsed["servers"];
        Assert.NotNull(servers);
        
        var dashboard = servers!["aspire-dashboard"];
        Assert.NotNull(dashboard);
        Assert.Equal("http", dashboard!["type"]?.GetValue<string>());
        Assert.Equal("http://localhost:23052/mcp", dashboard["url"]?.GetValue<string>());

        // Verify header uses input reference, not hardcoded key
        var headers = dashboard["headers"];
        Assert.NotNull(headers);
        var headerValue = headers!["x-mcp-api-key"]?.GetValue<string>();
        Assert.Equal("${input:x_mcp_api_key}", headerValue);
    }

    [Fact]
    public void McpInstallButton_WithApiKey_GeneratesProperInputConfiguration()
    {
        // Arrange
        var inputs = new List<McpInputModel>
        {
            new McpInputModel
            {
                Id = "x_mcp_api_key",
                Type = "promptString",
                Description = "Enter x-mcp-api-key",
                Password = true
            }
        };

        var model = new McpInstallButtonServerModel
        {
            Name = "aspire-dashboard",
            Inputs = inputs,
            Type = "http",
            Url = "http://localhost:23052/mcp",
            Headers = new Dictionary<string, string>
            {
                [McpApiKeyAuthenticationHandler.McpApiKeyHeaderName] = "${input:x_mcp_api_key}"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(model, McpInstallButtonModelContext.Default.McpInstallButtonServerModel);
        var parsed = JsonNode.Parse(json);

        // Assert
        Assert.NotNull(parsed);
        
        // Check name
        Assert.Equal("aspire-dashboard", parsed!["name"]?.GetValue<string>());

        // Check inputs array exists
        var inputsArray = parsed["inputs"]?.AsArray();
        Assert.NotNull(inputsArray);
        Assert.Single(inputsArray!);

        // Verify input configuration
        var input = inputsArray![0];
        Assert.Equal("x_mcp_api_key", input!["id"]?.GetValue<string>());
        Assert.Equal("promptString", input["type"]?.GetValue<string>());
        Assert.Equal("Enter x-mcp-api-key", input["description"]?.GetValue<string>());
        Assert.True(input["password"]?.GetValue<bool>());

        // Verify header uses input reference
        var headers = parsed["headers"];
        Assert.NotNull(headers);
        var headerValue = headers!["x-mcp-api-key"]?.GetValue<string>();
        Assert.Equal("${input:x_mcp_api_key}", headerValue);
    }

    [Fact]
    public void McpConfig_WithoutApiKey_NoInputsOrHeaders()
    {
        // Arrange
        var configModel = new McpJsonFileServerModel
        {
            Inputs = null,
            Servers = new()
            {
                ["aspire-dashboard"] = new()
                {
                    Type = "http",
                    Url = "http://localhost:23052/mcp",
                    Headers = null
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(configModel, McpConfigFileModelContext.Default.McpJsonFileServerModel);
        var parsed = JsonNode.Parse(json);

        // Assert
        Assert.NotNull(parsed);
        
        // Should not have inputs in JSON when null (due to JsonIgnoreCondition.WhenWritingNull)
        Assert.Null(parsed!["inputs"]);

        // Check servers configuration exists
        var servers = parsed["servers"];
        Assert.NotNull(servers);
        
        var dashboard = servers!["aspire-dashboard"];
        Assert.NotNull(dashboard);
        Assert.Equal("http", dashboard!["type"]?.GetValue<string>());
        Assert.Equal("http://localhost:23052/mcp", dashboard["url"]?.GetValue<string>());

        // Should not have headers
        Assert.Null(dashboard["headers"]);
    }
}
