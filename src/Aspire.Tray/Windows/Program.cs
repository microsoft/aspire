// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.Versioning;
using Aspire.Shared;

namespace Aspire.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(TrayOptions.Usage);
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This tray frontend requires Windows.");
            return 1;
        }

        return RunWindows(args);
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        var smoke = args.Contains("--smoke-seconds", StringComparer.Ordinal);
        var helper = args.FirstOrDefault() is "start" or "stop";
        WindowsTrayLog? log = null;
        try
        {
            if (args is ["stop"])
            {
                WindowsTrayLauncher.StopAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["start", .. var startArgs])
            {
                WindowsTrayLauncher.StartAsync(TrayOptions.Parse(startArgs)).GetAwaiter().GetResult();
                return 0;
            }
            if (!smoke && !helper)
            {
                log = new WindowsTrayLog();
            }
            var options = TrayOptions.Parse(args);
            if (options.SmokeSeconds is int seconds)
            {
                return NativeSmokeHarness.Run(options.CliPath, seconds);
            }

            // Run, acquire, and release the named mutex on the initial STA thread.
            using var instance = WindowsSingleInstance.TryAcquire();
            if (instance is null)
            {
                TrayActivation.ShowExistingAsync(WindowsSingleInstance.ActivationPipeName, CancellationToken.None).GetAwaiter().GetResult();
                Log("Restored the running Aspire tray icon.");
                return 0;
            }

            using var lease = options.BundleRoot is null
                ? null : BundleVersionLease.Acquire(options.BundleRoot, "tray", "tray");
            var controller = new TrayController(new CliAppHostClient(options.CliPath),
                new FileTraySavedStateStore(Path.Combine(WindowsSingleInstance.DirectoryPath, "apphosts.json")));
            TrayApplication? tray = null;
            TrayActivation? activation = null;
            try
            {
                tray = new TrayApplication(controller, smokeSeconds: null);
                activation = new TrayActivation(WindowsSingleInstance.ActivationPipeName,
                    tray.RestoreIconAsync, tray.WaitUntilReadyAsync, tray.RequestQuit);
                Log("Starting the Windows Aspire tray.");
                controller.Start();
                tray.Run();
            }
            finally
            {
                // Join IPC and controller producers while their native dispatcher is still
                // alive. Keep the bundle lease, diagnostic streams, and mutex through cleanup.
                try
                {
                    activation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    try
                    {
                        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    finally
                    {
                        tray?.Dispose();
                    }
                }
            }
            Log($"The Windows Aspire tray stopped (exit {tray.ExitCode}).");
            return tray.ExitCode;
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                NativeCallException => ex.Message,
                ArgumentException or TimeoutException or InvalidOperationException or FileNotFoundException => ex.Message,
                Win32Exception native => $"Windows startup failed (native error {native.NativeErrorCode}). {native.Message}",
                _ => $"The tray failed ({ex.GetType().Name})."
            };
            // Helpers and malformed smoke invocations must never block automation on a modal dialog.
            Log(message);
            return 1;
        }
        finally
        {
            log?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void Log(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException ex)
        {
            // Never let a closed console or failing disk throw through a native callback.
            // Preserve the failure in the debugger channel instead of silently swallowing it.
            NativeMethods.OutputDebugString($"Tray diagnostic write failed ({ex.GetType().Name}).{Environment.NewLine}");
        }
        NativeMethods.OutputDebugString(message + Environment.NewLine);
    }
}
