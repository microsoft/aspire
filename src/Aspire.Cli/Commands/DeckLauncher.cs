// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Hosting;

namespace Aspire.Cli.Commands;

/// <summary>
/// The endpoints Aspire Deck should use: the OTLP endpoints it hosts for telemetry ingestion
/// and, optionally, the resource service it connects to.
/// </summary>
internal sealed record DeckLaunchInfo(string OtlpGrpcUrl, string OtlpHttpUrl, string? ResourceServiceUrl);

/// <summary>
/// Resolves the Aspire Deck executable and launches it wired to a set of endpoints. Shared by the
/// standalone <c>aspire deck</c> command and the <c>aspire run --deck</c> path so both resolve and
/// start Deck identically.
/// </summary>
internal sealed class DeckLauncher(LayoutProcessRunner layoutProcessRunner, CliExecutionContext executionContext)
{
    /// <summary>
    /// Resolves the Aspire Deck executable, in priority order: the explicit path (e.g. the
    /// <c>--deck-path</c> option), the <c>ASPIRE_DECK_PATH</c> environment variable, then a
    /// conventional local build output under the repository.
    /// </summary>
    public string? ResolveExecutable(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var fromEnv = executionContext.GetEnvironmentVariable("ASPIRE_DECK_PATH");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        return ResolveDevBuildExecutable();
    }

    /// <summary>
    /// Starts Aspire Deck wired to <paramref name="info"/>. The Deck process reads its endpoints
    /// from the same environment variables the dashboard reads, so launching is just a matter of
    /// populating them. Additional environment variables can be supplied via <paramref name="extraEnvironment"/>.
    /// </summary>
    public IProcessExecution Start(string deckPath, DeckLaunchInfo info, IDictionary<string, string>? extraEnvironment = null, ProcessInvocationOptions? options = null)
    {
        var environmentVariables = new Dictionary<string, string>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = info.OtlpGrpcUrl,
            [KnownConfigNames.DashboardOtlpHttpEndpointUrl] = info.OtlpHttpUrl,
        };

        if (!string.IsNullOrEmpty(info.ResourceServiceUrl))
        {
            environmentVariables[KnownConfigNames.ResourceServiceEndpointUrl] = info.ResourceServiceUrl;
        }

        if (extraEnvironment is not null)
        {
            foreach (var kvp in extraEnvironment)
            {
                environmentVariables[kvp.Key] = kvp.Value;
            }
        }

        return layoutProcessRunner.Start(deckPath, [], environmentVariables: environmentVariables, options: options);
    }

    /// <summary>
    /// Reserves a free TCP port on the loopback interface. There is an inherent (small) race between
    /// releasing the listener here and the consumer binding the port, but it is acceptable for local
    /// development wiring where the alternative — letting each process pick its own port — would
    /// prevent the CLI from telling both the AppHost and Deck which endpoints to use.
    /// </summary>
    public static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Locates a locally built Deck binary by walking up from the CLI's base directory looking for
    /// <c>src/Aspire.Deck/src-tauri/target</c> (the Cargo output directory), preferring a release
    /// build over a debug build. This makes Deck work out of the box from a repo checkout after it
    /// has been built, without requiring <c>ASPIRE_DECK_PATH</c>.
    /// </summary>
    private static string? ResolveDevBuildExecutable()
    {
        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "aspire-deck.exe"
            : "aspire-deck";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var target = Path.Combine(dir.FullName, "src", "Aspire.Deck", "src-tauri", "target");
            if (Directory.Exists(target))
            {
                foreach (var profile in new[] { "release", "debug" })
                {
                    var candidate = Path.Combine(target, profile, exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            dir = dir.Parent;
        }

        return null;
    }
}
