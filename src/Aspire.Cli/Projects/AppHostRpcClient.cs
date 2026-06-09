// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Telemetry;
using Aspire.TypeSystem;
using StreamJsonRpc;

namespace Aspire.Cli.Projects;

/// <summary>
/// Implementation of <see cref="IAppHostRpcClient"/> using JSON-RPC over sockets/pipes.
/// </summary>
internal sealed class AppHostRpcClient : IAppHostRpcClient
{
    // Logical connection name attached to JSON-RPC profiling spans created via this client.
    // The backchannel listener registers handlers without a connection name, so this value
    // is purely for grouping client-side spans/metrics in the trace.
    private const string ConnectionName = "remotehost";

    private readonly Stream _stream;
    private readonly JsonRpc _jsonRpc;
    private readonly ProfilingTelemetry? _profilingTelemetry;

    private AppHostRpcClient(Stream stream, JsonRpc jsonRpc, ProfilingTelemetry? profilingTelemetry)
    {
        _stream = stream;
        _jsonRpc = jsonRpc;
        _profilingTelemetry = profilingTelemetry;
    }

    /// <summary>
    /// Creates and connects an RPC client to the specified socket path and authenticates the session.
    /// </summary>
    public static async Task<AppHostRpcClient> ConnectAsync(
        string socketPath,
        string authenticationToken,
        ProfilingTelemetry? profilingTelemetry,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticationToken);

        var stream = await ConnectToServerAsync(socketPath, cancellationToken);
        JsonRpc? jsonRpc = null;

        try
        {
            var formatter = BackchannelJsonSerializerContext.CreateRpcMessageFormatter();
            var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
            jsonRpc = new JsonRpc(handler)
            {
                ActivityTracingStrategy = new ActivityTracingStrategy()
            };
            jsonRpc.StartListening();

            var authenticated = await jsonRpc.InvokeWithProfilingAsync<bool>(
                profilingTelemetry,
                ConnectionName,
                "authenticate",
                [authenticationToken],
                cancellationToken).ConfigureAwait(false);
            if (!authenticated)
            {
                throw new InvalidOperationException("Failed to authenticate to the AppHost server.");
            }

            return new AppHostRpcClient(stream, jsonRpc, profilingTelemetry);
        }
        catch
        {
            jsonRpc?.Dispose();
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<RuntimeSpec> GetRuntimeSpecAsync(string languageId, CancellationToken cancellationToken)
        => InvokeAsync<RuntimeSpec>("getRuntimeSpec", [languageId], cancellationToken);

    /// <inheritdoc />
    public Task<Dictionary<string, string>> ScaffoldAppHostAsync(
        string languageId, string targetPath, string? projectName, CancellationToken cancellationToken)
        => InvokeAsync<Dictionary<string, string>>(
            "scaffoldAppHost",
            [languageId, targetPath, projectName],
            cancellationToken);

    // The generateCode and getCapabilities RPC methods each have a single server-side handler
    // that accepts optional filtering parameters. The typed methods below provide distinct
    // C# signatures that call the same underlying RPC endpoint with different arguments.

    /// <inheritdoc />
    public Task<Dictionary<string, string>> GenerateCodeAsync(string languageId, CancellationToken cancellationToken)
        => InvokeCodeGenerationAsync<Dictionary<string, string>>("generateCode", [languageId, null], cancellationToken);

    /// <inheritdoc />
    public Task<Dictionary<string, string>> GenerateCodeForAssemblyAsync(string languageId, string assemblyName, CancellationToken cancellationToken)
        => InvokeCodeGenerationAsync<Dictionary<string, string>>("generateCode", [languageId, assemblyName], cancellationToken);

    /// <inheritdoc />
    public Task<Commands.Sdk.CapabilitiesInfo> GetCapabilitiesAsync(CancellationToken cancellationToken)
        => InvokeCodeGenerationAsync<Commands.Sdk.CapabilitiesInfo>("getCapabilities", [null], cancellationToken);

    /// <inheritdoc />
    public Task<Commands.Sdk.CapabilitiesInfo> GetCapabilitiesForAssembliesAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken)
        => InvokeCodeGenerationAsync<Commands.Sdk.CapabilitiesInfo>("getCapabilities", [assemblyNames], cancellationToken);

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(string methodName, object?[] parameters, CancellationToken cancellationToken)
        => _jsonRpc.InvokeWithProfilingAsync<T>(_profilingTelemetry, ConnectionName, methodName, parameters, cancellationToken);

    /// <inheritdoc />
    public Task InvokeAsync(string methodName, object?[] parameters, CancellationToken cancellationToken)
        => _jsonRpc.InvokeWithProfilingAsync(_profilingTelemetry, ConnectionName, methodName, parameters, cancellationToken);

    /// <summary>
    /// Invokes a code-generation RPC method and rethrows structured load/type failures as
    /// <see cref="AppHostCodeGenerationException"/> so the CLI can render an actionable
    /// diagnostic instead of an empty or .NET-specific error message.
    /// </summary>
    private async Task<T> InvokeCodeGenerationAsync<T>(string methodName, object?[] parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await _jsonRpc.InvokeWithProfilingAsync<T>(_profilingTelemetry, ConnectionName, methodName, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteInvocationException ex) when (ex.ErrorCode == AppHostCodeGenerationErrorCodes.IncompatibleAspireSdk)
        {
            var diagnostic = TryReadDiagnostic(ex);
            if (diagnostic is null)
            {
                throw;
            }

            throw new AppHostCodeGenerationException(ex.Message, diagnostic, ex);
        }
    }

    /// <summary>
    /// Extracts a <see cref="AppHostCodeGenerationDiagnostic"/> from a <see cref="RemoteInvocationException"/>'s
    /// structured error data, returning <see langword="null"/> if the payload is missing or
    /// can't be deserialized.
    /// </summary>
    private static AppHostCodeGenerationDiagnostic? TryReadDiagnostic(RemoteInvocationException exception)
    {
        if (exception.DeserializedErrorData is AppHostCodeGenerationDiagnostic typed)
        {
            return typed;
        }

        var payload = exception.DeserializedErrorData ?? exception.ErrorData;
        if (payload is JsonElement element)
        {
            try
            {
                return element.Deserialize(BackchannelJsonSerializerContext.Default.AppHostCodeGenerationDiagnostic);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _jsonRpc.Dispose();
        await _stream.DisposeAsync();
    }

    /// <summary>
    /// Connects to the RPC server using platform-appropriate transport.
    /// </summary>
    private static async Task<Stream> ConnectToServerAsync(string socketPath, CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        const int ConnectionTimeoutSeconds = 30;

        if (OperatingSystem.IsWindows())
        {
            var pipeClient = new NamedPipeClientStream(".", socketPath, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                while ((DateTimeOffset.UtcNow - startTime) < TimeSpan.FromSeconds(ConnectionTimeoutSeconds))
                {
                    try
                    {
                        await pipeClient.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        return pipeClient;
                    }
                    catch (TimeoutException)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }

                throw new InvalidOperationException($"Failed to connect to RPC server at {socketPath}");
            }
            catch
            {
                pipeClient.Dispose();
                throw;
            }
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                var endpoint = new UnixDomainSocketEndPoint(socketPath);

                while ((DateTimeOffset.UtcNow - startTime) < TimeSpan.FromSeconds(ConnectionTimeoutSeconds))
                {
                    try
                    {
                        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }

                throw new InvalidOperationException($"Failed to connect to RPC server at {socketPath}");
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}

/// <summary>
/// Factory for creating <see cref="IAppHostRpcClient"/> instances.
/// </summary>
internal sealed class AppHostRpcClientFactory : IAppHostRpcClientFactory
{
    /// <inheritdoc />
    public async Task<IAppHostRpcClient> ConnectAsync(string socketPath, string authenticationToken, CancellationToken cancellationToken)
    {
        return await AppHostRpcClient.ConnectAsync(socketPath, authenticationToken, profilingTelemetry: null, cancellationToken).ConfigureAwait(false);
    }
}
