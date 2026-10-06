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

    [Theory]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a Unix executable shim and permissions.")]
    [InlineData("na")]
    [InlineData("naXYZ")]
    public async Task Bash_UsesCursorAndPreservesSuggestionBoundaries(string word)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "completion.bash");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("bash"));
        var binaryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        await File.WriteAllTextAsync(binaryPath, ("""
            #!/bin/sh
            test "$#" -eq 2 && test "$1" = '[suggest:tokens]' && test "$2" = 'na' || exit 1
            printf '%s\n' 'name with space' "name'quote" 'name$(id)'
            """ + "\n").ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var script = """
            source "$COMPLETION_SCRIPT"
            COMP_LINE="aspire $COMPLETION_WORD --help"
            COMP_POINT=9
            COMP_WORDS=(aspire "$COMPLETION_WORD" --help)
            COMP_CWORD=1
            _aspire_complete
            printf '%s\n' "${COMPREPLY[@]}"
            """;
        var output = await RunShellAsync("bash", ["--noprofile", "--norc", "-c", script.ReplaceLineEndings("\n")],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["COMPLETION_WORD"] = word,
                ["PATH"] = workspace.WorkspaceRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.Equal(["name\\ with\\ space", "name\\'quote", "name\\$\\(id\\)"], output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a Unix executable shim and permissions.")]
    [InlineData("echo 'a;b'; aspire --log-level \"Deb", new[] { "--log-level", "Deb" })]
    [InlineData("echo x && aspire config set 'a b' \"c d", new[] { "config", "set", "a b", "c d" })]
    [InlineData("echo x | aspire config get a\\:b", new[] { "config", "get", "a:b" })]
    [InlineData("aspire run --apphost='a b' --log-level ", new[] { "run", "--apphost=a b", "--log-level", "" })]
    [InlineData("aspire config get \"a\\qb", new[] { "config", "get", "a\\qb" })]
    [InlineData("aspire config set $'a b' x", new[] { "config", "set", "a b", "x" })]
    [InlineData("aspire config set $'a\\tb\\n' x", new[] { "config", "set", "a\tb\n", "x" })]
    [InlineData("aspire config set $'it\\'s \\\\ literal' x", new[] { "config", "set", "it's \\ literal", "x" })]
    [InlineData("aspire config set $'\\141\\x20\\142' x", new[] { "config", "set", "a b", "x" })]
    [InlineData("aspire config set $'\\0123\\cA' x", new[] { "config", "set", "\n3\u0001", "x" })]
    [InlineData("aspire config set pre$'a\\0discarded'post x", new[] { "config", "set", "preapost", "x" })]
    [InlineData("aspire config set $'\\x\\q\\?' x", new[] { "config", "set", "\\x\\q?", "x" })]
    [InlineData("aspire config get $'a\\tb", new[] { "config", "get", "a\tb" })]
    [InlineData("aspire config get $'a\\\\", new[] { "config", "get", "a\\" })]
    [InlineData("aspire config set $'$(touch completion-executed);`touch completion-executed`' x", new[] { "config", "set", "$(touch completion-executed);`touch completion-executed`", "x" })]
    public async Task Bash_QueriesOnlyDecodedCurrentCommandArguments(string line, string[] expectedArguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "completion.bash");
        var capturedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "arguments.bin");
        var binaryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("bash"));
        await File.WriteAllTextAsync(binaryPath, "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"$CAPTURED_ARGS\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var script = """
            source "$COMPLETION_SCRIPT"
            COMP_LINE="$COMPLETION_LINE"
            COMP_POINT=${#COMP_LINE}
            _aspire_complete
            """;
        var output = await RunShellAsync("bash", ["--noprofile", "--norc", "-c", script.ReplaceLineEndings("\n")],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["COMPLETION_LINE"] = line,
                ["CAPTURED_ARGS"] = capturedPath,
                ["PATH"] = workspace.WorkspaceRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.Equal(string.Empty, output);
        Assert.Equal(Encoding.UTF8.GetBytes(string.Join('\0', new[] { "[suggest:tokens]" }.Concat(expectedArguments)) + "\0"),
            await File.ReadAllBytesAsync(capturedPath));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "completion-executed")));
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a Unix executable shim and permissions.")]
    public async Task Bash_AnsiCEscapesUseTheShellLocaleAndVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "completion.bash");
        var capturedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "arguments.bin");
        var binaryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("bash"));
        await File.WriteAllTextAsync(binaryPath, "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"$CAPTURED_ARGS\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var script = """
            source "$COMPLETION_SCRIPT"
            COMP_LINE="aspire config get \$'\u263a\U0001f600\c?'"
            COMP_POINT=${#COMP_LINE}
            _aspire_complete
            printf '%s\0' '[suggest:tokens]' config get $'\u263a\U0001f600\c?'
            """;
        var expected = await RunShellAsync("bash", ["--noprofile", "--norc", "-c", script.ReplaceLineEndings("\n")],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["CAPTURED_ARGS"] = capturedPath,
                ["PATH"] = workspace.WorkspaceRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        // Older Bash versions retain Unicode escapes literally and decode \c? differently.
        // Compare with the shell's own decoding rather than imposing another version's rules.
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(capturedPath));
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
            complete --erase --command aspire
            complete --command aspire --arguments custom
            source "$COMPLETION_SCRIPT"
            source "$COMPLETION_SCRIPT"
            complete --command aspire | count
            """;
        var output = await RunShellAsync("fish", ["--no-config", "-c", script.ReplaceLineEndings("\n")],
            new Dictionary<string, string> { ["COMPLETION_SCRIPT"] = completionPath });

        Assert.Equal("2\n2\n", output.ReplaceLineEndings("\n"));
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
        var output = await RunShellAsync("zsh", ["-f", "-c", script.ReplaceLineEndings("\n")],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["HOME"] = workspace.WorkspaceRoot.FullName,
                ["ZDOTDIR"] = workspace.WorkspaceRoot.FullName
            });

        Assert.Equal("registered", output.Trim());
    }

    [Fact]
    [RequiresTools(["zsh"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Uses a Unix executable shim and permissions.")]
    public async Task Zsh_AutoloadCompletesFirstInvocationWithNativeCommandContext()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var completionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "_aspire");
        var binaryPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        var capturedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "arguments.bin");
        await File.WriteAllTextAsync(completionPath, CompletionScripts.Generate("zsh"));
        await File.WriteAllTextAsync(binaryPath, "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"$CAPTURED_ARGS\"\nprintf '%s\\n' Debug\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(binaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var script = """
            fpath=("$HOME" $fpath)
            autoload -Uz compinit
            compinit -D -i
            # Autoload registration must invoke the file body on the first request.
            [[ "${_comps[aspire]}" == _aspire ]] || exit 1
            BUFFER='echo ignored; aspire run --apphost "a b" --log-level "DeTAIL'
            words=(aspire run --apphost '"a b"' --log-level '"DeTAIL')
            CURRENT=6
            PREFIX=De
            compadd() { printf '%s\n' "$@"; }
            _aspire
            """;
        var output = await RunShellAsync("zsh", ["-f", "-c", script.ReplaceLineEndings("\n")],
            new Dictionary<string, string>
            {
                ["COMPLETION_SCRIPT"] = completionPath,
                ["HOME"] = workspace.WorkspaceRoot.FullName,
                ["ZDOTDIR"] = workspace.WorkspaceRoot.FullName,
                ["CAPTURED_ARGS"] = capturedPath,
                ["PATH"] = workspace.WorkspaceRoot.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });

        Assert.Equal("--\nDebug\n", output.ReplaceLineEndings("\n"));
        Assert.Equal(Encoding.UTF8.GetBytes("[suggest:tokens]\0run\0--apphost\0a b\0--log-level\0De\0"),
            await File.ReadAllBytesAsync(capturedPath));
    }

    private static async Task<string> RunShellAsync(string executable, string[] arguments, Dictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(environment["COMPLETION_SCRIPT"]),
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
