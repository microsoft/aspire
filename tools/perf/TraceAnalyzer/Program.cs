// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Tool to analyze .nettrace files and extract Aspire startup timing information.
// Usage: dotnet run -- <path-to-nettrace-file>
// Output: Prints the startup duration in milliseconds to stdout, or "null" if events not found.

using Microsoft.Diagnostics.Tracing;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: TraceAnalyzer <path-to-nettrace-file>");
    return 1;
}

var tracePath = args[0];

if (!File.Exists(tracePath))
{
    Console.Error.WriteLine($"Error: File not found: {tracePath}");
    return 1;
}

// Event IDs from AspireEventSource
const int DcpModelCreationStartEventId = 17;
const int DcpModelCreationStopEventId = 18;

const string AspireHostingProviderName = "Microsoft-Aspire-Hosting";

try
{
    double? startTime = null;
    double? stopTime = null;

    using (var source = new EventPipeEventSource(tracePath))
    {
        source.Dynamic.AddCallbackForProviderEvents((string pName, string eName) =>
        {
            if (pName != AspireHostingProviderName)
            {
                return EventFilterResponse.RejectProvider;
            }
            if (eName == null || eName.StartsWith("DcpModelCreation", StringComparison.Ordinal))
            {
                return EventFilterResponse.AcceptEvent;
            }
            return EventFilterResponse.RejectEvent;
        }, 
        (TraceEvent traceEvent) =>
        {
            if ((int)traceEvent.ID == DcpModelCreationStartEventId)
            {
                startTime = traceEvent.TimeStampRelativeMSec;
            }
            else if ((int)traceEvent.ID == DcpModelCreationStopEventId)
            {
                stopTime = traceEvent.TimeStampRelativeMSec;
            }
        });

        source.Process();
    }

    if (startTime.HasValue && stopTime.HasValue)
    {
        var duration = stopTime.Value - startTime.Value;
        Console.WriteLine(duration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }
    else
    {
        Console.WriteLine("null");
        return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error parsing trace: {ex.Message}");
    return 1;
}
