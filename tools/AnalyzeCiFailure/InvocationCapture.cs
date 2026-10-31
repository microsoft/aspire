// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

/// <summary>Retains partial metadata, attachment bytes, evidence and the original failure across invocation phases.</summary>
internal sealed class InvocationCapture
{
    internal Dictionary<string, JsonNode?> Documents { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CollectedAttachment> Attachments { get; } = new(StringComparer.Ordinal);
    internal string? HistoryCommit { get; set; }
    internal EvidenceBundle? Bundle { get; set; }
    internal Exception? Failure { get; set; }
}
