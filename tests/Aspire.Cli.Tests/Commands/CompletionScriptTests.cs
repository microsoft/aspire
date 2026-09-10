// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Cli.Completions;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class CompletionScriptTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [RequiresTools(["pwsh"])]
    [InlineData("aspire ", "", "aspire ")]
    [InlineData("aspire na", "na", "aspire na")]
    [InlineData("echo x; aspire na", "na", "aspire na")]
    [InlineData("& 'C:\\Program Files\\Aspire\\aspire.exe' na", "na", "aspire na")]
    [InlineData("aspire 'na", "'na", "aspire 'na")]
    public async Task PowerShell_PreservesArgumentsAndQuotesSuggestions(string input, string word, string expectedLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "completion.ps1");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("pwsh"));
        var script = """
            $ErrorActionPreference = 'Stop'
            function Register-ArgumentCompleter {
                param([switch]$Native, $CommandName, $ScriptBlock)
                $script:completer = $ScriptBlock
            }
            function aspire {
                if ($args.Count -ne 2 -or $args[0] -ne '[suggest]') { throw 'Invalid suggestion protocol' }
                $script:line = $args[1]
                'name with space'
                "name'quote"
                'name$(Get-Process)'
                '--help'
            }
            . $env:COMPLETION_SCRIPT
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($env:COMPLETION_INPUT, [ref]$null, [ref]$null)
            $command = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))[-1]
            $results = @(& $script:completer $env:COMPLETION_WORD $command $env:COMPLETION_INPUT.Length)
            $script:line
            $results | ForEach-Object CompletionText
            """;
        var output = await RunShellAsync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["COMPLETION_INPUT"] = input,
                ["COMPLETION_WORD"] = word
            });

        string[] expected = word.Length == 0
            ? [expectedLine, "'name with space'", "'name''quote'", "'name$(Get-Process)'", "--help"]
            : [expectedLine, "'name with space'", "'name''quote'", "'name$(Get-Process)'"];
        Assert.Equal(expected, output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_CompletesNpmStyleScriptShim()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "completion.ps1");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("pwsh"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.ps1"), """
            if ($args.Count -ne 2 -or $args[0] -ne '[suggest]' -or $args[1] -ne 'aspire comp') {
                throw 'Invalid completion request'
            }
            'completions'
            """);
        var script = """
            $ErrorActionPreference = 'Stop'
            . $env:COMPLETION_SCRIPT
            $line = 'aspire comp'
            (TabExpansion2 $line $line.Length).CompletionMatches.CompletionText
            """;
        var output = await RunShellAsync("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["PATH"] = workspace.WorkspaceRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.Equal("completions", output.Trim());
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a Unix executable shim and permissions.")]
    public async Task Bash_UsesCursorAndPreservesSuggestionBoundaries()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "completion.bash");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("bash"));
        var binaryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        await File.WriteAllTextAsync(binaryPath, """
            #!/bin/sh
            test "$#" -eq 2 && test "$1" = '[suggest]' && test "$2" = 'aspire na' || exit 1
            printf '%s\n' 'name with space' "name'quote" 'name$(id)' '--help'
            """ + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var script = """
            source "$COMPLETION_SCRIPT"
            COMP_LINE='aspire na --help'
            COMP_POINT=9
            COMP_WORDS=(aspire na --help)
            COMP_CWORD=1
            _aspire_complete
            printf '%s\n' "${COMPREPLY[@]}"
            """;
        var output = await RunShellAsync("bash", ["--noprofile", "--norc", "-c", script],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["PATH"] = workspace.WorkspaceRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.Equal(["name\\ with\\ space", "name\\'quote", "name\\$\\(id\\)"], output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    [RequiresTools(["fish"])]
    public async Task Fish_RepeatedLoadingPreservesOtherRegistrations()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.fish");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("fish"));
        var script = """
            complete --command aspire --arguments custom
            source "$COMPLETION_SCRIPT"
            source "$COMPLETION_SCRIPT"
            complete --command aspire | count
            """;
        var output = await RunShellAsync("fish", ["--no-config", "-c", script],
            new Dictionary<string, string> { ["COMPLETION_SCRIPT"] = completionPath });

        Assert.Equal("2", output.Trim());
    }

    [Fact]
    [RequiresTools(["zsh"])]
    public async Task Zsh_InitializesCompletionForFreshProfile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.zsh");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("zsh"));
        var script = """
            source "$COMPLETION_SCRIPT"
            [[ "${_comps[aspire]}" == _aspire ]] || exit 1
            print -r -- registered
            """;
        var output = await RunShellAsync("zsh", ["-f", "-c", script],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["HOME"] = workspace.WorkspaceRoot.FullName,
                ["ZDOTDIR"] = workspace.WorkspaceRoot.FullName
            });

        Assert.Equal("registered", output.Trim());
    }

    private static async Task<string> RunShellAsync(string executable, string[] arguments, Dictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().DefaultTimeout();
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Equal(string.Empty, await stderr);
            return await stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().DefaultTimeout();
            }
        }
    }
}
