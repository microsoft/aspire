// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Shared;

namespace Aspire.Tray;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine(TrayOptions.Usage);
            return 0;
        }
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("This frontend requires macOS.");
            return 1;
        }

        // AppKit must stay on the initial OS thread, including asynchronous-worker shutdown.
        try
        {
            if (args is ["stop"])
            {
                MacTrayLauncher.StopAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["start", .. var startArgs])
            {
                MacTrayLauncher.StartAsync(TrayOptions.Parse(startArgs)).GetAwaiter().GetResult();
                return 0;
            }
            // An isolated preview bundle can opt into the fake backend through its
            // LSEnvironment, so Launch Services can open it without command-line arguments.
            if (args.Length == 0 && Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_INTERACTIVE") == "1")
            {
                args = ["--cli", Environment.GetEnvironmentVariable("ASPIRE_TRAY_SMOKE_CLI") ?? "",
                    "--smoke-seconds", "120"];
            }
            var options = TrayOptions.Parse(args);
            if (options.SmokeSeconds is int seconds)
            {
                return NativeSmokeHarness.Run(options.CliPath, seconds, options.InteractiveSmoke);
            }

            using var singleton = SingleInstance.TryAcquire();
            if (singleton is null)
            {
                TrayActivation.ShowExistingAsync(SingleInstance.ActivationPipeName, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Restored the running Aspire tray icon.");
                return 0;
            }

            using var lease = options.BundleRoot is null
                ? null : BundleVersionLease.Acquire(options.BundleRoot, "tray", "tray");
            var controller = new TrayController(new CliAppHostClient(options.CliPath),
                new FileTraySavedStateStore(Path.Combine(SingleInstance.DirectoryPath, "apphosts.json")),
                TrayConfiguration.LoadRecentAppHostLimit(TrayConfiguration.GetSettingsPath(options.CliPath)));
            var startupSettings = new MacTrayStartupSettings(options, !RuntimeFeature.IsDynamicCodeSupported,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"));
            using var application = new MacTrayApplication(controller, "AspireTray", startupSettings);
            TrayActivation? activation = null;
            try
            {
                activation = new(SingleInstance.ActivationPipeName, application.RestoreIconAsync,
                    application.WaitUntilReadyAsync, application.RequestQuit);
                controller.Start();
                return application.Run();
            }
            finally
            {
                // Join producers before releasing the dispatcher and native callback targets.
                try
                {
                    activation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tray startup/shutdown failed ({ex.GetType().Name}).");
            return 1;
        }
    }
}
