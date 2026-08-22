// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class CliPlatformSmokeLifecycleTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;
    private readonly TemporaryWorkspace _workspace;

    public CliPlatformSmokeLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _harnessPath = Path.Combine(
            RepoRoot.Path,
            "tests",
            "Infrastructure.Tests",
            "WorkflowScripts",
            "cli-platform-smoke-lifecycle.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task DisposesPtyWhenShellStartupTimesOut()
    {
        HarnessResult result = await RunHarnessAsync("startup-disposal");

        Assert.Equal(
            "startup disposal: Timed out after 10 second(s) shell ready probe.",
            result.ErrorMessage);
        Assert.Equal(1, result.KillCount);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task DisposesPtyAndFlushesArtifactsWhenCommandTimesOut()
    {
        HarnessResult result = await RunHarnessAsync("command-timeout");

        Assert.Equal(
            "command timeout: Timed out after 0 second(s) waiting for command completion.",
            result.ErrorMessage);
        Assert.Equal(1, result.KillCount);
        Assert.True(result.LogExists);
        Assert.True(result.CastExists);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task WaitsForCallbackSettlementAfterScenarioTimeout()
    {
        HarnessResult result = await RunHarnessAsync("callback-settlement");

        Assert.Equal("callback settlement: Timed out after 0 second(s).", result.ErrorMessage);
        Assert.True(result.CallbackSettled);
        Assert.Equal(1, result.KillCount);
    }

    private async Task<HarnessResult> RunHarnessAsync(string operation)
    {
        using NodeCommand command = new(_output, operation);
        command.WithWorkingDirectory(RepoRoot.Path).WithTimeout(TimeSpan.FromSeconds(10));

        CommandResult result = await command.ExecuteScriptAsync(_harnessPath, operation, _workspace.Path);
        result.EnsureSuccessful();

        HarnessResult? response = JsonSerializer.Deserialize<HarnessResult>(result.Output, s_jsonOptions);
        return Assert.IsType<HarnessResult>(response);
    }

    private sealed class HarnessResult
    {
        public bool CallbackSettled { get; init; }
        public bool CastExists { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public int KillCount { get; init; }
        public bool LogExists { get; init; }
    }
}
