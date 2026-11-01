// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Google.Protobuf.WellKnownTypes;

namespace Aspire.Dashboard.Model;

internal sealed record TestingResultRow(string Name, string Outcome, double? DurationMilliseconds, string? Message);

internal sealed record TestingResourceGroup(string ResourceName, string Outcome, string? CollectionStatus, string? Error, IReadOnlyList<TestingResultRow> Results, bool HasInvalidData)
{
    public static TestingResourceGroup Create(IReadOnlyList<ResourceViewModel> replicas)
    {
        // Replica updates arrive independently. Use one complete logical-resource snapshot,
        // rather than mixing an old collection state with newer results from another replica.
        var values = replicas.Select(r => r.Properties.GetValueOrDefault(KnownProperties.Resource.TestingResults)?.Value).Where(v => v is not null).ToList();
        var snapshot = values
            .Where(v => v?.KindCase == Value.KindOneofCase.StructValue).Select(v => v!.StructValue)
            .OrderByDescending(s => s.Fields.TryGetValue("revision", out var revision) && revision.HasNumberValue ? revision.NumberValue : 0).FirstOrDefault();
        var rows = snapshot is null ? [] : ReadRows(snapshot);
        var error = snapshot is null ? null : GetString(snapshot, "collectionError");
        var collectionStatus = snapshot is null ? null : GetString(snapshot, "collectionStatus");
        var resourceOutcome = snapshot is null ? null : GetString(snapshot, "resourceOutcome");
        var expectedReplicas = replicas.Max(r => r.Properties.TryGetValue(KnownProperties.Resource.TestingExpectedReplicas, out var count) &&
            count.Value.HasNumberValue ? count.Value.NumberValue : 1);
        var invalid = values.Any(v => v!.KindCase != Value.KindOneofCase.StructValue) ||
            snapshot is not null && (collectionStatus is not ("pending" or "running" or "completed" or "failed" or "cancelled" or "notConfigured") ||
                resourceOutcome is not ("pending" or "passed" or "failed") ||
                !snapshot.Fields.TryGetValue("results", out var results) || results.KindCase != Value.KindOneofCase.ListValue ||
                rows.Any(r => string.IsNullOrEmpty(r.Name) || r.Outcome == "unknown"));
        var outcome = resourceOutcome == "failed" || replicas.Any(IsFailed) || rows.Any(r => r.Outcome == "failed") || collectionStatus == "failed"
            ? "failed"
            : collectionStatus == "cancelled" ? "cancelled"
            : collectionStatus == "running" ? "running"
            : invalid ? "unknown"
            : snapshot is not null ? resourceOutcome == "passed" && collectionStatus is "completed" or "notConfigured" ? "passed"
                : replicas.Any(r => r.KnownState == KnownResourceState.Running) ? "running"
                : replicas.Any(IsTerminal) ? "unknown" : "pending"
            : replicas.Count >= expectedReplicas && replicas.All(r => IsTerminal(r) && r.TryGetExitCode(out var code) && code == 0) ? "passed"
            : replicas.Any(r => r.KnownState == KnownResourceState.Running) ? "running"
            : replicas.Any(IsTerminal) ? "unknown" : "pending";

        return new(replicas[0].DisplayName, outcome, collectionStatus, error, rows, invalid);
    }

    private static bool IsTerminal(ResourceViewModel resource) =>
        resource.KnownState is KnownResourceState.Finished or KnownResourceState.Exited or KnownResourceState.FailedToStart or KnownResourceState.RuntimeUnhealthy;

    private static bool IsFailed(ResourceViewModel resource) =>
        resource.KnownState is KnownResourceState.FailedToStart or KnownResourceState.RuntimeUnhealthy ||
        IsTerminal(resource) && resource.TryGetExitCode(out var code) && code != 0;

    private static string? GetString(Struct value, string key) =>
        value.Fields.TryGetValue(key, out var field) && field.HasStringValue ? field.StringValue : null;

    private static IReadOnlyList<TestingResultRow> ReadRows(Struct value)
    {
        if (!value.Fields.TryGetValue("results", out var results) || results.KindCase != Value.KindOneofCase.ListValue)
        {
            return [];
        }

        // The snapshot contract is a list of structs, not delimited strings. Names/messages
        // can contain arbitrary punctuation and repeated names are distinct published results.
        return results.ListValue.Values.Select(v =>
        {
            if (v.KindCase != Value.KindOneofCase.StructValue)
            {
                return new TestingResultRow(string.Empty, "unknown", null, null);
            }
            var fields = v.StructValue;
            var outcome = GetString(fields, "outcome");
            return new TestingResultRow(GetString(fields, "name") ?? string.Empty,
                outcome is "passed" or "failed" or "skipped" ? outcome : "unknown",
                fields.Fields.TryGetValue("durationMilliseconds", out var duration) && duration.HasNumberValue ? duration.NumberValue : null,
                GetString(fields, "message"));
        }).ToList();
    }
}
