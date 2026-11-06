// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestTools;
using Infrastructure.Tests.TestServices;
using Xunit;

namespace Infrastructure.Tests;

public sealed class CombinedTrxReportGuardTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ubuntu-latest/testresults/Hosting_net10.0_20261103065037.trx", "Hosting", "ubuntu-latest", 0)]
    [InlineData("ubuntu-latest/testresults/Hosting-2_net11.0_20261103065037.trx", "Hosting-2", "8-core-ubuntu-latest", 0)]
    [InlineData("windows-latest/testresults/Hosting.trx", "Hosting", "windows-latest", 0)]
    [InlineData("macos-latest/testresults/Templates-AppHostTemplateTests.trx", "Templates", "macos-latest", 0)]
    [InlineData("ubuntu-latest/testresults/Hosting-Unknown_net10.0_20261103065037.trx", "Hosting", "ubuntu-latest", 1)]
    [InlineData("ubuntu-latest/testresults/Hosting_net10.0_bad.trx", "Hosting", "ubuntu-latest", 1)]
    [InlineData("windows-latest/testresults/Hosting.trx", "Hosting", "ubuntu-latest", 1)]
    [InlineData("ubuntu-latest/testresults/merged/merged-Hosting.trx", "Hosting", "ubuntu-latest", 0)]
    [InlineData("merged/merged-all.trx", "Hosting", "ubuntu-latest", 0)]
    [InlineData("ubuntu-latest/testresults/merged/Hosting.trx", "Hosting", "ubuntu-latest", 1)]
    [InlineData(null, "Hosting", "ubuntu-latest", 0)]
    public async Task AvailableReportsMustHaveOneKnownProducer(string? entry, string shortName, string runner, int expected)
    {
        var (exitCode, calls) = await RunAsync(entry, Jobs(shortName, runner));

        Assert.Equal(expected, exitCode);
        Assert.Equal(entry is null || entry.Contains("/merged-", StringComparison.Ordinal) ? [] :
            new[] { "/repos/microsoft/aspire/actions/runs/123/attempts/2/jobs", "/repos/microsoft/aspire/actions/runs/123/jobs?filter=all" }, calls);
    }

    [Theory]
    [InlineData("Hosting.trx")]
    [InlineData("Hosting-BatchTests.trx")]
    public async Task AmbiguousCurrentProducersCannotValidateAReport(string fileName)
    {
        var current = Jobs("Hosting", "ubuntu-latest");
        current["jobs"]!.AsArray().Add(Jobs("Hosting", "8-core-ubuntu-latest")["jobs"]![0]!.DeepClone());

        var (exitCode, _) = await RunAsync($"ubuntu-latest/testresults/{fileName}", current);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task HistoricalRepeatsAreDeduplicatedButSiblingProducersRemainKnown(bool siblingOnly, int expected)
    {
        var current = Jobs("Hosting", "ubuntu-latest");
        var historical = Jobs("Hosting", "ubuntu-latest");
        historical["jobs"]!.AsArray().Add(current["jobs"]![0]!.DeepClone());
        historical["jobs"]!.AsArray().Add(Jobs("Hosting-2", "ubuntu-latest")["jobs"]![0]!.DeepClone());

        var (exitCode, _) = await RunAsync(
            siblingOnly ? "ubuntu-latest/testresults/Hosting-2.trx" : "ubuntu-latest/testresults/Hosting.trx",
            current, historical);

        Assert.Equal(expected, exitCode);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("inventory")]
    [InlineData("history-http")]
    [InlineData("history-inventory")]
    [InlineData("directory")]
    [InlineData("cancelled")]
    public async Task AcquisitionFailuresCannotPassTheGuard(string failure)
    {
        var (exitCode, _) = await RunAsync("ubuntu-latest/testresults/Hosting.trx",
            failure == "inventory" ? new JsonObject { ["jobs"] = new JsonArray() } : Jobs("Hosting", "ubuntu-latest"),
            historical: failure == "history-inventory" ? new JsonObject { ["jobs"] = new JsonArray() } : null,
            failure: failure);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task FirstAttemptUsesEveryCurrentJobPageWithoutHistoricalRequests()
    {
        var (exitCode, calls) = await RunAsync("ubuntu-latest/testresults/Hosting.trx",
            Jobs("Hosting", "ubuntu-latest"), failure: "pagination", runAttempt: 1);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["/repos/microsoft/aspire/actions/runs/123/attempts/1/jobs", "/repos/microsoft/aspire/actions/runs/123/attempts/1/jobs?page=2"],
            calls);
    }

    private async Task<(int ExitCode, string[] Calls)> RunAsync(
        string? entry, JsonObject current, JsonObject? historical = null, string? failure = null, int runAttempt = 2)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var results = Path.Combine(workspace.Path, "testresults");
        Directory.CreateDirectory(results);
        if (entry is not null)
        {
            var file = Path.Combine(results, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // This guard checks actual paths, not XML content or test outcomes.
            await File.WriteAllTextAsync(file, "<TestRun />");
        }
        var calls = new List<string>();
        using var handler = new GitHubHttpMessageHandler((request, _) =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            if (failure == "cancelled")
            {
                throw new OperationCanceledException();
            }
            if (failure == "pagination" && request.RequestUri.Query.Length == 0)
            {
                return Task.FromResult(GitHubHttpMessageHandler.Json(
                    """{"jobs":[{"id":1,"name":"Build","steps":[]}]}""",
                    """<https://api.github.com/repos/microsoft/aspire/actions/runs/123/attempts/1/jobs?page=2>; rel="next" """));
            }
            return Task.FromResult(failure == "http" || failure == "history-http" && request.RequestUri.Query.Contains("filter=all", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) :
                GitHubHttpMessageHandler.Json(
                    (request.RequestUri.Query.Contains("filter=all", StringComparison.Ordinal) ? historical ?? current : current).ToJsonString()));
        });
        using var http = new HttpClient(handler);
        var exitCode = await Program.RunAsync(
            ["check-combined-reports", "--repo", "microsoft/aspire", "--run-id", "123", "--run-attempt", runAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--results-dir", failure == "directory" ? Path.Combine(results, "absent") : results],
            () => new GitHubApiClient(http, _ => Task.FromResult("test-token"), new Uri("https://api.github.com/")));

        return (exitCode, calls.ToArray());
    }

    private static JsonObject Jobs(string shortName, string runner)
        => new()
        {
            ["jobs"] = new JsonArray(new JsonObject
            {
                ["id"] = 77,
                ["name"] = $"Tests / No-package tests / {shortName} ({runner})",
                ["steps"] = new JsonArray(),
            }),
        };
}
