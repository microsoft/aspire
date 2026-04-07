# Wall-clock time investigations

Aspire has some built-in EventPipe providers that you can collect from during performance investigations.

## Collection with dotnet trace

You can use [dotnet trace](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace) to a trace from a running Aspire application (identified via process ID). Follow the documentation, but make sure to include the Aspire event provider, as in the example below:

```sh
dotnet trace collect --providers *Microsoft-Aspire-Hosting --process-id 1234 --buffersize 8192
```

Then analyze using `dotnet trace report` or convert to a format such as Speedscope.

`dotnet trace` allows granular control over data collected from your program. For more information see the documentation for `--providers` and `--clrevents` parameter for the [dotnet trace collect command](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace#dotnet-trace-collect). For additional information [well-known event providers in .NET](https://learn.microsoft.com/dotnet/core/diagnostics/well-known-event-providers) and [reference for .NET runtime events](https://learn.microsoft.com/dotnet/fundamentals/diagnostics/runtime-events).

## Collection with PerfView (Windows only)

On Windows, you can collect the trace using PerfView instead (https://github.com/microsoft/perfview/releases). Use Collect menu, then Collect again, to open the collection dialog and make the following changes from defaults:

1. If you do not intend to share the trace with anyone, uncheck the "Zip" and "Merge" option.
1. Increase Circular MB to `8192`.
1. Check the `Thread Time` checkbox.
1. Expand Advanced Options panel and make sure you have Kernel Base, Cpu Samples, File I/O, .NET, and Task (TPL) options checked.
1. In "Additional Providers" add `*Microsoft-Aspire-Hosting` (note the asterisk before the Aspire provider name!).

Once you are ready, hit "Start Collection" button and run your scenario.

When done with the scenario, hit "Stop Collection". Wait for PerfView to finish merging and analyzing data (the "working" status bar stops flashing).

### Verify that PerfView trace contains Aspire data

This is an optional step, but if you are wondering if your trace has been captured properly, you can check the following:

1. Open the trace (usually named PerfViewData.etl, if you haven't changed the name) and double click Events view. Verify you have a bunch of events from the Microsoft-Aspire-Hosting provider.

## Profiling scripts

The `tools/perf` folder in the repository contains scripts that help quickly assess the impact of code changes on key performance scenarios. Currently available scripts are:

| Script | Description |
| --- | --------- |
| `Measure-StartupPerformance.ps1` | Measures startup time for a specific Aspire project. More specifically, the script measures the time to get all application services and supporting dependencies CREATED; the application is not necessarily responsive after measured time. |
