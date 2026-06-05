// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Mcp.Tools;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Provides helper methods for creating <see cref="CallToolContext"/> instances in tests.
/// </summary>
internal static class CallToolContextTestHelper
{
    /// <summary>
    /// Creates a <see cref="CallToolContext"/> for testing.
    /// </summary>
    /// <param name="arguments">Optional arguments to pass to the tool.</param>
    /// <param name="notifier">Optional notifier to use. If null, a new <see cref="TestMcpNotifier"/> is created.</param>
    /// <param name="progressToken">Optional progress token to include in the context.</param>
    /// <returns>A new <see cref="CallToolContext"/> configured for testing.</returns>
    public static CallToolContext Create(
        IReadOnlyDictionary<string, JsonElement>? arguments = null,
        TestMcpNotifier? notifier = null,
        string? progressToken = null)
    {
        return new CallToolContext
        {
            Notifier = notifier ?? new TestMcpNotifier(),
            McpClient = null,
            Arguments = arguments,
            ProgressToken = progressToken is not null ? new ModelContextProtocol.Protocol.ProgressToken(progressToken) : null
        };
    }
}
