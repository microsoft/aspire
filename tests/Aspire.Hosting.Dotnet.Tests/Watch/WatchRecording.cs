// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests.Watch;

/// <summary>Replays real watch server observations (live or recorded) through <see cref="DotnetWatchEventInterpreter"/>.</summary>
internal static class WatchRecording
{
    public static IReadOnlyList<WatchObservation> Load(string name, string root)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Watch", "Recordings", name + ".log");
        return File.ReadAllLines(path)
            .Where(line => line.Length > 0)
            .Select(line => line.Replace(WatchServerHarness.RootPlaceholder, line.StartsWith("status ", StringComparison.Ordinal) ? JsonEncodedText.Encode(root).ToString() : root, StringComparison.Ordinal))
            .Select(line => line.StartsWith("status ", StringComparison.Ordinal)
                ? new WatchObservation(WatchObservationKind.Status, line["status ".Length..])
                : new WatchObservation(WatchObservationKind.Output, line.StartsWith("out ", StringComparison.Ordinal) ? line["out ".Length..] : line))
            .ToArray();
    }

    /// <summary>
    /// Feeds observations in arrival order, the way the coordinator does (status pipe and server console), and flushes
    /// pending failures where the coordinator's 2-second timer would.
    /// </summary>
    public static List<WatchNotice> Interpret(IEnumerable<WatchObservation> observations, string projectPath, string resourceName, string serverWorkingDirectory)
    {
        var interpreter = new DotnetWatchEventInterpreter([(projectPath, resourceName)], serverWorkingDirectory);
        var notices = new List<WatchNotice>();
        foreach (var observation in observations)
        {
            if (observation.Kind == WatchObservationKind.Status)
            {
                var status = observation.Status;
                var projects = status.TryGetProperty("projects", out var p) ? p.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
                bool? success = status.TryGetProperty("success", out var s) ? s.GetBoolean() : null;
                int? exitCode = status.TryGetProperty("exitCode", out var c) ? c.GetInt32() : null;
                notices.AddRange(interpreter.OnStatus(status.GetProperty("type").GetString()!, projects, success, exitCode));
            }
            else
            {
                notices.AddRange(interpreter.OnLogLine(observation.Text));
            }
        }

        notices.AddRange(interpreter.FlushPending());
        return notices;
    }
}
