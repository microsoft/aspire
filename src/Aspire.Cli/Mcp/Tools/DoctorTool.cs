// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Utils.EnvironmentChecker;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for checking Aspire prerequisites and environment setup.
/// </summary>
internal sealed class DoctorTool(IEnvironmentChecker environmentChecker) : CliMcpTool
{
    public override string Name => KnownMcpTools.Doctor;

    public override string Description => "Diagnose Aspire environment issues by performing comprehensive checks. Returns detailed information about each check including status (pass/warning/fail), messages, and actionable fix suggestions. Use this to identify and resolve environment problems before running Aspire applications. This tool does not require a running AppHost.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false,
              "description": "This tool takes no input parameters. It performs comprehensive environment checks and returns detailed results for each check."
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            // Run all environment checks
            var results = await environmentChecker.CheckAllAsync(cancellationToken);

            // Build response
            var passed = results.Count(r => r.Status == EnvironmentCheckStatus.Pass);
            var warnings = results.Count(r => r.Status == EnvironmentCheckStatus.Warning);
            var failed = results.Count(r => r.Status == EnvironmentCheckStatus.Fail);

            var response = new DoctorCheckResponse
            {
                Checks = results.ToList(),
                Summary = new DoctorCheckSummary
                {
                    Passed = passed,
                    Warnings = warnings,
                    Failed = failed
                }
            };

            var jsonContent = JsonSerializer.Serialize(response, JsonSourceGenerationContext.Default.DoctorCheckResponse);

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
                Content = [new TextContentBlock { Text = $"Failed to check environment: {ex.Message}" }]
            };
        }
    }
}
