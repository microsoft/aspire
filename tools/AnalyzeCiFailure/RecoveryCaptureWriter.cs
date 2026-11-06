// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using static AnalyzeCiFailure.AnalysisDiagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

/// <summary>Writes bounded, redacted diagnostic captures and receipts without creating resumable publication inputs.</summary>
internal static class RecoveryCaptureWriter
{
    internal const int MaximumTotalBytes = 16 * 1024 * 1024;

    internal static async Task WriteAsync(
        string directory,
        AnalysisProgress progress,
        string status,
        InvocationCapture capture,
        AnalysisScratchWorkspace scratch)
    {
        Directory.CreateDirectory(directory);
        // Finalize small diagnostics even after orderly cancellation. Hard runner termination
        // cannot guarantee recovery; no credentials, Git metadata or binary archives are copied.
        await File.WriteAllTextAsync(Path.Combine(directory, "collection-outcome.json"), TransientJson.Write(progress.CreateOutcome(status)));
        var entries = new JsonArray();
        var remaining = MaximumTotalBytes;
        var inventoryLimited = false;
        foreach (var (name, value) in capture.Documents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!HasInventorySpace())
            {
                break;
            }
            if (remaining == 0)
            {
                Omit(name);
                continue;
            }
            await RetainAsync(
                name,
                CiFailureJson.Serialize(DiagnosticTextSanitizer.SanitizeJson(value, MaximumFileBytes, multiline: true)),
                "GitHub/context capture; string values redacted");
        }
        if (capture.Bundle is { } bundle && HasInventorySpace())
        {
            // Rejected facts can contain invalid IDs, steps or enums. Diagnostic serialization
            // must not use the strict evidence writer and replace the original validation error.
            var diagnostic = JsonSerializer.SerializeToNode(
                bundle,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            await RetainAsync(
                "rejected-or-degraded-evidence.json",
                CiFailureJson.Serialize(DiagnosticTextSanitizer.SanitizeJson(diagnostic, MaximumFileBytes, multiline: true)),
                "collected facts; diagnostic only, not publication authority");
        }
        foreach (var (name, value) in capture.Attachments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!HasInventorySpace())
            {
                break;
            }
            if (value.Bytes.Length > MaximumFileBytes || value.Bytes.Length > remaining)
            {
                Omit(name);
                continue;
            }
            await RetainAsync(
                name,
                DiagnosticTextSanitizer.Sanitize(value.Text, MaximumFileBytes, multiline: true),
                "captured attachment; redacted");
        }
        if (Directory.Exists(scratch.Results))
        {
            foreach (var file in Directory.EnumerateFiles(scratch.Results, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (!HasInventorySpace())
                {
                    break;
                }
                var name = "extracted-results/" + Path.GetRelativePath(scratch.Results, file).Replace(Path.DirectorySeparatorChar, '/');
                var length = new FileInfo(file).Length;
                if (length > MaximumFileBytes || length > remaining)
                {
                    Omit(name);
                    continue;
                }
                await RetainAsync(
                    name,
                    DiagnosticTextSanitizer.Sanitize(await File.ReadAllTextAsync(file), MaximumFileBytes, multiline: true),
                    "extracted test report; redacted; may no longer be parseable");
            }
        }
        if (Directory.Exists(scratch.Requests))
        {
            foreach (var file in Directory.EnumerateFiles(scratch.Requests).Order(StringComparer.Ordinal))
            {
                if (!HasInventorySpace())
                {
                    break;
                }
                var name = "requests/" + Path.GetFileName(file);
                var length = new FileInfo(file).Length;
                if (length > MaximumFileBytes || length > remaining)
                {
                    Omit(name);
                    continue;
                }
                await RetainAsync(
                    name,
                    DiagnosticTextSanitizer.Sanitize(await File.ReadAllTextAsync(file), MaximumFileBytes, multiline: true),
                    "publication request; redacted");
            }
        }
        CiFailureJson.Write(
            Path.Combine(directory, "manifest.json"),
            new JsonObject
            {
                ["schema_version"] = 1,
                ["status"] = status,
                ["stage"] = progress.Stage,
                ["run_id"] = progress.RunId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["run_attempt"] = progress.RunAttempt,
                ["history_commit"] = capture.HistoryCommit,
                ["exception"] = capture.Failure is { } failure ? DescribeException(failure) : null,
                ["receipts"] = progress.Diagnostics.ReceiptSnapshot(),
                ["inventory_limited"] = inventoryLimited,
                ["replay"] = "Diagnostic capture only. Complete evidence packages support view regeneration and validation; collection resume is not supported.",
                ["files"] = entries,
            });

        bool HasInventorySpace()
        {
            if (entries.Count < MaximumManifestEntries)
            {
                return true;
            }
            inventoryLimited = true;
            return false;
        }

        void Omit(string name)
            => entries.Add(new JsonObject { ["file"] = name, ["state"] = "omitted", ["reason"] = "recovery size limit" });

        async Task RetainAsync(string name, string text, string transform)
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > MaximumFileBytes || bytes > remaining)
            {
                Omit(name);
                return;
            }
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
            remaining -= bytes;
            entries.Add(new JsonObject { ["file"] = name, ["state"] = "retained", ["bytes"] = bytes, ["transform"] = transform });
        }
    }
}
