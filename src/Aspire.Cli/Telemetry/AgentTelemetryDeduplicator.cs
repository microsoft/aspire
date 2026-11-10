// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Shared.Telemetry;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Drops repeated deliveries of the same agent tool call so one event is counted once.
/// </summary>
/// <remarks>
/// VS Code reads both Copilot and Claude hook files, so one tool call can run both installed hooks.
/// Each hook runs in its own short-lived process, so the first delivery leaves a marker file keyed
/// by the session ID and tool-use ID. The client name is not part of the key because the two hook
/// files can attribute the same call to different clients.
/// </remarks>
internal sealed class AgentTelemetryDeduplicator(string directory, TimeProvider timeProvider)
{
    internal static readonly TimeSpan s_markerLifetime = TimeSpan.FromHours(1);

    public AgentTelemetryDeduplicator(TimeProvider timeProvider)
        : this(Path.Combine(Path.GetDirectoryName(AspireTelemetryExporter.GetTelemetryStoragePath("cli"))!, "agent-telemetry-seen"), timeProvider)
    {
    }

    internal string Directory => directory;

    /// <summary>
    /// Returns <see langword="true"/> when this is the first delivery of the tool call, or when it cannot be determined.
    /// </summary>
    public bool TryMarkFirst(string sessionId, string toolUseId)
    {
        var markerPath = Path.Combine(directory, KeyFor(sessionId, toolUseId));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Prune();

            // CreateNew is atomic across processes, so concurrent hooks for the same call cannot both win.
            using var _ = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) when (File.Exists(markerPath))
        {
            return false;
        }
        catch (Exception)
        {
            // Record rather than lose the event when markers cannot be written.
            return true;
        }
    }

    private static string KeyFor(string sessionId, string toolUseId)
        => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes($"{sessionId}\n{toolUseId}")).ToString("x16", CultureInfo.InvariantCulture);

    private void Prune()
    {
        var cutoff = timeProvider.GetUtcNow() - s_markerLifetime;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff.UtcDateTime)
                {
                    file.Delete();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
