// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This frontend requires Linux.");
            return 1;
        }
        try
        {
            if (args is ["stop"])
            {
                LinuxTrayRuntime.StopAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["start", .. var start])
            {
                LinuxTrayRuntime.StartAsync(TrayOptions.Parse(start)).GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--detached", .. var detached])
            {
                LinuxTrayRuntime.Detach();
                args = detached;
            }
            var options = TrayOptions.Parse(args);
            if (options.SmokeSeconds is not null)
            {
                throw new ArgumentException("The Linux tray does not support --smoke-seconds.");
            }
            using var singleton = LinuxTrayRuntime.TryAcquire(LinuxTrayRuntime.DirectoryPath);
            if (singleton is null)
            {
                TrayActivation.ShowExistingAsync(LinuxTrayRuntime.PipeName, CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }
            using var lease = options.BundleRoot is null ? null : BundleVersionLease.Acquire(options.BundleRoot, "tray", "tray");
            var controller = new TrayController(new CliAppHostClient(options.CliPath),
                new FileTraySavedStateStore(TrayConfiguration.GetSavedStatePath(options.CliPath)),
                TrayConfiguration.LoadRecentAppHostLimit(TrayConfiguration.GetSettingsPath(options.CliPath)));
            var startup = new LinuxTrayStartupSettings(options, !RuntimeFeature.IsDynamicCodeSupported,
                LinuxTrayStartupSettings.GetAutostartDirectory());
            using var application = new LinuxTrayApplication(controller, startup);
            TrayActivation? activation = null;
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                application.RequestQuit();
            });
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                application.RequestQuit();
            });
            try
            {
                activation = new(LinuxTrayRuntime.PipeName, application.RestoreAsync, application.ReadyAsync, application.RequestQuit);
                controller.Start();
                application.Run();
            }
            finally
            {
                try
                {
                    activation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Linux tray failed: {ex.Message}");
            return 1;
        }
    }
}
