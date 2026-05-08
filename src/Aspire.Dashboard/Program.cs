// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard;

// === Phase 9h: dashboard lifecycle and crash diagnostics ===========
//
// The "Stop a WithTerminal resource → dashboard becomes unreachable" symptom
// (issue 16317 follow-up) is non-deterministic from the AppHost log alone:
// the AppHost log just goes silent on the dashboard's stdout/stderr. To
// distinguish between (a) clean shutdown via IHostApplicationLifetime,
// (b) unhandled exception, and (c) external kill (job object, console event,
// out-of-memory) we emit:
//
//   * AppDomain.UnhandledException     — fatal exception path
//   * TaskScheduler.UnobservedTaskException — leaked Task exception
//   * AppDomain.ProcessExit            — any clean process exit
//   * 10s [dashboard] heartbeat <ts>   — periodic liveness so the silence
//                                        timestamp in the AppHost log
//                                        bounds when the process stopped
//
// All four are written to stderr (and flushed on every line) because:
//   1. DCP attaches the dashboard's stderr to a per-resource pipe and
//      forwards it into the AppHost log unconditionally — no logger
//      configuration required.
//   2. ILogger output goes through ConsoleLoggerProvider whose buffer can
//      lose lines during abrupt termination.
//
// All of this is deliberately side-effect-free: no behavior change, just
// breadcrumbs for the next repro of the regression.
// ====================================================================
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    Console.Error.WriteLine($"[dashboard] UnhandledException (terminating={e.IsTerminating}): {ex?.GetType().FullName}: {ex?.Message}");
    if (ex?.StackTrace is { } stack)
    {
        Console.Error.WriteLine(stack);
    }
    Console.Error.Flush();
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[dashboard] UnobservedTaskException: {e.Exception.GetType().FullName}: {e.Exception.Message}");
    if (e.Exception.StackTrace is { } stack)
    {
        Console.Error.WriteLine(stack);
    }
    Console.Error.Flush();
    e.SetObserved();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    Console.Error.WriteLine($"[dashboard] ProcessExit at {DateTimeOffset.UtcNow:O}");
    Console.Error.Flush();
};

// Heartbeat task. Background, daemon-style: dies when the runtime tears
// down. We never await it; the loop's only job is to leave a periodic
// timestamp in stderr so the absence of one bounds the time of death.
_ = Task.Run(async () =>
{
    var heartbeatInterval = TimeSpan.FromSeconds(10);
    while (true)
    {
        try
        {
            Console.Error.WriteLine($"[dashboard] heartbeat {DateTimeOffset.UtcNow:O} pid={Environment.ProcessId}");
            Console.Error.Flush();
            await Task.Delay(heartbeatInterval).ConfigureAwait(false);
        }
        catch
        {
            // Heartbeat is best-effort. Never let it crash the process.
            return;
        }
    }
});

Console.Error.WriteLine($"[dashboard] startup pid={Environment.ProcessId} at {DateTimeOffset.UtcNow:O}");
Console.Error.Flush();

var app = new DashboardWebApplication();
return app.Run();
