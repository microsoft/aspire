// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Aspire.Hosting.Native.Rpc;

namespace Aspire.Hosting.Native.Server;

/// <summary>Projects configuration once, before lifecycle services consume typed values.</summary>
internal sealed record NativeServerConfiguration(
    NativeServerOptions Options,
    string RpcToken,
    bool TraceOperations,
    string? SocketPath,
    string? DcpPath,
    string? DashboardPath,
    int? DashboardPort,
    string ApplicationName,
    string? BackchannelPath,
    string? AppHostPath,
    NativeDashboardCredentials DashboardCredentials,
    NativeCliProcessMetadata CliProcess,
    string? GeneratedSdkDirectory)
{
    public bool IsPublishing { get; init; }
    public override string ToString() => nameof(NativeServerConfiguration);

    public static NativeServerConfiguration Parse(IConfiguration configuration, bool launchedByCli)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = NativeServerOptions.Load(configuration["ASPIRE_NATIVE_SERVER_OPTIONS_PATH"]);
        // A CLI launch supplies its own session token, regardless of inherited
        // native-testing configuration in the caller's shell.
        var token = (launchedByCli ? configuration["ASPIRE_REMOTE_APPHOST_TOKEN"] :
            configuration["ASPIRE_NATIVE_RPC_TOKEN"] ?? configuration["ASPIRE_REMOTE_APPHOST_TOKEN"])
            ?? throw new InvalidOperationException(launchedByCli ?
                "ASPIRE_REMOTE_APPHOST_TOKEN is required for a CLI server launch." :
                "Set ASPIRE_NATIVE_RPC_TOKEN to authenticate native server connections.");
        NativeRpcProtocol.ValidateAuthenticationToken(token);
        int? port = null;
        if (configuration["ASPIRE_NATIVE_DASHBOARD_PORT"] is { } value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 65535)
            {
                throw new ArgumentException("ASPIRE_NATIVE_DASHBOARD_PORT must be a local port or zero.");
            }
            port = number;
        }

        return new(options, token, configuration["ASPIRE_NATIVE_TRACE_OPERATIONS"] == "1",
            configuration["REMOTE_APP_HOST_SOCKET_PATH"], configuration["ASPIRE_NATIVE_DCP_PATH"],
            configuration["ASPIRE_DASHBOARD_PATH"], port,
            configuration["ASPIRE_NATIVE_APPLICATION_NAME"] ?? "Native AppHost", configuration["ASPIRE_BACKCHANNEL_PATH"],
            configuration["AppHost:FilePath"] ?? configuration["ASPIRE_APPHOST_FILEPATH"] ??
                configuration["AppHost:Path"] ?? configuration["ASPIRE_NATIVE_APPHOST_PATH"],
            new(configuration["ASPIRE_NATIVE_DASHBOARD_API_KEY"] ?? NewCredential(),
                configuration["AppHost:BrowserToken"] ?? NewCredential(),
                configuration["AppHost:DashboardApiKey"] ?? NewCredential(), NewCredential()),
            NativeCliProcessMetadata.Parse(configuration),
            configuration["ASPIRE_APPHOST_SERVER_V2_SDK_DIRECTORY"])
        {
            IsPublishing = configuration["ASPIRE_NATIVE_OPERATION"] is "publish" or "inspect"
        };
    }

    private static string NewCredential() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Keeps independently scoped Dashboard credentials out of runtime observations and diagnostics.</summary>
internal sealed record NativeDashboardCredentials(string ResourceServiceApiKey, string BrowserToken, string DashboardApiKey, string OtlpApiKey)
{
    public override string ToString() => nameof(NativeDashboardCredentials);
}

/// <summary>Parses the CLI's optional process identity metadata without ambient environment reads.</summary>
internal sealed record NativeCliProcessMetadata(int? ProcessId, DateTimeOffset? StartedAt, DateTimeOffset? StableStartedAt, string? LogFilePath)
{
    /// <summary>Disarms parent detection on disposal and prefers the stable PID-reuse identity.</summary>
    public IAsyncDisposable? MonitorParent(Action onParentExited, TimeProvider timeProvider)
    {
        if (ProcessId is not { } pid)
        {
            return null;
        }

        return ParentProcessLivenessMonitor.Start(pid,
            StableStartedAt?.ToUnixTimeMilliseconds() ?? StartedAt?.ToUnixTimeSeconds(),
            _ =>
            {
                onParentExited();
                return Task.CompletedTask;
            }, timeProvider, useRuntimeStartTime: StableStartedAt is null && StartedAt is not null);
    }

    public static NativeCliProcessMetadata Parse(IConfiguration configuration) => new(
        int.TryParse(configuration["ASPIRE_CLI_PID"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null,
        ParseTimestamp(configuration["ASPIRE_CLI_STARTED"], milliseconds: false),
        ParseTimestamp(configuration["ASPIRE_CLI_STARTED_STABLE"], milliseconds: true),
        configuration["ASPIRE_CLI_LOG_FILE"]);

    internal static DateTimeOffset? ParseTimestamp(string? raw, bool milliseconds)
    {
        // CLI metadata uses decimal Unix timestamps: ASPIRE_CLI_STARTED=1770000000
        // (seconds), ASPIRE_CLI_STARTED_STABLE=1770000000123 (milliseconds).
        // Missing, malformed and out-of-range optional identities remain absent.
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        try
        {
            return milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Parses only the native server's supported command-line transport shapes.</summary>
internal static class NativeStartupArguments
{
    public static EndPoint Parse(string[] arguments, string? configuredSocket) => arguments switch
    {
        ["server", "--contentRoot", var contentRoot] when Directory.Exists(contentRoot) &&
            configuredSocket is { } path => LocalEndpoint(path),
        [] when configuredSocket is { } socketPath => LocalEndpoint(socketPath),
        ["--socket", var path] => LocalEndpoint(path),
        ["--port", var port] when int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
            number is >= 0 and <= 65535 => new IPEndPoint(IPAddress.Loopback, number),
        _ => throw new ArgumentException("Use --socket <Unix socket path or Windows pipe name> or --port <loopback-port>.")
    };

    private static EndPoint LocalEndpoint(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The guest RPC address is a pipe name such as apphost.sock.<random-id>,
            // not a filesystem path. Node and the CLI add their native pipe prefix.
            if (string.IsNullOrWhiteSpace(path) || path.Length > 128 || path.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            {
                throw new ArgumentException("The Windows RPC endpoint must be a local pipe name.");
            }

            return new NamedPipeEndPoint(path);
        }
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The Unix RPC socket path must be absolute.");
        }

        return new UnixDomainSocketEndPoint(path);
    }
}
