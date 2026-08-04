// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// In-memory <see cref="IPeerInstallProbe"/> for tests. Returns
/// scripted results based on the binary path passed in, so we can
/// exercise discovery + trust-gate logic without spawning real CLI
/// processes.
/// </summary>
internal sealed class FakePeerInstallProbe : IPeerInstallProbe
{
    private readonly Dictionary<string, PeerProbeResult> _responses;
    private readonly StringComparer _pathComparer;
    private readonly List<string> _probedPaths = new();
    private readonly object _probedPathsLock = new();

    public FakePeerInstallProbe(IDictionary<string, PeerProbeResult>? responses = null)
    {
        _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _responses = new Dictionary<string, PeerProbeResult>(_pathComparer);
        if (responses is not null)
        {
            foreach (var kvp in responses)
            {
                _responses[kvp.Key] = kvp.Value;
            }
        }
    }

    // Snapshot accessor: returns a copy so callers can iterate without risking
    // concurrent mutation. Mutations themselves are serialized via _probedPathsLock
    // so ProbeAsync stays safe even if a future change parallelizes peer probes.
    public IReadOnlyList<string> ProbedPaths
    {
        get
        {
            lock (_probedPathsLock)
            {
                return _probedPaths.ToArray();
            }
        }
    }

    public Task<PeerProbeResult> ProbeAsync(string binaryPath, CancellationToken cancellationToken)
    {
        lock (_probedPathsLock)
        {
            _probedPaths.Add(binaryPath);
        }
        var result = _responses.TryGetValue(binaryPath, out var value)
            ? value
            : new PeerProbeResult.Failed("No response configured.");
        return Task.FromResult(result);
    }
}
