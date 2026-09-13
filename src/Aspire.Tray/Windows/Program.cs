// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.Versioning;

namespace Aspire.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
        // Suppress modal dialogs even when smoke arguments are malformed, so automation cannot hang.
        var smoke = args.Contains("--smoke-seconds", StringComparer.Ordinal);
        try
        {
            var options = TrayOptions.Parse(args);
            if (options.BundleRoot is not null)
            {
                throw new InvalidOperationException("The Windows scaffold does not support bundled tray lifecycle commands.");
            }
            using var instance = SingleInstance.Acquire();
            if (instance is null)
            {
                ReportError("The Aspire Tray is already running for this user. This frontend does not activate the existing instance.", smoke);
                return 2;
            }

            var controller = new TrayController(new CliAppHostClient(options.CliPath));
            try
            {
                var tray = new TrayApplication(controller, options.SmokeSeconds);
                try
                {
                    controller.Start();
                    tray.Run();
                }
                finally
                {
                    tray.Dispose();
                }

                return tray.ExitCode;
            }
            finally
            {
                // Keep the native message loop and the named mutex on this thread. Only discovery
                // runs asynchronously; releasing a mutex from an async continuation is not valid.
                controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                NativeCallException => ex.Message,
                ArgumentException => TrayOptions.Usage,
                Win32Exception native => $"Windows startup failed (native error {native.NativeErrorCode}).",
                _ => $"The tray failed ({ex.GetType().Name})."
            };
            ReportError(message, smoke);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void Log(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            // Closing a redirected console must not throw through a native window callback.
        }
        NativeMethods.OutputDebugString(message + Environment.NewLine);
    }

    [SupportedOSPlatform("windows")]
    private static void ReportError(string message, bool smoke)
    {
        Log(message);
        if (!smoke)
        {
            NativeMethods.MessageBox(0, message, "Aspire Tray", NativeMethods.MbIconError);
        }
    }
}
