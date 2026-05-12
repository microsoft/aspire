// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Shared helpers for codegen fixture-enumeration tests. Replaces the per-fixture loops
/// that previously lived in <c>.github/workflows/polyglot-validation/test-{python,go,java,typescript}-playground.sh</c>:
/// for every <c>tests/PolyglotAppHosts/&lt;Integration&gt;/&lt;Language&gt;/</c> fixture,
/// run <c>aspire restore --apphost</c> and then compile the regenerated SDK + apphost
/// with the language toolchain.
/// </summary>
/// <remarks>
/// The fixtures live in the source tree at <c>tests/PolyglotAppHosts/</c>. They are
/// mounted read-only into the test container at <see cref="ContainerFixturesPath"/> via
/// the <c>additionalVolumes</c> parameter on <c>CreateDockerTestTerminal</c>; the loop
/// inside the container copies each fixture to a writable temp directory before running
/// <c>aspire restore</c> so the source tree is never mutated. The CLI install and the
/// local NuGet hive are shared across fixtures within a single container, exactly as
/// the bash playground scripts shared them.
/// </remarks>
internal static class PolyglotFixtureValidation
{
    /// <summary>
    /// Mount point inside the container for the read-only <c>tests/PolyglotAppHosts</c> directory.
    /// </summary>
    internal const string ContainerFixturesPath = "/mnt/polyglot-fixtures";

    /// <summary>
    /// Builds the <c>additionalVolumes</c> entry that exposes the per-integration fixtures
    /// inside the test container read-only.
    /// </summary>
    internal static string GetFixtureVolumeMount(string repoRoot)
    {
        var fixturesPath = Path.Combine(repoRoot, "tests", "PolyglotAppHosts");
        // Read-only mount keeps the source tree safe even if a fixture's `aspire restore`
        // would otherwise write `.modules/` back into it; the loop always operates on a
        // writable copy under /tmp.
        return $"{fixturesPath}:{ContainerFixturesPath}:ro";
    }

    /// <summary>
    /// Writes a per-language fixture-validation script to the bind-mounted workspace and
    /// executes it inside the container. The script enumerates each
    /// <c>{ContainerFixturesPath}/&lt;Integration&gt;/&lt;<paramref name="languageSubdir"/>&gt;</c>
    /// directory, copies it to a writable temp dir, runs <c>aspire restore</c> against
    /// <paramref name="appHostFileName"/>, then runs <paramref name="compileCommand"/>.
    /// The script aggregates pass/fail per fixture and exits non-zero if any fail or if
    /// no fixtures are found.
    /// </summary>
    internal static async Task RunFixtureLoopAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        string languageSubdir,
        string appHostFileName,
        string compileCommand,
        TimeSpan? timeout = null,
        string? prepareCommand = null)
    {
        var effective = timeout ?? TimeSpan.FromMinutes(25);

        // The script body. Bash variable expansions are written as ${...} and need to be
        // escaped as $$ in an interpolated C# string -> we use a raw string instead and
        // substitute the four interpolated values with string.Replace below to avoid
        // double-brace headaches around bash arrays/heredocs.
        var script = """
            #!/bin/bash
            set -u
            fixtures_root=__FIXTURES_ROOT__
            language_subdir=__LANGUAGE_SUBDIR__
            apphost_file=__APPHOST_FILE__

            if [ ! -d "$fixtures_root" ]; then
              echo "[FIXTURES-MOUNT-MISSING] $fixtures_root"
              exit 1
            fi

            __PREPARE_COMMAND__

            log=/tmp/fixture-run.log
            : > "$log"

            passed=()
            failed=()
            total=0

            shopt -s nullglob
            for fixture in "$fixtures_root"/*/"$language_subdir"; do
              total=$((total+1))
              integration=$(basename "$(dirname "$fixture")")

              echo
              echo "==== [$total] $integration ===="

              work=$(mktemp -d)
              cp -R "$fixture/." "$work/"
              pushd "$work" > /dev/null

              if ! aspire restore --non-interactive --apphost "$apphost_file" > /tmp/restore.log 2>&1; then
                echo "  RESTORE FAILED"
                {
                  echo "--- $integration: aspire restore failed ---"
                  tail -n 80 /tmp/restore.log
                } >> "$log"
                failed+=("$integration (restore)")
                popd > /dev/null
                rm -rf "$work"
                continue
              fi

              if ! ( __COMPILE_COMMAND__ ) > /tmp/compile.log 2>&1; then
                echo "  COMPILE FAILED"
                {
                  echo "--- $integration: compile failed ---"
                  tail -n 80 /tmp/compile.log
                } >> "$log"
                failed+=("$integration (compile)")
                popd > /dev/null
                rm -rf "$work"
                continue
              fi

              echo "  PASS"
              passed+=("$integration")
              popd > /dev/null
              rm -rf "$work"
            done

            echo
            echo "=== $language_subdir fixture results: ${#passed[@]}/$total passed, ${#failed[@]} failed ==="

            if [ "$total" -eq 0 ]; then
              echo "[NO-FIXTURES-FOUND] expected fixtures under $fixtures_root/*/$language_subdir"
              exit 1
            fi

            if [ ${#failed[@]} -gt 0 ]; then
              echo "--- Failures ---"
              for f in "${failed[@]}"; do echo "  - $f"; done
              echo "--- Failure log (last 400 lines) ---"
              tail -n 400 "$log"
              exit 1
            fi

            echo "[ALL-FIXTURES-PASSED]"
            """;

        // Substitute the four C# inputs without interfering with bash brace expansions
        // inside the script body.
        script = script
            .Replace("__FIXTURES_ROOT__", BashSingleQuote(ContainerFixturesPath))
            .Replace("__LANGUAGE_SUBDIR__", BashSingleQuote(languageSubdir))
            .Replace("__APPHOST_FILE__", BashSingleQuote(appHostFileName))
            .Replace("__PREPARE_COMMAND__", prepareCommand ?? string.Empty)
            .Replace("__COMPILE_COMMAND__", compileCommand);

        var scriptFileName = $"validate-{languageSubdir.ToLowerInvariant()}-fixtures.sh";
        var hostScriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, scriptFileName);
        File.WriteAllText(hostScriptPath, script);

        await auto.RunCommandFailFastAsync(
            $"bash ./{scriptFileName}",
            counter,
            effective);
    }

    private static string BashSingleQuote(string value)
    {
        // Single-quote a value for safe bash literal substitution. Embedded single quotes
        // are escaped using the standard '\'' close-quote-escape-open-quote idiom.
        return "'" + value.Replace("'", "'\\''") + "'";
    }
}
