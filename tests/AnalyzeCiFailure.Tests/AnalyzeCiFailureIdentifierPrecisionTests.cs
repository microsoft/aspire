// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Verifies exact identifier handling above floating-point integer precision.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData(9007199254740994, 9007199254740993)]
    [InlineData(9007199254740993, 9007199254740992)]
    [RequiresTools(["bash", "jq"])]
    public async Task LastSuccessfulRunKeepsAdjacentLargeIdsDistinctAtTheSameTimestamp(long failed, long expected)
    {
        var fakeGh = """
            #!/usr/bin/env bash
            cat <<'JSON'
            {"total_count":3,"workflow_runs":[
              {"id":9007199254740995,"created_at":"2026-08-30T10:00:00Z","head_sha":"after"},
              {"id":9007199254740993,"created_at":"2026-08-30T10:00:00Z","head_sha":"adjacent"},
              {"id":9007199254740992,"created_at":"2026-08-30T10:00:00Z","head_sha":"previous"}
            ]}
            JSON
            """;
        var outputPath = Path.Combine(_workspace.Path, "last-success.json");

        var result = await RunHistoryScriptAsync(fakeGh, "2026-08-30T10:00:00Z", outputPath, failedRunId: failed);

        Assert.True(result.ExitCode == 0, result.Output);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        Assert.Equal(expected, output.RootElement.GetProperty("id").GetInt64());
    }
}
