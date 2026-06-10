// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// WithTerminal() is currently experimental (ASPIRETERMINAL001). Suppressed here
// because this playground project intentionally exercises the experimental API.
#pragma warning disable ASPIRETERMINAL001

var builder = DistributedApplication.CreateBuilder(args);

// A multi-replica project that calls `WithTerminal()` so each replica gets its
// own pseudo-terminal and the dashboard can attach to any of them via
// `/api/terminal?resource=repl&replica=<i>`. The replica index is forwarded as
// an environment variable so the REPL can stamp it on its banner.
builder.AddProject<Projects.Terminals_Repl>("repl")
    .WithReplicas(2)
    .WithEnvironment("ASPIRE_RESOURCE_NAME", "repl")
    .WithTerminal(options =>
    {
        options.Columns = 120;
        options.Rows = 32;
        options.ShowTerminalHost = true;
    });

if (OperatingSystem.IsWindows())
{
    // Single-replica executable wrapping cmd.exe to demonstrate that
    // WithTerminal() also works for arbitrary executables, not just projects.
    builder.AddExecutable("shell", "cmd.exe", ".")
        .WithTerminal(options => options.ShowTerminalHost = true);

    // A/B control resource: same cmd.exe but WITHOUT WithTerminal(). Used to
    // bisect whether the "Stop kills the dashboard" symptom is specific to
    // PTY-attached resources or applies to any DCP-managed Windows process.
    // Without a PTY, cmd.exe with redirected stdin would exit immediately, so
    // we wrap it in `/c ping -t 127.0.0.1` (continuous ping to localhost
    // until killed) so the resource stays in the Running state long enough
    // for "Stop" to be clicked from the dashboard.
    builder.AddExecutable("shell2", "cmd.exe", ".", "/c", "ping -t 127.0.0.1 > NUL");

    // Container resource exercising the container-side WithTerminal() path.
    // Launches a long-running Node.js LTS image with an interactive bash so
    // the terminal attaches to a shell where `npx`, `node`, and friends are
    // available. We override the image CMD (not the entrypoint!) so the
    // image's docker-entrypoint.sh keeps running and exec's bash for us;
    // overriding the entrypoint instead would leave the image's CMD
    // ("node") appended after bash and immediately exit.
    builder.AddContainer("nodebox", "node", "lts")
        .WithArgs("bash", "-l")
        .WithTerminal(options => options.ShowTerminalHost = true);
}
else
{
    // Unix counterpart of `shell`: an interactive login bash so the terminal
    // exercises the executable-with-PTY path on Linux/macOS. `-i` keeps bash
    // in interactive mode (prompt, line editing, job control) and `-l` makes
    // it source the user's login profile so `PATH`, aliases etc. behave the
    // same as a normal terminal session.
    builder.AddExecutable("shell", "/bin/bash", ".", "-i", "-l")
        .WithTerminal(options => options.ShowTerminalHost = true);
}

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

