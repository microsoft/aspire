// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects coding agents from known environment variables.
/// </summary>
internal sealed class CodingAgentDetector(IConfiguration configuration) : ICodingAgentDetector
{
    // Keep this in sync with the dotnet CLI's LLMEnvironmentDetectorForTelemetry detection
    // order so Aspire reports the same agent names when the same environment variables are set.
    // https://github.com/dotnet/sdk/blob/main/src/Cli/dotnet/Telemetry/LLMEnvironmentDetectorForTelemetry.cs
    private static readonly DetectionRule[] s_detectionRules =
    [
        new("cowork", ["CLAUDE_CODE_IS_COWORK"]),
        new("claude", ["CLAUDECODE", "CLAUDE_CODE", "CLAUDE_CODE_ENTRYPOINT"]),
        new("cursor", ["CURSOR_EDITOR", "CURSOR_AI", "CURSOR_TRACE_ID", "CURSOR_AGENT"]),
        new("gemini", ["GEMINI_CLI"]),
        // GitHub Copilot CLI (legacy gh extension: GITHUB_COPILOT_CLI_MODE; new Copilot CLI: GH_COPILOT_WORKING_DIRECTORY, COPILOT_CLI, COPILOT_MODEL, COPILOT_ALLOW_ALL, or COPILOT_GITHUB_TOKEN is set).
        new("copilot-cli", ["COPILOT_CLI", "GITHUB_COPILOT_CLI_MODE", "GH_COPILOT_WORKING_DIRECTORY", "COPILOT_MODEL", "COPILOT_ALLOW_ALL", "COPILOT_GITHUB_TOKEN"]),
        // GitHub Copilot agent mode in VS Code, which sets AI_AGENT=github_copilot_vscode_agent and COPILOT_AGENT=1 on the terminals it runs commands in.
        // See https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/terminalContrib/chatAgentTools/browser/toolTerminalCreator.ts
        new("copilot-vscode", ["AI_AGENT"], "github_copilot_vscode_agent"),
        new("copilot-vscode", ["COPILOT_AGENT"]),
        new("codex", ["CODEX_CLI", "CODEX_SANDBOX", "CODEX_CI", "CODEX_THREAD_ID"]),
        new("aider", ["OR_APP_NAME"], "Aider"),
        new("plandex", ["OR_APP_NAME"], "plandex"),
        new("amp", ["AMP_HOME"]),
        new("qwen", ["QWEN_CODE"]),
        new("droid", ["DROID_CLI"]),
        new("opencode", ["OPENCODE_AI"]),
        new("zed", ["ZED_ENVIRONMENT", "ZED_TERM"]),
        new("kimi", ["KIMI_CLI"]),
        new("openhands", ["OR_APP_NAME"], "OpenHands"),
        new("goose", ["GOOSE_TERMINAL", "GOOSE_PROVIDER"]),
        new("cline", ["CLINE_TASK_ID"]),
        new("roo", ["ROO_CODE_TASK_ID"]),
        new("windsurf", ["WINDSURF_SESSION"]),
        new("replit", ["REPL_ID"]),
        new("augment", ["AUGMENT_AGENT"]),
        new("antigravity", ["ANTIGRAVITY_AGENT"]),
        new("generic_agent", ["AGENT_CLI"])
    ];

    private readonly IConfiguration _configuration = configuration;

    /// <inheritdoc />
    public string? GetCodingAgent()
    {
        List<string>? agentNames = null;

        foreach (var rule in s_detectionRules)
        {
            if (rule.IsMatch(_configuration))
            {
                agentNames ??= [];
                agentNames.Add(rule.AgentName);
            }
        }

        return agentNames is { Count: > 0 } ? string.Join(", ", agentNames) : null;
    }

    private sealed class DetectionRule(string agentName, string[] variableNames, string? expectedValue = null)
    {
        private readonly string[] _variableNames = variableNames;
        private readonly string? _expectedValue = expectedValue;

        public string AgentName { get; } = agentName;

        public bool IsMatch(IConfiguration configuration)
        {
            foreach (var variableName in _variableNames)
            {
                var value = configuration[variableName];
                if (_expectedValue is null)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        return true;
                    }
                }
                else if (string.Equals(value, _expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
