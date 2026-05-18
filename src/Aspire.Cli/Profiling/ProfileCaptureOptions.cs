// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AspireRootCommand = Aspire.Cli.Commands.RootCommand;
using BootstrapRootCommand = System.CommandLine.RootCommand;

namespace Aspire.Cli.Profiling;

// Captures the small amount of state that must be decided before normal CLI startup.
// The hidden --capture-profile flag is parsed before DI so telemetry can be configured
// early, and the selected output path, session ID, and private dashboard/OTLP endpoints
// are then reused by the later profile collector and AppHost launch paths.
internal sealed record ProfileCaptureOptions(
    string OutputPath,
    string DashboardUrl,
    string OtlpGrpcUrl,
    string OtlpHttpUrl,
    string SessionId,
    TimeSpan StartupDelay)
{
    private const int DefaultStartupDelaySeconds = 0;

    public static ProfileCaptureOptions? TryCreate(
        IReadOnlyList<string> args,
        TimeProvider timeProvider,
        DirectoryInfo workingDirectory,
        Func<int>? portProvider = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        // This deliberately uses a tiny bootstrap command instead of the real DI-backed root
        // command. Profiling must be enabled before DI creates telemetry, and anything after "--"
        // belongs to the wrapped command/AppHost rather than to the root Aspire invocation:
        //   aspire run --capture-profile -- --capture-profile
        var captureProfileOption = new Option<bool>(AspireRootCommand.CaptureProfileOption.Name);
        var captureProfileOutputOption = new Option<string?>(AspireRootCommand.CaptureProfileOutputOption.Name);
        var captureProfileDelayOption = new Option<int>(AspireRootCommand.CaptureProfileDelayOption.Name)
        {
            DefaultValueFactory = _ => DefaultStartupDelaySeconds
        };
        var bootstrapCommand = new BootstrapRootCommand
        {
            TreatUnmatchedTokensAsErrors = false
        };
        bootstrapCommand.Options.Add(captureProfileOption);
        bootstrapCommand.Options.Add(captureProfileOutputOption);
        bootstrapCommand.Options.Add(captureProfileDelayOption);

        var parseResult = bootstrapCommand.Parse([.. args]);
        if (!parseResult.GetValue(captureProfileOption))
        {
            return null;
        }

        var outputPath = parseResult.GetValue(captureProfileOutputOption);
        var startupDelaySeconds = Math.Max(0, parseResult.GetValue(captureProfileDelayOption));
        var getPort = portProvider ?? GetFreeTcpPort;
        // Use loopback ephemeral ports so concurrent captures do not fight over fixed dashboard
        // or OTLP endpoints. The private dashboard binds these same values shortly after parsing.
        var dashboardPort = getPort();
        var otlpGrpcPort = getPort();
        var otlpHttpPort = getPort();
        var sessionId = Guid.NewGuid().ToString("N");

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(workingDirectory.FullName, CreateDefaultOutputFileName(timeProvider, sessionId));
        }
        else
        {
            if (!Path.IsPathRooted(outputPath))
            {
                outputPath = Path.Combine(workingDirectory.FullName, outputPath);
            }

            // Treat an existing directory, or a path with a trailing directory separator, as an
            // output directory and generate the same default archive name inside it. A non-existent
            // path without a trailing separator is preserved as the requested archive file path.
            if (Directory.Exists(outputPath) || Path.EndsInDirectorySeparator(outputPath))
            {
                outputPath = Path.Combine(outputPath, CreateDefaultOutputFileName(timeProvider, sessionId));
            }
        }

        return new ProfileCaptureOptions(
            Path.GetFullPath(outputPath),
            $"http://127.0.0.1:{dashboardPort.ToString(CultureInfo.InvariantCulture)}",
            $"http://127.0.0.1:{otlpGrpcPort.ToString(CultureInfo.InvariantCulture)}",
            $"http://127.0.0.1:{otlpHttpPort.ToString(CultureInfo.InvariantCulture)}",
            sessionId,
            TimeSpan.FromSeconds(startupDelaySeconds));
    }

    private static string CreateDefaultOutputFileName(TimeProvider timeProvider, string sessionId)
    {
        var timestamp = timeProvider.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"aspire-profile-{timestamp}-{sessionId[..8]}.zip";
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
