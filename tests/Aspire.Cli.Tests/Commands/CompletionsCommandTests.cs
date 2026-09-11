// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Commands;
using Aspire.Cli.Completions;
using Aspire.Cli.Tests.Utils;
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
    [InlineData("-v completions script bash", true)]
    [InlineData("--log-level Debug completions script bash", true)]
    [InlineData("--log-level=Debug completions script bash", true)]
    [InlineData("--log-level:Debug completions script bash", true)]
    [InlineData("--non-interactive true completions script bash", true)]
    [InlineData("--log-file completions run", false)]
    [InlineData("run -- completions script bash", false)]
    [InlineData("-- completions script bash", false)]
    public void CompletionDetection_UsesGlobalOptionArity(string arguments, bool expected)
    {
        Assert.Equal(expected, CompletionInvocation.Matches(arguments.Split(' ')));
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
    [InlineData("suggest")]
    [InlineData("script")]
    public void CompletionStartup_DoesNotWriteUserState(string request)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("aspire-home");
        var legacyConfig = Path.Combine(home.FullName, "globalsettings.json");
        File.WriteAllText(legacyConfig, "{}");

        using var remote = RemoteExecutor.Invoke(async (homePath, requestKind) =>
        {
            Environment.SetEnvironmentVariable("ASPIRE_HOME", homePath);
            // This must not connect to an inherited editor, start profiling, wait for a debugger,
            // write a first-use sentinel, or migrate the legacy config on Tab/script generation.
            Environment.SetEnvironmentVariable("ASPIRE_EXTENSION_ENDPOINT", "invalid-endpoint");
            Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "false");
            var output = new StringWriter();
            var error = new StringWriter();
            string[] args = requestKind == "script"
                ? ["--banner", "--log-level", "Debug", "--cli-wait-for-debugger", "completions", "script", "bash"]
                : ["[suggest]", System.CommandLine.RootCommand.ExecutableName + " run --apphost"];

            var result = await Program.InvokeCompletionAsync(args, output, error).DefaultTimeout();

            Assert.Equal(0, result);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Equal(["globalsettings.json"], Directory.GetFiles(homePath, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
            Assert.Empty(Directory.GetDirectories(homePath));
        }, home.FullName, request);
    }
}
