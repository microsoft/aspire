// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Run directly with `dotnet run --file terminal-features.cs`, or open the terminal-features
// resource in the Terminals playground. No terminal library is needed: these are ordinary OSC
// escape sequences terminated by BEL (\u0007).
// Title/directory: https://iterm2.com/documentation-escape-codes.html
// Progress: https://learn.microsoft.com/windows/terminal/tutorials/progress-bar-sequences
// Command marks: https://iterm2.com/documentation-shell-integration.html
// Program status: https://github.com/mitchdenny/hex1b/blob/e84f086/docs/program-status.md

using System.Text;

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

// Report illustrative paths (including a space to exercise URI decoding) without touching the filesystem.
var sourceDirectory = new Uri(Path.Combine(Environment.CurrentDirectory, "demo source") + Path.DirectorySeparatorChar).AbsoluteUri;
var outputDirectory = new Uri(Path.Combine(Environment.CurrentDirectory, "demo output") + Path.DirectorySeparatorChar).AbsoluteUri;

Console.WriteLine("Terminal feature driver. Watch the title bar, scrollbar and program status panel. Press Ctrl+C to stop.");

try
{
    for (var cycle = 1; ; cycle++)
    {
        Console.WriteLine($"\u001b]2;Terminal demo - cycle {cycle}\u0007");
        Console.WriteLine($"\u001b]7;{sourceDirectory}\u0007");
        Console.WriteLine("\u001b]133;A\u0007$ \u001b]133;B\u0007terminal-feature-demo");
        Console.WriteLine("\u001b]133;C\u0007");
        ReportStatus("working", "", "Terminal feature demo", "Preparing work", "app=terminal-demo:progress=0");
        ReportStatus("idle", "deploy", "Deployment", "Waiting for the build", "");
        ReportLargeStatusTree();

        Console.WriteLine("\u001b]9;4;3\u0007Indeterminate progress: preparing work...");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine($"\u001b]2;Building - cycle {cycle}\u0007");
        for (var percentage = 0; percentage <= 100; percentage += 10)
        {
            Console.WriteLine($"\u001b]9;4;1;{percentage}\u0007Determinate progress: {percentage}%");
            Console.WriteLine($"  Cycle {cycle}: processing item {percentage + 1}");
            Console.WriteLine("  More output for scrollback, selection and command-marker navigation.");
            ReportStatus("working", "build/compile", "Compiler", "Compiling sources", $"progress={percentage}");
            await Task.Delay(400, stopping.Token);
        }

        Console.WriteLine($"\u001b]7;{outputDirectory}\u0007");
        Console.WriteLine("\u001b]2;Warning - retrying\u0007");
        Console.WriteLine("\u001b]9;4;4;60\u0007Warning progress at 60% (simulated).");
        ReportStatus("done", "build/compile", "Compiler", "Compilation complete", "");
        ReportStatus("blocked", "build/tests", "Tests", "Approve running tests", "kind=permission:progress=60");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine("\u001b]2;Error - demonstration only\u0007");
        Console.WriteLine("\u001b]9;4;2;75\u0007Error progress at 75% (simulated).");
        ReportStatus("error", "build/tests", "Tests", "An assertion failed (demonstration only)", "");
        ReportStatus("blocked", "deploy", "Deployment", "Sign in before deploying", "kind=auth");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine("\u001b]2;Complete\u0007");
        Console.WriteLine("\u001b]9;4;1;100\u0007Complete at 100%.");
        Console.Write("\u001b]7501;state=clear:id=build\u0007");
        ReportStatus("done", "deploy", "Deployment", "Deployment complete", "");
        ReportStatus("done", "", "Terminal feature demo", "Ready to inspect", "app=terminal-demo");
        // Alternate completion status so retained command markers include successful and failed commands.
        Console.WriteLine($"\u001b]133;D;{cycle % 2}\u0007");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine("\u001b]9;4;0\u0007Progress cleared.");
        Console.Write("\u001b]7501;state=clear\u0007");
        Console.WriteLine("\u001b]2;\u0007Title cleared: the title bar should show its fallback name.");
        await Task.Delay(2000, stopping.Token);
    }
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    // Ctrl+C stops the continuous demo without reporting a failed workload.
}
finally
{
    Console.WriteLine("\u001b]7501;state=clear\u0007\u001b]9;4;0\u0007\u001b]2;\u0007Driver stopped.");
}

static void ReportStatus(string state, string id, string title, string message, string extra)
{
    // OSC 7501 is "state=working:id=build/compile:title=<base64>:msg=<base64>:progress=10".
    // Base64 UTF-8 protects text containing the ':' field delimiter; omit id for the root.
    var fields = $"state={state}:title={Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}:msg={Convert.ToBase64String(Encoding.UTF8.GetBytes(message))}";
    if (id.Length > 0)
    {
        fields += $":id={id}";
    }
    if (extra.Length > 0)
    {
        fields += $":{extra}";
    }
    Console.Write($"\u001b]7501;{fields}\u0007");
}

static void ReportLargeStatusTree()
{
    // Keep the inventory below the 256-record limit while exercising both scroll axes.
    // Eight path segments also exercise the protocol's maximum nesting depth.
    for (var region = 1; region <= 8; region++)
    {
        for (var task = 1; task <= 12; task++)
        {
            var state = (task % 5) switch
            {
                0 => "done",
                1 => "working",
                2 => "idle",
                3 => "blocked",
                _ => "error"
            };
            ReportStatus(state, $"regions/region-{region:D2}/task-{task:D2}",
                $"Region {region}: task {task} - validate configuration, provision dependencies, and verify service readiness",
                "Long task labels and nested groups exercise horizontal and vertical scrolling.",
                state switch
                {
                    "working" when task % 2 == 0 => $"progress={task * 7}",
                    "blocked" => "kind=question",
                    _ => ""
                });
        }
    }
    ReportStatus("working", "regions/region-08/services/backend/database/migrations/validate/schema",
        "Validate the complete backend database schema after applying the deployment's migration history",
        "This task is nested eight levels deep.", "");
}
