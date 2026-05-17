// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.TypeSystem;
using StreamJsonRpc;

namespace Aspire.Cli.Projects;

/// <summary>
/// Implementation of <see cref="IAppHostRpcClient"/> using JSON-RPC over sockets/pipes.
/// </summary>
internal sealed class AppHostRpcClient : IAppHostRpcClient
{
    private readonly Stream _stream;
    private readonly JsonRpc _jsonRpc;

    private AppHostRpcClient(Stream stream, JsonRpc jsonRpc)
    {
        _stream = stream;
        _jsonRpc = jsonRpc;
    }

    /// <summary>
    /// Creates and connects an RPC client to the specified socket path and authenticates the session.
    /// </summary>
    public static async Task<AppHostRpcClient> ConnectAsync(string socketPath, string authenticationToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(authenticationToken);

        var stream = await ConnectToServerAsync(socketPath, cancellationToken);
        JsonRpc? jsonRpc = null;

        try
        {
            var formatter = BackchannelJsonSerializerContext.CreateRpcMessageFormatter();
            var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
            jsonRpc = new JsonRpc(handler);
            jsonRpc.StartListening();

            var authenticated = await jsonRpc.InvokeWithCancellationAsync<bool>("authenticate", [authenticationToken], cancellationToken);
            if (!authenticated)
            {
                throw new InvalidOperationException("Failed to authenticate to the AppHost server.");
            }

            return new AppHostRpcClient(stream, jsonRpc);
        }
        catch
        {
            jsonRpc?.Dispose();
            await stream.DisposeAsync();
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // TYPED WRAPPERS
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<RuntimeSpec> GetRuntimeSpecAsync(string languageId, CancellationToken cancellationToken)
        => NormalizeRuntimeSpec(
            await _jsonRpc.InvokeWithCancellationAsync<RuntimeSpec?>("getRuntimeSpec", [languageId], cancellationToken).ConfigureAwait(false),
            "getRuntimeSpec");

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> ScaffoldAppHostAsync(
        string languageId, string targetPath, string? projectName, CancellationToken cancellationToken)
        => NormalizeGeneratedFiles(
            await _jsonRpc.InvokeWithCancellationAsync<Dictionary<string, string>?>(
                "scaffoldAppHost", [languageId, targetPath, projectName], cancellationToken).ConfigureAwait(false),
            "scaffoldAppHost");

    // The generateCode and getCapabilities RPC methods each have a single server-side handler
    // that accepts optional filtering parameters. The typed methods below provide distinct
    // C# signatures that call the same underlying RPC endpoint with different arguments.

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GenerateCodeAsync(string languageId, CancellationToken cancellationToken)
        => NormalizeGeneratedFiles(
            await _jsonRpc.InvokeWithCancellationAsync<Dictionary<string, string>?>(
                "generateCode", [languageId, null], cancellationToken).ConfigureAwait(false),
            "generateCode");

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GenerateCodeForAssemblyAsync(string languageId, string assemblyName, CancellationToken cancellationToken)
        => NormalizeGeneratedFiles(
            await _jsonRpc.InvokeWithCancellationAsync<Dictionary<string, string>?>(
                "generateCode", [languageId, assemblyName], cancellationToken).ConfigureAwait(false),
            "generateCode");

    /// <inheritdoc />
    public async Task<Commands.Sdk.CapabilitiesInfo> GetCapabilitiesAsync(CancellationToken cancellationToken)
        => await _jsonRpc.InvokeWithCancellationAsync<Commands.Sdk.CapabilitiesInfo?>(
            "getCapabilities", [null], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Malformed AppHost RPC payload: getCapabilities returned null.");

    /// <inheritdoc />
    public async Task<Commands.Sdk.CapabilitiesInfo> GetCapabilitiesForAssembliesAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken)
        => await _jsonRpc.InvokeWithCancellationAsync<Commands.Sdk.CapabilitiesInfo?>(
            "getCapabilities", [assemblyNames], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Malformed AppHost RPC payload: getCapabilities returned null.");

    internal static Dictionary<string, string> NormalizeGeneratedFiles(Dictionary<string, string>? files, string methodName)
    {
        if (files is null)
        {
            return [];
        }

        foreach (var (path, content) in files)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException($"Malformed AppHost RPC payload: {methodName} returned a generated file with no path.");
            }

            if (content is null)
            {
                throw new InvalidOperationException($"Malformed AppHost RPC payload: {methodName} returned null content for generated file '{path}'.");
            }
        }

        return files;
    }

    internal static RuntimeSpec NormalizeRuntimeSpec(RuntimeSpec? spec, string methodName)
    {
        if (spec is null)
        {
            throw CreateMalformedPayloadException($"{methodName} returned null.");
        }

        RequireNonEmpty(spec.Language, $"{methodName}.{nameof(RuntimeSpec.Language)}");
        RequireNonEmpty(spec.DisplayName, $"{methodName}.{nameof(RuntimeSpec.DisplayName)}");
        RequireNonEmpty(spec.CodeGenLanguage, $"{methodName}.{nameof(RuntimeSpec.CodeGenLanguage)}");

        if (spec.DetectionPatterns is null)
        {
            throw CreateMalformedRequiredMemberException($"{methodName}.{nameof(RuntimeSpec.DetectionPatterns)}");
        }

        foreach (var detectionPattern in spec.DetectionPatterns)
        {
            RequireNonEmpty(detectionPattern, $"{methodName}.{nameof(RuntimeSpec.DetectionPatterns)}[]");
        }

        RequireCommandSpec(spec.Execute, $"{methodName}.{nameof(RuntimeSpec.Execute)}");
        RequireCommandSpecs(spec.Initialize, $"{methodName}.{nameof(RuntimeSpec.Initialize)}");
        RequireCommandSpec(spec.InstallDependencies, $"{methodName}.{nameof(RuntimeSpec.InstallDependencies)}", required: false);
        RequireCommandSpecs(spec.PreExecute, $"{methodName}.{nameof(RuntimeSpec.PreExecute)}");
        RequireCommandSpec(spec.WatchExecute, $"{methodName}.{nameof(RuntimeSpec.WatchExecute)}", required: false);
        RequireCommandSpec(spec.PublishExecute, $"{methodName}.{nameof(RuntimeSpec.PublishExecute)}", required: false);
        RequireStringDictionary(spec.MigrationFiles, $"{methodName}.{nameof(RuntimeSpec.MigrationFiles)}");

        return spec;
    }

    private static void RequireCommandSpecs(CommandSpec[]? commands, string memberName)
    {
        if (commands is null)
        {
            return;
        }

        for (var i = 0; i < commands.Length; i++)
        {
            RequireCommandSpec(commands[i], $"{memberName}[{i}]");
        }
    }

    private static void RequireCommandSpec(CommandSpec? command, string memberName, bool required = true)
    {
        if (command is null)
        {
            if (required)
            {
                throw CreateMalformedRequiredMemberException(memberName);
            }

            return;
        }

        RequireNonEmpty(command.Command, $"{memberName}.{nameof(CommandSpec.Command)}");

        if (command.Args is null)
        {
            throw CreateMalformedRequiredMemberException($"{memberName}.{nameof(CommandSpec.Args)}");
        }

        foreach (var arg in command.Args)
        {
            if (arg is null)
            {
                throw CreateMalformedRequiredMemberException($"{memberName}.{nameof(CommandSpec.Args)}[]");
            }
        }

        RequireStringDictionary(command.EnvironmentVariables, $"{memberName}.{nameof(CommandSpec.EnvironmentVariables)}");
    }

    private static void RequireStringDictionary(Dictionary<string, string>? values, string memberName)
    {
        if (values is null)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            RequireNonEmpty(key, $"{memberName}.key");
            if (value is null)
            {
                throw CreateMalformedRequiredMemberException($"{memberName}['{key}']");
            }
        }
    }

    private static void RequireNonEmpty(string? value, string memberName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw CreateMalformedRequiredMemberException(memberName);
        }
    }

    private static InvalidOperationException CreateMalformedRequiredMemberException(string memberName)
    {
        return CreateMalformedPayloadException($"required member '{memberName}' was null or missing.");
    }

    private static InvalidOperationException CreateMalformedPayloadException(string message)
    {
        return new InvalidOperationException($"Malformed AppHost RPC payload: {message}");
    }

    // ═══════════════════════════════════════════════════════════════
    // GENERIC INVOKE
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(string methodName, object?[] parameters, CancellationToken cancellationToken)
        => _jsonRpc.InvokeWithCancellationAsync<T>(methodName, parameters, cancellationToken);

    /// <inheritdoc />
    public Task InvokeAsync(string methodName, object?[] parameters, CancellationToken cancellationToken)
        => _jsonRpc.InvokeWithCancellationAsync(methodName, parameters, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _jsonRpc.Dispose();
        await _stream.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    // CONNECTION LOGIC
    // ═══════════════════════════════════════════════════════════════

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
        return await AppHostRpcClient.ConnectAsync(socketPath, authenticationToken, cancellationToken);
    }
}
