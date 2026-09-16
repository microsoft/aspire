// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Completions;
using Aspire.Cli.Commands;
using Aspire.Cli.Completions;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class CompletionsCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("bash")]
    [InlineData("fish")]
    [InlineData("pwsh")]
    [InlineData("zsh")]
    public async Task Script_WritesOnlyGeneratedScript(string shell)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var output = new StringWriter();
        var error = new StringWriter();

        var result = await command.Parse(["completions", "script", shell])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }).DefaultTimeout();

        Assert.Equal(0, result);
        Assert.Equal(string.Empty, error.ToString());
        await Verify(output.ToString(), "txt").UseParameters(shell);
    }

    [Theory]
    [InlineData("/bin/bash", "bash")]
    [InlineData("/usr/bin/zsh", "zsh")]
    [InlineData("/usr/local/bin/fish", "fish")]
    [InlineData("/usr/bin/pwsh", "pwsh")]
    [InlineData("/bin/sh", null)]
    [InlineData(null, null)]
    public void DetectShell_UsesSupportedLoginShell(string? shellPath, string? expected)
    {
        var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?> { ["SHELL"] = shellPath });

        Assert.Equal(expected, CompletionScripts.DetectShell(environment));
    }

    [Fact]
    public void DetectShell_DefaultsToPowerShellOnWindows()
    {
        Assert.Equal("pwsh", CompletionScripts.DetectShell(TestEnvironment.CreateWindows()));
    }

    [Theory]
    [InlineData("--banner completions script bash", true)]
    [InlineData("--help completions script bash", true)]
    [InlineData("-h completions script bash", true)]
    [InlineData("-? completions script bash", true)]
    [InlineData("/h completions script bash", true)]
    [InlineData("/? completions script bash", true)]
    [InlineData("-v completions script bash", true)]
    [InlineData("--log-level Debug completions script bash", true)]
    [InlineData("--log-level=Debug completions script bash", true)]
    [InlineData("--log-level:Debug completions script bash", true)]
    [InlineData("--non-interactive true completions script bash", true)]
    [InlineData("--start-debug-session completions script bash", true)]
    [InlineData("--start-debug-session true completions script bash", true)]
    [InlineData("--start-debug-session false completions script bash", true)]
    [InlineData("--start-debug-session=true completions script bash", true)]
    [InlineData("--start-debug-session:false completions script bash", true)]
    [InlineData("--start-debug-session=invalid completions script bash", true)]
    [InlineData("--start-debug-session invalid completions script bash", false)]
    [InlineData("--start-debug-session true run -- completions script bash", false)]
    [InlineData("--start-debug-session --log-file completions run", false)]
    [InlineData("--log-file completions run", false)]
    [InlineData("run -- completions script bash", false)]
    [InlineData("-- completions script bash", false)]
    public void CompletionDetection_UsesGlobalOptionArity(string arguments, bool expected)
    {
        Assert.Equal(expected, CompletionInvocation.Matches(arguments.Split(' ')));
    }

    [Fact]
    public void OrdinaryCommands_DoNotExposeExtensionDebugOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        Assert.False(command.Options.Contains(RootCommand.StartDebugSessionOption));
        Assert.NotEmpty(command.Parse(["--start-debug-session", "run"]).Errors);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("sh")]
    public async Task Script_RejectsUnsupportedShell(string shell)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var output = new StringWriter();
        var error = new StringWriter();

        var result = await command.Parse(["completions", "script", shell])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }).DefaultTimeout();

        Assert.NotEqual(0, result);
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData("aspire compl", "completions")]
    [InlineData("aspire completions scr", "script")]
    [InlineData("aspire completions script pws", "pwsh")]
    [InlineData("aspire run --apph", "--apphost")]
    [InlineData("aspire --log-level Deb", "Debug")]
    public void Suggestions_UseLiveCommandModel(string line, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        command.Aliases.Add("aspire");
        var output = new StringWriter();
        var error = new StringWriter();

        var result = CompletionInvocation.WriteSuggestions(command, ["[suggest]", line], output, error);

        Assert.Equal(0, result);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal([expected], output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Suggestions_UseCursorAndNeverInvokeCommand()
    {
        var command = new System.CommandLine.RootCommand();
        command.Aliases.Add("aspire");
        var run = new Command("run");
        run.SetAction(int (ParseResult _) => throw new InvalidOperationException("Completion must not run a command."));
        run.Options.Add(new Option<string>("--project"));
        command.Subcommands.Add(run);
        var output = new StringWriter();
        var error = new StringWriter();

        var result = CompletionInvocation.WriteSuggestions(command, ["[suggest:16]", "aspire run --pro --help"], output, error);

        Assert.Equal(0, result);
        Assert.Equal("--project\n", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("My Project.csproj", "destination with spaces", "destination with spaces suffix")]
    [InlineData("My \"Quoted\" Project.csproj", "destination \"quoted", "destination \"quoted\"")]
    [InlineData("My 'Quoted' Project.csproj", "destination 'quoted", "destination 'quoted'")]
    [InlineData("My Project.csproj", "", "destination")]
    public void Suggestions_TokensPreserveArgumentsAndNeverInvokeCommand(string projectPath, string word, string expected)
    {
        var command = new System.CommandLine.RootCommand();
        command.Options.Clear();
        command.SetAction(int (ParseResult _) => throw new InvalidOperationException("Completion must not run a command."));
        var run = new Command("run");
        run.SetAction(int (ParseResult _) => throw new InvalidOperationException("Completion must not run a command."));
        var project = new Option<string>("--project");
        var destination = new Argument<string>("destination");
        var completionRequested = false;
        destination.CompletionSources.Add(context =>
        {
            completionRequested = true;
            Assert.Equal(projectPath, context.ParseResult.GetValue(project));
            Assert.Equal(word, context.WordToComplete);
            return [new CompletionItem(expected)];
        });
        run.Options.Add(project);
        run.Arguments.Add(destination);
        command.Subcommands.Add(run);
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["[suggest:tokens]", "run", "--project", projectPath, word];

        Assert.True(CompletionInvocation.Matches(args));
        var result = CompletionInvocation.WriteSuggestions(command, args, output, error);

        Assert.Equal(0, result);
        Assert.True(completionRequested);
        Assert.Equal(expected + "\n", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("--log-level Deb")]
    [InlineData("run --log-level Deb")]
    [InlineData("run --log-level=Deb")]
    public void Suggestions_TokensCompleteOptionValues(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CompletionInvocation.WriteSuggestions(command, ["[suggest:tokens]", .. arguments.Split(' ')], output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal("Debug\n", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("[suggest:bad]", "aspire run")]
    [InlineData("[suggest:-1]", "aspire run")]
    [InlineData("[suggest:100]", "aspire run")]
    [InlineData("[suggest:]", "aspire run")]
    [InlineData("[suggest", "aspire run")]
    [InlineData("[suggest:99999999999999999]", "aspire run")]
    public void Suggestions_RejectMalformedRequests(string directive, string line)
    {
        var command = new System.CommandLine.RootCommand();
        command.SetAction(int (ParseResult _) => throw new InvalidOperationException("Completion must not run a command."));
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.NotEqual(0, CompletionInvocation.WriteSuggestions(command, [directive, line], output, error));
        Assert.Equal(string.Empty, output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Theory]
    [InlineData("suggest", "local", "")]
    [InlineData("suggest", "legacy-local", "")]
    [InlineData("suggest", "global", "")]
    [InlineData("suggest", "legacy-global", "")]
    [InlineData("script", "local", "")]
    [InlineData("script", "legacy-local", "")]
    [InlineData("script", "global", "")]
    [InlineData("script", "legacy-global", "")]
    [InlineData("script", "global", "--start-debug-session")]
    [InlineData("script", "global", "--start-debug-session true")]
    [InlineData("script", "global", "--start-debug-session false")]
    [InlineData("script", "global", "--start-debug-session=true")]
    [InlineData("script", "global", "--start-debug-session:false")]
    [InlineData("script", "global", "--start-debug-session=invalid")]
    public void CompletionStartup_DoesNotWriteUserState(string request, string location, string extensionArguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyConfig = Path.Combine(home.FullName, "globalsettings.json");
        File.WriteAllText(legacyConfig, """
            { "features:terminalCommandsEnabled": false }
            """);
        var settingsPath = location switch
        {
            "global" => Path.Combine(home.FullName, AspireConfigFile.FileName),
            "legacy-global" => legacyConfig,
            "legacy-local" => ConfigurationHelper.BuildPathToSettingsJsonFile(workspace.Path),
            _ => Path.Combine(workspace.Path, AspireConfigFile.FileName)
        };
        File.WriteAllText(settingsPath, """
            {
              // Preserve the nested value and leave this file byte-for-byte unchanged.
              "features:terminalCommandsEnabled": false,
              "features": { "terminalCommandsEnabled": true },
            }
            """);
        var originalFiles = Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        var originalDirectories = Directory.GetDirectories(workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;

        using (RemoteExecutor.Invoke(static async (homePath, requestKind, extensionOptions) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            // This must not connect to an inherited editor, start profiling, wait for a debugger,
            // write a first-use sentinel, or migrate the legacy config on Tab/script generation.
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", "invalid-endpoint");
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "false");
            var output = new StringWriter();
            var error = new StringWriter();
            string[] args = requestKind == "script"
                ? ["--banner", "--log-level", "Debug", "--cli-wait-for-debugger",
                    .. extensionOptions.Split(' ', StringSplitOptions.RemoveEmptyEntries), "completions", "script", "bash"]
                : ["[suggest]", System.CommandLine.RootCommand.ExecutableName + " term"];

            Assert.True(CompletionInvocation.Matches(args));
            var result = await Program.InvokeCompletionAsync(args, output, error).DefaultTimeout();

            if (extensionOptions == "--start-debug-session=invalid")
            {
                Assert.NotEqual(0, result);
                Assert.NotEmpty(error.ToString());
            }
            else
            {
                Assert.Equal(0, result);
                Assert.Equal(string.Empty, error.ToString());
                if (requestKind == "suggest")
                {
                    // This command is feature-gated, proving normalized settings were loaded.
                    Assert.Equal("terminal\n", output.ToString());
                }
                else
                {
                    Assert.NotEmpty(output.ToString());
                }
            }
        }, home.FullName, request, extensionArguments, options))
        {
        }

        Assert.Equal(originalFiles.Keys.Order(), Directory.GetFiles(workspace.Path, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(originalDirectories, Directory.GetDirectories(workspace.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in originalFiles)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Theory]
    [InlineData("suggest")]
    [InlineData("script")]
    public void CompletionStartup_InvalidConfigurationReturnsStartupFailure(string request)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var settingsPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string invalidJson = "{ broken";
        File.WriteAllText(settingsPath, invalidJson);
        var options = new RemoteInvokeOptions();
        options.StartInfo.WorkingDirectory = workspace.Path;

        using (RemoteExecutor.Invoke(static async (homePath, requestKind) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            var output = new StringWriter();
            var error = new StringWriter();
            string[] args = requestKind == "script"
                ? ["completions", "script", "bash"]
                : ["[suggest]", "aspire comp"];

            var result = await Program.InvokeCompletionAsync(args, output, error).DefaultTimeout();

            Assert.Equal(CliExitCodes.FailedToStartCli, result);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains(AspireConfigFile.FileName, error.ToString());
        }, home.FullName, request, options))
        {
        }

        Assert.Equal(invalidJson, File.ReadAllText(settingsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.FullName));
    }

    [Fact]
    public async Task CompletionHelp_MatchesOrdinaryNonExtensionHelp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var expected = new StringWriter();
        var actual = new StringWriter();
        var error = new StringWriter();
        string[] args = ["completions", "script", "--help"];

        var expectedExitCode = await command.Parse(args).InvokeAsync(new InvocationConfiguration { Output = expected, Error = error }).DefaultTimeout();
        var actualExitCode = await Program.InvokeCompletionAsync(args, actual, error).DefaultTimeout();

        Assert.Equal(0, expectedExitCode);
        Assert.Equal(0, actualExitCode);
        Assert.Equal(expected.ToString(), actual.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }
}
