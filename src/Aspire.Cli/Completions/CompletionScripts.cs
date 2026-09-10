// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Completions;

internal static class CompletionScripts
{
    internal static string[] SupportedShells { get; } = ["bash", "fish", "pwsh", "zsh"];

    internal static string? DetectShell(IEnvironment environment)
    {
        var shell = Path.GetFileNameWithoutExtension(environment.GetEnvironmentVariable("SHELL"));
        if (SupportedShells.Contains(shell, StringComparer.Ordinal))
        {
            return shell;
        }

        return environment.IsWindows() ? "pwsh" : null;
    }

    internal static string Generate(string shell)
    {
        // Resolve aspire through PATH on every request. In particular, npm and bundle installs
        // run a versioned native binary whose ProcessPath must not be pinned in a shell profile.
        // Send only the text before the cursor. [suggest] uses its UTF-16 length in the CLI,
        // avoiding the different byte/code-point cursor units used by the supported shells.
        var script = shell switch
        {
            "bash" => """
                # Bash completion for Aspire. Source this file from ~/.bashrc.
                _aspire_complete()
                {
                    local line suggestion word
                    local LC_ALL=C
                    COMPREPLY=()
                    line="${COMP_LINE:0:COMP_POINT}"
                    word="${COMP_WORDS[COMP_CWORD]}"
                    while IFS= read -r suggestion; do
                        [[ -n "$suggestion" ]] || continue
                        [[ "$suggestion" == "$word"* ]] || continue
                        # Without -o filenames, Readline inserts custom candidates verbatim.
                        # Quote here to preserve argument boundaries and prevent shell expansion.
                        printf -v suggestion '%q' "$suggestion"
                        COMPREPLY+=("$suggestion")
                    done < <(command aspire '[suggest]' "$line" 2>/dev/null)
                }
                complete -o default -F _aspire_complete aspire
                """,
            "zsh" => """
                #compdef aspire
                _aspire()
                {
                    local suggestions
                    local -a values
                    suggestions=$(command aspire '[suggest]' "${BUFFER:0:$CURSOR}" 2>/dev/null)
                    values=("${(@f)suggestions}")
                    if [[ -n "$suggestions" ]]; then
                        compadd -- "${values[@]}"
                    else
                        _default
                    fi
                }
                # A fresh Zsh profile may not have initialized the completion system yet.
                # -i audits and excludes insecure directories instead of prompting during profile
                # loading. Unlike -u, it does not trust insecure completion directories.
                # https://zsh.sourceforge.io/Doc/Release/Completion-System.html#Initialization
                if (( ! $+functions[compdef] )); then
                    autoload -Uz compinit
                    compinit -i || return
                fi
                compdef _aspire aspire
                # An autoloaded #compdef file must also complete its first invocation.
                if [[ "$funcstack[1]" == "_aspire" ]]; then
                    _aspire "$@"
                fi
                """,
            "fish" => """
                # Fish completion for Aspire. Save as ~/.config/fish/completions/aspire.fish.
                # Reload the implementation without accumulating completion registrations.
                if not functions --query __aspire_complete
                    complete --command aspire --arguments '(__aspire_complete)'
                end
                function __aspire_complete
                    set -l line (commandline --current-process --cut-at-cursor)
                    command aspire '[suggest]' "$line" 2>/dev/null
                end
                """,
            "pwsh" => """
                # PowerShell 7+ completion for Aspire. Dot-source this file from $PROFILE.
                & {
                    $completer = {
                        param($wordToComplete, $commandAst, $cursorPosition)

                        # The cursor is relative to the entire input, but an AST can start after a
                        # pipeline/statement or use a quoted executable with the call operator (&).
                        $commandEnd = $commandAst.CommandElements[0].Extent.EndOffset
                        $argumentStart = $commandEnd - $commandAst.Extent.StartOffset
                        $argumentLength = [Math]::Max(0, $cursorPosition - $commandEnd)
                        # AST extents exclude trailing whitespace after the last token.
                        $arguments = $commandAst.ToString().Substring($argumentStart)
                        $line = 'aspire' + $arguments.PadRight($argumentLength).Substring(0, $argumentLength)
                        $prefix = $wordToComplete.TrimStart([char[]]@("'", '"'))
                        & aspire '[suggest]' $line 2>$null | ForEach-Object {
                            if (-not [string]::IsNullOrWhiteSpace($_) -and $_.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                                $text = $_
                                if ($text -match '[\s''"`$;&|<>(){}\[\]*?@#]') {
                                    $text = "'" + $text.Replace("'", "''") + "'"
                                }
                                [System.Management.Automation.CompletionResult]::new(
                                    $text, $_, [System.Management.Automation.CompletionResultType]::ParameterValue, $_)
                            }
                        }
                    }
                    Register-ArgumentCompleter -Native -CommandName aspire, aspire.exe, aspire.cmd -ScriptBlock $completer
                    # npm uses aspire.ps1 in PowerShell. Script-level registration (without a
                    # ParameterName) uses the same three arguments but a separate completer table.
                    Register-ArgumentCompleter -CommandName aspire, aspire.ps1 -ScriptBlock $completer
                }
                """,
            _ => throw new ArgumentException($"Unsupported shell: {shell}", nameof(shell))
        };

        return script.ReplaceLineEndings("\n") + "\n";
    }
}
