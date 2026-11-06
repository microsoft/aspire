// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDependencies.Tools;

internal static class Program
{
    internal static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };
    internal static CancellationTokenSource Cancellation { get; } = new();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            await RunAsync(args);
            return 0;
        }
        catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled. Inspect the cleanup result above; use down to remove the owned fixture.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The disposable SSH/nested-Docker fixture requires Linux.");
        }
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cancellation.Cancel();
        };

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Aspire.slnx")))
        {
            directory = directory.Parent;
        }
        var repo = directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
        var fixture = new Fixture(Path.Combine(repo, "playground", "RemoteDependencies"));
        switch (args)
        {
            case ["up"]:
                await fixture.UpAsync(nonRootPublicKey: false);
                break;
            case ["up", "--uid1000"]:
                await fixture.UpAsync(nonRootPublicKey: true);
                break;
            case ["down"]:
                await fixture.DownAsync();
                break;
            case ["verify"]:
                await new Verification(repo, fixture).RunAsync(injectShutdownFailure: false);
                break;
            case ["verify", "--inject-shutdown-failure"]:
                await new Verification(repo, fixture).RunAsync(injectShutdownFailure: true);
                break;
            default:
                throw new ArgumentException("Usage: RemoteDependencies.Tools up [--uid1000] | down | verify [--inject-shutdown-failure]");
        }
    }
}

internal static class Commands
{
    internal static ProcessStartInfo StartInfo(string command, params string[] args)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args)
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    internal static async Task<(int Code, string Output, string Error)> CaptureAsync(string command, params string[] args)
    {
        using var process = Process.Start(StartInfo(command, args))
            ?? throw new InvalidOperationException($"Could not start {command}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"{command} timed out.");
        }
        return (process.ExitCode, (await output).Trim(), (await error).Trim());
    }

    internal static async Task<string> RunAsync(string command, params string[] args)
    {
        var result = await CaptureAsync(command, args);
        if (result.Code != 0)
        {
            throw new InvalidOperationException($"{command} exited {result.Code}:\n{result.Output}\n{result.Error}");
        }
        return result.Output;
    }

    internal static async Task WaitAsync(Func<Task<bool>> predicate, string description, int seconds = 45,
        bool allowCancellation = true)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (allowCancellation)
            {
                Program.Cancellation.Token.ThrowIfCancellationRequested();
            }
            if (await predicate())
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out: {description}");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
}
