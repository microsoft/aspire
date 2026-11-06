// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Aspire.TestTools;
using Xunit;
using ToolProcessResult = Aspire.TestTools.ProcessResult;

namespace Infrastructure.Tests;

public sealed class TemplateInjectionTests(ITestOutputHelper output)
{
    public static TheoryData<string, string[]> CommandLines => new()
    {
        { "", [] },
        { "--timeout 20m --filter-trait \"Partition=My Tests\"", ["--timeout", "20m", "--filter-trait", "Partition=My Tests"] },
        { "--filter-method '*.MyTest' --filter-not-trait quarantined=true", ["--filter-method", "*.MyTest", "--filter-not-trait", "quarantined=true"] },
        { "/p:VersionSuffix=pr.123 /p:Configuration=Release", ["/p:VersionSuffix=pr.123", "/p:Configuration=Release"] },
        { "\"\" '' \"a\\\"b\" 'a\\b'", ["", "", "a\"b", "a\\b"] },
        { "'$(touch injected)' '`touch injected`' '; touch injected' '& exit 42' '*.cs'", ["$(touch injected)", "`touch injected`", "; touch injected", "& exit 42", "*.cs"] },
        { "--filter-trait \"Partition=it's a test\"\n--timeout\t20m", ["--filter-trait", "Partition=it's a test", "--timeout", "20m"] },
    };

    [Theory]
    [MemberData(nameof(CommandLines))]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public Task ArgumentStringsRemainDataOnUnix(string commandLine, string[] expected)
        => AssertArguments("python3", commandLine, expected);

    [Theory]
    [MemberData(nameof(CommandLines))]
    [RequiresTools(["python"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Uses the Windows Python executable.")]
    public Task ArgumentStringsRemainDataOnWindows(string commandLine, string[] expected)
        => AssertArguments("python", commandLine, expected);

    [Theory]
    [InlineData("'unterminated")]
    [InlineData("\"unterminated")]
    [RequiresTools(["python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses the Unix Python executable.")]
    public async Task MalformedArgumentsFailBeforeProducingOutput(string commandLine)
    {
        var result = await Parse("python3", commandLine, "json");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("No closing quotation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Runs the Bash test enumeration step.")]
    public async Task EnumerationPreservesArgumentBoundariesAndOrder()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var scripts = Directory.CreateDirectory(Path.Combine(workspace.Path, ".github", "workflows"));
        File.Copy(Path.Combine(RepoRoot.Path, ".github", "workflows", "parse-command-line.py"), Path.Combine(scripts.FullName, "parse-command-line.py"));
        var build = Path.Combine(workspace.Path, "build.sh");
        await File.WriteAllTextAsync(build, "#!/usr/bin/env bash\nprintf '%s\\0' \"$@\" > \"$CAPTURE_PATH\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(build, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var script = Path.Combine(RepoRoot.Path, ".github", "workflows", "enumerate-test-metadata.sh");
        var capture = Path.Combine(workspace.Path, "arguments");
        var result = await RunAsync("bash", ["-e", "-o", "pipefail", script], workspace.Path,
            new Dictionary<string, string>
            {
                ["CAPTURE_PATH"] = capture,
                ["BUILD_ARGS"] = "--filter-trait \"Partition=two words\" '/p:VersionSuffix=$(touch injected)' '*.cs'",
                ["BEFORE_BUILD_PROPS_PATH"] = "props path'$(touch injected).props",
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["-test", "/p:TestRunnerName=TestEnumerationRunsheetBuilder", "/p:TestMatrixOutputPath=artifacts/canonical-test-matrix.json",
             "/p:GenerateCIPartitions=true", "/p:TestEnumerationMetadataOnly=true", "/p:SkipGenerateCanonicalTestMatrix=true",
             "/p:BeforeBuildPropsPath=props path'$(touch injected).props", "/bl", "--filter-trait", "Partition=two words",
             "/p:VersionSuffix=$(touch injected)", "*.cs"],
            (await File.ReadAllTextAsync(capture)).Split('\0')[..^1]);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "injected")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["pwsh"])]
    public async Task MatrixFileHandlesLargeDataAndSkippedEnumeration(bool enumerated)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var matrix = enumerated
            ? JsonSerializer.Serialize(new { include = Array.Empty<object>(), data = new string('x', 200_000) + "'@\n$(throw 'injected')\n@'" })
            : "{\"include\":[]}";
        if (enumerated)
        {
            Assert.True(matrix.Length > 128 * 1024);
            Directory.CreateDirectory(Path.Combine(workspace.Path, "artifacts"));
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "artifacts", "github-test-matrix.json"), matrix);
        }

        var script = Path.Combine(RepoRoot.Path, ".github", "workflows", "write-test-matrix.ps1");
        var result = await RunAsync("pwsh", ["-NoProfile", "-File", script], workspace.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(matrix, await File.ReadAllTextAsync(Path.Combine(workspace.Path, "all_tests.json")));
    }

    private async Task AssertArguments(string python, string commandLine, string[] expected)
    {
        var json = await Parse(python, commandLine, "json");
        Assert.Equal(0, json.ExitCode);
        Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(json.Output));

        var nul = await Parse(python, commandLine, "nul");
        Assert.Equal(0, nul.ExitCode);
        Assert.Equal(string.Concat(expected.Select(argument => argument + '\0')), nul.Output);
    }

    private Task<ToolProcessResult> Parse(string python, string commandLine, string format)
        => RunAsync(
            python,
            [".github/workflows/parse-command-line.py", "--format", format, "TEST_ARGUMENTS"],
            RepoRoot.Path,
            new Dictionary<string, string> { ["TEST_ARGUMENTS"] = commandLine });

    private async Task<ToolProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = await ProcessRunner.RunAsync(executable, arguments, workingDirectory, environment);
        output.WriteLine(result.Output);
        output.WriteLine(result.Error);

        return result;
    }
}
