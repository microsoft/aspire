// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using NuGetLogLevel = NuGet.Common.LogLevel;
using NuGetLogMessage = NuGet.Common.ILogMessage;
using INuGetLogger = NuGet.Common.ILogger;

namespace Aspire.Managed.NuGet;

/// <summary>
/// Console logger adapter for NuGet operations.
/// </summary>
internal sealed class NuGetLogger(bool verbose) : INuGetLogger
{
    public void Log(NuGetLogLevel level, string data)
    {
        if (!verbose && level < NuGetLogLevel.Warning)
        {
            return;
        }

        var prefix = level switch
        {
            NuGetLogLevel.Error => "ERROR: ",
            NuGetLogLevel.Warning => "WARNING: ",
            _ => ""
        };

        // All log output goes to stderr to avoid mixing with JSON output on stdout
        Console.Error.WriteLine($"{prefix}{data}");
    }

    public void Log(NuGetLogMessage message) => Log(message.Level, message.Message);
    public Task LogAsync(NuGetLogLevel level, string data) { Log(level, data); return Task.CompletedTask; }
    public Task LogAsync(NuGetLogMessage message) { Log(message); return Task.CompletedTask; }
    public void LogDebug(string data) => Log(NuGetLogLevel.Debug, data);
    public void LogError(string data) => Log(NuGetLogLevel.Error, data);
    public void LogInformation(string data) => Log(NuGetLogLevel.Information, data);
    public void LogInformationSummary(string data) => Log(NuGetLogLevel.Information, data);
    public void LogMinimal(string data) => Log(NuGetLogLevel.Minimal, data);
    public void LogVerbose(string data) => Log(NuGetLogLevel.Verbose, data);
    public void LogWarning(string data) => Log(NuGetLogLevel.Warning, data);
}
