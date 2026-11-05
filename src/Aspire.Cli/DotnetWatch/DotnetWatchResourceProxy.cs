// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// SPIKE (plan D10 / Session 6a): hidden, infrastructure-only per-instance proxy between DCP and the pinned
/// <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server's resource pipe.
/// </summary>
/// <remarks>
/// <para>
/// Usage (constructed by <c>Aspire.Hosting.Dotnet</c>, never by users):
/// <c>aspire __dotnet-watch-proxy --server &lt;pipe&gt; --entrypoint &lt;path&gt; -- [application args]</c>.
/// </para>
/// <para>
/// The service environment is NOT passed on the command line. DCP launches this process with the service
/// environment, and <c>ASPIRE_DOTNET_WATCH_FORWARD_ENV</c> names (not values) the variables the app model produced.
/// Only those are forwarded in the pipe request.
/// </para>
/// <para>
/// Wire contract pinned to watcher 10.0.403 (dotnet/dotnet 5a2162be): version byte 1, 7-bit-length-prefixed UTF-8
/// JSON <c>LaunchResourceRequest</c>, ACK byte 1, then records of (type byte 1=stdout/2=stderr, 7-bit-length string).
/// </para>
/// <para>
/// This runs before the CLI host is built: no banners, prompts, first-run, telemetry or update checks.
/// </para>
/// </remarks>
internal static class DotnetWatchResourceProxy
{
    public const string CommandName = "__dotnet-watch-proxy";
    public const string ForwardedEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_FORWARD_ENV";
    public const string ConnectTimeoutVariable = "ASPIRE_DOTNET_WATCH_PROXY_CONNECT_TIMEOUT_SECONDS";

    // The proxy only starts after the server is healthy (WaitFor), so the pipe normally exists immediately.
    internal const int DefaultConnectTimeoutSeconds = 30;

    private const byte ProtocolVersion = 1;
    private const byte OutputTypeStdout = 1;
    private const byte OutputTypeStderr = 2;
    private const int MaxRecordLength = 16 * 1024 * 1024;

    // The proxy never reports 0 for an application outcome it could not observe.
    internal const int ExitUsage = 64;
    internal const int ExitConnectFailed = 69;
    internal const int ExitRejected = 70;
    internal const int ExitServerDisconnected = 75;

    public static bool Matches(string[] args) => args.Length > 0 && args[0] == CommandName;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var server, out var entryPoint, out var appArgs, out var error))
        {
            Console.Error.WriteLine($"{CommandName}: {error}");
            return ExitUsage;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            ctx =>
            {
                ctx.Cancel = true;
                shutdown.Cancel();
            });

        var environment = CollectForwardedEnvironment(
            Environment.GetEnvironmentVariable(ForwardedEnvironmentNamesVariable),
            Environment.GetEnvironmentVariable,
            Environment.GetEnvironmentVariables().Keys.Cast<string>());

        var timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(ConnectTimeoutVariable), out var seconds) && seconds > 0 ? seconds : DefaultConnectTimeoutSeconds);

        await using var pipe = new NamedPipeClientStream(".", server, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            connectTimeout.CancelAfter(timeout);
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{CommandName}: timed out after {timeout.TotalSeconds:0}s connecting to the watch server.");
            return ExitConnectFailed;
        }
        catch (OperationCanceledException)
        {
            return ExitConnectFailed;
        }

        try
        {
            var request = CreateRequestJson(entryPoint, appArgs, environment);
            await pipe.WriteAsync(new[] { ProtocolVersion }, shutdown.Token).ConfigureAwait(false);
            await WriteStringAsync(pipe, request, shutdown.Token).ConfigureAwait(false);
            await pipe.FlushAsync(shutdown.Token).ConfigureAwait(false);

            var ack = await ReadByteOrEofAsync(pipe, shutdown.Token).ConfigureAwait(false);
            if (ack != 1)
            {
                Console.Error.WriteLine($"{CommandName}: watch server rejected the launch request for '{entryPoint}'.");
                return ExitRejected;
            }

            // ACK precedes application launch and says nothing about application health (plan 3.3).
            using var stdout = Console.OpenStandardOutput();
            using var stderr = Console.OpenStandardError();
            while (true)
            {
                var type = await ReadByteOrEofAsync(pipe, shutdown.Token).ConfigureAwait(false);
                if (type < 0)
                {
                    // The server owns the process and its exit code is not represented on this pipe. While the server
                    // is alive it keeps this pipe open across app exits so it can relaunch on the next change; app exit
                    // codes reach the resource through the status pipe instead (hosting sets the "Crashed" state and
                    // ExitCode). Reaching EOF means the server itself went away.
                    Console.Error.WriteLine($"{CommandName}: watch server disconnected; the application's exit status is unknown.");
                    return ExitServerDisconnected;
                }

                var line = await ReadStringAsync(pipe, shutdown.Token).ConfigureAwait(false);
                var target = type switch
                {
                    OutputTypeStdout => stdout,
                    OutputTypeStderr => stderr,
                    _ => throw new InvalidDataException($"Unexpected output record type 0x{type:X2}.")
                };
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                await target.WriteAsync(bytes, shutdown.Token).ConfigureAwait(false);
                await target.FlushAsync(shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // DCP stop / Ctrl+C. Disposing the pipe makes the server terminate the application process.
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"{CommandName}: {ex.Message}");
            return ExitServerDisconnected;
        }
    }

    internal static bool TryParse(string[] args, out string server, out string entryPoint, out List<string> appArgs, out string? error)
    {
        server = entryPoint = string.Empty;
        appArgs = [];
        error = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server" when i + 1 < args.Length:
                    server = args[++i];
                    break;
                case "--entrypoint" when i + 1 < args.Length:
                    entryPoint = args[++i];
                    break;
                case "--":
                    appArgs.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default:
                    error = $"unexpected argument '{args[i]}'.";
                    return false;
            }
        }

        if (server.Length == 0 || entryPoint.Length == 0)
        {
            error = "--server and --entrypoint are required.";
            return false;
        }

        return true;
    }

    // Certificate trust and HTTPS settings are added by DCP after the resource's environment callbacks run, so they
    // can't appear in the forwarded-name list; forward them whenever DCP set them on the proxy.
    private static readonly string[] s_alwaysForwardedNames = ["SSL_CERT_DIR", "SSL_CERT_FILE"];
    private static readonly string[] s_alwaysForwardedPrefixes = ["Kestrel__Certificates__", "ASPNETCORE_Kestrel__Certificates__"];

    internal static Dictionary<string, string> CollectForwardedEnvironment(string? names, Func<string, string?> lookup, IEnumerable<string>? processVariableNames = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = (names ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Concat(s_alwaysForwardedNames)
            .Concat((processVariableNames ?? []).Where(n => s_alwaysForwardedPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))));
        foreach (var name in candidates)
        {
            if (lookup(name) is { } value)
            {
                result[name] = value;
            }
        }

        return result;
    }

    internal static string CreateRequestJson(string entryPoint, IReadOnlyList<string> appArgs, IReadOnlyDictionary<string, string> environment)
    {
        var env = new JsonObject();
        foreach (var (key, value) in environment)
        {
            env[key] = value;
        }

        // The watcher appends ApplicationArguments directly to `dotnet run`, so "--" keeps them from being parsed as
        // SDK options (for example `--configuration`). An empty array means "no arguments".
        var argsArray = new JsonArray();
        if (appArgs.Count > 0)
        {
            argsArray.Add((JsonNode?)JsonValue.Create("--"));
        }

        foreach (var a in appArgs)
        {
            argsArray.Add((JsonNode?)JsonValue.Create(a));
        }

        var json = new JsonObject
        {
            ["EntryPoint"] = entryPoint,
            ["ApplicationArguments"] = argsArray,
            ["EnvironmentVariables"] = env,
            // Optional<string?> on the watcher side: JSON null fails to deserialize; the object form is required.
            // Aspire materializes launch-profile settings itself, so request "no launch profile".
            ["LaunchProfileName"] = new JsonObject { ["HasValue"] = false, ["Value"] = null },
        };
        return json.ToJsonString();
    }

    internal static async Task WriteStringAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var prefix = new List<byte>(5);
        var remaining = (uint)bytes.Length;
        while (remaining > 0x7Fu)
        {
            prefix.Add((byte)(remaining | 0x80u));
            remaining >>= 7;
        }

        prefix.Add((byte)remaining);
        await stream.WriteAsync(prefix.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ReadStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (shift > 28)
            {
                throw new InvalidDataException("Invalid 7-bit encoded length.");
            }

            var b = await ReadByteOrEofAsync(stream, cancellationToken).ConfigureAwait(false);
            if (b < 0)
            {
                throw new EndOfStreamException();
            }

            length |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                break;
            }
        }

        if (length is < 0 or > MaxRecordLength)
        {
            throw new InvalidDataException($"Output record length {length} is out of bounds.");
        }

        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<int> ReadByteOrEofAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }
}
