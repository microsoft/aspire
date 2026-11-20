// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Agents.Hooks;

internal static class TelemetryHookJson
{
    /// <summary>
    /// Strict JSON options shared by the code that writes telemetry hook configuration and the code that
    /// inspects it, so <c>aspire doctor</c> only reports a file that <c>aspire agent init</c> can rewrite.
    /// JsonObject materializes properties lazily, so a repeated key would otherwise throw
    /// <see cref="ArgumentException"/> on first lookup instead of failing at parse time as malformed JSON.
    /// </summary>
    internal static JsonDocumentOptions ParseOptions { get; } = new() { AllowDuplicateProperties = false };
}
