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
        // Bash/Zsh send decoded argument tokens, not shell syntax. The other hooks send only
        // text before the cursor so the CLI can use its UTF-16 length rather than shell offsets.
        var script = shell switch
        {
            "bash" => """
                # Bash completion for Aspire. Source this file from ~/.bashrc.
                _aspire_complete()
                {
                    local line suggestion word='' quote='' character escaped=false started=false i text
                    local -a arguments=()
                    local LC_ALL=C
                    COMPREPLY=()
                    line="${COMP_LINE:0:COMP_POINT}"

                    # Decode literal shell words without eval or expansion. For example:
                    #   echo "a;b"; aspire run --apphost 'path with spaces' --log-level "De
                    # Separators inside quotes are data. Older Bash versions include preceding
                    # commands in COMP_LINE; COMP_WORDS also splits option values at '=' and ':'.
                    for ((i = 0; i < ${#line}; i++)); do
                        character="${line:i:1}"
                        if [[ "$escaped" == true ]]; then
                            if [[ "$quote" == '"' && "$character" != '$' && "$character" != '`' &&
                                  "$character" != '"' && "$character" != '\' && "$character" != $'\n' ]]; then
                                word+='\'
                            fi
                            [[ "$character" == $'\n' ]] || word+="$character"
                            escaped=false
                        elif [[ "$character" == '\' && "$quote" != "'" ]]; then
                            escaped=true
                            started=true
                        elif [[ -n "$quote" ]]; then
                            if [[ "$character" == "$quote" ]]; then
                                quote=''
                            else
                                word+="$character"
                            fi
                        else
                            case "$character" in
                                "'"|'"') quote="$character"; started=true ;;
                                ' '|$'\t'|$'\r')
                                    if [[ "$started" == true ]]; then arguments+=("$word"); fi
                                    word=''
                                    started=false
                                    ;;
                                ';'|'|'|'&'|'('|')'|$'\n')
                                    arguments=()
                                    word=''
                                    started=false
                                    ;;
                                *) word+="$character"; started=true ;;
                            esac
                        fi
                    done
                    [[ "$escaped" == false ]] || word+='\'
                    arguments+=("$word")

                    while IFS= read -r suggestion; do
                        [[ -n "$suggestion" ]] || continue
                        # Without -o filenames, Readline inserts custom candidates verbatim.
                        # Inside an open quote, escape for that quote; Readline closes it.
                        if [[ -z "$quote" ]]; then
                            printf -v text '%q' "$suggestion"
                        else
                            text=''
                            for ((i = 0; i < ${#suggestion}; i++)); do
                                character="${suggestion:i:1}"
                                if [[ "$quote" == "'" && "$character" == "'" ]]; then
                                    text+="'\''"
                                else
                                    if [[ "$quote" == '"' && ( "$character" == '\' || "$character" == '"' ||
                                          "$character" == '$' || "$character" == '`' ) ]]; then
                                        text+='\'
                                    fi
                                    text+="$character"
                                fi
                            done
                        fi
                        COMPREPLY+=("$text")
                    done < <(command aspire '[suggest:tokens]' "${arguments[@]:1}" 2>/dev/null)
                }
                complete -o default -F _aspire_complete aspire
                """,
            "zsh" => """
                #compdef aspire
                _aspire()
                {
                    local suggestions
                    local -a arguments values
                    # Zsh supplies only this command's words, even after a pipeline or ';'.
                    # Q removes quoting without evaluating substitutions; PREFIX excludes the
                    # suffix after the cursor and the opening quote of an unfinished argument.
                    arguments=("${(@Q)words[2,CURRENT-1]}")
                    suggestions=$(command aspire '[suggest:tokens]' "${arguments[@]}" "$PREFIX" 2>/dev/null)
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
