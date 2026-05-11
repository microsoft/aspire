// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Shared helpers for polyglot AppHost smoke tests that exercise the
/// <c>aspire run</c> + Redis-integration scenario.
/// </summary>
/// <remarks>
/// Replaces the polling logic previously implemented in
/// <c>.github/workflows/polyglot-validation/test-{python,go,rust}.sh</c>:
/// run the AppHost in the background, poll <c>docker ps</c> via the host Docker socket
/// for an emitted Redis container, then clean up the AppHost process.
/// </remarks>
internal static class PolyglotRedisAssertions
{
    /// <summary>
    /// Runs <c>aspire run</c> in the background inside the container shell, polls the host
    /// Docker socket for a Redis container, and fails the test if none materializes
    /// within <paramref name="aspireRunStartupTimeout"/>.
    /// </summary>
    /// <remarks>
    /// The poll script is written to the bind-mounted workspace from the host so the
    /// container shell can simply execute it. Writing the script as a single
    /// deterministic file keeps escaping out of the Hex1b TypeAsync path and makes the
    /// asciinema recording readable when a run fails.
    /// </remarks>
    internal static async Task RunAndAssertRedisContainerAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        TimeSpan? aspireRunStartupTimeout = null)
    {
        var startupTimeout = aspireRunStartupTimeout ?? TimeSpan.FromMinutes(3);
        var totalIterations = Math.Max(6, (int)Math.Ceiling(startupTimeout.TotalSeconds / 10));

        // The poll script:
        //  - launches `aspire run` in the background, redirecting output to aspire.log;
        //  - polls `docker ps` for an image name containing "redis" up to N iterations,
        //    sleeping 10s between attempts (matching the legacy shell scripts);
        //  - emits a final marker line ([REDIS-CONTAINER-FOUND] / [REDIS-CONTAINER-NOT-FOUND])
        //    plus diagnostic output on failure so the recording captures actionable detail;
        //  - kills the background AppHost on the way out so the container can exit cleanly.
        // The script bypasses Hex1b escaping by being written to the bind-mounted workspace
        // from the host.
        var scriptContents =
            "#!/bin/bash\n" +
            "set -u\n" +
            "( aspire run > /tmp/aspire-run.log 2>&1 ) &\n" +
            "ASPIRE_PID=$!\n" +
            "echo \"aspire run PID=$ASPIRE_PID\"\n" +
            "SUCCESS=0\n" +
            $"for i in $(seq 1 {totalIterations}); do\n" +
            $"  echo \"poll $i/{totalIterations} for redis container...\"\n" +
            "  if docker ps --format '{{.Image}} {{.Names}}' 2>/dev/null | grep -i redis; then\n" +
            "    SUCCESS=1\n" +
            "    break\n" +
            "  fi\n" +
            "  sleep 10\n" +
            "done\n" +
            "kill -9 \"$ASPIRE_PID\" 2>/dev/null || true\n" +
            "wait \"$ASPIRE_PID\" 2>/dev/null || true\n" +
            "if [ \"$SUCCESS\" = \"1\" ]; then\n" +
            "  echo '[REDIS-CONTAINER-FOUND]'\n" +
            "  exit 0\n" +
            "else\n" +
            "  echo '[REDIS-CONTAINER-NOT-FOUND]'\n" +
            "  echo '--- aspire run log (last 200 lines) ---'\n" +
            "  tail -n 200 /tmp/aspire-run.log || true\n" +
            "  echo '--- docker ps -a ---'\n" +
            "  docker ps -a || true\n" +
            "  exit 1\n" +
            "fi\n";

        var hostScriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "wait-for-redis.sh");
        File.WriteAllText(hostScriptPath, scriptContents);

        // bash sees the same workspace via the docker bind mount; PrepareDockerEnvironmentAsync
        // cd'd into the container-side workspace, so a relative path is sufficient.
        await auto.RunCommandFailFastAsync(
            "bash ./wait-for-redis.sh",
            counter,
            // Allow ~30s slack on top of the poll budget so the bash exit can be observed
            // (script kill + final marker can take a few seconds even after success).
            startupTimeout + TimeSpan.FromSeconds(30));
    }
}
