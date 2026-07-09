// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class GenerateSpecializedTestProjectsListTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    public GenerateSpecializedTestProjectsListTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ExcludesPlaygroundProjectFromSpecializedTestProjectList()
    {
        var outputFile = Path.Combine(_workspace.Path, "BeforeBuildProps.props");
        var scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "generate-specialized-test-projects-list.sh");

        var result = await RunBashAsync(scriptPath, ["OuterloopTest", outputFile]);

        result.EnsureSuccessful("generate-specialized-test-projects-list.sh failed.");

        var props = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj", props);
        Assert.DoesNotContain("tests/Aspire.Playground.Tests/Aspire.Playground.Tests.csproj", props);
    }

    private async Task<CommandResult> RunBashAsync(string scriptPath, string[] arguments)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(scriptPath);

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cancellationTokenSource.Token);

        var output = await outputTask + await errorTask;
        _output.WriteLine(output);

        return new CommandResult(process.ExitCode, output);
    }

    private sealed record CommandResult(int ExitCode, string Output)
    {
        public void EnsureSuccessful(string message)
        {
            Assert.True(ExitCode == 0, $"{message}{Environment.NewLine}{Output}");
        }
    }
}
