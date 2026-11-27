// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Agents.Hooks;

internal static class TelemetryHookJson
{
    /// <summary>
    /// Strict JSON options shared by Claude's settings writer and Claude's legacy hook detection, so
    /// <c>aspire doctor</c> only reports a Claude registration that <c>aspire agent init</c> can merge into.
    /// Copilot's hook file is Aspire-owned and is overwritten without parsing, so its detection allows repeated keys.
    /// JsonObject materializes properties lazily, so a repeated key would otherwise throw
    /// <see cref="ArgumentException"/> on first lookup instead of failing at parse time as malformed JSON.
    /// </summary>
    internal static JsonDocumentOptions ParseOptions { get; } = new() { AllowDuplicateProperties = false };
}
