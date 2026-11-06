// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using static Aspire.Shared.DotnetWatch.DotnetWatchHostProtocol;

namespace Aspire.Hosting.Dotnet.WatchHost;

/// <summary>
/// Plan D10 / Session 6a: per-replica proxy between DCP and the pinned <c>Microsoft.DotNet.HotReload.Watch.Aspire</c>
/// server's resource pipe.
/// </summary>
/// <remarks>
/// <para>
/// Usage (constructed by <c>Aspire.Hosting.Dotnet</c>, never by users):
/// <c>dotnet exec Aspire.Hosting.Dotnet.WatchHost.dll proxy --server &lt;pipe&gt; --entrypoint &lt;path&gt; [--launch-gate &lt;file&gt;] [--working-directory &lt;dir&gt;] -- [application args]</c>.
/// <c>--launch-gate</c> serializes the first launch of a project across its replicas (see <see cref="LaunchGate"/>).
/// </para>
/// <para>
/// The watcher launches every service as <c>dotnet run</c> from the project directory, with the watcher's own process
/// environment plus the request's variables. So the proxy also asks <c>dotnet run</c> for the project (<c>--project</c>,
/// since that directory can hold several) and the working directory (<c>-p:RunWorkingDirectory</c>), and resets the
/// variables set only for the watcher
/// (<c>ASPIRE_DOTNET_WATCH_RESTORE_ENV</c>, for example its temp directory or build configuration) to the proxy's own
/// values, or unsets them.
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
/// </remarks>
internal static class ResourceProxy
{
    private const string CommandName = "dotnet-watch proxy";

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

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"{CommandName}: {error}");
            return ExitUsage;
        }

        using var shutdown = new ShutdownSignal();
        var environment = CollectForwardedEnvironment(
            Environment.GetEnvironmentVariable(ForwardedEnvironmentNamesVariable),
            Environment.GetEnvironmentVariable,
            Environment.GetEnvironmentVariables().Keys.Cast<string>());
        AddRestoredEnvironment(environment, Environment.GetEnvironmentVariable(RestoreEnvironmentNamesVariable), Environment.GetEnvironmentVariable);
        var request = CreateRequestJson(options.EntryPoint, GetRunOptions(options.EntryPoint, options.WorkingDirectory), options.AppArgs, environment);
        var timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(ConnectTimeoutVariable), out var seconds) && seconds > 0 ? seconds : DefaultConnectTimeoutSeconds);

        LaunchGate? gate = null;
        var pipe = new NamedPipeClientStream(".", options.Server, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var pipeScope = pipe.ConfigureAwait(false);
        try
        {
            if (options.LaunchGate is { } marker)
            {
                gate = await LaunchGate.EnterAsync(marker, shutdown.Token).ConfigureAwait(false);
            }

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            connectTimeout.CancelAfter(timeout);
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            gate?.Dispose();
            if (!shutdown.Token.IsCancellationRequested)
            {
                Console.Error.WriteLine($"{CommandName}: timed out after {timeout.TotalSeconds:0}s connecting to the watch server.");
            }

            return ExitConnectFailed;
        }

        using (gate)
        {
            try
            {
                await pipe.WriteAsync(new[] { ProtocolVersion }, shutdown.Token).ConfigureAwait(false);
                await WriteStringAsync(pipe, request, shutdown.Token).ConfigureAwait(false);
                await pipe.FlushAsync(shutdown.Token).ConfigureAwait(false);

                if (await ReadByteOrEofAsync(pipe, shutdown.Token).ConfigureAwait(false) != 1)
                {
                    Console.Error.WriteLine($"{CommandName}: watch server rejected the launch request for '{options.EntryPoint}'.");
                    return ExitRejected;
                }

                // ACK precedes application launch and says nothing about application health (plan 3.3).
                var forwarding = ForwardOutputAsync(pipe, shutdown.Token);
                if (gate is not null)
                {
                    await gate.ReleaseAfterLaunchAsync(forwarding).ConfigureAwait(false);
                }

                await forwarding.ConfigureAwait(false);

                // The server owns the process and its exit code is not represented on this pipe. While the server is
                // alive it keeps this pipe open across app exits so it can relaunch on the next change; app exit codes
                // reach the resource through the status pipe instead (hosting sets the "Crashed" state and ExitCode).
                // Reaching EOF means the server itself went away.
                Console.Error.WriteLine($"{CommandName}: watch server disconnected; the application's exit status is unknown.");
                return ExitServerDisconnected;
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
    }

    private static async Task ForwardOutputAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var stdout = Console.OpenStandardOutput();
        using var stderr = Console.OpenStandardError();
        while (await ReadByteOrEofAsync(pipe, cancellationToken).ConfigureAwait(false) is var type and >= 0)
        {
            var target = type switch
            {
                OutputTypeStdout => stdout,
                OutputTypeStderr => stderr,
                _ => throw new InvalidDataException($"Unexpected output record type 0x{type:X2}.")
            };
            var line = await ReadStringAsync(pipe, cancellationToken).ConfigureAwait(false);
            await target.WriteAsync(Encoding.UTF8.GetBytes(line + Environment.NewLine), cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed record Options(string Server, string EntryPoint, string? WorkingDirectory, string? LaunchGate, List<string> AppArgs);

    internal static bool TryParse(string[] args, [NotNullWhen(true)] out Options? options, out string? error)
    {
        string? server = null, entryPoint = null, workingDirectory = null, launchGate = null;
        var appArgs = new List<string>();
        options = null;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case ServerOption when i + 1 < args.Length:
                    server = args[++i];
                    break;
                case EntryPointOption when i + 1 < args.Length:
                    entryPoint = args[++i];
                    break;
                case WorkingDirectoryOption when i + 1 < args.Length:
                    workingDirectory = args[++i];
                    break;
                case LaunchGateOption when i + 1 < args.Length:
                    launchGate = args[++i];
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

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(entryPoint))
        {
            error = $"{ServerOption} and {EntryPointOption} are required.";
            return false;
        }

        options = new Options(server, entryPoint, workingDirectory, launchGate, appArgs);
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

    /// <summary>
    /// Resets each variable the watcher has only for itself (and the app model didn't set) to this process's value, or
    /// unsets it (the watcher's in-app agent removes variables set to an empty string).
    /// </summary>
    internal static void AddRestoredEnvironment(Dictionary<string, string> environment, string? names, Func<string, string?> lookup)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var name in (names ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!environment.Keys.Contains(name, comparer))
            {
                environment[name] = lookup(name) ?? string.Empty;
            }
        }
    }

    /// <summary><c>dotnet run</c> options that precede the application arguments.</summary>
    /// <remarks>
    /// The watcher runs <c>dotnet run --no-build</c> in the entry point's directory without selecting it, which fails
    /// when that directory holds more than one project and never finds a file-based app's .cs file. <c>--project</c>
    /// or <c>--file</c> selects the requested one explicitly.
    /// </remarks>
    internal static List<string> GetRunOptions(string entryPoint, string? workingDirectory)
    {
        var options = new List<string>();
        if (IsProjectFile(entryPoint))
        {
            options.Add("--project");
            options.Add(entryPoint);
        }
        else
        {
            options.Add("--file");
            options.Add(entryPoint);
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            options.Add($"-p:RunWorkingDirectory={EscapeMSBuildPropertyValue(workingDirectory)}");
        }

        return options;
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase));

    // MSBuild splits -p values on ';' and ',' and unescapes %XX.
    internal static string EscapeMSBuildPropertyValue(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);

    internal static string CreateRequestJson(string entryPoint, IReadOnlyList<string> runOptions, IReadOnlyList<string> appArgs, IReadOnlyDictionary<string, string> environment)
    {
        var env = new JsonObject();
        foreach (var (key, value) in environment)
        {
            env[key] = value;
        }

        // The watcher appends ApplicationArguments directly to `dotnet run`, so "--" keeps them from being parsed as
        // SDK options (for example `--configuration`). An empty array means "no arguments".
        var argsArray = new JsonArray();
        foreach (var option in runOptions)
        {
            argsArray.Add((JsonNode?)JsonValue.Create(option));
        }

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
