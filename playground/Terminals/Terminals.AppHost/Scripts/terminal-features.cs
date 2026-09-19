// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Run directly with `dotnet run --file terminal-features.cs`, or open the terminal-features
// resource in the Terminals playground. No terminal library is needed: these are ordinary OSC
// escape sequences terminated by BEL (\u0007).
// Title/directory: https://iterm2.com/documentation-escape-codes.html
// Progress: https://learn.microsoft.com/windows/terminal/tutorials/progress-bar-sequences
// Command marks: https://iterm2.com/documentation-shell-integration.html

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

// Report illustrative paths (including a space to exercise URI decoding) without touching the filesystem.
var sourceDirectory = new Uri(Path.Combine(Environment.CurrentDirectory, "demo source") + Path.DirectorySeparatorChar).AbsoluteUri;
var outputDirectory = new Uri(Path.Combine(Environment.CurrentDirectory, "demo output") + Path.DirectorySeparatorChar).AbsoluteUri;

Console.WriteLine("Terminal feature driver. Watch the title bar and scrollbar. Press Ctrl+C to stop.");

try
{
    for (var cycle = 1; ; cycle++)
    {
        Console.WriteLine($"\u001b]2;Terminal demo - cycle {cycle}\u0007");
        Console.WriteLine($"\u001b]7;{sourceDirectory}\u0007");
        Console.WriteLine("\u001b]133;A\u0007$ \u001b]133;B\u0007terminal-feature-demo");
        Console.WriteLine("\u001b]133;C\u0007");

        Console.WriteLine("\u001b]9;4;3\u0007Indeterminate progress: preparing work...");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine($"\u001b]2;Building - cycle {cycle}\u0007");
        for (var percentage = 0; percentage <= 100; percentage += 10)
        {
            Console.WriteLine($"\u001b]9;4;1;{percentage}\u0007Determinate progress: {percentage}%");
            Console.WriteLine($"  Cycle {cycle}: processing item {percentage + 1}");
            Console.WriteLine("  More output for scrollback, selection and command-marker navigation.");
            await Task.Delay(400, stopping.Token);
        }

        Console.WriteLine($"\u001b]7;{outputDirectory}\u0007");
        Console.WriteLine("\u001b]2;Warning - retrying\u0007");
        Console.WriteLine("\u001b]9;4;4;60\u0007Warning progress at 60% (simulated).");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine("\u001b]2;Error - demonstration only\u0007");
        Console.WriteLine("\u001b]9;4;2;75\u0007Error progress at 75% (simulated).");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine("\u001b]2;Complete\u0007");
        Console.WriteLine("\u001b]9;4;1;100\u0007Complete at 100%.");
        // Alternate completion status so retained command markers include successful and failed commands.
        Console.WriteLine($"\u001b]133;D;{cycle % 2}\u0007");
        await Task.Delay(2000, stopping.Token);

        Console.WriteLine("\u001b]9;4;0\u0007Progress cleared.");
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
    Console.WriteLine("\u001b]9;4;0\u0007\u001b]2;\u0007Driver stopped.");
}
