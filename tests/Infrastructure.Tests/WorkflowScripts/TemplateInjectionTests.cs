// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Aspire.TestTools;
using Xunit;
using YamlDotNet.RepresentationModel;
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

    [Theory]
    [InlineData("run-tests.yml", "Build test project")]
    [InlineData("run-tests.yml", "Build and archive test project")]
    [InlineData("build-cli-native-archives.yml", "Build RID-specific packages")]
    [InlineData("build-cli-native-archives.yml", "Build CLI packages")]
    [RequiresTools(["pwsh"])]
    public async Task WindowsBuildStepsForwardLiteralPropertiesThroughRepositoryBuild(string workflow, string stepName)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        CopyParser(workspace.Path);
        var eng = workspace.CreateDirectory("eng");
        File.Copy(Path.Combine(RepoRoot.Path, "eng", "build.ps1"), Path.Combine(eng.FullName, "build.ps1"));
        if (OperatingSystem.IsWindows())
        {
            // A call-site regression must exercise the real batch wrapper, not fail on a missing file.
            File.Copy(Path.Combine(RepoRoot.Path, "build.cmd"), Path.Combine(workspace.Path, "build.cmd"));
        }
        var common = workspace.CreateDirectory("eng/common");
        // Exercise eng/build.ps1's real quoting/forwarding boundary, stopping before
        // Arcade performs a build. build.cmd would evaluate the marker expression.
        await File.WriteAllTextAsync(Path.Combine(common.FullName, "build.ps1"),
            "ConvertTo-Json -InputObject @($args | Where-Object { \"$_\" -like '/p:*' }) -Compress | Set-Content $env:CAPTURE_PATH\nWrite-Output 'Build captured.'\n$global:LASTEXITCODE = 0\n");
        var script = RenderWindowsScript(ReadStep(workflow, stepName))
            .Replace("${{ fromJson(inputs.properties).enablePlaywrightInstall != true && '/p:InstallBrowsersForPlaywright=false' || '' }}", "/p:InstallBrowsersForPlaywright=false", StringComparison.Ordinal);
        var capture = Path.Combine(workspace.Path, "arguments.json");
        var environment = new Dictionary<string, string>
        {
            ["CAPTURE_PATH"] = capture,
            ["GITHUB_WORKSPACE"] = workspace.Path,
            ["TEST_PROJECT_PATH"] = "tests/test project.csproj",
            ["BUILD_CONFIGURATION"] = "Release",
            ["TARGET_RIDS"] = "win-arm64",
            ["ASPIRE_CLI_CHANNEL"] = "pr-123",
            ["TreatWarningsAsErrors"] = "false",
            ["VERSION_OVERRIDE_ARG"] = "'/p:VersionSuffix=$(New-Item injected)' '/p:Label=two words' \"/p:Literal=it's&data\"",
        };
        if (workflow is "run-tests.yml" or "reproduce-flaky-tests.yml")
        {
            await RunWindowsSetup(workflow, workspace.Path, environment, resolveSdk: false);
            Assert.Equal(Path.Combine(eng.FullName, "build.ps1"),
                Path.GetFullPath(environment["BUILD_SCRIPT"].Replace('\\', Path.DirectorySeparatorChar), workspace.Path));
        }
        var path = Path.Combine(workspace.Path, "step.ps1");
        await File.WriteAllTextAsync(path, ExpandPaths(script, workspace.Path));
        var result = await RunAsync("pwsh", ["-NoProfile", "-File", path], workspace.Path, environment);

        Assert.Equal(0, result.ExitCode);
        var properties = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(capture))!;
        string[] expected = stepName switch
        {
            "Build test project" => ["/p:InstallBrowsersForPlaywright=false"],
            "Build and archive test project" => ["/p:PrepareForHelix=true", "/p:ArchiveTests=true", "/p:InstallBrowsersForPlaywright=false"],
            "Build RID-specific packages" => ["/p:ContinuousIntegrationBuild=true", "/p:BuildBundleDepsOnly=true", "/p:TargetRids=win-arm64", "/p:AspireCliChannel=pr-123"],
            _ => ["/p:ContinuousIntegrationBuild=true", "/p:SkipManagedBuild=true", "/p:TargetRids=win-arm64",
                $"/p:BundlePayloadPath={workspace.Path}/artifacts/bundle/aspire-ci-bundlepayload-win-arm64.tar.gz", "/p:AspireCliChannel=pr-123"],
        };
        Assert.Equal([.. expected, "/p:VersionSuffix=$(New-Item injected)", "/p:Label=two words", "/p:Literal=it's&data"], properties);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "injected")));
    }

    [Fact]
    [RequiresTools(["pwsh", "python", "dotnet"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Exercises native Windows executable argument passing.")]
    public async Task WindowsSdkInvocationsPreserveArgumentsAtTheNativeExecutable()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        CopyParser(workspace.Path);
        var common = workspace.CreateDirectory("eng/common");
        await File.WriteAllTextAsync(Path.Combine(common.FullName, "tools.ps1"), """
            function InitializeDotNetCli([bool]$install, [bool]$createSdkLocationFile) {
              if (!$install -or !$createSdkLocationFile) { throw 'SDK bootstrap settings changed.' }
              return $env:SDK_CAPTURE_ROOT
            }
            """);
        var app = workspace.CreateDirectory("capture");
        await File.WriteAllTextAsync(Path.Combine(app.FullName, "capture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>dotnet</AssemblyName></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(app.FullName, "Program.cs"), """
            System.IO.File.WriteAllText(System.Environment.GetEnvironmentVariable("CAPTURE_PATH"), System.Text.Json.JsonSerializer.Serialize(args));
            System.Console.WriteLine("total: 1");
            """);
        var built = await RunAsync("dotnet", ["build", Path.Combine(app.FullName, "capture.csproj"), "-o", Path.Combine(app.FullName, "out")], workspace.Path);
        Assert.Equal(0, built.ExitCode);
        // Supply the wrapper's bootstrap output so reverting the call exercises CMD reparsing.
        File.Copy(Path.Combine(RepoRoot.Path, "dotnet.cmd"), Path.Combine(workspace.Path, "dotnet.cmd"));
        var toolset = workspace.CreateDirectory("artifacts/toolset");
        await File.WriteAllTextAsync(Path.Combine(toolset.FullName, "sdk.txt"), Path.Combine(app.FullName, "out"));
        var capture = Path.Combine(workspace.Path, "arguments.json");
        var environment = new Dictionary<string, string>
        {
            ["CAPTURE_PATH"] = capture,
            ["SDK_CAPTURE_ROOT"] = Path.Combine(app.FullName, "out"),
            ["GITHUB_WORKSPACE"] = workspace.Path,
            ["GITHUB_ENV"] = Path.Combine(workspace.Path, "github-env"),
            ["TEST_PROJECT_PATH"] = "tests/test project.csproj",
            ["TEST_SHORT_NAME"] = "Capture",
            ["BUILD_CONFIGURATION"] = "Release",
            ["TARGET_RIDS"] = "win-arm64",
            ["EFFECTIVE_MTP_BASE_ARGS"] = "--timeout 20m",
            ["EXTRA_TEST_ARGS"] = "--filter-trait 'Partition=x&echo injected>injected' '--literal=$(New-Item injected)'",
            ["TEST_FILTER"] = "--filter-trait 'Partition=x&echo injected>injected' '--literal=$(New-Item injected)'",
            ["VERSION_OVERRIDE_ARG"] = "'/p:VersionSuffix=$(New-Item injected)' '/p:Label=two words'",
            ["ITERATIONS_PER_RUNNER"] = "1",
        };
        File.Copy(Path.Combine(RepoRoot.Path, ".github/workflows/normalize-mtp-exit-code.ps1"),
            Path.Combine(workspace.Path, ".github/workflows/normalize-mtp-exit-code.ps1"));
        foreach (var (workflow, step) in new[]
        {
            ("run-tests.yml", "Run tests (Windows)"),
            ("reproduce-flaky-tests.yml", "Run test iterations (Windows)"),
            ("build-cli-native-archives.yml", "Build bundle payload archive"),
        })
        {
            var stepEnvironment = new Dictionary<string, string>(environment);
            if (workflow is "run-tests.yml" or "reproduce-flaky-tests.yml")
            {
                await RunWindowsSetup(workflow, workspace.Path, stepEnvironment, resolveSdk: true);
                Assert.Equal(Path.Combine(app.FullName, "out", "dotnet.exe"), stepEnvironment["DOTNET_SCRIPT"]);
            }
            var path = Path.Combine(workspace.Path, "step.ps1");
            // Replace unrelated heartbeat/process cleanup, not the invocation under test.
            await File.WriteAllTextAsync(path, "function Start-Process { $null }\nfunction Get-Process { @() }\nfunction Start-Sleep {}\n" +
                ExpandPaths(RenderWindowsScript(ReadStep(workflow, step)), workspace.Path).Replace("${{ inputs.ignoreTestFailures }}", "false", StringComparison.Ordinal));
            File.Delete(capture);
            var result = await RunAsync("pwsh", ["-NoProfile", "-File", path], workspace.Path, stepEnvironment);
            Assert.Equal(0, result.ExitCode);
            var arguments = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(capture))!;
            string[] expected = step switch
            {
                "Run tests (Windows)" => ["test", "--project", "tests/test project.csproj", "/p:ContinuousIntegrationBuild=true", "/p:TrxFileNamePrefix=Capture",
                    $"-bl:{workspace.Path}/testresults/test.binlog", "--no-launch-profile", "--no-restore", "--no-build", "--", "--timeout", "20m", "--report-trx",
                    "--results-directory", $"{workspace.Path}/testresults", "--filter-trait", "Partition=x&echo injected>injected", "--literal=$(New-Item injected)"],
                "Run test iterations (Windows)" => ["test", "--project", "tests/test project.csproj", "--no-restore", "--no-build", "--", "--timeout", "10m",
                    "--results-directory", $"{workspace.Path}/testresults", "--filter-trait", "Partition=x&echo injected>injected", "--literal=$(New-Item injected)"],
                _ => ["msbuild", "eng/Bundle.proj", "/restore", "/p:Configuration=Release", "/p:TargetRid=win-arm64", "/p:BundleVersion=ci-bundlepayload",
                    "/p:SkipNativeBuild=true", "/p:ContinuousIntegrationBuild=true", $"/bl:{workspace.Path}/artifacts/log/Release/BundlePayload.binlog",
                    "/p:VersionSuffix=$(New-Item injected)", "/p:Label=two words"],
            };
            Assert.Equal(expected, arguments);
            Assert.False(File.Exists(Path.Combine(workspace.Path, "injected")));
        }
    }

    [Theory]
    [InlineData(0, "Passed", true, 0, false)]
    [InlineData(4, "Passed", true, 0, false)]
    [InlineData(5, "Passed", true, 0, true)]
    [InlineData(6, "Passed", true, 0, false)]
    [InlineData(5, "Failed", true, 0, false)]
    [InlineData(5, "NotExecuted", true, 0, false)]
    [InlineData(5, "Passed", false, 0, false)]
    [InlineData(5, "Passed", true, 17, false)]
    [RequiresTools(["pwsh", "python3"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses Unix executable fixtures to exercise the workflow's report gate.")]
    public async Task WindowsForwardingWorkflowRequiresFiveFreshPassedResults(int count, string outcome, bool writeReport, int testExitCode, bool succeeds)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var common = workspace.CreateDirectory("eng/common");
        await File.WriteAllTextAsync(Path.Combine(common.FullName, "tools.ps1"), """
            function InitializeDotNetCli([bool]$install, [bool]$createSdkLocationFile) {
              if (!$install -or !$createSdkLocationFile) { throw 'SDK bootstrap settings changed.' }
              return $env:SDK_CAPTURE_ROOT
            }
            """);
        var sdk = workspace.CreateDirectory("sdk");
        var sdkExecutable = Path.Combine(sdk.FullName, "dotnet.exe");
        await File.WriteAllTextAsync(sdkExecutable, """
            #!/usr/bin/env python3
            import json, os, pathlib, shutil, sys
            if sys.argv[1:] == ["--version"]:
                print("fixture-sdk-version")
                sys.exit(0)
            arguments = sys.argv[1:]
            pathlib.Path(os.environ["CAPTURE_PATH"]).write_text(json.dumps(arguments))
            pathlib.Path(os.environ["ENV_CAPTURE_PATH"]).write_text(json.dumps({
                name: os.environ.get(name) for name in
                ["DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]
            }))
            if os.environ["WRITE_REPORT"] == "true":
                directory = arguments[arguments.index("--results-directory") + 1]
                filename = arguments[arguments.index("--report-trx-filename") + 1]
                shutil.copyfile(os.environ["SOURCE_TRX"], pathlib.Path(directory) / filename)
            sys.exit(int(os.environ["TEST_EXIT_CODE"]))
            """);
        File.Copy(sdkExecutable, Path.Combine(sdk.FullName, "dotnet"));
        var bin = workspace.CreateDirectory("bin");
        await File.WriteAllTextAsync(Path.Combine(bin.FullName, "python"), "#!/usr/bin/env bash\nexit 0\n");
        await File.WriteAllTextAsync(Path.Combine(bin.FullName, "git"), "#!/usr/bin/env bash\nprintf 'fixture-checkout-sha\\n'\n");
        if (!OperatingSystem.IsWindows())
        {
            foreach (var executable in new[] { sdkExecutable, Path.Combine(sdk.FullName, "dotnet"), Path.Combine(bin.FullName, "python"), Path.Combine(bin.FullName, "git") })
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        var sourceTrx = TestTrxBuilder.CreateTrxFile(Path.Combine(workspace.Path, "source.trx"),
            [.. Enumerable.Range(0, count).Select(index => new TestTrxCase($"Tests.Case{index}", $"Case{index}", outcome))]);
        var resultsDirectory = Path.Combine(workspace.Path, "artifacts", "log", "Release", "windows-template-forwarding");
        var trxPath = Path.Combine(resultsDirectory, "windows-template-forwarding.trx");
        // A previously passing receipt must not make a missing new report look successful.
        TestTrxBuilder.CreateTrxFile(trxPath,
            [.. Enumerable.Range(0, 5).Select(index => new TestTrxCase($"Tests.Old{index}", $"Old{index}", "Passed"))]);
        var capture = Path.Combine(workspace.Path, "arguments.json");
        var environmentCapture = Path.Combine(workspace.Path, "environment.json");
        var script = Path.Combine(workspace.Path, "verify.ps1");
        await File.WriteAllTextAsync(script, ReadStep("build-cli-native-archives.yml", "Verify Windows template argument forwarding"));
        var result = await RunAsync("pwsh", ["-NoProfile", "-File", script], workspace.Path, new Dictionary<string, string>
        {
            ["GITHUB_WORKSPACE"] = workspace.Path,
            ["BUILD_CONFIGURATION"] = "Release",
            ["SDK_CAPTURE_ROOT"] = sdk.FullName,
            ["PATH"] = bin.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CAPTURE_PATH"] = capture,
            ["ENV_CAPTURE_PATH"] = environmentCapture,
            ["SOURCE_TRX"] = sourceTrx,
            ["WRITE_REPORT"] = writeReport ? "true" : "false",
            ["TEST_EXIT_CODE"] = testExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        Assert.Equal(succeeds, result.ExitCode == 0);
        Assert.Equal(
            ["test", "--project", "tests/Infrastructure.Tests/Infrastructure.Tests.csproj", "--configuration", "Release", "--no-launch-profile", "--",
                "--filter-method", "*.WindowsBuildStepsForwardLiteralPropertiesThroughRepositoryBuild",
                "--filter-method", "*.WindowsSdkInvocationsPreserveArgumentsAtTheNativeExecutable",
                "--filter-not-trait", "quarantined=true", "--filter-not-trait", "outerloop=true",
                "--report-trx", "--report-trx-filename", "windows-template-forwarding.trx", "--results-directory", resultsDirectory],
            JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(capture))!);
        var forwardedEnvironment = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(environmentCapture))!;
        Assert.Equal(sdk.FullName, forwardedEnvironment["DOTNET_ROOT"]);
        Assert.Equal(sdk.FullName, forwardedEnvironment["DOTNET_ROOT_X64"]);
        Assert.Equal("1", forwardedEnvironment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        Assert.Contains("Windows forwarding checkout SHA: fixture-checkout-sha", result.Output, StringComparison.Ordinal);
        Assert.Contains("Windows forwarding SDK version: fixture-sdk-version", result.Output, StringComparison.Ordinal);
        Assert.Equal(writeReport, File.Exists(trxPath));
    }

    [Theory]
    [InlineData(".github/workflows/reproduce-flaky-tests.yml", false)]
    [InlineData(".github/workflows/release-github-tasks.yml", true)]
    [RequiresTools(["bash", "jq"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Executes the Linux upstream CI admission action.")]
    public async Task ReproducerOnlyChangesReachTheUpstreamCiAdmissionAction(string changedPath, bool skipsCi)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var infrastructure = workspace.CreateDirectory("eng/github-ci");
        foreach (var file in new[] { "ci-skip-entirely-patterns.txt", "resolve-comparison-base.sh" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", "github-ci", file), Path.Combine(infrastructure.FullName, file));
        }
        var bin = workspace.CreateDirectory("bin");
        var git = Path.Combine(bin.FullName, "git");
        // Only Git's pinned comparison/diff boundary is stubbed. The production
        // admission action and its complete skip-pattern file perform the decision.
        await File.WriteAllTextAsync(git, """
            #!/usr/bin/env bash
            if [ "$1" = "--no-pager" ]; then shift; fi
            case "$1" in
              rev-parse)
                case "$2" in
                  --is-shallow-repository) printf 'false\n' ;;
                  HEAD) printf 'merge\n' ;;
                  HEAD^1) printf 'base\n' ;;
                  *) exit 1 ;;
                esac ;;
              rev-list) printf 'merge base head\n' ;;
              cat-file) exit 0 ;;
              merge-base) printf 'base\n' ;;
              diff) printf '%s\n' "$CHANGED_PATH" ;;
              *) exit 1 ;;
            esac
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var yaml = new YamlStream();
        using (var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "actions", "check-changed-files", "action.yml")))
        {
            yaml.Load(reader);
        }
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var runs = (YamlMappingNode)root.Children[new YamlScalarNode("runs")];
        var step = (YamlMappingNode)Assert.Single(((YamlSequenceNode)runs.Children[new YamlScalarNode("steps")]).Children);
        var script = Path.Combine(workspace.Path, "admission.sh");
        await File.WriteAllTextAsync(script,
            step.Children[new YamlScalarNode("run")].ToString().Replace("${{ github.event_name }}", "pull_request", StringComparison.Ordinal));
        var githubOutput = Path.Combine(workspace.Path, "github-output");
        var result = await RunAsync("bash", [script], workspace.Path, new Dictionary<string, string>
        {
            ["GITHUB_WORKSPACE"] = workspace.Path,
            ["GITHUB_OUTPUT"] = githubOutput,
            ["GITHUB_EVENT_NAME"] = "pull_request",
            ["GITHUB_REF"] = "refs/pull/21033/merge",
            ["GITHUB_SHA"] = "merge",
            ["PR_BASE_SHA"] = "base",
            ["PR_HEAD_SHA"] = "head",
            ["PATTERNS_FILE"] = "eng/github-ci/ci-skip-entirely-patterns.txt",
            ["PATH"] = bin.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CHANGED_PATH"] = changedPath,
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [$"only_changed={skipsCi.ToString().ToLowerInvariant()}", "no_changes=false"],
            (await File.ReadAllLinesAsync(githubOutput)).Take(2));
    }

    private async Task RunWindowsSetup(string workflow, string workspace, Dictionary<string, string> environment, bool resolveSdk)
    {
        environment["GITHUB_ENV"] = Path.Combine(workspace, "github-env");
        environment["GITHUB_PATH"] = Path.Combine(workspace, "github-path");
        await File.WriteAllTextAsync(environment["GITHUB_ENV"], "");
        var setup = ReadStep(workflow, "Setup vars (Windows)");
        if (resolveSdk)
        {
            setup += "\n" + ReadStep(workflow, "Resolve SDK executable (Windows)");
        }
        var path = Path.Combine(workspace, "setup.ps1");
        await File.WriteAllTextAsync(path, ExpandPaths(setup, workspace));
        var result = await RunAsync("pwsh", ["-NoProfile", "-File", path], workspace, environment);
        Assert.Equal(0, result.ExitCode);
        // GITHUB_ENV is applied between steps; later assignments replace earlier ones.
        foreach (var line in await File.ReadAllLinesAsync(environment["GITHUB_ENV"]))
        {
            var separator = line.IndexOf('=');
            Assert.True(separator > 0, $"Invalid GITHUB_ENV assignment: {line}");
            environment[line[..separator]] = line[(separator + 1)..];
        }
    }

    private static string RenderWindowsScript(string script)
        // Evaluate the workflow's OS selector, not a particular expected executable.
        // For example, runner.os == 'Windows' && './eng/build.ps1' || './build.sh'.
        => Regex.Replace(script, """\$\{\{\s*runner\.os\s*==\s*'Windows'\s*&&\s*'([^']+)'\s*\|\|\s*'[^']+'\s*\}\}""", "$1");

    private static void CopyParser(string workspace)
    {
        var scripts = Directory.CreateDirectory(Path.Combine(workspace, ".github", "workflows"));
        File.Copy(Path.Combine(RepoRoot.Path, ".github", "workflows", "parse-command-line.py"), Path.Combine(scripts.FullName, "parse-command-line.py"));
    }

    private static string ExpandPaths(string script, string workspace)
        => script.Replace("${{ github.workspace }}", workspace, StringComparison.Ordinal)
            .Replace("${{ runner.temp }}", workspace, StringComparison.Ordinal);

    private static string ReadStep(string workflow, string stepName)
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", workflow));
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var steps = jobs.Children.Values.Cast<YamlMappingNode>()
            .Where(job => job.Children.ContainsKey(new YamlScalarNode("steps")))
            .SelectMany(job => ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>());
        return Assert.Single(steps, step => step.Children.TryGetValue(new YamlScalarNode("name"), out var name) && name.ToString() == stepName)
            .Children[new YamlScalarNode("run")].ToString();
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
