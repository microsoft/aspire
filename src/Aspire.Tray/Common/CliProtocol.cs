// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aspire.Shared;

namespace Aspire.Tray;

internal static class CliProtocol
{
    public static TrayWatchMessage ReadWatchMessage(string line)
    {
        var message = JsonSerializer.Deserialize(line, TrayCliJsonContext.Default.TrayWatchMessage)
            ?? throw new CliProtocolException();
        if (message.Version != TrayCliProtocol.Version)
        {
            throw new CliProtocolException();
        }
        return message.Type switch
        {
            "snapshot" when message.AppHosts is not null && message.ErrorCode is null => message,
            "heartbeat" when message.AppHosts is null && message.ErrorCode is null => message,
            "error" when message.AppHosts is null && message.ErrorCode is "discovery_failed" or "limit_exceeded" => message,
            _ => throw new CliProtocolException()
        };
    }

    public static AppHostSnapshot ReadSnapshot(TrayWatchMessage message)
    {
        if (message.AppHosts is null || message.AppHosts.Count > TrayCliProtocol.MaximumAppHosts)
        {
            throw new CliProtocolException();
        }
        var processes = new HashSet<int>();
        var hosts = new List<AppHostInfo>(message.AppHosts.Count);
        foreach (var item in message.AppHosts)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.AppHostPath)
                || !Path.IsPathFullyQualified(item.AppHostPath) || item.AppHostPath.Contains('\0')
                || item.AppHostPid <= 0 || item.ProcessStartTimeUnixMilliseconds is <= 0)
            {
                throw new CliProtocolException();
            }
            var host = new AppHostInfo(item.AppHostPath, item.AppHostPid, item.DashboardUrl)
            {
                ProcessStartTimeUnixMilliseconds = item.ProcessStartTimeUnixMilliseconds,
                Health = item.Health switch
                {
                    "healthy" => AppHostHealth.Healthy,
                    "warning" => AppHostHealth.Warning,
                    "unhealthy" => AppHostHealth.Unhealthy,
                    _ => AppHostHealth.Unknown
                }
            };
            if (!processes.Add(host.AppHostPid))
            {
                throw new CliProtocolException();
            }
            hosts.Add(host);
        }
        return new(hosts.OrderBy(host => host.AppHostPath, StringComparer.Ordinal)
            .ThenBy(host => host.AppHostPid).ToArray(), DiscoveryState.Live);
    }

    public static TrayStopMessage ReadStopMessage(string line)
    {
        var message = JsonSerializer.Deserialize(line, TrayCliJsonContext.Default.TrayStopMessage)
            ?? throw new CliProtocolException();
        if (message.Version != TrayCliProtocol.Version || message.ExitCode < 0)
        {
            throw new CliProtocolException();
        }
        _ = GetStopOutcome(message.Outcome);
        if ((message.Outcome == "stopped") != (message.ExitCode == 0))
        {
            throw new CliProtocolException();
        }
        return message;
    }

    public static StopOutcome GetStopOutcome(string outcome) => outcome switch
    {
        "stopped" => StopOutcome.Stopped,
        "not_found" => StopOutcome.NotFound,
        "ambiguous" => StopOutcome.Ambiguous,
        "identity_mismatch" => StopOutcome.IdentityMismatch,
        "identity_unavailable" => StopOutcome.IdentityUnavailable,
        "stop_failed" => StopOutcome.Failed,
        "invalid_request" => StopOutcome.Incompatible,
        _ => throw new CliProtocolException()
    };

    public static async IAsyncEnumerable<string> ReadLinesAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // NDJSON frames are separated by LF or CRLF. Blank lines are ignored; a final
        // unterminated frame is accepted, but EOF never implies an empty/live snapshot.
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    var value = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (value.Length != 0)
                    {
                        yield return value;
                    }
                }
                else
                {
                    // CR belongs to the CRLF delimiter, not to the payload size. Permit
                    // precisely that extra character so the same boundary works on Windows.
                    if (line.Length >= TrayCliProtocol.MaximumMessageLength
                        && (line.Length != TrayCliProtocol.MaximumMessageLength || buffer[i] != '\r'))
                    {
                        throw new CliProtocolException();
                    }
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length != 0)
        {
            if (line.Length > TrayCliProtocol.MaximumMessageLength)
            {
                throw new CliProtocolException();
            }
            yield return line.ToString();
        }
    }
}

internal sealed class CliProtocolException : Exception
{
    public CliProtocolException() : base("The CLI returned an incompatible protocol response.")
    {
    }
}

internal sealed class CliDiscoveryException(string code) : IOException($"CLI discovery failed ({code}).")
{
    public string Code { get; } = code;
}
