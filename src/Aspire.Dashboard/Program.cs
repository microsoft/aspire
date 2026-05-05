// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard;

// Surface fatal background-task / unhandled exceptions to stderr so we have a
// breadcrumb when the dashboard process exits unexpectedly. AppHost-managed
// dashboards forward stderr to the AppHost's own log stream, so anything we
// write here is recoverable from the AppHost output.
//
// .NET's default behaviour is:
//   * AppDomain.UnhandledException — process is going to terminate.
//   * TaskScheduler.UnobservedTaskException — observed at GC, NOT fatal by
//     default (since .NET 4.5+) but can mask real bugs.
// We log both at error so a future "dashboard silently died" can be diagnosed
// from the AppHost log without needing a debugger attached.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    Console.Error.WriteLine($"[dashboard] UnhandledException (terminating={e.IsTerminating}): {ex?.GetType().FullName}: {ex?.Message}");
    if (ex?.StackTrace is { } stack)
    {
        Console.Error.WriteLine(stack);
    }
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[dashboard] UnobservedTaskException: {e.Exception.GetType().FullName}: {e.Exception.Message}");
    if (e.Exception.StackTrace is { } stack)
    {
        Console.Error.WriteLine(stack);
    }
    // Mark as observed so it isn't escalated by any custom handler.
    e.SetObserved();
};

var app = new DashboardWebApplication();
return app.Run();
